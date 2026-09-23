using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using RemoteRelay.Common;
using RemoteRelay.Configurator.Services;
using Zeroconf;

namespace RemoteRelay.Configurator.ViewModels;

public class DiscoveredServerItem
{
    public string Name { get; }
    public string Host { get; }
    public int Port { get; }
    public string Display => $"{Name} ({Host}:{Port})";

    public DiscoveredServerItem(string name, string host, int port)
    {
        Name = name;
        Host = host;
        Port = port;
    }
}

public class MainWindowViewModel : ViewModelBase
{
    private readonly ConfiguratorClient _client = new();

    private string _host = "localhost";
    public string Host
    {
        get => _host;
        set => this.RaiseAndSetIfChanged(ref _host, value);
    }

    private int _port = 33101;
    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    private string _pin = string.Empty;
    public string Pin
    {
        get => _pin;
        set => this.RaiseAndSetIfChanged(ref _pin, value);
    }

    private bool _isPinRequired;
    public bool IsPinRequired
    {
        get => _isPinRequired;
        set => this.RaiseAndSetIfChanged(ref _isPinRequired, value);
    }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    private string _statusMessage = "Ready. Enter server address or pick a discovered server.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ObservableCollection<DiscoveredServerItem> DiscoveredServers { get; } = new();

    private DiscoveredServerItem? _selectedServer;
    public DiscoveredServerItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedServer, value);
            if (value != null)
            {
                Host = value.Host;
                Port = value.Port;
            }
        }
    }

    private ConfiguratorViewModel? _configurator;
    public ConfiguratorViewModel? Configurator
    {
        get => _configurator;
        set => this.RaiseAndSetIfChanged(ref _configurator, value);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand LoadLocalFileCommand { get; }

    public MainWindowViewModel()
    {
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
        ScanCommand = ReactiveCommand.CreateFromTask(ScanServersAsync);
        LoadLocalFileCommand = ReactiveCommand.Create(LoadLocalConfig);

        _ = ScanServersAsync();
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        StatusMessage = $"Connecting to {Host}:{Port}…";

        try
        {
            var connected = await _client.ConnectAsync(Host, Port);
            if (!connected)
            {
                StatusMessage = $"✗ Failed to connect to {Host}:{Port}. Check that RemoteRelay.Server is running.";
                IsConnected = false;
                return;
            }

            IsPinRequired = await _client.IsPinRequiredAsync();

            var settings = await _client.GetSettingsAsync();
            if (settings == null)
            {
                StatusMessage = "✗ Connected, but failed to fetch server settings.";
                IsConnected = false;
                return;
            }

            Configurator = new ConfiguratorViewModel(settings.Value, _client, () => string.IsNullOrWhiteSpace(Pin) ? null : Pin.Trim());
            IsConnected = true;
            StatusMessage = $"✓ Connected to {Host}:{Port}" + (IsPinRequired ? " (PIN protected)" : "");
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Connection error: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
        Configurator = null;
        IsConnected = false;
        StatusMessage = "Disconnected.";
    }

    public void LoadLocalConfig()
    {
        try
        {
            var path = AppPaths.ServerConfigPath;
            if (!System.IO.File.Exists(path))
            {
                StatusMessage = $"No local config found at {path}.";
                return;
            }

            var json = System.IO.File.ReadAllText(path);
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Configurator = new ConfiguratorViewModel(settings, null, () => null);
            IsConnected = true;
            StatusMessage = $"✓ Loaded from local file: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading local file: {ex.Message}";
        }
    }

    private async Task ScanServersAsync()
    {
        try
        {
            var results = await ZeroconfResolver.ResolveAsync("_remoterelay._tcp.local.", TimeSpan.FromSeconds(3));
            DiscoveredServers.Clear();

            foreach (var r in results)
            {
                var service = r.Services.Values.FirstOrDefault();
                var ip = r.IPAddresses.FirstOrDefault();
                if (service != null && ip != null)
                {
                    DiscoveredServers.Add(new DiscoveredServerItem(r.DisplayName, ip, service.Port));
                }
            }
        }
        catch
        {
        }
    }
}
