using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The security fields (PRD §21, §36), read from the <c>status</c> the sampler already opens and
/// from <c>attr/current</c> when it is asked for.
/// </summary>
[TestFixture(false, TestName = "LinuxSecurityTests (syscalls)")]
[TestFixture(true, TestName = "LinuxSecurityTests (portable file access)")]
public sealed class LinuxSecurityTests(bool portable) {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private LinuxProbeOptions Options => new() {
    UsePortableFileAccess = portable,
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  };

  /// <summary>
  /// What a run that named one of the six status columns of §20 and §21 asks the probe for. They
  /// are opt-in for a cost that is neither a read nor an allocation: five more labels to recognise
  /// in a loop that runs fifty times per process, measured against main at seven to eight
  /// milliseconds per thousand processes (PRD §5.4, §71.2).
  /// </summary>
  private LinuxProbeOptions Secure => this.Options with { ReadSecurityStatus = true };

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"pid {pid} is not in the snapshot");
    return default;
  }

  private SystemSnapshot Sample(LinuxProbeOptions? options = null) {
    using var probe = new LinuxProbe(options ?? this.Options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    return snapshot;
  }

  [Test]
  public void TheEffectiveUidDecidesElevationRatherThanTheRealOne() {
    var snapshot = this.Sample();

    // pid 1002 is launched by alice (real uid 1000) and runs as root (effective 0) — a setuid
    // binary. Reading the real uid would call it unprivileged, which is the whole point of the
    // distinction.
    var setuid = Find(snapshot, 1002);
    Assert.That(setuid.UserId, Is.EqualTo(1000), "real uid");
    Assert.That(setuid.EffectiveUserId, Is.EqualTo(0), "effective uid");
    Assert.That(setuid.IsElevated.Value, Is.EqualTo(1ul));

    var ordinary = Find(snapshot, 1000);
    Assert.That(ordinary.IsElevated.Value, Is.EqualTo(0ul));
  }

  [Test]
  public void SeccompAndNoNewPrivsAreRead() {
    var snapshot = this.Sample();

    var confined = Find(snapshot, 1000);
    Assert.That(confined.SeccompMode.Value, Is.EqualTo(2ul), "2 is a filter");
    Assert.That(confined.NoNewPrivileges.Value, Is.EqualTo(1ul));

    var unconfined = Find(snapshot, 1001);
    Assert.That(unconfined.SeccompMode.Value, Is.EqualTo(0ul));
    Assert.That(unconfined.NoNewPrivileges.Value, Is.EqualTo(0ul));
  }

  /// <summary>
  /// How many filters, not just whether there is one: two of them is a process something sandboxed
  /// twice, and the mode alone cannot say that.
  /// </summary>
  [Test]
  public void TheFilterCountIsReadWhereTheKernelWritesIt() {
    var snapshot = this.Sample();

    Assert.That(Find(snapshot, 1000).SeccompFilters.Value, Is.EqualTo(3ul));
    Assert.That(Find(snapshot, 1).SeccompFilters.Value, Is.EqualTo(0ul), "a real zero");

    // Seccomp_filters arrived in 5.9. Fixture 1001 has no such line, and a kernel that does not
    // write one has not said there are none.
    var older = Find(snapshot, 1001);
    Assert.That(older.SeccompFilters.HasValue, Is.False);
    Assert.That(older.SeccompMode.Value, Is.EqualTo(0ul), "the mode is still there");
  }

  /// <summary>
  /// <c>Seccomp_filters:</c> begins with the seven characters of <c>Seccomp:</c>, and it is the very
  /// next line. A prefix match that dropped the colon would read the filter count as the mode, and
  /// the two are numbers in overlapping ranges — so the fixture gives one process a mode and a count
  /// that differ, which is the only shape that catches it.
  /// </summary>
  [Test]
  public void TheFilterCountIsNotMistakenForTheMode() {
    var confined = Find(this.Sample(), 1000);

    Assert.That(confined.SeccompMode.Value, Is.EqualTo(2ul), "the mode");
    Assert.That(confined.SeccompFilters.Value, Is.EqualTo(3ul), "the count, from the next line");
  }

  /// <summary>
  /// The mask is separated from its label by a tab, not a space. Trimming only spaces left the tab
  /// in front of the digits and the hex parser stopped on it, reporting every process on the machine
  /// as having no capabilities at all — a security field that was confidently, silently wrong.
  /// </summary>
  [Test]
  public void TheCapabilityMaskIsParsedPastTheTabThatFollowsItsLabel() {
    var snapshot = this.Sample();

    Assert.That(Find(snapshot, 1).EffectiveCapabilities.Value, Is.EqualTo(0x000001ffffffffffUL));
    Assert.That(Find(snapshot, 1002).EffectiveCapabilities.Value, Is.EqualTo(0x0000003fffffffffUL));
    Assert.That(Find(snapshot, 1000).EffectiveCapabilities.Value, Is.EqualTo(0UL), "a real zero");
  }

  /// <summary>
  /// A kernel that does not write the line at all must leave the field unknown. default(Counter) is
  /// a confident zero, so forgetting this would claim every such process is unprivileged and
  /// unconfined (PRD §72.3).
  /// </summary>
  [Test]
  public void AStatusWithoutTheseLinesLeavesThemUnknownRatherThanZero() {
    var kernelThread = Find(this.Sample(), 2);

    Assert.That(kernelThread.SeccompMode.HasValue, Is.False);
    Assert.That(kernelThread.SeccompMode.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(kernelThread.SeccompFilters.HasValue, Is.False);
    Assert.That(kernelThread.NoNewPrivileges.HasValue, Is.False);
    Assert.That(kernelThread.EffectiveCapabilities.HasValue, Is.False);
    Assert.That(kernelThread.PermittedCapabilities.HasValue, Is.False);
    Assert.That(kernelThread.InheritableCapabilities.HasValue, Is.False);
    Assert.That(kernelThread.BoundingCapabilities.HasValue, Is.False);
    Assert.That(kernelThread.AmbientCapabilities.HasValue, Is.False);
  }

  #region the other four capability sets

  /// <summary>
  /// Five masks, five different questions. Reading only the effective one hides a process that has
  /// put a capability down and can pick it up again without asking anybody (PRD §21).
  /// </summary>
  [Test]
  public void AllFiveSetsAreRead() {
    var systemd = Find(this.Sample(), 1);

    Assert.That(systemd.InheritableCapabilities.Value, Is.EqualTo(0UL));
    Assert.That(systemd.PermittedCapabilities.Value, Is.EqualTo(0x000001ffffffffffUL));
    Assert.That(systemd.EffectiveCapabilities.Value, Is.EqualTo(0x000001ffffffffffUL));
    Assert.That(systemd.BoundingCapabilities.Value, Is.EqualTo(0x000001ffffffffffUL));
    Assert.That(systemd.AmbientCapabilities.Value, Is.EqualTo(0UL));
  }

  /// <summary>
  /// The five labels differ only in their last three characters, so a parser matching them in the
  /// wrong order — or on too short a prefix — would fill every field from one line. This is the
  /// shape that catches it: a process whose five masks are five different numbers.
  /// </summary>
  [Test]
  public void TheFiveLabelsAreNotConfusedWithEachOther() {
    var setuid = Find(this.Sample(), 1002);

    Assert.That(setuid.InheritableCapabilities.Value, Is.EqualTo(0UL), "CapInh");
    Assert.That(setuid.PermittedCapabilities.Value, Is.EqualTo(0x0000003fffffffffUL), "CapPrm");
    Assert.That(setuid.EffectiveCapabilities.Value, Is.EqualTo(0x0000003fffffffffUL), "CapEff");
    Assert.That(setuid.BoundingCapabilities.Value, Is.EqualTo(0x000001ffffffffffUL), "CapBnd");
    Assert.That(setuid.AmbientCapabilities.Value, Is.EqualTo(0x20UL), "CapAmb");
  }

  /// <summary>
  /// A column that says <c>0x0000000000003000</c> answers no question anybody has. The names are
  /// the ones <c>capsh --decode</c> prints, so a reader can paste one straight into a unit file.
  /// </summary>
  [Test]
  public void TheColumnShowsNamesRatherThanAMask() {
    var snapshot = this.Sample();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var networked = Find(snapshot, 1001);
    Assert.That(
      FieldAccessor.Text(ProcessField.Capabilities, in networked, delta, 0),
      Is.EqualTo("cap_net_admin,cap_net_raw")
    );

    // The raw mask stays one field away, in the form the kernel's own tools accept.
    Assert.That(
      FieldAccessor.Text(ProcessField.CapabilitiesHex, in networked, delta, 0),
      Is.EqualTo("0x0000000000003000")
    );

    // Forty-one names would fill a screen and say less than one word does.
    var systemd = Find(snapshot, 1);
    Assert.That(FieldAccessor.Text(ProcessField.Capabilities, in systemd, delta, 0), Is.EqualTo("all"));
    Assert.That(FieldAccessor.Text(ProcessField.AmbientCapabilities, in systemd, delta, 0), Is.EqualTo("none"));
  }

  /// <summary>
  /// A kernel newer than this table. The bounding set of fixture 1001 holds two bits no released
  /// kernel had; both must be reported, because the one direction a privilege must never be rounded
  /// is downwards.
  /// </summary>
  [Test]
  public void ACapabilityThisBuildHasNoNameForIsStillReported() {
    var snapshot = this.Sample();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var process = Find(snapshot, 1001);
    Assert.That(process.BoundingCapabilities.Value, Is.EqualTo(0x0000060000000000UL));
    Assert.That(FieldAccessor.Text(ProcessField.BoundingCapabilities, in process, delta, 0), Is.EqualTo("41,42"));
  }

  #endregion

  #region who the process is

  /// <summary>
  /// All four ids, not two. A process whose real and effective ids are an ordinary user while the
  /// saved one is root has given up nothing at all — it can call <c>seteuid(0)</c> whenever it
  /// likes — and only the quartet says so (PRD §36).
  /// </summary>
  [Test]
  public void TheWholeUidAndGidQuartetsAreRead() {
    var snapshot = this.Sample();

    var setuid = Find(snapshot, 1002);
    Assert.That(setuid.UserId, Is.EqualTo(1000), "real");
    Assert.That(setuid.EffectiveUserId, Is.EqualTo(0), "effective");
    Assert.That(setuid.SavedUserId, Is.EqualTo(0), "saved");
    Assert.That(setuid.FilesystemUserId, Is.EqualTo(1000), "filesystem");

    var setgid = Find(snapshot, 1001);
    Assert.That(setgid.GroupId, Is.EqualTo(1000), "real");
    Assert.That(setgid.EffectiveGroupId, Is.EqualTo(44), "effective");
    Assert.That(setgid.SavedGroupId, Is.EqualTo(1000), "saved");
    Assert.That(setgid.FilesystemGroupId, Is.EqualTo(44), "filesystem");
  }

  /// <summary>
  /// The account the process runs <em>as</em>, which for anything set-user-ID is not the account
  /// that started it. Both names are kept, because the difference is the point.
  /// </summary>
  [Test]
  public void TheEffectiveAccountIsNamedAsWellAsTheRealOne() {
    var setuid = Find(this.Sample(), 1002);

    Assert.That(setuid.UserName, Is.EqualTo("alice"));
    Assert.That(setuid.EffectiveUserName, Is.EqualTo("root"));
  }

  /// <summary>
  /// A set-group-ID binary is the same kind of thing as a set-user-ID one, so the field notices both
  /// — and says nothing at all when it has not been told either.
  /// </summary>
  [Test]
  public void APrivilegeChangeIsNoticedForGroupsAsWellAsUsers() {
    var snapshot = this.Sample();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    static string Text(SystemSnapshot snapshot, SnapshotDelta delta, int pid) {
      var process = Find(snapshot, pid);
      return FieldAccessor.Text(ProcessField.PrivilegeChanged, in process, delta, 0);
    }

    Assert.That(Text(snapshot, delta, 1002), Is.EqualTo("yes"), "set-user-ID");
    Assert.That(Text(snapshot, delta, 1001), Is.EqualTo("yes"), "set-group-ID");
    Assert.That(Text(snapshot, delta, 1000), Is.EqualTo("no"));
    Assert.That(Text(snapshot, delta, 1), Is.EqualTo("no"), "root started as root");
  }

  /// <summary>
  /// -1, never 0. Zero is root, so an id nobody filled would name the superuser for every process
  /// on a platform that does not report the quartet at all (PRD §5.3).
  /// </summary>
  [Test]
  public void AnIdNobodyReportedIsNotRoot() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "test";
    records[0].UserId = -1;
    records[0].EffectiveUserId = -1;
    records[0].SavedUserId = -1;
    records[0].GroupId = -1;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    foreach (var field in new[] {
      ProcessField.UserId, ProcessField.EffectiveUserId, ProcessField.SavedUserId, ProcessField.GroupId,
    }) {
      Assert.That(FieldAccessor.Number(field, in snapshot.Processes[0], delta, 0), Is.Null, field.ToString());
      Assert.That(
        FieldAccessor.Text(field, in snapshot.Processes[0], delta, 0),
        Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)),
        field.ToString()
      );
    }

    // And with nothing to compare, the derived field must not claim the identity is unchanged.
    Assert.That(
      FieldAccessor.Number(ProcessField.PrivilegeChanged, in snapshot.Processes[0], delta, 0),
      Is.Null
    );
  }

  #endregion

  #region supplementary groups

  [Test]
  public void TheGroupsAreNotKeptUnlessTheyWereAskedFor() {
    var process = Find(this.Sample(), 1000);

    Assert.That(process.SupplementaryGroups, Is.Null);
    Assert.That(process.SupplementaryGroupsReason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  [Test]
  public void TheGroupsAreKeptWhenTheyAre() {
    var snapshot = this.Sample(this.Options with { ReadSupplementaryGroups = true });

    Assert.That(Find(snapshot, 1000).SupplementaryGroups, Is.EqualTo("998 1000"));
    Assert.That(Find(snapshot, 1001).SupplementaryGroups, Is.EqualTo("44 1000"));
  }

  /// <summary>
  /// systemd is in no supplementary group and a kernel thread has no such line at all. Those are
  /// different answers and the program says so — the first is a fact about the process, the second
  /// is the absence of one (PRD §72.3).
  /// </summary>
  [Test]
  public void BelongingToNoGroupIsNotTheSameAsNobodySaying() {
    var snapshot = this.Sample(this.Options with { ReadSupplementaryGroups = true });
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var systemd = Find(snapshot, 1);
    Assert.That(systemd.SupplementaryGroups, Is.EqualTo(string.Empty));
    Assert.That(FieldAccessor.Text(ProcessField.SupplementaryGroups, in systemd, delta, 0), Is.EqualTo("none"));

    var kernelThread = Find(snapshot, 2);
    Assert.That(kernelThread.SupplementaryGroups, Is.Null);
    Assert.That(
      FieldAccessor.Text(ProcessField.SupplementaryGroups, in kernelThread, delta, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  #endregion

  #region the mitigation state (PRD §21)

  /// <summary>
  /// Linux has no per-process mitigation <em>policy</em> the way Windows does, but it does publish
  /// the mitigation <em>state</em>, and it is per task rather than per machine: the fixture's shell
  /// asked for both mitigations and the process beside it did not.
  /// </summary>
  [Test]
  public void TheSpeculationStatesAreReadFromTheStatusTheSamplerAlreadyHas() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var systemd = Find(snapshot, 1);
    Assert.That(
      FieldAccessor.Text(ProcessField.SpeculationStoreBypass, in systemd, delta, 0),
      Is.EqualTo("thread vulnerable")
    );
    Assert.That(
      FieldAccessor.Text(ProcessField.SpeculationIndirectBranch, in systemd, delta, 0),
      Is.EqualTo("conditional enabled")
    );

    var confined = Find(snapshot, 1000);
    Assert.That(
      FieldAccessor.Text(ProcessField.SpeculationStoreBypass, in confined, delta, 0),
      Is.EqualTo("thread mitigated")
    );
    Assert.That(
      FieldAccessor.Text(ProcessField.SpeculationIndirectBranch, in confined, delta, 0),
      Is.EqualTo("conditional force disabled")
    );
  }

  /// <summary>
  /// Three kernel vintages on one fixture. The store-bypass line arrived in 4.17, the seccomp filter
  /// count in 5.9, the indirect-branch line in 5.11 and the thread features in 6.6 — so a kernel
  /// between them writes some of these and not others, and every absence must read as an absence
  /// rather than as the safest value in the table (PRD §72.3).
  /// </summary>
  [Test]
  public void AKernelTooOldForALineLeavesItUnknownRatherThanSafe() {
    var older = Find(this.Sample(this.Secure), 1001);

    Assert.That(older.SpeculationStoreBypass.HasValue, Is.True, "4.17 and newer write this one");
    Assert.That(
      (SpeculationState)older.SpeculationStoreBypass.Value,
      Is.EqualTo(SpeculationState.NotVulnerable)
    );

    Assert.That(older.SpeculationIndirectBranch.HasValue, Is.False, "5.11 added this line");
    Assert.That(older.SpeculationIndirectBranch.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(older.ThreadFeatures.HasValue, Is.False, "6.6 added this one");
    Assert.That(older.SeccompFilters.HasValue, Is.False, "and 5.9 this one");
  }

  /// <summary>
  /// <c>x86_Thread_features_locked:</c> is the very next line and begins with every character of
  /// <c>x86_Thread_features</c>. A prefix match that dropped the colon would read the locked set as
  /// the enabled one — the same mistake that once read <c>Seccomp_filters</c> as the seccomp mode —
  /// so the fixture gives one process two lines whose contents differ.
  /// </summary>
  [Test]
  public void TheLockedFeatureSetIsNotMistakenForTheEnabledOne() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    // The fixture's enabled set is "shstk" while its locked set is "shstk wrss".
    var confined = Find(snapshot, 1000);
    Assert.That(
      (ThreadSecurityFeatures)confined.ThreadFeatures.Value,
      Is.EqualTo(ThreadSecurityFeatures.ShadowStack)
    );
    Assert.That(FieldAccessor.Text(ProcessField.ThreadFeatures, in confined, delta, 0), Is.EqualTo("shstk"));

    var both = Find(snapshot, 1002);
    Assert.That(
      (ThreadSecurityFeatures)both.ThreadFeatures.Value,
      Is.EqualTo(ThreadSecurityFeatures.ShadowStack | ThreadSecurityFeatures.WriteableShadowStack)
    );
  }

  /// <summary>
  /// A process with no shadow stack is the ordinary case, and "none" is what it says. The kernel
  /// thread beside it has no such line at all, which is a different statement — and the one that
  /// would have been a confident "no protections here" if the field defaulted to zero.
  /// </summary>
  [Test]
  public void NoFeaturesIsNotTheSameAsNoLine() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var systemd = Find(snapshot, 1);
    Assert.That(systemd.ThreadFeatures.HasValue, Is.True);
    Assert.That(FieldAccessor.Text(ProcessField.ThreadFeatures, in systemd, delta, 0), Is.EqualTo("none"));

    var kernelThread = Find(snapshot, 2);
    Assert.That(kernelThread.ThreadFeatures.HasValue, Is.False);
    Assert.That(kernelThread.SpeculationStoreBypass.HasValue, Is.False);
    Assert.That(kernelThread.SpeculationIndirectBranch.HasValue, Is.False);
    Assert.That(
      FieldAccessor.Text(ProcessField.ThreadFeatures, in kernelThread, delta, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  #endregion

  /// <summary>
  /// Recognising these five lines is not free, so no run pays for it unless a column or a filter
  /// named one of the six fields — and "nobody asked" must not be reported as "this kernel does not
  /// write it", which would be a mitigation column saying "not supported here" on a kernel that
  /// supports it perfectly well (PRD §5.4, §72.3).
  /// </summary>
  [Test]
  public void TheStatusSecurityLinesAreNotReadUnlessTheyWereAskedFor() {
    var process = Find(this.Sample(), 1);

    foreach (var counter in new[] {
      process.SpeculationStoreBypass, process.SpeculationIndirectBranch, process.ThreadFeatures,
      process.Umask, process.TracerPid, process.DescriptorTableSize,
    }) {
      Assert.That(counter.HasValue, Is.False);
      Assert.That(counter.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    }

    // And when they are asked for, the same process answers all six.
    var asked = Find(this.Sample(this.Secure), 1);
    Assert.That(asked.SpeculationStoreBypass.HasValue, Is.True);
    Assert.That(asked.Umask.HasValue, Is.True);
    Assert.That(asked.DescriptorTableSize.HasValue, Is.True);
  }

  #region the umask, the tracer and the descriptor table

  /// <summary>
  /// Base eight. Reading <c>0022</c> as the decimal twenty-two would name a mask that withholds
  /// nothing anybody expects, and no other column on the row would contradict it.
  /// </summary>
  [Test]
  public void TheUmaskIsReadAsOctalAndShownAsOctal() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var ordinary = Find(snapshot, 1000);
    Assert.That(ordinary.Umask.Value, Is.EqualTo(0b000_010_010UL), "0022 is eighteen, not twenty-two");
    Assert.That(FieldAccessor.Text(ProcessField.Umask, in ordinary, delta, 0), Is.EqualTo("0022"));

    var strict = Find(snapshot, 1001);
    Assert.That(strict.Umask.Value, Is.EqualTo(0b000_111_111UL), "0077");
    Assert.That(FieldAccessor.Text(ProcessField.Umask, in strict, delta, 0), Is.EqualTo("0077"));

    // Nought is a real mask and a finding: this process withholds nothing from anything it creates.
    var open = Find(snapshot, 2);
    Assert.That(open.Umask.HasValue, Is.True);
    Assert.That(FieldAccessor.Text(ProcessField.Umask, in open, delta, 0), Is.EqualTo("0000"));
  }

  /// <summary>
  /// Zero means nobody is attached, which is the usual answer and is not a missing one. Verified
  /// against the kernel by attaching to a child with <c>PTRACE_ATTACH</c>: the line went from 0 to
  /// the tracer's pid and back to 0 on detach.
  /// </summary>
  [Test]
  public void TheTracerIsNamedRatherThanCountedAndNoneReadsAsNone() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var traced = Find(snapshot, 1000);
    Assert.That(traced.TracerPid.Value, Is.EqualTo(1002UL));
    Assert.That(FieldAccessor.Text(ProcessField.TracerPid, in traced, delta, 0), Is.EqualTo("1002"));

    var untraced = Find(snapshot, 1);
    Assert.That(untraced.TracerPid.Value, Is.EqualTo(0UL));
    Assert.That(FieldAccessor.Text(ProcessField.TracerPid, in untraced, delta, 0), Is.EqualTo("none"));

    var noLine = Find(snapshot, 2);
    Assert.That(noLine.TracerPid.HasValue, Is.False);
  }

  /// <summary>
  /// A capacity, not a count and not a ceiling. On the machine this was written on a shell held four
  /// open descriptors with a table of 256 and an <c>RLIMIT_NOFILE</c> of 524288: three numbers, none
  /// of them the other two, which is why this is its own field and not <c>handles.peak</c>
  /// (PRD §20).
  /// </summary>
  [Test]
  public void TheDescriptorTableSizeIsItsOwnNumber() {
    var snapshot = this.Sample(this.Secure with { CountFileDescriptors = true });
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var confined = Find(snapshot, 1000);
    Assert.That(confined.DescriptorTableSize.Value, Is.EqualTo(64UL));
    Assert.That(FieldAccessor.Text(ProcessField.DescriptorTableSize, in confined, delta, 0), Is.EqualTo("64"));

    // The table has room for more than the process is using, which is the whole point of reporting
    // it: it is an upper bound on what was once held, not what is held now.
    Assert.That(confined.HandleCount.HasValue, Is.True);
    Assert.That(confined.HandleCount.Value, Is.LessThan(confined.DescriptorTableSize.Value));

    Assert.That(Find(snapshot, 1).DescriptorTableSize.Value, Is.EqualTo(512UL));
    Assert.That(Find(snapshot, 2).DescriptorTableSize.HasValue, Is.False, "no line, no number");
  }

  #endregion

  #region the LSM label

  [Test]
  public void TheSecurityContextIsNotReadUnlessItWasAskedFor() {
    var process = Find(this.Sample(), 1000);

    Assert.That(process.SecurityContext, Is.Null);
    Assert.That(process.SecurityContextReason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  [Test]
  public void TheSecurityContextIsReadWhenItIs() {
    var process = Find(this.Sample(this.Options with { ReadSecurityContext = true }), 1000);

    // The file is NUL-terminated; neither the NUL nor a trailing newline may reach the column.
    Assert.That(process.SecurityContext, Is.EqualTo("/usr/bin/bash (enforce)"));
  }

  [Test]
  public void AKernelWithNoSecurityModuleSaysSoRatherThanSayingNothing() {
    // Fixture 1 has no attr/current at all, which is what a kernel with no LSM looks like.
    var process = Find(this.Sample(this.Options with { ReadSecurityContext = true }), 1);

    Assert.That(process.SecurityContext, Is.Null);
    Assert.That(process.SecurityContextReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(
      FieldAccessor.Text(ProcessField.SecurityContext, in process, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  /// <summary>
  /// The bracketed word is a fact the label column already shows and cannot be sorted on. Splitting
  /// it out is what makes "which of these are only being watched" one click rather than a read of
  /// six hundred labels.
  /// </summary>
  [Test]
  public void TheConfinementModeComesOutOfTheLabelTheLsmColumnAlreadyRead() {
    var snapshot = this.Sample(this.Options with { ReadSecurityContext = true });
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var confined = Find(snapshot, 1000);
    Assert.That(confined.SecurityContext, Is.EqualTo("/usr/bin/bash (enforce)"));
    Assert.That((LsmConfinementMode)confined.ConfinementMode.Value, Is.EqualTo(LsmConfinementMode.Enforce));
    Assert.That(FieldAccessor.Text(ProcessField.ConfinementMode, in confined, delta, 0), Is.EqualTo("enforce"));
  }

  /// <summary>
  /// It costs no read of its own, so it must not appear without one: nothing turns the label on but
  /// somebody naming a column, and asking for the mode has to be one of those names or the column
  /// ships permanently empty (PRD §5.4).
  /// </summary>
  [Test]
  public void TheModeIsNotFilledUnlessTheLabelWasAskedFor() {
    var process = Find(this.Sample(), 1000);

    Assert.That(process.ConfinementMode.HasValue, Is.False);
    Assert.That(process.ConfinementMode.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// A kernel with no security module has no label and therefore no mode, and the two say the same
  /// thing rather than one of them claiming the process is unconfined.
  /// </summary>
  [Test]
  public void NoLabelMeansNoModeRatherThanUnconfined() {
    var process = Find(this.Sample(this.Options with { ReadSecurityContext = true }), 1);

    Assert.That(process.ConfinementMode.HasValue, Is.False);
    Assert.That(process.ConfinementMode.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(
      FieldAccessor.Text(ProcessField.ConfinementMode, in process, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  #endregion

  #region how they read and filter

  [Test]
  public void TheFieldsRenderAsWordsRatherThanNumbers() {
    var snapshot = this.Sample();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var setuid = Find(snapshot, 1002);
    Assert.That(FieldAccessor.Text(ProcessField.Elevated, in setuid, delta, 0), Is.EqualTo("yes"));

    var confined = Find(snapshot, 1000);
    Assert.That(FieldAccessor.Text(ProcessField.Seccomp, in confined, delta, 0), Is.EqualTo("filter"));
    Assert.That(FieldAccessor.Text(ProcessField.NoNewPrivileges, in confined, delta, 0), Is.EqualTo("yes"));
    Assert.That(FieldAccessor.Text(ProcessField.Capabilities, in confined, delta, 0), Is.EqualTo("none"));
  }

  [Test]
  public void TheyCanBeFilteredByTheWordOrByTheNumber() {
    var snapshot = this.Sample(this.Secure);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    static List<int> Matching(string query, SystemSnapshot snapshot, SnapshotDelta delta) {
      Assert.That(ProcessQuery.TryParse(query, out var parsed, out var error), Is.True, error);
      var pids = new List<int>();
      var processes = snapshot.Processes;
      for (var i = 0; i < processes.Length; ++i)
        if (parsed.Matches(in processes[i], delta, i))
          pids.Add(processes[i].Pid);

      // The snapshot is in directory-enumeration order, which puts "2" after "1002". Sorting keeps
      // the expectations about which processes matched rather than about how /proc was walked.
      pids.Sort();
      return pids;
    }

    // kthreadd is in here too: a kernel thread has effective uid 0, so it is running as root
    // whatever else it is.
    Assert.That(Matching("elevated:yes", snapshot, delta), Is.EqualTo(new[] { 1, 2, 1002 }));
    Assert.That(Matching("elevated:1", snapshot, delta), Is.EqualTo(new[] { 1, 2, 1002 }), "the number works too");
    Assert.That(Matching("seccomp:filter", snapshot, delta), Is.EqualTo(new[] { 1000 }));
    Assert.That(Matching("nnp:yes", snapshot, delta), Is.EqualTo(new[] { 1000 }));

    // kthreadd's seccomp is unknown, so it matches neither side — the same rule as every other
    // unknown value in the program.
    Assert.That(Matching("seccomp:off", snapshot, delta), Does.Not.Contain(2));

    // "which processes may reconfigure the network" is the question a capability column exists for,
    // and it is asked by name rather than by working out which bit that is. The two root processes
    // are in the answer because they hold every capability there is — which is exactly the case a
    // filter reading the abbreviated column text would have missed, since that text says "all".
    Assert.That(Matching("caps:cap_net_admin", snapshot, delta), Is.EqualTo(new[] { 1, 1001, 1002 }));
    Assert.That(Matching("caps.bounding:cap_sys_module", snapshot, delta), Is.EqualTo(new[] { 1, 1000, 1002 }));
    // The kernel's own words, so the question reads the way it would be said aloud. The trap is
    // worth asserting rather than hiding: "vulnerable" is a substring of "not vulnerable", so the
    // loose form catches the safe process along with the exposed one. That is the Contains operator
    // behaving the way it does for every text field in the language rather than anything about this
    // one, and the exact form is how the question gets asked precisely.
    Assert.That(Matching("spec.ssb:vulnerable", snapshot, delta), Is.EqualTo(new[] { 1, 1001 }));
    Assert.That(Matching("spec.ssb==\"thread vulnerable\"", snapshot, delta), Is.EqualTo(new[] { 1 }));
    Assert.That(Matching("spec.ssb:\"not vulnerable\"", snapshot, delta), Is.EqualTo(new[] { 1001 }));
    Assert.That(Matching("spec.ib:\"conditional force disabled\"", snapshot, delta), Is.EqualTo(new[] { 1000 }));
    Assert.That(Matching("shstk:shstk", snapshot, delta), Is.EqualTo(new[] { 1000, 1002 }));
    Assert.That(Matching("shstk:none", snapshot, delta), Is.EqualTo(new[] { 1 }));

    // The octal digits, because that is the form somebody has the number in. The kernel holds a
    // mask to nine bits, so all four digits are always there and comparing them as text gives the
    // same order as comparing them as numbers: "withholds less than the usual" is still a
    // comparison, and it still finds the process that withholds nothing.
    Assert.That(Matching("umask:0077", snapshot, delta), Is.EqualTo(new[] { 1001 }));
    Assert.That(Matching("umask<0022", snapshot, delta), Is.EqualTo(new[] { 2 }));
    Assert.That(Matching("tracer>0", snapshot, delta), Is.EqualTo(new[] { 1000 }));
    Assert.That(Matching("fd.table>=256", snapshot, delta), Is.EqualTo(new[] { 1, 1002 }));

    Assert.That(Matching("setuid:yes", snapshot, delta), Is.EqualTo(new[] { 1001, 1002 }));
    Assert.That(Matching("euid:0", snapshot, delta), Is.EqualTo(new[] { 1, 2, 1002 }));
    Assert.That(Matching("uid:1000", snapshot, delta), Is.EqualTo(new[] { 1000, 1001, 1002 }));
    Assert.That(Matching("egid:44", snapshot, delta), Is.EqualTo(new[] { 1001 }));
  }

  #endregion

  /// <summary>
  /// Elevated and System are different categories on purpose: one is a process root started, the
  /// other is a process a user started that is root now (PRD §23).
  /// </summary>
  [Test]
  public void APrivilegeGainingProcessIsColouredDifferentlyFromARootOwnedOne() {
    var snapshot = this.Sample();

    Assert.That(
      ProcessCategories.Classify(Find(snapshot, 1002), currentUserId: 1000, isNew: false),
      Is.EqualTo(ProcessCategory.Elevated)
    );

    Assert.That(
      ProcessCategories.Classify(Find(snapshot, 1), currentUserId: 1000, isNew: false),
      Is.EqualTo(ProcessCategory.System)
    );

    Assert.That(
      ProcessCategories.Classify(Find(snapshot, 1000), currentUserId: 1000, isNew: false),
      Is.EqualTo(ProcessCategory.Own)
    );
  }

}
