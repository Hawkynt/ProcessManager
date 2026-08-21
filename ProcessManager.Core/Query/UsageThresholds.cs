using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>How hard a process is leaning on one resource (PRD §23).</summary>
public enum UsageHeat : byte {

  /// <summary>Nothing worth marking.</summary>
  None = 0,

  /// <summary>Enough to notice.</summary>
  Warm,

  /// <summary>Enough to be the reason the machine feels the way it does.</summary>
  Hot,

}

/// <summary>
/// When a process is using enough of something to be worth marking (PRD §23).
/// </summary>
/// <remarks>
/// <para>
/// The mark goes on the <em>cell</em>, not the row. A row's colour already says what kind of process
/// it is (§7.1) and that is a one-of-many answer; how much CPU it is using is a separate axis, and a
/// system process can be busy without stopping being a system process. Colouring the row for both
/// would mean one of the two facts silently winning.
/// </para>
/// <para>
/// The defaults are deliberately not round numbers pulled from nowhere. CPU is expressed against one
/// core because that is the unit a person thinks in — "it is eating a core" — and memory against the
/// machine, because a gigabyte means nothing until you know how many there are.
/// </para>
/// </remarks>
public readonly record struct UsageThresholds(
  double WarmCpuPercent,
  double HotCpuPercent,
  double WarmMemoryPercent,
  double HotMemoryPercent,
  double WarmBytesPerSecond,
  double HotBytesPerSecond
) {

  /// <summary>
  /// The defaults.
  /// </summary>
  /// <remarks>
  /// Warm at half a core and hot at a whole one: below half a core almost everything on a desktop
  /// would light up, and a process holding a core to itself is exactly what somebody is looking for.
  /// Memory at a twentieth and a tenth of the machine. Throughput at ten and a hundred megabytes a
  /// second, which is the difference between a program reading a file and one saturating a disk.
  /// </remarks>
  public static readonly UsageThresholds Default = new(50, 100, 5, 10, 10d * 1024 * 1024, 100d * 1024 * 1024);

  /// <summary>How hot a CPU percentage is. The figure is per core, not normalised.</summary>
  public UsageHeat Cpu(Rate percent) => Classify(percent, this.WarmCpuPercent, this.HotCpuPercent);

  public UsageHeat Memory(Rate percent) => Classify(percent, this.WarmMemoryPercent, this.HotMemoryPercent);

  public UsageHeat Throughput(Rate bytesPerSecond)
    => Classify(bytesPerSecond, this.WarmBytesPerSecond, this.HotBytesPerSecond);

  /// <summary>
  /// Which band a reading falls in.
  /// </summary>
  /// <remarks>
  /// A reading with no value is never hot. That sounds obvious and is the whole of the bug it
  /// prevents: <c>default(Rate)</c> is a confident zero, so an unread counter compares as 0 and
  /// would be cold — but a counter that came back as <em>not permitted</em> must not be treated as a
  /// measurement at all, in either direction (PRD §5.3).
  /// </remarks>
  private static UsageHeat Classify(Rate reading, double warm, double hot) {
    if (!reading.HasValue)
      return UsageHeat.None;

    // Hot first: with a badly configured pair where warm exceeds hot, the more serious answer should
    // win rather than the first test that happens to pass.
    if (hot > 0 && reading.Value >= hot)
      return UsageHeat.Hot;

    return warm > 0 && reading.Value >= warm ? UsageHeat.Warm : UsageHeat.None;
  }

  /// <summary>
  /// How hot the value of one field is, or <see cref="UsageHeat.None"/> for a field that is not
  /// about consumption.
  /// </summary>
  /// <remarks>
  /// The mapping lives here rather than in a front-end so the window and the terminal mark the same
  /// cells — the same reason the field registry exists at all (§5.1).
  /// </remarks>
  public UsageHeat Of(ProcessField id, in ProcessRecord process, SnapshotDelta? delta, int index) {
    if (delta is null)
      return UsageHeat.None;

    return id switch {
      // Per core rather than normalised: "it is eating a core" is the thing being looked for, and
      // normalised percentages make a saturated core on a 32-thread machine read as 3 %.
      ProcessField.CpuPercent or ProcessField.CpuPercentPerCore => this.Cpu(delta.CpuPercentPerCore(index)),
      // Only the two fields the share actually describes. Marking the private-bytes cell from the
      // resident share was the first version of this, and it put a hot wash on a 6.8 GB commit
      // charge because the process happened to have a large resident set — a mark that points at
      // the wrong number is worse than no mark (PRD §5.1).
      ProcessField.MemoryPercent or ProcessField.WorkingSetBytes => this.Memory(delta.MemoryPercent(index)),
      ProcessField.IoTotalRate => this.Throughput(delta.IoTotalBytesPerSecond(index)),
      ProcessField.ReadBytesPerSecond => this.Throughput(delta.ReadBytesPerSecond(index)),
      ProcessField.WriteBytesPerSecond => this.Throughput(delta.WriteBytesPerSecond(index)),
      _ => UsageHeat.None,
    };
  }

}
