using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That an unwritten action says so, rather than blaming the machine (PRD §7, §72.3).
/// </summary>
/// <remarks>
/// <para>
/// Readings have distinguished "this platform cannot" from "nobody has written it" since
/// <see cref="UnknownReason"/> was defined. Actions did not: every unimplemented one reported
/// <see cref="ActionOutcome.NotSupportedOnPlatform"/>, so a program running on Windows — which
/// implements six of the interface's eighteen members — told people their computer could not start
/// a process, restart one, command a window, or set a thread's priority. Every one of those is
/// something Windows plainly does.
/// </para>
/// <para>
/// One of the two is a fact about the machine and the other is a fact about this program, and only
/// the first is a reason for somebody to stop looking.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ActionOutcomeHonestyTests {

  /// <summary>Nothing but the interface's own defaults, which is what an unimplemented action hits.</summary>
  private sealed class BareActions : IProcessActions {
    public ActionResult Terminate(ProcessKey key) => ActionResult.Ok;
    public ActionResult Suspend(ProcessKey key) => ActionResult.Ok;
    public ActionResult Resume(ProcessKey key) => ActionResult.Ok;
    public ActionResult SetPriority(ProcessKey key, int priority) => ActionResult.Ok;
    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Ok;
    public ActionResult SendSignal(ProcessKey key, int signal) => ActionResult.Ok;
  }

  private static readonly ProcessKey _Key = new(1234, 5678);

  /// <summary>
  /// The six a supported platform can do and this program has not written for it: it says so about
  /// itself.
  /// </summary>
  [Test]
  public void AnUnwrittenActionBlamesTheProgramAndNotTheMachine() {
    IProcessActions bare = new BareActions();

    Assert.Multiple(() => {
      Assert.That(bare.Launch(new("x", [])).Outcome.Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "start");
      Assert.That(bare.Restart(_Key).Outcome.Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "restart");
      Assert.That(bare.CommandWindow(_Key, 1, WindowCommand.Close).Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "window");
      Assert.That(bare.SetIoPriority(_Key, new IoPriority(IoPriorityClass.Idle)).Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "io priority");
      Assert.That(bare.SetThreadPriority(_Key, 1, 0).Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "thread priority");
      Assert.That(bare.SetThreadAffinity(_Key, 1, 1).Outcome, Is.EqualTo(ActionOutcome.NotImplementedHere), "thread affinity");
    });
  }

  /// <summary>
  /// And the four that really are platform limits keep saying so. These are the ones where the
  /// concept does not exist rather than the code: an out-of-memory score, a POSIX resource limit, a
  /// cgroup freezer and a scheduler class. Downgrading them all to "not written" would have been the
  /// same mistake pointing the other way.
  /// </summary>
  [Test]
  public void ARealPlatformLimitStillSaysSo() {
    IProcessActions bare = new BareActions();

    Assert.Multiple(() => {
      Assert.That(
        bare.SetOomScoreAdjustment(_Key, 0).Outcome,
        Is.EqualTo(ActionOutcome.NotSupportedOnPlatform),
        "an out-of-memory score is a Linux idea"
      );

      Assert.That(
        bare.SetResourceLimit(_Key, ResourceLimitKind.OpenFiles, 1, 1).Outcome,
        Is.EqualTo(ActionOutcome.NotSupportedOnPlatform),
        "a POSIX resource limit is a POSIX idea"
      );

      Assert.That(
        bare.FreezeCgroup(_Key, true).Outcome,
        Is.EqualTo(ActionOutcome.NotSupportedOnPlatform),
        "a cgroup freezer is a Linux idea"
      );

      Assert.That(
        bare.SetSchedulingClass(_Key, SchedulingPolicy.Fifo, 1).Outcome,
        Is.EqualTo(ActionOutcome.NotSupportedOnPlatform),
        "mapping priority classes onto SCHED_* is the false equivalence §5.3 forbids"
      );
    });
  }

  /// <summary>
  /// No default says a platform cannot do something while naming it as though it were written. The
  /// wording is checked because it is the part a person reads: an outcome is a word in a log and a
  /// sentence is what appears in a box.
  /// </summary>
  [Test]
  public void TheWordingMatchesTheOutcome() {
    IProcessActions bare = new BareActions();

    Assert.Multiple(() => {
      foreach (var (what, result) in (ReadOnlySpan<(string, ActionResult)>)[
        ("restart", bare.Restart(_Key).Outcome),
        ("window", bare.CommandWindow(_Key, 1, WindowCommand.Close)),
        ("io priority", bare.SetIoPriority(_Key, new IoPriority(IoPriorityClass.Idle))),
        ("thread priority", bare.SetThreadPriority(_Key, 1, 0)),
      ]) {
        Assert.That(result.Detail, Is.Not.Null.And.Not.Empty, what);
        Assert.That(
          result.Detail!.Contains("this platform has no", StringComparison.OrdinalIgnoreCase)
            || result.Detail.Contains("this platform cannot", StringComparison.OrdinalIgnoreCase),
          Is.False,
          $"{what} blames the machine for something this program has not written: '{result.Detail}'"
        );
      }
    });
  }

}
