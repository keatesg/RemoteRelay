using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using RemoteRelay.Common;
using RemoteRelay.Connection;
using RemoteRelay.MultiOutput;
using RemoteRelay.SingleOutput;
using Zeroconf;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace RemoteRelay;

public class MainWindowViewModel : ViewModelBase
{
    private const int RetryIntervalSeconds = 5;
    private readonly object _retryLock = new();
    private System.Timers.Timer? _retryTimer;
    private int _retryCountdown;
    private ClientConfig _clientConfig = new();
    // On Windows this resolves to %ProgramData%\RemoteRelay\Client\ClientConfig.json; on
    // Linux it stays in the install directory (see AppPaths).
    private static readonly string ConfigFileName = AppPaths.ClientConfigPath;
    private AppSettings? _currentSettings;
    private IDisposable? _clientSubscriptions;

    private string _serverStatusMessage = string.Empty;
    public string ServerStatusMessage
    {
        get => _serverStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _serverStatusMessage, value);
    }

    private string _filterStatusMessage = string.Empty;
    public string FilterStatusMessage
    {
        get => _filterStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _filterStatusMessage, value);
    }

    private string _updateMessage = string.Empty;
    public string UpdateMessage
    {
        get => _updateMessage;
        set => this.RaiseAndSetIfChanged(ref _updateMessage, value);
    }

    private ViewModelBase? _operationViewModel;
    public ViewModelBase? OperationViewModel
    {
        get => _operationViewModel;
        set
        {
            if (ReferenceEquals(_operationViewModel, value))
            {
                return;
            }

            if (_operationViewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }

            this.RaiseAndSetIfChanged(ref _operationViewModel, value);
            this.RaisePropertyChanged(nameof(IsOperationViewReady));
        }
    }

    public bool IsOperationViewReady => _operationViewModel != null;

    public bool IsFullscreen => _clientConfig.IsFullscreen ?? true;

    public void SaveFullscreenState(bool isFullscreen)
    {
        _clientConfig.IsFullscreen = isFullscreen;
        SaveConfig();
    }

    private bool _showIpOnScreen = true;
    public bool ShowIpOnScreen
    {
        get => _showIpOnScreen;
        set => this.RaiseAndSetIfChanged(ref _showIpOnScreen, value);
    }

    public ICommand OpenConnectionSettingsCommand { get; }

    // Set once the connection settings view has been offered this session, so a
    // failing first-run connection doesn't keep bouncing the dialog at the user.
    private bool _connectionPromptShown;

    public MainWindowViewModel()
    {
        Debug.WriteLine(Guid.NewGuid());

        LoadOrMigrateConfig();

        OpenConnectionSettingsCommand = ReactiveCommand.Create(OpenConnectionSettings);

        InitClient();

        _ = InitializeConnectionAsync();
    }

    private void InitClient(string? tempHost = null, int? tempPort = null)
    {
        var h = tempHost ?? (string.IsNullOrWhiteSpace(_clientConfig.Host) ? "localhost" : _clientConfig.Host);
        var p = tempPort ?? _clientConfig.Port ?? 33101;
        var serverUri = new Uri($"http://{h}:{p}/relay");
        SwitcherClient.InitializeInstance(serverUri);
        SetupClientSubscriptions();
    }

    private void SetupClientSubscriptions()
    {
        var disposables = new CompositeDisposable();

        disposables.Add(SwitcherClient.Instance.SettingsUpdates
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(settings =>
            {
                _currentSettings = settings;
                // Only apply if the user isn't editing connection settings
                if (OperationViewModel is not ConnectionSettingsViewModel)
                {
                    ApplySettings(settings);
                }
                ServerStatusMessage = $"Connected to {SwitcherClient.Instance.ServerUri} (settings updated {DateTime.Now:T})";
            }));

        disposables.Add(SwitcherClient.Instance.CompatibilityUpdates
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(status =>
           {
               switch (status)
               {
                   case CompatibilityStatus.ClientOutdated:
                       UpdateMessage = "Client Update Available - Please Run Update";
                       break;
                   case CompatibilityStatus.ServerOutdated:
                       UpdateMessage = "Server Version Unknown/Outdated";
                       break;
                   default:
                       UpdateMessage = string.Empty;
                       break;
               }
           }));

        // Subscribe to connection state changes. These fire on SignalR callback threads;
        // OperationViewModel changes dispose/create view models (incl. DispatcherTimers)
        // and update bindings, all of which must happen on the UI thread.
        disposables.Add(SwitcherClient.Instance._connectionStateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isConnected =>
        {
            if (!isConnected)
            {
                OperationViewModel = null;
                StartRetryTimer();
            }
            else
            {
                _ = OnConnected();
            }
        }));

        _clientSubscriptions = disposables;
    }

    private void DisposeClientSubscriptions()
    {
        _clientSubscriptions?.Dispose();
        _clientSubscriptions = null;
    }

    private void OpenConnectionSettings()
    {
        _connectionPromptShown = true;
        StopRetryTimer();
        OperationViewModel = new ConnectionSettingsViewModel(
            _clientConfig.Host,
            _clientConfig.Port,
            availableInputs: _currentSettings?.Sources,
            selectedInputs: _clientConfig.ShownInputs,
            availableOutputs: _currentSettings?.Outputs,
            selectedOutputs: _clientConfig.ShownOutputs,
            onSave: (host, port, inputs, outputs) => _ = ApplyConnectionSettingsAsync(host, port, inputs, outputs),
            onCancel: CancelConnectionSettings);
        ServerStatusMessage = "Editing client settings…";
    }

    private async Task ApplyConnectionSettingsAsync(string? host, int? port, List<string>? shownInputs, List<string>? shownOutputs)
    {
        var targetHost = host ?? string.Empty;
        bool hostChanged = !string.Equals(_clientConfig.Host, targetHost, StringComparison.OrdinalIgnoreCase) || _clientConfig.Port != port;

        _clientConfig.Host = targetHost;
        _clientConfig.Port = port;
        _clientConfig.ShownInputs = shownInputs;
        _clientConfig.ShownOutputs = shownOutputs;

        if (hostChanged)
        {
            _clientConfig.LastDiscoveredHost = null;
            _clientConfig.LastDiscoveredPort = null;
        }

        SaveConfig();
        OperationViewModel = null;

        if (hostChanged || !SwitcherClient.Instance.IsConnected)
        {
            DisposeClientSubscriptions();
            await SwitcherClient.ResetInstanceAsync();
            InitClient();
            await InitializeConnectionAsync();
        }
        else if (_currentSettings.HasValue)
        {
            ApplySettings(_currentSettings.Value);
        }
    }

    private void CancelConnectionSettings()
    {
        OperationViewModel = null;
        if (SwitcherClient.Instance.IsConnected)
        {
            _ = OnConnected();
        }
        else
        {
            StartRetryTimer();
        }
    }

    private void LoadOrMigrateConfig()
    {
        if (File.Exists(ConfigFileName))
        {
            try
            {
                var json = File.ReadAllText(ConfigFileName);
                _clientConfig = JsonSerializer.Deserialize<ClientConfig>(json) ?? new ClientConfig();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading config: {ex.Message}");
                _clientConfig = new ClientConfig();
            }
        }
        else if (File.Exists("ServerDetails.json"))
        {
            try
            {
                // Migrate from ServerDetails
                var serverInfo = JsonSerializer.Deserialize<ServerDetails>(File.ReadAllText("ServerDetails.json"));
                if (serverInfo != null)
                {
                    _clientConfig = new ClientConfig
                    {
                        Host = serverInfo.Host,
                        Port = serverInfo.Port
                    };
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error migrating config: {ex.Message}");
                _clientConfig = new ClientConfig();
            }
        }
        else
        {
            _clientConfig = new ClientConfig();
            SaveConfig();
        }
        
        ShowIpOnScreen = _clientConfig.ShowIpOnScreen ?? true;
    }

    private void SaveConfig()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_clientConfig, options);
            File.WriteAllText(ConfigFileName, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private async Task InitializeConnectionAsync()
    {
        var autoMode = string.IsNullOrWhiteSpace(_clientConfig.Host);
        var discovered = false;

        // Auto-discovery if configured for localhost or empty host
        if (autoMode || _clientConfig.IsLocalConnection)
        {
            ServerStatusMessage = "Scanning for RemoteRelay server...";
            try
            {
                // Quick scan (2 seconds)
                var results = await ZeroconfResolver.ResolveAsync("_remoterelay._tcp.local.", TimeSpan.FromSeconds(2));
                var firstResult = results.FirstOrDefault();

                if (firstResult != null)
                {
                    var service = firstResult.Services.Values.FirstOrDefault();
                    var ip = firstResult.IPAddresses.FirstOrDefault();

                    if (service != null && ip != null)
                    {
                        var newHost = ip.ToString();
                        var newPort = service.Port;
                        discovered = true;

                        // Cache the result so a later launch can still connect when
                        // mDNS is down. Host stays empty, so discovery keeps running
                        // first on every launch — the cache is only a fallback.
                        if (_clientConfig.LastDiscoveredHost != newHost || _clientConfig.LastDiscoveredPort != newPort)
                        {
                            _clientConfig.LastDiscoveredHost = newHost;
                            _clientConfig.LastDiscoveredPort = newPort;
                            SaveConfig();
                        }

                        // Only re-initialize if it differs from what we would use by default
                        var currentHost = string.IsNullOrWhiteSpace(_clientConfig.Host) ? "localhost" : _clientConfig.Host;
                        var currentPort = _clientConfig.Port ?? 33101;
                        if (!currentHost.Equals(newHost, StringComparison.OrdinalIgnoreCase) || currentPort != newPort)
                        {
                            ServerStatusMessage = $"Found server at {newHost}:{newPort}. Connecting...";

                            // Re-initialize client with discovered host and port
                            DisposeClientSubscriptions();
                            await SwitcherClient.ResetInstanceAsync();
                            InitClient(newHost, newPort);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Auto-discovery failed: {ex.Message}");
            }

            // Nothing discovered: fall back to the server discovery last found, so
            // a temporary mDNS outage doesn't strand an auto-configured client.
            if (!discovered && autoMode && !string.IsNullOrWhiteSpace(_clientConfig.LastDiscoveredHost))
            {
                var cachedHost = _clientConfig.LastDiscoveredHost!;
                var cachedPort = _clientConfig.LastDiscoveredPort ?? 33101;
                ServerStatusMessage = $"No server discovered. Trying last known server {cachedHost}:{cachedPort}...";
                DisposeClientSubscriptions();
                await SwitcherClient.ResetInstanceAsync();
                InitClient(cachedHost, cachedPort);
            }
        }

        if (await SwitcherClient.Instance.ConnectAsync())
        {
            await OnConnected();
        }
        else if (autoMode && !discovered && !_connectionPromptShown)
        {
            // Nothing configured, nothing discovered, and the connection failed —
            // ask the user rather than silently retrying localhost forever.
            Dispatcher.UIThread.Post(OpenConnectionSettings);
        }
        else
        {
            StartRetryTimer();
        }
    }

    private void UpdateServerStatusMessageForRetry()
    {
        var message = $"Server offline. Trying to connect to {SwitcherClient.Instance.ServerUri}. Retrying in {_retryCountdown}s...";
        Dispatcher.UIThread.Post(() => ServerStatusMessage = message);
    }

    private void StartRetryTimer()
    {
        lock (_retryLock)
        {
            if (_retryTimer != null)
            {
                // A retry cycle is already running; don't spawn a competing timer.
                return;
            }

            _retryCountdown = RetryIntervalSeconds;
            var timer = new System.Timers.Timer(1000); // 1 second interval
            timer.Elapsed += async (_, _) => await OnRetryTimerTickAsync(timer);
            _retryTimer = timer;
            timer.Start();
        }

        UpdateServerStatusMessageForRetry();
    }

    private void StopRetryTimer()
    {
        lock (_retryLock)
        {
            _retryTimer?.Stop();
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }

    private async Task OnRetryTimerTickAsync(System.Timers.Timer timer)
    {
        try
        {
            lock (_retryLock)
            {
                if (!ReferenceEquals(timer, _retryTimer))
                {
                    return; // stale timer that was already replaced/stopped
                }

                _retryCountdown--;
                if (_retryCountdown > 0)
                {
                    // Keep counting down; message updated below outside the lock.
                }
                else
                {
                    timer.Stop();
                    timer.Dispose();
                    _retryTimer = null;
                }
            }

            if (_retryCountdown > 0)
            {
                UpdateServerStatusMessageForRetry();
                return;
            }

            var message = $"Server offline. Trying to connect to {SwitcherClient.Instance.ServerUri}. Retrying now...";
            Dispatcher.UIThread.Post(() => ServerStatusMessage = message);

            bool connected = await SwitcherClient.Instance.ConnectAsync();
            if (connected)
            {
                await OnConnected();
            }
            else
            {
                StartRetryTimer();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in retry timer: {ex.Message}");
            StartRetryTimer();
        }
    }

    private async Task OnConnected()
    {
        // Settings application creates/disposes view models and DispatcherTimers,
        // but we can be called from timer/SignalR threads — hop to the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(OnConnected);
            return;
        }

        StopRetryTimer();

        if (OperationViewModel is ConnectionSettingsViewModel)
        {
            // The user is editing connection settings — don't yank the view away.
            // Cancel/Save will re-apply the current settings when they're done.
            ServerStatusMessage = $"Connected to {SwitcherClient.Instance.ServerUri}";
            return;
        }

        ServerStatusMessage = $"Connected to {SwitcherClient.Instance.ServerUri}. Fetching settings...";
        SwitcherClient.Instance.RequestSettings();
        var settings = await SwitcherClient.Instance.GetSettingsAsync();

        if (settings != null)
        {
            ApplySettings(settings.Value);
            ServerStatusMessage = $"Connected to {SwitcherClient.Instance.ServerUri}";

            SwitcherClient.Instance.RequestStatus();
        }
        else
        {
            ServerStatusMessage = "Failed to retrieve valid settings from server. Will retry...";
            await Task.Delay(2000);
            StartRetryTimer();
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        ShowIpOnScreen = _clientConfig.ShowIpOnScreen ?? settings.ShowIpOnScreen;

        // Handle unconfigured server
        if (!settings.IsConfigured)
        {
            OperationViewModel = null;
            ServerStatusMessage = "Server is not configured. Please use RemoteRelay Configurator to set up routes.";
            return;
        }

        // Reconcile filters against current server state
        bool configUpdated = false;

        if (_clientConfig.ShownInputs == null)
        {
            _clientConfig.ShownInputs = settings.Sources.ToList();
            configUpdated = true;
        }
        else
        {
            var serverSources = new HashSet<string>(settings.Sources, StringComparer.OrdinalIgnoreCase);
            // Remove stale entries that no longer exist on server
            int removed = _clientConfig.ShownInputs.RemoveAll(s => !serverSources.Contains(s));
            // Add new server entries not yet in filter
            foreach (var source in settings.Sources)
            {
                if (!_clientConfig.ShownInputs.Contains(source, StringComparer.OrdinalIgnoreCase))
                    _clientConfig.ShownInputs.Add(source);
            }
            if (removed > 0 || _clientConfig.ShownInputs.Count != settings.Sources.Count)
                configUpdated = true;
        }

        if (_clientConfig.ShownOutputs == null)
        {
            _clientConfig.ShownOutputs = settings.Outputs.ToList();
            configUpdated = true;
        }
        else
        {
            var serverOutputs = new HashSet<string>(settings.Outputs, StringComparer.OrdinalIgnoreCase);
            // Remove stale entries that no longer exist on server
            int removed = _clientConfig.ShownOutputs.RemoveAll(s => !serverOutputs.Contains(s));
            // Add new server entries not yet in filter
            foreach (var output in settings.Outputs)
            {
                if (!_clientConfig.ShownOutputs.Contains(output, StringComparer.OrdinalIgnoreCase))
                    _clientConfig.ShownOutputs.Add(output);
            }
            if (removed > 0 || _clientConfig.ShownOutputs.Count != settings.Outputs.Count)
                configUpdated = true;
        }

        if (configUpdated)
        {
            SaveConfig();
        }

        // Filter routes
        var filteredRoutes = settings.Routes.Where(r =>
           (_clientConfig.ShownInputs?.Contains(r.SourceName) ?? true) &&
           (_clientConfig.ShownOutputs?.Contains(r.OutputName) ?? true)
        ).ToList();

        var filteredSettings = settings;
        filteredSettings.Routes = filteredRoutes;

        // Determine filter status
        bool isFiltered =
           (_clientConfig.ShownInputs != null && _clientConfig.ShownInputs.Count < settings.Sources.Count) ||
           (_clientConfig.ShownOutputs != null && _clientConfig.ShownOutputs.Count < settings.Outputs.Count);

        FilterStatusMessage = isFiltered ? "Filtered" : "All";

        OperationViewModel = filteredSettings.Outputs.Count > 1
           ? new MultiOutputViewModel(filteredSettings, _clientConfig.ShowIpOnScreen, FilterStatusMessage)
           : new SingleOutputViewModel(filteredSettings, _clientConfig.ShowIpOnScreen);
    }

    private class ServerDetails
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 33101;
    }
}