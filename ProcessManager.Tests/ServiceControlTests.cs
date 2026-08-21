using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Starting and stopping systemd units (PRD §41).
/// </summary>
/// <remarks>
/// Every one of these runs against an injected runner rather than the machine's own manager. A test
/// suite may not start or stop services on the computer it happens to be running on — the whole
/// point of the feature is that it changes the machine, and a green test is not worth somebody's
/// network dropping. What is asserted is the invocation that would have been made and what is done
/// with each answer, which is where the mistakes live anyway.
/// </remarks>
[TestFixture]
public sealed class ServiceControlTests {

  /// <summary>Remembers what it was asked to run, and answers however the test wants.</summary>
  private sealed class Recorder {

    public List<string> Arguments { get; } = [];
    public string Program { get; private set; } = string.Empty;
    public int Code { get; set; }
    public string Error { get; set; } = string.Empty;

    public (int, string, string) Run(string program, IReadOnlyList<string> arguments) {
      this.Program = program;
      this.Arguments.Clear();
      this.Arguments.AddRange(arguments);
      return (this.Code, string.Empty, this.Error);
    }

  }

  private static bool Systemd => SystemdServiceControl.IsPresent;

  [Test]
  public void TheUnitNameIsAnArgumentRatherThanPartOfACommandLine() {
    if (!Systemd)
      Assert.Ignore("no systemd here, so the control refuses before it builds anything");

    var recorder = new Recorder();
    new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Start, "nginx.service");

    Assert.That(recorder.Program, Is.EqualTo("systemctl"));
    Assert.That(recorder.Arguments, Is.EqualTo(new[] { "--no-ask-password", "start", "--", "nginx.service" }));
  }

  /// <summary>
  /// A unit whose name begins with a dash is a unit, not a switch. The separator is what makes that
  /// true, and it is the difference between naming a service and passing systemctl an option.
  /// </summary>
  [Test]
  public void ANameThatLooksLikeASwitchIsStillAName() {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder();
    new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Stop, "--version");

    Assert.That(recorder.Arguments, Is.EqualTo(new[] { "--no-ask-password", "stop", "--", "--version" }));
  }

  [Test]
  public void TheUsersOwnManagerIsADifferentQuestion() {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder();
    new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Restart, "app.service", userScope: true);

    Assert.That(recorder.Arguments, Is.EqualTo(new[] { "--user", "--no-ask-password", "restart", "--", "app.service" }));
  }

  [TestCase(ServiceCommand.Start, "start")]
  [TestCase(ServiceCommand.Stop, "stop")]
  [TestCase(ServiceCommand.Restart, "restart")]
  [TestCase(ServiceCommand.Reload, "reload")]
  [TestCase(ServiceCommand.Enable, "enable")]
  [TestCase(ServiceCommand.Disable, "disable")]
  public void EveryCommandHasItsOwnVerb(ServiceCommand command, string verb) {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder();
    new SystemdServiceControl(recorder.Run).Apply(command, "a.service");

    Assert.That(recorder.Arguments[recorder.Arguments.IndexOf("--no-ask-password") + 1], Is.EqualTo(verb));
  }

  /// <summary>
  /// A name with a path separator in it is refused before anything runs. <c>../../etc/passwd</c> is
  /// not a unit, and it must not reach a process that might treat it as a path.
  /// </summary>
  [TestCase("")]
  [TestCase("../../etc/passwd")]
  [TestCase("a/b.service")]
  [TestCase("bad\nname.service")]
  public void ANameThatIsNotAUnitNameIsRefusedBeforeAnythingRuns(string unit) {
    var recorder = new Recorder();
    var result = new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Start, unit);

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(recorder.Program, Is.Empty, "nothing was run");
  }

  [Test]
  public void ASuccessfulCommandSaysSo() {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder { Code = 0 };

    Assert.That(new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Start, "a.service").Succeeded, Is.True);
  }

  /// <summary>
  /// polkit refuses in more than one way — an interactive denial, and a session with no way to ask.
  /// Both mean the same thing to a caller and neither is an ordinary failure.
  /// </summary>
  [TestCase("Failed to start a.service: Access denied")]
  [TestCase("Interactive authentication required.")]
  [TestCase("The operation is not authorized")]
  public void ARefusalByPolicyIsNotAFailure(string error) {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder { Code = 1, Error = error };
    var result = new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Start, "a.service");

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.NotPermitted));
    Assert.That(result.Detail, Does.Contain(error.Trim()));
  }

  /// <summary>Anything else keeps the manager's own words, which say more than we could.</summary>
  [Test]
  public void AnyOtherFailureCarriesWhatTheManagerSaid() {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder { Code = 5, Error = "Unit nosuch.service not found." };
    var result = new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Start, "nosuch.service");

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Failed));
    Assert.That(result.Detail, Does.Contain("not found"));
  }

  /// <summary>
  /// A silent non-zero exit still has to say something. "It did not work" with no reason is the
  /// answer a person can do least with.
  /// </summary>
  [Test]
  public void AFailureWithNothingToSayStillNamesWhatWasTried() {
    if (!Systemd)
      Assert.Ignore("no systemd here");

    var recorder = new Recorder { Code = 3 };
    var result = new SystemdServiceControl(recorder.Run).Apply(ServiceCommand.Stop, "quiet.service");

    Assert.That(result.Detail, Does.Contain("stop").And.Contain("quiet.service"));
  }

  /// <summary>
  /// A machine with no systemd is not a machine where this failed — there is nothing to ask.
  /// </summary>
  [Test]
  public void WithoutAManagerThereIsNothingToAsk() {
    if (Systemd)
      Assert.Ignore("this machine has systemd, so the absent case cannot be reached from here");

    var result = new SystemdServiceControl().Apply(ServiceCommand.Start, "a.service");

    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.NotSupportedOnPlatform));
  }

}
