using System;
using System.Windows.Input;
using ReactiveUI;
using RemoteRelay.Configurator.Services;

namespace RemoteRelay.Configurator.ViewModels;

public class RouteConfigViewModel : ViewModelBase
{
    private readonly Func<Action<RouteConfigViewModel>> _getDeleteAction;
    private readonly ConfiguratorClient? _client;
    private readonly Func<string> _getSourceName;

    private string _outputName = string.Empty;
    public string OutputName
    {
        get => _outputName;
        set => this.RaiseAndSetIfChanged(ref _outputName, value);
    }

    private int _relayPin;
    public int RelayPin
    {
        get => _relayPin;
        set => this.RaiseAndSetIfChanged(ref _relayPin, value);
    }

    private bool _activeLow = true;
    public bool ActiveLow
    {
        get => _activeLow;
        set => this.RaiseAndSetIfChanged(ref _activeLow, value);
    }

    private string _tcpMessage = string.Empty;
    public string TcpMessage
    {
        get => _tcpMessage;
        set => this.RaiseAndSetIfChanged(ref _tcpMessage, value);
    }

    public ICommand DeleteRouteCommand { get; }
    public ICommand TestRouteCommand { get; }

    public RouteConfigViewModel(
        string outputName,
        int relayPin,
        bool activeLow,
        string tcpMessage,
        Func<Action<RouteConfigViewModel>> getDeleteAction,
        Func<string> getSourceName,
        ConfiguratorClient? client = null)
    {
        _outputName = outputName;
        _relayPin = relayPin;
        _activeLow = activeLow;
        _tcpMessage = tcpMessage;
        _getDeleteAction = getDeleteAction;
        _getSourceName = getSourceName;
        _client = client;

        DeleteRouteCommand = ReactiveCommand.Create(() => _getDeleteAction()(this));
        TestRouteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_client != null && _client.IsConnected && !string.IsNullOrWhiteSpace(_outputName))
            {
                var src = _getSourceName();
                if (!string.IsNullOrWhiteSpace(src))
                {
                    await _client.SwitchSourceAsync(src, _outputName);
                }
            }
        });
    }
}
