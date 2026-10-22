namespace AnyFS.Sdk;

/// <summary>Version of the plugin contract. The host refuses plugins built against a newer major.</summary>
public static class SdkInfo
{
    public static readonly Version SdkVersion = new(0, 2, 0);
}
