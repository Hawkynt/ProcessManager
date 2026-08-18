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
    Assert.That(kernelThread.NoNewPrivileges.HasValue, Is.False);
    Assert.That(kernelThread.EffectiveCapabilities.HasValue, Is.False);
  }

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
    Assert.That(FieldAccessor.Text(ProcessField.Capabilities, in confined, delta, 0), Is.EqualTo("0x0"));
  }

  [Test]
  public void TheyCanBeFilteredByTheWordOrByTheNumber() {
    var snapshot = this.Sample();
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
