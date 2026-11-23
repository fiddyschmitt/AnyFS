using System.Collections.Concurrent;
using System.Net;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The loop every source's HTTP client sends through (<see cref="RequestCore"/>): what it does for all of them, shown on a
/// client with nothing of a product's own. What a product adds is in that product's tests.
/// </summary>
[TestClass]
public class RequestCoreTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ARequestThatGetsNoAnswerGivesItsPlaceBackWhileItPauses()
    {
        // one place: the first request's connection drops, and it pauses before it tries again (here: for good)
        var server = new Server((request, _) => request.RequestUri!.AbsolutePath == "/dropped"
            ? throw new HttpRequestException("the connection was closed")
            : Task.FromResult(Ok()));
        using var client = new Client(server, slots: 1) { Paused = Timeout.InfiniteTimeSpan };
        using var over = new CancellationTokenSource();
        var dropped = client.GetAsync("/dropped", over.Token);
        await server.Asked("/dropped", 1).WaitAsync(Soon);

        // each client held its place through that pause: with one place nothing else went until it was over
        using var answer = await client.GetAsync("/other", CancellationToken.None).WaitAsync(Soon);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);

        over.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => dropped);
    }

    [TestMethod]
    public async Task ARequestThatGetsNoAnswerIsSentThreeTimesAndThenSaysSo()
    {
        var server = new Server((_, _) => throw new HttpRequestException("the connection was closed"));
        using var client = new Client(server);

        var ex = await Assert.ThrowsAsync<VfsException>(() => client.GetAsync("/a", CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        StringAssert.Contains(ex.Message, "Product (server.test) did not answer: the connection was closed");
        Assert.AreEqual(3, server.Count("/a"));
    }

    [TestMethod]
    public async Task ARequestOfItsOwnThatIsCancelledIsNotTriedAgain()
    {
        var server = new Server(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });
        using var client = new Client(server);
        using var over = new CancellationTokenSource();
        var asked = client.GetAsync("/a", over.Token);
        await server.Asked("/a", 1).WaitAsync(Soon);

        over.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => asked);
        Assert.AreEqual(1, server.Count("/a"));
    }

    [TestMethod]
    public async Task APersonsRequestGoesAheadOfAWalksThatWaitedBeforeIt()
    {
        var let = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/first")
            {
                await let.Task;
            }

            return Ok();
        });
        using var client = new Client(server, slots: 1);
        var first = client.GetAsync("/first", CancellationToken.None);
        await server.Asked("/first", 1).WaitAsync(Soon);

        // each is waiting for the place by the time its call returns: nothing before that wait yields
        var walks = WalkAsync(client, "/walk");
        var persons = client.GetAsync("/person", CancellationToken.None);
        Assert.IsFalse(Walks.Current, "the walk's mark stays with the walk");

        let.SetResult();
        (await first.WaitAsync(Soon)).Dispose();
        (await walks.WaitAsync(Soon)).Dispose();
        (await persons.WaitAsync(Soon)).Dispose();
        CollectionAssert.AreEqual(new[] { "/first", "/person", "/walk" }, server.Order.ToArray(), "the person's asked last and went first");
    }

    [TestMethod]
    public async Task ARefusedSignInIsMadeAgainOnceAndTheRequestSentAgain()
    {
        var session = new Session();
        var server = new Server((request, _) => Task.FromResult(request.Headers.GetValues("X-Sign-In").Single() == "1" ? Status(HttpStatusCode.Unauthorized) : Ok()));
        using var client = new Client(server, session);

        using var answer = await client.GetAsync("/a", CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        CollectionAssert.AreEqual(new[] { 1 }, session.Renewals.ToArray(), "renewed once, for the sign-in the request went with");
        Assert.AreEqual(2, server.Count("/a"));
    }

    [TestMethod]
    public async Task ASignInRefusedAgainAfterItWasMadeAgainIsTheRequestsAnswer()
    {
        var session = new Session();
        var server = new Server((_, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized)));
        using var client = new Client(server, session);

        var ex = await Assert.ThrowsAsync<VfsException>(() => client.GetAsync("/a", CancellationToken.None));
        StringAssert.Contains(ex.Message, "answered 401");
        Assert.AreEqual(1, session.Renewals.Count, "not renewed a second time for the one request");
        Assert.AreEqual(2, server.Count("/a"));
    }

    [TestMethod]
    public async Task ASignInThatCannotBeMadeAgainIsNotAskedFor()
    {
        var session = new Session { CanRenew = false };
        var server = new Server((_, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized)));
        using var client = new Client(server, session);

        await Assert.ThrowsAsync<VfsException>(() => client.GetAsync("/a", CancellationToken.None));
        Assert.AreEqual(0, session.Renewals.Count);
        Assert.AreEqual(1, server.Count("/a"));
    }

    [TestMethod]
    public async Task TheSignInARequestWentWithIsTheOneItsRefusalAsksToRenew()
    {
        // one place, held; a second request waits for it; the sign-in is made again meanwhile (another request's refusal)
        var session = new Session();
        var let = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refuse = true;
        var server = new Server(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/first")
            {
                await let.Task;
                return Ok();
            }

            // the new sign-in is refused once too (the server lost its sessions): that refusal is this request's own
            if (refuse)
            {
                refuse = false;
                return Status(HttpStatusCode.Unauthorized);
            }

            return Ok();
        });
        using var client = new Client(server, session, slots: 1);
        var first = client.GetAsync("/first", CancellationToken.None);
        await server.Asked("/first", 1).WaitAsync(Soon);
        var second = client.GetAsync("/second", CancellationToken.None);

        await session.RenewAsync(1, CancellationToken.None);
        session.Renewals.Clear();
        let.SetResult();
        (await first.WaitAsync(Soon)).Dispose();

        // read before the wait (generation 1), the refusal of sign-in 2 asked for a renewal "unless renewed since 1":
        // none was made, the request went again with the sign-in just refused and failed
        using var answer = await second.WaitAsync(Soon);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        CollectionAssert.AreEqual(new[] { 2 }, session.Renewals.ToArray());
    }

    [TestMethod]
    public async Task AServersOwnFailureIsTriedAgainAndThenToldAsItIs()
    {
        var failures = 2;
        var server = new Server((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/down" || failures-- > 0 ? Status(HttpStatusCode.BadGateway) : Ok()));
        using var client = new Client(server);

        using var answer = await client.GetAsync("/a", CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual(3, server.Count("/a"), "twice more, as the clients did");

        var ex = await Assert.ThrowsAsync<VfsException>(() => client.GetAsync("/down", CancellationToken.None));
        StringAssert.Contains(ex.Message, "answered 502");
        Assert.AreEqual(3, server.Count("/down"));
    }

    [TestMethod]
    public async Task AnAnswerThatAsksForAWaitIsWaitedOutAndTheRequestSentAgain()
    {
        var throttled = true;
        var server = new Server((_, _) =>
        {
            if (!throttled)
            {
                return Task.FromResult(Ok());
            }

            throttled = false;
            var busy = Status(HttpStatusCode.TooManyRequests);
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(busy);
        });
        using var client = new Client(server);

        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        using var answer = await client.GetAsync("/a", CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.IsTrue(System.Diagnostics.Stopwatch.GetElapsedTime(began) >= TimeSpan.FromMilliseconds(900), "the second it asked for was waited");
        Assert.AreEqual(1, client.Holds, "the client was told of the hold, once");
    }

    [TestMethod]
    public async Task ARequestSentOnIsFollowedOnlyWhereTheClientSaysSo()
    {
        var server = new Server((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/a")
            {
                var moved = Status(HttpStatusCode.Found);
                moved.Headers.Location = new Uri("https://storage.test/kept/a?signed=1");
                return Task.FromResult(moved);
            }

            return Task.FromResult(Ok());
        });
        using var follows = new Client(server) { Follows = true };
        using var answer = await follows.GetAsync("/a", CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("storage.test", server.Hosts.Last());

        // a client that does not: the answer is its own to read (a redirect to a login page is a lapsed session to one)
        using var stays = new Client(server);
        var ex = await Assert.ThrowsAsync<VfsException>(() => stays.GetAsync("/a", CancellationToken.None));
        StringAssert.Contains(ex.Message, "answered 302");
    }

    [TestMethod]
    public async Task ARequestSentRoundInCirclesIsGivenUp()
    {
        var server = new Server((request, _) =>
        {
            var moved = Status(HttpStatusCode.TemporaryRedirect);
            moved.Headers.Location = new Uri("/a", UriKind.Relative);
            return Task.FromResult(moved);
        });
        using var client = new Client(server) { Follows = true };

        var ex = await Assert.ThrowsAsync<VfsException>(() => client.GetAsync("/a", CancellationToken.None));
        StringAssert.Contains(ex.Message, "more than 5 times");
        Assert.AreEqual(6, server.Count("/a"));
    }

    private static async Task<HttpResponseMessage> WalkAsync(Client client, string path)
    {
        Walks.Current = true;
        return await client.GetAsync(path, CancellationToken.None).ConfigureAwait(false);
    }

    private static HttpResponseMessage Ok() => Status(HttpStatusCode.OK);

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };

    /// <summary>A server that answers as a test says, and remembers what it was asked and in what order.</summary>
    private sealed class Server(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _asked = new();
        private readonly Lock _gate = new();
        private readonly List<(string Path, int Count, TaskCompletionSource Reached)> _waits = [];

        public IEnumerable<string> Order => _asked.Select(u => u.AbsolutePath);

        public IEnumerable<string> Hosts => _asked.Select(u => u.Host);

        public int Count(string path) => _asked.Count(u => u.AbsolutePath == path);

        /// <summary>Completes once <paramref name="path"/> has been asked for <paramref name="count"/> times.</summary>
        public Task Asked(string path, int count)
        {
            lock (_gate)
            {
                if (Count(path) >= count)
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((path, count, reached));
                return reached.Task;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _asked.Enqueue(request.RequestUri!);
                foreach (var wait in _waits.Where(w => Count(w.Path) >= w.Count).ToList())
                {
                    _waits.Remove(wait);
                    wait.Reached.SetResult();
                }
            }

            var response = await answer(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
    }

    /// <summary>A sign-in that is a number on each request, and is made again by counting up.</summary>
    private sealed class Session : IRequestSession
    {
        private int _generation = 1;

        public HttpClient Http { get; set; } = null!;

        public int Generation => Volatile.Read(ref _generation);

        public bool CanRenew { get; init; } = true;

        /// <summary>The sign-in each renewal was asked for.</summary>
        public ConcurrentQueue<int> Renewals { get; } = new();

        public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.TryAddWithoutValidation("X-Sign-In", Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.CompletedTask;
        }

        public Task RenewAsync(int seen, CancellationToken ct)
        {
            Renewals.Enqueue(seen);

            // as the sources' sessions do it: not again where it has been made again since the caller looked
            Interlocked.CompareExchange(ref _generation, seen + 1, seen);
            return Task.CompletedTask;
        }
    }

    /// <summary>A client with nothing of a product's own, and no pause between its tries unless a test asks for one.</summary>
    private sealed class Client : RequestCore
    {
        private readonly HttpClient _http;

        public Client(Server server, Session? session = null, int slots = 4)
            : this(server, session ?? new Session(), slots, new HttpClient(server) { BaseAddress = new Uri("https://server.test/") })
        {
        }

        private Client(Server server, Session session, int slots, HttpClient http)
            : base(Signed(session, http), RequestPace.Unpaced("core-test:" + Guid.NewGuid().ToString("N")), slots, NullLogger.Instance)
        {
            _http = http;
        }

        public TimeSpan Paused { get; init; } = TimeSpan.Zero;

        public bool Follows { get; init; }

        public int Holds { get; private set; }

        protected override string Product => "Product";

        protected override string Host => "server.test";

        protected override string LogName => "product";

        protected override Uri Server => new("https://server.test/");

        protected override bool FollowsRedirects => Follows;

        protected override TimeSpan Pause(int attempt, bool answered) => Paused;

        public Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
            => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(Server, path)), path, ct);

        protected override void OnHold(TimeSpan hold, HttpResponseMessage response, string? kind, string what) => Holds++;

        protected override Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
            => Task.FromResult(new VfsException(VfsError.IoError, $"Product answered {(int)response.StatusCode} to {what}"));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Session Signed(Session session, HttpClient http)
        {
            session.Http = http;
            return session;
        }
    }
}
