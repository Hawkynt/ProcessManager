using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The terminal's colours, as a settings file names them (PRD §57.4, §67).
/// </summary>
/// <remarks>
/// Everything else about the terminal had been settable for a while — the key bindings, the mouse,
/// the style its history columns are drawn in — and the colours were the one thing that could only
/// be changed by editing the source. The window's palette had been a <c>color.</c> line since §7.1
/// was written, so the two front-ends disagreed about whether a colour was a preference at all.
/// <para>
/// The palette is installed once into static state, the way the window's is, so every test here puts
/// it back afterwards. A fixture that left an override behind would repaint the built-in palette for
/// whatever ran next, and the assertion that the ten appearances are all distinct is exactly the one
/// that would then fail somewhere else.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class TerminalPaletteTests {

  [TearDown]
  public void ForgetThePalette() => Attributes.Apply(null);

  private static UserSettings Read(string text) => UserSettings.Parse(text);

  #region what the file may say

  /// <summary>
  /// The two lists are the same ten words. The renderer's is what a slot is called on screen and the
  /// file's is what a person may write, and nothing holds them together but this.
  /// </summary>
  [Test]
  public void TheRendererAndTheFileNameTheSameAppearances()
    => Assert.That(Attributes.SlotNames, Is.EqualTo(UserSettings.TerminalColourNames));

  /// <summary>Every name in the list is one the file will actually take.</summary>
  [Test]
  public void EveryNameTheFileAdvertisesIsOneItAccepts() {
    foreach (var name in UserSettings.TerminalColourNames) {
      Assert.That(Read($"tui.color.{name}=#123456").TerminalColours.ContainsKey(name), Is.True, name);
      Assert.That(Read($"tui.color.{name}.bg=#123456").TerminalColours.ContainsKey(name + ".bg"), Is.True, name);
    }
  }

  /// <summary>
  /// A name this build does not know is kept verbatim rather than dropped — the rule the whole file
  /// is written to, and the one that stops an older build eating an eleventh appearance.
  /// </summary>
  [Test]
  public void AnAppearanceThisBuildHasNeverHeardOfSurvivesBeingRead() {
    var settings = Read("tui.color.iridescent=#ff00ff");

    Assert.That(settings.TerminalColours, Is.Empty);
    Assert.That(settings.Unknown, Does.Contain("tui.color.iridescent=#ff00ff"));
    Assert.That(settings.Write(), Does.Contain("tui.color.iridescent=#ff00ff"));
  }

  /// <summary>A colour that will not parse leaves the appearance alone, like every other bad line.</summary>
  [Test]
  public void AColourThatIsNotAColourLeavesTheBuiltInWhereItWas() {
    var settings = Read("tui.color.good=lime green");

    Assert.That(settings.TerminalColours, Is.Empty);
    Assert.That(settings.Unknown, Does.Contain("tui.color.good=lime green"));
  }

  /// <summary>
  /// It comes back out as it went in. A file that lost a colour on every save would lose the palette
  /// the first time somebody moved the window.
  /// </summary>
  [Test]
  public void ThePaletteSurvivesBeingWrittenAndReadAgain() {
    var written = Read("tui.color.warn=#ffaa00\ntui.color.header=#101010\ntui.color.header.bg=#00ddcc").Write();
    var again = Read(written).TerminalColours;

    Assert.Multiple(() => {
      Assert.That(again["warn"], Is.EqualTo(0xFFFFAA00));
      Assert.That(again["header"], Is.EqualTo(0xFF101010));
      Assert.That(again["header.bg"], Is.EqualTo(0xFF00DDCC));
    });
  }

  /// <summary>A file that names none of them says nothing about them at all.</summary>
  [Test]
  public void AFileWithNoOpinionCarriesNoColourLines()
    => Assert.That(new UserSettings().Write(), Does.Not.Contain("tui.color."));

  /// <summary>
  /// The window's palette and the terminal's are two maps, and a build that folded them together
  /// would give every <c>color.</c> line an effect on a terminal it was never written for.
  /// </summary>
  [Test]
  public void TheWindowsPaletteAndTheTerminalsDoNotLeakIntoEachOther() {
    var settings = Read("color.zombie=#ff0000\ntui.color.bad=#00ff00");

    Assert.That(settings.Colours.Keys, Is.EquivalentTo(new[] { "zombie" }));
    Assert.That(settings.TerminalColours.Keys, Is.EquivalentTo(new[] { "bad" }));
  }

  #endregion

  #region what the terminal then paints

  /// <summary>The colour asked for arrives verbatim where the terminal can show it.</summary>
  [Test]
  public void AStatedColourIsPaintedExactlyAtTwentyFourBits() {
    Attributes.Apply(Read("tui.color.accent=#2a5fd0").TerminalColours);

    Assert.That(Attributes.ToAnsi(Attributes.Accent, ColorDepth.TrueColor), Does.Contain("38;2;42;95;208"));
  }

  /// <summary>
  /// And the nearest it has where it cannot. A terminal given a 24-bit escape it does not understand
  /// prints the escape, which is worse than an approximate colour by every measure there is.
  /// </summary>
  [Test]
  public void ALowerDepthGetsTheNearestColourItCanActuallyShow() {
    Attributes.Apply(Read("tui.color.accent=#ff0000").TerminalColours);

    Assert.Multiple(() => {
      var ansi256 = Attributes.ToAnsi(Attributes.Accent, ColorDepth.Ansi256);
      Assert.That(ansi256, Does.Contain("38;5;196"), "the cube's own red");
      Assert.That(ansi256, Does.Not.Contain("38;2;"), "and not a 24-bit escape in a 256-colour frame");

      var ansi16 = Attributes.ToAnsi(Attributes.Accent, ColorDepth.Ansi16);
      Assert.That(ansi16, Does.Contain("91"), "bright red, of the sixteen");
      Assert.That(ansi16, Does.Not.Contain("38;"), "and not an extended escape in a sixteen-colour frame");
    });
  }

  /// <summary>
  /// A grey lands on the ramp rather than on the cube's six-step diagonal, which is the difference
  /// between the colour asked for and one visibly beside it.
  /// </summary>
  [Test]
  public void AGreyIsQuantisedOntoTheRampRatherThanIntoTheCube() {
    Attributes.Apply(Read("tui.color.dim=#8a8a8a").TerminalColours);

    Assert.That(Attributes.ToAnsi(Attributes.Dim, ColorDepth.Ansi256), Does.Contain("38;5;245"));
  }

  /// <summary>
  /// A ground is a ground. Naming only the ink of an appearance that paints a bar takes the bar
  /// away, which is the answer to what was asked rather than half of it.
  /// </summary>
  [Test]
  public void AGroundIsPaintedWhenItIsNamedAndNotWhenItIsNot() {
    Attributes.Apply(Read("tui.color.header=#101010\ntui.color.header.bg=#00ddcc").TerminalColours);
    Assert.That(Attributes.ToAnsi(Attributes.Header, ColorDepth.TrueColor), Does.Contain("48;2;0;221;204"));

    Attributes.Apply(Read("tui.color.header=#101010").TerminalColours);
    Assert.That(Attributes.ToAnsi(Attributes.Header, ColorDepth.TrueColor), Does.Not.Contain("48;"));
  }

  /// <summary>A ground on its own is a ground, and the terminal's own ink is left on it.</summary>
  [Test]
  public void AGroundMayBeNamedWithoutAnInk() {
    Attributes.Apply(Read("tui.color.selected.bg=#334455").TerminalColours);

    var escape = Attributes.ToAnsi(Attributes.Selected, ColorDepth.TrueColor);
    Assert.That(escape, Does.Contain("48;2;51;68;85"));
    Assert.That(escape, Does.Not.Contain("38;"));
  }

  /// <summary>
  /// An appearance nobody named keeps the built-in one. The whole point of a sparse map is that
  /// naming one colour does not make somebody responsible for the other nine.
  /// </summary>
  [Test]
  public void TheOtherNineAreLeftExactlyAsTheyWere() {
    var before = Attributes.ToAnsi(Attributes.Bad, ColorDepth.TrueColor);
    Attributes.Apply(Read("tui.color.good=#00ff00").TerminalColours);

    Assert.That(Attributes.ToAnsi(Attributes.Bad, ColorDepth.TrueColor), Is.EqualTo(before));
  }

  /// <summary>
  /// A terminal with no colour at all is left alone. Everything it draws carries its meaning in a
  /// glyph as well, and there is no escape to put a colour in (PRD §57.4, §74).
  /// </summary>
  [Test]
  public void AMonochromeTerminalIsUnmovedByAnyOfIt() {
    var before = Attributes.ToAnsi(Attributes.Good, ColorDepth.None);
    Attributes.Apply(Read("tui.color.good=#00ff00\ntui.color.good.bg=#000000").TerminalColours);

    Assert.That(Attributes.ToAnsi(Attributes.Good, ColorDepth.None), Is.EqualTo(before));
  }

  /// <summary>
  /// And it can be put back. The window's palette works this way too, and a front-end started twice
  /// in one process must not inherit the first run's file.
  /// </summary>
  [Test]
  public void ThePaletteCanBeTakenAwayAgain() {
    var built = Attributes.ToAnsi(Attributes.Warn, ColorDepth.Ansi256);
    Attributes.Apply(Read("tui.color.warn=#ff00ff").TerminalColours);
    Assert.That(Attributes.ToAnsi(Attributes.Warn, ColorDepth.Ansi256), Is.Not.EqualTo(built));

    Attributes.Apply(null);
    Assert.That(Attributes.ToAnsi(Attributes.Warn, ColorDepth.Ansi256), Is.EqualTo(built));
  }

  /// <summary>
  /// The whole frame follows, not merely the lookup. A palette that never reached the writer would
  /// pass every assertion above.
  /// </summary>
  [Test]
  public void TheColourReachesTheBytesTheTerminalIsSent() {
    Attributes.Apply(Read("tui.color.accent=#2a5fd0").TerminalColours);

    var screen = new TerminalScreen(10, 1, ColorDepth.TrueColor);
    screen.BeginFrame();
    screen.Write(0, 0, "hi", Attributes.Accent);
    var writer = new StringWriter();
    screen.Flush(writer);

    Assert.That(writer.ToString(), Does.Contain("38;2;42;95;208"));
  }

  #endregion

}
