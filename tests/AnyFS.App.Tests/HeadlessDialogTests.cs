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
