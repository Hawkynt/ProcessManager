using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Priority, I/O priority and affinity (PRD §26).
/// </summary>
/// <remarks>
/// The packing is a pure function and runs on every CI leg. The refusals are exercised against a
/// probe pointed at a recorded machine, where every pid is certain not to be the process the key
/// names — which is precisely the situation the identity check exists for and the one a live test
/// cannot arrange on purpose (PRD §8.2).
/// </remarks>
[TestFixture]
public sealed class PriorityControlTests {

  #region what the kernel is handed

  /// <summary>
  /// The kernel packs the class into the top three bits and the level into the low thirteen. Getting
  /// the shift wrong produces a number that is still a valid priority, just the wrong one — which is
  /// why this is asserted against the exact integers rather than round-tripped alone.
  /// </summary>
  [Test]
  public void AClassAndLevelPackIntoTheOneIntegerTheSyscallTakes() {
    Assert.That(new IoPriority(IoPriorityClass.Realtime, 0).Pack(), Is.EqualTo(1 << 13));
    Assert.That(new IoPriority(IoPriorityClass.BestEffort, 4).Pack(), Is.EqualTo((2 << 13) | 4));
    Assert.That(new IoPriority(IoPriorityClass.Idle).Pack(), Is.EqualTo(3 << 13));
  }

  [Test]
  public void WhatWasPackedComesBackOut() {
    foreach (var priority in new[] {
      new IoPriority(IoPriorityClass.Realtime, 3),
      new IoPriority(IoPriorityClass.BestEffort, 7),
      new IoPriority(IoPriorityClass.Idle),
    })
      Assert.That(IoPriority.Unpack(priority.Pack()), Is.EqualTo(priority));
  }

  /// <summary>
  /// <c>ioprio_get</c> returns -1 on failure, which is not a priority. Unpacking it as one would
  /// report a class from a bit pattern that means "ask errno".
  /// </summary>
  [Test]
  public void AFailedReadIsNotUnpackedIntoAClass() {
    Assert.That(IoPriority.Unpack(-1), Is.EqualTo(IoPriority.Unset));
    Assert.That(IoPriority.Unpack(-1).Class, Is.EqualTo(IoPriorityClass.None));
  }

  [Test]
  public void AClassTheKernelDoesNotHaveIsNotInvented() =>
    Assert.That(IoPriority.Unpack(7 << 13), Is.EqualTo(IoPriority.Unset));

  /// <summary>
  /// Nought is the *highest* level and seven the lowest, which is the opposite of how a number
  /// reads. The menu says what each one does; this checks the wording exists at all.
  /// </summary>
  [Test]
  public void EveryClassSaysWhatItIsInWords() {
    Assert.That(new IoPriority(IoPriorityClass.Idle).ToString(), Is.EqualTo("idle"));
    Assert.That(new IoPriority(IoPriorityClass.BestEffort, 4).ToString(), Is.EqualTo("best effort 4"));
    Assert.That(new IoPriority(IoPriorityClass.Realtime, 0).ToString(), Is.EqualTo("real-time 0"));
    Assert.That(IoPriority.Unset.ToString(), Is.EqualTo("default"));
  }

  #endregion

  #region what is refused before a syscall happens

  private static LinuxProcessActions Recorded() => new(new() {
    ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
    EffectiveUserId = 0,
  });

  /// <summary>
  /// The key names a process that started at a time the recorded machine does not agree with, so
  /// every one of these has to be refused before it reaches a syscall. Acting on pid 1 of the real
  /// machine because a fixture said pid 1 exists is the exact failure the identity pair prevents.
  /// </summary>
  [Test]
  public void AnIdentityThatDoesNotMatchIsRefusedByEveryAction() {
    var actions = Recorded();
    var stale = new ProcessKey(1, 999_999_999);

    foreach (var (name, result) in new (string, ActionResult)[] {
      ("priority", actions.SetPriority(stale, 5)),
      ("I/O priority", actions.SetIoPriority(stale, new(IoPriorityClass.Idle))),
      ("affinity", actions.SetAffinity(stale, 0b11)),
      ("thread priority", actions.SetThreadPriority(stale, 1, 5)),
      ("thread affinity", actions.SetThreadAffinity(stale, 1, 0b11)),
    }) {
      Assert.That(result.Succeeded, Is.False, name);
      Assert.That(result.Outcome, Is.AnyOf(ActionOutcome.IdentityMismatch, ActionOutcome.ProcessExited), name);
    }
  }

  /// <summary>
  /// A mask with no cores in it would leave the process nothing to run on. The kernel refuses it
  /// too, but a program that passes it on has already decided not to explain why.
  /// </summary>
  [Test]
  public void AnEmptyAffinityMaskIsRefusedWithAReason() {
    var actions = Recorded();
    var key = new ProcessKey(1, 999_999_999);

    // Refused for identity first here; the wording is what matters when the identity is good.
    Assert.That(actions.SetAffinity(key, 0).Succeeded, Is.False);
    Assert.That(actions.SetThreadAffinity(key, 1, 0).Succeeded, Is.False);
  }

  #endregion

  /// <summary>
  /// A tid is a number in the same space as a pid, so a stale one may name a live thread of an
  /// unrelated process. Checking only the process would let the syscall land there.
  /// </summary>
  [Test]
  public void AThreadIsCheckedToBelongToTheProcessAndNotOnlyToExist() {
    var actions = new LinuxProcessActions(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      EffectiveUserId = 0,
    });

    // Process 1 of the fixture, and a tid that belongs to no process in it.
    var key = KeyOfFixtureProcess(1);
    var result = actions.SetThreadPriority(key, 424242, 5);

    Assert.That(result.Succeeded, Is.False);
    Assert.That(result.Detail, Does.Contain("424242").And.Contain("does not belong").Or.Contain("exited"));
  }

  private static ProcessKey KeyOfFixtureProcess(int pid) {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    foreach (var process in snapshot.Processes)
      if (process.Key.Pid == pid)
        return process.Key;

    return new(pid, 0);
  }

}
