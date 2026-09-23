using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;
using RemoteRelay.Common;
using RemoteRelay.Configurator.Services;

namespace RemoteRelay.Configurator.ViewModels;

public class ConfiguratorViewModel : ViewModelBase
{
    private readonly ConfiguratorClient? _client;
    private readonly Func<string?> _getProvidedPin;

    private int _serverPort = 33101;
    public int ServerPort
    {
        get => _serverPort;
        set => this.RaiseAndSetIfChanged(ref _serverPort, value);
    }

    private string? _configPin;
    public string? ConfigPin
    {
        get => _configPin;
        set => this.RaiseAndSetIfChanged(ref _configPin, value);
    }

    private string _relayDriver = "Auto";
    public string RelayDriver
    {
        get => _relayDriver;
        set => this.RaiseAndSetIfChanged(ref _relayDriver, value);
    }

    public ObservableCollection<string> AvailableRelayDrivers { get; } = new()
    {
        "Auto", "RpiGpio", "K8090", "SainSmart", "LCUS", "ModbusRTU", "Mock"
    };

    // Driver-specific
    private string _k8090Port = string.Empty;
    public string K8090Port
    {
        get => _k8090Port;
        set => this.RaiseAndSetIfChanged(ref _k8090Port, value);
    }

    private string _sainSmartPort = string.Empty;
    public string SainSmartPort
    {
        get => _sainSmartPort;
        set => this.RaiseAndSetIfChanged(ref _sainSmartPort, value);
    }

    private int _sainSmartChannels = 4;
    public int SainSmartChannels
    {
        get => _sainSmartChannels;
        set => this.RaiseAndSetIfChanged(ref _sainSmartChannels, value);
    }

    private string _lcusPort = string.Empty;
    public string LcusPort
    {
        get => _lcusPort;
        set => this.RaiseAndSetIfChanged(ref _lcusPort, value);
    }

    private int _lcusChannels = 4;
    public int LcusChannels
    {
        get => _lcusChannels;
        set => this.RaiseAndSetIfChanged(ref _lcusChannels, value);
    }

    private string _modbusPort = string.Empty;
    public string ModbusPort
    {
        get => _modbusPort;
        set => this.RaiseAndSetIfChanged(ref _modbusPort, value);
    }

    private int _modbusChannels = 8;
    public int ModbusChannels
    {
        get => _modbusChannels;
        set => this.RaiseAndSetIfChanged(ref _modbusChannels, value);
    }

    private byte _modbusSlaveId = 1;
    public byte ModbusSlaveId
    {
        get => _modbusSlaveId;
        set => this.RaiseAndSetIfChanged(ref _modbusSlaveId, value);
    }

    private string _tcpMirrorAddress = string.Empty;
    public string TcpMirrorAddress
    {
        get => _tcpMirrorAddress;
        set => this.RaiseAndSetIfChanged(ref _tcpMirrorAddress, value);
    }

    private int? _tcpMirrorPort;
    public int? TcpMirrorPort
    {
        get => _tcpMirrorPort;
        set => this.RaiseAndSetIfChanged(ref _tcpMirrorPort, value);
    }

    private int? _udpApiPort;
    public int? UdpApiPort
    {
        get => _udpApiPort;
        set => this.RaiseAndSetIfChanged(ref _udpApiPort, value);
    }

    private int _inactiveRelayPin;
    public int InactiveRelayPin
    {
        get => _inactiveRelayPin;
        set => this.RaiseAndSetIfChanged(ref _inactiveRelayPin, value);
    }

    private string _inactiveRelayState = "High";
    public string InactiveRelayState
    {
        get => _inactiveRelayState;
        set => this.RaiseAndSetIfChanged(ref _inactiveRelayState, value);
    }

    private bool _flashOnSelect = true;
    public bool FlashOnSelect
    {
        get => _flashOnSelect;
        set => this.RaiseAndSetIfChanged(ref _flashOnSelect, value);
    }

    private bool _showIpOnScreen = true;
    public bool ShowIpOnScreen
    {
        get => _showIpOnScreen;
        set => this.RaiseAndSetIfChanged(ref _showIpOnScreen, value);
    }

    private bool _showClockOnScreen = true;
    public bool ShowClockOnScreen
    {
        get => _showClockOnScreen;
        set => this.RaiseAndSetIfChanged(ref _showClockOnScreen, value);
    }

    private bool _logging = true;
    public bool Logging
    {
        get => _logging;
        set => this.RaiseAndSetIfChanged(ref _logging, value);
    }

    private string _logoFile = string.Empty;
    public string LogoFile
    {
        get => _logoFile;
        set => this.RaiseAndSetIfChanged(ref _logoFile, value);
    }

    private string _themePalette = "Default";
    public string ThemePalette
    {
        get => _themePalette;
        set => this.RaiseAndSetIfChanged(ref _themePalette, value);
    }

    public ObservableCollection<string> AvailablePalettes { get; } = new()
    {
        "Default", "Warm", "Cool", "Pastel", "HighContrast"
    };

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        set => this.RaiseAndSetIfChanged(ref _isSaving, value);
    }

    public ObservableCollection<InputConfigViewModel> Inputs { get; } = new();
    public ObservableCollection<DefaultRouteViewModel> DefaultRoutes { get; } = new();

    public ICommand AddInputCommand { get; }
    public ICommand SaveCommand { get; }

    public ConfiguratorViewModel(AppSettings settings, ConfiguratorClient? client, Func<string?> getProvidedPin)
    {
        _client = client;
        _getProvidedPin = getProvidedPin;

        LoadSettings(settings);

        AddInputCommand = ReactiveCommand.Create(AddInput);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    public void LoadSettings(AppSettings settings)
    {
        ServerPort = settings.ServerPort > 0 ? settings.ServerPort : 33101;
        ConfigPin = settings.ConfigPin;
        RelayDriver = settings.RelayDriver ?? "Auto";
        K8090Port = settings.K8090?.Port ?? string.Empty;
        SainSmartPort = settings.SainSmart?.Port ?? string.Empty;
        SainSmartChannels = settings.SainSmart?.Channels ?? 4;
        LcusPort = settings.LCUS?.Port ?? string.Empty;
        LcusChannels = settings.LCUS?.Channels ?? 4;
        ModbusPort = settings.ModbusRTU?.Port ?? string.Empty;
        ModbusChannels = settings.ModbusRTU?.Channels ?? 8;
        ModbusSlaveId = settings.ModbusRTU?.SlaveId ?? 1;

        TcpMirrorAddress = settings.TcpMirrorAddress ?? string.Empty;
        TcpMirrorPort = settings.TcpMirrorPort;
        UdpApiPort = settings.UdpApiPort;

        if (settings.InactiveRelay != null)
        {
            InactiveRelayPin = settings.InactiveRelay.Pin;
            InactiveRelayState = settings.InactiveRelay.InactiveState;
        }

        FlashOnSelect = settings.FlashOnSelect;
        ShowIpOnScreen = settings.ShowIpOnScreen;
        ShowClockOnScreen = settings.ShowClockOnScreen ?? true;
        Logging = settings.Logging;
        LogoFile = settings.LogoFile ?? string.Empty;
        ThemePalette = settings.ThemePalette ?? "Default";

        Inputs.Clear();
        DefaultRoutes.Clear();

        if (settings.Routes != null)
        {
            var groupedRoutes = settings.Routes.GroupBy(r => r.SourceName);
            foreach (var group in groupedRoutes)
            {
                var customColor = string.Empty;
                if (settings.SourceColorPalette != null && settings.SourceColorPalette.TryGetValue(group.Key, out var color))
                {
                    customColor = color;
                }

                var inputVm = new InputConfigViewModel(
                    group.Key,
                    customColor,
                    Colors.Gray,
                    DeleteInput,
                    msg => StatusMessage = msg,
                    _client);

                if (settings.PhysicalSourceButtons != null &&
                    settings.PhysicalSourceButtons.TryGetValue(group.Key, out var btnConfig))
                {
                    inputVm.PhysicalButtonPin = btnConfig.PinNumber;
                    inputVm.PhysicalButtonTrigger = btnConfig.TriggerState;
                }

                foreach (var route in group)
                {
                    inputVm.OutputRoutes.Add(new RouteConfigViewModel(
                        route.OutputName,
                        route.RelayPin,
                        route.ActiveLow,
                        string.Empty,
                        () => r => inputVm.OutputRoutes.Remove(r),
                        () => inputVm.SourceName,
                        _client));
                }

                Inputs.Add(inputVm);
            }
        }

        RebuildDefaultRoutes();
    }

    private void AddInput()
    {
        var sourceName = $"Input {Inputs.Count + 1}";
        var newInput = new InputConfigViewModel(
            sourceName,
            string.Empty,
            Colors.Gray,
            DeleteInput,
            msg => StatusMessage = msg,
            _client);

        newInput.OutputRoutes.Add(new RouteConfigViewModel(
            "Output 1",
            0,
            activeLow: true,
            string.Empty,
            () => r => newInput.OutputRoutes.Remove(r),
            () => newInput.SourceName,
            _client));

        Inputs.Add(newInput);
        RebuildDefaultRoutes();
    }

    private void DeleteInput(InputConfigViewModel input)
    {
        Inputs.Remove(input);
        RebuildDefaultRoutes();
    }

    public void RebuildDefaultRoutes()
    {
        var allOutputs = Inputs.SelectMany(i => i.OutputRoutes).Select(r => r.OutputName).Distinct().ToList();

        var existingMap = DefaultRoutes.ToDictionary(d => d.SourceName, d => d.SelectedOutput);
        DefaultRoutes.Clear();

        foreach (var input in Inputs)
        {
            existingMap.TryGetValue(input.SourceName, out var currentOutput);
            var defaultVm = new DefaultRouteViewModel(input.SourceName, currentOutput);
            foreach (var outName in allOutputs)
            {
                defaultVm.AvailableOutputs.Add(outName);
            }
            if (!string.IsNullOrEmpty(currentOutput) && allOutputs.Contains(currentOutput))
            {
                defaultVm.SelectedOutput = currentOutput;
            }
            else if (allOutputs.Count > 0)
            {
                defaultVm.SelectedOutput = allOutputs[0];
            }
            DefaultRoutes.Add(defaultVm);
        }
    }

    public AppSettings BuildAppSettings()
    {
        var settings = new AppSettings
        {
            ServerPort = ServerPort,
            ConfigPin = string.IsNullOrWhiteSpace(ConfigPin) ? null : ConfigPin.Trim(),
            RelayDriver = RelayDriver,
            FlashOnSelect = FlashOnSelect,
            ShowIpOnScreen = ShowIpOnScreen,
            ShowClockOnScreen = ShowClockOnScreen,
            Logging = Logging,
            LogoFile = LogoFile,
            ThemePalette = ThemePalette,
            TcpMirrorAddress = string.IsNullOrWhiteSpace(TcpMirrorAddress) ? null : TcpMirrorAddress,
            TcpMirrorPort = TcpMirrorPort,
            UdpApiPort = UdpApiPort
        };

        if (RelayDriver == "K8090")
        {
            settings.K8090 = new K8090Settings { Port = K8090Port };
        }
        else if (RelayDriver == "SainSmart")
        {
            settings.SainSmart = new SainSmartSettings { Port = SainSmartPort, Channels = SainSmartChannels };
        }
        else if (RelayDriver == "LCUS")
        {
            settings.LCUS = new LcusSettings { Port = LcusPort, Channels = LcusChannels };
        }
        else if (RelayDriver == "ModbusRTU")
        {
            settings.ModbusRTU = new ModbusSettings { Port = ModbusPort, Channels = ModbusChannels, SlaveId = ModbusSlaveId };
        }

        if (InactiveRelayPin > 0)
        {
            settings.InactiveRelay = new InactiveRelaySettings
            {
                Pin = InactiveRelayPin,
                InactiveState = InactiveRelayState
            };
        }

        var routes = new List<RelayConfig>();
        var physicalButtons = new Dictionary<string, PhysicalButtonConfig>();
        var palettes = new Dictionary<string, string>();

        foreach (var input in Inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.CustomColor))
            {
                palettes[input.SourceName] = input.CustomColor;
            }

            if (input.PhysicalButtonPin > 0)
            {
                physicalButtons[input.SourceName] = new PhysicalButtonConfig
                {
                    PinNumber = input.PhysicalButtonPin,
                    TriggerState = input.PhysicalButtonTrigger
                };
            }

            foreach (var route in input.OutputRoutes)
            {
                routes.Add(new RelayConfig
                {
                    SourceName = input.SourceName,
                    OutputName = route.OutputName,
                    RelayPin = route.RelayPin,
                    ActiveLow = route.ActiveLow
                });
            }
        }

        settings.Routes = routes;
        settings.PhysicalSourceButtons = physicalButtons;
        settings.SourceColorPalette = palettes;

        var defaultRoutes = new Dictionary<string, string>();
        foreach (var d in DefaultRoutes)
        {
            if (!string.IsNullOrEmpty(d.SelectedOutput))
            {
                defaultRoutes[d.SourceName] = d.SelectedOutput;
            }
        }
        settings.DefaultRoutes = defaultRoutes;

        return settings;
    }

    public async Task SaveAsync()
    {
        if (IsSaving) return;
        IsSaving = true;
        StatusMessage = "Saving configuration…";

        try
        {
            var settings = BuildAppSettings();

            if (_client != null && _client.IsConnected)
            {
                var providedPin = _getProvidedPin();
                var resp = await _client.SaveConfigurationAsync(settings, providedPin);
                if (resp.Success)
                {
                    StatusMessage = $"✓ Configuration saved successfully! ({DateTime.Now:T})";
                }
                else
                {
                    StatusMessage = $"✗ Save failed: {resp.Error}";
                }
            }
            else
            {
                // Fallback to saving to local file if not connected over network
                var path = AppPaths.ServerConfigPath;
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                StatusMessage = $"✓ Saved to local file: {path} ({DateTime.Now:T})";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error saving: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
