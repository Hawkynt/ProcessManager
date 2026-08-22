using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// §69's four classes, and the policy that reads them.
/// </summary>
/// <remarks>
/// The classes were written down long before anything consulted them, which is the same as not
/// having them: every confirmation asked the one settings flag and none of them asked what the
/// request was. What is asserted here is the half that closes that — that class 0 never asks, that
/// classes 1 and 2 are the ones the setting governs, that class 3 is not something a preference can
/// switch off, and that a request nobody classified is treated as the worst rather than the best.
/// </remarks>
[TestFixture]
public sealed class ActionSafetyTests {

  [Test]
  public void ReadingSomethingIsNeverConfirmed() {
    Assert.Multiple(() => {
      Assert.That(ActionSafety.MustAsk(ActionClass.ReadOnly, confirmsSingleActions: true), Is.False);
      Assert.That(ActionSafety.MustAsk(ActionClass.ReadOnly, confirmsSingleActions: false), Is.False);
      Assert.That(
        ActionSafety.MustAsk(ActionClass.ReadOnly, confirmsSingleActions: true, systemTarget: true),
        Is.False,
        "copying a row out of init is still copying a row"
      );
    });
  }

  /// <summary>
  /// Classes 1 and 2 are the two the <c>confirm.destructive</c> setting is about, and the only two.
  /// </summary>
  [TestCase(ActionClass.Reversible)]
  [TestCase(ActionClass.DataLoss)]
  public void TheSettingGovernsTheTwoMiddleClasses(ActionClass @class) {
    Assert.That(ActionSafety.MustAsk(@class, confirmsSingleActions: true), Is.True);
    Assert.That(ActionSafety.MustAsk(@class, confirmsSingleActions: false), Is.False);
  }

  /// <summary>
  /// §69's "default enabled for high-value and system targets". The setting is turned off by people
  /// who end their own programs all day; it is not turned off by people who meant to stop the
  /// machine's init.
  /// </summary>
  [Test]
  public void SomethingTheMachineDependsOnIsConfirmedWhateverTheSettingSays() {
    Assert.That(
      ActionSafety.MustAsk(ActionClass.DataLoss, confirmsSingleActions: false, systemTarget: true),
      Is.True
    );
  }

  /// <summary>
  /// Class 1 is deliberately <em>not</em> given that override. Suspending a daemon is undone by the
  /// item beside it, and a confirmation somebody switched off should stay switched off for the
  /// reversible half — otherwise the setting means nothing on a machine whose interesting rows are
  /// all root's.
  /// </summary>
  [Test]
  public void TheOverrideIsOnTheClassThatCanLoseSomethingAndNotTheOneThatCannot() {
    Assert.That(
      ActionSafety.MustAsk(ActionClass.Reversible, confirmsSingleActions: false, systemTarget: true),
      Is.False
    );
  }

  [Test]
  public void ExpertActionsAreNotSomethingAPreferenceCanSwitchOff() {
    Assert.That(ActionSafety.MustAsk(ActionClass.Unsafe, confirmsSingleActions: false), Is.True);
    Assert.That(ActionSafety.MustAsk(ActionClass.Unsafe, confirmsSingleActions: true), Is.True);
  }

  /// <summary>
  /// The defect this project keeps meeting, applied to a class rather than to a counter: a
  /// default-constructed value must never turn out to be the benign answer. An action nobody sorted
  /// is the one nobody thought about.
  /// </summary>
  [Test]
  public void AnActionNobodyClassifiedIsTreatedAsTheMostDangerousOne() {
    Assert.That(default(ActionClass), Is.EqualTo(ActionClass.Unclassified));
    Assert.That(ActionSafety.MustAsk(default, confirmsSingleActions: false), Is.True);
    Assert.That(ActionSafety.MustAsk((ActionClass)200, confirmsSingleActions: false), Is.True, "and so is a value outside the enum");
  }

  [Test]
  public void RootsProcessesAndTheLowestPidsAreSystemTargets() {
    Assert.Multiple(() => {
      Assert.That(ActionSafety.IsSystemTarget(Record(1, userId: 0)), Is.True, "init");
      Assert.That(ActionSafety.IsSystemTarget(Record(2, userId: 0)), Is.True, "kthreadd");
      Assert.That(ActionSafety.IsSystemTarget(Record(4, userId: 1000)), Is.True, "Windows' kernel pid, whatever the token says");
      Assert.That(ActionSafety.IsSystemTarget(Record(4321, userId: 0)), Is.True, "an ordinary daemon of root's");
      Assert.That(ActionSafety.IsSystemTarget(Record(4321, userId: 1000)), Is.False, "somebody's own editor");
    });
  }

  /// <summary>
  /// The warning names what the process is rather than passing a verdict on it — a reader can check
  /// "this belongs to root" against the user column, and cannot check "this is critical" against
  /// anything.
  /// </summary>
  [Test]
  public void TheWarningNamesWhatTheProcessIs() {
    Assert.That(ActionSafety.SystemTargetWarning(1), Does.Contain("init"));
    Assert.That(ActionSafety.SystemTargetWarning(3), Does.Contain("kernel"));
    Assert.That(ActionSafety.SystemTargetWarning(4321), Does.Contain("root"));
  }

  private static ProcessRecord Record(int pid, int userId) {
    var record = new ProcessRecord { Key = new(pid, 1), UserId = userId };
    return record;
  }

}

/// <summary>
/// The other half of §69's last line — the actions the operating system itself will not carry out.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these runs against the recorded <c>/proc</c> tree and every one of them asserts a
/// <em>refusal</em>, which is what makes them safe to run anywhere: the refusal happens before the
/// syscall, so nothing is ever sent to this machine's pid 1 or to its kernel threads. The positive
/// direction is deliberately not asserted here, because asserting it would mean signalling something
/// that belongs to the person running the suite.
/// </para>
/// <para>
/// The failure being prevented is the one <c>kill</c> with a nought already has a refusal for: the
/// call returns success, nothing whatever happens, and the person who asked is told the action
/// worked.
/// </para>
/// </remarks>
[TestFixture]
public sealed class UndeliverableSignalTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProcessActions Actions() => new(new() { ProcRoot = FixtureRoot });

  /// <summary>The recorded tree's pid 1 and pid 2, with the start times their stat lines record.</summary>
  private static readonly ProcessKey _Init = new(1, 5);

  private static readonly ProcessKey _KernelThreadDaemon = new(2, 43);

  [Test]
  public void SuspendingInitIsRefusedRatherThanReportedAsDone() {
    // SIGSTOP is one of the two no process may install a handler for, and the kernel delivers pid 1
    // nothing else. kill() would return zero and systemd would carry on running.
    var result = Actions().Suspend(_Init);

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(result.Detail, Does.Contain("pid 1"));
    Assert.That(result.Detail, Does.Contain("SIGSTOP"));
    Assert.That(result.Detail, Does.Contain("handler"));
  }

  [Test]
  public void KillingInitIsRefusedRatherThanReportedAsDone() {
    var result = Actions().SendSignal(_Init, 9);

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(result.Detail, Does.Contain("SIGKILL"));
  }

  /// <summary>
  /// A kernel thread never returns to user space, so there is no point at which it looks at a
  /// pending signal — even <c>SIGKILL</c> to one is a successful call that does nothing.
  /// </summary>
  [TestCase(9)]
  [TestCase(19)]
  public void SignallingAKernelThreadIsRefusedRatherThanReportedAsDone(int signal) {
    var result = Actions().SendSignal(_KernelThreadDaemon, signal);

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(result.Detail, Does.Contain("kernel thread"));
  }

  /// <summary>
  /// The identity check still comes first. A refusal that named the wrong reason would send somebody
  /// looking at the kernel's signal rules when what actually happened is that the pid was recycled
  /// (PRD §8.2).
  /// </summary>
  [Test]
  public void ARecycledPidIsStillTheReasonGivenRatherThanTheKernelsSignalRules() {
    var result = Actions().Suspend(new(1, 999_999));

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.IdentityMismatch));
  }

}
