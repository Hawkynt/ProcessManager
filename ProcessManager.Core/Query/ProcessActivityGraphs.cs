using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The system-history quantities for which Process Manager can truthfully retain per-process causes.
/// </summary>
/// <remarks>
/// These are deliberately process-wide quantities. A process has one CPU share, one aggregate I/O
/// rate and one private-memory delta in <see cref="SnapshotDelta"/>. There is no per-process/per-disk
/// counter in the sampling model, so a disk-specific graph must never pretend these aggregate I/O
/// contributors caused that particular device's spike (PRD §5.3, §45).
/// </remarks>
public static class ProcessActivityGraphs {

  public const string Processor = "Processor";
  public const string Io = "Process I/O";
  public const string MemoryGrowth = "Private memory growth";

  /// <summary>Builds the three current readings on the same semantics retained by spike history.</summary>
  public static IReadOnlyList<PerformanceGraph> Build(SystemSnapshot snapshot, SnapshotDelta delta) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    var io = Total(snapshot, delta.IoTotalBytesPerSecond, positiveOnly: false);
    var memory = Total(snapshot, delta.PrivateBytesDelta, positiveOnly: true);
    return [
      new(
        Processor,
        delta.SystemCpuPercent,
        100,
        delta.SystemCpuPercent.HasValue
          ? Humanize.Percent(delta.SystemCpuPercent) + " %"
          : Humanize.Placeholder(delta.SystemCpuPercent.Reason),
        "cpu",
        PerformanceUnit.Percent
      ),
      new(
        Io,
        io,
        0,
        Humanize.BytesPerSecond(io),
        "io",
        PerformanceUnit.BytesPerSecond
      ),
      new(
        MemoryGrowth,
        memory,
        0,
        Humanize.BytesPerSecond(memory),
        "memory",
        PerformanceUnit.BytesPerSecond
      ),
    ];
  }

  /// <summary>The retained contributor metric corresponding to one graph label.</summary>
  public static bool TryGetMetric(string label, out SpikeMetric metric) {
    metric = label switch {
      Processor => SpikeMetric.Cpu,
      Io => SpikeMetric.Io,
      MemoryGrowth => SpikeMetric.MemoryGrowth,
      _ => default,
    };

    return label is Processor or Io or MemoryGrowth;
  }

  private static Rate Total(SystemSnapshot snapshot, Func<int, Rate> read, bool positiveOnly) {
    var measured = false;
    var total = 0d;
    var reason = UnknownReason.NotSampledYet;
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (processes[i].HasExited)
        continue;

      var value = read(i);
      if (!value.HasValue) {
        reason = value.Reason;
        continue;
      }

      measured = true;
      if (!positiveOnly || value.Value > 0)
        total += value.Value;
    }

    return measured ? Rate.Of(total) : Rate.Unknown(reason);
  }

}
