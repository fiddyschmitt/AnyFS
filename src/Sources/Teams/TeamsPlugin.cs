using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>
/// Microsoft Teams (type <c>teams</c>, folder <c>teams</c>, spec 7.9): the signed-in account's teams and their channels,
/// and the account's chats, read-only, as Markdown with their files. Signed in by a web session (the Teams web client
/// mints no Graph token, so AnyFS reads Microsoft's own Teams services with the tokens the client mints, as Outlook's web
/// session reads <c>service.svc</c>). Cloud-only: there is no on-premises Teams.
/// </summary>
public sealed class TeamsPlugin : ISourcePlugin, ISettingVisibility
{
    public string TypeId => "teams";

    public string DisplayName => "Microsoft Teams";

    public string Description => "A Microsoft Teams account's teams, channels and chats as Markdown, with their files. Signed in through your browser, no app registration. Read-only.";

    public string TypeFolder => "teams";

    /// <summary>The default letter icon for now; an own mark is drawn in a later commit (the order of work, spec 7.9).</summary>
    public Stream? OpenIcon() => typeof(TeamsPlugin).Assembly.GetManifestResourceStream("teams.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(TeamsSettings);

    public object CreateDefaultSettings() => new TeamsSettings();

    public ISource CreateSource(object settings) => new TeamsSource((TeamsSettings)settings);

    /// <summary>The account, which names the source: <c>jane@contoso.com</c> → <c>jane (contoso.com)</c>.</summary>
    public string? SuggestName(object settings)
    {
        var account = ((TeamsSettings)settings).AccountHint;
        var at = account.IndexOf('@');
        var name = at > 0 ? $"{account[..at]} ({account[(at + 1)..]})" : account;
        var safe = string.Concat(name.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c)).Trim().Trim('.').Trim();
        return safe.Length == 0 ? null : safe;
    }

    public void NormaliseSettings(object settings)
    {
        var s = (TeamsSettings)settings;
        s.Account = s.Account.Trim();
    }

    /// <summary>The walk interval shows only with listings kept, as SharePoint and GitLab hide it.</summary>
    public bool IsVisible(object settings, string propertyName)
    {
        var s = (TeamsSettings)settings;
        return propertyName switch
        {
            nameof(TeamsSettings.WalkMinutes) => s.KeepListings,
            _ => true,
        };
    }
}
