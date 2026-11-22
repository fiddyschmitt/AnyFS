using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Common;

/// <summary>
/// Enough of the Chrome DevTools protocol for the web sessions: commands over one WebSocket, answers matched by id, to
/// the browser or to one of its targets' sessions, and events handed to a callback with the session they came from (a
/// session that watches what a page sends listens to them; one that polls ignores them). When the connection ends, every
/// command still waiting fails at once rather than at its timeout, and <see cref="Closed"/> tells a session that is
/// sending none. The one client the web sessions share
/// (<see cref="DevToolsBrowser"/>): SharePoint's and 3DX's <see cref="BrowserSession"/>, Outlook's and Teams'.
/// </summary>
public sealed class DevToolsClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receive;
    private int _nextId;

    /// <summary>A client over a connection that is already open: the browser's, or in the tests one that stands in for it.</summary>
    internal DevToolsClient(WebSocket socket)
    {
        _socket = socket;
        _receive = ReceiveAsync();
    }

    /// <summary>An event: its method, its parameters, the session it came from (null for the browser's own).</summary>
    public event Action<string, JsonNode, string?>? Event;

    /// <summary>
    /// Ends when the connection has, whichever end closed it (the browser quit, or this was disposed); it never fails. A
    /// session that only listens while it waits sends nothing that would meet the closed connection, so it waits on this.
    /// </summary>
    public Task Closed => _closed.Task;

    public static async Task<DevToolsClient> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new DevToolsClient(socket);
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        if (Closed.IsCompleted)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("the DevTools connection closed");
        }

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

        return await answer.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
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

                JsonNode? root;
                try
                {
                    root = JsonNode.Parse(message.ToArray());
                }
                catch (JsonException)
                {
                    root = null;
                }

                message.SetLength(0);
                if (root is null)
                {
                    continue;
                }

                if (root["id"] is { } id)
                {
                    if (_pending.TryRemove(id.GetValue<int>(), out var answer))
                    {
                        if (root["error"] is { } error)
                        {
                            answer.TrySetException(new IOException($"DevTools error: {error.ToJsonString()}"));
                        }
                        else
                        {
                            answer.TrySetResult(root["result"] ?? new JsonObject());
                        }
                    }
                }
                else if (root["method"] is { } m)
                {
                    try
                    {
                        Event?.Invoke(m.GetValue<string>(), root["params"] ?? new JsonObject(), root["sessionId"]?.GetValue<string>());
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // a handler's slip must not take the connection down
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            // said before the commands waiting are failed: one sent from here on finds it said, or is among them
            _closed.TrySetResult();
            foreach (var answer in _pending.Values)
            {
                answer.TrySetException(new IOException("the DevTools connection closed"));
            }
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
        _sendGate.Dispose();
        _stop.Dispose();
    }
}
