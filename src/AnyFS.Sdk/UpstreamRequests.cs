using System.Net;
using System.Text;

namespace AnyFS.Sdk;

/// <summary>
/// Who hears what a source asks of whatever is behind it, its upstream: a server, a mirrored folder, a mail file
/// (<see cref="ISourceContext.UpstreamWatcher"/>). That is the host, whose Operations tab shows each as a row beside what
/// AnyFS itself was asked. A source that talks HTTP puts <see cref="UpstreamRequestsHandler"/> in its client's handlers and has
/// nothing more to do; any other says each call with <see cref="UpstreamWatcherExtensions.AskAsync{T}"/> or
/// <see cref="UpstreamWatcherExtensions.Ask{T}"/>, or with <see cref="Asked"/> for one that stays open.
/// </summary>
public interface IUpstreamWatcher
{
    /// <summary>The source is asking its upstream something; what this returns hears how it goes, ending with <see cref="IWatchedRequest.Done"/>.</summary>
    /// <param name="what">What is asked, in a word or two: GET, SFTP list, List folder.</param>
    /// <param name="target">Of what: a path there, or an address with anything secret left out (<see cref="UpstreamRequestsHandler.Shown"/>).</param>
    IWatchedRequest Asked(string what, string target);
}

/// <summary>One request of an upstream, as its watcher hears of it. It may be told things from any thread, and <see cref="Done"/> more than once.</summary>
public interface IWatchedRequest
{
    /// <summary>Bytes have gone by, in either direction.</summary>
    void AddBytes(long count);

    /// <summary>A listing has given entries.</summary>
    void AddEntries(long count);

    /// <summary>What the upstream said: "200 OK". Refused, the request counts as failed with that for its reason, whatever comes after.</summary>
    void Answered(string answer, bool refused);

    /// <summary>The request came to nothing; <paramref name="ct"/> tells one given up on from a failure.</summary>
    void Failed(Exception error, CancellationToken ct);

    void Done();
}

/// <summary>A call to an upstream as one request of its watcher's. Nothing happens but the call where there is no watcher.</summary>
public static class UpstreamWatcherExtensions
{
    /// <summary>The same as <see cref="AskAsync{T}"/>, for a call that blocks.</summary>
    public static T Ask<T>(this IUpstreamWatcher? watcher, string what, string target, Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (watcher is null)
        {
            return call();
        }

        var asked = watcher.Asked(what, target);
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            asked.Failed(ex, CancellationToken.None);
            throw;
        }
        finally
        {
            asked.Done();
        }
    }

    /// <summary>The same as <see cref="AskAsync(IUpstreamWatcher?, string, string, Func{Task}, CancellationToken)"/>, for a call that blocks.</summary>
    public static void Ask(this IUpstreamWatcher? watcher, string what, string target, Action call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (watcher is null)
        {
            call();
            return;
        }

        var asked = watcher.Asked(what, target);
        try
        {
            call();
        }
        catch (Exception ex)
        {
            asked.Failed(ex, CancellationToken.None);
            throw;
        }
        finally
        {
            asked.Done();
        }
    }

    public static async Task<T> AskAsync<T>(this IUpstreamWatcher? watcher, string what, string target, Func<Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (watcher is null)
        {
            return await call().ConfigureAwait(false);
        }

        var asked = watcher.Asked(what, target);
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            asked.Failed(ex, ct);
            throw;
        }
        finally
        {
            asked.Done();
        }
    }

    public static async Task AskAsync(this IUpstreamWatcher? watcher, string what, string target, Func<Task> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (watcher is null)
        {
            await call().ConfigureAwait(false);
            return;
        }

        var asked = watcher.Asked(what, target);
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            asked.Failed(ex, ct);
            throw;
        }
        finally
        {
            asked.Done();
        }
    }
}

/// <summary>
/// Tells a <see cref="IUpstreamWatcher"/> of every request that goes through it. Put it just above what sends the requests
/// (<see cref="Around"/>), below anything that retries or renews a sign-in, and each exchange with the server is one request
/// of the watcher's: the method, the address (<see cref="Shown"/>), what the server answered and the bytes that went by. It
/// lasts until the answer's body has been read or the response is disposed, so a slow download shows as running.
/// </summary>
public sealed class UpstreamRequestsHandler : DelegatingHandler
{
    /// <summary>A value in a query longer than this is left out of <see cref="Shown"/>: what long and opaque is goes by many names.</summary>
    private const int LongestValueShown = 128;

    private const string LeftOut = "…";

    // parts of the names under which a query carries what a row, its copy or a log must not: said loosely, as a page number lost costs nothing
    private static readonly string[] SecretNames = ["token", "secret", "pass", "pwd", "auth", "sig", "key", "credential", "ticket", "code", "session", "cookie", "assertion", "jwt"];

    private readonly IUpstreamWatcher _watcher;

    private UpstreamRequestsHandler(IUpstreamWatcher watcher, HttpMessageHandler inner)
        : base(inner)
    {
        _watcher = watcher;
    }

    /// <summary><paramref name="inner"/> with a <paramref name="watcher"/> told of what goes through it, or as it is where there is no watcher.</summary>
    public static HttpMessageHandler Around(HttpMessageHandler inner, IUpstreamWatcher? watcher)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return watcher is null ? inner : new UpstreamRequestsHandler(watcher, inner);
    }

    /// <summary>
    /// An address as a watcher is told it: no user name or password and no fragment, and of the query, only values that could
    /// not be a secret (a signed download link's signature, a sign-in's code, a bearer in a query are left out, with anything
    /// long, as <c>name=…</c>).
    /// </summary>
    public static string Shown(Uri? address)
    {
        if (address is null)
        {
            return "";
        }

        if (!address.IsAbsoluteUri)
        {
            var text = address.OriginalString;
            var mark = text.IndexOfAny(['?', '#']);
            return mark < 0 ? text : text[..mark];
        }

        var shown = new StringBuilder(address.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)).Append(address.AbsolutePath);
        if (address.Query.Length > 1)
        {
            var separator = '?';
            foreach (var part in address.Query[1..].Split('&'))
            {
                var equals = part.IndexOf('=');
                var name = equals < 0 ? "" : part[..equals];
                var value = equals < 0 ? part : part[(equals + 1)..];
                shown.Append(separator);
                if (equals >= 0)
                {
                    shown.Append(name).Append('=');
                }

                shown.Append(IsSecret(name, value) ? LeftOut : value);
                separator = '&';
            }
        }

        return shown.ToString();
    }

    private static bool IsSecret(string name, string value)
        => value.Length > LongestValueShown
            || value.StartsWith("eyJ", StringComparison.Ordinal)
            || SecretNames.Any(secret => name.Contains(secret, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var asked = _watcher.Asked(request.Method.Method, Shown(request.RequestUri));
        HttpResponseMessage response;
        try
        {
            asked.AddBytes(request.Content?.Headers.ContentLength ?? 0);
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            asked.Failed(ex, cancellationToken);
            asked.Done();
            throw;
        }

        Watch(request, response, asked);
        return response;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var asked = _watcher.Asked(request.Method.Method, Shown(request.RequestUri));
        HttpResponseMessage response;
        try
        {
            asked.AddBytes(request.Content?.Headers.ContentLength ?? 0);
            response = base.Send(request, cancellationToken);
        }
        catch (Exception ex)
        {
            asked.Failed(ex, cancellationToken);
            asked.Done();
            throw;
        }

        Watch(request, response, asked);
        return response;
    }

    private static void Watch(HttpRequestMessage request, HttpResponseMessage response, IWatchedRequest asked)
    {
        var status = (int)response.StatusCode;
        asked.Answered($"{status} {response.ReasonPhrase}".TrimEnd(), refused: status >= 400);
        if (HasNoBody(request, response))
        {
            asked.Done();
            return;
        }

        response.Content = new WatchedContent(response.Content, asked);
    }

    private static bool HasNoBody(HttpRequestMessage request, HttpResponseMessage response)
        => request.Method == HttpMethod.Head
            || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.ResetContent or HttpStatusCode.NotModified
            || response.Content.Headers.ContentLength == 0;

    /// <summary>
    /// An answer's body with the bytes counted as they are read, however they are read: all at once into HttpClient's buffer
    /// (<see cref="SerializeToStreamAsync(Stream, TransportContext?, CancellationToken)"/>), or as a stream.
    /// </summary>
    private sealed class WatchedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IWatchedRequest _asked;

        public WatchedContent(HttpContent inner, IWatchedRequest asked)
        {
            _inner = inner;
            _asked = asked;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var body = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                return new WatchedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _asked);
            }
            catch (Exception ex)
            {
                _asked.Failed(ex, cancellationToken);
                throw;
            }
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            try
            {
                return new WatchedStream(_inner.ReadAsStream(cancellationToken), _asked);
            }
            catch (Exception ex)
            {
                _asked.Failed(ex, cancellationToken);
                throw;
            }
        }

        // its Content-Length, where it has one, is among the headers taken from the content inside
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _asked.Done();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The body as it is read: the request is over at its end, or when it is closed before that.</summary>
    private sealed class WatchedStream(Stream body, IWatchedRequest asked) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = body.Read(buffer);
                Went(read, buffer.Length);
                return read;
            }
            catch (Exception ex)
            {
                asked.Failed(ex, CancellationToken.None);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                Went(read, buffer.Length);
                return read;
            }
            catch (Exception ex)
            {
                asked.Failed(ex, cancellationToken);
                throw;
            }
        }

        private void Went(int read, int askedFor)
        {
            if (read > 0)
            {
                asked.AddBytes(read);
            }
            else if (askedFor > 0)
            {
                asked.Done();
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                asked.Done();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await body.DisposeAsync().ConfigureAwait(false);
            asked.Done();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
