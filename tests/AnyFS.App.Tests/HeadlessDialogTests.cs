using System.Diagnostics.CodeAnalysis;
using AnyFS.App.Gui.ViewModels;
using AnyFS.App.Gui.Views;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Tests;

/// <summary>
/// Every dialog is built and shown once on the headless platform: the XAML compiler can't see a binding
/// to a property that throws, a template that fails to build, or a resource that is missing at run time.
/// </summary>
[TestClass]
public class HeadlessDialogTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessDialogTests).Assembly);

    [TestMethod]
    public async Task EveryDialogBuildsAndShows()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "dialogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var paths = SettingsPaths.Resolve(Path.Combine(root, "settings"));
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                await using var engine = new AnyFsEngine(paths, registry, NullLoggerFactory.Instance);
                engine.Load();
                await engine.StartAsync(null, CancellationToken.None);
                var dialogs = new FakeDialogs();
                var clipboard = new FakeClipboard();
                var collection = engine.Collections.Single();

                await ShowAsync(new CollectionDialog(new CollectionDialogViewModel("Add collection", "")));

                var source = new SourceDialogViewModel(engine, dialogs, null, null, askPath: true);
                var sourceDialog = new SourceDialog(source);
                await ShowAsync(sourceDialog);
                // the generated form rendered one control per field of the memory plugin's settings
                var textBoxes = sourceDialog.GetVisualDescendants().OfType<TextBox>().Count();
                Assert.IsTrue(textBoxes >= 4, $"name, path, Files, Fail times, Secret...: {textBoxes} text boxes");
                Assert.IsTrue(sourceDialog.GetVisualDescendants().OfType<CheckBox>().Count() >= 4, "Read-only, Enabled, Count files, and the bool settings");
                sourceDialog.Close();

                var definition = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings());
                await engine.AddMembershipAsync(collection.Id, definition.Id, "mem/alpha", false);
                var membership = collection.Definition.Sources.Single();
                await ShowAsync(new SourceDialog(new SourceDialogViewModel(engine, dialogs, definition, membership, askPath: true)));
                await ShowAsync(new SourceDialog(new SourceDialogViewModel(engine, dialogs, definition, null, askPath: false)));
                await ShowAsync(new ExistingSourceDialog(new ExistingSourceDialogViewModel("alpha", "mem/alpha", "work")));

                await ShowAsync(new MountDialog(new MountDialogViewModel(engine, dialogs, collection, null)));
                await ShowAsync(new MountDialog(new MountDialogViewModel(engine, dialogs, collection, collection.Definition.Mounts.Single())));

                foreach (var kind in Enum.GetValues<PromptKind>())
                {
                    var request = new PromptRequest(kind, "Smart card PIN", "Enter the PIN for the card in reader 1", kind == PromptKind.Choice ? ["one", "two"] : null, kind == PromptKind.Text ? "default" : null);
                    await ShowAsync(new PromptDialog(new PromptDialogViewModel("aus-files-01", request)));
                }

                await ShowAsync(new ChooseDialog(new ChooseDialogViewModel("Found on this computer", "Choose one to fill in the form.", ["one", "two"])));
                await ShowAsync(new ConfirmDialog(new ConfirmDialogViewModel("Delete collection", "Delete 'work'?", "Delete", "No", "Cancel", "Also delete the orphaned sources", true)));
                await ShowAsync(new ConfirmDialog(new ConfirmDialogViewModel("Oops", "Something went wrong.", "OK", cancelText: null)));
                await ShowAsync(new TextDialog(new TextDialogViewModel("Mount command", "sudo mount -t nfs host:/work ~/anyfs/work", clipboard)));
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task TheSiteUrlIsAskedFirstAndNamesTheSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "dialogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new SharePointPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                await using var engine = new AnyFsEngine(SettingsPaths.Resolve(Path.Combine(root, "settings")), registry, NullLoggerFactory.Instance);
                engine.Load();
                await engine.StartAsync(null, CancellationToken.None);
                var source = new SourceDialogViewModel(engine, new FakeDialogs(), null, null, askPath: true);
                var dialog = new SourceDialog(source);
                dialog.Show();
                await Task.Delay(50);

                var site = dialog.FindControl<ContentControl>("PrimaryField")!.GetVisualDescendants().OfType<TextBox>().Single();
                var name = dialog.FindControl<TextBox>("NameBox")!;
                Assert.IsTrue(site.IsFocused, "the site's address has the focus");
                Assert.IsTrue(site.TranslatePoint(default, dialog)!.Value.Y < name.TranslatePoint(default, dialog)!.Value.Y, "it is asked above the name");
                Assert.AreEqual(1, dialog.GetVisualDescendants().OfType<TextBox>().Count(t => t.PlaceholderText == site.PlaceholderText), "and not again in the form");

                site.Text = "https://acme.sharepoint.us";
                Assert.AreEqual("acme.sharepoint.us", name.Text, "the host as given, not just the tenant");
                Assert.AreEqual("sharepoint/acme.sharepoint.us", source.Path);

                site.Text = "https://contoso.sharepoint.com/sites/Team";
                Assert.AreEqual("contoso.sharepoint.com Team", name.Text);
                Assert.AreEqual("sharepoint/contoso.sharepoint.com Team", source.Path);

                // several sites, one per line: the server they share, or no name where they share none
                Assert.IsTrue(site.AcceptsReturn, "the box takes several lines");
                site.Text = "https://acme.sharepoint.us/sites/Foo\r\nhttps://acme.sharepoint.us/Bar";
                Assert.AreEqual("acme.sharepoint.us", name.Text);
                Assert.AreEqual("sharepoint/acme.sharepoint.us", source.Path);
                site.Text = "http://intranet/sites/a\r\nhttp://teams.corp/sites/b";
                Assert.AreEqual("", name.Text, "a suggestion that lapses empties the box");
                dialog.Close();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task WhatDetectSaysTakesTheRowToTheDialogsEdge()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "dialogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new DetectingPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                await using var engine = new AnyFsEngine(SettingsPaths.Resolve(Path.Combine(root, "settings")), registry, NullLoggerFactory.Instance);
                engine.Load();
                await engine.StartAsync(null, CancellationToken.None);
                var source = new SourceDialogViewModel(engine, new FakeDialogs(), null, null, askPath: true);
                var dialog = new SourceDialog(source);
                dialog.Show();
                source.Form!.DetectMessage = "Filled in: Offline copy of jane@agency.gov (" + string.Join(Path.DirectorySeparatorChar, "C:", "Users", "jane", "AppData", "Local", "Microsoft", "Outlook", "jane@agency.gov.ost") + ")";
                await Task.Delay(50);

                var message = dialog.FindControl<TextBlock>("DetectMessage")!;
                var name = dialog.FindControl<TextBox>("NameBox")!;
                static double RightOf(Control control, Visual window) => control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                Assert.IsTrue(source.Form.ShowDetectAbove);
                Assert.AreEqual(RightOf(name, dialog), RightOf(message, dialog), 1, "to the column's right edge, as the Name box");
                var width = message.Bounds.Width;

                dialog.Width += 300;
                await Task.Delay(50);
                Assert.AreEqual(width + 300, message.Bounds.Width, 1, "the dialog made wider, so is the message");
                Assert.AreEqual(RightOf(name, dialog), RightOf(message, dialog), 1);

                // editing, where Detect sits beside Test in the form: the same
                var form = new SettingsFormViewModel(typeof(DetectedSettings), new DetectedSettings(), null, null, null, new FakeDialogs(), detector: new DetectingPlugin()) { DetectMessage = "Found nothing on this computer." };
                var window = new Window { Width = 700, Content = new SettingsFormView { DataContext = form } };
                window.Show();
                await Task.Delay(50);
                var said = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == form.DetectMessage);
                Assert.IsTrue(form.ShowDetectInForm);
                Assert.AreEqual(window.Bounds.Width, RightOf(said, window), 1, "to the window's edge");
                dialog.Close();
                window.Close();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A source type with a Detect button (that finds nothing), whose form lifts its address as Outlook's does.</summary>
    private sealed class DetectingPlugin : ISourcePlugin, IDetectable
    {
        public string TypeId => "detecting";

        public string DisplayName => "Detecting";

        public string Description => "a source type with a Detect button";

        public string TypeFolder => "detecting";

        public Stream? OpenIcon() => null;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type SettingsType => typeof(DetectedSettings);

        public object CreateDefaultSettings() => new DetectedSettings();

        public ISource CreateSource(object settings) => throw new NotSupportedException("never started");

        public Task<IReadOnlyList<DetectedSource>> DetectAsync(IProgress<string> progress, CancellationToken ct) => Task.FromResult<IReadOnlyList<DetectedSource>>([]);
    }

    private sealed class DetectedSettings
    {
        [Setting("Address", Required = true, Primary = true)]
        public string Address { get; set; } = "";
    }

    [TestMethod]
    public async Task TheChooserFitsItsLongestChoiceUpToMostOfTheScreen()
    {
        await Session.Dispatch(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();
            try
            {
                // Detect's finds as a work PC's Outlook gives them; the offline copy's path is the longest
                const string mailbox = "Mailbox jane@agency.gov (Exchange Online, US Government (GCC High))";
                var ost = $"Offline copy of jane@agency.gov ({string.Join(Path.DirectorySeparatorChar, "C:", "Users", "jane", "AppData", "Local", "Microsoft", "Outlook", "jane@agency.gov.ost")})";
                var narrow = await ChooseAsync(owner, ["one", "two"]);
                var fitted = await ChooseAsync(owner, [mailbox, ost]);
                var capped = await ChooseAsync(owner, [mailbox, string.Concat(Enumerable.Repeat(ost + " ", 3))]);

                Assert.AreEqual(560, narrow.Width, 1, $"short choices: the least width, not {narrow.Width}");
                Assert.IsTrue(fitted.Width > narrow.Width && fitted.Width < fitted.MaxWidth, $"wider, to fit the path: {fitted.Width} of at most {fitted.MaxWidth}");
                Assert.AreEqual(fitted.Heights[0], fitted.Heights[1], 0.5, $"the path on one line, as the mailbox: {string.Join(", ", fitted.Heights)}");
                Assert.IsTrue(capped.Width > fitted.Width && capped.Width <= capped.MaxWidth, $"no wider than most of the screen: {capped.Width} of {capped.MaxWidth}");
                Assert.IsTrue(capped.Heights[1] > 1.5 * capped.Heights[0], $"a choice longer than that wraps: {string.Join(", ", capped.Heights)}");
                Assert.IsTrue(capped.CanResize);
            }
            finally
            {
                owner.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    /// <summary>What a chooser came out as: its width, the most it may take, whether it resizes, and each choice's height.</summary>
    private sealed record Chooser(double Width, double MaxWidth, bool CanResize, double[] Heights);

    /// <summary>Shows a chooser over <paramref name="owner"/>, notes how it came out, and closes it without a choice.</summary>
    private static async Task<Chooser> ChooseAsync(Window owner, IReadOnlyList<string> choices)
    {
        var dialog = new ChooseDialog(new ChooseDialogViewModel("Found on this computer", "Choose one to fill in the form.", choices));
        var chosen = dialog.ShowAsync(owner);
        await Task.Delay(50);
        var shown = new Chooser(dialog.Bounds.Width, dialog.MaxWidth, dialog.CanResize,
            dialog.GetVisualDescendants().OfType<ListBoxItem>().Select(i => i.GetVisualDescendants().OfType<TextBlock>().First().Bounds.Height).ToArray());
        dialog.Close(null);
        Assert.IsNull(await chosen, "closed without a choice");
        return shown;
    }

    private static async Task ShowAsync(Window window)
    {
        window.Show();
        await Task.Delay(50);
        Assert.IsTrue(window.IsVisible, window.GetType().Name);
        Assert.IsTrue(window.GetVisualDescendants().Any(), $"{window.GetType().Name} has a visual tree");
        if (window.IsVisible)
        {
            window.Close();
        }
    }
}
