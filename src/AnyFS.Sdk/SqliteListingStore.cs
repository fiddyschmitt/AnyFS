using System.Diagnostics;
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
/// never held up by the writer, and each commit is on the disk before it returns. The folders below one are a range of the key.
/// What it does is counted (<see cref="Statistics"/>).
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
    private readonly Meter _meter = new();
    private SqliteConnection _db = null!;
    private SqliteCommand _get = null!;
    private SqliteCommand _insert = null!;
    private SqliteCommand _update = null!;
    private SqliteCommand _delete = null!;
    private SqliteCommand _range = null!;
    private SqliteCommand _rangeFrom = null!;
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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Open(key);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                // damaged (SQLITE_CORRUPT), or not a database at all (SQLITE_NOTADB): put aside (listings.db.damaged, for
                // whoever wants to look), a new one made, and the listings walked again; nothing else is ever put aside
                _logger.LogWarning("{Name}: the listings database {Path} could not be read ({Error}); it is put aside and the listings are walked again", _name, path, ex.Message);
                PutAside();
                try
                {
                    Open(key);
                    return;
                }
                catch (SqliteException again)
                {
                    _db?.Dispose();
                    throw new IOException($"the listings database {path} could not be opened: {again.Message}", again);
                }
            }
            catch (SqliteException ex) when (attempt < 7 && ex.SqliteErrorCode is 5 or 6 or 10)
            {
                // busy, locked, or an I/O error: the run before, just ended, has not let go of it yet (its log and shared
                // memory beside it are freed a moment after it dies); a moment, and again, two seconds in all
                _db?.Dispose();
                _db = null!;
                Thread.Sleep(100 * attempt);
            }
            catch (SqliteException ex)
            {
                // the folder refused, the disk full, or still held: the caller keeps the listings in memory for this run
                _db?.Dispose();
                throw new IOException($"the listings database {path} could not be opened: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Opens the database, makes its tables where they are not there, and checks it is this source's; the connection is left open on failure for the caller to dispose.</summary>
    private void Open(string key)
    {
        _db?.Dispose();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());

        // how long a statement waits for a lock another connection holds (the one writer is this connection; a run just
        // ended may hold the file a moment): short, the open trying again a few times
        _db.DefaultTimeout = 2;
        _db.Open();
        // every commit reaches the disk before it returns (as the file's appends do): commits are a few a minute, so it costs nothing
        Run("PRAGMA journal_mode=WAL");
        Run("PRAGMA synchronous=FULL");
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

        FullWalkAt = null;
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

    /// <summary>Moves a database that could not be opened to <c>.damaged</c> beside it (the one before it overwritten), its log and shared memory deleted.</summary>
    private void PutAside()
    {
        _db?.Dispose();
        _db = null!;
        try
        {
            File.Move(_path, _path + ".damaged", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(_path);
        }

        foreach (var beside in new[] { _path + "-wal", _path + "-shm" })
        {
            File.Delete(beside);
        }
    }

    /// <summary>When the last walk that listed every folder was, as the last run left it.</summary>
    public DateTimeOffset? FullWalkAt { get; private set; }

    public int Count => Volatile.Read(ref _count);

    /// <summary>What the store has done since it was opened, as of now; readable after it is disposed too.</summary>
    public ListingStoreStatistics Statistics() => _meter.Snapshot();

    public StoredListing? Get(string path)
    {
        var asked = Stopwatch.GetTimestamp();
        using (_gate.EnterScope())
        {
            var started = _meter.Entered(asked);
            try
            {
                _get.Parameters[0].Value = path;
                using var reader = _get.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                _meter.Found();
                return Read(reader, 0);
            }
            catch (SqliteException ex)
            {
                Failed("read", ex);
                return null;
            }
            finally
            {
                _meter.Note(Meter.Read, started);
            }
        }
    }

    public bool Set(string path, StoredListing listing)
    {
        var asked = Stopwatch.GetTimestamp();
        using (_gate.EnterScope())
        {
            var started = _meter.Entered(asked);
            var op = Meter.Insert;
            try
            {
                Begin();
                var packed = listing.Packed ?? ListingCodec.Pack(listing.List!, out _);
                _meter.Bytes(packed.Length);
                Fill(_insert, path, listing, packed);
                if (_insert.ExecuteNonQuery() > 0)
                {
                    Interlocked.Increment(ref _count);
                    _meter.Note(op, started);
                    started = 0;
                    Wrote();
                    return true;
                }

                op = Meter.Update;
                Fill(_update, path, listing, packed);
                _update.ExecuteNonQuery();
                _meter.Note(op, started);
                started = 0;
                Wrote();
                return false;
            }
            catch (SqliteException ex)
            {
                Failed("write", ex);
                return false;
            }
            finally
            {
                if (started != 0)
                {
                    _meter.Note(op, started);
                }
            }
        }
    }

    public bool Remove(string path)
    {
        var asked = Stopwatch.GetTimestamp();
        using (_gate.EnterScope())
        {
            var started = _meter.Entered(asked);
            try
            {
                Begin();
                _delete.Parameters[0].Value = path;
                if (_delete.ExecuteNonQuery() == 0)
                {
                    return false;
                }

                Interlocked.Decrement(ref _count);
                _meter.Note(Meter.Delete, started);
                started = 0;
                Wrote();
                return true;
            }
            catch (SqliteException ex)
            {
                Failed("write", ex);
                return false;
            }
            finally
            {
                if (started != 0)
                {
                    _meter.Note(Meter.Delete, started);
                }
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
            var asked = Stopwatch.GetTimestamp();
            using (_gate.EnterScope())
            {
                var started = _meter.Entered(asked);
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

                    _meter.Rows(page.Count);
                }
                catch (SqliteException ex)
                {
                    Failed("read", ex);
                    yield break;
                }
                finally
                {
                    _meter.Note(Meter.Range, started);
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
            var started = Stopwatch.GetTimestamp();
            try
            {
                Run("COMMIT");
            }
            finally
            {
                _meter.Note(Meter.Commit, started);
            }
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

    /// <summary>Counts what the store does: each kind of operation, its time in all and the longest, and the waits for the connection.</summary>
    private sealed class Meter
    {
        public const int Read = 0;
        public const int Insert = 1;
        public const int Update = 2;
        public const int Delete = 3;
        public const int Range = 4;
        public const int Commit = 5;
        private const int Ops = 6;

        private readonly long[] _count = new long[Ops];
        private readonly long[] _ticks = new long[Ops];
        private readonly long[] _longest = new long[Ops];
        private long _found;
        private long _rows;
        private long _bytes;
        private long _waited;
        private long _longestWait;

        /// <summary>The connection is had, asked for at <paramref name="asked"/>: the wait counted, and now, for the operation's own time.</summary>
        public long Entered(long asked)
        {
            var now = Stopwatch.GetTimestamp();
            var waited = now - asked;
            Interlocked.Add(ref _waited, waited);
            Longest(ref _longestWait, waited);
            return now;
        }

        public void Note(int op, long started)
        {
            var took = Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref _count[op]);
            Interlocked.Add(ref _ticks[op], took);
            Longest(ref _longest[op], took);
        }

        public void Found() => Interlocked.Increment(ref _found);

        public void Rows(int rows) => Interlocked.Add(ref _rows, rows);

        public void Bytes(int bytes) => Interlocked.Add(ref _bytes, bytes);

        public ListingStoreStatistics Snapshot() => new()
        {
            Reads = Of(Read),
            ReadsFound = Volatile.Read(ref _found),
            Inserts = Of(Insert),
            Updates = Of(Update),
            Deletes = Of(Delete),
            Ranges = Of(Range),
            RangeRows = Volatile.Read(ref _rows),
            Commits = Of(Commit),
            BytesWritten = Volatile.Read(ref _bytes),
            Waited = Stopwatch.GetElapsedTime(0, Volatile.Read(ref _waited)),
            LongestWait = Stopwatch.GetElapsedTime(0, Volatile.Read(ref _longestWait)),
        };

        private ListingStoreOperation Of(int op)
            => new(Volatile.Read(ref _count[op]), Stopwatch.GetElapsedTime(0, Volatile.Read(ref _ticks[op])), Stopwatch.GetElapsedTime(0, Volatile.Read(ref _longest[op])));

        private static void Longest(ref long longest, long value)
        {
            long seen;
            while (value > (seen = Volatile.Read(ref longest)) && Interlocked.CompareExchange(ref longest, value, seen) != seen)
            {
            }
        }
    }
}
