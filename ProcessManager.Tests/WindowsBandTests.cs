using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Windows bands and peaks of PRD §14, §15 and §16: the priority class, the page priority, the
/// quality-of-service masks, the CPU sets, the peak private commit and the MSIX package.
/// </summary>
/// <remarks>
/// Every one of these is read or rendered by code with no platform attribute, which is what lets
/// them be checked here rather than only on a Windows kernel (PRD §9.4). What the calls behind them
/// return is exercised by <c>--self-test</c> on the Windows CI leg; what those returns <em>mean</em>
/// is what is asserted below.
/// </remarks>
[TestFixture]
public sealed class WindowsBandTests {

  private static ProcessRecord Record() {
    var record = default(ProcessRecord);
    record.Key = new(1234, 1);
    record.Name = "test";
    return record;
  }

  /// <summary>One process out of the fixture, sampled by the Linux probe.</summary>
  private static ProcessRecord LinuxSample() {
    var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    var probe = new Hawkynt.ProcessManager.Platform.Linux.LinuxProbe(new() {
      ProcRoot = fixtures,
      PasswdPath = Path.Combine(fixtures, "passwd"),
      EffectiveUserId = 0,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();
    return snapshot.Processes[0];
  }

  #region the priority class (PRD §15)

  /// <summary>
  /// The six base priorities the six classes produce, by the table <c>SetPriorityClass</c>'s own
  /// reference page gives. Reading the class out of the base priority is only sound because that
  /// table is one-to-one, so the test is the table.
  /// </summary>
  [TestCase(4, "idle")]
  [TestCase(6, "below normal")]
  [TestCase(8, "normal")]
  [TestCase(10, "above normal")]
  [TestCase(13, "high")]
  [TestCase(24, "real time")]
  public void EachBasePriorityNamesItsBand(int basePriority, string expected) {
    var record = Record();
    record.PriorityClass = Counter.Of((ulong)basePriority);

    Assert.That(FieldAccessor.Text(ProcessField.PriorityClass, in record, null, 0), Is.EqualTo(expected));
  }

  /// <summary>
  /// The bands sort the way the scheduler orders them, which is the only order a priority class has —
  /// and is why the base priority is what is stored rather than the <c>PROCESS_PRIORITY_CLASS_*</c>
  /// ordinal, which numbers "below normal" above "high".
  /// </summary>
  [Test]
  public void TheBandsSortFromIdleToRealTime() {
    var idle = Record();
    idle.PriorityClass = Counter.Of(4ul);
    var high = Record();
    high.PriorityClass = Counter.Of(13ul);

    Assert.That(FieldAccessor.Compare(ProcessField.PriorityClass, in idle, 0, in high, 0, null), Is.LessThan(0));
  }

  /// <summary>
  /// A base priority no class produces is left unknown rather than rounded into the nearest band it
  /// is not — the same rule the integrity level and the subsystem follow.
  /// </summary>
  [Test]
  public void ABasePriorityNoClassProducesIsNotRoundedIntoOne() {
    var (buffer, baseAddress, handle) = WindowsBufferBuilder.Build(basePriority: 9);
    try {
      var snapshot = new SystemSnapshot();
      SystemProcessInformationReader.Parse(buffer, baseAddress, snapshot);

      var process = snapshot.Processes[0];
      Assert.That(process.PriorityClass.HasValue, Is.False);
      Assert.That(process.PriorityClass.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
      Assert.That(FieldAccessor.Text(ProcessField.PriorityClass, in process, null, 0), Is.EqualTo("?"));
    } finally {
      handle.Free();
    }
  }

  /// <summary>
  /// And the ordinary case comes out of the bulk query itself, refreshed by every sample — which is
  /// the reason it is derived rather than read off a handle once and cached: the class is settable,
  /// and this program has a menu item that sets it.
  /// </summary>
  [Test]
  public void TheClassComesOutOfTheSampleRatherThanACache() {
    var (buffer, baseAddress, handle) = WindowsBufferBuilder.Build(basePriority: 13);
    try {
      var snapshot = new SystemSnapshot();
      SystemProcessInformationReader.Parse(buffer, baseAddress, snapshot);

      Assert.That(FieldAccessor.Text(ProcessField.PriorityClass, in snapshot.Processes[0], null, 0), Is.EqualTo("high"));
    } finally {
      handle.Free();
    }
  }

  /// <summary>
  /// Linux has no such band, and says so rather than claiming the ordinary one. Its own priority
  /// number lives in a different scale entirely — <c>stat</c> field 18 runs from -100 to 39 — so a
  /// record that let this be read from it would name a band for every process on the machine.
  /// </summary>
  [Test]
  public void LinuxHasNoPriorityClass() {
    var process = LinuxSample();

    Assert.That(process.PriorityClass.HasValue, Is.False);
    Assert.That(process.PriorityClass.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(FieldAccessor.Text(ProcessField.PriorityClass, in process, null, 0), Is.EqualTo("n/a"));
    // …and the number it would otherwise have been read from is still there and still means
    // something else entirely: stat field 18 runs from -100 to 39 and is not a band.
    Assert.That(FieldAccessor.Number(ProcessField.Priority, in process, null, 0), Is.Not.Null);
  }

  #endregion

  #region the peak private commit (PRD §16)

  [Test]
  public void ThePeakPrivateCommitIsThePeakOfTheSameCharge() {
    var (buffer, baseAddress, handle) = WindowsBufferBuilder.Build(
      privateBytes: 104_857_600,
      peakPrivateBytes: 4_294_967_296
    );

    try {
      var snapshot = new SystemSnapshot();
      SystemProcessInformationReader.Parse(buffer, baseAddress, snapshot);

      var process = snapshot.Processes[0];
      Assert.That(process.PrivateBytes.Value, Is.EqualTo(104_857_600ul));
      Assert.That(process.PeakPrivateBytes.Value, Is.EqualTo(4_294_967_296ul));
      Assert.That(
        FieldAccessor.Number(ProcessField.PeakPrivateBytes, in process, null, 0),
        Is.EqualTo(4_294_967_296d)
      );
    } finally {
      handle.Free();
    }
  }

  /// <summary>
  /// A peak that is nought for every process on the machine is a stub rather than a measurement —
  /// the same wine-shaped defect the private bytes and the cycle counter already guard against.
  /// </summary>
  [Test]
  public void APeakThatIsNoughtEverywhereIsReportedUnavailable() {
    var (buffer, baseAddress, handle) = WindowsBufferBuilder.Build(
      privateBytes: 104_857_600,
      peakPrivateBytes: 0
    );

    try {
      var snapshot = new SystemSnapshot();
      SystemProcessInformationReader.Parse(buffer, baseAddress, snapshot);

      Assert.That(snapshot.Processes[0].PeakPrivateBytes.HasValue, Is.False);
    } finally {
      handle.Free();
    }
  }

  /// <summary>
  /// Linux keeps no high-water mark of its commit charge, and the two peaks it does keep are already
  /// their own columns. Not applicable rather than unknown (PRD §5.3).
  /// </summary>
  [Test]
  public void LinuxHasNoPeakPrivateCommit() {
    var process = LinuxSample();

    Assert.That(process.PeakPrivateBytes.HasValue, Is.False);
    Assert.That(process.PeakPrivateBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    // …while the peaks Linux does keep are still there, so this is a missing figure rather than a
    // missing file.
    Assert.That(process.PeakWorkingSetBytes.HasValue, Is.True);
    Assert.That(process.PeakVirtualBytes.HasValue, Is.True);
  }

  #endregion

  #region the page priority (PRD §16)

  [TestCase(0, "lowest")]
  [TestCase(1, "very low")]
  [TestCase(2, "low")]
  [TestCase(3, "medium")]
  [TestCase(4, "below normal")]
  [TestCase(5, "normal")]
  public void EachMemoryPriorityHasItsOwnWord(int value, string expected) {
    var record = Record();
    record.PagePriority = Counter.Of((ulong)value);

    Assert.That(FieldAccessor.Text(ProcessField.PagePriority, in record, null, 0), Is.EqualTo(expected));
    Assert.That(FieldAccessor.RawText(ProcessField.PagePriority, in record), Is.EqualTo(expected));
  }

  /// <summary>
  /// Nought is <c>MEMORY_PRIORITY_LOWEST</c> and a real reading, so a run that did not ask must not
  /// look like a machine full of processes whose pages go first (PRD §72.3).
  /// </summary>
  [Test]
  public void NoughtIsAReadingAndAnUnaskedColumnIsNot() {
    var lowest = Record();
    lowest.PagePriority = Counter.Of(0ul);
    var unasked = Record();
    unasked.PagePriority = Counter.NotSampledYet;

    Assert.That(FieldAccessor.Text(ProcessField.PagePriority, in lowest, null, 0), Is.EqualTo("lowest"));
    Assert.That(FieldAccessor.Text(ProcessField.PagePriority, in unasked, null, 0), Is.EqualTo("…"));
    Assert.That(FieldAccessor.Number(ProcessField.PagePriority, in unasked, null, 0), Is.Null);
  }

  #endregion

  #region the CPU sets (PRD §15)

  /// <summary>
  /// No set assigned is a real answer and the ordinary one — the process uses the system's default
  /// set, which is every processor — so it reads as a word rather than as a hole (PRD §72.3).
  /// </summary>
  [Test]
  public void NoSetAssignedIsTheDefaultSetRatherThanNone() {
    var record = Record();
    record.CpuSets = string.Empty;
    record.CpuSetsReason = UnknownReason.None;

    Assert.That(FieldAccessor.Text(ProcessField.CpuSets, in record, null, 0), Is.EqualTo("default"));
    // And the export writes the same word the column shows, which is the seam §103's invariant
    // exists to catch.
    Assert.That(FieldAccessor.RawText(ProcessField.CpuSets, in record), Is.EqualTo("default"));
  }

  [Test]
  public void AnAssignedSetIsListedAsTheNumbersWindowsUses() {
    var record = Record();
    record.CpuSets = "256,257,260";
    record.CpuSetsReason = UnknownReason.None;

    Assert.That(FieldAccessor.Text(ProcessField.CpuSets, in record, null, 0), Is.EqualTo("256,257,260"));
  }

  [Test]
  public void LinuxHasNoCpuSetsOfItsOwn() {
    var record = Record();
    ProcessRecord.ClearPlatformReadings(ref record);

    Assert.That(FieldAccessor.Text(ProcessField.CpuSets, in record, null, 0), Is.EqualTo("n/a"));
  }

  #endregion

  #region the MSIX package (PRD §14)

  /// <summary>
  /// A package full name is <c>name_version_architecture__publisherhash</c>; the family name is the
  /// application id, and is not a substring of it — the version and the architecture sit between its
  /// two halves, which is why it is asked for separately.
  /// </summary>
  [Test]
  public void APackageFullNameIsReadIntoItsNameAndVersion() {
    var package = MsixIdentity.Describe(
      "Microsoft.WindowsTerminal_1.18.3181.0_x64__8wekyb3d8bbwe",
      "Microsoft.WindowsTerminal_8wekyb3d8bbwe"
    );

    Assert.Multiple(() => {
      Assert.That(package.Source, Is.EqualTo(PackageSource.Msix));
      Assert.That(package.Name, Is.EqualTo("Microsoft.WindowsTerminal"));
      Assert.That(package.Version, Is.EqualTo("1.18.3181.0"));
      Assert.That(package.ApplicationId, Is.EqualTo("Microsoft.WindowsTerminal_8wekyb3d8bbwe"));
      Assert.That(package.Text, Is.EqualTo("msix: Microsoft.WindowsTerminal 1.18.3181.0"));
    });
  }

  /// <summary>
  /// A name that is not in that shape is shown whole rather than cut at a separator it does not have:
  /// reporting half of a name nobody recognises is worse than reporting the name (PRD §5.3).
  /// </summary>
  [Test]
  public void ANameThatIsNotInThatShapeIsShownWhole() {
    var package = MsixIdentity.Describe("something-nobody-expected", null);

    Assert.That(package.Name, Is.EqualTo("something-nobody-expected"));
    Assert.That(package.Version, Is.Null);
    Assert.That(package.ApplicationId, Is.Null);
  }

  /// <summary>
  /// No package at all is the answer for nearly every process on a Windows machine, and it is a
  /// finding rather than a hole — the same word the Linux half uses for it.
  /// </summary>
  [Test]
  public void NoPackageIsAFindingRatherThanAHole() {
    var package = MsixIdentity.Describe(null, null);

    Assert.That(package.WasChecked, Is.True);
    Assert.That(package.Reason, Is.EqualTo(UnknownReason.None));
    Assert.That(package.Text, Is.EqualTo("not packaged"));

    var record = Record();
    record.Package = package;
    // The application id of an unpackaged process is a dash — somebody looked and there is none —
    // rather than the placeholder that means nobody looked.
    Assert.That(FieldAccessor.Text(ProcessField.ApplicationId, in record, null, 0), Is.EqualTo("—"));
  }

  #endregion

}
