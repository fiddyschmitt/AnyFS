using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Teams;

/// <summary>
/// Enough of the Chrome DevTools protocol for the web session: commands over one WebSocket, answers matched by id, events
/// handed to a callback with the session they came from. The same small client the Outlook web session has
/// (<c>OwaTokenSession</c>'s <c>DevToolsClient</c>); a shared one could be lifted to Common later (both, and the spike, repeat it).
/// </summary>
internal sealed class TeamsCdp : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _receive = Task.CompletedTask;
    private int _nextId;

    /// <summary>An event: its method, its parameters, the session it came from (null for the browser's own).</summary>
    public event Action<string, JsonNode, string?>? Event;

    public static async Task<TeamsCdp> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var client = new TeamsCdp();
        try
        {
            await client._socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        client._receive = client.ReceiveAsync();
        return client;
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var root = JsonNode.Parse(message.ToArray())!;
                message.SetLength(0);
                if (root["id"] is { } id)
                {
                    if (_pending.TryRemove(id.GetValue<int>(), out var tcs))
                    {
                        if (root["error"] is { } error)
                        {
                            tcs.TrySetException(new IOException("DevTools error: " + error.ToJsonString()));
                        }
                        else
                        {
                            tcs.TrySetResult(root["result"] ?? new JsonObject());
                        }
                    }
                }
                else if (root["method"] is { } m)
                {
                    try
                    {
                        Event?.Invoke(m.GetValue<string>(), root["params"] ?? new JsonObject(), root["sessionId"]?.GetValue<string>());
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _ = ex;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or JsonException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        try
        {
            await _receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }

        _socket.Dispose();
    }
}
