using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Jira;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A Jira source's Browser setting against a real browser (spec 7.10; the user, 10-Nov-2026: "Confirm it uses it
/// immediately during the source startup instead of HttpClient or Edge"). The first start signs in through the browser the
/// setting names; the next start through the session kept from before, with no browser at all. The fake Jira is served on a
/// port of 127.0.0.1, where a page the browser asks for signs it in, as an identity provider already signed in would. Runs
/// where ANYFS_TEST_ATLASSIAN_BROWSER names an Edge, Chrome or Chromium; never Firefox, which on the PC would join the
/// person's own.
/// </summary>
[TestClass]
public class AtlassianBrowserLiveTests
{
    private static readonly string[] Variables = ["ANYFS_JIRA_PROFILES", "ANYFS_JIRA_BROWSER", "ANYFS_JIRA_WEBDRIVER"];

    [TestMethod]
    public async Task TheBrowserTheSettingNamesIsTheOneTheSignInOpens()
    {
        var browser = Environment.GetEnvironmentVariable("ANYFS_TEST_ATLASSIAN_BROWSER");
        if (string.IsNullOrWhiteSpace(browser) || !File.Exists(browser) || BrowserLocator.KindOf(browser) is null or BrowserKind.Firefox)
        {
            Assert.Inconclusive("ANYFS_TEST_ATLASSIAN_BROWSER names no Edge, Chrome or Chromium");
        }

        var kept = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        var profiles = Path.Combine(Path.GetTempPath(), "anyfs-tests", "jira-profiles-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ANYFS_JIRA_PROFILES", profiles);
        Environment.SetEnvironmentVariable("ANYFS_JIRA_BROWSER", null);
        Environment.SetEnvironmentVariable("ANYFS_JIRA_WEBDRIVER", null);
        try
        {
            using var server = new JiraOverHttp();
            var log = new LineLogger();
            using var context = new TestSourceContext(logger: log);
            var settings = new JiraSettings { Address = server.Fake.Server, SignInMethod = AtlassianSignInChoices.WebSession, BrowserPath = browser, RefreshSeconds = 0 };
            var name = BrowserLocator.DisplayName(browser);
            await using (var first = new JiraSource(settings))
            {
                await first.InitialiseAsync(context, CancellationToken.None);
                Assert.AreEqual($"with {name}", first.Session.How, string.Join("\n", log.Lines));
            }

            Console.WriteLine(string.Join("\n", log.Lines.Where(l => l.Contains("jira:", StringComparison.Ordinal))));
            Console.WriteLine("pages asked for by: " + string.Join(" | ", server.PageAgents));
            Assert.IsTrue(log.Lines.Any(l => l.Contains($"opened {name} headless for 127.0.0.1", StringComparison.Ordinal)), "the setting's browser, headless first");
            Assert.IsTrue(server.PageAgents.Count > 0, "the browser asked for the installation's page");
            var edge = BrowserLocator.KindOf(browser) == BrowserKind.Edge;
            Assert.IsTrue(server.PageAgents.All(agent => agent.Contains("Edg/", StringComparison.Ordinal) == edge), "every page was asked for by that browser and no other");

            var pages = server.PageAgents.Count;
            await using (var second = new JiraSource(settings))
            {
                await second.InitialiseAsync(context, CancellationToken.None);
                Assert.AreEqual("with the session kept from before", second.Session.How);
            }

            Assert.AreEqual(pages, server.PageAgents.Count, "the next start opens no browser");
        }
        finally
        {
            foreach (var (variable, value) in kept)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }

            try
            {
                Directory.Delete(profiles, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// A <see cref="FakeJira"/> on a port of 127.0.0.1: what AnyFS asks is forwarded to it; a page a browser asks for (HTML)
    /// is answered with a session for the tester, as a sign-in already made at the organisation's identity provider would
    /// give it, and the browser's user agent noted.
    /// </summary>
    private sealed class JiraOverHttp : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpMessageInvoker _fake;
        private readonly CancellationTokenSource _stop = new();

        public JiraOverHttp()
        {
            var port = FreePort();
            Fake = new FakeJira(origin: $"http://127.0.0.1:{port}", root: "/jira");
            Fake.AddAccount("tester", "Test Er", JiraSeed.Token, JiraSeed.Password);
            Fake.AddProject("ANYFS", "AnyFS Lab");
            _fake = new HttpMessageInvoker(Fake.Handler());
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = ServeAsync();
        }

        public FakeJira Fake { get; }

        /// <summary>The user agent of every page asked for, in order.</summary>
        public ConcurrentQueue<string> PageAgents { get; } = new();

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _fake.Dispose();
            _stop.Dispose();
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AnswerAsync(context);
                    }
                    catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or HttpRequestException or OperationCanceledException)
                    {
                    }
                    finally
                    {
                        try
                        {
                            context.Response.Close();
                        }
                        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                        {
                        }
                    }
                });
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (request.AcceptTypes?.Any(t => t.Contains("text/html", StringComparison.OrdinalIgnoreCase)) == true)
            {
                PageAgents.Enqueue(request.UserAgent ?? "");
                var session = Fake.SessionFor("tester");
                response.Headers.Add("Set-Cookie", $"JSESSIONID={session.Value}; Path={session.Path}; HttpOnly");
                await WriteAsync(response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<html><head><title>System dashboard</title></head><body>Jira</body></html>"));
                return;
            }

            using var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), request.Url);
            if (request.HasEntityBody)
            {
                using var body = new MemoryStream();
                await request.InputStream.CopyToAsync(body, _stop.Token);
                message.Content = new ByteArrayContent(body.ToArray());
                if (request.ContentType is { } type)
                {
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                }
            }

            foreach (var header in request.Headers.AllKeys.OfType<string>())
            {
                if (!header.Equals("Host", StringComparison.OrdinalIgnoreCase) && !header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    message.Headers.TryAddWithoutValidation(header, request.Headers[header]);
                }
            }

            using var answer = await _fake.SendAsync(message, _stop.Token);
            var headers = answer.Headers.SelectMany(h => h.Value.Select(v => (h.Key, Value: v)))
                .Concat(answer.Content.Headers.Where(h => h.Key is not ("Content-Type" or "Content-Length")).Select(h => (h.Key, Value: string.Join(", ", h.Value))));
            foreach (var (header, value) in headers)
            {
                try
                {
                    response.Headers.Add(header, value);
                }
                catch (ArgumentException)
                {
                    // a header HttpListener sets itself
                }
            }

            await WriteAsync(response, (int)answer.StatusCode, answer.Content.Headers.ContentType?.ToString(), await answer.Content.ReadAsByteArrayAsync(_stop.Token));
        }

        private async Task WriteAsync(HttpListenerResponse response, int status, string? type, byte[] body)
        {
            response.StatusCode = status;
            if (type is not null)
            {
                response.ContentType = type;
            }

            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, _stop.Token);
        }
    }
}
