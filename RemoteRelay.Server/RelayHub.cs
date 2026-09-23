using Microsoft.AspNetCore.SignalR;
using RemoteRelay.Common;
using RemoteRelay.Server.Services;

namespace RemoteRelay.Server;

public class RelayHub : Hub
{
    private readonly SwitcherState _switcherState;
    private readonly ConfigurationService _configurationService;

    public RelayHub(SwitcherState switcherState, ConfigurationService configurationService)
    {
        _switcherState = switcherState;
        _configurationService = configurationService;
    }

    public override async Task OnConnectedAsync()
    {
        // Send current system state to newly connected client
        var state = _switcherState.GetSystemState();
        await Clients.Caller.SendAsync("SystemState", state);
        await base.OnConnectedAsync();
    }

    public async Task SwitchSource(string sourceName, string outputName)
    {
        if (sourceName != null)
        {
            _switcherState.SwitchSource(sourceName, outputName);
            //Sends the SystemState nessage to all clients
            await GetSystemState();
        }
    }

    public async Task GetSystemState()
    {
        var state = _switcherState.GetSystemState();
        await Clients.All.SendAsync("SystemState", state);
    }

    public async Task ClearSource(string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            _switcherState.ClearSource(sourceName);
            await GetSystemState();
        }
    }

    public async Task GetConfiguration()
    {
        await Clients.Caller.SendAsync("Configuration", _switcherState.GetSettings());
    }

    /// <summary>
    /// Tests an individual GPIO pin by setting it to the specified state.
    /// Used during setup to verify pin assignments.
    /// </summary>
    public void TestPin(int pin, bool activeLow, bool active)
    {
        _switcherState.TestPin(pin, activeLow, active);
    }

    public async Task<string?> TestPhysicalButton(string sourceName)
    {
        var error = _switcherState.TestPhysicalButton(sourceName);
        if (error == null)
        {
            await GetSystemState();
        }

        return error;
    }

    /// <summary>
    /// Checks whether the server configuration is protected by a PIN.
    /// </summary>
    public bool IsPinRequired()
    {
        var current = _switcherState.GetSettings();
        return !string.IsNullOrWhiteSpace(current.ConfigPin);
    }

    /// <summary>
    /// Overload for clients that do not pass a PIN.
    /// </summary>
    public Task<SaveConfigurationResponse> SaveConfiguration(AppSettings settings)
    {
        return SaveConfiguration(settings, null);
    }

    /// <summary>
    /// Saves the provided configuration to the server's config.json file.
    /// If the server has a ConfigPin configured, providedPin must match it.
    /// </summary>
    /// <returns>A response indicating success or failure with error message.</returns>
    public async Task<SaveConfigurationResponse> SaveConfiguration(AppSettings settings, string? providedPin)
    {
        var current = _switcherState.GetSettings();
        if (!string.IsNullOrWhiteSpace(current.ConfigPin))
        {
            if (string.IsNullOrWhiteSpace(providedPin) || !string.Equals(current.ConfigPin, providedPin))
            {
                return new SaveConfigurationResponse
                {
                    Success = false,
                    Error = "Invalid or missing configuration PIN."
                };
            }
        }

        var (success, error) = await _configurationService.SaveAsync(settings);
        if (success)
        {
            // Apply to the running server's in-memory state and push to every connected client.
            // Without this the ConfigurationWatcher will eventually catch up, but the caller's immediate
            // RequestSettings() can race against the file watcher and pull back the previous settings.
            await _switcherState.ApplySettingsAsync(settings);
            if (Clients != null)
            {
                await Clients.All.SendAsync("Configuration", _switcherState.GetSettings());
            }
        }

        return new SaveConfigurationResponse
        {
            Success = success,
            Error = error
        };
    }

    public async Task<HandshakeResponse> Handshake(string clientVersion)
    {
        var serverVersionStr = VersionHelper.GetVersion();
        var response = new HandshakeResponse
        {
            ServerVersion = serverVersionStr,
            Status = CompatibilityStatus.Compatible
        };

        if (System.Version.TryParse(clientVersion, out var cVer) &&
            System.Version.TryParse(serverVersionStr, out var sVer))
        {
            if (cVer < sVer)
            {
                response.Status = CompatibilityStatus.ClientOutdated;
                response.Message = "Client is outdated. Please update.";
            }
            else if (cVer > sVer)
            {
                response.Status = CompatibilityStatus.ServerOutdated; // Optional handling
            }
        }
        else
        {
            // Fallback if parsing fails - assume compatible or warn? 
            // For now, let's assume compatible if we can't curb version, or maybe warning.
            // Actually, let's leave it as Compatible but maybe log it?
        }

        return response;
    }
}