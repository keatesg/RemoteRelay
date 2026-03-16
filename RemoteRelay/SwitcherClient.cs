using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks; // Added for TaskCompletionSource
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteRelay.Common;

namespace RemoteRelay;

public class SwitcherClient
{
    private static SwitcherClient? _instance;
    private static readonly object _lock = new();
    private readonly object _requestLock = new();
    private readonly HubConnection _connection;
    private AppSettings? _settings;
    private Uri _hubUri;
    private TaskCompletionSource<AppSettings?> _settingsTcs;
    private readonly ReplaySubject<AppSettings> _settingsChanged = new(1);

    public Subject<Dictionary<string, string>> _stateChanged = new();
    public Subject<bool> _connectionStateChanged = new();
    private readonly BehaviorSubject<CompatibilityStatus> _compatibilityChanged = new(CompatibilityStatus.Compatible);
    public IObservable<CompatibilityStatus> CompatibilityUpdates => _compatibilityChanged.AsObservable();

    private SwitcherClient(Uri hubUri)
    {
        _hubUri = hubUri;
        _settingsTcs = new TaskCompletionSource<AppSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection = new HubConnectionBuilder()
           .WithAutomaticReconnect(new InfiniteRetryPolicy())
           .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
           .WithUrl(hubUri)
           .AddJsonProtocol(options =>
           {
               options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
           })
           .Build();

        // Handle connection state changes
        _connection.Closed += OnConnectionClosed;
        _connection.Reconnecting += OnConnectionReconnecting;
        _connection.Reconnected += OnConnectionReconnected;

        _connection.On<Dictionary<string, string>>("SystemState", state => { _stateChanged.OnNext(state); });

        _connection.On<AppSettings>("Configuration", settings =>
        {
            _settings = settings;
            _settingsTcs.TrySetResult(settings); // Complete the TCS with the received settings
            _settingsChanged.OnNext(settings);
        });
    }

    public Uri ServerUri => _hubUri;
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public static SwitcherClient Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("Instance not initialized. Call InitializeInstance first.");
            return _instance;
        }
    }

    public AppSettings? Settings
    {
        get => _settings;
    }

    public IObservable<AppSettings> SettingsUpdates => _settingsChanged.AsObservable();

    public static void InitializeInstance(Uri hubUri)
    {
        lock (_lock)
        {
            if (_instance == null)
                _instance = new SwitcherClient(hubUri);
        }
    }

    public static async Task ResetInstanceAsync()
    {
        if (_instance != null)
        {
            await _instance._connection.DisposeAsync();
            lock (_lock)
            {
                _instance = null;
            }
        }
    }

    public async System.Threading.Tasks.Task<bool> ConnectAsync()
    {
        if (IsConnected)
            return true;

        if (_connection.State != HubConnectionState.Disconnected)
            return false;

        try
        {
            await _connection.StartAsync();

            // Small delay to ensure connection is fully established
            await Task.Delay(100);

            await PerformHandshake();

            return true;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SwitchSource(string source, string output)
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot switch source: connection is not active");
            return;
        }

        try
        {
            _connection.SendAsync("SwitchSource", source, output);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending SwitchSource command: {ex.Message}");
        }
    }

    public void ClearSource(string sourceName)
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot clear source: connection is not active");
            return;
        }

        try
        {
            _connection.SendAsync("ClearSource", sourceName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending ClearSource command: {ex.Message}");
        }
    }

    public void RequestStatus()
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot request status: connection is not active");
            return;
        }

        try
        {
            _connection.SendAsync("GetSystemState");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending GetSystemState command: {ex.Message}");
        }
    }

    public void RequestSettings()
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot request settings: connection is not active");
            return;
        }

        try
        {
            // Reset TCS for cases where settings might be requested again
            lock (_requestLock)
            {
                if (_settingsTcs.Task.IsCompleted)
                {
                    _settingsTcs = new TaskCompletionSource<AppSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            // _settings = null; // Removed to prevent null reference exceptions in UI binding
            _connection.SendAsync("GetConfiguration");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending GetConfiguration command: {ex.Message}");
            _settingsTcs.TrySetException(ex);
        }
    }

    public async Task<AppSettings?> GetSettingsAsync(int timeoutMs = 15000)
    {
        Task<AppSettings?> task;
        lock (_requestLock)
        {
            task = _settingsTcs.Task;
        }

        if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
        {
            return await task;
        }

        System.Diagnostics.Debug.WriteLine($"GetSettingsAsync timed out after {timeoutMs}ms");
        return null;
    }

    /// <summary>
    /// Tests an individual GPIO pin by setting it to the specified state.
    /// </summary>
    public async Task TestPinAsync(int pin, bool activeLow, bool active)
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot test pin: connection is not active");
            return;
        }

        try
        {
            await _connection.SendAsync("TestPin", pin, activeLow, active);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending TestPin command: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the provided configuration to the server.
    /// </summary>
    public async Task<SaveConfigurationResponse?> SaveConfigurationAsync(AppSettings settings)
    {
        if (!IsConnected)
        {
            System.Diagnostics.Debug.WriteLine("Cannot save configuration: connection is not active");
            return new SaveConfigurationResponse { Success = false, Error = "Not connected to server" };
        }

        try
        {
            return await _connection.InvokeAsync<SaveConfigurationResponse>("SaveConfiguration", settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending SaveConfiguration command: {ex.Message}");
            return new SaveConfigurationResponse { Success = false, Error = ex.Message };
        }
    }

    private Task OnConnectionClosed(Exception? exception)
    {
        System.Diagnostics.Debug.WriteLine($"Connection closed. Exception: {exception?.Message}");
        _connectionStateChanged.OnNext(false);
        return Task.CompletedTask;
    }

    private Task OnConnectionReconnecting(Exception? exception)
    {
        System.Diagnostics.Debug.WriteLine($"Connection reconnecting. Exception: {exception?.Message}");
        _connectionStateChanged.OnNext(false);
        return Task.CompletedTask;
    }

    private async Task OnConnectionReconnected(string? connectionId)
    {
        System.Diagnostics.Debug.WriteLine($"Connection reconnected. Connection ID: {connectionId}");
        _connectionStateChanged.OnNext(true);

        // Re-request settings and status after reconnection
        try
        {
            await PerformHandshake();
            RequestSettings();
            RequestStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error re-requesting data after reconnection: {ex.Message}");
        }
    }

    private async Task PerformHandshake()
    {
        try
        {
            var response = await _connection.InvokeAsync<HandshakeResponse>("Handshake", VersionHelper.GetVersion());
            _compatibilityChanged.OnNext(response.Status);
        }
        catch (Exception ex)
        {
            // If we fail to call Handshake, it likely means the server is old and doesn't have the endpoint
            System.Diagnostics.Debug.WriteLine($"Handshake failed: {ex.Message}");
            _compatibilityChanged.OnNext(CompatibilityStatus.ServerOutdated);
        }
    }

    private class InfiniteRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.PreviousRetryCount < 4)
                return TimeSpan.FromSeconds(Math.Pow(2, retryContext.PreviousRetryCount));
            
            return TimeSpan.FromSeconds(15);
        }
    }
}