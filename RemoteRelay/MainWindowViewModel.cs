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
using RemoteRelay.MultiOutput;
using RemoteRelay.Setup;
using RemoteRelay.SingleOutput;
using Zeroconf;
using System.Reactive.Disposables;

namespace RemoteRelay;

public class MainWindowViewModel : ViewModelBase
{
    private const int RetryIntervalSeconds = 5;
    private System.Timers.Timer? _retryTimer;
    private int _retryCountdown;
    private ClientConfig _clientConfig = new();
    private const string ConfigFileName = "ClientConfig.json";
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

    private bool _showIpOnScreen = true;
    public bool ShowIpOnScreen
    {
        get => _showIpOnScreen;
        set => this.RaiseAndSetIfChanged(ref _showIpOnScreen, value);
    }

    private bool _showSetupButton;
    public bool ShowSetupButton
    {
        get => _showSetupButton;
        set => this.RaiseAndSetIfChanged(ref _showSetupButton, value);
    }

    public ICommand OpenSetupCommand { get; }

    public MainWindowViewModel()
    {
        Debug.WriteLine(Guid.NewGuid());

        LoadOrMigrateConfig();

        // Setup button is only shown when connected to localhost
        ShowSetupButton = _clientConfig.IsLocalConnection;

        OpenSetupCommand = ReactiveCommand.Create(OpenSetup);

        OpenSetupCommand = ReactiveCommand.Create(OpenSetup);

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
                // Only apply if we're not in setup mode
                if (OperationViewModel is not SetupViewModel)
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

        // Subscribe to connection state changes
        disposables.Add(SwitcherClient.Instance._connectionStateChanged.Subscribe(isConnected =>
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

    private void OpenSetup()
    {
        if (_currentSettings.HasValue)
        {
            OperationViewModel = new SetupViewModel(_currentSettings.Value, CloseSetup);
        }
    }

    private void CloseSetup()
    {
        if (_currentSettings.HasValue)
        {
            ApplySettings(_currentSettings.Value);
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
        // Auto-discovery if configured for localhost or empty host
        if (string.IsNullOrWhiteSpace(_clientConfig.Host) || _clientConfig.IsLocalConnection)
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

                        // Only re-initialize if it differs from what we would use by default
                        var currentHost = string.IsNullOrWhiteSpace(_clientConfig.Host) ? "localhost" : _clientConfig.Host;
                        var currentPort = _clientConfig.Port ?? 33101;
                        if (!currentHost.Equals(newHost, StringComparison.OrdinalIgnoreCase) || currentPort != newPort)
                        {
                            ServerStatusMessage = $"Found server at {newHost}:{newPort}. Connecting...";

                            // Do NOT save the discovered Host and Port to _clientConfig so it discovers again next time
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
        }

        if (await SwitcherClient.Instance.ConnectAsync())
        {
            await OnConnected();
        }
        else
        {
            StartRetryTimer();
        }
    }

    private void UpdateServerStatusMessageForRetry()
    {
        ServerStatusMessage = $"Server offline. Trying to connect to {SwitcherClient.Instance.ServerUri}. Retrying in {_retryCountdown}s...";
    }

    private void StartRetryTimer()
    {
        _retryTimer?.Stop();
        _retryTimer?.Dispose();

        _retryCountdown = RetryIntervalSeconds;
        UpdateServerStatusMessageForRetry();

        _retryTimer = new System.Timers.Timer(1000); // 1 second interval
        _retryTimer.Elapsed += async (sender, e) =>
        {
            try
            {
                if (_retryCountdown > 0)
                {
                    _retryCountdown--;
                    UpdateServerStatusMessageForRetry();
                }

                if (_retryCountdown <= 0)
                {
                    _retryTimer?.Stop();
                    _retryTimer?.Dispose();
                    _retryTimer = null;

                    ServerStatusMessage = $"Server offline. Trying to connect to {SwitcherClient.Instance.ServerUri}. Retrying now...";

                    bool connected = await SwitcherClient.Instance.ConnectAsync();
                    if (connected)
                    {
                        await OnConnected();
                    }
                    else
                    {
                        Task.Run(() => StartRetryTimer());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in retry timer: {ex.Message}");
                // Restart retry timer on error using scheduler to avoid deeply stacked tasks
                Task.Run(() => StartRetryTimer());
            }
        };
        _retryTimer.Start();
    }

    private async Task OnConnected()
    {
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _retryTimer = null;

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
            if (_clientConfig.IsLocalConnection)
            {
                _currentSettings = settings;
                OperationViewModel = new SetupViewModel(settings, CloseSetup);
                ServerStatusMessage = "Server not configured. Please set up routes below.";
            }
            else
            {
                OperationViewModel = null;
                ServerStatusMessage = "Server is not configured. Please configure the server from a local client.";
            }
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