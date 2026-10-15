using System.Globalization;
using System.Text.RegularExpressions;

namespace AnyFS.E2E;

/// <summary>How a client reached a collection: a path on the client (a mounted or native file system) or a URL prefix for curl.</summary>
public sealed class Access(string? path, string? url, Action? release) : IDisposable
{
    public string? Path { get; } = path;

    public string? Url { get; } = url;

    public bool IsUrl => Url is not null;

    public void Dispose()
    {
        try
        {
            release?.Invoke();
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// A machine acting as a client of anyfs's mounts (spec 16.2: the host itself, and the upstream as the remote
/// client). Each OS has the tools it ships with: Linux mounts cifs and davfs2 and uses curl, wget and sha256sum;
/// macOS mount_smbfs, mount_webdav, curl and shasum; Windows net use (SMB with /TCPPORT, WebDAV through the
/// WebClient service), WinFsp's own UNC path, curl.exe and Get-FileHash. Every check is a shell command whose
/// output the test compares with the fixture.
/// </summary>
public abstract partial class Client(Machine machine)
{
    public Machine M { get; } = machine;

    public static Client For(Machine machine) => machine.IsWindows ? new WindowsClient(machine) : machine.IsMac ? new MacClient(machine) : new LinuxClient(machine);

    /// <summary>Attaches to a mount of <paramref name="collection"/> on <paramref name="host"/>; <paramref name="local"/> when this client is the host machine.</summary>
    public abstract Access Attach(MountKind kind, AnyFsHost host, string collection, bool local);

    public abstract IReadOnlyList<string> ListNames(Access access, string relDir);

    public abstract string Sha256(Access access, string relFile);

    public abstract string Sha256Range(Access access, string relFile, long offset, int length);

    /// <summary>Create a folder, write a file, rename it, read it back, delete both. True when all of it worked; false with the refusal when any step did not.</summary>
    public abstract bool TryWriteRoundTrip(Access access, string relDir, out string detail);

    public abstract IReadOnlyList<string> ConcurrentSha256(Access access, string relFile, int readers);

    public abstract (string Hash, double Seconds) TimedSha256(Access access, string relFile);

    /// <summary>HTTP only: the first half with a Range, then the rest with curl -C -; the hash of the result.</summary>
    public abstract string ResumedDownloadSha256(Access access, string relFile);

    /// <summary>Builds the host address a client should use: the loopback for the host itself, else its LAN address.</summary>
    protected static string HostAddress(AnyFsHost host, bool local) => local ? "127.0.0.1" : host.Host.Ip;

    protected static string UrlOf(Access access, string rel)
        => access.Url!.TrimEnd('/') + "/" + string.Join("/", rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>The server-side absolute path of a collection-relative one (what FTP quote commands take): /collection/rel.</summary>
    protected static string FtpAbsolutePath(Access access, string rel)
        => new Uri(access.Url!).AbsolutePath.TrimEnd('/') + (rel.Length == 0 ? "" : "/" + rel);

    protected static IReadOnlyList<string> JsonNames(string json) => JsonName().Matches(json).Select(m => Regex.Unescape(m.Groups[1].Value)).ToList();

    [GeneratedRegex("\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex JsonName();

    protected static double ParseSeconds(string text) => double.Parse(text.Trim(), CultureInfo.InvariantCulture);
}

/// <summary>Linux and macOS share the shell; the differences are the hash tool, how mounts are made and whether wget exists.</summary>
public abstract class PosixClient(Machine machine) : Client(machine)
{
    protected abstract string Sha256Command { get; }

    protected static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    protected string PathOf(Access access, string rel) => rel.Length == 0 ? access.Path! : access.Path!.TrimEnd('/') + "/" + rel;

    public override IReadOnlyList<string> ListNames(Access access, string relDir)
    {
        if (!access.IsUrl)
        {
            return Lines(M.Sh($"ls -A1 {Q(PathOf(access, relDir))}"));
        }

        var url = UrlOf(access, relDir) + "/";
        return url.StartsWith("ftp://", StringComparison.Ordinal)
            ? Lines(M.Sh($"curl -sf --list-only {Q(url)}"))
            : JsonNames(M.Sh($"curl -sf {Q(url + "?format=json")}"));
    }

    public override string Sha256(Access access, string relFile)
        => access.IsUrl
            ? M.Sh($"curl -sf {Q(UrlOf(access, relFile))} | {Sha256Command} | cut -d' ' -f1", 600).Trim()
            : M.Sh($"{Sha256Command} {Q(PathOf(access, relFile))} | cut -d' ' -f1", 600).Trim();

    public override string Sha256Range(Access access, string relFile, long offset, int length)
        => access.IsUrl
            ? M.Sh($"curl -sf -r {offset}-{offset + length - 1} {Q(UrlOf(access, relFile))} | {Sha256Command} | cut -d' ' -f1").Trim()
            : M.Sh($"tail -c +{offset + 1} {Q(PathOf(access, relFile))} | head -c {length} | {Sha256Command} | cut -d' ' -f1").Trim();

    public override bool TryWriteRoundTrip(Access access, string relDir, out string detail)
    {
        var name = "e2e-" + Guid.NewGuid().ToString("N")[..8];
        (int Code, string Output) result;
        string cleanup;
        if (!access.IsUrl)
        {
            var d = PathOf(access, relDir) + "/" + name;
            result = M.Run($"set -e; mkdir {Q(d)}; printf hello > {Q(d + "/a.txt")}; mv {Q(d + "/a.txt")} {Q(d + "/b.txt")}; [ \"$(cat {Q(d + "/b.txt")})\" = hello ]; rm {Q(d + "/b.txt")}; rmdir {Q(d)}; echo ok");
            cleanup = $"rm -rf {Q(d)}";
        }
        else if (access.Url!.StartsWith("ftp://", StringComparison.Ordinal))
        {
            // curl runs -Q commands at the login directory, so they take absolute paths; -T uploads to the URL's folder
            var dir = UrlOf(access, relDir).TrimEnd('/') + "/";
            var abs = FtpAbsolutePath(access, relDir) + "/" + name;
            result = M.Run($"printf hello > /tmp/{name} && curl -sfS -Q {Q("MKD " + abs)} {Q(dir)} >/dev/null && curl -sfS -T /tmp/{name} {Q(dir + name + "/a.txt")} && curl -sfS -Q {Q("RNFR " + abs + "/a.txt")} -Q {Q("RNTO " + abs + "/b.txt")} {Q(dir)} >/dev/null && [ \"$(curl -sfS {Q(dir + name + "/b.txt")})\" = hello ] && curl -sfS -Q {Q("DELE " + abs + "/b.txt")} {Q(dir)} >/dev/null && curl -sfS -Q {Q("RMD " + abs)} {Q(dir)} >/dev/null && rm -f /tmp/{name} && echo ok");
            // a * prefix makes curl carry on past a quote command that fails (the file may be a.txt, b.txt or neither)
            cleanup = $"curl -s -Q {Q("*DELE " + abs + "/a.txt")} -Q {Q("*DELE " + abs + "/b.txt")} -Q {Q("*RMD " + abs)} {Q(dir)} >/dev/null 2>&1; rm -f /tmp/{name}";
        }
        else
        {
            detail = "HTTP has no writes";
            return false;
        }

        detail = result.Output;
        var ok = result.Code == 0 && result.Output.Contains("ok");
        if (!ok)
        {
            // best effort, so a failed step leaves nothing behind for the rows after this one (the sources share trees)
            M.Run(cleanup);
        }

        return ok;
    }

    public override IReadOnlyList<string> ConcurrentSha256(Access access, string relFile, int readers)
    {
        var one = access.IsUrl ? $"curl -sf {Q(UrlOf(access, relFile))} | {Sha256Command} | cut -d' ' -f1" : $"{Sha256Command} {Q(PathOf(access, relFile))} | cut -d' ' -f1";
        return Lines(M.Sh($"for i in $(seq {readers}); do ({one}) & done; wait", 600));
    }

    public override (string Hash, double Seconds) TimedSha256(Access access, string relFile)
    {
        var one = access.IsUrl ? $"curl -sf {Q(UrlOf(access, relFile))} | {Sha256Command} | cut -d' ' -f1" : $"{Sha256Command} {Q(PathOf(access, relFile))} | cut -d' ' -f1";
        var lines = Lines(M.Sh($"s=$(perl -MTime::HiRes=time -e 'print time'); h=$({one}); e=$(perl -MTime::HiRes=time -e 'print time'); echo $h; perl -e \"print $e - $s\"", 600));
        return (lines[0], ParseSeconds(lines[1]));
    }

    public override string ResumedDownloadSha256(Access access, string relFile)
    {
        var url = Q(UrlOf(access, relFile));
        var part = "/tmp/e2e-part-" + Guid.NewGuid().ToString("N")[..8];
        return M.Sh($"set -e; curl -sf -r 0-{(Fixture.SizeOf("large.bin") / 2) - 1} -o {part} {url}; curl -sf -C - -o {part} {url}; {Sha256Command} {part} | cut -d' ' -f1; rm -f {part}", 600).Trim();
    }

    protected static IReadOnlyList<string> Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
}

public sealed class LinuxClient(Machine machine) : PosixClient(machine)
{
    protected override string Sha256Command => "sha256sum";

    public override Access Attach(MountKind kind, AnyFsHost host, string collection, bool local)
    {
        var address = HostAddress(host, local);
        switch (kind)
        {
            case MountKind.Fuse:
                if (!local)
                {
                    throw new InvalidOperationException("FUSE is local to the host");
                }

                return new Access(host.FuseMountPoint(collection), null, null);
            case MountKind.Smb:
            {
                var mp = $"/mnt/anyfs-e2e/smb-{collection}-{Guid.NewGuid().ToString("N")[..6]}";
                M.Sh($"sudo mkdir -p {Q(mp)} && sudo mount -t cifs {Q($"//{address}/{collection}")} {Q(mp)} -o port={AnyFsHost.SmbPort},vers=3.0,username={AnyFsHost.SmbUser},password={AnyFsHost.SmbPassword},uid=$(id -u),gid=$(id -g),iocharset=utf8");
                return new Access(mp, null, () => M.Run($"sudo umount {Q(mp)}; sudo rmdir {Q(mp)}"));
            }

            case MountKind.WebDav:
            {
                var mp = $"/mnt/anyfs-e2e/dav-{collection}-{Guid.NewGuid().ToString("N")[..6]}";
                // davfs2 asks for a login even when the server wants none: two empty answers
                M.Sh($"sudo mkdir -p {Q(mp)} && printf '\\n\\n' | sudo mount -t davfs {Q($"http://{address}:{AnyFsHost.WebDavPort}/{collection}/")} {Q(mp)} -o uid=$(id -u),gid=$(id -g)");
                return new Access(mp, null, () => M.Run($"sudo umount {Q(mp)}; sudo rmdir {Q(mp)}", 120));
            }

            case MountKind.Ftp:
                return new Access(null, $"ftp://{address}:{AnyFsHost.FtpPort}/{collection}", null);
            case MountKind.Http:
                return new Access(null, $"http://{address}:{AnyFsHost.HttpPort}/{collection}", null);
            default:
                throw new InvalidOperationException($"{kind} is not a Linux client's mount");
        }
    }

    /// <summary>wget -c on a partial file: the other resumable client of spec 8.6.</summary>
    public string WgetResumedSha256(Access access, string relFile)
    {
        var url = Q(UrlOf(access, relFile));
        var part = "/tmp/e2e-wget-" + Guid.NewGuid().ToString("N")[..8];
        return M.Sh($"set -e; curl -sf -r 0-{(Fixture.SizeOf("large.bin") / 2) - 1} -o {part} {url}; wget -q -c -O {part} {url}; sha256sum {part} | cut -d' ' -f1; rm -f {part}", 600).Trim();
    }
}

public sealed class MacClient(Machine machine) : PosixClient(machine)
{
    protected override string Sha256Command => "shasum -a 256";

    public override Access Attach(MountKind kind, AnyFsHost host, string collection, bool local)
    {
        var address = HostAddress(host, local);
        var mnt = $"/Users/{M.Info.User}/anyfs-lab/mnt";
        switch (kind)
        {
            case MountKind.Smb:
            {
                var mp = $"{mnt}/smb-{collection}-{Guid.NewGuid().ToString("N")[..6]}";
                M.Sh($"mkdir -p {Q(mp)} && mount -t smbfs {Q($"//{AnyFsHost.SmbUser}:{AnyFsHost.SmbPassword}@{address}:{AnyFsHost.SmbPort}/{collection}")} {Q(mp)}");
                return new Access(mp, null, () => M.Run($"umount {Q(mp)} || diskutil unmount force {Q(mp)}; rmdir {Q(mp)}"));
            }

            case MountKind.WebDav:
            {
                var mp = $"{mnt}/dav-{collection}-{Guid.NewGuid().ToString("N")[..6]}";
                M.Sh($"mkdir -p {Q(mp)} && mount_webdav -S {Q($"http://{address}:{AnyFsHost.WebDavPort}/{collection}/")} {Q(mp)}");
                return new Access(mp, null, () => M.Run($"umount {Q(mp)} || diskutil unmount force {Q(mp)}; rmdir {Q(mp)}", 120));
            }

            case MountKind.Ftp:
                return new Access(null, $"ftp://{address}:{AnyFsHost.FtpPort}/{collection}", null);
            case MountKind.Http:
                return new Access(null, $"http://{address}:{AnyFsHost.HttpPort}/{collection}", null);
            default:
                throw new InvalidOperationException($"{kind} is not a macOS client's mount");
        }
    }
}

/// <summary>PowerShell over SSH; file names and hashes come back one per line.</summary>
public sealed class WindowsClient(Machine machine) : Client(machine)
{
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private static string PathOf(Access access, string rel) => rel.Length == 0 ? access.Path! : access.Path!.TrimEnd('\\') + "\\" + rel.Replace('/', '\\');

    public override Access Attach(MountKind kind, AnyFsHost host, string collection, bool local)
    {
        var address = HostAddress(host, local);
        switch (kind)
        {
            case MountKind.WinFsp:
                if (!local)
                {
                    throw new InvalidOperationException("WinFsp is local to the host");
                }

                return new Access($@"\\anyfs\{collection}", null, null);
            case MountKind.Smb:
            {
                var unc = $@"\\{address}\{collection}";
                M.Sh($"net use {unc} /delete /y 2>$null | Out-Null; $o = net use {unc} /TCPPORT:{AnyFsHost.SmbPort} /USER:{AnyFsHost.SmbUser} {AnyFsHost.SmbPassword} 2>&1; if ($LASTEXITCODE -ne 0) {{ throw \"net use: $o\" }}; 'ok'");
                return new Access(unc, null, () => M.Run($"net use {unc} /delete /y 2>&1 | Out-Null"));
            }

            case MountKind.WebDav:
            {
                var unc = $@"\\{address}@{AnyFsHost.WebDavPort}\{collection}";
                M.Sh($"net use {unc} /delete /y 2>$null | Out-Null; $o = net use {unc} 2>&1; if ($LASTEXITCODE -ne 0) {{ throw \"net use: $o\" }}; 'ok'", 180);
                return new Access(unc, null, () => M.Run($"net use {unc} /delete /y 2>&1 | Out-Null"));
            }

            case MountKind.Ftp:
                return new Access(null, $"ftp://{address}:{AnyFsHost.FtpPort}/{collection}", null);
            case MountKind.Http:
                return new Access(null, $"http://{address}:{AnyFsHost.HttpPort}/{collection}", null);
            default:
                throw new InvalidOperationException($"{kind} is not a Windows client's mount");
        }
    }

    public override IReadOnlyList<string> ListNames(Access access, string relDir)
    {
        if (!access.IsUrl)
        {
            return Lines(M.Sh($"(Get-ChildItem -LiteralPath {Q(PathOf(access, relDir))} -Force).Name"));
        }

        var url = UrlOf(access, relDir) + "/";
        return url.StartsWith("ftp://", StringComparison.Ordinal)
            ? Lines(CurlWithOneRetry($"--list-only {Q(url)}"))
            : JsonNames(CurlWithOneRetry(Q(url + "?format=json")));
    }

    /// <summary>
    /// curl.exe with the given arguments, its output returned. A failure is thrown with curl's exit code and error line
    /// (PowerShell's own exit code says nothing more than "failed"); before that, the command gets one more try a second
    /// later, with the first failure written to the test output. The FTP rows run from the Windows machine itself have
    /// seen a transient failure about once in several hundred data connections; a repeat is recorded, not fatal.
    /// </summary>
    private string CurlWithOneRetry(string arguments, string before = "", string after = "", int timeoutSeconds = 120)
    {
        // stderr is kept apart from the output: with 2>&1 PowerShell would hand a stray warning line of curl's back as
        // part of a listing
        var command = $"{before}$e = @(); $o = @(curl.exe -sfS {arguments} 2>&1 | ForEach-Object {{ if ($_ -is [Management.Automation.ErrorRecord]) {{ $e += $_.ToString() }} else {{ $_ }} }}); if ($LASTEXITCODE -ne 0) {{ throw \"curl exit ${{LASTEXITCODE}}: $($e -join ' / ')\" }}; {(after.Length == 0 ? "$o" : after)}";
        var (code, output) = M.Run(command, timeoutSeconds);
        if (code == 0)
        {
            return output;
        }

        Console.WriteLine($"curl retry after: {output.Trim()}");
        Thread.Sleep(1000);
        return M.Sh(command, timeoutSeconds);
    }

    /// <summary>
    /// PowerShell that leaves the SHA-256 of the file at <paramref name="pathLiteral"/> (a quoted path or a variable) in
    /// <c>$h</c>, reading 64 KiB at a time like sha256sum and shasum do. Get-FileHash reads 4 KB at a time, which is a
    /// benchmark of the client's round trips rather than of the mount (and the WinFsp read-ahead exists for that case).
    /// </summary>
    private static string HashInto(string pathLiteral)
        => $"$fs = New-Object IO.FileStream({pathLiteral}, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite, 65536); try {{ $h = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fs)) -replace '-', '').ToLower() }} finally {{ $fs.Close() }}";

    private const string ToTempFile = "$t = [IO.Path]::GetTempFileName(); ";
    private const string HashTempFile = "(Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash.ToLower(); Remove-Item $t";

    public override string Sha256(Access access, string relFile)
        => access.IsUrl
            ? CurlWithOneRetry($"-o $t {Q(UrlOf(access, relFile))}", ToTempFile, HashTempFile, 600).Trim()
            : M.Sh($"{HashInto(Q(PathOf(access, relFile)))}; $h", 600).Trim();

    public override string Sha256Range(Access access, string relFile, long offset, int length)
    {
        if (access.IsUrl)
        {
            return CurlWithOneRetry($"-r {offset}-{offset + length - 1} -o $t {Q(UrlOf(access, relFile))}", ToTempFile, HashTempFile).Trim();
        }

        return M.Sh($"$fs = [IO.File]::OpenRead({Q(PathOf(access, relFile))}); $fs.Position = {offset}; $b = New-Object byte[] {length}; $n = 0; while ($n -lt {length}) {{ $r = $fs.Read($b, $n, {length} - $n); if ($r -le 0) {{ break }}; $n += $r }}; $fs.Close(); ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b, 0, $n)) -replace '-', '').ToLower()").Trim();
    }

    public override bool TryWriteRoundTrip(Access access, string relDir, out string detail)
    {
        var name = "e2e-" + Guid.NewGuid().ToString("N")[..8];
        (int Code, string Output) result;
        string cleanup;
        if (!access.IsUrl)
        {
            var d = PathOf(access, relDir) + "\\" + name;
            // (Windows PowerShell's New-Item has no -LiteralPath; the names here carry no wildcard characters)
            result = M.Run($"$ErrorActionPreference = 'Stop'; New-Item -ItemType Directory -Path {Q(d)} | Out-Null; [IO.File]::WriteAllText({Q(d + "\\a.txt")}, 'hello'); Rename-Item -LiteralPath {Q(d + "\\a.txt")} 'b.txt'; if ([IO.File]::ReadAllText({Q(d + "\\b.txt")}) -ne 'hello') {{ throw 'read back differs' }}; Remove-Item -LiteralPath {Q(d + "\\b.txt")}; Remove-Item -LiteralPath {Q(d)}; 'ok'");
            cleanup = $"Remove-Item -LiteralPath {Q(d)} -Recurse -Force -ErrorAction SilentlyContinue";
        }
        else if (access.Url!.StartsWith("ftp://", StringComparison.Ordinal))
        {
            // curl runs -Q commands at the login directory, so they take absolute paths; -T uploads to the URL's folder.
            // -S keeps curl's own error line, and the exit code says which step failed (curl's manual lists them).
            var dir = UrlOf(access, relDir).TrimEnd('/') + "/";
            var abs = FtpAbsolutePath(access, relDir) + "/" + name;
            result = M.Run($"$ErrorActionPreference = 'Stop'; $t = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($t, 'hello'); $o = curl.exe -sfS -Q {Q("MKD " + abs)} {Q(dir)} 2>&1; if ($LASTEXITCODE -ne 0) {{ throw \"MKD failed (curl exit $LASTEXITCODE): $o\" }}; $o = curl.exe -sfS -T $t {Q(dir + name + "/a.txt")} 2>&1; if ($LASTEXITCODE -ne 0) {{ throw \"STOR failed (curl exit $LASTEXITCODE): $o\" }}; $o = curl.exe -sfS -Q {Q("RNFR " + abs + "/a.txt")} -Q {Q("RNTO " + abs + "/b.txt")} {Q(dir)} 2>&1; if ($LASTEXITCODE -ne 0) {{ throw \"RNTO failed (curl exit $LASTEXITCODE): $o\" }}; $back = curl.exe -sf {Q(dir + name + "/b.txt")}; if ($back -ne 'hello') {{ throw \"read back: $back\" }}; curl.exe -sf -Q {Q("DELE " + abs + "/b.txt")} {Q(dir)} | Out-Null; curl.exe -sf -Q {Q("RMD " + abs)} {Q(dir)} | Out-Null; Remove-Item $t; 'ok'");
            // a * prefix makes curl carry on past a quote command that fails (the file may be a.txt, b.txt or neither)
            cleanup = $"curl.exe -s -Q {Q("*DELE " + abs + "/a.txt")} -Q {Q("*DELE " + abs + "/b.txt")} -Q {Q("*RMD " + abs)} {Q(dir)} 2>&1 | Out-Null";
        }
        else
        {
            detail = "HTTP has no writes";
            return false;
        }

        detail = result.Output;
        var ok = result.Code == 0 && result.Output.Contains("ok");
        if (!ok)
        {
            // best effort, so a failed step leaves nothing behind for the rows after this one (the sources share trees)
            M.Run(cleanup);
        }

        return ok;
    }

    public override IReadOnlyList<string> ConcurrentSha256(Access access, string relFile, int readers)
    {
        var block = access.IsUrl
            ? $"param($u) $t = [IO.Path]::GetTempFileName(); curl.exe -sf -o $t $u; $h = (Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash.ToLower(); Remove-Item $t; $h"
            : $"param($p) {HashInto("$p")}; $h";
        var arg = access.IsUrl ? UrlOf(access, relFile) : PathOf(access, relFile);
        return Lines(M.Sh($"$jobs = 1..{readers} | ForEach-Object {{ Start-Job -ScriptBlock {{ {block} }} -ArgumentList {Q(arg)} }}; $jobs | Wait-Job -Timeout 500 | Out-Null; $jobs | Receive-Job; $jobs | Remove-Job -Force", 600));
    }

    public override (string Hash, double Seconds) TimedSha256(Access access, string relFile)
    {
        var one = access.IsUrl
            ? $"$t = [IO.Path]::GetTempFileName(); curl.exe -sf -o $t {Q(UrlOf(access, relFile))}; $h = (Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash.ToLower(); Remove-Item $t"
            : HashInto(Q(PathOf(access, relFile)));
        var lines = Lines(M.Sh($"$sw = [Diagnostics.Stopwatch]::StartNew(); {one}; $sw.Stop(); $h; $sw.Elapsed.TotalSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)", 600));
        return (lines[0], ParseSeconds(lines[1]));
    }

    public override string ResumedDownloadSha256(Access access, string relFile)
    {
        var url = Q(UrlOf(access, relFile));
        return M.Sh($"$ErrorActionPreference = 'Stop'; $t = Join-Path $env:TEMP ('e2e-part-' + [guid]::NewGuid().ToString('N')); curl.exe -sf -r 0-{(Fixture.SizeOf("large.bin") / 2) - 1} -o $t {url}; curl.exe -sf -C - -o $t {url}; if ($LASTEXITCODE -ne 0) {{ throw 'curl -C - failed' }}; (Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash.ToLower(); Remove-Item $t", 600).Trim();
    }

    private static IReadOnlyList<string> Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
}
