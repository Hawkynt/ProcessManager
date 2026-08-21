using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Marking a process that is leaning hard on something (PRD §23).
/// </summary>
/// <remarks>
/// The mark goes on the cell rather than the row, because a row's colour already answers a different
/// question — what kind of process it is — and colouring for both would mean one of the two facts
/// quietly winning.
/// </remarks>
[TestFixture]
public sealed class UsageHeatTests {

  private static readonly UsageThresholds _Thresholds = UsageThresholds.Default;

  [Test]
  public void HalfACoreIsWarmAndAWholeOneIsHot() {
    Assert.That(_Thresholds.Cpu(Rate.Of(10)), Is.EqualTo(UsageHeat.None));
    Assert.That(_Thresholds.Cpu(Rate.Of(50)), Is.EqualTo(UsageHeat.Warm));
    Assert.That(_Thresholds.Cpu(Rate.Of(99)), Is.EqualTo(UsageHeat.Warm));
    Assert.That(_Thresholds.Cpu(Rate.Of(100)), Is.EqualTo(UsageHeat.Hot));
    Assert.That(_Thresholds.Cpu(Rate.Of(400)), Is.EqualTo(UsageHeat.Hot), "four cores is still hot");
  }

  /// <summary>
  /// The trap this project keeps meeting. <c>default(Rate)</c> is a confident zero, so an unread
  /// counter compares as 0 and reads as cold — but a reading that came back <em>not permitted</em>
  /// is not a measurement at all, and must not be treated as one in either direction (PRD §5.3).
  /// </summary>
  [Test]
  public void AReadingThatDoesNotExistIsNeverHotAndNeverCold() {
    foreach (var reason in new[] { UnknownReason.NotPermitted, UnknownReason.NotSampledYet, UnknownReason.CounterInvalid })
      Assert.That(_Thresholds.Cpu(Rate.Unknown(reason)), Is.EqualTo(UsageHeat.None), reason.ToString());
  }

  /// <summary>
  /// Somebody who sets warm above hot has made a mistake, and the more serious answer is the safer
  /// one to give — a process at 200 % should not read as merely warm because warm was tested first.
  /// </summary>
  [Test]
  public void ABadlyOrderedPairStillReportsTheSeriousAnswer() {
    var backwards = UsageThresholds.Default with { WarmCpuPercent = 150, HotCpuPercent = 100 };

    Assert.That(backwards.Cpu(Rate.Of(200)), Is.EqualTo(UsageHeat.Hot));
  }

  /// <summary>A threshold of nought would mark every cell, so it turns the band off instead.</summary>
  [Test]
  public void AThresholdOfNoughtDisablesItsBandRatherThanMatchingEverything() {
    var off = UsageThresholds.Default with { WarmCpuPercent = 0, HotCpuPercent = 0 };

    Assert.That(off.Cpu(Rate.Of(0)), Is.EqualTo(UsageHeat.None));
    Assert.That(off.Cpu(Rate.Of(1000)), Is.EqualTo(UsageHeat.None));
  }

  [Test]
  public void ThroughputIsJudgedInBytesPerSecond() {
    Assert.That(_Thresholds.Throughput(Rate.Of(1024 * 1024)), Is.EqualTo(UsageHeat.None));
    Assert.That(_Thresholds.Throughput(Rate.Of(20d * 1024 * 1024)), Is.EqualTo(UsageHeat.Warm));
    Assert.That(_Thresholds.Throughput(Rate.Of(200d * 1024 * 1024)), Is.EqualTo(UsageHeat.Hot));
  }

  #region which cells are judged

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Machine(ulong cpuNs, ulong resident) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    var first = before.PrepareProcesses(1);
    first[0] = default;
    first[0].Key = new(1, 1000);
    first[0].CpuTimeNs = Counter.Of(0);
    before.System.CoreCount = 8;
    before.System.TotalMemoryBytes = Counter.Of(1000);

    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    var second = after.PrepareProcesses(1);
    second[0] = default;
    second[0].Key = new(1, 1000);
    second[0].Name = "busy";
    second[0].CpuTimeNs = Counter.Of(cpuNs);
    second[0].WorkingSetBytes = Counter.Of(resident);
    after.System.CoreCount = 8;
    after.System.TotalMemoryBytes = Counter.Of(1000);

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return (after, delta);
  }

  /// <summary>
  /// Per core, not normalised. A process holding one whole core of a thirty-two thread machine is
  /// what somebody is looking for, and normalising turns that into 3 % — which is nothing.
  /// </summary>
  [Test]
  public void CpuIsJudgedPerCoreAndNotNormalisedAcrossTheMachine() {
    // A full second of CPU in a one-second interval: one whole core.
    var (snapshot, delta) = Machine(1_000_000_000, 10);

    Assert.That(
      _Thresholds.Of(ProcessField.CpuPercent, in snapshot.Processes[0], delta, 0),
      Is.EqualTo(UsageHeat.Hot),
      "one core held is hot however many cores the machine has"
    );
  }

  [Test]
  public void MemoryIsJudgedAgainstTheMachinesTotal() {
    var (snapshot, delta) = Machine(0, 120);

    Assert.That(_Thresholds.Of(ProcessField.MemoryPercent, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.Hot));
    Assert.That(_Thresholds.Of(ProcessField.WorkingSetBytes, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.Hot));
  }

  /// <summary>
  /// A mark that points at the wrong number is worse than no mark. Commit charge and resident set
  /// are different quantities, and the first version of this washed a private-bytes cell because the
  /// process happened to have a large resident set.
  /// </summary>
  [Test]
  public void OnlyTheFieldsTheShareDescribesAreMarked() {
    var (snapshot, delta) = Machine(0, 900);

    Assert.That(_Thresholds.Of(ProcessField.WorkingSetBytes, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.Hot));
    Assert.That(
      _Thresholds.Of(ProcessField.PrivateBytes, in snapshot.Processes[0], delta, 0),
      Is.EqualTo(UsageHeat.None),
      "the resident share says nothing about the commit charge"
    );
  }

  /// <summary>A name or a pid is not a quantity and must never be marked.</summary>
  [Test]
  public void FieldsThatAreNotAboutConsumptionAreNeverMarked() {
    var (snapshot, delta) = Machine(1_000_000_000, 900);

    foreach (var id in new[] { ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.StartTime })
      Assert.That(_Thresholds.Of(id, in snapshot.Processes[0], delta, 0), Is.EqualTo(UsageHeat.None), id.ToString());
  }

  [Test]
  public void WithoutASecondSampleNothingIsMarked() {
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(1);

    Assert.That(_Thresholds.Of(ProcessField.CpuPercent, in snapshot.Processes[0], null, 0), Is.EqualTo(UsageHeat.None));
  }

  #endregion

  #region settings

  [Test]
  public void ThresholdsSurviveTheRoundTrip() {
    var settings = new UserSettings {
      Thresholds = new(25, 75, 2, 8, 1024, 2048),
    };

    var reread = UserSettings.Parse(settings.Write());

    Assert.That(reread.Thresholds, Is.EqualTo(settings.Thresholds));
  }

  /// <summary>Defaults are not written out, so a file that never changed them stays short.</summary>
  [Test]
  public void UntouchedThresholdsAreNotWrittenToTheFile() =>
    Assert.That(new UserSettings().Write(), Does.Not.Contain("heat."));

  /// <summary>
  /// A threshold of nought marks every cell, which is the most annoying possible response to a
  /// typo. A line that will not parse leaves the setting alone.
  /// </summary>
  [Test]
  public void ALineThatWillNotParseLeavesTheThresholdAlone() {
    var settings = UserSettings.Parse("heat.cpu.hot=lots\nheat.memory.warm=-5");

    Assert.That(settings.Thresholds.HotCpuPercent, Is.EqualTo(UsageThresholds.Default.HotCpuPercent));
    Assert.That(settings.Thresholds.WarmMemoryPercent, Is.EqualTo(UsageThresholds.Default.WarmMemoryPercent));
  }

  #endregion

}
