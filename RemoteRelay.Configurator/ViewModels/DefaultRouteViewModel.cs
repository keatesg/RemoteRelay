using System.Collections.ObjectModel;
using ReactiveUI;

namespace RemoteRelay.Configurator.ViewModels;

public class DefaultRouteViewModel : ViewModelBase
{
    private string _sourceName = string.Empty;
    public string SourceName
    {
        get => _sourceName;
        set => this.RaiseAndSetIfChanged(ref _sourceName, value);
    }

    private string? _selectedOutput;
    public string? SelectedOutput
    {
        get => _selectedOutput;
        set => this.RaiseAndSetIfChanged(ref _selectedOutput, value);
    }

    public ObservableCollection<string> AvailableOutputs { get; } = new();

    public DefaultRouteViewModel(string sourceName, string? selectedOutput)
    {
        _sourceName = sourceName;
        _selectedOutput = selectedOutput;
    }
}
