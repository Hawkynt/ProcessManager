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
    this._probe.Sample(this._current);
    // After the probe, before the delta: a row's parent name is a fact about the table as a whole,
    // and no probe can know it while it is still filling the table in.
    this._current.ResolveParentNames();
    this.LastSampleDuration = Stopwatch.GetElapsedTime(startedAt);

    this.Delta.Update(this._hasPrevious ? this._previous : null, this._current, this.CpuPercentMode);

    // Here rather than in each front-end, because this is the one place all three pass through and
    // three copies of "add this interval" would be three chances to add it twice. Null unless
    // somebody asked for it: a file recording which programs a person ran is surveillance if it
    // appears unasked, however useful it is once asked for (PRD §44).
    this.Usage?.Add(this._current, this._hasPrevious ? this.Delta.ElapsedSeconds : 0, DateTime.UtcNow.Ticks);

    this._hasPrevious = true;
    ++this.SampleCount;
  }

  public void Dispose() => this._probe.Dispose();

}
