using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;

namespace AnyFS.E2E;

/// <summary>Which protocol a row reaches a collection through; one mount of each kind the host's OS has sits on every collection.</summary>
public enum MountKind
{
    WinFsp,
    Fuse,
    Smb,
    WebDav,
    Ftp,
    Http,
}

/// <summary>A source as the host serves it: its collection names and where the fixture tree sits inside them.</summary>
public sealed record SourceSpec(string Name, string Type, string TreePrefix, JsonObject Settings)
{
    /// <summary>The writable collection and its read-only twin.</summary>
    public string Collection => Name;

    public string ReadOnlyCollection => Name + "-ro";
}

/// <summary>
/// anyfs on one host machine of the lab (spec 16.2): the binaries copied out of the shared publish folder (or
/// over SCP for the Mac), the fixture generated there and on the upstream, one settings folder with a
/// collection per source (plus a read-only twin, an upstream-outage collection and a prompt-needing one), every
/// mount provider the OS has on each, <c>anyfs serve</c> started and watched through state/status.json.
/// </summary>
public sealed class AnyFsHost : IDisposable
{
    public const int HttpPort = 11001;
    public const int WebDavPort = 11000;
    public const int FtpPort = 2121;
    public const int SmbPort = 1445;
    public const string SmbUser = "user";
    public const string SmbPassword = "anyfs-smb";

    private readonly List<string> _started = [];

    public AnyFsHost(Machine host, Machine upstream)
    {
        Host = host;
        Upstream = upstream;
        Sources = DefineSources();
    }

    public Machine Host { get; }

    public Machine Upstream { get; }

    public IReadOnlyList<SourceSpec> Sources { get; }

    /// <summary>Collections with every mount: the sources' and their read-only twins.</summary>
    public IEnumerable<(string Collection, SourceSpec Source, bool ReadOnly)> FullCollections
        => Sources.SelectMany(s => new[] { (s.Collection, s, false), (s.ReadOnlyCollection, s, true) });

    public IReadOnlyList<MountKind> Mounts => Host.Info.Os switch
    {
        "windows" => [MountKind.WinFsp, MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
        "linux" => [MountKind.Fuse, MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
        _ => [MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
    };

    // where things live on the host
    public string LabDir => Host.Info.Os switch
    {
        "windows" => @"C:\anyfs-lab\anyfs",
        "macos" => $"/Users/{Host.Info.User}/anyfs-lab/anyfs",
        _ => $"/home/{Host.Info.User}/anyfs-lab/anyfs",
    };

    public string Exe => Host.IsWindows ? LabDir + @"\anyfs.exe" : LabDir + "/anyfs";

    public string SettingsDir => Join("settings");

    public string StatusFile => Join("state", "status.json");

    public string LogFile => Join("anyfs.log");

    public string FixtureDir => Host.IsWindows ? @"C:\anyfs-lab\fixture" : Parent(LabDir) + "/fixture";

    public string OutageDir => Host.IsWindows ? @"C:\anyfs-lab\outage" : Parent(LabDir) + "/outage";

    public string SshfsKeyFile => Join("sshfs_key");

    /// <summary>The FUSE mount point of a collection (Linux hosts).</summary>
    public string FuseMountPoint(string collection) => $"/home/{Host.Info.User}/anyfs/{collection}";

    private string Join(params string[] parts) => Host.IsWindows ? LabDir + "\\" + string.Join("\\", parts) : LabDir + "/" + string.Join("/", parts);

    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    // ---- deployment -----------------------------------------------------------------------------------------

    public void Deploy()
    {
        Stop();
        CopyBinaries();
        EnsureFixture(Host, FixtureDir, Host.IsWindows ? LabDir + @"\MakeTree.exe" : LabDir + "/MakeTree");
        EnsureFixture(Upstream, Lab.FixtureDir, $"/home/{Upstream.Info.User}/anyfs-lab/MakeTree");
        EnsureOutageTree();
        InstallSshfsKey();
        WriteSettings();
    }

    private void CopyBinaries()
    {
        var rid = Host.Info.Os switch { "windows" => "win-x64", "macos" => "osx-arm64", _ => "linux-x64" };
        if (Host.IsWindows)
        {
            Host.Sh($"""
                $ErrorActionPreference = 'Stop'
                New-Item -ItemType Directory -Force '{LabDir}', '{LabDir}\plugins', '{LabDir}\settings' | Out-Null
                Copy-Item '\\vboxsvr\publish\{rid}\anyfs.exe', '\\vboxsvr\publish\{rid}\MakeTree.exe' '{LabDir}\' -Force
                Copy-Item '\\vboxsvr\publish\plugins\AnyFS.SamplePlugin.dll' '{LabDir}\plugins\' -Force
                Get-Item '{LabDir}\anyfs.exe' | Select-Object -ExpandProperty Length
                """);
        }
        else if (Host.IsMac)
        {
            Host.Sh($"mkdir -p '{LabDir}/plugins' '{LabDir}/settings'");
            // a fresh file, renamed into place: macOS may refuse to run a binary overwritten in place (the ft lab's finding)
            Host.Upload(Path.Combine(Lab.PublishDir, rid, "anyfs"), LabDir + "/anyfs.new");
            Host.Upload(Path.Combine(Lab.PublishDir, rid, "MakeTree"), LabDir + "/MakeTree.new");
            Host.Upload(Path.Combine(Lab.PublishDir, "plugins", "AnyFS.SamplePlugin.dll"), LabDir + "/plugins/AnyFS.SamplePlugin.dll");
            Host.Sh($"cd '{LabDir}' && chmod +x anyfs.new MakeTree.new && mv -f anyfs.new anyfs && mv -f MakeTree.new MakeTree && xattr -d com.apple.quarantine anyfs MakeTree 2>/dev/null; ls -l anyfs");
        }
        else
        {
            var share = $"{Lab.LinuxPublishDir}/{rid}";
            Host.Sh($"set -e; mkdir -p '{LabDir}/plugins' '{LabDir}/settings'; test -f '{share}/anyfs' || {{ echo 'no build in {share}: run provisioning/publish.ps1 on the PC' >&2; exit 2; }}; cp '{share}/anyfs' '{share}/MakeTree' '{LabDir}/'; cp '{Lab.LinuxPublishDir}/plugins/AnyFS.SamplePlugin.dll' '{LabDir}/plugins/'; chmod +x '{LabDir}/anyfs' '{LabDir}/MakeTree'; ls -l '{LabDir}/anyfs'");
        }

        // the upstream generates its own copy of the fixture with MakeTree from the share
        if (!ReferenceEquals(Upstream, Host))
        {
            Upstream.Sh($"set -e; mkdir -p ~/anyfs-lab; cp '{Lab.LinuxPublishDir}/linux-x64/MakeTree' ~/anyfs-lab/MakeTree; chmod +x ~/anyfs-lab/MakeTree");
        }
    }

    /// <summary>The fixture tree, generated on the machine when its manifest is missing or short (so a re-run is instant).</summary>
    private static void EnsureFixture(Machine machine, string dir, string makeTree)
    {
        if (machine.IsWindows)
        {
            machine.Sh($$"""
                $ErrorActionPreference = 'Stop'
                $m = '{{dir}}\{{Fixture.ManifestName}}'
                if (-not (Test-Path $m) -or @(Get-Content $m).Count -ne {{Fixture.ManifestLines}}) {
                    if (Test-Path '{{dir}}') { Remove-Item -Recurse -Force '{{dir}}' }
                    & '{{makeTree}}' '{{dir}}' | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "MakeTree failed with $LASTEXITCODE" }
                }
                @(Get-Content $m).Count
                """, 600);
        }
        else
        {
            machine.Sh($"set -e; if [ \"$(wc -l < '{dir}/{Fixture.ManifestName}' 2>/dev/null)\" != \"{Fixture.ManifestLines}\" ]; then rm -rf '{dir}'; mkdir -p \"$(dirname '{dir}')\"; '{makeTree}' '{dir}' >/dev/null; fi; wc -l < '{dir}/{Fixture.ManifestName}'", 600);
        }
    }

    /// <summary>A small tree the upstream-outage row moves away and back.</summary>
    private void EnsureOutageTree()
    {
        if (Host.IsWindows)
        {
            Host.Sh($"$ErrorActionPreference = 'Stop'; if (Test-Path '{OutageDir}.off') {{ Move-Item '{OutageDir}.off' '{OutageDir}' }}; New-Item -ItemType Directory -Force '{OutageDir}' | Out-Null; Copy-Item '{FixtureDir}\\readme.txt', '{FixtureDir}\\small.bin' '{OutageDir}\\' -Force");
        }
        else
        {
            Host.Sh($"set -e; [ ! -d '{OutageDir}.off' ] || mv '{OutageDir}.off' '{OutageDir}'; mkdir -p '{OutageDir}'; cp '{FixtureDir}/readme.txt' '{FixtureDir}/small.bin' '{OutageDir}/'");
        }
    }

    /// <summary>The key the SSHFS source logs in to the upstream with, fetched from the upstream and put beside anyfs.</summary>
    private void InstallSshfsKey()
    {
        var key = Upstream.DownloadText(Lab.SshfsKeyPath) ?? throw new InvalidOperationException($"no SSHFS key at {Upstream.Name}:{Lab.SshfsKeyPath}; was the upstream provisioned?");
        if (Host.IsWindows)
        {
            Host.UploadText(key, "/" + SshfsKeyFile.Replace('\\', '/'));
        }
        else
        {
            Host.UploadText(key, SshfsKeyFile);
            Host.Sh($"chmod 600 '{SshfsKeyFile}'");
        }
    }

    private IReadOnlyList<SourceSpec> DefineSources()
    {
        var list = new List<SourceSpec>
        {
            new("mirror", "mirror", TreePrefixOfLocalPath(FixtureDir), new JsonObject { ["paths"] = FixtureDir }),
            new("sshfs", "sshfs", "", new JsonObject
            {
                ["host"] = Upstream.Ip,
                ["port"] = 22,
                ["username"] = Upstream.Info.User,
                ["root"] = Lab.FixtureDir,
                ["keyFile"] = SshfsKeyFile,
                ["connections"] = 4,
            }),
        };
        if (Host.IsWindows)
        {
            list.Add(new("unc", "mirror", $"{Upstream.Ip}/fixture", new JsonObject { ["paths"] = $@"\\{Upstream.Ip}\fixture" }));
        }

        return list;
    }

    /// <summary>Mirror shows a path under its own chain: /home/user/x as home/user/x, C:\a as c/a (docs/settings-format.md).</summary>
    public static string TreePrefixOfLocalPath(string path)
    {
        if (path.Length >= 2 && path[1] == ':')
        {
            return char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        }

        return path.TrimStart('/');
    }

    private void WriteSettings()
    {
        var ids = new Dictionary<string, Guid>();
        Guid IdOf(string name) => ids.TryGetValue(name, out var id) ? id : ids[name] = Guid.NewGuid();

        var sources = new JsonArray();
        foreach (var s in Sources)
        {
            sources.Add(SourceJson(IdOf(s.Name), s.Name, s.Type, (JsonObject)JsonNode.Parse(s.Settings.ToJsonString())!));
        }

        sources.Add(SourceJson(IdOf("outage"), "outage", "mirror", new JsonObject { ["paths"] = OutageDir, ["checkIntervalSeconds"] = 5 }));
        // no key, no password: headless anyfs can't answer the prompt, so this fails cleanly with "Needs input"
        sources.Add(SourceJson(IdOf("prompt"), "prompt", "sshfs", new JsonObject { ["host"] = Upstream.Ip, ["port"] = 22, ["username"] = Upstream.Info.User, ["root"] = Lab.FixtureDir }));

        var collections = new JsonArray();
        foreach (var (collection, source, readOnly) in FullCollections)
        {
            collections.Add(CollectionJson(collection, IdOf(source.Name), readOnly, Mounts.Select(m => MountJson(m, collection))));
        }

        collections.Add(CollectionJson("outage", IdOf("outage"), false, [MountJson(MountKind.Http, "outage")]));
        collections.Add(CollectionJson("prompt", IdOf("prompt"), false, [MountJson(MountKind.Http, "prompt")]));

        var sourcesJson = new JsonObject { ["version"] = 1, ["sources"] = sources }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var collectionsJson = new JsonObject { ["version"] = 1, ["collections"] = collections }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var appJson = new JsonObject { ["version"] = 1, ["logLevel"] = "debug", ["bindAddress"] = "0.0.0.0", ["countFilesOnStart"] = true }.ToJsonString();
        if (Host.IsWindows)
        {
            var settings = "/" + SettingsDir.Replace('\\', '/');
            // fresh state and a fresh log, so the log check sees this run only
            Host.Sh($"Remove-Item -Recurse -Force '{Join("state")}', '{LogFile}' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '{SettingsDir}' | Out-Null");
            Host.UploadText(sourcesJson, settings + "/sources.json");
            Host.UploadText(collectionsJson, settings + "/collections.json");
            Host.UploadText(appJson, settings + "/app.json");
        }
        else
        {
            Host.Sh($"rm -rf '{Join("state")}' '{LogFile}'; mkdir -p '{SettingsDir}'");
            Host.UploadText(sourcesJson, SettingsDir + "/sources.json");
            Host.UploadText(collectionsJson, SettingsDir + "/collections.json");
            Host.UploadText(appJson, SettingsDir + "/app.json");
        }
    }

    private static JsonObject SourceJson(Guid id, string name, string type, JsonObject settings) => new()
    {
        ["id"] = id.ToString(),
        ["name"] = name,
        ["type"] = type,
        ["enabled"] = true,
        ["countFilesOnStart"] = true,
        ["settings"] = settings,
    };

    private static JsonObject CollectionJson(string name, Guid sourceId, bool readOnly, IEnumerable<JsonObject> mounts) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["enabled"] = true,
        ["sources"] = new JsonArray(new JsonObject { ["id"] = sourceId.ToString(), ["path"] = "", ["readOnly"] = readOnly }),
        ["mounts"] = new JsonArray(mounts.ToArray<JsonNode?>()),
    };

    private JsonObject MountJson(MountKind kind, string collection)
    {
        var (provider, settings) = kind switch
        {
            MountKind.WinFsp => ("winfsp", new JsonObject()),
            MountKind.Fuse => ("fuse", new JsonObject { ["mountPoint"] = FuseMountPoint(collection), ["allowOther"] = true }),
            MountKind.Smb => ("smb", new JsonObject { ["bindAddress"] = "0.0.0.0", ["port"] = SmbPort, ["guest"] = false, ["user"] = SmbUser, ["password"] = SmbPassword }),
            MountKind.WebDav => ("webdav", new JsonObject { ["bindAddress"] = "0.0.0.0", ["port"] = WebDavPort }),
            MountKind.Ftp => ("ftp", new JsonObject { ["bindAddress"] = "0.0.0.0", ["port"] = FtpPort, ["allowAnonymous"] = true, ["passivePortFrom"] = 21100, ["passivePortTo"] = 21120 }),
            MountKind.Http => ("http", new JsonObject { ["bindAddress"] = "0.0.0.0", ["port"] = HttpPort }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = kind.ToString(),
            ["provider"] = provider,
            ["enabled"] = true,
            ["readOnly"] = false,
            ["settings"] = settings,
        };
    }

    // ---- the process ------------------------------------------------------------------------------------------

    public void Start()
    {
        if (Host.IsWindows)
        {
            // through RunRemote, in the logged-on session (spec 16.2): WinFsp's \\anyfs\... and stored credentials live there
            var info = new ProcessStartInfo(Lab.RunRemoteClient)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { $"{Host.Ip}:8888", Exe, "serve", "--settings-dir", SettingsDir, "--log-level", "debug" })
            {
                info.ArgumentList.Add(a);
            }

            using var p = Process.Start(info) ?? throw new InvalidOperationException("runremote did not start");
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20_000) || p.ExitCode != 0)
            {
                throw new InvalidOperationException($"runremote to {Host.Ip} failed (exit {p.ExitCode}): {output}");
            }

            Trace.WriteLine($"runremote {Host.Ip}: {output.Trim()}");
        }
        else
        {
            Host.Sh($"cd '{LabDir}' && (nohup '{Exe}' serve --settings-dir '{SettingsDir}' --log-level debug > '{LabDir}/serve.out' 2>&1 &) ; sleep 1; pgrep -f '{Exe} serve' >/dev/null && echo started");
        }

        _started.Add(Exe);
    }

    /// <summary>Waits until the named collections are green (every source and mount up); throws with the status and the log tail otherwise.</summary>
    public StatusSnapshot WaitReady(IEnumerable<string> collections, TimeSpan? timeout = null)
    {
        var wanted = collections.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        StatusSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = Status();
            if (last is not null && wanted.All(name => last.Collections.FirstOrDefault(c => c.Name == name) is { Colour: "Green" }))
            {
                return last;
            }

            Thread.Sleep(1000);
        }

        var state = last is null ? "no status.json yet" : string.Join("\n", last.Collections.Select(c => $"{c.Name}: {c.Colour} {c.Text}; " + string.Join(", ", c.Sources.Select(s => $"{s.Name}={s.State}({s.Text})").Concat(c.Mounts.Select(m => $"{m.Name}={m.State}({m.Text})")))));
        throw new TimeoutException($"anyfs on {Host.Name} did not get {string.Join(", ", wanted)} green:\n{state}\n--- anyfs.log tail\n{LogTail(40)}");
    }

    public StatusSnapshot? Status()
    {
        var json = Host.DownloadText(StatusFile);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.StatusSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string LogTail(int lines)
    {
        var log = Host.DownloadText(LogFile) ?? "";
        var all = log.Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    public string Log() => Host.DownloadText(LogFile) ?? "";

    public void Stop()
    {
        if (Host.IsWindows)
        {
            Host.Run($"Get-Process anyfs -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{Exe}' }} | Stop-Process -Force; Start-Sleep -Milliseconds 800; Remove-Item '{StatusFile}' -ErrorAction SilentlyContinue", 60);
        }
        else
        {
            Host.Run($"pkill -TERM -f '{Exe} serve' || true; for i in 1 2 3 4 5 6 7 8 9 10; do pgrep -f '{Exe} serve' >/dev/null || break; sleep 1; done; pkill -KILL -f '{Exe} serve' || true; rm -f '{StatusFile}'", 60);
            if (Host.IsLinux)
            {
                // a FUSE mount left behind by a killed process would make the next start fail
                Host.Run($"for m in $(findmnt -rn -t fuse.anyfs,fuse -o TARGET | grep '^/home/{Host.Info.User}/anyfs/' || true); do fusermount3 -u \"$m\" 2>/dev/null || true; done", 60);
            }
        }

        _started.Clear();
    }

    public void Dispose() => Stop();
}
