using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using Zeroconf;

namespace RemoteRelay.Connection;

public class DiscoveredServer
{
    public DiscoveredServer(string name, string host, int port)
    {
        Name = name;
        Host = host;
        Port = port;
    }

    public string Name { get; }
    public string Host { get; }
    public int Port { get; }
    public string Display => $"{Name} ({Host}:{Port})";
}

/// <summary>
/// Edits the client's server connection (host/port) with mDNS discovery.
/// Save reports the chosen host/port back to the owner; a null host means
/// "auto-discover on this network".
/// </summary>
public class ConnectionSettingsViewModel : ViewModelBase
{
    private readonly Action<string?, int?> _onSave;

    private string _host;
    public string Host
    {
        get => _host;
        set => this.RaiseAndSetIfChanged(ref _host, value);
    }

    private decimal? _port;
    public decimal? Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = new();

    private DiscoveredServer? _selectedServer;
    public DiscoveredServer? SelectedServer
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

    public ICommand ScanCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public ConnectionSettingsViewModel(string? host, int? port, Action<string?, int?> onSave, Action onCancel)
    {
        _onSave = onSave;
        _host = host ?? string.Empty;
        _port = port;

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(onCancel);

        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        StatusMessage = "Scanning for RemoteRelay servers…";
        try
        {
            var results = await ZeroconfResolver.ResolveAsync("_remoterelay._tcp.local.", TimeSpan.FromSeconds(3));

            DiscoveredServers.Clear();
            foreach (var result in results)
            {
                var service = result.Services.Values.FirstOrDefault();
                var ip = result.IPAddresses.FirstOrDefault();
                if (service == null || ip == null)
                {
                    continue;
                }

                DiscoveredServers.Add(new DiscoveredServer(result.DisplayName, ip, service.Port));
            }

            StatusMessage = DiscoveredServers.Count switch
            {
                0 => "No servers found. Enter the address manually, or scan again.",
                1 => "Found 1 server — tap it to use it.",
                var n => $"Found {n} servers — tap one to use it."
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Save()
    {
        var host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        _onSave(host, Port is null ? null : (int)Port);
    }
}
