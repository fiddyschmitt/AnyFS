namespace AnyFS.Sources.SharePoint;

/// <summary>
/// What a test puts in place of Microsoft 365 and the browser (the fake SharePoint of the tests, for when the lab and the
/// sandbox are not there): the handler every request goes through, and the account the app sign-in or the web session
/// is, with its tokens or its cookies. A source (<see cref="SharePointSiteSource"/>) or the Test button given one signs
/// in with it instead of with MSAL or a browser, and sends its requests through it; a Windows sign-in needs none, its
/// requests going to whatever address it is given. Null for the real thing.
/// </summary>
/// <param name="Handler">A new handler for each client made, since a client disposes the handler it is given.</param>
/// <param name="Account">Who the sign-in is: the app sign-in's user name, the web session's account.</param>
/// <param name="Token">An app sign-in's bearer token; true asks for a fresh one, as after a 401.</param>
/// <param name="Cookies">
/// A web session's cookies: true at the start, when a person may be asked; false for a renewal, which throws
/// <see cref="SignInRequiredException"/> when it would need one.
/// </param>
/// <param name="BrowserName">What the Test button says the web session signed in with.</param>
internal sealed record SharePointStandIn(
    Func<HttpMessageHandler> Handler,
    string Account,
    Func<CancellationToken, bool, Task<string>> Token,
    Func<bool, CancellationToken, Task<IReadOnlyList<SessionCookie>>> Cookies,
    string BrowserName = "a stand-in browser");

/// <summary>An app sign-in, done: who it is, and its bearer tokens (true after a 401: a fresh one), from MSAL or a test's stand-in.</summary>
internal sealed record AppSignIn(string? Username, Func<CancellationToken, bool, Task<string>> Token);
