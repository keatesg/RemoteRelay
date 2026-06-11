using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

/// <summary>
/// Velleman K8090 8-channel USB relay card driver. Talks to the card's virtual
/// COM port using the documented STX/ETX framing (see K8090 Protocol Manual).
/// Channels are 1..8 (bit 0..7 of the mask byte).
/// </summary>
public class K8090RelayDriver : IRelayDriver
{
    private const byte Stx = 0x04;
    private const byte Etx = 0x0F;
    private const byte CmdSwitchRelayOn = 0x11;
    private const byte CmdSwitchRelayOff = 0x12;

    private readonly SerialPort _port;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private byte _state;

    public K8090RelayDriver(string portName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("K8090 driver requires a serial port name (set K8090.Port in config).");
        }

        _logger = logger;
        _port = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true
        };
        try
        {
            _port.Open();
            SendCommand(CmdSwitchRelayOff, 0xFF);
            _state = 0;
        }
        catch
        {
            // Release the COM port on any init failure, otherwise it stays locked
            // until process exit and every subsequent open attempt fails too.
            try { _port.Dispose(); } catch { /* best-effort */ }
            throw;
        }

        _logger.LogInformation("K8090 relay driver opened on {Port} (8 channels).", portName);
    }

    public string Name => "K8090";

    public void RegisterChannel(RelayConfig config)
    {
        if (config.RelayPin < 1 || config.RelayPin > 8)
        {
            throw new InvalidOperationException(
                $"K8090 relay channel must be between 1 and 8 (route {config.SourceName}->{config.OutputName} uses {config.RelayPin}).");
        }
    }

    public void SetRelay(int channel, bool energized)
    {
        if (channel < 1 || channel > 8)
        {
            _logger.LogWarning("K8090 SetRelay ignored: channel {Channel} out of range 1..8.", channel);
            return;
        }

        var mask = (byte)(1 << (channel - 1));
        lock (_lock)
        {
            SendCommand(energized ? CmdSwitchRelayOn : CmdSwitchRelayOff, mask);
            if (energized)
                _state |= mask;
            else
                _state &= (byte)~mask;
        }
    }

    public bool GetRelay(int channel)
    {
        if (channel < 1 || channel > 8) return false;
        var mask = (byte)(1 << (channel - 1));
        lock (_lock)
        {
            return (_state & mask) != 0;
        }
    }

    private void SendCommand(byte cmd, byte mask, byte param1 = 0x00, byte param2 = 0x00)
    {
        var frame = new byte[7];
        frame[0] = Stx;
        frame[1] = cmd;
        frame[2] = mask;
        frame[3] = param1;
        frame[4] = param2;
        frame[5] = (byte)(-(Stx + cmd + mask + param1 + param2));
        frame[6] = Etx;

        try
        {
            _port.Write(frame, 0, frame.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "K8090: failed to write command 0x{Cmd:X2} mask 0x{Mask:X2}.", cmd, mask);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
            {
                try { SendCommand(CmdSwitchRelayOff, 0xFF); } catch { /* best-effort */ }
                _port.Close();
            }
        }
        finally
        {
            _port.Dispose();
        }
    }
}
