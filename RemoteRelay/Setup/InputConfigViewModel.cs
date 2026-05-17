using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using System.Windows.Input;
using Avalonia.Media;

namespace RemoteRelay.Setup;

/// <summary>
/// View model for a single input card in the setup UI.
/// </summary>
public class InputConfigViewModel : ViewModelBase
{
    private readonly Action<InputConfigViewModel> _deleteAction;
    private readonly Action<string>? _statusReporter;

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
            _customColor = value.ToString(); // Generates #AARRGGBB
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
    public ICommand TestPhysicalButtonCommand { get; }
    public ICommand ClearColorCommand { get; }

    public InputConfigViewModel(string sourceName, string customColor, Color autoPaletteColour, Action<InputConfigViewModel> deleteAction, Action<string>? statusReporter = null)
    {
        _sourceName = sourceName;
        _autoPaletteColour = autoPaletteColour;
        _deleteAction = deleteAction;
        _statusReporter = statusReporter;

        // Drive both _customColor and _customColorValue via the property setter so the ColorPicker reflects the saved value.
        CustomColor = customColor;

        AddOutputRouteCommand = ReactiveCommand.Create(AddOutputRoute);
        DeleteInputCommand = ReactiveCommand.Create(() => _deleteAction(this));
        TestPhysicalButtonCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrWhiteSpace(SourceName))
            {
                _statusReporter?.Invoke("Enter a source name before testing the physical switch.");
                return;
            }

            if (PhysicalButtonPin <= 0)
            {
                _statusReporter?.Invoke($"Configure a physical button pin for {SourceName} before testing.");
                return;
            }

            _statusReporter?.Invoke($"Testing saved switch path for {SourceName}...");
            var error = await SwitcherClient.Instance.TestPhysicalButtonAsync(SourceName);
            _statusReporter?.Invoke(error == null
                ? $"Simulated physical button press for {SourceName}."
                : $"Physical switch test failed for {SourceName}: {error}");
        });

        ClearColorCommand = ReactiveCommand.Create(() => CustomColor = string.Empty);
    }

    private void AddOutputRoute()
    {
        var newName = $"Output {OutputRoutes.Count + 1}";
        OutputRoutes.Add(new RouteConfigViewModel(newName, 0, true, string.Empty, () => RemoveRoute));
    }

    public void RemoveRoute(RouteConfigViewModel route)
    {
        OutputRoutes.Remove(route);
    }
}
