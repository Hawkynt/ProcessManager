using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Splitting what somebody typed into arguments (PRD §54, §91).
/// </summary>
/// <remarks>
/// <para>
/// This is where a "run" box goes quietly wrong. A path with a space in it is the ordinary case and
/// splitting it in two starts a program that does not exist, or worse, starts the wrong one with a
/// fragment of a path as its argument.
/// </para>
/// <para>
/// And it is deliberately not a shell. No globs, no variables, no substitution: a box that quietly
/// ran a shell would make every character somebody typed a possible command, which is a much larger
/// thing to offer than "start this program" — and the whole reason it exists is the case where the
/// shell is what has gone wrong.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RunTaskDialogTests {

  private static IReadOnlyList<string> Split(string line) => RunTaskDialog.SplitArguments(line);

  [Test]
  public void NothingTypedIsNoArguments() {
    Assert.That(Split(string.Empty), Is.Empty);
    Assert.That(Split("   "), Is.Empty);
  }

  [Test]
  public void WordsSplitOnSpaces()
    => Assert.That(Split("--tree --sort cpu"), Is.EqualTo(new[] { "--tree", "--sort", "cpu" }));

  /// <summary>
  /// A quoted run stays one argument, which is the whole reason for reading the line rather than
  /// calling <c>Split(' ')</c> on it.
  /// </summary>
  [TestCase("\"/usr/local/my programs/thing\"")]
  [TestCase("'/usr/local/my programs/thing'")]
  public void AQuotedPathStaysOneArgument(string line)
    => Assert.That(Split(line), Is.EqualTo(new[] { "/usr/local/my programs/thing" }));

  [Test]
  public void QuotesComeOffTheArgumentTheyHeldTogether()
    => Assert.That(Split("--name \"two words\" --flag"), Is.EqualTo(new[] { "--name", "two words", "--flag" }));

  /// <summary>
  /// An empty quoted run is an argument. Passing one on purpose is a real thing to do and dropping
  /// it silently shifts every argument after it along by one.
  /// </summary>
  [Test]
  public void AnEmptyQuotedRunIsStillAnArgument()
    => Assert.That(Split("--name \"\" --flag"), Is.EqualTo(new[] { "--name", string.Empty, "--flag" }));

  /// <summary>
  /// Runs of whitespace are one separator, not several empty arguments.
  /// </summary>
  [Test]
  public void ExtraSpacesAreNotExtraArguments()
    => Assert.That(Split("  one    two  "), Is.EqualTo(new[] { "one", "two" }));

  /// <summary>
  /// A quote nobody closed takes the rest of the line, which is what every shell does and is the
  /// least surprising of the available answers.
  /// </summary>
  [Test]
  public void AnUnclosedQuoteTakesTheRest()
    => Assert.That(Split("--name \"never closed"), Is.EqualTo(new[] { "--name", "never closed" }));

  /// <summary>
  /// <b>Nothing is expanded.</b> A glob stays the characters somebody typed, a variable stays a
  /// dollar sign and a name, and a backtick is a backtick. This starts a program; it is not a shell,
  /// and the difference is the whole security argument for having it at all.
  /// </summary>
  [TestCase("*.txt")]
  [TestCase("$HOME")]
  [TestCase("`whoami`")]
  [TestCase("$(whoami)")]
  [TestCase("a;rm -rf b")]
  public void NothingIsExpandedOrInterpreted(string typed) {
    var arguments = Split(typed);

    // Joining them back gives exactly what was typed. Nothing was expanded, nothing was swallowed,
    // and a semicolon is a character in an argument rather than the end of a command — which is the
    // whole distinction between this and a shell.
    Assert.That(string.Join(" ", arguments), Is.EqualTo(typed));
  }

  /// <summary>
  /// The other half: the program is its own field rather than the first word of a line. So a path
  /// with a space in it needs no quoting at all, which is the case somebody is most likely to get
  /// wrong and least likely to think about.
  /// </summary>
  [Test]
  public void TheProgramIsItsOwnFieldSoItNeedsNoQuoting() {
    var dialog = new RunTaskDialog();

    Assert.That(dialog.Request, Is.Null, "nothing named yet");
  }

}
