using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using GarageGames.Core.Domain;
using GarageGames.Core.Protocol;

namespace GarageGames.Controller.Services;

public sealed class BridgeConnectionManager(ILogger<BridgeConnectionManager> logger)
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new(StringComparer.OrdinalIgnoreCase);

    public bool Connected => _connections.Values.Any(socket => socket.State == WebSocketState.Open);
    public IReadOnlyList<string> ConnectedMasters =>
        _connections.Where(item => item.Value.State == WebSocketState.Open).Select(item => item.Key).ToArray();

    public void Register(string deviceId, WebSocket socket)
    {
        _connections.AddOrUpdate(deviceId, socket, (_, old) =>
        {
            try
            {
                old.Abort();
                old.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            return socket;
        });
        logger.LogInformation("Master {DeviceId} connected.", deviceId);
    }

    public void Remove(string deviceId, WebSocket socket)
    {
        if (_connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current, socket))
        {
            _connections.TryRemove(deviceId, out _);
        }
        logger.LogInformation("Master {DeviceId} disconnected.", deviceId);
    }

    public async Task BroadcastAsync(
        ControllerCommand command,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(command));
        foreach (var item in _connections.ToArray())
        {
            if (item.Value.State != WebSocketState.Open)
            {
                _connections.TryRemove(item.Key, out _);
                continue;
            }

            try
            {
                await item.Value.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException exception)
            {
                logger.LogWarning(exception, "Failed to send command to master {DeviceId}.", item.Key);
                _connections.TryRemove(item.Key, out _);
            }
        }
    }
}

