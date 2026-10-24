using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// Outlook / Exchange (type <c>outlook</c>, folder <c>outlook</c>, spec 7.7): a mailbox as folders, a folder per message
/// holding the message as Markdown and as .eml with its attachments, contacts as .vcf, appointments as .ics. Read-only.
/// This release reads Outlook's data files (PST and OST); mailboxes on their servers follow.
/// </summary>
public sealed class OutlookPlugin : ISourcePlugin, IValidatable, ITestable
{
    public string TypeId => "outlook";

    public string DisplayName => "Outlook";

    public string Description => "A mailbox from an Outlook data file (.pst or .ost, read while Outlook has it open): its folders by year and month, a folder per message with the message as Markdown and .eml and its attachments, contacts as .vcf, appointments as .ics. Read-only.";

    public string TypeFolder => "outlook";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(OutlookSettings);

    public object CreateDefaultSettings() => new OutlookSettings();

    public ISource CreateSource(object settings) => new OutlookSource((OutlookSettings)settings);

    /// <summary>The file's name without its extension ("archive" for archive.pst), or the mailbox address an OST is named after.</summary>
    public string? SuggestName(object settings)
    {
        var file = ((OutlookSettings)settings).File;
        if (file is null)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(file.TrimEnd('\\', '/'));
        var safe = MailNames.Clean(MailNames.Replace(name));
        return safe.Length == 0 ? null : safe;
    }

    public void NormaliseSettings(object settings)
    {
        var s = (OutlookSettings)settings;
        s.Path = s.File ?? "";
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (OutlookSettings)settings;
        if (s.File is null)
        {
            yield return new ValidationMessage("Give the path of a .pst or .ost file.", nameof(OutlookSettings.Path));
        }
        else if (s.LooksLikeAMailbox)
        {
            yield return new ValidationMessage("Mailboxes on a server (an address, Exchange Online, an Exchange server) are not served yet: give the path of a .pst or .ost file. Outlook's offline copy of a mailbox is the .ost under %LOCALAPPDATA%\\Microsoft\\Outlook.", nameof(OutlookSettings.Path));
        }
        else if (!s.IsDataFile)
        {
            yield return new ValidationMessage("An Outlook data file ends in .pst or .ost.", nameof(OutlookSettings.Path));
        }
        else if (!File.Exists(s.File))
        {
            yield return new ValidationMessage("No file at that path.", nameof(OutlookSettings.Path), IsError: false);
        }

        if (s.RefreshMinutes is < 0 or > 10080)
        {
            yield return new ValidationMessage("Look for changes every must be between 0 (at the start only) and 10080 minutes (a week).", nameof(OutlookSettings.RefreshMinutes));
        }
    }

    /// <summary>Opens the file as the source will and says what it holds.</summary>
    public Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (OutlookSettings)settings;
        if (Validate(s).FirstOrDefault(m => m.IsError) is { } problem)
        {
            return Task.FromResult(new TestResult(false, problem.Message));
        }

        if (!File.Exists(s.File!))
        {
            return Task.FromResult(new TestResult(false, $"No file at {s.File}."));
        }

        return Task.Run(() =>
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
                    + (folders == 0 ? " The source would show nothing." : ""));
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
        }, ct);
    }
}
