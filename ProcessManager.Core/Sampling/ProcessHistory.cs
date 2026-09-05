using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>Which series a per-process history holds.</summary>
public enum HistorySeries : byte { Cpu, Memory, Io, Gpu }

/// <summary>
/// A short rolling history per process, for the in-row sparklines.
/// </summary>
/// <remarks>
/// <para>
/// Every process in every sample is tracked, not merely rows which happen to be visible. Scrolling
/// therefore reveals the same recent history that would have been visible had the row stayed on
/// screen, and the table history is useful as the short-range projection of the system recorder.
/// </para>
/// <para>
/// The rings stay deliberately short: a table sparkline is only a few dozen pixels wide. Longer
/// replay belongs to <see cref="SystemReplayHistory"/>, whose tiered retention does not multiply a
/// one-second ring by every process for hours.
/// </para>
/// </remarks>
public sealed class ProcessHistory {

  private const int _Capacity = 64;
  private const int _GraceSamples = 4;

  private sealed class Entry {
    public readonly HistoryRing<Rate> Cpu = new(_Capacity);
    public readonly HistoryRing<Rate> Memory = new(_Capacity);
    public readonly HistoryRing<Rate> Io = new(_Capacity);
    public readonly HistoryRing<Rate> Gpu = new(_Capacity);
    public int LastSeen;
  }

  private readonly Dictionary<ProcessKey, Entry> _entries = [];
  private readonly List<ProcessKey> _stale = [];
  private int _generation;

  private const double _CpuFloor = 5;
  private const double _MemoryFloor = 32 * 1024 * 1024;
  private const double _IoFloor = 64 * 1024;
  private const double _GpuFloor = 5;

  public double CpuScale { get; private set; } = _CpuFloor;
  public double MemoryScale { get; private set; } = _MemoryFloor;
  public double IoScale { get; private set; } = _IoFloor;
  public double GpuScale { get; private set; } = _GpuFloor;

  /// <summary>How many processes are being tracked.</summary>
  public int Count => this._entries.Count;

  /// <summary>
  /// Appends one coherent sample for every process in <paramref name="snapshot"/>.
  /// </summary>
  /// <remarks>
  /// The view/viewport arguments are retained for source compatibility with existing front-ends;
  /// they no longer determine which processes get history. A monitor cannot truthfully rewind a row
  /// which it decided not to sample merely because that row was below the fold.
  /// </remarks>
  public void Update(SystemSnapshot snapshot, SnapshotDelta delta, ProcessView view, int first, int count) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);
    ArgumentNullException.ThrowIfNull(view);
    _ = first;
    _ = count;

    ++this._generation;
    var processes = snapshot.Processes;
    var peakCpu = _CpuFloor;
    var peakMemory = _MemoryFloor;
    var peakIo = _IoFloor;
    var peakGpu = _GpuFloor;

    for (var index = 0; index < processes.Length; ++index) {
      ref readonly var process = ref processes[index];
      if (!this._entries.TryGetValue(process.Key, out var entry)) {
        entry = new();
        this._entries[process.Key] = entry;
      }

      entry.LastSeen = this._generation;

      var cpu = process.HasExited ? Rate.NotSampledYet : delta.CpuPercent(index);
      entry.Cpu.Add(cpu);
      if (cpu.HasValue)
        peakCpu = Math.Max(peakCpu, cpu.Value);

      var memory = process.PrivateBytes.HasValue ? Rate.Of(process.PrivateBytes.Value) : Rate.Gap;
      entry.Memory.Add(memory);
      if (memory.HasValue)
        peakMemory = Math.Max(peakMemory, memory.Value);

      var io = process.HasExited ? Rate.NotSampledYet : delta.IoTotalBytesPerSecond(index);
      entry.Io.Add(io);
      if (io.HasValue)
        peakIo = Math.Max(peakIo, io.Value);

      var gpu = process.HasExited ? Rate.NotSampledYet : delta.GpuPercent(index);
      entry.Gpu.Add(gpu);
      if (gpu.HasValue)
        peakGpu = Math.Max(peakGpu, gpu.Value);
    }

    this.CpuScale = Math.Max(peakCpu, this.CpuScale * 0.92);
    this.MemoryScale = Math.Max(peakMemory, this.MemoryScale * 0.92);
    this.IoScale = Math.Max(peakIo, this.IoScale * 0.92);
    this.GpuScale = Math.Max(peakGpu, this.GpuScale * 0.92);
    this.Prune();
  }

  /// <summary>The ring for one process and series, or null when it has not been seen recently.</summary>
  public HistoryRing<Rate>? Get(ProcessKey key, HistorySeries series) {
    if (!this._entries.TryGetValue(key, out var entry))
      return null;

    return series switch {
      HistorySeries.Cpu => entry.Cpu,
      HistorySeries.Memory => entry.Memory,
      HistorySeries.Io => entry.Io,
      HistorySeries.Gpu => entry.Gpu,
      _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
    };
  }

  /// <summary>The top of the scale for a series — shared by every row, so rows compare.</summary>
  public double ScaleOf(HistorySeries series) => series switch {
    HistorySeries.Cpu => this.CpuScale,
    HistorySeries.Memory => this.MemoryScale,
    HistorySeries.Io => this.IoScale,
    HistorySeries.Gpu => this.GpuScale,
    _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
  };

  /// <summary>What the top of a series' scale currently is, for a caption or tooltip.</summary>
  public string DescribeScale(HistorySeries series) => series switch {
    HistorySeries.Cpu => $"{this.CpuScale:0.#} %",
    HistorySeries.Memory => Query.Humanize.Bytes(Model.Counter.Of((ulong)this.MemoryScale)),
    HistorySeries.Io => Query.Humanize.Bytes(Model.Counter.Of((ulong)this.IoScale)) + "/s",
    HistorySeries.Gpu => $"{this.GpuScale:0.#} %",
    _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
  };

  private void Prune() {
    this._stale.Clear();
    foreach (var (key, entry) in this._entries)
      if (this._generation - entry.LastSeen > _GraceSamples)
        this._stale.Add(key);

    foreach (var key in this._stale)
      this._entries.Remove(key);
  }

}
