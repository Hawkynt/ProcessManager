using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That a unit can be commanded from somewhere a person would look (PRD §41, §91).
/// </summary>
/// <remarks>
/// Every one of these commands worked from <c>--service</c> long before either front-end offered
/// them, and §91 counts that as not having the capability: somebody looking at a list of units and
/// wanting to stop one does not go and read the help. What is asserted here is the seam that makes
/// the offer possible — that the control is an interface neither front-end has to name a platform to
/// reach, and that its verbs are the ones the command line already parses.
///
/// Nothing here starts or stops a service. A test suite may not do that to the machine it is running
/// on, whatever the machine happens to be.
/// </remarks>
[TestFixture]
public sealed class ServiceCommandReachTests {

  /// <summary>Answers however the test wants, and remembers what it was asked.</summary>
  private sealed class Recorder {

    public List<string> Arguments { get; } = [];
    public int Code { get; set; }

    public (int, string, string) Run(string program, IReadOnlyList<string> arguments) {
      this.Arguments.Clear();
      this.Arguments.AddRange(arguments);
      return (this.Code, string.Empty, string.Empty);
    }

  }

  /// <summary>
  /// The systemd control is reachable as the interface, which is the whole point of the interface:
  /// the window and the terminal offer these commands without either of them mentioning systemd.
  /// </summary>
  [Test]
  public void TheControlIsReachableWithoutNamingAPlatform() {
    IServiceControl control = new SystemdServiceControl();

    Assert.That(control, Is.Not.Null);
    Assert.That(control.IsAvailable, Is.EqualTo(SystemdServiceControl.IsPresent));
  }

  /// <summary>
  /// Every command has a verb, and it is the verb <c>--service</c> parses. One table, so a menu item
  /// and a command-line word cannot come to mean different things.
  /// </summary>
  [TestCase(ServiceCommand.Start, "start")]
  [TestCase(ServiceCommand.Stop, "stop")]
  [TestCase(ServiceCommand.Restart, "restart")]
  [TestCase(ServiceCommand.Reload, "reload")]
  [TestCase(ServiceCommand.Enable, "enable")]
  [TestCase(ServiceCommand.Disable, "disable")]
  public void EveryCommandHasTheVerbTheCommandLineParses(ServiceCommand command, string verb)
    => Assert.That(IServiceControl.Verb(command), Is.EqualTo(verb));

  /// <summary>
  /// And the verb the interface gives is the one that reaches systemctl, so a menu item that says
  /// "reload its configuration" does not quietly restart the machine's name daemon.
  /// </summary>
  [TestCase(ServiceCommand.Stop)]
  [TestCase(ServiceCommand.Reload)]
  [TestCase(ServiceCommand.Disable)]
  public void TheVerbShownIsTheVerbSent(ServiceCommand command) {
    if (!SystemdServiceControl.IsPresent)
      Assert.Ignore("no systemd here, so the control refuses before it builds anything");

    var recorder = new Recorder();
    IServiceControl control = new SystemdServiceControl(recorder.Run);
    control.Apply(command, "a.service");

    Assert.That(recorder.Arguments, Does.Contain(IServiceControl.Verb(command)));
  }

  /// <summary>
  /// A machine with nothing to ask says so through <see cref="IServiceControl.IsAvailable"/> rather
  /// than by refusing every command in turn. A front-end reading it hides the commands; a menu of
  /// six items that all answer "not on this platform" is worse than no menu.
  /// </summary>
  [Test]
  public void WhetherThereIsAnythingToAskIsAskableBeforeAsking() {
    IServiceControl control = new SystemdServiceControl();

    if (control.IsAvailable)
      Assert.Pass("this machine has a manager, so the absent case cannot be reached from here");

    Assert.That(
      control.Apply(ServiceCommand.Start, "a.service").Outcome,
      Is.EqualTo(ActionOutcome.NotSupportedOnPlatform)
    );
  }

  /// <summary>
  /// A name that is not a unit name is still refused before anything runs, whichever front-end it
  /// came from — the menu passes what the row says, and a row says whatever the disk said.
  /// </summary>
  [TestCase("../../etc/passwd")]
  [TestCase("")]
  public void AnUnusableNameIsStillRefusedThroughTheInterface(string unit) {
    var recorder = new Recorder();
    IServiceControl control = new SystemdServiceControl(recorder.Run);

    Assert.That(control.Apply(ServiceCommand.Start, unit).Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(recorder.Arguments, Is.Empty, "nothing was run");
  }

}
