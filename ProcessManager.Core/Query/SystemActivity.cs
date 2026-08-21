using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One process in a "what is using this" list (PRD §51).</summary>
/// <param name="Key">Kept so a caller can navigate to the process rather than only read about it.</param>
/// <param name="Value">Already formatted, in the unit the resource is measured in.</param>
public readonly record struct ActivityEntry(ProcessKey Key, string Name, string Value);

/// <summary>
/// What is using the machine, and how fast it is changing (PRD §51).
/// </summary>
/// <remarks>
/// <para>
/// The question a sorted table answers only if you already sorted it by the right column. Somebody
/// opening a system page wants the four answers at once — what is using the processor, the memory
/// and the disk — and getting them means re-sorting the table three times and losing their place
/// each time.
/// </para>
/// <para>
/// Selection, not sorting. Finding the top five of four hundred by sorting is four hundred log four
/// hundred comparisons, three times over, every second; a single pass keeping the best five is four
/// hundred. On a page that refreshes at one hertz that difference is the whole cost of the feature.
/// </para>
/// </remarks>
public static class SystemActivity {

  /// <summary>How many entries each list holds.</summary>
  /// <remarks>
  /// Five. Three does not show a pattern, ten is a table — and a table is the thing this exists to
  /// save somebody from.
  /// </remarks>
  public const int Depth = 5;

  /// <summary>The processes using the most of one resource, largest first.</summary>
  public static IReadOnlyList<ActivityEntry> Top(
    SystemSnapshot snapshot,
    SnapshotDelta? delta,
    ProcessField field
  ) {
    ArgumentNullException.ThrowIfNull(snapshot);
    if (delta is null)
      return [];

    var processes = snapshot.Processes;
    Span<int> best = stackalloc int[Depth];
    Span<double> scores = stackalloc double[Depth];
    var found = 0;

    for (var i = 0; i < processes.Length; ++i) {
      // A reading that does not exist is not a small one. A process whose counters are unreadable
      // must not sit at the bottom of the list as though it were idle (PRD §5.3).
      if (FieldAccessor.Number(field, in processes[i], delta, i) is not { } score)
        continue;

      if (score <= 0)
        continue;

      var at = found;
      while (at > 0 && scores[at - 1] < score) {
        if (at < Depth) {
          scores[at] = scores[at - 1];
          best[at] = best[at - 1];
        }

        --at;
      }

      if (at >= Depth)
        continue;

      scores[at] = score;
      best[at] = i;
      if (found < Depth)
        ++found;
    }

    var entries = new ActivityEntry[found];
    for (var i = 0; i < found; ++i) {
      ref readonly var process = ref processes[best[i]];
      entries[i] = new(process.Key, process.Name, FieldAccessor.Text(field, in process, delta, best[i]));
    }

    return entries;
  }

  /// <summary>
  /// How fast the machine's own bookkeeping is moving: processes appearing and disappearing, and
  /// context switches.
  /// </summary>
  /// <remarks>
  /// Started and exited come from the delta's own comparison of two snapshots rather than from the
  /// kernel's cumulative <c>processes</c> counter, because that counter only ever counts *forks* —
  /// it cannot say how many went away, and a machine churning a thousand short-lived processes a
  /// second looks identical to one that started a thousand and kept them.
  /// </remarks>
  public static IReadOnlyList<PerformanceRow> Rates(SystemSnapshot snapshot, SnapshotDelta? delta) {
    ArgumentNullException.ThrowIfNull(snapshot);

    var elapsed = delta?.ElapsedNanoseconds ?? double.NaN;
    var perSecond = double.IsNaN(elapsed) || elapsed <= 0 ? double.NaN : 1_000_000_000d / elapsed;

    var (received, sent) = NetworkActivity(snapshot, delta);
    return [
      new("Processes started", PerSecond(delta?.StartedCount, perSecond)),
      new("Processes ended", PerSecond(delta?.Exited.Count, perSecond)),
      new("Context switches", Humanize.Rate(delta?.SystemContextSwitchesPerSecond ?? Rate.NotSampledYet)),
      new("Threads", Humanize.Count(Counter.Of((ulong)Math.Max(0, snapshot.System.TotalThreads)))),
      // The machine's traffic, not any process's. §18 refuses per-process byte counters because no
      // portable source for them exists, and that refusal says nothing about the machine as a whole,
      // whose interfaces have counted every byte since boot. A reader looking at a saturated link
      // and an idle process table has learnt something — that whatever is using it is not here.
      new("Network in", Humanize.Rate(received)),
      new("Network out", Humanize.Rate(sent)),
    ];
  }

  /// <summary>
  /// A count over the interval it was counted in.
  /// </summary>
  /// <remarks>
  /// Divided by the real elapsed time rather than assumed to be one second: a page whose refresh is
  /// set to five seconds would otherwise report five seconds of forks as a per-second rate and
  /// quintuple everything.
  /// </remarks>
  /// <summary>
  /// What the machine is sending and receiving, summed across its real interfaces.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Loopback is left out. Traffic a machine sends to itself crosses no wire and counts twice — once
  /// out and once in — so including it would report a database and its client on one host as heavy
  /// network users when nothing has left the box.
  /// </para>
  /// <para>
  /// An interface whose rate has not been sampled contributes nothing and does not make the total
  /// unknown; a total that is unknown until every interface has been seen twice would be unknown on
  /// any machine with a device that comes and goes. But if <em>no</em> interface has a rate, the
  /// total is unknown rather than nought — nought is a measurement and this would not be one
  /// (PRD §72.3).
  /// </para>
  /// </remarks>
  private static (Rate Received, Rate Sent) NetworkActivity(SystemSnapshot snapshot, SnapshotDelta? delta) {
    if (delta is null)
      return (Rate.NotSampledYet, Rate.NotSampledYet);

    double received = 0, sent = 0;
    var seen = 0;
    foreach (var network in snapshot.Networks) {
      if (network.Name is not { Length: > 0 } name || IsLoopback(name))
        continue;

      var rates = delta.NetworkRatesOf(name);
      if (rates.ReceivedBytesPerSecond.HasValue) {
        received += rates.ReceivedBytesPerSecond.Value;
        ++seen;
      }

      if (rates.SentBytesPerSecond.HasValue) {
        sent += rates.SentBytesPerSecond.Value;
        ++seen;
      }
    }

    return seen == 0
      ? (Rate.NotSampledYet, Rate.NotSampledYet)
      : (Rate.Of(received), Rate.Of(sent));
  }

  /// <summary>
  /// Whether an interface is the machine talking to itself.
  /// </summary>
  /// <remarks>
  /// By name, which is the one thing the counter file gives. Every kernel this runs on calls it
  /// <c>lo</c>; Windows names its loopback differently and reports no counters for it at all, so the
  /// prefix covers what there is to cover and a miss costs a doubled figure rather than a wrong one.
  /// </remarks>
  private static bool IsLoopback(string name)
    => name.Equals("lo", StringComparison.OrdinalIgnoreCase)
    || name.StartsWith("lo:", StringComparison.OrdinalIgnoreCase)
    || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

  private static string PerSecond(int? count, double perSecond) {
    if (count is not { } value || double.IsNaN(perSecond))
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    return Humanize.Rate(Rate.Of(value * perSecond));
  }

}
