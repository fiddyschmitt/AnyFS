using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>
/// The listings on disk, in a SQLite database (<see cref="KeptListingsOptions.OnDisk"/>): a row a folder, its path the
/// key, its entries as the cache holds them (<see cref="ListingCodec"/>). For a source that keeps the listings of more
/// than fits in memory: what is held is the database, with the last few thousand folders asked for made in memory by the
/// cache, and the start reads nothing back. One connection, used by one caller at a time; writes go into a transaction
/// committed when the source saves (<see cref="Save"/>), after a walk and at the stop, and when it has grown big, so a
/// run that ends without stopping loses only what came after the last of those. Write-ahead logging, so a reader is
/// never held up by the writer and a commit does not wait for the disk. The folders below one are a range of the key.
/// </summary>
internal sealed class SqliteListingStore : IListingStore
{
    private const int FormatVersion = 1;

    // writes in one transaction at most: committed then, whatever the save interval, so the log never grows past a few hundred megabytes
    private const int CommitEvery = 50_000;

    // rows read at a time where a range is read (what is held below a folder), the connection free between them
    private const int PageRows = 1_000;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly Lock _gate = new();
    private readonly SqliteConnection _db;
    private readonly SqliteCommand _get;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _update;
    private readonly SqliteCommand _delete;
    private readonly SqliteCommand _range;
    private readonly SqliteCommand _rangeFrom;
    private int _count;
    private bool _inTransaction;
    private int _pending;
    private long _failures;

    public SqliteListingStore(string path, string key, ILogger logger, string name)
    {
        _path = path;
        _logger = logger;
        _name = name;
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Run("PRAGMA journal_mode=WAL");
            Run("PRAGMA synchronous=NORMAL");
            Run("PRAGMA cache_size=-65536");
            Run("PRAGMA temp_store=MEMORY");
            Run("PRAGMA mmap_size=67108864");
            Run("CREATE TABLE IF NOT EXISTS listing(path TEXT PRIMARY KEY NOT NULL, stamp TEXT, count INTEGER NOT NULL, folders INTEGER NOT NULL, entries BLOB NOT NULL) WITHOUT ROWID");
            Run("CREATE TABLE IF NOT EXISTS meta(name TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID");
            Run("CREATE TABLE IF NOT EXISTS leftout(folder TEXT PRIMARY KEY NOT NULL, names TEXT NOT NULL) WITHOUT ROWID");
            var ours = Meta("key") == key && Meta("format") == FormatVersion.ToString(CultureInfo.InvariantCulture);
            if (!ours)
            {
                // another's listings, of another format, or none yet: none of it is this run's
                if (Meta("key") is not null)
                {
                    _logger.LogInformation("{Name}: the listings saved by the last run are for something else; listing afresh", _name);
                }

                Run("DELETE FROM listing");
                Run("DELETE FROM leftout");
                Run("DELETE FROM meta");
                SetMeta("key", key);
                SetMeta("format", FormatVersion.ToString(CultureInfo.InvariantCulture));
            }

            using (var count = _db.CreateCommand())
            {
                count.CommandText = "SELECT count(*) FROM listing";
                _count = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (Meta("fullwalk") is { } at && at.Split('|') is [var ticks, var minutes] && long.TryParse(ticks, CultureInfo.InvariantCulture, out var t) && int.TryParse(minutes, CultureInfo.InvariantCulture, out var m))
            {
                FullWalkAt = new DateTimeOffset(t, TimeSpan.FromMinutes(m));
            }

            _get = Prepare("SELECT stamp, count, folders, entries FROM listing WHERE path = $path", "$path");
            _insert = Prepare("INSERT OR IGNORE INTO listing(path, stamp, count, folders, entries) VALUES($path, $stamp, $count, $folders, $entries)", "$path", "$stamp", "$count", "$folders", "$entries");
            _update = Prepare("UPDATE listing SET stamp = $stamp, count = $count, folders = $folders, entries = $entries WHERE path = $path", "$path", "$stamp", "$count", "$folders", "$entries");
            _delete = Prepare("DELETE FROM listing WHERE path = $path", "$path");
            _range = Prepare("SELECT path, stamp, count, folders, entries FROM listing WHERE path >= $from AND path < $to ORDER BY path LIMIT $limit", "$from", "$to", "$limit");
            _rangeFrom = Prepare("SELECT path, stamp, count, folders, entries FROM listing WHERE path > $from AND path < $to ORDER BY path LIMIT $limit", "$from", "$to", "$limit");
        }
        catch (SqliteException ex)
        {
            _db.Dispose();
            throw new IOException($"the listings database {path} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>When the last walk that listed every folder was, as the last run left it.</summary>
    public DateTimeOffset? FullWalkAt { get; private set; }

    public int Count => Volatile.Read(ref _count);

    public StoredListing? Get(string path)
    {
        lock (_gate)
        {
            try
            {
                _get.Parameters[0].Value = path;
                using var reader = _get.ExecuteReader();
                return reader.Read() ? Read(reader, 0) : null;
            }
            catch (SqliteException ex)
            {
                Failed("read", ex);
                return null;
            }
        }
    }

    public bool Set(string path, StoredListing listing)
    {
        lock (_gate)
        {
            try
            {
                Begin();
                var packed = listing.Packed ?? ListingCodec.Pack(listing.List!, out _);
                Fill(_insert, path, listing, packed);
                if (_insert.ExecuteNonQuery() > 0)
                {
                    Interlocked.Increment(ref _count);
                    Wrote();
                    return true;
                }

                Fill(_update, path, listing, packed);
                _update.ExecuteNonQuery();
                Wrote();
                return false;
            }
            catch (SqliteException ex)
            {
                Failed("write", ex);
                return false;
            }
        }
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            try
            {
                Begin();
                _delete.Parameters[0].Value = path;
                if (_delete.ExecuteNonQuery() == 0)
                {
                    return false;
                }

                Interlocked.Decrement(ref _count);
                Wrote();
                return true;
            }
            catch (SqliteException ex)
            {
                Failed("write", ex);
                return false;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                Begin();
                Run("DELETE FROM listing");
                Volatile.Write(ref _count, 0);
                Wrote();
            }
            catch (SqliteException ex)
            {
                Failed("write", ex);
            }
        }
    }

    public List<(string Path, StoredListing Listing)> Under(string folder)
    {
        var held = new List<(string, StoredListing)>();
        if (folder.Length == 0)
        {
            foreach (var row in Range("", "￿￿"))
            {
                held.Add(row);
            }

            return held;
        }

        if (Get(folder) is { } own)
        {
            held.Add((folder, own));
        }

        // in ordinal order every "folder/…" lies between "folder/" and "folder0", '0' being the character after '/'
        foreach (var row in Range(folder + "/", folder + "0"))
        {
            held.Add(row);
        }

        return held;
    }

    public List<string> Paths()
    {
        var paths = new List<string>(Count);
        foreach (var (path, _) in Range("", "￿￿"))
        {
            paths.Add(path);
        }

        return paths;
    }

    public IEnumerable<(string Path, StoredListing Listing)> All() => Range("", "￿￿");

    /// <summary>The rows from <paramref name="from"/> up to <paramref name="to"/> in path order, a page at a time, the connection free between pages.</summary>
    private IEnumerable<(string Path, StoredListing Listing)> Range(string from, string to)
    {
        var first = true;
        var last = from;
        while (true)
        {
            var page = new List<(string, StoredListing)>(PageRows);
            lock (_gate)
            {
                try
                {
                    var command = first ? _range : _rangeFrom;
                    command.Parameters[0].Value = last;
                    command.Parameters[1].Value = to;
                    command.Parameters[2].Value = PageRows;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        page.Add((reader.GetString(0), Read(reader, 1)));
                    }
                }
                catch (SqliteException ex)
                {
                    Failed("read", ex);
                    yield break;
                }
            }

            foreach (var row in page)
            {
                yield return row;
            }

            if (page.Count < PageRows)
            {
                yield break;
            }

            first = false;
            last = page[^1].Item1;
        }
    }

    /// <summary>What the file held before the listings were kept on disk, moved in whole.</summary>
    public void Import(SavedState saved)
    {
        lock (_gate)
        {
            try
            {
                Begin();
                foreach (var (path, listing) in saved.Listings)
                {
                    Fill(_insert, path, listing, listing.Packed!);
                    if (_insert.ExecuteNonQuery() > 0)
                    {
                        _count++;
                    }
                }

                foreach (var (folder, names) in saved.LeftOut)
                {
                    SetLeftOut(folder, names);
                }

                if (saved.FullWalkAt is { } at)
                {
                    SetFullWalkAt(at);
                }

                Commit();
            }
            catch (SqliteException ex)
            {
                throw new IOException($"the listings could not be moved into the database: {ex.Message}", ex);
            }
        }
    }

    /// <summary>What walks leave out, as the last run left it.</summary>
    public Dictionary<string, IReadOnlySet<string>> LeftOut()
    {
        var left = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        lock (_gate)
        {
            try
            {
                using var command = _db.CreateCommand();
                command.CommandText = "SELECT folder, names FROM leftout";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    left[reader.GetString(0)] = reader.GetString(1).Split('\n').ToHashSet(StringComparer.Ordinal);
                }
            }
            catch (SqliteException ex)
            {
                Failed("read", ex);
            }
        }

        return left;
    }

    /// <summary>Writes down what changed beside the listings, and commits everything stored since the last time.</summary>
    public void Save(DateTimeOffset? fullWalkAt, bool fullWalkChanged, IReadOnlyList<(string Folder, IReadOnlySet<string>? Names)> leftOut)
    {
        lock (_gate)
        {
            try
            {
                if (leftOut.Count > 0 || fullWalkChanged)
                {
                    Begin();
                }

                foreach (var (folder, names) in leftOut)
                {
                    SetLeftOut(folder, names);
                }

                if (fullWalkChanged)
                {
                    if (fullWalkAt is { } at)
                    {
                        SetFullWalkAt(at);
                    }
                    else
                    {
                        Run("DELETE FROM meta WHERE name = 'fullwalk'");
                    }
                }

                Commit();
            }
            catch (SqliteException ex)
            {
                throw new IOException($"the listings database could not be written: {ex.Message}", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                Commit();
                Run("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (SqliteException ex)
            {
                _logger.LogInformation("{Name}: the listings database could not be closed cleanly ({Error})", _name, ex.Message);
            }

            foreach (var command in new[] { _get, _insert, _update, _delete, _range, _rangeFrom })
            {
                command.Dispose();
            }

            _db.Dispose();
        }
    }

    // --- under the gate --------------------------------------------------------------------------------------------

    private void Begin()
    {
        if (!_inTransaction)
        {
            Run("BEGIN");
            _inTransaction = true;
            _pending = 0;
        }
    }

    private void Wrote()
    {
        if (++_pending >= CommitEvery)
        {
            Commit();
        }
    }

    private void Commit()
    {
        if (_inTransaction)
        {
            _inTransaction = false;
            Run("COMMIT");
        }
    }

    private void SetLeftOut(string folder, IReadOnlySet<string>? names)
    {
        using var command = _db.CreateCommand();
        if (names is null or { Count: 0 })
        {
            command.CommandText = "DELETE FROM leftout WHERE folder = $folder";
            command.Parameters.AddWithValue("$folder", folder);
        }
        else
        {
            command.CommandText = "INSERT OR REPLACE INTO leftout(folder, names) VALUES($folder, $names)";
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$names", string.Join('\n', names));
        }

        command.ExecuteNonQuery();
    }

    private void SetFullWalkAt(DateTimeOffset at)
    {
        FullWalkAt = at;
        SetMeta("fullwalk", $"{at.Ticks.ToString(CultureInfo.InvariantCulture)}|{((int)at.Offset.TotalMinutes).ToString(CultureInfo.InvariantCulture)}");
    }

    private string? Meta(string name)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private void SetMeta(string name, string value)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta(name, value) VALUES($name, $value)";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Run(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var name in parameters)
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        return command;
    }

    private static void Fill(SqliteCommand command, string path, StoredListing listing, byte[] packed)
    {
        command.Parameters[0].Value = path;
        command.Parameters[1].Value = listing.Stamp is null ? DBNull.Value : listing.Stamp;
        command.Parameters[2].Value = listing.Count;
        command.Parameters[3].Value = listing.Folders;
        command.Parameters[4].Value = packed;
    }

    private static StoredListing Read(SqliteDataReader reader, int at)
    {
        var stamp = reader.IsDBNull(at) ? null : reader.GetString(at);
        var count = reader.GetInt32(at + 1);
        var folders = reader.GetInt32(at + 2);
        var entries = reader.GetFieldValue<byte[]>(at + 3);
        return new StoredListing(0, stamp, entries, count, folders);
    }

    private void Failed(string what, SqliteException ex)
    {
        // the first failure is told, then one in a thousand: a disk that is full says so at every write
        if (_failures++ % 1000 == 0)
        {
            _logger.LogWarning("{Name}: the listings database {Path} could not be {What} ({Error})", _name, _path, what == "read" ? "read" : "written", ex.Message);
        }
    }
}
