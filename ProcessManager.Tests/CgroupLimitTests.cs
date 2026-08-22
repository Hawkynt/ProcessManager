using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a cgroup allows a process (PRD §38).
/// </summary>
/// <remarks>
/// The answer to "why is this slow when the machine is idle". This machine has no capped cgroups on
/// it, so the limits that matter are exercised against a recorded hierarchy — the uncapped case is
/// cross-checked against the real one in the pull request.
/// </remarks>
[TestFixture]
public sealed class CgroupLimitTests {

  private static string Root => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "cgroup-limited");

  /// <summary>
  /// Deliberately a directory that is not there.
  /// </summary>
  /// <remarks>
  /// The device numbers in a recorded hierarchy are somebody else's machine's, so resolving them
  /// against <c>/sys/dev/block</c> here would put the name of one of <em>this</em> computer's disks
  /// beside a limit recorded on another. It would also make the test's result depend on which disks
  /// the machine running it happens to have, which is the other half of why fixtures exist.
  /// </remarks>
  private const string _NoDevices = "/nonexistent/sys/dev/block";

  /// <summary>
  /// A cgroup that is capped in every way the kernel allows.
  /// </summary>
  /// <remarks>
  /// Read through the same entry point the probe uses, path and all, because the path handling is
  /// half the bug surface here: a cgroup path begins with a slash, and joining it to a root naively
  /// discards the root and reads from the filesystem root instead.
  /// </remarks>
  private static CgroupInfo Capped() {
    var info = Platform.Linux.CgroupReader.Read(Root, "/system.slice/capped.service", _NoDevices);
    Assert.That(info, Is.Not.Null, "the fixture did not resolve");
    return info!;
  }

  private static CgroupInfo Free() {
    var info = Platform.Linux.CgroupReader.Read(Root, "/user.slice/free.scope", _NoDevices);
    Assert.That(info, Is.Not.Null);
    return info!;
  }

  [Test]
  public void APathBeginningWithASlashIsStillRelativeToTheRoot() =>
    Assert.That(Capped().Path, Is.EqualTo("/system.slice/capped.service"));

  /// <summary>
  /// A quota is written as two microsecond figures and means nothing until they are divided.
  /// "Half a core" is the sentence somebody wants; "50000 100000" is not.
  /// </summary>
  [Test]
  public void AQuotaIsReportedAsANumberOfCores() =>
    Assert.That(Capped().CpuQuotaCores, Is.EqualTo(0.5).Within(0.0001));

  /// <summary>
  /// Unlimited is not a quantity. A caller that formatted a very large number would print something
  /// absurd, and "no limit" and "a limit of nine million terabytes" must not look alike (PRD §5.3).
  /// </summary>
  [Test]
  public void NoLimitIsNotAVeryLargeNumber() {
    var free = Free();

    Assert.That(free.CpuQuotaCores, Is.Null);
    Assert.That(free.MemoryMaxBytes.HasValue, Is.False);
    Assert.That(free.MemoryHighBytes.HasValue, Is.False);
    Assert.That(free.PidsMax.HasValue, Is.False);
  }

  /// <summary>
  /// A controller that is switched off and a controller set to <c>max</c> are different answers.
  /// </summary>
  /// <remarks>
  /// Both have no number to show, and reading them as the same thing said the machine could not
  /// answer when it had answered plainly. The first means the question does not apply to this group
  /// and an ancestor's limit governs it; the second means this group was deliberately left unbounded.
  /// Somebody chasing why a process hit a wall needs to know which (PRD §5.3).
  /// </remarks>
  [Test]
  public void NoControllerAndNoLimitAreNotTheSameAnswer() {
    var free = Free();

    // memory.max exists in this cgroup and reads "max".
    Assert.That(free.MemoryMaxBytes.HasValue, Is.False);
    Assert.That(free.MemoryMaxBytes.Reason, Is.EqualTo(UnknownReason.NoLimit));

    // cpu.stat does not exist here at all, because the controller is not enabled.
    Assert.That(free.ThrottledCount.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>And each renders as itself rather than as a shared blank.</summary>
  [Test]
  public void EachKindOfAbsenceRendersAsItself() {
    Assert.That(Humanize.Placeholder(UnknownReason.NoLimit), Is.Not.Empty);
    Assert.That(
      Humanize.Placeholder(UnknownReason.NoLimit),
      Is.Not.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  [Test]
  public void TheLimitsAndTheUsageAreBothRead() {
    var capped = Capped();

    Assert.That(capped.MemoryCurrentBytes.Value, Is.EqualTo(268435456ul));
    Assert.That(capped.MemoryMaxBytes.Value, Is.EqualTo(536870912ul));
    Assert.That(capped.MemoryHighBytes.Value, Is.EqualTo(402653184ul), "the soft cap is a different limit");
    Assert.That(capped.PidsCurrent.Value, Is.EqualTo(12ul));
    Assert.That(capped.PidsMax.Value, Is.EqualTo(64ul));
  }

  /// <summary>
  /// The number that turns "it is slow" into "it is being throttled", which is a different sentence
  /// and the one somebody can act on.
  /// </summary>
  [Test]
  public void ThrottlingIsCounted() =>
    Assert.That(Capped().ThrottledCount.Value, Is.EqualTo(137ul));

  /// <summary>
  /// A cgroup with a quota it never reaches reports nought throttles, which is a real nought. A
  /// cgroup whose CPU controller is not enabled has no such file, which is not.
  /// </summary>
  [Test]
  public void NoThrottleFileIsNotNoughtThrottles() {
    var free = Free();

    Assert.That(free.ThrottledCount.HasValue, Is.False);
    Assert.That(free.ThrottledCount.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// A limit file existing does not mean its controller is on. A delegated cgroup may have memory
  /// and not cpu, in which case the CPU limit is inherited from an ancestor rather than absent.
  /// </summary>
  [Test]
  public void TheEnabledControllersAreReported() {
    Assert.That(Capped().Controllers, Is.EqualTo(new[] { "cpu", "io", "memory", "pids" }));
    Assert.That(Capped().Has("cpu"), Is.True);
    Assert.That(Free().Has("cpu"), Is.False);
    Assert.That(Free().Has("memory"), Is.True);
  }

  /// <summary>Per-cgroup pressure is the same format as the machine's, and the same parser.</summary>
  [Test]
  public void PressureIsReadPerCgroup() {
    var capped = Capped();

    Assert.That(capped.CpuPressure.Some.Average10.Value, Is.EqualTo(12.5).Within(0.001));
    Assert.That(capped.CpuPressure.Full.Average10.Value, Is.EqualTo(4).Within(0.001));
    Assert.That(capped.IoPressure.Some.Average300.Value, Is.EqualTo(3).Within(0.001));
  }

  /// <summary>
  /// A cgroup with no pressure files — an older kernel, or one built without PSI — reports unknown
  /// rather than a machine under no pressure at all.
  /// </summary>
  [Test]
  public void ACgroupWithoutPressureFilesSaysSoRatherThanReadingZero() {
    var free = Free();

    Assert.That(free.CpuPressure.HasValue, Is.False);
    Assert.That(free.MemoryPressure.HasValue, Is.False);
  }

  [Test]
  public void ACgroupThatIsNotThereIsNullRatherThanAnError() {
    Assert.That(Platform.Linux.CgroupReader.Read(Root, "/nothing/here.scope", _NoDevices), Is.Null);
    Assert.That(Platform.Linux.CgroupReader.Read(Root, null, _NoDevices), Is.Null);
    Assert.That(Platform.Linux.CgroupReader.Read(Root, string.Empty, _NoDevices), Is.Null);
  }

  #region the hierarchy (PRD §38)

  /// <summary>
  /// The whole chain is read, root first, and the cgroup itself is the last of it.
  /// </summary>
  /// <remarks>
  /// The last entry being this cgroup is what lets a caller say "and its ancestors" by dropping one
  /// entry instead of reading the same four files a second time.
  /// </remarks>
  [Test]
  public void TheChainRunsFromTheRootDownToTheCgroupItself() {
    var paths = new List<string>();
    foreach (var level in Capped().Chain)
      paths.Add(level.Path);

    Assert.That(paths, Is.EqualTo(new[] { "/", "/system.slice", "/system.slice/capped.service" }));
  }

  /// <summary>
  /// The whole point: a ceiling set two levels up governs a cgroup that sets a looser one of its own.
  /// </summary>
  /// <remarks>
  /// The fixture's service asks for 512M and sits in a slice capped at 256M, which is a layout the
  /// kernel permits and enforces from the outside in. Reading one directory reports 512M — a number
  /// the process will never be allowed to reach, presented as its limit.
  /// </remarks>
  [Test]
  public void AnAncestorsCeilingIsTheOneInForce() {
    var capped = Capped();

    Assert.That(capped.MemoryMaxBytes.Value, Is.EqualTo(536870912ul), "what this cgroup asks for");

    var ceiling = capped.TightestMemoryLimit();
    Assert.That(ceiling.Value.Value, Is.EqualTo(268435456ul), "what it actually gets");
    Assert.That(ceiling.Path, Is.EqualTo("/system.slice"));
  }

  /// <summary>
  /// And the reverse: where the cgroup itself is the tighter one, it is named and the ancestor is not.
  /// </summary>
  [Test]
  public void TheCgroupsOwnCeilingWinsWhenItIsTheTighter() {
    var capped = Capped();

    var tasks = capped.TightestTaskLimit();
    Assert.That(tasks.Value.Value, Is.EqualTo(64ul));
    Assert.That(tasks.Unit, Is.EqualTo("capped.service"));

    var (cores, _, _, unit) = capped.TightestCpuQuota();
    Assert.That(cores, Is.EqualTo(0.5).Within(0.0001), "half a core against the slice's two");
    Assert.That(unit, Is.EqualTo("capped.service"));
  }

  /// <summary>
  /// A slice is not a unit, so the level that imposes a limit is named by its path where it has no
  /// unit name — reporting <c>system.slice</c> as a unit would name a container rather than an owner.
  /// </summary>
  [Test]
  public void ASliceIsNamedByItsPathBecauseItIsNotAUnit() {
    var ceiling = Capped().TightestMemoryLimit();

    Assert.That(ceiling.Unit, Is.Null);
    Assert.That(ceiling.Path, Is.EqualTo("/system.slice"));
  }

  /// <summary>
  /// "Nothing in the chain limits this" and "no cgroup in the chain has the controller on" are
  /// different answers, and a chain that answered <c>max</c> at some level said the first plainly.
  /// </summary>
  [Test]
  public void UnlimitedAllTheWayUpIsNotTheSameAsNoControllerAnywhere() {
    var free = Free();

    var memory = free.TightestMemoryLimit();
    Assert.That(memory.Path, Is.Null, "nobody set a number");
    Assert.That(memory.Value.Reason, Is.EqualTo(UnknownReason.NoLimit), "but somebody wrote max");

    // The user slice caps tasks even though the scope inside it does not, which is the ordinary
    // shape of a desktop and the one a single-directory read gets wrong.
    var tasks = free.TightestTaskLimit();
    Assert.That(tasks.Value.Value, Is.EqualTo(4096ul));
    Assert.That(tasks.Path, Is.EqualTo("/user.slice"));

    var (cores, quotaReason, path, _) = free.TightestCpuQuota();
    Assert.That(cores, Is.Null);
    Assert.That(path, Is.Null);

    // And which kind of "no quota" it is survives the walk: the scope wrote "max 100000", which is a
    // decision somebody made, where the slice above it has no cpu.max at all.
    Assert.That(quotaReason, Is.EqualTo(UnknownReason.NoLimit));
  }

  /// <summary>
  /// A limit file that was there and would not read is a hole, and outranks both kinds of "no
  /// ceiling" when the chain is reduced.
  /// </summary>
  /// <remarks>
  /// The reassuring half of §72.3's mistake. A chain that dropped it would report "no cgroup in the
  /// chain has that controller on" about a cgroup that plainly does, and a reader would go looking
  /// anywhere but at the file nobody could read.
  /// </remarks>
  [Test]
  public void AFileThatWouldNotReadOutranksBothKindsOfNoCeiling() {
    var directory = Path.Combine(Path.GetTempPath(), "procman-cgroup-" + Guid.NewGuid().ToString("N"));
    try {
      var inner = Path.Combine(directory, "system.slice", "broken.service");
      Directory.CreateDirectory(inner);
      File.WriteAllText(Path.Combine(directory, "system.slice", "memory.max"), "not a number\n");
      File.WriteAllText(Path.Combine(inner, "memory.max"), "max\n");
      File.WriteAllText(Path.Combine(inner, "cpu.max"), "not a quota\n");

      var info = Platform.Linux.CgroupReader.Read(directory, "/system.slice/broken.service", _NoDevices);
      Assert.That(info, Is.Not.Null);

      var memory = info!.TightestMemoryLimit();
      Assert.That(memory.Path, Is.Null);
      Assert.That(memory.Value.Reason, Is.EqualTo(UnknownReason.CounterInvalid), "and not NoLimit from the inner max");

      var (cores, reason, _, _) = info.TightestCpuQuota();
      Assert.That(cores, Is.Null);
      Assert.That(reason, Is.EqualTo(UnknownReason.CounterInvalid), "a quota is a double? and still carries why");
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  #endregion

  #region io.max (PRD §38)

  [Test]
  public void EveryDirectionOfADevicesCeilingIsRead() {
    var capped = Capped();

    Assert.That(capped.IoLimitsReason, Is.EqualTo(UnknownReason.None));
    Assert.That(capped.Io, Has.Count.EqualTo(2));

    var first = capped.Io[0];
    Assert.That((first.Major, first.Minor), Is.EqualTo((8, 0)));
    Assert.That(first.ReadBytesPerSecond.Value, Is.EqualTo(2097152ul));
    Assert.That(first.WriteOperationsPerSecond.Value, Is.EqualTo(500ul));
    Assert.That(first.IsLimited, Is.True);
  }

  /// <summary>
  /// <c>max</c> in one direction is no ceiling in that direction, and must never read as a ceiling
  /// of nought — which would say the device was closed to the group entirely.
  /// </summary>
  [Test]
  public void MaxInOneDirectionIsNoLimitAndNotANought() {
    var first = Capped().Io[0];

    Assert.That(first.WriteBytesPerSecond.HasValue, Is.False);
    Assert.That(first.WriteBytesPerSecond.Reason, Is.EqualTo(UnknownReason.NoLimit));
  }

  /// <summary>
  /// A device line the kernel wrote with every direction unlimited is not a limited device, and a
  /// page that listed it would show a row saying nothing.
  /// </summary>
  [Test]
  public void ADeviceWithNothingCappedIsNotALimitedDevice() =>
    Assert.That(Capped().Io[1].IsLimited, Is.False);

  /// <summary>
  /// No <c>io.max</c> at all means the controller is off here and an ancestor's throttling governs.
  /// That is not "nothing is capped", which is what an empty list on its own would say (PRD §5.3).
  /// </summary>
  [Test]
  public void NoIoMaxFileIsNotAnUnthrottledCgroup() {
    var free = Free();

    Assert.That(free.Io, Is.Empty);
    Assert.That(free.IoLimitsReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// The device numbers in a fixture are another machine's, so nothing here resolves them — and an
  /// unresolvable number is still a device with a limit on it rather than a row that vanishes.
  /// </summary>
  [Test]
  public void ADeviceWhoseNameCannotBeLookedUpKeepsItsNumbers() {
    var first = Capped().Io[0];

    Assert.That(first.Device, Is.Null);
    Assert.That(first.Name, Is.EqualTo("8:0"));
  }

  /// <summary>An ancestor's throttling is in the chain, where somebody can find it.</summary>
  [Test]
  public void AnAncestorsThrottlingIsInTheChain() {
    foreach (var level in Capped().Chain) {
      if (level.Path != "/system.slice")
        continue;

      Assert.That(level.IoLimits, Has.Count.EqualTo(1));
      Assert.That(level.IoLimits[0].WriteBytesPerSecond.Value, Is.EqualTo(1048576ul));
      return;
    }

    Assert.Fail("the slice is not in the chain");
  }

  #endregion

}
