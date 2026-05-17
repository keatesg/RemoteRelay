using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

public interface IRelayDriver : IDisposable
{
    string Name { get; }

    void RegisterChannel(RelayConfig config);

    void SetRelay(int channel, bool energized);

    bool GetRelay(int channel);
}
