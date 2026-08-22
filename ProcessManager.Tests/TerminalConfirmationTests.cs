using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.App;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That the terminal and the window read the same preference the same way (PRD §58, §67, §69).
/// </summary>
/// <remarks>
/// The window has read <c>confirm.destructive</c> since the setting existed. The terminal never did:
/// it asked about a terminate whatever the file said. Stricter, so it was never unsafe, but it meant
/// one preference produced two different programs — which is exactly the front-end disagreement §58
/// exists to stop, and the sort of gap that is invisible until somebody sets the preference and finds
/// only half of it took.
/// </remarks>
[TestFixture]
public sealed class TerminalConfirmationTests {

  /// <summary>The preference reaches the options, which is how every other one reaches a front-end.</summary>
  [Test]
  public void ThePreferenceReachesTheOptions() {
    var asks = CommandLineOptions.Parse([], new() { ConfirmDestructiveActions = true });
    var doesNot = CommandLineOptions.Parse([], new() { ConfirmDestructiveActions = false });

    Assert.That(asks.ConfirmSingleActions, Is.True);
    Assert.That(doesNot.ConfirmSingleActions, Is.False);
  }

  /// <summary>
  /// A settings file that says nothing means "ask", which is the safe half of the question.
  /// </summary>
  [Test]
  public void SayingNothingMeansAsk()
    => Assert.That(CommandLineOptions.Parse([], null).ConfirmSingleActions, Is.True);

  /// <summary>
  /// Ending a process is class 2: the preference decides, and it decides the same way in both
  /// front-ends because both ask the same table.
  /// </summary>
  [Test]
  public void EndingAProcessIsTheSettingsToDecide() {
    Assert.That(ActionSafety.MustAsk(ActionClass.DataLoss, confirmsSingleActions: true), Is.True);
    Assert.That(ActionSafety.MustAsk(ActionClass.DataLoss, confirmsSingleActions: false), Is.False);
  }

  /// <summary>
  /// …unless it is aimed at something the machine depends on, which is asked about whatever anybody
  /// set. The preference turns off the prompt for a person's own programs, not for the machine's.
  /// </summary>
  [Test]
  public void SomethingTheMachineDependsOnIsAskedAboutAnyway()
    => Assert.That(
      ActionSafety.MustAsk(ActionClass.DataLoss, confirmsSingleActions: false, systemTarget: true),
      Is.True
    );

  /// <summary>
  /// And nothing turns off the prompt for an unsafe action, or for one nobody classified — the
  /// default class is the most dangerous one there is, because the thing nobody filled in is the
  /// thing nobody thought about.
  /// </summary>
  [Test]
  public void NothingTurnsOffThePromptForTheDangerousOnes() {
    Assert.That(ActionSafety.MustAsk(ActionClass.Unsafe, confirmsSingleActions: false), Is.True);
    Assert.That(ActionSafety.MustAsk(ActionClass.Unclassified, confirmsSingleActions: false), Is.True);
    Assert.That(ActionSafety.MustAsk((ActionClass)200, confirmsSingleActions: false), Is.True);
  }

  /// <summary>
  /// Reading something is never asked about, whatever anybody set. A prompt on a copy teaches
  /// people to dismiss prompts, which is what makes the one that matters ineffective.
  /// </summary>
  [Test]
  public void ReadingIsNeverAskedAbout() {
    Assert.That(ActionSafety.MustAsk(ActionClass.ReadOnly, confirmsSingleActions: true), Is.False);
    Assert.That(ActionSafety.MustAsk(ActionClass.ReadOnly, confirmsSingleActions: true, systemTarget: true), Is.False);
  }

}
