using System.Device.Gpio;

namespace RemoteRelay.Server.Drivers;

public interface IGpioInputs
{
    void OpenInputPin(int pin, PinMode mode);

    void RegisterButton(int pin, PinEventTypes trigger, PinChangeEventHandler handler);

    void UnregisterButton(int pin, PinChangeEventHandler handler);
}
