using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

/// <summary>
/// Driver for Modbus RTU serial relay modules (e.g. Waveshare, Dingtian, and commercial
/// DIN-rail multi-channel relay modules).
///
/// Uses standard Modbus RTU Function 0x05 (Write Single Coil) with 16-bit CRC:
///   [SlaveID, 0x05, CoilAddrHi, CoilAddrLo, ValHi, ValLo, CRCLo, CRCHi]
///   where CoilAddress is 0-indexed (Channel 1 = Coil 0).
/// </summary>
public class ModbusRtuRelayDriver : IRelayDriver
{
    private const byte FuncWriteSingleCoil = 0x05;
    private const byte CoilValOn = 0xFF;
    private const byte CoilValOff = 0x00;

    private const int ReconnectIntervalMs = 1000;
    private const int IdleKeepAliveIntervalMs = 5000;
    private const int WriteTimeoutMs = 1000;

    private readonly string _portName;
    private readonly int _channels;
    private readonly int _baudRate;
    private readonly byte _slaveId;
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

    public ModbusRtuRelayDriver(string portName, int channels, int baudRate, byte slaveId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("ModbusRTU driver requires a serial port name (set ModbusRTU.Port in config).");
        }

        _portName = portName;
        _channels = channels > 0 ? channels : 8;
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _slaveId = slaveId > 0 ? slaveId : (byte)1;
        _logger = logger;

        _monitor = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "ModbusRTU-monitor"
        };
        _monitor.Start();
    }

    public string Name => "ModbusRTU";
    public int Channels => _channels;
    public int BaudRate => _baudRate;
    public byte SlaveId => _slaveId;

    public static ushort CalculateCrc(byte[] buffer, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= buffer[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                else
                    crc = (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    public static byte[] BuildCommand(byte slaveId, int channel, bool energized)
    {
        int coilAddress = channel - 1; // 0-based coil address
        var frame = new byte[8];
        frame[0] = slaveId;
        frame[1] = FuncWriteSingleCoil;
        frame[2] = (byte)((coilAddress >> 8) & 0xFF);
        frame[3] = (byte)(coilAddress & 0xFF);
        frame[4] = energized ? CoilValOn : CoilValOff;
        frame[5] = 0x00;

        ushort crc = CalculateCrc(frame, 6);
        frame[6] = (byte)(crc & 0xFF);         // Low byte first in Modbus RTU
        frame[7] = (byte)((crc >> 8) & 0xFF);  // High byte second

        return frame;
    }

    public void RegisterChannel(RelayConfig config)
    {
        if (config.RelayPin < 1 || config.RelayPin > _channels)
        {
            throw new InvalidOperationException(
                $"ModbusRTU relay channel must be between 1 and {_channels} (route {config.SourceName}->{config.OutputName} uses {config.RelayPin}).");
        }
    }

    public void SetRelay(int channel, bool energized)
    {
        if (channel < 1 || channel > _channels)
        {
            _logger.LogWarning("ModbusRTU SetRelay ignored: channel {Channel} out of range 1..{MaxChannels}.", channel, _channels);
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
                _dirty = true;
            }

            _logger.LogInformation("ModbusRTU relay driver connected on {Port} (slave {SlaveId}, {Channels} channels, {BaudRate} baud).",
                _portName, _slaveId, _channels, _baudRate);
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
                _logger.LogWarning(ex, "ModbusRTU not reachable on {Port}; retrying every {Interval}ms until it appears.",
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
            var cmd = BuildCommand(_slaveId, ch, on);
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
            _logger.LogWarning(ex, "ModbusRTU disconnected on {Port} ({Reason}); will attempt to reconnect.", _portName, reason);
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
                            var offCmd = BuildCommand(_slaveId, ch, false);
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
