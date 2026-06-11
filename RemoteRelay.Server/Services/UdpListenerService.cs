using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Services;

/// <summary>
/// Background service that listens for UDP switching commands from external applications.
/// Expects messages in format: SWITCH &lt;input&gt; &lt;output&gt;
/// Input/output can be either numeric (1-based index) or names.
/// </summary>
public class UdpListenerService : BackgroundService
{
    private static readonly TimeSpan BindRetryDelay = TimeSpan.FromSeconds(5);

    private readonly SwitcherState _switcherState;
    private readonly IHubContext<RelayHub> _hubContext;
    private readonly ILogger<UdpListenerService> _logger;
    private readonly int _port;

    public UdpListenerService(
        SwitcherState switcherState,
        IHubContext<RelayHub> hubContext,
        ILogger<UdpListenerService> logger,
        int port)
    {
        _switcherState = switcherState;
        _hubContext = hubContext;
        _logger = logger;
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UDP listener starting on port {Port}", _port);

        // Outer loop: (re)bind the socket. The port can be transiently unavailable at boot,
        // and a fatal socket error mid-run should recreate the listener rather than kill it.
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpClient udpClient;
            try
            {
                udpClient = new UdpClient(_port);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Failed to bind UDP listener on port {Port}. Retrying in {Delay}s.", _port, BindRetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(BindRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            _logger.LogInformation("UDP listener ready on port {Port}", _port);

            using (udpClient)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(stoppingToken);
                        var message = Encoding.UTF8.GetString(result.Buffer).Trim();

                        _logger.LogInformation("UDP received from {RemoteEndPoint}: {Message}",
                            result.RemoteEndPoint, message);

                        await ProcessMessageAsync(message);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Normal shutdown
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogError(ex, "UDP socket error on port {Port}; recreating listener.", _port);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error receiving UDP message");
                    }
                }
            }
        }

        _logger.LogInformation("UDP listener stopped");
    }

    private async Task ProcessMessageAsync(string message)
    {
        // Expected format: SWITCH <input> <output>
        // Examples: "SWITCH 1 2" or "SWITCH Input 1 Output 2"
        
        if (!message.StartsWith("SWITCH ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid UDP command format. Expected: SWITCH <input> <output>");
            return;
        }

        var settings = _switcherState.GetSettings();
        var sources = settings.Sources.ToList();
        var outputs = settings.Outputs.ToList();

        // Remove "SWITCH " prefix and parse remaining parts
        var parts = message.Substring(7).Trim();
        
        // Try to parse as two numbers first
        var tokens = parts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        string? inputName = null;
        string? outputName = null;

        if (tokens.Length == 2 && int.TryParse(tokens[0], out int inputIndex) && int.TryParse(tokens[1], out int outputIndex))
        {
            // Numeric indices (1-based)
            if (inputIndex >= 1 && inputIndex <= sources.Count)
            {
                inputName = sources[inputIndex - 1];
            }
            if (outputIndex >= 1 && outputIndex <= outputs.Count)
            {
                outputName = outputs[outputIndex - 1];
            }
        }
        else
        {
            // Try to find matching source and output names
            // This is trickier with spaces, so we try to match known names
            inputName = FindMatchingName(parts, sources);
            if (inputName != null)
            {
                var remaining = parts.Substring(parts.IndexOf(inputName, StringComparison.OrdinalIgnoreCase) + inputName.Length).Trim();
                outputName = FindMatchingName(remaining, outputs);
            }
        }

        if (string.IsNullOrEmpty(inputName))
        {
            _logger.LogWarning("UDP command: could not parse input from '{Message}'", message);
            return;
        }

        if (string.IsNullOrEmpty(outputName))
        {
            _logger.LogWarning("UDP command: could not parse output from '{Message}'", message);
            return;
        }

        // Verify route exists
        var routeExists = settings.Routes.Any(r => 
            string.Equals(r.SourceName, inputName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.OutputName, outputName, StringComparison.OrdinalIgnoreCase));

        if (!routeExists)
        {
            _logger.LogWarning("UDP command: route '{Input}' -> '{Output}' does not exist", inputName, outputName);
            return;
        }

        // Execute the switch
        _switcherState.SwitchSource(inputName, outputName);
        _logger.LogInformation("UDP switch executed: {Input} -> {Output}", inputName, outputName);

        // Broadcast state update to all SignalR clients
        var state = _switcherState.GetSystemState();
        await _hubContext.Clients.All.SendAsync("SystemState", state);
    }

    private static string? FindMatchingName(string text, IList<string> candidates)
    {
        // Longest names first, otherwise "Output 1" would shadow "Output 10".
        var ordered = candidates.OrderByDescending(c => c.Length).ToList();

        // Try prefix match first
        foreach (var candidate in ordered)
        {
            if (text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // Try case-insensitive contains as fallback
        foreach (var candidate in ordered)
        {
            if (text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
