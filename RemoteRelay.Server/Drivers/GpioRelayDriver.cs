using System.Device.Gpio;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

public class GpioRelayDriver : IRelayDriver, IGpioInputs
{
    private readonly GpioController _controller;
    private readonly Dictionary<int, RelayConfig> _channels = new();
    private readonly string _name;

    protected GpioRelayDriver(GpioController controller, string name)
    {
        _controller = controller;
        _name = name;
    }

    public GpioRelayDriver() : this(new GpioController(), "RpiGpio") { }

    public string Name => _name;

    public void RegisterChannel(RelayConfig config)
    {
        if (!_controller.IsPinOpen(config.RelayPin))
        {
            _controller.OpenPin(config.RelayPin, PinMode.Output);
        }
        _channels[config.RelayPin] = config;
    }

    public void SetRelay(int channel, bool energized)
    {
        var activeLow = _channels.TryGetValue(channel, out var cfg) ? cfg.ActiveLow : true;
        var value = energized
            ? (activeLow ? PinValue.Low : PinValue.High)
            : (activeLow ? PinValue.High : PinValue.Low);

        if (!_controller.IsPinOpen(channel))
        {
            _controller.OpenPin(channel, PinMode.Output);
        }
        _controller.Write(channel, value);
    }

    public bool GetRelay(int channel)
    {
        if (!_controller.IsPinOpen(channel))
        {
            return false;
        }

        var activeLow = _channels.TryGetValue(channel, out var cfg) ? cfg.ActiveLow : true;
        var current = _controller.Read(channel);
        return activeLow ? current == PinValue.Low : current == PinValue.High;
    }

    public void OpenInputPin(int pin, PinMode mode)
    {
        if (!_controller.IsPinOpen(pin))
        {
            _controller.OpenPin(pin, mode);
        }
        else
        {
            _controller.SetPinMode(pin, mode);
        }
    }

    public void RegisterButton(int pin, PinEventTypes trigger, PinChangeEventHandler handler)
    {
        _controller.RegisterCallbackForPinValueChangedEvent(pin, trigger, handler);
    }

    public void UnregisterButton(int pin, PinChangeEventHandler handler)
    {
        if (_controller.IsPinOpen(pin))
        {
            _controller.UnregisterCallbackForPinValueChangedEvent(pin, handler);
            _controller.ClosePin(pin);
        }
    }

    public void Dispose()
    {
        _controller.Dispose();
    }
}
