using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

/// <summary>
/// Velleman K8090 8-channel USB relay card driver. Talks to the card's virtual
/// COM port using the documented STX/ETX framing (see K8090 Protocol Manual).
/// Channels are 1..8 (bit 0..7 of the mask byte).
///
/// A single background monitor thread owns the serial port exclusively. It:
///  - opens (and reopens) the port, so a card that is unplugged, power-cycled or
///    not yet present recovers automatically without a config reload;
///  - flushes relay changes queued by <see cref="SetRelay"/>; and
///  - polls the card's relay status (command 0x18 / event 0x51) so the software
///    state cannot silently drift from the hardware — any mismatch is logged and
///    the intended state is re-asserted.
///
/// Software routing is authoritative: on reconnect or detected drift the driver
/// pushes the desired relay state back onto the card. Callers of SetRelay/GetRelay
/// never touch the port, never block on serial I/O, and SetRelay never throws when
/// the card is missing — the change is remembered and applied once it reconnects.
/// </summary>
public class K8090RelayDriver : IRelayDriver
{
    private const byte Stx = 0x04;
    private const byte Etx = 0x0F;
    private const byte CmdSwitchRelayOn = 0x11;
    private const byte CmdSwitchRelayOff = 0x12;
    private const byte CmdQueryStatus = 0x18;
    private const byte EvtRelayStatus = 0x51;
    private const int FrameLength = 7;

    private const int PollIntervalMs = 2000;
    private const int ReconnectIntervalMs = 1000;
    private const int ReadTimeoutMs = 250;

    private readonly string _portName;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _monitor;

    // Guarded by _stateLock.
    private byte _desired;
    private bool _dirty;
    private bool _connected;

    // Owned exclusively by the monitor thread (except in Dispose, after the thread has joined).
    private SerialPort? _port;
    private readonly List<byte> _rx = new();
    private bool _connectFailureLogged;

    private volatile bool _disposed;

    public K8090RelayDriver(string portName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("K8090 driver requires a serial port name (set K8090.Port in config).");
        }

        _portName = portName;
        _logger = logger;

        _monitor = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "K8090-monitor"
        };
        _monitor.Start();
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
        lock (_stateLock)
        {
            if (energized)
                _desired |= mask;
            else
                _desired &= (byte)~mask;
            _dirty = true;
        }
        _wake.Set();
    }

    public bool GetRelay(int channel)
    {
        if (channel < 1 || channel > 8) return false;
        var mask = (byte)(1 << (channel - 1));
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
                else if (TakeDirty())
                {
                    WriteDesired();
                }
                else
                {
                    PollAndReconcile();
                }
            }
            catch (Exception ex)
            {
                // A write/read failure almost always means the card went away; drop
                // the connection and let the loop reconnect.
                Disconnect("serial I/O error", ex);
            }

            if (_disposed) break;

            lock (_stateLock) connected = _connected;
            _wake.WaitOne(connected ? PollIntervalMs : ReconnectIntervalMs);
        }
    }

    private bool TakeDirty()
    {
        lock (_stateLock)
        {
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
            port = new SerialPort(_portName, 19200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = 1000,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            _port = port;
            _rx.Clear();
            _connectFailureLogged = false;

            lock (_stateLock)
            {
                _connected = true;
                _dirty = true; // assert the desired state onto the freshly-opened card
            }

            _logger.LogInformation("K8090 relay driver connected on {Port} (8 channels).", _portName);
            _wake.Set(); // run again immediately to flush the desired state
        }
        catch (Exception ex)
        {
            try { port?.Dispose(); } catch { /* best-effort */ }
            _port = null;
            lock (_stateLock) _connected = false;

            if (!_connectFailureLogged)
            {
                _connectFailureLogged = true;
                _logger.LogWarning(ex, "K8090 not reachable on {Port}; retrying every {Interval}ms until it appears.", _portName, ReconnectIntervalMs);
            }
        }
    }

    private void WriteDesired()
    {
        byte desired;
        lock (_stateLock) desired = _desired;

        // Turn off everything not wanted, then switch on everything wanted. The two
        // masks are disjoint, so the order does not affect the final relay state.
        SendFrame(CmdSwitchRelayOff, (byte)~desired);
        if (desired != 0)
        {
            SendFrame(CmdSwitchRelayOn, desired);
        }
    }

    private void PollAndReconcile()
    {
        SendFrame(CmdQueryStatus, 0x00);

        if (!TryReadRelayStatus(out var hwState))
        {
            return; // no status this cycle; check again next poll
        }

        lock (_stateLock)
        {
            // Skip while a flush is pending: the card simply hasn't caught up yet.
            if (!_dirty && hwState != _desired)
            {
                _logger.LogWarning(
                    "K8090 relay state drift on {Port}: hardware=0x{Hw:X2}, expected=0x{Sw:X2}. Re-asserting.",
                    _portName, hwState, _desired);
                _dirty = true;
                _wake.Set();
            }
        }
    }

    private void SendFrame(byte cmd, byte mask)
    {
        var port = _port;
        if (port == null) return;

        var frame = BuildFrame(cmd, mask);
        port.Write(frame, 0, frame.Length);
    }

    /// <summary>
    /// Reads the card's reply to a status query. Returns the current relay on-mask
    /// reported in the latest 0x51 event (which also covers unsolicited status
    /// changes such as the card's own buttons or timers). Returns false when no
    /// complete status frame is available this cycle.
    /// </summary>
    private bool TryReadRelayStatus(out byte state)
    {
        state = 0;
        var port = _port;
        if (port == null) return false;

        // Block briefly for the first byte of the reply (ReadTimeout), then drain
        // whatever else has arrived without blocking.
        try
        {
            _rx.Add((byte)port.ReadByte());
        }
        catch (TimeoutException)
        {
            return TryExtractRelayStatus(out state); // an earlier unsolicited frame may still be buffered
        }

        while (port.BytesToRead > 0)
        {
            var b = port.ReadByte();
            if (b < 0) break;
            _rx.Add((byte)b);
        }

        return TryExtractRelayStatus(out state);
    }

    private bool TryExtractRelayStatus(out byte state)
    {
        state = 0;
        var found = false;

        var i = 0;
        while (i + FrameLength <= _rx.Count)
        {
            if (_rx[i] != Stx)
            {
                i++;
                continue;
            }

            if (TryParseFrame(_rx, i, out var cmd, out _, out var param1, out _))
            {
                if (cmd == EvtRelayStatus)
                {
                    state = param1; // Param1 = current relay on-mask
                    found = true;
                }
                i += FrameLength;
            }
            else
            {
                i++; // not a valid frame at this STX; resync one byte at a time
            }
        }

        // Discard everything scanned; keep any partial tail for the next read.
        if (i > 0)
        {
            _rx.RemoveRange(0, Math.Min(i, _rx.Count));
        }

        // Backstop against unbounded growth from a garbage/partial stream.
        if (_rx.Count > 64)
        {
            _rx.RemoveRange(0, _rx.Count - FrameLength);
        }

        return found;
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
        _rx.Clear();

        if (wasConnected)
        {
            // Suppress the immediate "not reachable" retry log; we've already said it's gone.
            _connectFailureLogged = true;
            _logger.LogWarning(ex, "K8090 disconnected on {Port} ({Reason}); will attempt to reconnect.", _portName, reason);
        }
    }

    private static byte[] BuildFrame(byte cmd, byte mask, byte param1 = 0x00, byte param2 = 0x00)
    {
        var frame = new byte[FrameLength];
        frame[0] = Stx;
        frame[1] = cmd;
        frame[2] = mask;
        frame[3] = param1;
        frame[4] = param2;
        frame[5] = (byte)(-(Stx + cmd + mask + param1 + param2));
        frame[6] = Etx;
        return frame;
    }

    private static bool TryParseFrame(List<byte> buf, int offset, out byte cmd, out byte mask, out byte param1, out byte param2)
    {
        cmd = mask = param1 = param2 = 0;
        if (offset + FrameLength > buf.Count) return false;
        if (buf[offset] != Stx || buf[offset + 6] != Etx) return false;

        // Checksum: byte 5 == -(byte0 + byte1 + byte2 + byte3 + byte4), i.e. the
        // low byte of the sum of the first six bytes is zero.
        var sum = (byte)(buf[offset] + buf[offset + 1] + buf[offset + 2] + buf[offset + 3] + buf[offset + 4] + buf[offset + 5]);
        if (sum != 0) return false;

        cmd = buf[offset + 1];
        mask = buf[offset + 2];
        param1 = buf[offset + 3];
        param2 = buf[offset + 4];
        return true;
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
                    try { var off = BuildFrame(CmdSwitchRelayOff, 0xFF); port.Write(off, 0, off.Length); } catch { /* best-effort */ }
                    port.Close();
                }
            }
            catch { /* best-effort */ }
            finally { try { port.Dispose(); } catch { /* best-effort */ } }
        }

        _wake.Dispose();
    }
}
