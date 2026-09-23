using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteRelay.Common;

namespace RemoteRelay.Configurator.Services;

public class ConfiguratorClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private TaskCompletionSource<AppSettings?>? _settingsTcs;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public Uri? ServerUri { get; private set; }

    public event Action<bool>? ConnectionStateChanged;
    public event Action<AppSettings>? ConfigurationReceived;
    public event Action<Dictionary<string, string>>? SystemStateReceived;

    public async Task<bool> ConnectAsync(string host, int port)
    {
        await DisconnectAsync();

        var uriBuilder = new UriBuilder("http", host, port, "relay");
        ServerUri = uriBuilder.Uri;

        _settingsTcs = new TaskCompletionSource<AppSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _connection = new HubConnectionBuilder()
            .WithUrl(ServerUri)
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        _connection.Closed += e =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _connection.Reconnecting += e =>
        {
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _connection.Reconnected += e =>
        {
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        _connection.On<Dictionary<string, string>>("SystemState", state =>
        {
            SystemStateReceived?.Invoke(state);
        });

        _connection.On<AppSettings>("Configuration", settings =>
        {
            _settingsTcs?.TrySetResult(settings);
            ConfigurationReceived?.Invoke(settings);
        });

        try
        {
            await _connection.StartAsync();
            ConnectionStateChanged?.Invoke(true);
            return true;
        }
        catch
        {
            ConnectionStateChanged?.Invoke(false);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            try
            {
                await _connection.StopAsync();
                await _connection.DisposeAsync();
            }
            catch
            {
            }
            finally
            {
                _connection = null;
                ConnectionStateChanged?.Invoke(false);
            }
        }
    }

    public async Task<bool> IsPinRequiredAsync()
    {
        if (!IsConnected || _connection == null) return false;
        try
        {
            return await _connection.InvokeAsync<bool>("IsPinRequired");
        }
        catch
        {
            return false;
        }
    }

    public async Task<AppSettings?> GetSettingsAsync(TimeSpan? timeout = null)
    {
        if (!IsConnected || _connection == null) return null;

        _settingsTcs = new TaskCompletionSource<AppSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.InvokeAsync("RequestSettings");

        var completed = await Task.WhenAny(_settingsTcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        return completed == _settingsTcs.Task ? await _settingsTcs.Task : null;
    }

    public async Task<SaveConfigurationResponse> SaveConfigurationAsync(AppSettings settings, string? pin)
    {
        if (!IsConnected || _connection == null)
        {
            return new SaveConfigurationResponse { Success = false, Error = "Not connected to server" };
        }

        try
        {
            return await _connection.InvokeAsync<SaveConfigurationResponse>("SaveConfiguration", settings, pin);
        }
        catch (Exception ex)
        {
            return new SaveConfigurationResponse { Success = false, Error = ex.Message };
        }
    }

    public async Task SwitchSourceAsync(string sourceName, string outputName)
    {
        if (IsConnected && _connection != null)
        {
            await _connection.InvokeAsync("SwitchSource", sourceName, outputName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
