using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;
using RemoteRelay.Configurator.Services;

namespace RemoteRelay.Configurator.ViewModels;

public class InputConfigViewModel : ViewModelBase
{
    private readonly Action<InputConfigViewModel> _deleteAction;
    private readonly Action<string>? _statusReporter;
    private readonly ConfiguratorClient? _client;

    private string _sourceName = string.Empty;
    public string SourceName
    {
        get => _sourceName;
        set => this.RaiseAndSetIfChanged(ref _sourceName, value);
    }

    private Color _autoPaletteColour = Colors.LightGray;

    private Color _customColorValue = Colors.LightGray;
    public Color CustomColorValue
    {
        get => _customColorValue;
        set
        {
            this.RaiseAndSetIfChanged(ref _customColorValue, value);
            _customColor = value.ToString();
            this.RaisePropertyChanged(nameof(CustomColor));
        }
    }

    private string _customColor = string.Empty;
    public string CustomColor
    {
        get => _customColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _customColor, value);
            if (!string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var parsedColor))
            {
                _customColorValue = parsedColor;
            }
            else
            {
                _customColorValue = _autoPaletteColour;
            }
            this.RaisePropertyChanged(nameof(CustomColorValue));
        }
    }

    private int _physicalButtonPin;
    public int PhysicalButtonPin
    {
        get => _physicalButtonPin;
        set => this.RaiseAndSetIfChanged(ref _physicalButtonPin, value);
    }

    private string _physicalButtonTrigger = "Low";
    public string PhysicalButtonTrigger
    {
        get => _physicalButtonTrigger;
        set => this.RaiseAndSetIfChanged(ref _physicalButtonTrigger, value);
    }

    public ObservableCollection<RouteConfigViewModel> OutputRoutes { get; } = new();

    public ICommand AddOutputRouteCommand { get; }
    public ICommand DeleteInputCommand { get; }
    public ICommand ClearColorCommand { get; }

    public InputConfigViewModel(
        string sourceName,
        string customColor,
        Color autoPaletteColour,
        Action<InputConfigViewModel> deleteAction,
        Action<string>? statusReporter = null,
        ConfiguratorClient? client = null)
    {
        _sourceName = sourceName;
        _autoPaletteColour = autoPaletteColour;
        _deleteAction = deleteAction;
        _statusReporter = statusReporter;
        _client = client;

        CustomColor = customColor;

        AddOutputRouteCommand = ReactiveCommand.Create(AddOutputRoute);
        DeleteInputCommand = ReactiveCommand.Create(() => _deleteAction(this));
        ClearColorCommand = ReactiveCommand.Create(() =>
        {
            _customColor = string.Empty;
            _customColorValue = _autoPaletteColour;
            this.RaisePropertyChanged(nameof(CustomColor));
            this.RaisePropertyChanged(nameof(CustomColorValue));
        });
    }

    private void AddOutputRoute()
    {
        var outputName = $"Output {OutputRoutes.Count + 1}";
        var newRoute = new RouteConfigViewModel(
            outputName,
            0,
            activeLow: true,
            string.Empty,
            () => DeleteRoute,
            () => SourceName,
            _client);
        OutputRoutes.Add(newRoute);
    }

    private void DeleteRoute(RouteConfigViewModel route)
    {
        OutputRoutes.Remove(route);
    }
}
