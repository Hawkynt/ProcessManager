using System.Diagnostics;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// Drives one probe and keeps the two snapshots every rate is computed from.
/// </summary>
/// <remarks>
/// Deliberately has no timer of its own. Who decides when to sample is a front-end question — the
/// terminal UI blocks on a key with a timeout, the desktop UI runs a background loop — and a sampler
/// that owned a timer would make both of them fight it (PRD §3.5).
/// </remarks>
public sealed class Sampler : IDisposable {

  private readonly ISystemProbe _probe;
  private SystemSnapshot _current = new();
  private SystemSnapshot _previous = new();
  private bool _hasPrevious;

  /// <summary>
  /// The last reading of each process that has ended, while it is still being kept (PRD §14, §87).
  /// </summary>
  /// <remarks>
  /// Held here rather than in the snapshot because the snapshots are swapped and cleared every
  /// sample: a row that outlives its process has to outlive the buffer it was read into.
  /// </remarks>
  private readonly List<ProcessRecord> _tombstones = [];

  /// <summary>
  /// How long a row is kept after the process behind it has gone, in seconds (PRD §87, §14).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Nought, and off by default.</b> A table that keeps its dead is showing something that is not
  /// there, which is a considered thing to ask for and a bad thing to assume — and on a machine that
  /// churns through processes it doubles the table for no benefit to somebody who was not looking
  /// for a process that ended.
  /// </para>
  /// <para>
  /// In seconds and not in samples, like the highlight it pairs with: a row kept "for three samples"
  /// lasts three seconds at one refresh rate and thirty at another, which is not something a person
  /// setting it means.
  /// </para>
  /// </remarks>
  public double KeepExitedSeconds {
    get;
    set {
      field = double.IsFinite(value) && value > 0 ? Math.Min(value, _MaxKeepSeconds) : 0;
      if (field <= 0)
        this._tombstones.Clear();
    }
  }

  /// <summary>An hour. Past that a table of the dead is an event log, and §63 is the event log.</summary>
  private const double _MaxKeepSeconds = 3600;

  /// <summary>
  /// A bound on the count as well as on the age, because the two fail differently.
  /// </summary>
  /// <remarks>
  /// A build machine can start and end a thousand processes a second, and thirty seconds of that is
  /// thirty thousand rows nobody can read. The age is what somebody asked for and this is what keeps
  /// the promise that the table stays a table.
  /// </remarks>
  private const int _MaxTombstones = 2000;

  /// <summary>How many ended rows are being kept, for a test and for the status line.</summary>
  public int RetainedCount => this._tombstones.Count;

  public Sampler(ISystemProbe probe, CpuPercentMode cpuPercentMode = CpuPercentMode.Normalized) {
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;
    this.CpuPercentMode = cpuPercentMode;
  }

  /// <summary>Which convention <see cref="SnapshotDelta.CpuPercent"/> is expressed in.</summary>
  public CpuPercentMode CpuPercentMode { get; set; }

  /// <summary>The most recent snapshot. Invalidated by the next <see cref="Sample"/>.</summary>
  public SystemSnapshot Current => this._current;

  public SnapshotDelta Delta { get; } = new();

  /// <summary>
  /// The top processes behind each sampled CPU, I/O and memory-growth point (PRD §45, §73).
  /// </summary>
  /// <remarks>
  /// Kept with the sampler rather than with the performance window so closing that window does not
  /// erase the evidence that explains the spike somebody opens it to investigate. The capacity is a
  /// little over fifteen minutes at the default one-second interval and is bounded independently of
  /// process count; each slot stores at most five identities per metric.
  /// </remarks>
  public SpikeAttributionHistory Attribution { get; } = new(960);

  /// <summary>
  /// What each program has cost across sessions, or null when nobody asked for it (PRD §44).
  /// </summary>
  /// <remarks>
  /// Off is the default and is the whole design of this feature rather than a preference about it.
  /// Null here means nothing is accumulated and no file is written, so a build nobody has configured
  /// keeps no record of what was run on it.
  /// </remarks>
  public UsageHistory? Usage { get; set; }

  /// <summary>How long the last <see cref="Sample"/> took. Surfaced in the status bar (PRD §4).</summary>
  public TimeSpan LastSampleDuration { get; private set; }

  /// <summary>How many samples have been taken since construction.</summary>
  public long SampleCount { get; private set; }

  /// <summary>
  /// Takes one reading and recomputes the delta. Synchronous, and safe to call from a background
  /// thread as long as only one thread calls it.
  /// </summary>
  public void Sample() {
    // The older of the two buffers becomes the one we fill, so a steady-state sample allocates
    // nothing beyond what a growing process count forces (PRD §4).
    (this._current, this._previous) = (this._previous, this._current);
    this._current.Clear();

    // Stamped before the probe runs, not after: the interval between two samples is then the
    // interval between the two reads that produced them, and a probe that got slower does not
    // silently inflate every rate it produced.
    var startedAt = Stopwatch.GetTimestamp();
    this._current.TimestampTicks = startedAt;
    // Which backend the reading came from, stamped here because this is the one place that knows
    // both the probe and the snapshot. A replay against a recorded tree names the tree, so a bundle
    // and a live reading cannot be confused for one another (PRD §104).
    this._current.Source = this._probe.Description;
    this._probe.Sample(this._current);
    // After the probe, before the delta: a row's parent name is a fact about the table as a whole,
    // and no probe can know it while it is still filling the table in.
    this._current.ResolveParentNames();
    this.LastSampleDuration = Stopwatch.GetElapsedTime(startedAt);

    this.Delta.Update(this._hasPrevious ? this._previous : null, this._current, this.CpuPercentMode);

    // One wall-clock stamp for everything retained from this sample. If attribution and usage each
    // called UtcNow themselves they could disagree across a clock tick about what "this interval"
    // means even though both were derived from the same snapshot pair.
    var utcNow = DateTime.UtcNow.Ticks;
    this.Attribution.Add(this._current, this.Delta, utcNow);

    // Here rather than in each front-end, because this is the one place all three pass through and
    // three copies of "add this interval" would be three chances to add it twice. Null unless
    // somebody asked for it: a file recording which programs a person ran is surveillance if it
    // appears unasked, however useful it is once asked for (PRD §44).
    this.Usage?.Add(this._current, this._hasPrevious ? this.Delta.ElapsedSeconds : 0, utcNow);

    // After the delta, because the delta is what says which processes have gone — and before anything
    // reads the snapshot, because a row that is going to be shown has to be in it (PRD §14, §87).
    this.KeepTheDead();

    this._hasPrevious = true;
    ++this.SampleCount;
  }

  public void Dispose() => this._probe.Dispose();


  /// <summary>
  /// Puts the rows of processes that have ended back into the snapshot, while they are still wanted.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The last reading each one had, taken from the previous snapshot before it is overwritten — not a
  /// fresh reading, because there is nothing left to read. Every rate over a tombstone is unsampled
  /// (see <see cref="SnapshotDelta"/>), so what a kept row shows is what the process had done by the
  /// time it stopped and nothing about the interval since.
  /// </para>
  /// <para>
  /// Ordered oldest first, so the drop when the count bound bites takes the ones that have been dead
  /// longest rather than whichever the dictionary happened to hand over.
  /// </para>
  /// </remarks>
  private void KeepTheDead() {
    if (this.KeepExitedSeconds <= 0)
      return;

    var now = DateTime.UtcNow.Ticks;
    var keepFor = (long)(this.KeepExitedSeconds * TimeSpan.TicksPerSecond);

    // The ones that went this sample, with the reading they last had. The previous snapshot is where
    // it is: the current one no longer contains them, which is what "exited" means.
    if (this._hasPrevious && this.Delta.Exited.Count > 0) {
      var gone = new HashSet<ProcessKey>(this.Delta.Exited);
      var previous = this._previous.Processes;
      for (var i = 0; i < previous.Length; ++i) {
        if (!gone.Contains(previous[i].Key))
          continue;

        var record = previous[i];
        record.ExitedUtcTicks = now;
        this._tombstones.Add(record);
      }
    }

    // Then the ones whose time is up. Both bounds, because they fail differently: the age is what
    // somebody asked for, and the count is what keeps a build machine's thousand-a-second from making
    // the table unreadable.
    this._tombstones.RemoveAll(record => now - record.ExitedUtcTicks >= keepFor);
    if (this._tombstones.Count > _MaxTombstones)
      this._tombstones.RemoveRange(0, this._tombstones.Count - _MaxTombstones);

    if (this._tombstones.Count == 0)
      return;

    var living = this._current.ProcessCount;
    foreach (var tombstone in this._tombstones)
      this._current.AppendProcess() = tombstone;

    // The delta was sized to the snapshot as the probe left it, which was before these rows existed.
    // Without this, asking a kept row for any rate indexes past the end of every array in it — the
    // row would be on screen, look right, and throw the moment a front-end read a percentage off it.
    this.Delta.ExtendForRetainedRows(this._current, living);
  }

}
