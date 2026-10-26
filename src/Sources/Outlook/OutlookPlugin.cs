using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// Outlook / Exchange (type <c>outlook</c>, folder <c>outlook</c>, spec 7.7): a mailbox as folders, a folder per message
/// holding the message as Markdown and as .eml with its attachments, contacts as .vcf, appointments as .ics. Read-only.
/// A mailbox on Exchange Online through the web session (Outlook on the web, no app registration) or an app sign-in
/// (Microsoft Graph), a mailbox on an Exchange server (EWS, a Windows account), or an Outlook data file (PST, OST).
/// </summary>
public sealed class OutlookPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    /// <summary>The service the app sign-in and its Graph calls are for, as their messages and log lines name it.</summary>
    internal static readonly Microsoft365Service Service = new("Exchange Online", "outlook", "the mailbox");

    /// <summary>The delegated Microsoft Graph scopes the app sign-in asks for: the mailbox's mail, calendars and contacts, read only.</summary>
    internal static readonly string[] MailScopes = ["Mail.Read", "Calendars.Read", "Contacts.Read"];

    /// <summary>The same as messages list them, for an organisation's own registration.</summary>
    internal const string MailPermissions = "Mail.Read, Calendars.Read and Contacts.Read";

    public string TypeId => "outlook";

    public string DisplayName => "Outlook";

    public string Description => "A mailbox on Exchange Online (through Outlook on the web with no app registration, or as an app through Microsoft Graph) or on an Exchange server (a Windows account), or an Outlook data file (.pst or .ost, read while Outlook has it open): its folders by year and month, a folder per message with the message as Markdown and .eml and its attachments, contacts as .vcf, appointments as .ics. Read-only.";

    public string TypeFolder => "outlook";

    /// <summary>Outlook's logo, a PNG (Assets/outlook.png).</summary>
    public Stream? OpenIcon() => typeof(OutlookPlugin).Assembly.GetManifestResourceStream("outlook.png");

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(OutlookSettings);

    public object CreateDefaultSettings() => new OutlookSettings();

    public ISource CreateSource(object settings) => new OutlookSource((OutlookSettings)settings);

    /// <summary>The mailbox's address, the server's host, or the file's name without its extension ("archive" for archive.pst).</summary>
    public string? SuggestName(object settings)
    {
        var s = (OutlookSettings)settings;
        if (s.File is null)
        {
            return null;
        }

        var name = s.Kind switch
        {
            OutlookKind.Mailbox => s.Address ?? s.WebHost!,
            OutlookKind.Server => s.ServerUrl!.Host,
            _ => Path.GetFileNameWithoutExtension(s.File.TrimEnd('\\', '/')),
        };
        var safe = MailNames.Clean(MailNames.Replace(name));
        return safe.Length == 0 ? null : safe;
    }

    public void NormaliseSettings(object settings)
    {
        var s = (OutlookSettings)settings;
        // a mailbox's address in lower case; Outlook on the web's as its origin (https://outlook.office365.us); else as given
        s.Path = s.Address ?? (s.WebHost is { } web ? "https://" + web : s.File ?? "");
    }

    /// <summary>
    /// The sign-in fields and the calendar window are a mailbox's; a file has none of the sign-in fields and of the
    /// window only the months ahead (how far a recurring series without an end is listed). The web session's and the app
    /// sign-in's fields show for Exchange Online, the Windows account's for a server.
    /// </summary>
    public bool IsVisible(object settings, string propertyName)
    {
        var s = (OutlookSettings)settings;
        return propertyName switch
        {
            nameof(OutlookSettings.SignInMethod) or nameof(OutlookSettings.CalendarMonthsBack) => s.Kind is OutlookKind.Mailbox or OutlookKind.Server,
            nameof(OutlookSettings.CalendarMonthsAhead) => s.Kind is OutlookKind.Mailbox or OutlookKind.Server or OutlookKind.DataFile,
            nameof(OutlookSettings.Cloud) => s.Kind == OutlookKind.Mailbox && s.WebCloud is null, // Outlook on the web's address names its cloud
            nameof(OutlookSettings.BrowserPath) => s.Kind == OutlookKind.Mailbox && !s.IsAppSignIn,
            nameof(OutlookSettings.Tenant) or nameof(OutlookSettings.ClientId) => s.Kind == OutlookKind.Mailbox && s.IsAppSignIn,
            nameof(OutlookSettings.Username) or nameof(OutlookSettings.Password) => s.Kind == OutlookKind.Server && s.EffectiveSignIn == OutlookSignIn.Windows,
            nameof(OutlookSettings.TrustAnyCertificate) => s.Kind == OutlookKind.Server,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (OutlookSettings)settings;
        switch (s.Kind)
        {
            case OutlookKind.None when s.File is null:
                yield return new ValidationMessage("Give a mailbox's address, or the path of a .pst or .ost file.", nameof(OutlookSettings.Path));
                break;
            case OutlookKind.None:
                yield return new ValidationMessage("That is neither a mailbox's address (jane@contoso.com), nor a server's (https://mail.contoso.com), nor an Outlook data file (one ends in .pst or .ost).", nameof(OutlookSettings.Path));
                break;
            case OutlookKind.Server:
                if (s.SignInMethod?.Trim() is { } chosen && (chosen.Equals(OutlookSettings.WebSession, StringComparison.OrdinalIgnoreCase) || chosen.Equals(OutlookSettings.AppBrowser, StringComparison.OrdinalIgnoreCase) || chosen.Equals(OutlookSettings.AppDeviceCode, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new ValidationMessage($"An Exchange server takes a Windows account: {OutlookSettings.WindowsCurrent}, or {OutlookSettings.Windows} with a user name and password. (The web session and the app sign-in are for Exchange Online.)", nameof(OutlookSettings.SignInMethod), IsError: false);
                }

                foreach (var problem in WindowsAccount.Problems(s.IsWindowsCurrent, s.EffectiveSignIn == OutlookSignIn.Windows ? s.Username : null, s.EffectiveSignIn == OutlookSignIn.Windows ? s.Password : null, nameof(OutlookSettings.SignInMethod), nameof(OutlookSettings.Username)))
                {
                    yield return problem;
                }

                if (s.ServerUrl!.Scheme == "http" && !s.TrustAnyCertificate)
                {
                    yield return new ValidationMessage("The server's address is http: the sign-in and the mail would go unencrypted. Exchange serves EWS over https.", nameof(OutlookSettings.Path), IsError: false);
                }

                if (s.CalendarMonthsBack is < 0 or > 600 || s.CalendarMonthsAhead is < 1 or > 600)
                {
                    yield return new ValidationMessage("The calendar window is between 0 and 600 months back and 1 and 600 ahead.", nameof(OutlookSettings.CalendarMonthsBack));
                }

                break;
            case OutlookKind.DataFile:
                if (!File.Exists(s.File!))
                {
                    yield return new ValidationMessage("No file at that path.", nameof(OutlookSettings.Path), IsError: false);
                }

                if (s.CalendarMonthsAhead is < 1 or > 600)
                {
                    yield return new ValidationMessage("Calendar: months ahead is between 1 and 600.", nameof(OutlookSettings.CalendarMonthsAhead));
                }

                break;
            case OutlookKind.Mailbox:
                if (s.WebHost is { } web && !string.Equals(s.SignInMethod?.Trim(), OutlookSettings.WebSession, StringComparison.OrdinalIgnoreCase))
                {
                    // Outlook on the web's address names no mailbox: whose it is, the web session's sign-in there says
                    yield return new ValidationMessage(s.IsAppSignIn
                        ? $"An app sign-in needs the mailbox's address here (jane@contoso.com). With Outlook on the web's address ({web}), sign in with {OutlookSettings.WebSession}."
                        : $"{web} is Outlook on the web for Exchange Online: sign in with {OutlookSettings.WebSession}.", nameof(OutlookSettings.SignInMethod));
                }
                else if (s.EffectiveSignIn == OutlookSignIn.WebSession && !string.Equals(s.SignInMethod?.Trim(), OutlookSettings.WebSession, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ValidationMessage($"A mailbox on Exchange Online signs in with {OutlookSettings.WebSession}, {OutlookSettings.AppBrowser} or {OutlookSettings.AppDeviceCode}.", nameof(OutlookSettings.SignInMethod));
                }

                if (s.IsAppSignIn && s.WebCloud is null)
                {
                    foreach (var message in CloudSettings.Check(s.Cloud, s.KnownCloud ?? MicrosoftCloud.Global, s.Tenant, s.EffectiveClientId, siteHost: null, MailPermissions, tenantRequired: false))
                    {
                        yield return message;
                    }
                }

                if (!s.IsAppSignIn && !MicrosoftCloud.IsChoice(s.Cloud))
                {
                    yield return new ValidationMessage($"Cloud is one of {MicrosoftCloud.AutomaticName}, {MicrosoftCloud.GlobalName}, {MicrosoftCloud.UsGovernmentName}.", nameof(OutlookSettings.Cloud));
                }

                if (!s.IsAppSignIn && s.Browser is { } browser && !File.Exists(browser))
                {
                    yield return new ValidationMessage("No browser at that path; leave it empty to find Edge or Chrome automatically.", nameof(OutlookSettings.BrowserPath), IsError: false);
                }

                if (s.CalendarMonthsBack is < 0 or > 600 || s.CalendarMonthsAhead is < 1 or > 600)
                {
                    yield return new ValidationMessage("The calendar window is between 0 and 600 months back and 1 and 600 ahead.", nameof(OutlookSettings.CalendarMonthsBack));
                }

                break;
        }

        if (s.RefreshMinutes is < 0 or > 10080)
        {
            yield return new ValidationMessage("Look for changes every must be between 0 (at the start only) and 10080 minutes (a week).", nameof(OutlookSettings.RefreshMinutes));
        }
    }

    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct) => TestAsync(settings, progress, null, ct);

    /// <summary>Opens the file, or signs in to the mailbox as the source will (a window where the saved sign-in has lapsed), and says what it holds.</summary>
    internal async Task<TestResult> TestAsync(object settings, IProgress<string> progress, OutlookStandIn? standIn, CancellationToken ct)
    {
        var s = (OutlookSettings)settings;
        if (Validate(s).FirstOrDefault(m => m.IsError) is { } problem)
        {
            return new TestResult(false, problem.Message);
        }

        if (s.Kind == OutlookKind.DataFile)
        {
            return File.Exists(s.File!) ? await Task.Run(() => TestFile(s, progress), ct).ConfigureAwait(false) : new TestResult(false, $"No file at {s.File}.");
        }

        try
        {
            var (service, directory, account, how) = await OutlookSource.ConnectAsync(s, standIn, NullLogger.Instance, interactive: true, progress.Report, hooks: null, ct).ConfigureAwait(false);
            using (service as IDisposable)
            using (ReferenceEquals(directory, service) ? null : directory as IDisposable)
            {
                progress.Report($"Signed in as {account}; listing the folders");
                var folders = await service.FindFoldersAsync(ct).ConfigureAwait(false);
                var shown = folders.Where(f => MailItemKinds.IsShownFolder(f.FolderClass, f.Name)).ToList();
                var inbox = folders.FirstOrDefault(f => f.DistinguishedId == "inbox");
                var top = string.Join(", ", shown.Where(f => f.ParentId == shown.FirstOrDefault(x => x.DistinguishedId == "inbox")?.ParentId).Take(6).Select(f => f.Name));
                return new TestResult(folders.Count > 0, $"Signed in as {account} {how}. The mailbox has {shown.Count:N0} folder{(shown.Count == 1 ? "" : "s")}"
                    + (inbox is null ? "" : $"; the Inbox holds {inbox.TotalCount:N0} item{(inbox.TotalCount == 1 ? "" : "s")}") + (top.Length == 0 ? "." : $" ({top}).")
                    + (folders.Count == 0 ? " The source would show nothing." : ""));
            }
        }
        catch (SignInRequiredException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (EwsException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (SourceFailedException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }

    private static TestResult TestFile(OutlookSettings s, IProgress<string> progress)
    {
        try
        {
            progress.Report($"Reading {Path.GetFileName(s.File!)}");
            using var store = new PstMailStore(s.File!, s.ShowHidden);
            var folders = 0;
            var items = 0L;
            void Count(IReadOnlyList<MailFolder> list)
            {
                foreach (var folder in list)
                {
                    folders++;
                    items += folder.ItemCount;
                    Count(folder.Folders);
                }
            }

            Count(store.Folders);
            var kind = store.Format switch
            {
                LibOutlook.PstFormat.Ansi => "an ANSI PST (Outlook 97-2002)",
                LibOutlook.PstFormat.Unicode => "a Unicode PST",
                _ => "an OST (Outlook 2013 or later)",
            };
            var top = string.Join(", ", store.Folders.Take(6).Select(f => f.Name)) + (store.Folders.Count > 6 ? ", …" : "");
            return new TestResult(folders > 0, $"{store.Name}: {kind} with {folders:N0} folder{(folders == 1 ? "" : "s")} and {items:N0} item{(items == 1 ? "" : "s")}{(top.Length == 0 ? "" : $" ({top})")}."
                + (folders == 0 ? " The source would show nothing." : "")
                + (store.Damaged > 0 ? $" Some of the file does not read in full ({store.Damaged} place{(store.Damaged == 1 ? "" : "s")}); the source serves the rest and its log names them." : ""));
        }
        catch (LibOutlook.PstException ex)
        {
            return new TestResult(false, $"{Path.GetFileName(s.File!)} could not be read as an Outlook data file: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }

    /// <summary>Where a mailbox's web-session profile lives: one per address.</summary>
    internal static string ProfileDir(string address) => WebSignInTarget.ProfileFor("outlook-browser", address, "ANYFS_OUTLOOK_PROFILES");

    /// <summary>The browser the web session drives, or null when there is none.</summary>
    internal static string? BrowserFor(OutlookSettings s) => BrowserLocator.Find(s.Browser, "ANYFS_OUTLOOK_BROWSER");

    /// <summary>A logger for the plugin's own work.</summary>
    internal static ILogger NoLogger => NullLogger.Instance;
}
