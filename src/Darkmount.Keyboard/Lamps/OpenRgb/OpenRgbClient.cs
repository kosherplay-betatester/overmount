using System.Buffers.Binary;
using System.Net.Sockets;
using static Darkmount.Keyboard.Lamps.OpenRgb.OpenRgbProtocol;

namespace Darkmount.Keyboard.Lamps.OpenRgb;

/// <summary>
/// Synchronous client for the OpenRGB SDK server (TCP, default 127.0.0.1:6742).
/// <para>
/// Threading: use it from one thread at a time. Calls are serialised by an internal lock, so an accidental concurrent call
/// waits rather than corrupting the stream, but the class is designed for a single owner (the RGB engine thread).
/// </para>
/// <para>
/// Errors: any socket or protocol error (timeout, reset, bad magic, oversized packet) closes the connection,
/// <see cref="Connected"/> turns false and that call and every later one throws <see cref="IOException"/>.
/// Reconnect with <see cref="TryConnect"/>.
/// </para>
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan VersionReplyTimeout = TimeSpan.FromSeconds(1);

    readonly Socket _socket;
    readonly Lock _lock = new();
    readonly byte[] _header = new byte[HeaderSize];
    bool _connected = true, _disposed, _deviceListChanged;

    OpenRgbClient(Socket socket) => _socket = socket;

    /// <summary>Negotiated protocol version: min(server, <see cref="OpenRgbProtocol.ClientVersion"/>), 0 for servers that don't answer.</summary>
    public uint ProtocolVersion { get; private set; }

    public bool Connected
    {
        get { lock (_lock) { Pump(); return _connected && !_disposed; } }
    }

    /// <summary>
    /// True once the server sent DEVICE_LIST_UPDATED since the last <see cref="GetControllers"/> started (one that arrives
    /// while GetControllers runs keeps it set, so the caller re-fetches). Reading it also drains any notifications already
    /// waiting on the socket, so it works while the caller only streams <see cref="UpdateLeds"/>.
    /// </summary>
    public bool DeviceListChanged
    {
        get { lock (_lock) { Pump(); return _deviceListChanged; } }
    }

    /// <summary>
    /// Connects, sends the client name and negotiates version = min(server, ClientVersion) (no reply within 1 s → version 0).
    /// Null when nothing listens or the handshake fails. <paramref name="timeout"/> bounds the connect and every later
    /// socket read/write (default 2 s).
    /// </summary>
    public static OpenRgbClient? TryConnect(string host = "127.0.0.1", int port = OpenRgbProtocol.DefaultPort, string clientName = "OverMount", TimeSpan? timeout = null)
    {
        var limit = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (!socket.ConnectAsync(host, port).Wait(limit) || !socket.Connected)
            {
                socket.Dispose();
                return null;
            }
            socket.ReceiveTimeout = socket.SendTimeout = (int)Math.Min(limit.TotalMilliseconds, int.MaxValue);

            var client = new OpenRgbClient(socket);
            try
            {
                client.Handshake(clientName);
                return client;
            }
            catch
            {
                client.Dispose();
                return null;
            }
        }
        catch (Exception e) when (e is SocketException or AggregateException or IOException or ObjectDisposedException or ArgumentException)
        {
            socket?.Dispose();
            return null;
        }
    }

    void Handshake(string clientName)
    {
        lock (_lock)
        {
            Send(0, SetClientName, ClientNamePayload(clientName));
            Send(0, RequestProtocolVersion, UInt32Payload(ClientVersion));

            // Protocol-0 servers never answer; wait up to 1 s with Poll (a receive timeout would leave the Windows socket
            // in an undefined state), skipping DEVICE_LIST_UPDATED notifications that may arrive first.
            var deadline = DateTime.UtcNow + VersionReplyTimeout;
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                bool ready;
                try { ready = left > TimeSpan.Zero && _socket.Poll(left, SelectMode.SelectRead); }
                catch (Exception e) when (IsSocketFailure(e)) { throw Fail(e); }
                if (!ready)
                {
                    ProtocolVersion = 0;
                    return;
                }
                var (_, pktId, payload) = ReadPacket();
                if (pktId == DeviceListUpdated) { _deviceListChanged = true; continue; }
                if (pktId != RequestProtocolVersion) continue;
                if (payload.Length < 4) throw Fail("Short protocol-version reply.");
                ProtocolVersion = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload), ClientVersion);
                return;
            }
        }
    }

    /// <summary>
    /// All controllers. A controller whose data fails to parse is skipped. Clears <see cref="DeviceListChanged"/> first.
    /// </summary>
    public IReadOnlyList<OpenRgbController> GetControllers()
    {
        lock (_lock)
        {
            EnsureOpen();
            Pump();
            _deviceListChanged = false;

            Send(0, RequestControllerCount);
            var countReply = Expect(RequestControllerCount, 0);
            if (countReply.Length < 4) throw Fail("Short controller-count reply.");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(countReply);
            if (count > ushort.MaxValue) throw Fail($"Implausible controller count {count}.");

            var controllers = new List<OpenRgbController>((int)count);
            for (var i = 0u; i < count; i++)
            {
                Send(i, RequestControllerData, UInt32Payload(ProtocolVersion));
                var data = Expect(RequestControllerData, i);
                try
                {
                    controllers.Add(ParseController(data, (int)i, ProtocolVersion));
                }
                catch (FormatException)
                {
                    // Skip a controller we can't parse rather than losing the whole list.
                }
            }
            return controllers;
        }
    }

    /// <summary>SETCUSTOMMODE: switches the controller to its direct/custom mode (no payload, no reply).</summary>
    public void SetCustomMode(int index)
    {
        lock (_lock)
        {
            EnsureOpen();
            Send(Index(index), OpenRgbProtocol.SetCustomMode);
        }
    }

    /// <summary>UPDATELEDS: one colour per LED, in LED-index order.</summary>
    public void UpdateLeds(int index, IReadOnlyList<LampColor> colors)
    {
        var payload = UpdateLedsPayload(colors);
        lock (_lock)
        {
            EnsureOpen();
            Send(Index(index), OpenRgbProtocol.UpdateLeds, payload);
        }
    }

    /// <summary>UPDATEMODE: restores a mode exactly as it was received (<see cref="OpenRgbMode.RawBlock"/>).</summary>
    public void UpdateMode(int index, int modeIndex, byte[] rawModeBlock)
    {
        ArgumentNullException.ThrowIfNull(rawModeBlock);
        var payload = UpdateModePayload(modeIndex, rawModeBlock);
        lock (_lock)
        {
            EnsureOpen();
            Send(Index(index), OpenRgbProtocol.UpdateMode, payload);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _connected = false;
            _socket.Dispose();
        }
    }

    static uint Index(int index) =>
        index >= 0 ? (uint)index : throw new ArgumentOutOfRangeException(nameof(index), index, "Controller index must be ≥ 0.");

    void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected) throw new IOException("Not connected to the OpenRGB server.");
    }

    /// <summary>Reads notifications already waiting on the socket without blocking for new ones; detects a closed peer.</summary>
    void Pump()
    {
        if (!_connected || _disposed) return;
        try
        {
            while (_socket.Poll(0, SelectMode.SelectRead))
            {
                if (_socket.Available == 0) throw Fail("The OpenRGB server closed the connection.");
                var (_, pktId, _) = ReadPacket();
                if (pktId == DeviceListUpdated) _deviceListChanged = true;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close();
        }
    }

    /// <summary>Reads packets until one with <paramref name="pktId"/> for <paramref name="devId"/>, skipping notifications.</summary>
    byte[] Expect(uint pktId, uint devId)
    {
        while (true)
        {
            var (dev, id, payload) = ReadPacket();
            if (id == DeviceListUpdated) { _deviceListChanged = true; continue; }
            if (id == pktId && dev == devId) return payload;
            // Anything else is unsolicited; skip it.
        }
    }

    (uint DevId, uint PktId, byte[] Payload) ReadPacket()
    {
        ReadExact(_header);
        if (!TryParseHeader(_header, out var devId, out var pktId, out var size)) throw Fail("Bad packet magic.");
        if (size > MaxPayloadSize) throw Fail($"Packet {pktId} claims {size} bytes.");
        var payload = size == 0 ? [] : new byte[size];
        ReadExact(payload);
        return (devId, pktId, payload);
    }

    void ReadExact(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int n;
            try { n = _socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None); }
            catch (Exception e) when (IsSocketFailure(e)) { throw Fail(e); }
            if (n == 0) throw Fail("The OpenRGB server closed the connection.");
            read += n;
        }
    }

    void Send(uint devId, uint pktId, ReadOnlySpan<byte> payload = default)
    {
        var packet = Packet(devId, pktId, payload);
        var sent = 0;
        try
        {
            while (sent < packet.Length) sent += _socket.Send(packet, sent, packet.Length - sent, SocketFlags.None);
        }
        catch (Exception e) when (IsSocketFailure(e)) { throw Fail(e); }
    }

    static bool IsSocketFailure(Exception e) => e is SocketException or ObjectDisposedException or InvalidOperationException;

    /// <summary>Closes the connection and wraps a socket failure as <see cref="IOException"/>.</summary>
    IOException Fail(Exception e)
    {
        Close();
        return new IOException($"OpenRGB connection failed: {e.Message}", e);
    }

    IOException Fail(string message)
    {
        Close();
        return new IOException(message);
    }

    void Close()
    {
        _connected = false;
        try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        _socket.Close();
    }
}
