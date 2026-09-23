using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

/// <summary>
/// Driver for SainSmart USB 4-channel (and compatible multi-channel) relay modules.
/// Communicates over a virtual serial COM port (COMx on Windows, /dev/ttyUSBx or
/// /dev/ttyACMx on Linux) using the 3-byte command protocol:
///   [0xFF, (byte)Channel, (byte)(Energized ? 0x01 : 0x00)]
///
/// A single background monitor thread owns the serial port exclusively. It:
///  - opens and reopens the port automatically, recovering gracefully if the
///    board is unplugged, power-cycled, or not yet present at startup;
///  - flushes relay state changes queued by <see cref="SetRelay"/> without
///    blocking calling threads; and
///  - de-energizes all relays upon clean disposal / server shutdown.
/// </summary>
public class SainSmartRelayDriver : IRelayDriver
{
    private const byte CommandHeader = 0xFF;
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

    public SainSmartRelayDriver(string portName, int channels, int baudRate, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("SainSmart driver requires a serial port name (set SainSmart.Port in config).");
        }

        _portName = portName;
        _channels = channels > 0 ? channels : 4;
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _logger = logger;

        _monitor = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "SainSmart-monitor"
        };
        _monitor.Start();
    }

    public string Name => "SainSmart";
    public int Channels => _channels;
    public int BaudRate => _baudRate;

    public static byte[] BuildCommand(int channel, bool energized)
    {
        return new byte[]
        {
            CommandHeader,
            (byte)channel,
            energized ? StateOn : StateOff
        };
    }

    public void RegisterChannel(RelayConfig config)
    {
        if (config.RelayPin < 1 || config.RelayPin > _channels)
        {
            throw new InvalidOperationException(
                $"SainSmart relay channel must be between 1 and {_channels} (route {config.SourceName}->{config.OutputName} uses {config.RelayPin}).");
        }
    }

    public void SetRelay(int channel, bool energized)
    {
        if (channel < 1 || channel > _channels)
        {
            _logger.LogWarning("SainSmart SetRelay ignored: channel {Channel} out of range 1..{MaxChannels}.", channel, _channels);
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
                _dirty = true; // assert desired state immediately onto newly opened card
            }

            _logger.LogInformation("SainSmart relay driver connected on {Port} ({Channels} channels, {BaudRate} baud).",
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
                _logger.LogWarning(ex, "SainSmart not reachable on {Port}; retrying every {Interval}ms until it appears.",
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
            _logger.LogWarning(ex, "SainSmart disconnected on {Port} ({Reason}); will attempt to reconnect.", _portName, reason);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        try { _monitor.Join(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }

        // The monitor has stopped, so we now own the port. Switch everything off so
        // the card doesn't hold a stale route, then release the port.
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
