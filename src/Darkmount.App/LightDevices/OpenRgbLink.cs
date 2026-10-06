using System.Net.Sockets;
using Darkmount.Keyboard.Lamps;
using Darkmount.Keyboard.Lamps.OpenRgb;

namespace Darkmount.App.LightDevices;

/// <summary>
/// The connection to OpenRGB's SDK server, run on its own thread so a slow, busy or hung OpenRGB can never stall the
/// RGB engine (which also drives the be quiet! keyboard). The engine posts the latest colours per device and reads the
/// device list; this thread connects (retrying every 10 s), re-reads the list when OpenRGB reports a change, and sends —
/// only the newest frame per device, so a slow device skips frames instead of queueing them.
/// </summary>
public sealed class OpenRgbLink : IDisposable
{
    const int RetryMs = 10_000;

    readonly Func<OpenRgbClient?> _connect;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly Lock _gate = new();
    readonly Dictionary<int, Pending> _pending = [];
    readonly HashSet<int> _custom = []; // devices switched to OpenRGB's direct mode (this thread only)
    OpenRgbClient? _client;
    volatile IReadOnlyList<OpenRgbController> _controllers = [];
    volatile bool _connected, _stop, _busy;
    int _generation;

    sealed class Pending
    {
        public int Generation;
        public LampColor[]? Colors;
        public (int Mode, byte[] Raw)? HandBack;
    }

    /// <param name="connect">Opens the connection (tests pass a fake server); default: 127.0.0.1:6742.</param>
    public OpenRgbLink(Func<OpenRgbClient?>? connect = null)
    {
        _connect = connect ?? (() => OpenRgbClient.TryConnect(timeout: TimeSpan.FromSeconds(2)));
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenRGB link" };
        _thread.Start();
    }

    public bool Connected => _connected;

    /// <summary>OpenRGB's devices (empty while not connected). Replaced as a whole; see <see cref="Generation"/>.</summary>
    public IReadOnlyList<OpenRgbController> Controllers => _controllers;

    /// <summary>
    /// Changes whenever <see cref="Controllers"/> is replaced (connected, list re-read, disconnected). Device indices are
    /// only meaningful within one generation: posts tagged with an older one are dropped.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Shows <paramref name="colors"/> (one per LED) on the device; replaces any frame not yet sent.</summary>
    public void Post(int index, int generation, LampColor[] colors)
    {
        lock (_gate)
        {
            var p = Slot(index, generation);
            p.Colors = colors;
        }
        _wake.Set();
    }

    /// <summary>Puts the device back in <paramref name="mode"/> (its own effect) if OverMount had switched it to direct mode.</summary>
    public void PostHandBack(int index, int generation, int mode, byte[] rawModeBlock)
    {
        lock (_gate)
        {
            var p = Slot(index, generation);
            p.Colors = null;
            p.HandBack = (mode, rawModeBlock);
        }
        _wake.Set();
    }

    Pending Slot(int index, int generation)
    {
        if (!_pending.TryGetValue(index, out var p) || p.Generation != generation)
            _pending[index] = p = new Pending { Generation = generation };
        return p;
    }

    /// <summary>For tests: waits until everything posted so far has been sent.</summary>
    internal bool WaitIdle(TimeSpan timeout)
    {
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            lock (_gate) if (_pending.Count == 0 && !_busy) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    void Run()
    {
        long nextTry = 0;
        while (!_stop)
        {
            try
            {
                if (_client is not { Connected: true })
                {
                    if (_client is not null)
                    {
                        Log.Write("OpenRGB disconnected");
                        Disconnect();
                    }
                    if (Environment.TickCount64 >= nextTry)
                    {
                        nextTry = Environment.TickCount64 + RetryMs;
                        _client = _connect();
                        if (_client is not null)
                        {
                            Log.Write($"OpenRGB connected (protocol {_client.ProtocolVersion})");
                            Relist();
                        }
                    }
                    if (_client is null)
                    {
                        lock (_gate) _pending.Clear(); // nothing to send them to
                        _wake.WaitOne(1000);
                        continue;
                    }
                }
                if (_client.DeviceListChanged) Relist();
                Flush();
                _wake.WaitOne(500); // also notices a list change or a closed connection while idle
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                Log.Write($"OpenRGB: {e.Message}");
                Disconnect();
            }
            catch (Exception e)
            {
                Log.Write($"OpenRGB link error: {e}"); // never let this thread die
                Disconnect();
                Thread.Sleep(1000);
            }
        }
        try { if (_client is { Connected: true }) Flush(); } // the last hand-backs (OverMount closing)
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        _client?.Dispose();
        _client = null;
    }

    void Relist()
    {
        var list = _client!.GetControllers();
        lock (_gate)
        {
            _pending.Clear();
            _custom.Clear();
        }
        Publish(list, connected: true);
        Log.Write($"OpenRGB: {list.Count} devices, {list.Count(c => c.IsKeyboardLike)} keyboard-like");
    }

    void Disconnect()
    {
        _client?.Dispose();
        _client = null;
        lock (_gate)
        {
            _pending.Clear();
            _custom.Clear();
        }
        Publish([], connected: false);
    }

    void Publish(IReadOnlyList<OpenRgbController> list, bool connected)
    {
        _controllers = list;
        _connected = connected;
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Sends what the engine posted: hand-backs first, then the newest colours, per device.</summary>
    void Flush()
    {
        while (true)
        {
            int index;
            Pending p;
            lock (_gate)
            {
                if (_pending.Count == 0) { _busy = false; return; }
                (index, p) = _pending.First();
                _pending.Remove(index);
                _busy = true;
            }
            if (p.Generation != Generation) continue; // the device list moved: that index may be another device now
            if (p.HandBack is { } back && _custom.Remove(index)) _client!.UpdateMode(index, back.Mode, back.Raw);
            if (p.Colors is { } colors)
            {
                if (_custom.Add(index)) _client!.SetCustomMode(index);
                _client!.UpdateLeds(index, colors);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        if (!_thread.IsAlive) _wake.Dispose();
    }
}
