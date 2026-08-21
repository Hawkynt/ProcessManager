using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The per-process CPU fields that are not a rate: which class of the scheduler runs a process,
/// which processors it may use, and how often its cgroup has stopped it (PRD §15).
/// </summary>
/// <remarks>
/// Read against a recorded <c>/proc</c> so they are exercised on every CI leg (PRD §9.1). The live
/// readings were cross-checked against <c>chrt</c>, <c>taskset</c> and the kernel's own
/// <c>cpu.stat</c> in the pull request that added them.
/// </remarks>
[TestFixture(false, TestName = "CpuFieldTests (syscalls)")]
[TestFixture(true, TestName = "CpuFieldTests (portable file access)")]
public sealed class CpuFieldTests(bool portable) {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static string CgroupRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "cgroup-limited");

  private LinuxProbeOptions Options => new() {
    UsePortableFileAccess = portable,
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  };

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"pid {pid} is not in the snapshot");
    return default;
  }

  private static SystemSnapshot Sample(LinuxProbeOptions options) {
    using var probe = new LinuxProbe(options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    return snapshot;
  }

  #region the scheduler class (PRD §15)

  /// <summary>
  /// Field 41 of <c>stat</c>, which is what <c>chrt</c> reports and what nothing else in the line
  /// says: a batch task and an ordinary one can carry the same priority and the same nice value.
  /// </summary>
  [Test]
  public void TheSchedulerClassIsReadFromTheStatLine() {
    var snapshot = Sample(this.Options);

    Assert.That(Find(snapshot, 1000).SchedulingPolicy, Is.EqualTo(SchedulingPolicy.Other));
    Assert.That(Find(snapshot, 1001).SchedulingPolicy, Is.EqualTo(SchedulingPolicy.Batch));
  }

  /// <summary>
  /// A class nobody reported is not the ordinary class. Zero is <c>SCHED_OTHER</c>, so a record
  /// nobody filled would claim every process on the machine is scheduled normally — including the
  /// real-time ones, which is the answer that matters (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnreadClassIsNotTheOrdinaryClass() {
    var record = default(ProcessRecord);
    record.Name = "test";

    Assert.That(record.SchedulingPolicy, Is.EqualTo(SchedulingPolicy.Unknown));
    Assert.That(
      FieldAccessor.Text(ProcessField.SchedulingClass, in record, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
    Assert.That(FieldAccessor.Number(ProcessField.SchedulingClass, in record, null, 0), Is.Null);
    Assert.That(FieldAccessor.RawText(ProcessField.SchedulingClass, in record), Is.Null);
  }

  /// <summary>
  /// The kernel's own spelling, because that is what somebody will search for and what every manual
  /// page and <c>chrt</c> itself print (PRD §5.3).
  /// </summary>
  [Test]
  public void TheClassIsShownUnderTheKernelsOwnName() {
    var snapshot = Sample(this.Options);
    var batch = Find(snapshot, 1001);

    Assert.That(FieldAccessor.Text(ProcessField.SchedulingClass, in batch, null, 0), Is.EqualTo("SCHED_BATCH"));
    Assert.That(FieldAccessor.RawText(ProcessField.SchedulingClass, in batch), Is.EqualTo("SCHED_BATCH"));
  }

  /// <summary>
  /// A stat line that stops before field 41 — an older kernel, a truncated read — leaves the class
  /// unknown rather than defaulting to the ordinary one.
  /// </summary>
  [Test]
  public void AStatLineThatStopsShortLeavesTheClassUnknown() {
    var record = default(ProcessRecord);
    var truncated = System.Text.Encoding.ASCII.GetBytes("1 (short) S 0 1 1 0 -1 0 0 0 0 0 1 2 0 0 20 0 1 0 5 100 3");

    Assert.That(LinuxProbe.ParseStat(truncated, 10_000_000, 4096, ref record), Is.True);
    Assert.That(record.SchedulingPolicy, Is.EqualTo(SchedulingPolicy.Unknown));
  }

  #endregion

  #region the affinity list (PRD §15)

  /// <summary>
  /// The list, not the mask on the line above it: <c>Cpus_allowed:</c> is a prefix of nothing, but
  /// a parser matching on the first characters would read "00ff" into a column headed with a list
  /// of processor numbers.
  /// </summary>
  [Test]
  public void TheAffinityIsTheListRatherThanTheMask() {
    var snapshot = Sample(this.Options with { ReadCpuAffinity = true });

    Assert.That(Find(snapshot, 1000).CpuAffinity, Is.EqualTo("0-7"));
    Assert.That(Find(snapshot, 1001).CpuAffinity, Is.EqualTo("2-3"));
  }

  /// <summary>
  /// The line is free — it is in a <c>status</c> the sampler already has open — but keeping it is a
  /// string per process per sample, so it is kept only when a column or a filter names it (§5.4).
  /// Not asked for reads as "nobody looked", never as an empty affinity.
  /// </summary>
  [Test]
  public void TheAffinityIsKeptOnlyWhenSomethingAsksForIt() {
    var unasked = Find(Sample(this.Options), 1000);

    Assert.That(unasked.CpuAffinity, Is.Null);
    Assert.That(unasked.CpuAffinityReason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(
      FieldAccessor.Text(ProcessField.CpuAffinity, in unasked, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet))
    );
  }

  /// <summary>
  /// A kernel that writes no such line — and one built without <c>CONFIG_CPUSETS</c> does not —
  /// leaves the field unknown rather than claiming the process may run nowhere.
  /// </summary>
  [Test]
  public void AKernelThatWritesNoAffinityLineIsNotAnEmptyAffinity() {
    var absent = Find(Sample(this.Options with { ReadCpuAffinity = true }), 1002);

    Assert.That(absent.CpuAffinity, Is.Null);
    Assert.That(absent.CpuAffinityReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(FieldAccessor.RawText(ProcessField.CpuAffinity, in absent), Is.Null);
  }

  /// <summary>
  /// Sorted by its spelling. An affinity list is a set: "0-7" and "2-3" have no order as numbers,
  /// and comparing them as numbers would be an ordering nobody could explain.
  /// </summary>
  [Test]
  public void AffinitiesSortByTheirText() {
    var snapshot = Sample(this.Options with { ReadCpuAffinity = true });
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var first = Find(snapshot, 1000);
    var second = Find(snapshot, 1001);

    Assert.That(FieldAccessor.Compare(ProcessField.CpuAffinity, in first, 0, in second, 1, delta), Is.LessThan(0));
    Assert.That(FieldAccessor.Compare(ProcessField.CpuAffinity, in second, 1, in first, 0, delta), Is.GreaterThan(0));
  }

  #endregion

  #region cgroup throttling (PRD §15, §38)

  /// <summary>
  /// The counter belongs to the group rather than to the process, and the column says so: pid 1000
  /// is in the recorded scope whose <c>cpu.stat</c> has been throttled forty-two times.
  /// </summary>
  [Test]
  public void ThrottlingComesFromTheProcessesOwnCgroup() {
    var snapshot = Sample(this.Options with { ReadCpuThrottling = true, CgroupRoot = CgroupRoot });

    Assert.That(Find(snapshot, 1000).ThrottledPeriods.Value, Is.EqualTo(42ul));
  }

  /// <summary>
  /// A group with no <c>cpu.stat</c> — no CPU controller on it — is not a group that has never been
  /// throttled. The two would otherwise be the same cell with opposite meanings (PRD §72.3).
  /// </summary>
  [Test]
  public void AGroupWithNoCounterIsNotAGroupThatWasNeverThrottled() {
    var snapshot = Sample(this.Options with { ReadCpuThrottling = true, CgroupRoot = CgroupRoot });

    // pid 1 is in /init.scope, which the recorded hierarchy does not carry.
    var absent = Find(snapshot, 1);
    Assert.That(absent.ThrottledPeriods.HasValue, Is.False);
    Assert.That(absent.ThrottledPeriods.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(FieldAccessor.Number(ProcessField.CpuThrottled, in absent, null, 0), Is.Null);
  }

  /// <summary>Nobody asking is "not sampled yet" — the read costs a file per cgroup (§5.4).</summary>
  [Test]
  public void ThrottlingIsReadOnlyWhenSomethingAsksForIt() {
    var unasked = Find(Sample(this.Options), 1000);

    Assert.That(unasked.ThrottledPeriods.HasValue, Is.False);
    Assert.That(unasked.ThrottledPeriods.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  #endregion

  #region the cpu.stat parser (PRD §9.2)

  [Test]
  public void TheThrottleCountIsReadAndTheMicrosecondsAreNot() {
    var text = "usage_usec 12345\nnr_periods 900\nnr_throttled 137\nthrottled_usec 4200000\n";

    Assert.That(CgroupCpuStatParser.Throttled(text).Value, Is.EqualTo(137ul));
  }

  /// <summary>
  /// A file without the line is a controller that is not enabled, which is not nought throttles.
  /// </summary>
  [Test]
  public void AFileWithoutTheLineHasNoCountAtAll() {
    var counter = CgroupCpuStatParser.Throttled("usage_usec 12345\nuser_usec 9000\n");

    Assert.That(counter.HasValue, Is.False);
    Assert.That(counter.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  [Test]
  public void ARealNoughtIsARealAnswer() =>
    Assert.That(CgroupCpuStatParser.Throttled("nr_periods 0\nnr_throttled 0\n").Value, Is.Zero);

  [Test]
  public void AValueThatIsNotANumberIsInvalidRatherThanZero() {
    var counter = CgroupCpuStatParser.Throttled("nr_throttled banana\n");

    Assert.That(counter.HasValue, Is.False);
    Assert.That(counter.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  /// <summary>A line without its trailing newline is the last line of the file, and still counts.</summary>
  [Test]
  public void TheLastLineNeedsNoNewline() =>
    Assert.That(CgroupCpuStatParser.Throttled("nr_periods 3\nnr_throttled 4").Value, Is.EqualTo(4ul));

  #endregion

  #region the catalogue (PRD §5.1)

  [Test]
  public void TheNewFieldsAreSpelledTheWayThePrdNamesThem() {
    Assert.Multiple(() => {
      Assert.That(FieldRegistry.TryParse("sched.class", out var scheduling), Is.True);
      Assert.That(scheduling, Is.EqualTo(ProcessField.SchedulingClass));

      Assert.That(FieldRegistry.TryParse("cpu.affinity", out var affinity), Is.True);
      Assert.That(affinity, Is.EqualTo(ProcessField.CpuAffinity));

      Assert.That(FieldRegistry.TryParse("throttled", out var throttled), Is.True);
      Assert.That(throttled, Is.EqualTo(ProcessField.CpuThrottled));
    });
  }

  /// <summary>
  /// Both of the expensive ones are declared expensive, which is what keeps them out of a default
  /// column set and makes the opt-in in the command line the only way to turn them on (PRD §5.4).
  /// </summary>
  [Test]
  public void TheReadsThatCostSomethingSaySo() {
    Assert.That(FieldRegistry.Get(ProcessField.CpuAffinity).Cost, Is.EqualTo(FieldCost.High));
    Assert.That(FieldRegistry.Get(ProcessField.CpuThrottled).Cost, Is.EqualTo(FieldCost.High));
    Assert.That(FieldRegistry.Get(ProcessField.SchedulingClass).Cost, Is.EqualTo(FieldCost.Free));
  }

  #endregion

  #region the change in a share (PRD §15)

  /// <summary>One process on a one-core machine, having used <paramref name="cpuSeconds"/> so far.</summary>
  private static SystemSnapshot AtSecond(int second, double cpuSeconds) {
    var snapshot = new SystemSnapshot {
      TimestampTicks = second * System.Diagnostics.Stopwatch.Frequency,
    };
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1000, 100);
    records[0].Name = "worker";
    records[0].CpuTimeNs = Counter.Of((ulong)(cpuSeconds * 1_000_000_000));
    snapshot.System.CoreCount = 1;
    return snapshot;
  }

  private static string Shown(SnapshotDelta delta, SystemSnapshot snapshot)
    => FieldAccessor.Text(ProcessField.CpuPercentDelta, in snapshot.Processes[0], delta, 0);

  /// <summary>
  /// The column that was in the catalogue with a header and a description and rendered an empty
  /// cell for every process, because nothing computed it and nothing rendered it (PRD §5.1).
  /// </summary>
  [Test]
  public void TheChangeInAShareIsTheDifferenceBetweenTwoIntervals() {
    var first = AtSecond(0, 0);
    var second = AtSecond(1, 0.1);                     // 10 % of the interval
    var third = AtSecond(2, 0.4);                      // 30 % of the next one

    var delta = new SnapshotDelta();
    delta.Update(null, first, CpuPercentMode.Normalized);
    delta.Update(first, second, CpuPercentMode.Normalized);
    delta.Update(second, third, CpuPercentMode.Normalized);

    Assert.That(delta.CpuPercentDelta(0).Value, Is.EqualTo(20).Within(0.01));
    Assert.That(Shown(delta, third), Is.EqualTo("+20.0"));
  }

  /// <summary>A process that has stopped working is as interesting as one that has started.</summary>
  [Test]
  public void AFallIsSignedAndIsNotAnAbsoluteValue() {
    var first = AtSecond(0, 0);
    var second = AtSecond(1, 0.5);
    var third = AtSecond(2, 0.6);

    var delta = new SnapshotDelta();
    delta.Update(null, first, CpuPercentMode.Normalized);
    delta.Update(first, second, CpuPercentMode.Normalized);
    delta.Update(second, third, CpuPercentMode.Normalized);

    Assert.That(delta.CpuPercentDelta(0).Value, Is.EqualTo(-40).Within(0.01));
    Assert.That(Shown(delta, third), Is.EqualTo("−40.0"), "a minus sign, not a magnitude");
  }

  /// <summary>
  /// Two samples give a share and nothing to compare it against. Nought would read as "steady" for
  /// a whole interval after start-up, which is the one thing this column must not say (PRD §72.3).
  /// </summary>
  [Test]
  public void TwoSamplesAreNotEnoughAndSaySoRatherThanReadingSteady() {
    var first = AtSecond(0, 0);
    var second = AtSecond(1, 0.1);

    var delta = new SnapshotDelta();
    delta.Update(null, first, CpuPercentMode.Normalized);
    Assert.That(delta.CpuPercentDelta(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));

    delta.Update(first, second, CpuPercentMode.Normalized);
    Assert.That(delta.CpuPercentDelta(0).HasValue, Is.False);
    Assert.That(delta.CpuPercentDelta(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(Shown(delta, second), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
    Assert.That(FieldAccessor.Number(ProcessField.CpuPercentDelta, in second.Processes[0], delta, 0), Is.Null);
  }

  /// <summary>A process that has just appeared has no earlier share of its own to have moved from.</summary>
  [Test]
  public void AProcessThatHasJustStartedHasNoChange() {
    var first = AtSecond(0, 0);
    var second = AtSecond(1, 0.1);
    var third = new SystemSnapshot { TimestampTicks = 2 * System.Diagnostics.Stopwatch.Frequency };
    var records = third.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1001, 200);                   // a different process in the same row
    records[0].Name = "fresh";
    records[0].CpuTimeNs = Counter.Of(0ul);
    third.System.CoreCount = 1;

    var delta = new SnapshotDelta();
    delta.Update(null, first, CpuPercentMode.Normalized);
    delta.Update(first, second, CpuPercentMode.Normalized);
    delta.Update(second, third, CpuPercentMode.Normalized);

    Assert.That(delta.CpuPercentDelta(0).HasValue, Is.False);
    Assert.That(delta.CpuPercentDelta(0).Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  #endregion

  #region asking for it (PRD §5.4)

  /// <summary>
  /// Naming the column or the filter term is the request, and it has to reach the probe: a field
  /// whose read nothing ever switches on is a column that is always empty while the document claims
  /// it works.
  /// </summary>
  [Test]
  public void NamingTheColumnIsWhatTurnsTheExpensiveReadOn() {
    Assert.Multiple(() => {
      Assert.That(Parse("--columns=name,cpu.affinity").WantsCpuAffinity, Is.True);
      Assert.That(Parse("--filter=cpu.affinity=0-7").WantsCpuAffinity, Is.True);
      Assert.That(Parse("--columns=name,ws").WantsCpuAffinity, Is.False);

      Assert.That(Parse("--columns=name,throttled").WantsCpuThrottling, Is.True);
      Assert.That(Parse("--filter=throttled>0").WantsCpuThrottling, Is.True);
      Assert.That(Parse("--columns=name,ws").WantsCpuThrottling, Is.False);
    });
  }

  /// <summary>The class costs nothing, so it is filled whether or not anybody asked.</summary>
  [Test]
  public void TheClassNeedsNoAskingBecauseItCostsNothing() {
    var snapshot = Sample(this.Options);

    Assert.That(Find(snapshot, 1001).SchedulingPolicy, Is.EqualTo(SchedulingPolicy.Batch));
  }

  private static Hawkynt.ProcessManager.App.CommandLineOptions Parse(string argument)
    => Hawkynt.ProcessManager.App.CommandLineOptions.Parse([argument], null);

  #endregion

}
