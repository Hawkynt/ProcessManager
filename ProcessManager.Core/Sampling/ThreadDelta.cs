using System.Diagnostics;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>
/// The per-thread figures that needed two readings to know: how much of a processor each thread is
/// using, and how often it is being switched (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the probe, for the same reason <see cref="SnapshotDelta"/> is: a probe reports
/// counters and nothing else, so that there is exactly one place in the program where a division
/// happens and exactly one place a rate can be wrong (PRD §2, §3.2).
/// </para>
/// <para>
/// The thread view is filled on demand rather than on the sampling tick, so the interval between two
/// updates is however long somebody left the tab open — a minute, or a quarter of a second. That is
/// why the elapsed time is measured on the monotonic clock at each update instead of being assumed
/// to be the sample interval: a percentage over an interval nobody measured is a percentage of
/// nothing.
/// </para>
/// <para>
/// Indexed by position in the list handed to <see cref="Update"/>, so a caller already walking the
/// threads pays nothing to ask for the matching rate.
/// </para>
/// </remarks>
public sealed class ThreadDelta {

  /// <summary>
  /// What one thread read last time.
  /// </summary>
  /// <remarks>
  /// Keyed on the thread's start time as well as its id, because Linux reuses thread ids as freely as
  /// it reuses process ids: a pool that ends a worker and starts another gets the same number back,
  /// and charging the new thread with the old one's CPU time would show a thread that has been busy
  /// since before it existed (PRD §8.2).
  /// </remarks>
  private readonly record struct Reading(long Timestamp, Counter CpuTimeNs, Counter ContextSwitches);

  private readonly Dictionary<(int Tid, long StartTimeUtcTicks), Reading> _previous = [];
  private readonly HashSet<(int Tid, long StartTimeUtcTicks)> _seen = [];
  private Rate[] _cpuPercent = [];
  private Rate[] _cpuPercentPerCore = [];
  private Rate[] _contextSwitchesPerSecond = [];
  private ProcessKey _key = ProcessKey.None;
  private int _count;

  /// <summary>False until the same process has been read twice; every rate is then unsampled.</summary>
  public bool HasPrevious { get; private set; }

  /// <summary>What share of the machine this thread used between the last two readings.</summary>
  public Rate CpuPercent(int index) => (uint)index < (uint)this._count ? this._cpuPercent[index] : Rate.NotSampledYet;

  /// <summary>
  /// The same figure with one processor as the whole, the way <c>top</c> reports it.
  /// </summary>
  /// <remarks>
  /// A thread cannot exceed one processor, so unlike the process figure this one really is bounded by
  /// 100 — and a reading above it means the interval was disturbed, which is worth seeing rather than
  /// clamping away (PRD §3.2).
  /// </remarks>
  public Rate CpuPercentPerCore(int index)
    => (uint)index < (uint)this._count ? this._cpuPercentPerCore[index] : Rate.NotSampledYet;

  public Rate ContextSwitchesPerSecond(int index)
    => (uint)index < (uint)this._count ? this._contextSwitchesPerSecond[index] : Rate.NotSampledYet;

  /// <summary>
  /// Takes a reading and computes what the previous one makes knowable.
  /// </summary>
  /// <param name="key">
  /// The process the threads belong to. A different one empties the history: thread ids are unique
  /// only inside a process, and keeping them across a change of selection would subtract one
  /// program's thread from another's.
  /// </param>
  /// <param name="threads">This reading, in the order the caller will display it.</param>
  /// <param name="coreCount">Logical processors, for the normalized percentage.</param>
  public void Update(ProcessKey key, IReadOnlyList<ThreadRecord> threads, int coreCount) {
    ArgumentNullException.ThrowIfNull(threads);

    if (key != this._key) {
      this._previous.Clear();
      this._key = key;
      this.HasPrevious = false;
    }

    var now = Stopwatch.GetTimestamp();
    var hadPrevious = this._previous.Count > 0;
    this._count = threads.Count;
    Grow(ref this._cpuPercent, threads.Count);
    Grow(ref this._cpuPercentPerCore, threads.Count);
    Grow(ref this._contextSwitchesPerSecond, threads.Count);

    this._seen.Clear();
    for (var i = 0; i < threads.Count; ++i) {
      var thread = threads[i];
      var id = (thread.Tid, thread.StartTimeUtcTicks);
      this._seen.Add(id);

      // A thread that was not in the previous reading has no interval to divide by. TryGetValue
      // leaves a default Reading behind on a miss, whose counters are default(Counter) — a confident
      // zero, and the one mistake this whole model exists to make impossible (PRD §3.4, §72.3).
      if (!this._previous.TryGetValue(id, out var before)) {
        this._cpuPercent[i] = Rate.NotSampledYet;
        this._cpuPercentPerCore[i] = Rate.NotSampledYet;
        this._contextSwitchesPerSecond[i] = Rate.NotSampledYet;
      } else {
        var elapsed = RateCalculator.ElapsedNanoseconds(before.Timestamp, now);
        this._cpuPercent[i] = RateCalculator.CpuPercent(
          before.CpuTimeNs, thread.CpuTimeNs, elapsed, coreCount, CpuPercentMode.Normalized
        );
        this._cpuPercentPerCore[i] = RateCalculator.CpuPercent(
          before.CpuTimeNs, thread.CpuTimeNs, elapsed, coreCount, CpuPercentMode.PerCore
        );
        this._contextSwitchesPerSecond[i] = RateCalculator.PerSecond(
          before.ContextSwitches, thread.ContextSwitches, elapsed
        );
      }

      this._previous[id] = new(now, thread.CpuTimeNs, thread.ContextSwitches);
    }

    // Threads that ended are dropped rather than left to accumulate: a long-lived pool churns
    // through ids all day, and a dictionary that only ever grows is a leak with a slow fuse.
    if (this._previous.Count > threads.Count) {
      var gone = new List<(int, long)>();
      foreach (var id in this._previous.Keys)
        if (!this._seen.Contains(id))
          gone.Add(id);

      foreach (var id in gone)
        this._previous.Remove(id);
    }

    this.HasPrevious = hadPrevious;
  }

  private static void Grow(ref Rate[] buffer, int count) {
    if (buffer.Length < count)
      buffer = new Rate[Math.Max(count, buffer.Length * 2)];

    for (var i = 0; i < count; ++i)
      buffer[i] = Rate.NotSampledYet;
  }

}
