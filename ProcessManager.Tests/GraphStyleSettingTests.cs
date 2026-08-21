using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Remembering how the terminal draws a history column (PRD §57.4, §67).
/// </summary>
/// <remarks>
/// The style used to live only in the terminal assembly, which meant it could be chosen per run and
/// never kept: somebody who preferred braille said so every time they started the program. The enum
/// is in Core now for no other reason than that the settings record is.
/// </remarks>
[TestFixture]
public sealed class GraphStyleSettingTests {

  private static UserSettings Read(string line) => UserSettings.Parse(line + "\n");

  [TestCase("blocks", GraphStyle.Blocks)]
  [TestCase("braille", GraphStyle.Braille)]
  [TestCase("ascii", GraphStyle.Ascii)]
  [TestCase("numbers", GraphStyle.Numbers)]
  [TestCase("BRAILLE", GraphStyle.Braille)]
  public void EveryStyleCanBeAskedForByName(string written, GraphStyle expected)
    => Assert.That(Read($"tui.graphs={written}").TerminalGraphs, Is.EqualTo(expected));

  /// <summary>
  /// Saying nothing and saying "auto" are the same thing, and neither is a fifth style. The terminal
  /// then reads its own locale, which is what it did before any of this existed.
  /// </summary>
  [Test]
  public void SayingNothingAndSayingAutoAreTheSame() {
    Assert.That(Read("tui.graphs=auto").TerminalGraphs, Is.Null);
    Assert.That(UserSettings.Parse(string.Empty).TerminalGraphs, Is.Null);
  }

  /// <summary>
  /// A word this build does not know leaves the setting alone rather than failing the file — the
  /// rule §67 gives for every line in it.
  /// </summary>
  [Test]
  public void AWordThisBuildDoesNotKnowChangesNothing()
    => Assert.That(Read("tui.graphs=hieroglyphs").TerminalGraphs, Is.Null);

  /// <summary>
  /// It survives being written out and read back. A setting the box can change and the file cannot
  /// keep is a setting that does not exist.
  /// </summary>
  [TestCase(GraphStyle.Blocks)]
  [TestCase(GraphStyle.Braille)]
  [TestCase(GraphStyle.Ascii)]
  [TestCase(GraphStyle.Numbers)]
  public void ItSurvivesBeingWrittenOutAndReadBack(GraphStyle style) {
    var written = (new UserSettings { TerminalGraphs = style }).Write();

    Assert.That(UserSettings.Parse(written).TerminalGraphs, Is.EqualTo(style));
  }

  /// <summary>
  /// And a file that states nothing writes nothing, so the absence stays an absence rather than
  /// becoming an explicit "blocks" the next time the file is saved.
  /// </summary>
  [Test]
  public void StatingNothingWritesNothing() {
    var written = (new UserSettings()).Write();

    Assert.That(written, Does.Not.Contain("tui.graphs"));
    Assert.That(UserSettings.Parse(written).TerminalGraphs, Is.Null);
  }

  /// <summary>
  /// The older <c>blocks=false</c> still means ASCII. An older file must keep working, which is the
  /// same promise §67 makes in the other direction about keys a build does not understand.
  /// </summary>
  [Test]
  public void TheOlderSpellingStillMeansWhatItMeant() {
    var settings = Read("blocks=false");

    Assert.That(settings.BlockCharacters, Is.False);
    Assert.That(settings.TerminalGraphs, Is.Null, "it never stated a style, only which two");
  }

}
