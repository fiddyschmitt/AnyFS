# AnyFS.Sdk

The plugin SDK for [AnyFS](https://github.com/fiddyschmitt/AnyFS), which presents anything as a
file system. A plugin is a class library with one package reference and one class:

- a **source** (`ISourcePlugin` + `ISource`, or derive from `SourceBase`) lists, stats, reads
  (whole and ranged) and optionally writes a tree of files: a folder, a server, an API, anything;
- a **mount provider** (`IMountProviderPlugin` + `IMount`) exposes a collection's merged tree
  through a protocol or a native file system.

Settings are a plain class with `[Setting]` attributes; AnyFS builds the form, the JSON and the
secret handling. Drop the built DLL (with its dependencies, `<EnableDynamicLoading>true</EnableDynamicLoading>`)
into the `plugins` folder beside AnyFS's settings and it is loaded at the next start.

```csharp
public sealed class HelloSettings
{
    [Setting("Greeting", Required = true)]
    public string Greeting { get; set; } = "hello";
}

public sealed class HelloPlugin : ISourcePlugin
{
    public string TypeId => "hello";
    public string DisplayName => "Hello";
    public string Description => "One file that says hello.";
    public string TypeFolder => "hello";
    public Stream? OpenIcon() => null;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(HelloSettings);
    public object CreateDefaultSettings() => new HelloSettings();
    public ISource CreateSource(object settings) => new HelloSource((HelloSettings)settings);
}
```

`anyfs plugin check MyPlugin.dll` tells you before a user does whether the DLL loads in the trimmed
host, which framework assemblies it may use, and whether its settings round-trip. `AnyFS.Sdk.Testing`
has `SourceContractTests`: point it at your source and it checks list/stat/read/range/write semantics.

The full walk-through: [docs/plugin-authoring.md](https://github.com/fiddyschmitt/AnyFS/blob/main/docs/plugin-authoring.md).
