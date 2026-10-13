# Writing a plugin

A plugin is a .NET class library that references `AnyFS.Sdk` and contains a class implementing
`ISourcePlugin` (a kind of source) or `IMountProviderPlugin` (a kind of mount). Drop the DLL
into the `plugins` folder beside `settings` and AnyFS loads it at start. Nothing in the SDK
references a UI framework.

## A source in 60 lines

[tests/fixtures/SamplePlugin](../tests/fixtures/SamplePlugin) is the complete example. The
shape:

```csharp
public sealed class SampleSettings
{
    [Setting("Greeting", Hint = "Shown in hello.txt", Required = true)]
    public string Greeting { get; set; } = "Hello from the sample plugin";

    [Setting("API token", Secret = true)]
    public string? Token { get; set; }
}

public sealed class SamplePlugin : ISourcePlugin
{
    public string TypeId => "sample";          // stable; stored in sources.json
    public string DisplayName => "Sample";
    public string Description => "A tiny read-only source.";
    public string TypeFolder => "sample";      // first segment of the default source path
    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SampleSettings);
    public object CreateDefaultSettings() => new SampleSettings();
    public ISource CreateSource(object settings) => new SampleSource((SampleSettings)settings);
}

public sealed class SampleSource(SampleSettings settings) : SourceBase
{
    protected override Task OnInitialiseAsync(CancellationToken ct) { ...; return Task.CompletedTask; }
    public override IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) { ... }
    public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) { ... }
}
```

`SourceBase` gives you `StatAsync` (from a listing of the parent), refuses writes unless your
`Capabilities` say `CanWrite`, and holds the `ISourceContext` (`Context`, `Logger`).

## The contract

- Paths are relative to your root, `/`-separated, no leading slash; the root is `""`. The host
  normalises them and rejects `..` before they reach you.
- `ListAsync` throws `VfsException` with `NotFound` or `NotADirectory`; `StatAsync` returns null
  for a missing path; `OpenAsync` returns an `IVfsFile` whose `ReadAsync(offset, buffer)` is the
  primitive (that's what every mount asks for; `VfsFileStream` wraps it when you need a `Stream`).
- Throw `VfsException` for anything the client should see (`VfsException.FromIo` maps the usual
  `System.IO` exceptions). Throw `SourceFailedException(message, permanent: true)` from
  `InitialiseAsync` when the settings are wrong; any other failure is retried with backoff.
- Report progress during a slow start with `Context.ReportProgress("Logging in", 0.3)`; ask
  the user for a PIN or code with `Context.PromptAsync(...)` (headless without a terminal, the
  source fails with "Needs input" until it's retried from the UI); report later trouble with
  `Context.ReportHealth(ComponentHealth.Degraded, "root X unreachable")` and recovery with
  `ComponentHealth.Ready`.
- Everything may be called concurrently from many threads.
- Read-only is not your setting: say what you *can* do in `Capabilities`; the user decides per
  collection and per mount whether writes are allowed.
- File counts: keep `Counters` up to date if you know your tree's size. When the user ticks
  "Count files on start" the host walks your tree directory by directory, unless your source
  implements `ICountable`, in which case it calls your `CountFilesAsync` instead (Mirror walks every
  configured path at the same time). Return `true` from the plugin's `IsQuickToCount` when that walk
  is cheap for your kind of source (local disks): the checkbox then defaults on for new sources of it.

## Settings

A plain POCO with public get/set properties. `[Setting]` gives the label and, optionally,
`Hint`, `Placeholder`, `Required`, `Kind`, `Group`, `Advanced`, `Order` and `Choices`. With
`Kind = Auto` (the default) the form follows the property type: `bool` is a checkbox, `int`,
`long`, `double`, `float` and `decimal` are number boxes (nullable ones may be left empty), an
enum or a property with `Choices` is a drop-down, a `Secret` string is a masked password box,
anything else is a text box; `Multiline`, `FilePath` and `FolderPath` (with a Browse button)
are chosen explicitly. An empty box becomes null for a property declared nullable (`string?`,
`int?`) and `""` for a plain `string`, so you only ever see a null you declared. `Group` puts fields under a heading and `Advanced` collapses them. The host builds the form, the
JSON (camelCase names in `sources.json`) and the secret handling: a `Secret` property is never
written to the settings files; it's kept in the protected secret store and filled back into your
POCO. Implement `IValidatable` to check settings before they're saved and `ITestable` to back a
Test button. When a hint or placeholder depends on the machine (an example path for this OS),
implement `ISettingTextProvider` on the plugin: `HintFor(propertyName)` and
`PlaceholderFor(propertyName)` are asked for each property and null keeps the attribute's text.

## Mount providers

A mount provider implements `IMountProviderPlugin` and returns an `IMount` per collection; the
mount gets an `IVirtualFileSystem` (the whole collection with the read-only layers applied, plus
`CanWrite(path)`) and an `IMountContext` (logger, state folder, stats sink, health reports and
`AcquireSharedAsync` for one listener shared by every mount on the same port). Report where the
mount can be reached in `AccessPaths` and what Open should do in `OpenAction` (a path for the
file manager, a URL, a command to run first, or text to show). `BindAddress` parses the usual
bind-address setting and names the host for access paths. A provider that can install what it is
missing (WinFsp through winget) implements `IInstallableProvider`: the tile and the Add mount
dialog show its button while `UnavailableReason` is set, and retry the mount after a success.

## Caching helpers

Caching is the source's business; the SDK makes the common strategies one-liners:

- `ListingCache`: memoises listings for a TTL, shares one upstream call between concurrent
  misses, invalidate on writes.
- `SpoolCache`: for upstreams that only stream sequentially. The first open starts the download
  into `Context.CacheDirectory`; any sub-range is served as soon as the file has reached it;
  concurrent readers share one download; eviction by size and age.
- `RandomAccessOverStream`: for upstreams that support ranged requests but hand back streams
  (SFTP, HTTP Range): keeps a few streams positioned where recent reads ended.

## Testing

Reference `AnyFS.Sdk.Testing` and derive a test class from `SourceContractTests`: point it at an
initialised instance of your source and it checks list/stat/read/range/write semantics.
`TestSourceContext` and `TestMountContext` stand in for the host; `MemorySource` is an in-memory
source for testing a mount provider.

## What a plugin may use from the framework

The host is a trimmed single file. Besides the whole of `AnyFS.Sdk`, these framework assemblies
are kept intact and may be used freely:

`System.Private.CoreLib`, `System.Runtime`, `System.Collections`, `System.Collections.Concurrent`,
`System.Linq`, `System.Memory`, `System.Threading`, `System.Threading.Tasks`,
`System.IO.Compression`, `System.IO.Pipelines`, `System.Net.Http`, `System.Net.Primitives`,
`System.Text.Json`, `System.Text.RegularExpressions`, `System.Security.Cryptography`,
`System.Diagnostics.Process`, `System.ComponentModel.Primitives`,
`Microsoft.Extensions.Logging.Abstractions`.

Anything else (other framework assemblies, your NuGet packages) ships beside your DLL: the host
loads each plugin in its own `AssemblyLoadContext` with `AnyFS.Sdk` resolved to the host's copy,
and resolves the rest from your `.deps.json`. Set `<EnableDynamicLoading>true</EnableDynamicLoading>`
in your project so the dependencies are copied next to it.

The host refuses a plugin built against a newer SDK than its own; the version is
`SdkInfo.SdkVersion`.
