using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

/// <summary>
/// Driver for LC Technology (LCUS-1, LCUS-2, LCUS-4, LCUS-8) and Seeit (USBB-RELAY04,
/// USBM-RELAY01) USB relay boards commonly based on the CH340 + STC micro architecture.
///
/// Communicates over virtual COM port (COMx / /dev/ttyUSBx) at 9600 baud 8N1 using the
/// 4-byte checksummed packet protocol:
///   [0xA0, (byte)Channel, (byte)(Energized ? 0x01 : 0x00), (byte)Checksum]
///   where Checksum = (0xA0 + Channel + State) & 0xFF.
/// </summary>
public class LcusRelayDriver : IRelayDriver
{
    private const byte CommandHeader = 0xA0;
    private const byte StateOff = 0x00;
    private const byte StateOn = 0x01;

    private const int ReconnectIntervalMs = 1000;
    private const int IdleKeepAliveIntervalMs = 5000;
    private const int WriteTimeoutMs = 1000;

    private readonly string _portName;
    private readonly int _channels;
    private readonly int _baudRate;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _monitor;

    // Guarded by _stateLock.
    private int _desired;
    private bool _dirty;
    private bool _connected;

    // Owned exclusively by the monitor thread (except in Dispose after thread joins).
    private SerialPort? _port;
    private bool _connectFailureLogged;

    private volatile bool _disposed;

    public LcusRelayDriver(string portName, int channels, int baudRate, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("LCUS driver requires a serial port name (set LCUS.Port in config).");
        }

        _portName = portName;
        _channels = channels > 0 ? channels : 4;
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _logger = logger;

        _monitor = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "LCUS-monitor"
        };
        _monitor.Start();
    }

    public string Name => "LCUS";
    public int Channels => _channels;
    public int BaudRate => _baudRate;

    public static byte[] BuildCommand(int channel, bool energized)
    {
        byte state = energized ? StateOn : StateOff;
        byte checksum = (byte)(CommandHeader + channel + state);
        return new byte[]
        {
            CommandHeader,
            (byte)channel,
            state,
            checksum
        };
    }

    public void RegisterChannel(RelayConfig config)
    {
        if (config.RelayPin < 1 || config.RelayPin > _channels)
        {
            throw new InvalidOperationException(
                $"LCUS relay channel must be between 1 and {_channels} (route {config.SourceName}->{config.OutputName} uses {config.RelayPin}).");
        }
    }

    public void SetRelay(int channel, bool energized)
    {
        if (channel < 1 || channel > _channels)
        {
            _logger.LogWarning("LCUS SetRelay ignored: channel {Channel} out of range 1..{MaxChannels}.", channel, _channels);
            return;
        }

        var mask = 1 << (channel - 1);
        lock (_stateLock)
        {
            if (energized)
                _desired |= mask;
            else
                _desired &= ~mask;
            _dirty = true;
        }
        _wake.Set();
    }

    public bool GetRelay(int channel)
    {
        if (channel < 1 || channel > _channels) return false;
        var mask = 1 << (channel - 1);
        lock (_stateLock)
        {
            return (_desired & mask) != 0;
        }
    }

    // ----- monitor thread -----

    private void MonitorLoop()
    {
        while (!_disposed)
        {
            bool connected;
            lock (_stateLock) connected = _connected;

            try
            {
                if (!connected)
                {
                    TryConnect();
                }
                else if (TakeDirty(out var desired))
                {
                    WriteDesired(desired);
                }
            }
            catch (Exception ex)
            {
                Disconnect("serial I/O error", ex);
            }

            if (_disposed) break;

            lock (_stateLock) connected = _connected;
            _wake.WaitOne(connected ? IdleKeepAliveIntervalMs : ReconnectIntervalMs);
        }
    }

    private bool TakeDirty(out int desired)
    {
        lock (_stateLock)
        {
            desired = _desired;
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }
    }

    private void TryConnect()
    {
        SerialPort? port = null;
        try
        {
            port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = WriteTimeoutMs,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _port = port;
            _connectFailureLogged = false;

            lock (_stateLock)
            {
                _connected = true;
                _dirty = true; // assert desired state onto newly opened card
            }

            _logger.LogInformation("LCUS relay driver connected on {Port} ({Channels} channels, {BaudRate} baud).",
                _portName, _channels, _baudRate);
            _wake.Set();
        }
        catch (Exception ex)
        {
            try { port?.Dispose(); } catch { /* best-effort */ }
            _port = null;
            lock (_stateLock) _connected = false;

            if (!_connectFailureLogged)
            {
                _connectFailureLogged = true;
                _logger.LogWarning(ex, "LCUS not reachable on {Port}; retrying every {Interval}ms until it appears.",
                    _portName, ReconnectIntervalMs);
            }
        }
    }

    private void WriteDesired(int desired)
    {
        var port = _port;
        if (port == null || !port.IsOpen) return;

        for (int ch = 1; ch <= _channels; ch++)
        {
            var mask = 1 << (ch - 1);
            var on = (desired & mask) != 0;
            var cmd = BuildCommand(ch, on);
            port.Write(cmd, 0, cmd.Length);
        }
    }

    private void Disconnect(string reason, Exception? ex = null)
    {
        bool wasConnected;
        lock (_stateLock)
        {
            wasConnected = _connected;
            _connected = false;
        }

        var port = _port;
        _port = null;
        if (port != null)
        {
            try { if (port.IsOpen) port.Close(); } catch { /* best-effort */ }
            try { port.Dispose(); } catch { /* best-effort */ }
        }

        if (wasConnected)
        {
            _connectFailureLogged = true;
            _logger.LogWarning(ex, "LCUS disconnected on {Port} ({Reason}); will attempt to reconnect.", _portName, reason);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        try { _monitor.Join(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }

        var port = _port;
        _port = null;
        if (port != null)
        {
            try
            {
                if (port.IsOpen)
                {
                    for (int ch = 1; ch <= _channels; ch++)
                    {
                        try
                        {
                            var offCmd = BuildCommand(ch, false);
                            port.Write(offCmd, 0, offCmd.Length);
                        }
                        catch { /* best-effort */ }
                    }
                    port.Close();
                }
            }
            catch { /* best-effort */ }
            finally
            {
                try { port.Dispose(); } catch { /* best-effort */ }
            }
        }

        _wake.Dispose();
    }
}
