using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// One immutable process reading retained in a <see cref="SystemReplayFrame"/>.
/// </summary>
/// <remarks>
/// The absolute <see cref="ProcessRecord"/> is copied together with the rates that only exist in
/// <see cref="SnapshotDelta"/>. A replay frame therefore never has to combine an old snapshot with a
/// newer delta, which would manufacture a state that never existed.
/// </remarks>
public readonly record struct ReplayProcessSample(
  ProcessRecord Record,
  Rate CpuPercent,
  Rate MemoryPercent,
  Rate IoBytesPerSecond,
  Rate GpuPercent
);

/// <summary>A coherent point-in-time copy of the complete sampled system state.</summary>
public sealed class SystemReplayFrame {

  internal SystemReplayFrame(SystemSnapshot snapshot, SnapshotDelta delta, long utcTicks) {
    this.UtcTicks = utcTicks;
    this.Source = snapshot.Source;
    this.System = snapshot.System;
    this.PerCore = snapshot.PerCore.ToArray();
    this.SystemCpuPercent = delta.SystemCpuPercent;
    this.ElapsedSeconds = delta.ElapsedSeconds;

    var processes = snapshot.Processes;
    var copy = new ReplayProcessSample[processes.Length];
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      copy[i] = new(
        process,
        delta.CpuPercent(i),
        delta.MemoryPercent(i),
        delta.IoTotalBytesPerSecond(i),
        delta.GpuPercent(i)
      );
    }

    this.Processes = copy;
  }

  /// <summary>Wall-clock UTC ticks shared by every reading in this frame.</summary>
  public long UtcTicks { get; }

  public DateTime TimestampUtc => new(this.UtcTicks, DateTimeKind.Utc);

  /// <summary>The probe/backend which produced the frame.</summary>
  public string Source { get; }

  /// <summary>All machine-wide absolute counters sampled with this process table.</summary>
  public SystemCounters System { get; }

  /// <summary>Absolute per-core CPU counters sampled with this process table.</summary>
  public IReadOnlyList<CpuTimes> PerCore { get; }

  /// <summary>Busy percentage of the complete machine at this point.</summary>
  public Rate SystemCpuPercent { get; }

  /// <summary>Length of the interval from which rate values in this frame were derived.</summary>
  public double ElapsedSeconds { get; }

  /// <summary>Every process present in the sampled system, in snapshot order.</summary>
  public IReadOnlyList<ReplayProcessSample> Processes { get; }

  public bool TryGetProcess(ProcessKey key, out ReplayProcessSample sample) {
    foreach (var candidate in this.Processes) {
      if (candidate.Record.Key != key)
        continue;

      sample = candidate;
      return true;
    }

    sample = default;
    return false;
  }

}

/// <summary>
/// Bounded, tiered point-in-time history of the complete sampled system state.
/// </summary>
/// <remarks>
/// <para>
/// The recent tier keeps every sample for five minutes. The medium tier keeps one coherent frame per
/// five-second bucket for thirty minutes, and the long tier one per thirty-second bucket for four
/// hours. At the default one-second refresh that is roughly one thousand retained frames rather than
/// fourteen thousand, while preserving second/minute/hour rewind semantics.
/// </para>
/// <para>
/// A frame is created once and may be referenced by more than one tier. No tier splices process or
/// machine values from different samples together. Retention is based on wall-clock ticks supplied
/// by the sampler, so changing the UI refresh interval does not change what "four hours" means.
/// </para>
/// </remarks>
public sealed class SystemReplayHistory {

  private static readonly long _RecentAge = TimeSpan.FromMinutes(5).Ticks;
  private static readonly long _MediumAge = TimeSpan.FromMinutes(30).Ticks;
  private static readonly long _LongAge = TimeSpan.FromHours(4).Ticks;
  private static readonly long _MediumBucket = TimeSpan.FromSeconds(5).Ticks;
  private static readonly long _LongBucket = TimeSpan.FromSeconds(30).Ticks;

  private readonly List<SystemReplayFrame> _recent = [];
  private readonly List<SystemReplayFrame> _medium = [];
  private readonly List<SystemReplayFrame> _long = [];
  private long _mediumBucket = long.MinValue;
  private long _longBucket = long.MinValue;

  /// <summary>Total retained references across tiers. Frames shared by tiers are counted per tier.</summary>
  public int Count => this._recent.Count + this._medium.Count + this._long.Count;

  /// <summary>Oldest timestamp still available, or null before the first sample.</summary>
  public DateTime? OldestUtc {
    get {
      var ticks = this.OldestTicks();
      return ticks == long.MaxValue ? null : new DateTime(ticks, DateTimeKind.Utc);
    }
  }

  /// <summary>Newest timestamp still available, or null before the first sample.</summary>
  public DateTime? NewestUtc
    => this._recent.Count == 0 ? null : this._recent[^1].TimestampUtc;

  /// <summary>Adds one complete sample and applies all retention tiers.</summary>
  public void Add(SystemSnapshot snapshot, SnapshotDelta delta, long utcTicks) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);
    if (utcTicks <= 0)
      throw new ArgumentOutOfRangeException(nameof(utcTicks));

    var frame = new SystemReplayFrame(snapshot, delta, utcTicks);
    this._recent.Add(frame);

    var mediumBucket = utcTicks / _MediumBucket;
    if (mediumBucket != this._mediumBucket) {
      this._medium.Add(frame);
      this._mediumBucket = mediumBucket;
    }

    var longBucket = utcTicks / _LongBucket;
    if (longBucket != this._longBucket) {
      this._long.Add(frame);
      this._longBucket = longBucket;
    }

    Trim(this._recent, utcTicks - _RecentAge);
    Trim(this._medium, utcTicks - _MediumAge);
    Trim(this._long, utcTicks - _LongAge);
  }

  /// <summary>
  /// Finds the retained frame nearest to <paramref name="utc"/> without crossing into the future.
  /// </summary>
  /// <remarks>
  /// Playback uses floor semantics rather than absolute-nearest semantics: asking for 12:00:10 must
  /// not show a process that did not start until 12:00:11. When the request predates retention, the
  /// oldest retained frame is returned so a slider can clamp cleanly to its left edge.
  /// </remarks>
  public SystemReplayFrame? AtOrBefore(DateTime utc) {
    if (this._recent.Count == 0)
      return null;

    var ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
    SystemReplayFrame? best = null;
    FindFloor(this._long, ticks, ref best);
    FindFloor(this._medium, ticks, ref best);
    FindFloor(this._recent, ticks, ref best);
    return best ?? this.OldestFrame();
  }

  /// <summary>Removes all retained playback data.</summary>
  public void Clear() {
    this._recent.Clear();
    this._medium.Clear();
    this._long.Clear();
    this._mediumBucket = long.MinValue;
    this._longBucket = long.MinValue;
  }

  private long OldestTicks()
    => Math.Min(
      this._recent.Count == 0 ? long.MaxValue : this._recent[0].UtcTicks,
      Math.Min(
        this._medium.Count == 0 ? long.MaxValue : this._medium[0].UtcTicks,
        this._long.Count == 0 ? long.MaxValue : this._long[0].UtcTicks
      )
    );

  private SystemReplayFrame OldestFrame() {
    var oldest = this._recent[0];
    if (this._medium.Count > 0 && this._medium[0].UtcTicks < oldest.UtcTicks)
      oldest = this._medium[0];
    if (this._long.Count > 0 && this._long[0].UtcTicks < oldest.UtcTicks)
      oldest = this._long[0];
    return oldest;
  }

  private static void Trim(List<SystemReplayFrame> frames, long minimumTicks) {
    var remove = 0;
    while (remove < frames.Count && frames[remove].UtcTicks < minimumTicks)
      ++remove;
    if (remove > 0)
      frames.RemoveRange(0, remove);
  }

  private static void FindFloor(List<SystemReplayFrame> frames, long ticks, ref SystemReplayFrame? best) {
    var lo = 0;
    var hi = frames.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var candidate = frames[mid];
      if (candidate.UtcTicks <= ticks) {
        if (best is null || candidate.UtcTicks > best.UtcTicks)
          best = candidate;
        lo = mid + 1;
      } else
        hi = mid - 1;
    }
  }

}
