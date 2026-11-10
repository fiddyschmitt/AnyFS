namespace AnyFS.Sources.Tests;

/// <summary>
/// Whether a fake server can be reached, as a test says: down (every request fails as one to an unreachable server does),
/// or holding (every request waits until it is given up). For the starts that fail, or are stopped, before they sign in.
/// </summary>
internal sealed class Reachability
{
    public bool Down { get; set; }

    public bool Holding { get; set; }

    /// <summary>Done once a request is being held.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A handler in front of <paramref name="inner"/> that does as this says: one for each session, since a session disposes its own.</summary>
    public HttpMessageHandler Around(HttpMessageHandler inner) => new Gate(inner, this);

    private sealed class Gate(HttpMessageHandler inner, Reachability state) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (state.Down)
            {
                throw new HttpRequestException("No such host is known.");
            }

            if (state.Holding)
            {
                state.Held.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }

            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}
