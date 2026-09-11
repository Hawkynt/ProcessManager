using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// The part of a process sample that is useful when reconstructing an old system state.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="ProcessRecord"/>. That record is roughly two kilobytes now because
/// it carries every inspectable fact about a process, including static image/security metadata. A
/// playback row keeps identity/topology plus the changing quantities somebody investigating an old
/// frame can actually compare. Copying the complete record once per process per second would turn a
/// monitor into an in-memory database and break the sampling allocation budget (PRD §4, §71).
/// </remarks>
public readonly struct PlaybackProcessSample {

  private readonly ulong _privateBytes;
  private readonly ulong _workingSetBytes;
  private readonly float _cpuPercent;
  private readonly double _ioBytesPerSecond;
  private readonly float _gpuPercent;
  private readonly UnknownReason _privateBytesReason;
  private readonly UnknownReason _workingSetReason;
  private readonly UnknownReason _cpuReason;
  private readonly UnknownReason _ioReason;
  private readonly UnknownReason _gpuReason;

  internal PlaybackProcessSample(in ProcessRecord process, SnapshotDelta delta, int index) {
    this.Key = process.Key;
    this.ParentPid = process.ParentPid;
    this.Name = process.Name ?? string.Empty;
    this.UserName = process.UserName;
    this.State = process.State;
    this.ThreadCount = process.ThreadCount;

    this._privateBytesReason = process.PrivateBytes.Reason;
    this._privateBytes = process.PrivateBytes.GetValueOrDefault();
    this._workingSetReason = process.WorkingSetBytes.Reason;
    this._workingSetBytes = process.WorkingSetBytes.GetValueOrDefault();

    var cpu = delta.CpuPercent(index);
    this._cpuReason = cpu.Reason;
    this._cpuPercent = cpu.HasValue ? (float)cpu.Value : 0;

    var io = delta.IoTotalBytesPerSecond(index);
    this._ioReason = io.Reason;
    this._ioBytesPerSecond = io.GetValueOrDefault();

    var gpu = delta.GpuPercent(index);
    this._gpuReason = gpu.Reason;
    this._gpuPercent = gpu.HasValue ? (float)gpu.Value : 0;
  }

  public ProcessKey Key { get; }
  public int ParentPid { get; }
  public string Name { get; }
  public string? UserName { get; }
  public ProcessState State { get; }
  public int ThreadCount { get; }

  public Counter PrivateBytes => Counter(this._privateBytes, this._privateBytesReason);
  public Counter WorkingSetBytes => Counter(this._workingSetBytes, this._workingSetReason);
  public Rate CpuPercent => Rate(this._cpuPercent, this._cpuReason);
  public Rate IoBytesPerSecond => Rate(this._ioBytesPerSecond, this._ioReason);
  public Rate GpuPercent => Rate(this._gpuPercent, this._gpuReason);

  private static Counter Counter(ulong value, UnknownReason reason)
    => reason == UnknownReason.None ? Model.Counter.Of(value) : Model.Counter.Unknown(reason);

  private static Rate Rate(double value, UnknownReason reason)
    => reason == UnknownReason.None ? Model.Rate.Of(value) : Model.Rate.Unknown(reason);

}

/// <summary>
/// A lightweight cursor over one retained historical frame.
/// </summary>
/// <remarks>
/// The cursor owns no arrays. Its process span points into the recorder's preallocated ring and is
/// valid until that slot is eventually overwritten. UI code consumes it synchronously; keeping one
/// for minutes instead of seeking again is deliberately unsupported.
/// </remarks>
public readonly struct SystemPlaybackFrame {

  private readonly PlaybackTier? _owner;
  private readonly int _slot;
  private readonly long _utcTicks;

  internal SystemPlaybackFrame(PlaybackTier owner, int slot) {
    this._owner = owner;
    this._slot = slot;
    this._utcTicks = owner.UtcTicks(slot);
  }

  public bool IsValid => this._owner is not null && this._owner.UtcTicks(this._slot) == this._utcTicks;
  public long UtcTicks => this._utcTicks;
  public DateTime TimestampUtc => new(this._utcTicks, DateTimeKind.Utc);
  public string Source => this.IsValid ? this._owner!.Source(this._slot) : string.Empty;
  public SystemCounters System => this.IsValid ? this._owner!.System(this._slot) : SystemCounters.Unread;
  public Rate SystemCpuPercent => this.IsValid ? this._owner!.SystemCpuPercent(this._slot) : Rate.NotSampledYet;
  public ReadOnlySpan<PlaybackProcessSample> Processes
    => this.IsValid ? this._owner!.Processes(this._slot) : [];

}

/// <summary>
/// Bounded diagnostic playback for the system state sampled while the desktop monitor is running.
/// </summary>
/// <remarks>
/// <para>
/// Storage is three preallocated rings. Recent history gets the densest points, medium history fewer,
/// and the hours tier fewer again. Each point is still a coherent sample: thinning chooses a whole
/// frame to keep; it never combines CPU from one second with memory from another.
/// </para>
/// <para>
/// The total number of retained process slots is bounded. On machines with many processes the same
/// two-minute / thirty-minute / four-hour coverage is preserved by widening the time buckets rather
/// than multiplying memory by process count. A thousand-process workstation therefore gives up some
/// old-point resolution instead of allocating gigabytes.
/// </para>
/// <para>
/// Disabled until a front-end asks for it. The desktop enables it before its first sample; the TUI,
/// which currently has no playback surface, does not pay tens of megabytes for data it cannot read.
/// </para>
/// </remarks>
public sealed class SystemPlaybackHistory {

  private const int _TargetRecentFrames = 120;
  private const int _TargetMediumFrames = 168;
  private const int _TargetLongFrames = 210;
  private const int _TargetFrames = _TargetRecentFrames + _TargetMediumFrames + _TargetLongFrames;
  private const int _MinimumFramesPerTier = 2;

  /// <summary>
  /// Upper bound on process-row slots across all tiers. A compact row is intentionally small; this
  /// still keeps playback in the tens-of-megabytes class instead of the gigabytes of full records.
  /// </summary>
  public const int MaxRetainedProcessSlots = 524_288;

  private static readonly long _RecentCoverage = TimeSpan.FromMinutes(2).Ticks;
  private static readonly long _MediumCoverage = TimeSpan.FromMinutes(30).Ticks;
  private static readonly long _LongCoverage = TimeSpan.FromHours(4).Ticks;

  private PlaybackTier? _recent;
  private PlaybackTier? _medium;
  private PlaybackTier? _long;
  private int _processCapacity;

  public int RetainedFrameCount
    => (this._recent?.Count ?? 0) + (this._medium?.Count ?? 0) + (this._long?.Count ?? 0);

  public int RetainedProcessSlotCapacity
    => (this._recent?.RowCapacity ?? 0) + (this._medium?.RowCapacity ?? 0) + (this._long?.RowCapacity ?? 0);

  public int ProcessCapacity => this._processCapacity;

  public DateTime? OldestUtc {
    get {
      var ticks = Math.Min(
        this._recent?.OldestUtcTicks ?? long.MaxValue,
        Math.Min(this._medium?.OldestUtcTicks ?? long.MaxValue, this._long?.OldestUtcTicks ?? long.MaxValue)
      );
      return ticks == long.MaxValue ? null : new DateTime(ticks, DateTimeKind.Utc);
    }
  }

  public DateTime? NewestUtc {
    get {
      var ticks = Math.Max(
        this._recent?.NewestUtcTicks ?? 0,
        Math.Max(this._medium?.NewestUtcTicks ?? 0, this._long?.NewestUtcTicks ?? 0)
      );
      return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }
  }

  public void Add(SystemSnapshot snapshot, SnapshotDelta delta, long utcTicks) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(utcTicks, 0);

    this.EnsureStorage(snapshot.ProcessCount);
    this._recent!.TryAdd(snapshot, delta, utcTicks);
    this._medium!.TryAdd(snapshot, delta, utcTicks);
    this._long!.TryAdd(snapshot, delta, utcTicks);
  }

  /// <summary>
  /// Finds the newest retained frame which already existed at <paramref name="utc"/>.
  /// </summary>
  /// <remarks>
  /// Floor semantics matter for forensics: asking for 14:03:10 must never show a process that did
  /// not start until 14:03:11 merely because that later frame was numerically closer.
  /// </remarks>
  public bool TryAtOrBefore(DateTime utc, out SystemPlaybackFrame frame) {
    var ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
    var bestTicks = 0L;
    PlaybackTier? bestTier = null;
    var bestSlot = -1;

    this._recent?.FindFloor(ticks, ref bestTicks, ref bestTier, ref bestSlot);
    this._medium?.FindFloor(ticks, ref bestTicks, ref bestTier, ref bestSlot);
    this._long?.FindFloor(ticks, ref bestTicks, ref bestTier, ref bestSlot);

    if (bestTier is not null) {
      frame = new(bestTier, bestSlot);
      return true;
    }

    // Before the left edge: clamp to the oldest retained sample, like a playback slider.
    var oldestTicks = long.MaxValue;
    this._recent?.FindOldest(ref oldestTicks, ref bestTier, ref bestSlot);
    this._medium?.FindOldest(ref oldestTicks, ref bestTier, ref bestSlot);
    this._long?.FindOldest(ref oldestTicks, ref bestTier, ref bestSlot);
    if (bestTier is not null) {
      frame = new(bestTier, bestSlot);
      return true;
    }

    frame = default;
    return false;
  }

  public bool TryNewest(out SystemPlaybackFrame frame) {
    if (this.NewestUtc is { } utc)
      return this.TryAtOrBefore(utc, out frame);

    frame = default;
    return false;
  }

  public void Clear() {
    this._recent?.Clear();
    this._medium?.Clear();
    this._long?.Clear();
  }

  private void EnsureStorage(int processCount) {
    if (this._recent is null) {
      this.BuildStorage(GrowProcessCapacity(processCount));
      return;
    }

    if (processCount <= this._processCapacity)
      return;

    // Process-count jumps are rare and are the one allowed growth path. Reconfigure the rings rather
    // than letting row capacity grow without bound. Old frames cannot be re-packed without another
    // full copy of the old store, so a scale change starts a new playback epoch; the UI names the new
    // left edge rather than pretending older data still exists.
    this.BuildStorage(GrowProcessCapacity(processCount));
  }

  private void BuildStorage(int processCapacity) {
    this._processCapacity = processCapacity;
    var totalFrames = Math.Clamp(MaxRetainedProcessSlots / Math.Max(1, processCapacity), 3 * _MinimumFramesPerTier, _TargetFrames);

    var recentFrames = Math.Max(_MinimumFramesPerTier, totalFrames * _TargetRecentFrames / _TargetFrames);
    var mediumFrames = Math.Max(_MinimumFramesPerTier, totalFrames * _TargetMediumFrames / _TargetFrames);
    var longFrames = totalFrames - recentFrames - mediumFrames;
    if (longFrames < _MinimumFramesPerTier) {
      var missing = _MinimumFramesPerTier - longFrames;
      while (missing > 0 && mediumFrames > _MinimumFramesPerTier) {
        --mediumFrames;
        --missing;
      }
      while (missing > 0 && recentFrames > _MinimumFramesPerTier) {
        --recentFrames;
        --missing;
      }
      longFrames = _MinimumFramesPerTier;
    }

    this._recent = new(recentFrames, processCapacity, BucketFor(_RecentCoverage, recentFrames, TimeSpan.TicksPerSecond));
    this._medium = new(mediumFrames, processCapacity, BucketFor(_MediumCoverage, mediumFrames, TimeSpan.FromSeconds(10).Ticks));
    this._long = new(longFrames, processCapacity, BucketFor(_LongCoverage, longFrames, TimeSpan.FromMinutes(1).Ticks));
  }

  private static long BucketFor(long coverageTicks, int frames, long floorTicks)
    => Math.Max(floorTicks, (coverageTicks + frames - 1) / frames);

  private static int GrowProcessCapacity(int count) {
    var wanted = Math.Max(128, count);
    var capacity = 128;
    while (capacity < wanted && capacity <= 1 << 29)
      capacity <<= 1;

    // If the current table already occupies more than three quarters of the bucket, reserve the next
    // one now. This turns normal process churn into overwrites, not a hundred-megabyte reallocation.
    if (wanted > capacity * 3 / 4 && capacity <= 1 << 29)
      capacity <<= 1;

    return capacity;
  }

}

/// <summary>One resolution tier of <see cref="SystemPlaybackHistory"/>.</summary>
internal sealed class PlaybackTier {

  private struct Metadata {
    public long UtcTicks;
    public int ProcessCount;
    public string Source;
    public SystemCounters System;
    public Rate SystemCpuPercent;
  }

  private readonly Metadata[] _frames;
  private readonly PlaybackProcessSample[] _rows;
  private readonly int _processCapacity;
  private readonly long _bucketTicks;
  private long _lastBucket = long.MinValue;
  private int _next;
  private int _count;

  public PlaybackTier(int capacity, int processCapacity, long bucketTicks) {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(processCapacity, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(bucketTicks, 1);

    this.Capacity = capacity;
    this._processCapacity = processCapacity;
    this._bucketTicks = bucketTicks;
    this._frames = new Metadata[capacity];
    this._rows = new PlaybackProcessSample[checked(capacity * processCapacity)];
  }

  public int Capacity { get; }
  public int Count => this._count;
  public int RowCapacity => this._rows.Length;

  public long OldestUtcTicks {
    get {
      if (this._count == 0)
        return long.MaxValue;
      return this._frames[this.SlotAtAge(this._count - 1)].UtcTicks;
    }
  }

  public long NewestUtcTicks => this._count == 0 ? 0 : this._frames[this.SlotAtAge(0)].UtcTicks;

  public void TryAdd(SystemSnapshot snapshot, SnapshotDelta delta, long utcTicks) {
    var bucket = utcTicks / this._bucketTicks;
    if (bucket == this._lastBucket)
      return;

    this._lastBucket = bucket;
    var slot = this._next;
    ref var frame = ref this._frames[slot];
    var previousCount = frame.ProcessCount;
    var processes = snapshot.Processes;

    var rows = this._rows.AsSpan(slot * this._processCapacity, this._processCapacity);
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      rows[i] = new(in process, delta, i);
    }

    if (previousCount > processes.Length)
      rows.Slice(processes.Length, previousCount - processes.Length).Clear();

    frame.UtcTicks = utcTicks;
    frame.ProcessCount = processes.Length;
    frame.Source = snapshot.Source;
    frame.System = snapshot.System;
    frame.SystemCpuPercent = delta.SystemCpuPercent;

    this._next = (slot + 1) % this.Capacity;
    if (this._count < this.Capacity)
      ++this._count;
  }

  public long UtcTicks(int slot) => this._frames[slot].UtcTicks;
  public string Source(int slot) => this._frames[slot].Source ?? string.Empty;
  public SystemCounters System(int slot) => this._frames[slot].System;
  public Rate SystemCpuPercent(int slot) => this._frames[slot].SystemCpuPercent;

  public ReadOnlySpan<PlaybackProcessSample> Processes(int slot) {
    ref readonly var frame = ref this._frames[slot];
    return this._rows.AsSpan(slot * this._processCapacity, frame.ProcessCount);
  }

  public void FindFloor(long ticks, ref long bestTicks, ref PlaybackTier? bestTier, ref int bestSlot) {
    for (var age = 0; age < this._count; ++age) {
      var slot = this.SlotAtAge(age);
      var candidate = this._frames[slot].UtcTicks;
      if (candidate <= ticks && candidate > bestTicks) {
        bestTicks = candidate;
        bestTier = this;
        bestSlot = slot;
      }
    }
  }

  public void FindOldest(ref long oldestTicks, ref PlaybackTier? bestTier, ref int bestSlot) {
    if (this._count == 0)
      return;

    var slot = this.SlotAtAge(this._count - 1);
    var ticks = this._frames[slot].UtcTicks;
    if (ticks < oldestTicks) {
      oldestTicks = ticks;
      bestTier = this;
      bestSlot = slot;
    }
  }

  public void Clear() {
    this._frames.AsSpan().Clear();
    this._rows.AsSpan().Clear();
    this._lastBucket = long.MinValue;
    this._next = 0;
    this._count = 0;
  }

  private int SlotAtAge(int age) {
    var newest = (this._next - 1 + this.Capacity) % this.Capacity;
    return (newest - age + this.Capacity) % this.Capacity;
  }

}
