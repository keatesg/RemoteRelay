using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;
using Makaretu.Dns;

namespace RemoteRelay.Server.Services;

/// <summary>
/// Background service that broadcasts mDNS presence.
/// Advertises _remoterelay._tcp.local.
/// </summary>
public class MdnsBeaconService : BackgroundService
{
    private readonly SwitcherState _switcherState;
    private readonly ILogger<MdnsBeaconService> _logger;
    private ServiceDiscovery? _serviceDiscovery;

    public MdnsBeaconService(
        SwitcherState switcherState,
        ILogger<MdnsBeaconService> logger)
    {
        _switcherState = switcherState;
        _logger = logger;
    }

    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _switcherState.GetSettings();
        var port = settings.ServerPort;

        _logger.LogInformation("Starting mDNS beacon for _remoterelay._tcp.local. on port {Port}", port);

        // Retry until the advertisement is up: at boot the service can start before
        // the network is available, and giving up permanently breaks auto-discovery.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _serviceDiscovery = new ServiceDiscovery();
                var profile = new ServiceProfile("RemoteRelay", "_remoterelay._tcp", (ushort)port);
                _serviceDiscovery.Advertise(profile);

                _logger.LogInformation("mDNS advertisement active.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start mDNS beacon. Retrying in {Delay}s.", StartRetryDelay.TotalSeconds);
                _serviceDiscovery?.Dispose();
                _serviceDiscovery = null;

                try
                {
                    await Task.Delay(StartRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        try
        {
            // Hold the advertisement until shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _serviceDiscovery?.Dispose();
            _serviceDiscovery = null;
        }
    }
}
