using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LibSmb.E2E;

/// <summary>A command's exit code and output (stdout, then stderr).</summary>
public readonly record struct SmbResult(int Code, string Output)
{
    public bool Ok => Code == 0;

    /// <summary>The result when the command succeeded; otherwise the condition stops here, saying what failed and why.</summary>
    public SmbResult Must(string what) => Ok ? this : throw new SmbStepException($"{what} failed (exit {Code}): {SmbOps.Tail(Output, 2000)}");
}

/// <summary>A step a condition cannot go on without failed; the condition stops there.</summary>
public sealed class SmbStepException(string message) : Exception(message);

/// <summary>
/// The file operations the SMB conditions run on a machine, in its own shell, as portable-smb-server's rig has them
/// (test/rig/ops.go): PowerShell on Windows (.NET calls where a cmdlet's behaviour varies between versions), bash on Linux,
/// and on macOS the BSD userland (shasum, not sha256sum; stat -f, not stat -c; perl for byte-exact work, since macOS has
/// neither truncate(1) nor a GNU dd). One definition each, so the conditions cannot drift apart; every path is quoted, so
/// names with quotes, spaces and non-ASCII are safe.
/// </summary>
public static class SmbOps
{
    private const int OpTimeout = 120;

    /// <summary>Quotes a path or text for the machine's shell.</summary>
    public static string Q(Machine h, string s) => h.IsWindows ? "'" + s.Replace("'", "''") + "'" : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A path inside <paramref name="root"/>, with the machine's separator.</summary>
    public static string Join(Machine h, string root, params string[] parts)
    {
        var rest = parts.Where(p => p.Length > 0).ToArray();
        if (rest.Length == 0)
        {
            return root;
        }

        return h.IsWindows
            ? root.TrimEnd('\\') + "\\" + string.Join('\\', rest.Select(p => p.Replace('/', '\\')))
            : root.TrimEnd('/') + "/" + string.Join('/', rest);
    }

    /// <summary>A path as the machine's SFTP server takes it: Win32-OpenSSH wants <c>/C:/...</c>.</summary>
    public static string SftpPath(Machine h, string path) => h.IsWindows ? "/" + path.Replace('\\', '/') : path;

    /// <summary>
    /// Runs a command. On Windows the exit code reflects a failing cmdlet as well as a native command's own code, and the
    /// output comes back as UTF-8 whatever the console's code page.
    /// </summary>
    public static SmbResult Run(Machine h, string command, int timeoutSeconds = OpTimeout)
    {
        if (h.IsWindows)
        {
            command = "$OutputEncoding = [Text.Encoding]::UTF8; $ErrorActionPreference = 'Stop'\n" + command + "\nexit $LASTEXITCODE";
        }

        var (code, output) = h.Run(command, timeoutSeconds);
        return new SmbResult(code, output);
    }

    /// <summary>Run for best-effort commands whose chatter on stderr (a net use /delete of nothing, say) must not count as failure.</summary>
    public static SmbResult RunLax(Machine h, string command, int timeoutSeconds = OpTimeout)
    {
        if (h.IsWindows)
        {
            command = "$ErrorActionPreference = 'Continue'\n" + command + "\nexit 0";
        }

        var (code, output) = h.Run(command, timeoutSeconds);
        return new SmbResult(code, output);
    }

    public static string Tail(string s, int max) => s.Length <= max ? s : "..." + s[^max..];

    /// <summary>Makes a directory and any missing parents.</summary>
    public static SmbResult Mkdir(Machine h, string dir)
        => Run(h, h.IsWindows ? $"New-Item -ItemType Directory -Force -Path {Q(h, dir)} | Out-Null" : $"mkdir -p {Q(h, dir)}");

    /// <summary>Makes one directory; fails when it exists or the share refuses.</summary>
    public static SmbResult MkdirStrict(Machine h, string dir)
        => Run(h, h.IsWindows
            ? $"[IO.Directory]::CreateDirectory({Q(h, dir)}) | Out-Null; if (-not (Test-Path -LiteralPath {Q(h, dir)} -PathType Container)) {{ throw 'not created' }}"
            : $"mkdir {Q(h, dir)}");

    /// <summary>Deletes a file or a directory tree. Best effort: it never fails.</summary>
    public static void Remove(Machine h, string path)
    {
        if (h.IsWindows)
        {
            RunLax(h, $"if (Test-Path -LiteralPath {Q(h, path)}) {{ Remove-Item -LiteralPath {Q(h, path)} -Recurse -Force -ErrorAction SilentlyContinue }}", 600);
        }
        else
        {
            RunLax(h, $"rm -rf {Q(h, path)}", 600);
        }
    }

    /// <summary>Deletes a file or a directory tree and fails when it can't.</summary>
    public static SmbResult RemoveStrict(Machine h, string path)
        => Run(h, h.IsWindows
            ? $"Remove-Item -LiteralPath {Q(h, path)} -Recurse -Force; if (Test-Path -LiteralPath {Q(h, path)}) {{ throw 'still there' }}"
            : $"rm -r {Q(h, path)} && ! [ -e {Q(h, path)} ]", 600);

    /// <summary>Deletes one file (not a tree); fails when it can't, including when it isn't there.</summary>
    public static SmbResult RemoveFile(Machine h, string path)
        // Remove-Item, not [IO.File]::Delete: .NET's is a silent no-op on a missing file
        => Run(h, h.IsWindows
            ? $"Remove-Item -LiteralPath {Q(h, path)} -Force; if (Test-Path -LiteralPath {Q(h, path)}) {{ throw 'still there' }}"
            : $"rm {Q(h, path)}");

    /// <summary>Removes one directory, which must be empty: the share's "directory not empty" is what the delete condition is after.</summary>
    public static SmbResult Rmdir(Machine h, string dir)
        => Run(h, h.IsWindows ? $"[IO.Directory]::Delete({Q(h, dir)})" : $"rmdir {Q(h, dir)}");

    public static bool Exists(Machine h, string path)
    {
        var r = Run(h, h.IsWindows
            ? $"if (Test-Path -LiteralPath {Q(h, path)}) {{ 'yes' }} else {{ 'no' }}"
            : $"[ -e {Q(h, path)} ] && echo yes || echo no").Must("exists");
        return r.Output.Trim() == "yes";
    }

    public static SmbResult Copy(Machine h, string from, string to)
        => Run(h, h.IsWindows ? $"Copy-Item -LiteralPath {Q(h, from)} -Destination {Q(h, to)} -Force" : $"cp {Q(h, from)} {Q(h, to)}", 1200);

    /// <summary>A file's SHA-256, lower-case hex, computed on the machine.</summary>
    public static string Hash(Machine h, string path) => TryHash(h, path).Must("hash " + path).Output.Trim().ToLowerInvariant();

    public static SmbResult TryHash(Machine h, string path)
        => Run(h, h.Info.Os switch
        {
            "windows" => $"(Get-FileHash -Algorithm SHA256 -LiteralPath {Q(h, path)}).Hash",
            "macos" => $"shasum -a 256 {Q(h, path)} | cut -c1-64",
            _ => $"sha256sum {Q(h, path)} | cut -c1-64",
        }, 1200);

    public static long Size(Machine h, string path)
        => long.Parse(Run(h, h.Info.Os switch
        {
            "windows" => $"(Get-Item -LiteralPath {Q(h, path)} -Force).Length",
            "macos" => $"stat -f %z {Q(h, path)}",
            _ => $"stat -c %s {Q(h, path)}",
        }).Must("size of " + path).Output.Trim(), CultureInfo.InvariantCulture);

    /// <summary>A directory's immediate children (without . and ..), as the machine lists them.</summary>
    public static IReadOnlyList<string> ListNames(Machine h, string dir)
        => Lines(Run(h, h.IsWindows ? $"Get-ChildItem -LiteralPath {Q(h, dir)} -Force -Name" : $"ls -A1 {Q(h, dir)}", 600).Must("list " + dir).Output);

    /// <summary>A directory's children counted the cheapest way the machine has (no sort, no attributes).</summary>
    public static int CountEntries(Machine h, string dir) => int.Parse(TryCountEntries(h, dir).Must("count " + dir).Output.Trim(), CultureInfo.InvariantCulture);

    public static SmbResult TryCountEntries(Machine h, string dir)
        => Run(h, h.IsWindows
            ? $"@([IO.Directory]::EnumerateFileSystemEntries({Q(h, dir)})).Count"
            : $"""ls -f {Q(h, dir)} | grep -cv '^\.\.\?$'""", 600);

    /// <summary>A directory's children counted the expensive way: a listing with every entry's attributes, as Finder and Explorer list.</summary>
    public static int CountWithAttributes(Machine h, string dir)
        => int.Parse(Run(h, h.IsWindows
            ? $"@(Get-ChildItem -LiteralPath {Q(h, dir)} -Force | ForEach-Object {{ $_.Length }}).Count"
            : $"ls -lA {Q(h, dir)} | grep -c '^[-dl]'", 600).Must("list -l " + dir).Output.Trim(), CultureInfo.InvariantCulture);

    /// <summary>Looks up each named entry of a directory one at a time in one shell loop, failing on the first that is missing.</summary>
    public static SmbResult LookupMany(Machine h, string dir, IEnumerable<string> names)
    {
        if (h.IsWindows)
        {
            return Run(h, $"foreach ($n in @({string.Join(',', names.Select(n => Q(h, n)))})) {{ $null = (Get-Item -LiteralPath (Join-Path {Q(h, dir)} $n) -Force).Length }}", 600);
        }

        var stat = h.IsMac ? "stat -f %z" : "stat -c %s";
        return Run(h, $"for n in {string.Join(' ', names.Select(n => Q(h, n)))}; do {stat} {Q(h, dir)}/\"$n\" >/dev/null || exit 1; done", 600);
    }

    /// <summary>A short file as text; when it can't be read, a note saying why (which no expected text equals).</summary>
    public static string ReadText(Machine h, string path)
    {
        var r = Run(h, h.IsWindows ? $"[IO.File]::ReadAllText({Q(h, path)}, [Text.Encoding]::UTF8)" : $"cat {Q(h, path)}");
        return r.Ok ? r.Output.Replace("\r\n", "\n").TrimEnd('\n') : $"[read failed (exit {r.Code}): {Tail(r.Output, 300)}]";
    }

    /// <summary>Writes a short file as UTF-8, creating or replacing it.</summary>
    public static SmbResult WriteText(Machine h, string path, string text)
        => Run(h, h.IsWindows
            ? $"[IO.File]::WriteAllText({Q(h, path)}, {Q(h, text)}, (New-Object Text.UTF8Encoding $false))"
            : $"printf '%s' {Q(h, text)} > {Q(h, path)}");

    /// <summary>Appends the bytes of <paramref name="source"/> to <paramref name="target"/> (a write at the end of the file).</summary>
    public static SmbResult Append(Machine h, string target, string source)
        => Run(h, h.IsWindows
            ? $"$b = [IO.File]::ReadAllBytes({Q(h, source)}); $f = [IO.File]::Open({Q(h, target)}, [IO.FileMode]::Append, [IO.FileAccess]::Write); try {{ $f.Write($b, 0, $b.Length) }} finally {{ $f.Close() }}"
            : $"cat {Q(h, source)} >> {Q(h, target)}", 600);

    /// <summary>
    /// Renames a file or directory with rename(2) / MoveFileEx semantics, replacing a file at the target. (Not mv: for a
    /// name that differs only in case, mv on a case-insensitive share says the two are the same file and refuses.)
    /// </summary>
    public static SmbResult Rename(Machine h, string from, string to)
        => Run(h, h.IsWindows
            ? $"Move-Item -LiteralPath {Q(h, from)} -Destination {Q(h, to)} -Force"
            : $"""perl -e 'rename($ARGV[0], $ARGV[1]) or die "rename: $!\n"' {Q(h, from)} {Q(h, to)}""");

    /// <summary>Sets a file's length, shrinking it or growing it with zeros. The file must exist.</summary>
    public static SmbResult Truncate(Machine h, string path, long size)
        => Run(h, h.IsWindows
            ? $"$f = [IO.File]::Open({Q(h, path)}, [IO.FileMode]::Open, [IO.FileAccess]::Write); try {{ $f.SetLength({size}) }} finally {{ $f.Close() }}"
            : $"""perl -e 'open(F, "+<", $ARGV[0]) or die "open: $!\n"; truncate(F, $ARGV[1]) or die "truncate: $!\n"; close F' {Q(h, path)} {size}""", 600);

    /// <summary>Overwrites the file at a MiB-aligned offset with the bytes of <paramref name="source"/>, without truncating it; past the end it grows.</summary>
    public static SmbResult WriteAtMiB(Machine h, string path, long offsetMiB, string source)
        => Run(h, h.IsWindows
            ? $"$b = [IO.File]::ReadAllBytes({Q(h, source)}); $f = [IO.File]::Open({Q(h, path)}, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write); try {{ $f.Seek({offsetMiB << 20}, 'Begin') | Out-Null; $f.Write($b, 0, $b.Length) }} finally {{ $f.Close() }}"
            : $"dd if={Q(h, source)} of={Q(h, path)} bs=1048576 seek={offsetMiB} conv=notrunc 2>/dev/null", 1200);

    /// <summary>The SHA-256 of a MiB-aligned range of a file (a random-access read); a note saying why when it can't be read.</summary>
    public static string HashRangeMiB(Machine h, string path, long offsetMiB, long lengthMiB)
    {
        var r = Run(h, h.Info.Os switch
        {
            "windows" => $"$f = [IO.File]::Open({Q(h, path)}, [IO.FileMode]::Open, [IO.FileAccess]::Read); try {{ $f.Seek({offsetMiB << 20}, 'Begin') | Out-Null; $b = New-Object byte[] {lengthMiB << 20}; $n = 0; while ($n -lt $b.Length) {{ $k = $f.Read($b, $n, $b.Length - $n); if ($k -le 0) {{ break }}; $n += $k }}; if ($n -ne $b.Length) {{ throw \"short read: $n\" }}; ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)) -replace '-', '').ToLower() }} finally {{ $f.Close() }}",
            "macos" => $"dd if={Q(h, path)} bs=1048576 skip={offsetMiB} count={lengthMiB} 2>/dev/null | shasum -a 256 | cut -c1-64",
            _ => $"dd if={Q(h, path)} bs=1048576 skip={offsetMiB} count={lengthMiB} 2>/dev/null | sha256sum | cut -c1-64",
        }, 600);
        return r.Ok ? r.Output.Trim().ToLowerInvariant() : $"[range read failed (exit {r.Code}): {Tail(r.Output, 300)}]";
    }

    /// <summary>The SHA-256 of <paramref name="mib"/> MiB of zeros: what a hole in a sparse file reads as.</summary>
    public static string ZeroHashMiB(int mib)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var zeros = new byte[1 << 20];
        for (var i = 0; i < mib; i++)
        {
            sha.AppendData(zeros);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Sets a file's modification time (seconds since the epoch, UTC).</summary>
    public static SmbResult SetMtime(Machine h, string path, long epoch)
        => Run(h, h.IsWindows
            ? $"(Get-Item -LiteralPath {Q(h, path)} -Force).LastWriteTimeUtc = [DateTimeOffset]::FromUnixTimeSeconds({epoch}).UtcDateTime"
            : $"""perl -e 'utime($ARGV[1], $ARGV[1], $ARGV[0]) or die "utime: $!\n"' {Q(h, path)} {epoch}""");

    /// <summary>A file's modification time (seconds since the epoch, UTC), or null when it can't be read.</summary>
    public static long? Mtime(Machine h, string path)
    {
        var r = Run(h, h.Info.Os switch
        {
            "windows" => $"[DateTimeOffset]::new((Get-Item -LiteralPath {Q(h, path)} -Force).LastWriteTimeUtc).ToUnixTimeSeconds()",
            "macos" => $"stat -f %m {Q(h, path)}",
            _ => $"stat -c %Y {Q(h, path)}",
        });
        return r.Ok && long.TryParse(r.Output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    /// <summary>The total and free bytes of the volume a path is on (what df and Explorer show for a mounted share).</summary>
    public static (long Total, long Free) DiskSpace(Machine h, string path)
    {
        var r = Run(h, h.Info.Os switch
        {
            "windows" => $"$d = New-Object IO.DriveInfo((Resolve-Path -LiteralPath {Q(h, path)}).Path.Substring(0, 3)); \"$($d.TotalSize) $($d.AvailableFreeSpace)\"",
            "macos" => $"df -k {Q(h, path)} | tail -1 | awk '{{print $2*1024, $4*1024}}'",
            _ => $"df -k --output=size,avail {Q(h, path)} | tail -1 | awk '{{print $1*1024, $2*1024}}'",
        }).Must("df " + path);
        // awk may print a large product in exponent form
        var parts = r.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new SmbStepException($"df output '{r.Output}'");
        }

        return ((long)double.Parse(parts[0], CultureInfo.InvariantCulture), (long)double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Makes a file of random bytes on the machine (so a 256 MiB payload needn't cross the LAN twice); its SHA-256.</summary>
    public static string RandomFile(Machine h, string path, long size)
    {
        var command = h.IsWindows
            ? $"$rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 1048576; $left = [int64]{size}; $f = [IO.File]::Create({Q(h, path)}); try {{ while ($left -gt 0) {{ $n = [int][math]::Min($left, $b.Length); $rng.GetBytes($b); $f.Write($b, 0, $n); $left -= $n }} }} finally {{ $f.Close() }}"
            : size == 0
                ? $": > {Q(h, path)}"    // BSD head refuses -c 0
                : $"head -c {size} /dev/urandom > {Q(h, path)}";
        Run(h, command, 1200).Must("random file " + path);
        return Hash(h, path);
    }

    /// <summary>f_1.dat to f_<paramref name="n"/>.dat in a directory, each holding "content-i", in one shell loop (n CREATE/WRITE/CLOSE sequences on the wire).</summary>
    public static SmbResult NumberedFiles(Machine h, string dir, int n)
        => Run(h, h.IsWindows
            ? $$"""$d = {{Q(h, dir)}}; 1..{{n}} | ForEach-Object { [IO.File]::WriteAllText("$d\f_$_.dat", "content-$_") }"""
            : $$"""d={{Q(h, dir)}}; for i in $(seq 1 {{n}}); do printf 'content-%s' "$i" > "$d/f_$i.dat" || exit 1; done""", 1800);

    /// <summary>
    /// Runs several scripts on the machine at once (bash on Unix, PowerShell on Windows; each a whole script that exits
    /// non-zero on failure) as separate processes in this connection's logon session, so a mapped drive is visible to
    /// them; each one's result, in order.
    /// </summary>
    public static IReadOnlyList<SmbResult> Scripts(Machine h, string workDir, IReadOnlyList<string> scripts, int timeoutSeconds)
    {
        Mkdir(h, workDir).Must("make " + workDir);
        var ext = h.IsWindows ? ".ps1" : ".sh";
        var runner = new StringBuilder(h.IsWindows ? "$procs = @(); " : "");
        for (var i = 0; i < scripts.Count; i++)
        {
            var script = Join(h, workDir, $"par_{i}{ext}");
            var body = h.IsWindows ? "$ErrorActionPreference = 'Stop'\n" + scripts[i] + "\nexit 0\n" : "set -e\n" + scripts[i] + "\n";
            h.UploadText(body, SftpPath(h, script));
            var output = Join(h, workDir, $"par_{i}.out");
            var code = Join(h, workDir, $"par_{i}.code");
            runner.Append(h.IsWindows
                ? $"$p = Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',{Q(h, script)} -RedirectStandardOutput {Q(h, output)} -RedirectStandardError {Q(h, output + ".err")} -PassThru -WindowStyle Hidden; $null = $p.Handle; $procs += $p; "
                : $"( bash {Q(h, script)} > {Q(h, output)} 2>&1; echo $? > {Q(h, code)} ) & ");
        }

        runner.Append(h.IsWindows
            ? $"$procs | Wait-Process; $i = 0; foreach ($p in $procs) {{ [IO.File]::WriteAllText((Join-Path {Q(h, workDir)} \"par_$i.code\"), \"$($p.ExitCode)\"); $i++ }}"
            : "wait");
        Run(h, runner.ToString(), timeoutSeconds).Must("the parallel scripts");

        var collect = new StringBuilder();
        for (var i = 0; i < scripts.Count; i++)
        {
            var output = Join(h, workDir, $"par_{i}.out");
            var code = Join(h, workDir, $"par_{i}.code");
            collect.Append(h.IsWindows
                ? $"\"==RESULT $([IO.File]::ReadAllText({Q(h, code)}).Trim())\"; Get-Content -LiteralPath {Q(h, output)} -Raw -ErrorAction SilentlyContinue; Get-Content -LiteralPath {Q(h, output + ".err")} -Raw -ErrorAction SilentlyContinue; "
                : $"echo \"==RESULT $(cat {Q(h, code)})\"; cat {Q(h, output)}; ");
        }

        var collected = Run(h, collect.ToString()).Must("collect the scripts' results").Output.Replace("\r\n", "\n");
        var results = new List<SmbResult>();
        foreach (var chunk in collected.Split("==RESULT ").Skip(1))
        {
            var nl = chunk.IndexOf('\n');
            var codeText = nl < 0 ? chunk : chunk[..nl];
            results.Add(new SmbResult(int.TryParse(codeText.Trim(), out var c) ? c : -1, nl < 0 ? "" : chunk[(nl + 1)..]));
        }

        if (results.Count != scripts.Count)
        {
            throw new SmbStepException($"the parallel scripts: {results.Count} results for {scripts.Count} scripts:\n{Tail(collected, 3000)}");
        }

        return results;
    }

    // ---- waits: a client may cache what it saw for a few seconds (Windows' directory and file-information caches are
    // 10 s), so whatever one side checks after the other acted is polled. Each says why it gave up, or null. ----

    public static string? WaitFor(int seconds, Func<(bool Done, string Why)> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            string why;
            try
            {
                var (done, reason) = condition();
                if (done)
                {
                    return null;
                }

                why = reason;
            }
            catch (SmbStepException ex)
            {
                why = ex.Message;
            }

            if (DateTime.UtcNow > deadline)
            {
                return $"gave up after {seconds} s: {why}";
            }

            Thread.Sleep(500);
        }
    }

    public static string? WaitForHash(Machine h, string path, string want, int seconds)
        => WaitFor(seconds, () =>
        {
            if (!Exists(h, path))
            {
                return (false, $"{path} is not there");
            }

            var got = Hash(h, path);
            return (got == want, $"{path} has hash {got}, want {want}");
        });

    public static string? WaitForGone(Machine h, string path, int seconds) => WaitFor(seconds, () => (!Exists(h, path), $"{path} still exists"));

    public static string? WaitForCount(Machine h, string dir, int n, int seconds)
        => WaitFor(seconds, () =>
        {
            var got = CountEntries(h, dir);
            return (got == n, $"{dir} has {got} entries, want {n}");
        });

    public static IReadOnlyList<string> Lines(string output)
        => output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A share mounted on a client machine with the OS's own SMB client, as portable-smb-server's rig mounts them
/// (test/rig/client.go): Windows net use with a drive letter and /TCPPORT (Windows 11 24H2+), Linux mount -t cifs (the
/// kernel's client), macOS mount_smbfs. <see cref="Root"/> is the mount's local path once mounted.
/// </summary>
public sealed class SmbMount(Machine host, string serverIp, string share, int port)
{
    /// <summary>Where the Linux mounts go; nothing else of the machine's is ever unmounted.</summary>
    public const string LinuxPrefix = "/mnt/anyfs-e2e/c-";

    /// <summary>The drive letters the Windows mounts take (Z when a mount has no tag).</summary>
    public const string WindowsLetters = "ZYXWVUT";

    public Machine Host { get; } = host;

    public string Share { get; } = share;

    public bool Guest { get; init; }

    public string User { get; init; } = AnyFsHost.SmbUser;

    public string Password { get; init; } = AnyFsHost.SmbPassword;

    /// <summary>Linux only: the dialect mount.cifs asks for (vers=), null to let it negotiate.</summary>
    public string? Vers { get; init; }

    /// <summary>Linux only: further mount options (sign, seal).</summary>
    public string? Options { get; init; }

    /// <summary>Keeps mounts on one machine apart: the Unix mount point's suffix, and on Windows the drive letter (its first letter; Z without one).</summary>
    public string Tag { get; init; } = "";

    /// <summary>The mount's local path once mounted.</summary>
    public string? Root { get; private set; }

    public static string MacPrefix(Machine h) => $"/Users/{h.Info.User}/anyfs-lab/mnt/c-";

    private string Suffix => Tag.Length == 0 ? "" : "-" + Tag;

    private string MountPoint => Host.Info.Os switch
    {
        "windows" => (Tag.Length == 0 ? "Z" : Tag[..1].ToUpperInvariant()) + ":",
        "linux" => LinuxPrefix + Share + Suffix,
        _ => MacPrefix(Host) + Share + Suffix,
    };

    public string MountCommand()
    {
        var mp = MountPoint;
        switch (Host.Info.Os)
        {
            case "linux":
            {
                var credentials = Guest ? "guest" : $"username={User},password={Password}";
                var options = $"port={port},{credentials},uid=$(id -u),gid=$(id -g),file_mode=0777,dir_mode=0777,iocharset=utf8";
                if (Vers is not null)
                {
                    options += ",vers=" + Vers;
                }

                if (Options is not null)
                {
                    options += "," + Options;
                }

                return $"sudo mkdir -p {SmbOps.Q(Host, mp)} && sudo mount -t cifs //{serverIp}/{Share} {SmbOps.Q(Host, mp)} -o {options}";
            }

            case "macos":
            {
                // mount_smbfs takes the port in the URL; a guest is the "guest" account with an empty password, and -N keeps
                // it from asking for one. A user may mount on a folder of its own, so no sudo.
                var url = Guest ? $"//guest:@{serverIp}:{port}/{Share}" : $"//{User}:{Password}@{serverIp}:{port}/{Share}";
                return $"mkdir -p {SmbOps.Q(Host, mp)} && mount_smbfs -N {SmbOps.Q(Host, url)} {SmbOps.Q(Host, mp)}";
            }

            default:
                return $@"net use {mp} \\{serverIp}\{Share} /TCPPORT:{port} /PERSISTENT:NO" + (Guest ? "" : $" /USER:{User} {Password}");
        }
    }

    /// <summary>Mounts the share and hands back what happened, for the conditions where a refusal is expected.</summary>
    public SmbResult TryMount()
    {
        Unmount();
        var r = SmbOps.Run(Host, MountCommand(), 90);
        if (r.Ok)
        {
            Root = MountPoint + (Host.IsWindows ? "\\" : "");
        }

        return r;
    }

    public void Mount()
    {
        var r = TryMount();
        if (!r.Ok && Host.IsWindows && r.Output.Contains("System error 67", StringComparison.Ordinal) && serverIp != "127.0.0.1")
        {
            // net use never tried the port: WinFsp 2026's network provider, first in the provider order, makes Windows'
            // SMB client try 445 and QUIC for a server it has not reached yet, never the /TCPPORT one (found 23-Oct-2026;
            // the loopback is not affected). Nothing reached the server under test.
            Assert.Inconclusive($"net use \\\\{serverIp}\\{Share} /TCPPORT:{port} failed with error 67, before reaching the server: WinFsp's network provider (WinFsp.Np) comes first in this machine's provider order, and Windows' SMB client then ignores /TCPPORT for a server it has not connected to yet. Move WinFsp.Np after LanmanWorkstation (HKLM\\SYSTEM\\CurrentControlSet\\Control\\NetworkProvider\\Order) to run this row.");
        }

        r.Must("mount " + Describe());
    }

    public string Describe() => $"//{serverIp}:{port}/{Share} {(Guest ? "as guest" : "as " + User)} on {Host.Name}{(Vers is null && Options is null ? "" : $" ({Vers} {Options})".Replace("( ", "(").Replace(" )", ")"))}";

    /// <summary>Unmounts the share, forcibly if it has to. Best effort.</summary>
    public void Unmount()
    {
        var mp = MountPoint;
        var q = SmbOps.Q(Host, mp);
        switch (Host.Info.Os)
        {
            case "linux":
                SmbOps.RunLax(Host, $"if mountpoint -q {q}; then sudo umount {q} 2>/dev/null || sudo umount -f -l {q}; fi; true", 60);
                break;
            case "macos":
                SmbOps.RunLax(Host, $"if /sbin/mount | grep -q \" on {mp} \"; then umount {q} 2>/dev/null || diskutil unmount force {q} >/dev/null 2>&1; fi; true", 60);
                break;
            default:
                SmbOps.RunLax(Host, $"net use {mp} /delete /yes 2>$null | Out-Null", 60);
                break;
        }

        Root = null;
    }

    /// <summary>
    /// Gets rid of every mount the conditions left on a machine, even one whose server has gone, without waiting on it (a
    /// wedged macOS mount can take minutes to unmount), and only under the conditions' own mount points or letters: the
    /// machines' other mounts are none of their business.
    /// </summary>
    public static void ForceUnmountAll(Machine h)
    {
        switch (h.Info.Os)
        {
            case "linux":
                SmbOps.RunLax(h, $"for m in $(awk '$2 ~ \"^{LinuxPrefix}\" {{print $2}}' /proc/mounts); do sudo umount -f -l \"$m\" 2>/dev/null; done; true", 60);
                break;
            case "macos":
                SmbOps.RunLax(h, $"for m in $(/sbin/mount | awk '$3 ~ \"^(/private)?{MacPrefix(h)}\" {{print $3}}'); do (/sbin/umount -f \"$m\" >/dev/null 2>&1 &); done; true", 60);
                break;
            default:
                // a letter is let go only when it points at one of the conditions' shares
                SmbOps.RunLax(h, $$"""foreach ($l in @({{string.Join(',', WindowsLetters.Select(c => $"'{c}:'"))}})) { $o = net use $l 2>$null; if ($LASTEXITCODE -eq 0 -and (($o -join ' ') -match '\\(smbdata|smbsecond|smbro|smbguest|nosuchshare)\b')) { net use $l /delete /y 2>$null | Out-Null } }""", 60);
                break;
        }
    }
}

/// <summary>
/// One SMB condition's run, as portable-smb-server's rig has its pair (test/rig/matrix_test.go): the client machine, anyfs's
/// host (the server), the client's mount of smbdata (made the first time a condition asks for <see cref="Root"/>), the
/// share's folder as the client sees it and as the host has it, a scratch folder on the client's own disk, and what the
/// condition found. A failed <see cref="Check"/> is recorded and the condition goes on; a failed must stops it.
/// </summary>
public sealed class SmbPair
{
    private readonly List<Action> _cleanups = [];
    private readonly List<string> _errors = [];
    private readonly List<string> _notes = [];
    private SmbMount? _main;
    private string? _root;

    public SmbPair(AnyFsHost anyfs, Machine client, bool local)
    {
        AnyFs = anyfs;
        S = anyfs.Host;
        C = client;
        ServerIp = local ? "127.0.0.1" : anyfs.Host.Ip;
        Scratch = C.Info.Os switch
        {
            "windows" => @"C:\anyfs-lab\smb-scratch",
            "macos" => $"/Users/{C.Info.User}/anyfs-lab/smb-scratch",
            _ => $"/home/{C.Info.User}/anyfs-lab/smb-scratch",
        };
        SmbMount.ForceUnmountAll(C);
        SmbOps.Remove(C, Scratch);
        SmbOps.Mkdir(C, Scratch).Must("make the scratch folder " + Scratch);
    }

    public AnyFsHost AnyFs { get; }

    /// <summary>The client machine.</summary>
    public Machine C { get; }

    /// <summary>The server: anyfs's host.</summary>
    public Machine S { get; }

    /// <summary>The address the client reaches the server at: the loopback when it is the host itself.</summary>
    public string ServerIp { get; }

    public string Scratch { get; }

    public string ClientOs => C.Info.Os;

    public string ServerOs => S.Info.Os;

    /// <summary>The smbdata share's folder as the client sees it: the mount, then the Mirror's chain to the host's folder.</summary>
    public string Root
    {
        get
        {
            if (_root is null)
            {
                _main = NewMount("smbdata", "");
                _root = ShareFolder(_main, AnyFs.SmbDataDir);
            }

            return _root;
        }
    }

    /// <summary>A share's folder under a mount: a Mirror shows its path under its own chain (/home/user/x as home/user/x).</summary>
    public string ShareFolder(SmbMount mount, string hostDir) => SmbOps.Join(C, mount.Root!, AnyFsHost.TreePrefixOfLocalPath(hostDir).Split('/'));

    /// <summary>A path in the smbdata share, as the client sees it.</summary>
    public string Mnt(params string[] parts) => SmbOps.Join(C, Root, parts);

    /// <summary>A path in the smbdata share's folder on the host.</summary>
    public string Backing(params string[] parts) => SmbOps.Join(S, AnyFs.SmbDataDir, parts);

    /// <summary>A path in the client's scratch folder.</summary>
    public string ScratchPath(string name) => SmbOps.Join(C, Scratch, name);

    /// <summary>A name no earlier run used.</summary>
    public static string Unique(string prefix) => $"{prefix}-{Random.Shared.Next(1 << 24):x6}";

    /// <summary>Mounts another share from the client, unmounted when the condition ends.</summary>
    public SmbMount NewMount(string share, string tag, bool guest = false, int port = AnyFsHost.SmbPort)
    {
        var mount = new SmbMount(C, ServerIp, share, port) { Tag = tag, Guest = guest };
        _cleanups.Add(mount.Unmount);
        mount.Mount();
        return mount;
    }

    /// <summary>A file of random bytes in the client's scratch folder (removed when the condition ends): its path and hash.</summary>
    public (string Path, string Hash) RandomFile(string name, long size)
    {
        var path = ScratchPath(name);
        _cleanups.Add(() => SmbOps.Remove(C, path));
        return (path, SmbOps.RandomFile(C, path, size));
    }

    /// <summary>Something to undo when the condition ends, whatever happened: the last registered runs first.</summary>
    public void Cleanup(Action action) => _cleanups.Add(action);

    /// <summary>Records a failure unless <paramref name="ok"/>; the condition goes on.</summary>
    public void Check(bool ok, string message)
    {
        if (!ok)
        {
            _errors.Add(message);
        }
    }

    /// <summary>Records a failure when a wait gave up (<paramref name="error"/> is its reason); the condition goes on.</summary>
    public void Check(string? error, string what)
    {
        if (error is not null)
        {
            _errors.Add($"{what}: {error}");
        }
    }

    /// <summary>Stops the condition unless <paramref name="ok"/>.</summary>
    public void Must(bool ok, string message)
    {
        if (!ok)
        {
            throw new SmbStepException(message);
        }
    }

    /// <summary>Stops the condition when a wait gave up (<paramref name="error"/> is its reason).</summary>
    public void Must(string? error, string what)
    {
        if (error is not null)
        {
            throw new SmbStepException($"{what}: {error}");
        }
    }

    /// <summary>A measurement for the test's output.</summary>
    public void Note(string text)
    {
        _notes.Add(text);
        Console.WriteLine(text);
    }

    /// <summary>Runs the condition, then its cleanups; fails with everything it found.</summary>
    public void Run(Action<SmbPair> condition)
    {
        string? stopped = null;
        try
        {
            condition(this);
        }
        catch (SmbStepException ex)
        {
            stopped = ex.Message;
        }
        finally
        {
            for (var i = _cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    _cleanups[i]();
                }
                catch (Exception ex) when (ex is SmbStepException or InvalidOperationException or IOException)
                {
                    Console.WriteLine("cleanup: " + ex.Message);
                }
            }

            SmbOps.Remove(C, Scratch);
        }

        List<string> problems = stopped is null ? _errors : [.. _errors, "stopped: " + stopped];
        if (problems.Count > 0)
        {
            Assert.Fail($"{problems.Count} problem(s):\n  " + string.Join("\n  ", problems));
        }
    }
}
