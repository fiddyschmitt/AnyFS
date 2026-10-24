namespace AnyFS.Sources.Outlook;

/// <summary>
/// What a test puts in place of Exchange and the browser (the fake Exchange of the tests, for when the sandbox and the
/// lab are not there): the handler every request goes through, the account the web session is, and its bearer tokens.
/// A source or the Test button given one skims no token from a browser and sends its requests through the handler.
/// Null for the real thing.
/// </summary>
/// <param name="Handler">A new handler for each client made, since a client disposes the handler it is given.</param>
/// <param name="Account">Who the sign-in is.</param>
/// <param name="Token">The bearer; true asks for a fresh one, as after a 401.</param>
/// <param name="Origin">Where the service answers (the fake's address), in place of the cloud's Outlook on the web.</param>
internal sealed record OutlookStandIn(Func<HttpMessageHandler> Handler, string Account, Func<bool, CancellationToken, Task<string>> Token, Uri Origin);
