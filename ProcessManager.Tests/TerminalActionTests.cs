using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The terminal's process actions: which keys reach them, and what the prompt says first (PRD §25).
/// </summary>
/// <remarks>
/// The actions object is a recorder, so nothing here signals anything. What is checked is the
/// wiring — that a key reaches the action it claims to, and that what a person is asked before a
/// destructive one names the target, its pid and what it costs (PRD §90).
/// </remarks>
[TestFixture]
public sealed class TerminalActionTests {

  private sealed class RecordingActions : IProcessActions {

    public List<string> Calls { get; } = [];

    public ActionResult Terminate(ProcessKey key) {
      this.Calls.Add($"terminate {key.Pid}");
      return ActionResult.Ok;
    }

    public ActionResult EndTask(ProcessKey key) {
      this.Calls.Add($"endtask {key.Pid}");
      return new(ActionOutcome.Succeeded, "its window was asked to close");
    }

    public ActionResult Suspend(ProcessKey key) => ActionResult.Ok;

    public ActionResult Resume(ProcessKey key) => ActionResult.Ok;

    public ActionResult SetPriority(ProcessKey key, int priority) => ActionResult.Ok;

    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Ok;

    public ActionResult SendSignal(ProcessKey key, int signal) => ActionResult.Ok;

    public ActionResult SetSchedulingClass(ProcessKey key, SchedulingPolicy policy, int priority) {
      this.Calls.Add($"class {key.Pid} {policy} {priority}");
      return ActionResult.Ok;
    }

    public LaunchResult Restart(ProcessKey key) {
      this.Calls.Add($"restart {key.Pid}");
      return new(ActionResult.Ok, key.Pid + 1, new(key.Pid + 1, 1));
    }

  }

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  /// <summary>A UI over the recorded machine with the top row selected.</summary>
  private static (TerminalUi Ui, RecordingActions Actions, LinuxProbe Probe) Machine() {
    var probe = new LinuxProbe(new() {
      ProcRoot = FixtureRoot,
      PasswdPath = Path.Combine(FixtureRoot, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    var actions = new RecordingActions();
    var ui = new TerminalUi(new Sampler(probe), probe, actions, 120, 40, ColorDepth.None) { ShowTiming = false };
    ui.Update();
    // Nothing is selected until the selection has been moved onto a row.
    ui.HandleKey(Key(ConsoleKey.Home));
    return (ui, actions, probe);
  }

  private static ConsoleKeyInfo Key(char character) => new(character, default, false, false, false);

  private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

  private static string Frame(TerminalUi ui) {
    ui.Refresh();
    return ui.Screen.Capture();
  }

  [Test]
  public void EndTaskAsksTheProgramAndIsNotConfirmed() {
    // The reversible one. It asks, the program may decline, and nothing is lost if it does — which
    // is why it does not stop to confirm and 'k' does (PRD §25.1).
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('e'));

      Assert.That(actions.Calls, Has.Count.EqualTo(1));
      Assert.That(actions.Calls[0], Does.StartWith("endtask "));
      Assert.That(Frame(ui), Does.Contain("its window was asked to close"), "what the action said, not a generic sentence");
    }
  }

  [Test]
  public void TerminateStopsToConfirmAndSaysWhatItCosts() {
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('k'));

      var prompt = Frame(ui);
      Assert.That(actions.Calls, Is.Empty, "nothing happens until it is answered");
      Assert.That(prompt, Does.Contain("Terminate"));
      Assert.That(prompt, Does.Contain("PID"), "the target is named, not merely numbered (PRD §90)");
      Assert.That(prompt, Does.Contain("not asked to save"));

      ui.HandleKey(Key('y'));
      Assert.That(actions.Calls, Has.Count.EqualTo(1));
      Assert.That(actions.Calls[0], Does.StartWith("terminate "));
    }
  }

  [Test]
  public void AnythingButYesCancels() {
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('k'));
      ui.HandleKey(Key('n'));

      Assert.That(actions.Calls, Is.Empty);
      Assert.That(Frame(ui), Does.Contain("cancelled"));
    }
  }

  /// <summary>
  /// The tree prompt counts the descendants, because that count is the whole difference between the
  /// two requests a single row can stand for (PRD §90).
  /// </summary>
  [Test]
  public void TheTreePromptSaysHowManyProcessesGoWithIt() {
    var (ui, _, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('K'));

      var prompt = Frame(ui);
      Assert.That(prompt, Does.Contain("processes under it"));
      Assert.That(prompt, Does.Contain("Unsaved work"));
    }
  }

  [Test]
  public void RestartConfirmsSeparatelyFromTerminating() {
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('R'));

      Assert.That(Frame(ui), Does.Contain("start it again"));
      ui.HandleKey(Key('y'));

      Assert.That(actions.Calls, Has.Count.EqualTo(1));
      Assert.That(actions.Calls[0], Does.StartWith("restart "));
      Assert.That(Frame(ui), Does.Contain("started again as"));
    }
  }

  [Test]
  public void TheSchedulerClassIsOneMoreKeystroke() {
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('s'));
      Assert.That(Frame(ui), Does.Contain("Scheduler class"));

      ui.HandleKey(Key('i'));
      Assert.That(actions.Calls, Has.Count.EqualTo(1));
      Assert.That(actions.Calls[0], Does.Contain("Idle 0"));
      Assert.That(Frame(ui), Does.Contain("SCHED_IDLE"), "named the way chrt names it");
    }
  }

  /// <summary>
  /// A real-time class picked with one keystroke gets the lowest static priority it has. Anything
  /// above that is a decision for a prompt rather than for a key in a list (PRD §68).
  /// </summary>
  [Test]
  public void ARealTimeClassPickedFromTheListTakesItsLowestPriority() {
    var (ui, actions, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('s'));
      ui.HandleKey(Key('f'));

      Assert.That(actions.Calls[0], Does.Contain("Fifo 1"));
    }
  }

  [Test]
  public void EveryNewKeyIsNamedOnTheHelpLine() {
    // The line is hand-maintained, so it drifts unless something checks it. A binding nobody can
    // discover is a binding that does not exist (PRD §25.2).
    var (ui, _, probe) = Machine();
    using (probe) {
      var frame = Frame(ui);
      Assert.Multiple(() => {
        Assert.That(frame, Does.Contain("e end task"));
        Assert.That(frame, Does.Contain("k kill"));
        Assert.That(frame, Does.Contain("K kill tree"));
        Assert.That(frame, Does.Contain("R restart"));
        Assert.That(frame, Does.Contain("s class"));
      });
    }
  }

}
