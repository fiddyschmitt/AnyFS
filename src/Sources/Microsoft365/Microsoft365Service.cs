namespace AnyFS.Sources.Microsoft365;

/// <summary>
/// The Microsoft 365 service a sign-in (<see cref="EntraSignIn"/>) and a Graph client (<see cref="GraphClient"/>) work
/// for, as their messages and log lines name it: SharePoint and its site, Exchange Online and its mailbox.
/// </summary>
/// <param name="Name">The service in messages: "SharePoint is throttling this app", "Sign in to SharePoint".</param>
/// <param name="LogPrefix">The prefix of the log lines: "sharepoint".</param>
/// <param name="Storage">Whose storage a full quota is, in messages: "the site".</param>
public sealed record Microsoft365Service(string Name, string LogPrefix, string Storage);
