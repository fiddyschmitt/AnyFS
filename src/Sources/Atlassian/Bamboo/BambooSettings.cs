using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Bamboo;

public sealed class BambooSettings : IAtlassianSettings
{
    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://bamboo.example.com/browse/PROJ-PLAN",
        Hint = "A Bamboo server (Data Center), a project on it or a plan: its address as the browser shows it. A server shows every project you can see; a project its plans; a plan only itself. A page's address pasted here is cut back to what it belongs to: a build's (…/browse/PROJ-PLAN-12) names its plan.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [AtlassianSignInChoices.Token, AtlassianSignInChoices.WebSession, AtlassianSignInChoices.Password],
        Hint = AtlassianSignInChoices.Hint)]
    public string SignInMethod { get; set; } = AtlassianSignInChoices.Token;

    [Setting("Personal access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password, Hint = "A personal access token made under your profile, Personal access tokens (Bamboo 8.0 and later).")]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = "the name you sign in to Bamboo with")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Logs", Order = 5, Hint = "In each build, a folder per job with its log, as Bamboo keeps it.")]
    public bool Logs { get; set; } = true;

    [Setting("Artifacts", Order = 6, Hint = "In each job's folder, artifacts\\ with the files the job kept, each read on its own.")]
    public bool Artifacts { get; set; } = true;

    [Setting("Plan branches", Order = 7, Hint = "In each plan, branches\\ with a folder per plan branch and its builds.")]
    public bool PlanBranches { get; set; } = true;

    [Setting("Browser", Advanced = true, Order = 8, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome, Chromium or Firefox to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 9,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Builds kept", Advanced = true, Order = 10, Hint = "How many of a plan's newest builds (and of each branch's) are shown. A busy plan has thousands.")]
    public int BuildsKept { get; set; } = 25;

    [Setting("Use Bamboo's answers for (seconds)", Advanced = true, Order = 11,
        Hint = "How long an answer from Bamboo is used again before it is asked for again, as a file is opened or a folder not kept yet is listed. It checks no kept folder for changes: the walks do that (below). 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 12,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once and going through them again asks nothing of Bamboo; a change shows after the next walk (below). A folder that could not be listed shows empty until then. With 'Count files on start', the count walks the folders first and keeps what it finds. Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 13,
        Hint = "With folder listings kept: how long after one walk the next begins. The walks are what checks kept folders for changes, so this is how long a change can take to show. 0 walks once, at the start. Without 'Count files on start' a walk goes only through the folders that have been opened; with it, through every plan. A build that has finished is kept as it was, with every folder below it, so a walk over a quiet plan asks a request for its builds and one for its branches.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Plans at a time", Advanced = true, Order = 14,
        Hint = "How many plans a walk goes through at once, each one's folders listed together. A server with thousands of plans and the capacity to answer counts faster with more. However many are set, a server that asks AnyFS to wait (429) is waited for.")]
    public int PlansAtATime { get; set; } = 8;

    [Setting("Requests at a time per plan", Advanced = true, Order = 15,
        Hint = "How many of one plan's folders a walk lists at once, and how many requests one plan's own listing and opens have open with the server at once. Plans at a time times this is what AnyFS has open with the server at most.")]
    public int RequestsPerPlan { get; set; } = 8;

    internal AtlassianSignIn EffectiveSignIn => AtlassianSignInChoices.From(SignInMethod) ?? AtlassianSignIn.Token;

    AtlassianSignIn IAtlassianSettings.EffectiveSignIn => EffectiveSignIn;

    /// <summary>The address as given, tidied (<see cref="AtlassianAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    internal Uri? Url => AtlassianAddress.Tidy(Address);

    Uri? IAtlassianSettings.Url => Url;

    string? IAtlassianSettings.Token => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken.Trim();

    string? IAtlassianSettings.User => EffectiveSignIn == AtlassianSignIn.Password && !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : null;

    string? IAtlassianSettings.Secret => Password;

    string? IAtlassianSettings.Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    /// <summary>What the API client has open with the server at most: the two settings' product.</summary>
    int IAtlassianSettings.Concurrency => Requests;

    internal int Requests => Math.Clamp(PlansAtATime, 1, 64) * Math.Clamp(RequestsPerPlan, 1, 64);

    internal int Builds => Math.Clamp(BuildsKept, 1, 10_000);

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    /// <summary>Whether folder listings are kept: asked for, and a refresh interval to keep them by.</summary>
    internal bool Keeps => KeepListings && Refresh > TimeSpan.Zero;

    /// <summary>The settings that shape the tree, as one string: listings saved under another are not read back.</summary>
    internal string Shape => $"{Logs}|{Artifacts}|{PlanBranches}|{Builds}";

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));
}
