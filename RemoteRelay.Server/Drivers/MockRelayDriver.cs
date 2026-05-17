using System.Device.Gpio;

namespace RemoteRelay.Server.Drivers;

public class MockRelayDriver : GpioRelayDriver
{
    public MockRelayDriver() : base(new GpioController(new MockGpioDriver()), "Mock") { }
}
