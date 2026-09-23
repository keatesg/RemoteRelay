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

public class FilterItem : ViewModelBase
{
    public string Name { get; }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public FilterItem(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }
}

/// <summary>
/// Edits the client's server connection (host/port) and per-client input/output filtering.
/// </summary>
public class ConnectionSettingsViewModel : ViewModelBase
{
    private readonly Action<string?, int?, System.Collections.Generic.List<string>?, System.Collections.Generic.List<string>?> _onSave;

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
    public ObservableCollection<FilterItem> AvailableInputs { get; } = new();
    public ObservableCollection<FilterItem> AvailableOutputs { get; } = new();

    public bool HasFilteringOptions => AvailableInputs.Count > 0 || AvailableOutputs.Count > 0;

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
    public ICommand SelectAllInputsCommand { get; }
    public ICommand SelectAllOutputsCommand { get; }

    public ConnectionSettingsViewModel(
        string? host,
        int? port,
        System.Collections.Generic.IEnumerable<string>? availableInputs,
        System.Collections.Generic.IEnumerable<string>? selectedInputs,
        System.Collections.Generic.IEnumerable<string>? availableOutputs,
        System.Collections.Generic.IEnumerable<string>? selectedOutputs,
        Action<string?, int?, System.Collections.Generic.List<string>?, System.Collections.Generic.List<string>?> onSave,
        Action onCancel)
    {
        _onSave = onSave;
        _host = host ?? string.Empty;
        _port = port;

        var selectedInputSet = new System.Collections.Generic.HashSet<string>(selectedInputs ?? System.Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (availableInputs != null)
        {
            foreach (var input in availableInputs)
            {
                var isSelected = selectedInputs == null || selectedInputSet.Contains(input);
                AvailableInputs.Add(new FilterItem(input, isSelected));
            }
        }

        var selectedOutputSet = new System.Collections.Generic.HashSet<string>(selectedOutputs ?? System.Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (availableOutputs != null)
        {
            foreach (var output in availableOutputs)
            {
                var isSelected = selectedOutputs == null || selectedOutputSet.Contains(output);
                AvailableOutputs.Add(new FilterItem(output, isSelected));
            }
        }

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(onCancel);
        SelectAllInputsCommand = ReactiveCommand.Create(() => { foreach (var item in AvailableInputs) item.IsSelected = true; });
        SelectAllOutputsCommand = ReactiveCommand.Create(() => { foreach (var item in AvailableOutputs) item.IsSelected = true; });

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
        var inputs = AvailableInputs.Count > 0 ? AvailableInputs.Where(x => x.IsSelected).Select(x => x.Name).ToList() : null;
        var outputs = AvailableOutputs.Count > 0 ? AvailableOutputs.Where(x => x.IsSelected).Select(x => x.Name).ToList() : null;
        _onSave(host, Port is null ? null : (int)Port, inputs, outputs);
    }
}
