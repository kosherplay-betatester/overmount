using System.Collections;
using Darkmount.Sensors;

namespace Darkmount.Screens;

/// <summary>Fixed-capacity ring buffer of the most recent values, enumerated oldest → newest.</summary>
public sealed class RingSeries : IReadOnlyList<double?>
{
    private readonly double?[] _items;
    private int _start;

    public RingSeries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new double?[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public double? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Add(double? value)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = value;
            Count++;
        }
        else
        {
            _items[_start] = value;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public IEnumerator<double?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Recent history of the graphed metrics (one sample per frame, default 60 samples ≈ 2 minutes).</summary>
public sealed class MetricHistory
{
    private readonly RingSeries _cpuTemp, _cpuLoad, _cpuPower, _gpuTemp, _gpuLoad, _gpuPower, _fps, _fpsLow, _netDown, _netUp;

    public MetricHistory(int capacity = 60)
    {
        Capacity = capacity;
        _cpuTemp = new(capacity);
        _cpuLoad = new(capacity);
        _cpuPower = new(capacity);
        _gpuTemp = new(capacity);
        _gpuLoad = new(capacity);
        _gpuPower = new(capacity);
        _fps = new(capacity);
        _fpsLow = new(capacity);
        _netDown = new(capacity);
        _netUp = new(capacity);
    }

    public int Capacity { get; }

    public IReadOnlyList<double?> CpuTemp => _cpuTemp;
    public IReadOnlyList<double?> CpuLoad => _cpuLoad;
    public IReadOnlyList<double?> GpuTemp => _gpuTemp;
    public IReadOnlyList<double?> GpuLoad => _gpuLoad;
    public IReadOnlyList<double?> CpuPower => _cpuPower;
    public IReadOnlyList<double?> GpuPower => _gpuPower;
    public IReadOnlyList<double?> Fps => _fps;
    public IReadOnlyList<double?> FpsLow => _fpsLow;

    /// <summary>Download rate in bytes/s, fed by <see cref="AddNetwork"/> (independent of <see cref="Add"/>).</summary>
    public IReadOnlyList<double?> NetDown => _netDown;

    /// <summary>Upload rate in bytes/s, fed by <see cref="AddNetwork"/> (independent of <see cref="Add"/>).</summary>
    public IReadOnlyList<double?> NetUp => _netUp;

    public void Add(Snapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _cpuTemp.Add(s.CpuTemp);
        _cpuLoad.Add(s.CpuLoad);
        _cpuPower.Add(s.CpuPower);
        _gpuTemp.Add(s.GpuTemp);
        _gpuLoad.Add(s.GpuLoad);
        _gpuPower.Add(s.GpuPower);
        _fps.Add(s.Fps);
        _fpsLow.Add(s.FpsLow);
    }

    /// <summary>Appends one network sample in bytes/s (call once per frame, alongside <see cref="Add"/>).</summary>
    public void AddNetwork(double down, double up)
    {
        _netDown.Add(down);
        _netUp.Add(up);
    }

    /// <summary>Forgets the frame-rate history (call when a game ends).</summary>
    public void ClearFps()
    {
        _fps.Clear();
        _fpsLow.Clear();
    }
}
