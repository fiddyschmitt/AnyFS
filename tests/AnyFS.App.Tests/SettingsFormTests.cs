using AnyFS.App.Gui.ViewModels;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Mirror;

namespace AnyFS.App.Tests;

/// <summary>The form generated from [Setting] attributes (spec 6.7).</summary>
[TestClass]
public class SettingsFormTests
{
    private enum Flavour
    {
        Plain,
        Spicy,
    }

    private sealed class EverySetting : IValidatable
    {
        [Setting("Host", Required = true, Hint = "e.g. aus-files-01", Placeholder = "server", Order = 0)]
        public string Host { get; set; } = "";

        [Setting("Port", Order = 1)]
        public int Port { get; set; } = 22;

        [Setting("Password", Secret = true, Order = 2)]
        public string? Password { get; set; }

        [Setting("Notes", Kind = SettingKind.Multiline, Order = 3)]
        public string? Notes { get; set; }

        [Setting("Verify", Order = 4)]
        public bool Verify { get; set; } = true;

        [Setting("Flavour", Order = 5)]
        public Flavour Flavour { get; set; } = Flavour.Spicy;

        [Setting("Mode", Choices = ["fast", "safe"], Order = 6)]
        public string Mode { get; set; } = "safe";

        [Setting("Key file", Kind = SettingKind.FilePath, Group = "Authentication", Order = 7)]
        public string? KeyFile { get; set; }

        [Setting("Timeout", Advanced = true, Order = 8)]
        public double? Timeout { get; set; }

        public IEnumerable<ValidationMessage> Validate(object settings)
        {
            var s = (EverySetting)settings;
            if (s.Port < 1)
            {
                yield return new ValidationMessage("Port must be positive.", nameof(Port));
            }

            if (s.Host == "warn")
            {
                yield return new ValidationMessage("That host is slow.", nameof(Host), IsError: false);
            }

            if (s.Host == "global")
            {
                yield return new ValidationMessage("Nothing is right.");
            }
        }
    }

    private static SettingsFormViewModel Form(EverySetting settings)
        => new(typeof(EverySetting), settings, settings, null, null, new FakeDialogs());

    [TestMethod]
    public void FieldsFollowTheAttributesAndTheTypes()
    {
        var form = Form(new EverySetting { Password = "pw", Timeout = 1.5 });
        var byName = form.Fields.ToDictionary(f => f.PropertyName);
        Assert.AreEqual("Host *", byName["Host"].LabelWithMark);
        Assert.AreEqual("Text", byName["Host"].TemplateKey);
        Assert.AreEqual("server", byName["Host"].Placeholder);
        Assert.AreEqual("Number", byName["Port"].TemplateKey);
        Assert.AreEqual("22", byName["Port"].Text);
        Assert.AreEqual("Password", byName["Password"].TemplateKey);
        Assert.AreEqual("pw", byName["Password"].Text);
        Assert.IsTrue(byName["Password"].IsSecret);
        Assert.AreEqual("Multiline", byName["Notes"].TemplateKey);
        Assert.AreEqual("Bool", byName["Verify"].TemplateKey);
        Assert.IsTrue(byName["Verify"].Checked);
        Assert.AreEqual("Choice", byName["Flavour"].TemplateKey);
        CollectionAssert.AreEqual(new[] { "Plain", "Spicy" }, byName["Flavour"].Choices.ToList());
        Assert.AreEqual("Spicy", byName["Flavour"].SelectedChoice);
        Assert.AreEqual("Choice", byName["Mode"].TemplateKey);
        Assert.AreEqual("safe", byName["Mode"].SelectedChoice);
        Assert.AreEqual("Path", byName["KeyFile"].TemplateKey);
        Assert.AreEqual("1.5", byName["Timeout"].Text);

        CollectionAssert.AreEqual(new[] { "", "Authentication", "Advanced" }, form.Groups.Select(g => g.Name).ToList());
        Assert.IsFalse(form.Groups[0].HasName);
        Assert.IsTrue(form.Groups[1].HasName);
        Assert.IsTrue(form.Groups[2].IsAdvanced);
        Assert.AreEqual("Timeout", form.Groups[2].Fields.Single().PropertyName);
        Assert.IsFalse(form.HasTest);
    }

    [TestMethod]
    public void ValidateWritesTheFieldsBackAndReportsErrorsInline()
    {
        var settings = new EverySetting();
        var form = Form(settings);
        var byName = form.Fields.ToDictionary(f => f.PropertyName);

        Assert.IsFalse(form.Validate(), "Host is required");
        Assert.AreEqual("Required.", byName["Host"].Error);

        byName["Host"].Text = "  aus-files-01 ";
        byName["Port"].Text = "abc";
        Assert.IsFalse(form.Validate());
        Assert.IsNull(byName["Host"].Error);
        StringAssert.Contains(byName["Port"].Error!, "number");

        byName["Port"].Text = "-5";
        Assert.IsFalse(form.Validate(), "the plugin's own rule");
        Assert.AreEqual("Port must be positive.", byName["Port"].Error);

        byName["Port"].Text = "2222";
        byName["Password"].Text = "";
        byName["Notes"].Text = "a\r\nb";
        byName["Verify"].Checked = false;
        byName["Flavour"].SelectedChoice = "Plain";
        byName["Mode"].SelectedChoice = "fast";
        byName["KeyFile"].Text = @"C:\keys\id";
        byName["Timeout"].Text = "";
        Assert.IsTrue(form.Validate());
        Assert.AreEqual("aus-files-01", settings.Host, "trimmed");
        Assert.AreEqual(2222, settings.Port);
        Assert.IsNull(settings.Password, "empty secret is null");
        Assert.AreEqual("a\nb", settings.Notes, "multiline keeps lines, normalised");
        Assert.IsFalse(settings.Verify);
        Assert.AreEqual(Flavour.Plain, settings.Flavour);
        Assert.AreEqual("fast", settings.Mode);
        Assert.AreEqual(@"C:\keys\id", settings.KeyFile);
        Assert.IsNull(settings.Timeout, "an empty optional number is null");

        byName["Host"].Text = "warn";
        Assert.IsTrue(form.Validate(), "a warning doesn't block OK");
        Assert.AreEqual("That host is slow.", byName["Host"].Warning);

        byName["Host"].Text = "global";
        Assert.IsFalse(form.Validate());
        Assert.AreEqual("Nothing is right.", form.FormError);
    }

    [TestMethod]
    public async Task BrowseFillsThePathFromThePicker()
    {
        var dialogs = new FakeDialogs { FileToPick = @"D:\picked.key" };
        var settings = new EverySetting();
        var form = new SettingsFormViewModel(typeof(EverySetting), settings, settings, null, null, dialogs);
        var field = form.Fields.Single(f => f.PropertyName == "KeyFile");
        await field.BrowseCommand.ExecuteAsync(null);
        Assert.AreEqual(@"D:\picked.key", field.Text);
    }

    [TestMethod]
    public void MirrorSettingsMakeAMultilineRequiredPathsFieldAndAnAdvancedInterval()
    {
        var plugin = new MirrorPlugin();
        var settings = (MirrorSettings)plugin.CreateDefaultSettings();
        var form = new SettingsFormViewModel(plugin.SettingsType, settings, plugin, null, plugin as ISettingTextProvider, new FakeDialogs());
        var paths = form.Fields.Single(f => f.PropertyName == nameof(MirrorSettings.Paths));
        Assert.AreEqual("Multiline", paths.TemplateKey);
        Assert.IsTrue(paths.IsRequired);
        Assert.IsTrue(form.Groups.Last().IsAdvanced);
        Assert.IsFalse(form.Validate(), "no paths yet");
        Assert.AreEqual("Required.", paths.Error);

        paths.Text = OperatingSystem.IsWindows() ? "relative\\path" : "relative/path";
        Assert.IsFalse(form.Validate(), "the Mirror plugin validates each line");
        StringAssert.Contains(paths.Error!, "absolute");

        paths.Text = OperatingSystem.IsWindows() ? "C:\\" : "/tmp";
        Assert.IsTrue(form.Validate());
        Assert.AreEqual(paths.Text, settings.Paths);
    }

    [TestMethod]
    public void MemoryPluginSettingsRoundTrip()
    {
        var plugin = new MemoryPlugin();
        var settings = new MemoryPluginSettings { Files = "a.txt=A", Secret = "s" };
        var form = new SettingsFormViewModel(plugin.SettingsType, settings, null, null, null, new FakeDialogs());
        Assert.AreEqual("Multiline", form.Fields.Single(f => f.PropertyName == "Files").TemplateKey);
        Assert.AreEqual("Password", form.Fields.Single(f => f.PropertyName == "Secret").TemplateKey);
        form.Fields.Single(f => f.PropertyName == "FailTimes").Text = "3";
        Assert.IsTrue(form.Validate());
        Assert.AreEqual(3, settings.FailTimes);
    }

    [TestMethod]
    public void APluginCanWordPlaceholdersAndHintsForThisMachine()
    {
        var plugin = new MirrorPlugin();
        var form = new SettingsFormViewModel(plugin.SettingsType, plugin.CreateDefaultSettings(), plugin, null, plugin, new FakeDialogs());
        var paths = form.Fields.Single(f => f.PropertyName == nameof(MirrorSettings.Paths));
        Assert.AreEqual("One per line", paths.Hint, "the hint is the attribute's (the provider returns null for it)");
        Assert.AreEqual(MirrorPlugin.PathsPlaceholder(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()), paths.Placeholder);
        var lines = paths.Placeholder.Split('\n');
        Assert.AreEqual(2, lines.Length, "a local drive, then a typical network path");
        Assert.AreEqual(OperatingSystem.IsWindows() ? @"C:\" : "/", lines[0]);
        StringAssert.Contains(lines[1], "aus-files-01");
        Assert.AreEqual("How often unreachable paths are retried and reachable ones re-checked.", form.Fields.Single(f => f.PropertyName == nameof(MirrorSettings.CheckIntervalSeconds)).Hint, "other fields keep the attribute's text");

        Assert.AreEqual("C:\\\n\\\\aus-files-01\\shared", MirrorPlugin.PathsPlaceholder(windows: true, macOs: false));
        Assert.AreEqual("/\n/mnt/aus-files-01", MirrorPlugin.PathsPlaceholder(windows: false, macOs: false));
        Assert.AreEqual("/\n/Volumes/aus-files-01", MirrorPlugin.PathsPlaceholder(windows: false, macOs: true));

        var without = new SettingsFormViewModel(plugin.SettingsType, plugin.CreateDefaultSettings(), plugin, null, null, new FakeDialogs());
        Assert.AreEqual("", without.Fields.Single(f => f.PropertyName == nameof(MirrorSettings.Paths)).Placeholder, "without the provider the attribute's (empty) placeholder stands");
    }
}
