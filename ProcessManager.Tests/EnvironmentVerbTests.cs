using Hawkynt.ProcessManager.App;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Asking from the command line for the block a process was started with (PRD §37, §102).
/// </summary>
/// <remarks>
/// The window had a page for this and neither the terminal nor the command line had anything, which
/// is the one thing §102's audit found — a program that shows something in one front-end and nowhere
/// else has made that thing unavailable to anybody working over ssh or from a script.
/// </remarks>
[TestFixture]
public sealed class EnvironmentVerbTests {

  private static CommandLineOptions Parse(params string[] arguments)
    => CommandLineOptions.Parse(arguments, null);

  [TestCase("--environment")]
  [TestCase("--env")]
  public void TheVerbTakesAPid(string spelling) {
    var options = Parse(spelling, "1234");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.TargetPid, Is.EqualTo(1234));
  }

  /// <summary>
  /// Without one it says what is missing rather than reading whichever process the sampler happened
  /// to put first — which is the failure mode a defaulted pid has.
  /// </summary>
  [Test]
  public void WithoutAPidItSaysWhatIsMissing()
    => Assert.That(Parse("--environment").Error, Does.Contain("pid"));

  [Test]
  public void SomethingThatIsNotAPidIsNotOne()
    => Assert.That(Parse("--environment", "the-first-one").Error, Does.Contain("pid"));

  /// <summary>
  /// It is a mode of its own, so nothing else this run was going to do happens as well. A verb that
  /// left the mode alone would print the block and then bring the window up.
  /// </summary>
  [Test]
  public void ItIsAModeRatherThanAnExtra() {
    var options = Parse("--environment", "1");

    Assert.That(options.Mode.ToString(), Is.EqualTo("Environment"));
  }

  /// <summary>And the format flag reaches it, so a script can ask for JSON.</summary>
  [Test]
  public void AScriptCanAskForJson() {
    var options = Parse("--environment", "1", "--format", "json");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.Format.ToString(), Is.EqualTo("Json"));
  }

  /// <summary>
  /// It is in the help. A verb reachable only by knowing it exists is one this program does not
  /// really have (PRD §91).
  /// </summary>
  [Test]
  public void ItIsInTheHelp()
    => Assert.That(CommandLineOptions.HelpText, Does.Contain("--environment"));

}
