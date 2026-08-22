using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>Which series a per-process history holds.</summary>
public enum HistorySeries : byte { Cpu, Memory, Io }

/// <summary>
/// A short rolling history per process, for the in-row sparklines.
/// </summary>
/// <remarks>
/// <para>
/// PRD §3.3 says history is kept only for the processes somebody is looking at, and this is what
/// enforces it: rings exist for the rows a front-end says are on screen, and are dropped a few
/// samples after they stop being. Keeping 600 samples × 1000 processes × 3 series would be 14 MB of
/// numbers nobody reads, and re-allocating that every second would be worse.
/// </para>
/// <para>
/// The rings are short on purpose — a sparkline is forty pixels wide, so sixty samples is already
/// more than it can show.
/// </para>
/// <para>
/// Which field feeds which ring is not decided here: the catalogue declares it, as
/// <see cref="Query.FieldDescriptor.Series"/> on <c>cpu</c>, <c>private</c> and <c>io.total</c>, and
/// on the three drawn columns that plot them. A test reads the declaration and checks this class
/// against it, so a ring cannot quietly come to hold something other than the column it is drawn
/// beside (PRD §5.1).
/// </para>
/// </remarks>
public sealed class ProcessHistory {

  private const int _Capacity = 64;
  private const int _GraceSamples = 4;

  private sealed class Entry {
    public readonly HistoryRing<Rate> Cpu = new(_Capacity);
    public readonly HistoryRing<Rate> Memory = new(_Capacity);
    public readonly HistoryRing<Rate> Io = new(_Capacity);
    public int LastSeen;
  }

  private readonly Dictionary<ProcessKey, Entry> _entries = [];
  private readonly List<ProcessKey> _stale = [];
  private int _generation;

  // One scale per series, shared by every row and decayed rather than reset. Shared, because the
  // point of a column of plots is that the rows can be compared with each other — a plot scaled to
  // its own row's maximum makes an idle process look exactly as busy as a saturated one. Decayed,
  // because a scale that snapped to each sample's peak would make every plot jump whenever one
  // process spiked. And floored, because on an idle machine the peak is noise, and amplifying noise
  // to full height is how a monitor cries wolf.
  private const double _CpuFloor = 5;                     // percent
  private const double _MemoryFloor = 32 * 1024 * 1024;   // bytes
  private const double _IoFloor = 64 * 1024;              // bytes per second

  /// <summary>The busiest CPU reading recently seen, in percent; the top of the CPU sparklines.</summary>
  public double CpuScale { get; private set; } = _CpuFloor;

  /// <summary>The largest private-memory reading recently seen, in bytes.</summary>
  public double MemoryScale { get; private set; } = _MemoryFloor;

  /// <summary>The largest byte rate recently seen.</summary>
  public double IoScale { get; private set; } = _IoFloor;

  /// <summary>How many processes are being tracked.</summary>
  public int Count => this._entries.Count;

  /// <summary>
  /// Appends a sample for the rows a front-end is showing.
  /// </summary>
  /// <param name="snapshot">The snapshot just taken.</param>
  /// <param name="delta">Its delta, for the rates.</param>
  /// <param name="view">The rebuilt view; only its rows are tracked.</param>
  /// <param name="first">Index of the first row on screen.</param>
  /// <param name="count">How many rows are on screen.</param>
  public void Update(SystemSnapshot snapshot, SnapshotDelta delta, ProcessView view, int first, int count) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);
    ArgumentNullException.ThrowIfNull(view);

    ++this._generation;
    var processes = snapshot.Processes;
    var rows = view.Rows;
    var last = Math.Min(rows.Length, first + count);
    var peakCpu = _CpuFloor;
    var peakMemory = _MemoryFloor;
    var peakIo = _IoFloor;

    for (var i = Math.Max(0, first); i < last; ++i) {
      // A grouping heading occupies a row and is not a process; there is nothing to keep a history
      // of, and its index into the snapshot is deliberately invalid (PRD §83).
      if (rows[i].IsGroupHeader)
        continue;

      var index = rows[i].Index;
      ref readonly var process = ref processes[index];
      if (!this._entries.TryGetValue(process.Key, out var entry)) {
        entry = new();
        this._entries[process.Key] = entry;
      }

      entry.LastSeen = this._generation;

      // Raw readings, not fractions: the scale is decided once, below, for all rows together.
      var cpu = delta.CpuPercent(index);
      entry.Cpu.Add(cpu);
      if (cpu.HasValue)
        peakCpu = Math.Max(peakCpu, cpu.Value);

      entry.Memory.Add(process.PrivateBytes.HasValue ? Rate.Of(process.PrivateBytes.Value) : Rate.Gap);
      if (process.PrivateBytes.HasValue)
        peakMemory = Math.Max(peakMemory, process.PrivateBytes.Value);

      // The same total the "I/O total rate" column shows, and read from the same place: this used to
      // add read and write here and leave out the third figure Windows keeps, so the plot and the
      // column beside it were two different numbers under one name. Which field feeds which ring is
      // declared in the catalogue now (PRD §5.1) — <c>io.total</c> for this one — and a test holds
      // the two together.
      var io = delta.IoTotalBytesPerSecond(index);
      entry.Io.Add(io);
      if (io.HasValue)
        peakIo = Math.Max(peakIo, io.Value);
    }

    this.CpuScale = Math.Max(peakCpu, this.CpuScale * 0.92);
    this.MemoryScale = Math.Max(peakMemory, this.MemoryScale * 0.92);
    this.IoScale = Math.Max(peakIo, this.IoScale * 0.92);
    this.Prune();
  }

  /// <summary>The ring for one process and series, or null when it is not being tracked.</summary>
  public HistoryRing<Rate>? Get(ProcessKey key, HistorySeries series) {
    if (!this._entries.TryGetValue(key, out var entry))
      return null;

    return series switch {
      HistorySeries.Cpu => entry.Cpu,
      HistorySeries.Memory => entry.Memory,
      _ => entry.Io,
    };
  }

  /// <summary>The top of the scale for a series — shared by every row, so rows compare.</summary>
  public double ScaleOf(HistorySeries series) => series switch {
    HistorySeries.Cpu => this.CpuScale,
    HistorySeries.Memory => this.MemoryScale,
    _ => this.IoScale,
  };

  /// <summary>What the top of a series' scale currently is, for a caption or a tooltip.</summary>
  public string DescribeScale(HistorySeries series) => series switch {
    HistorySeries.Cpu => $"{this.CpuScale:0.#} %",
    HistorySeries.Memory => Query.Humanize.Bytes(Model.Counter.Of((ulong)this.MemoryScale)),
    _ => Query.Humanize.Bytes(Model.Counter.Of((ulong)this.IoScale)) + "/s",
  };

  private void Prune() {
    // A few samples of grace, so scrolling one row and back does not throw away the history that was
    // just being drawn.
    this._stale.Clear();
    foreach (var (key, entry) in this._entries)
      if (this._generation - entry.LastSeen > _GraceSamples)
        this._stale.Add(key);

    foreach (var key in this._stale)
      this._entries.Remove(key);
  }

}
