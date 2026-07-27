using System.Net;
using System.Net.Sockets;
using System.Text;
using GarageGames.Core.Protocol;

namespace GarageGames.Controller.Services;

public sealed class DiscoveryHostedService(
    IConfiguration configuration,
    ILogger<DiscoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = configuration.GetValue("Controller:DiscoveryPort", Protocol.DefaultDiscoveryPort);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        client.EnableBroadcast = true;
        logger.LogInformation("Discovery service listening on UDP {Port}.", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await client.ReceiveAsync(stoppingToken);
                var request = Encoding.UTF8.GetString(received.Buffer);
                if (!string.Equals(request.Trim(), "GG_DISCOVER_V1", StringComparison.Ordinal))
                {
                    continue;
                }

                var controllerPort = configuration.GetValue("Controller:Port", Protocol.DefaultControllerPort);
                var response = Encoding.UTF8.GetBytes(
                    $"GG_CONTROLLER_V1|{Environment.MachineName}|{controllerPort}");
                await client.SendAsync(response, received.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Controller discovery request failed.");
            }
        }
    }
}

