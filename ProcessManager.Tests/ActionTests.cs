using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The subtree walk that "end process tree" depends on.
/// </summary>
[TestFixture]
public sealed class ProcessTreeWalkTests {

  [Test]
  public void DescendantsComeBeforeTheirParent() {
    // Ending the parent first reparents its children to init, and they are then no longer findable
    // as its descendants — so a kill-tree that starts at the top kills the top and loses the rest.
    var snapshot = Build((1, 0), (10, 1), (11, 10), (12, 11));

    var order = ProcessTree.DescendantsFirst(snapshot, 10);

    Assert.That(order.Select(k => k.Pid), Is.EqualTo(new[] { 12, 11, 10 }));
  }

  [Test]
  public void SiblingsAreAllIncluded() {
    var snapshot = Build((1, 0), (10, 1), (11, 10), (12, 10), (13, 12));

    var order = ProcessTree.DescendantsFirst(snapshot, 10);

    Assert.That(order.Select(k => k.Pid), Is.EquivalentTo(new[] { 10, 11, 12, 13 }));
    Assert.That(order[^1].Pid, Is.EqualTo(10), "the root is ended last");
    Assert.That(order.Select(k => k.Pid).ToList().IndexOf(13), Is.LessThan(order.Select(k => k.Pid).ToList().IndexOf(12)));
  }

  [Test]
  public void ALeafIsJustItself() {
    var snapshot = Build((1, 0), (10, 1));
    Assert.That(ProcessTree.DescendantsFirst(snapshot, 10).Select(k => k.Pid), Is.EqualTo(new[] { 10 }));
  }

  [Test]
  public void AnUnknownPidYieldsNothingRatherThanEverything() {
    // The failure mode this guards is the worst one available: a walk that treats "not found" as
    // "match everything" would end every process on the machine.
    var snapshot = Build((1, 0), (10, 1));
    Assert.That(ProcessTree.DescendantsFirst(snapshot, 999), Is.Empty);
  }

  [Test]
  public void ACycleTerminates() {
    var snapshot = Build((10, 11), (11, 10));
    Assert.That(() => ProcessTree.DescendantsFirst(snapshot, 10), Throws.Nothing);
  }

  [Test]
  public void FindReturnsTheIdentityNotJustThePid() {
    var snapshot = Build((1, 0), (10, 1));
    var key = ProcessTree.Find(snapshot, 10);

    Assert.That(key.Pid, Is.EqualTo(10));
    Assert.That(key.StartTicks, Is.EqualTo(1000ul));
    Assert.That(ProcessTree.Find(snapshot, 999).IsNone, Is.True);
  }

  private static SystemSnapshot Build(params (int Pid, int ParentPid)[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = 0 };
    snapshot.System.CoreCount = 1;
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000ul);
      buffer[i].ParentPid = processes[i].ParentPid;
      buffer[i].Name = $"p{processes[i].Pid}";
    }

    return snapshot;
  }

}

/// <summary>
/// The identity check every action performs before it acts (PRD §8.2).
/// </summary>
/// <remarks>
/// These run against the recorded /proc tree, so they check the refusal paths without ending
/// anything: a real process is not needed to prove that the wrong process is refused, and a test
/// that terminates something to prove it can is not a test anyone should run in CI.
/// </remarks>
[TestFixture]
public sealed class LinuxProcessActionIdentityTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProcessActions Actions() => new(new() { ProcRoot = FixtureRoot });

  [Test]
  public void APidWhoseStartTimeDoesNotMatchIsRefused() {
    // The whole reason the key is a pair. Between the click and the syscall the pid can be recycled;
    // acting on it because the number matched is how the wrong program gets killed.
    var result = Actions().Terminate(new(1000, 999_999));

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
    Assert.That(result.Detail, Does.Contain("reused"));
  }

  [Test]
  public void APidThatIsNotThereIsReportedAsExited() {
    var result = Actions().Terminate(new(4242, 1));
    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.ProcessExited));
  }

  [Test]
  public void APidOfZeroOrLessIsRefusedOutright() {
    Assert.That(Actions().Terminate(new(0, 0)).Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(Actions().Suspend(new(-1, 0)).Outcome, Is.EqualTo(ActionOutcome.Refused));
  }

  [Test]
  public void AnEmptyAffinityMaskIsRefusedBeforeAnythingIsAttempted() {
    // A mask with no cores in it would leave the process nothing to run on. The refusal has to come
    // before the identity check, because it is wrong regardless of which process it names.
    var result = Actions().SetAffinity(new(1000, 100000), 0);
    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
  }

  [Test]
  public void EveryActionChecksIdentityRatherThanJustTheTerminateOne() {
    var wrong = new ProcessKey(1000, 999_999);
    var actions = Actions();

    Assert.Multiple(() => {
      Assert.That(actions.Terminate(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.Suspend(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.Resume(wrong).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetPriority(wrong, 5).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SetAffinity(wrong, 0b11).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
      Assert.That(actions.SendSignal(wrong, 15).Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
    });
  }

}
