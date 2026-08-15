using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class TerminalScreenTests {

  [Test]
  public void TextIsClippedAtTheRightEdgeRatherThanWrapping() {
    var screen = new TerminalScreen(10, 2, ColorDepth.None);
    screen.BeginFrame();
    screen.Write(6, 0, "abcdefgh");

    var frame = screen.Capture().Split('\n');
    Assert.That(frame[0], Is.EqualTo("      abcd"));
    Assert.That(frame[1], Is.Empty, "nothing wrapped onto the next line");
  }

  [Test]
  public void ARightAlignedValueLosesItsHeadRatherThanItsTail() {
    // The significant digits of a number are at the end, so a value too wide for its column keeps
    // them. Headers do the opposite, which is the caller's job.
    var screen = new TerminalScreen(20, 1, ColorDepth.None);
    screen.BeginFrame();
    screen.WriteRight(0, 0, 4, "123456");

    Assert.That(screen.Capture().TrimEnd(), Is.EqualTo("3456"));
  }

  [Test]
  public void WritingOffScreenIsIgnoredRatherThanThrowing() {
    var screen = new TerminalScreen(8, 2, ColorDepth.None);
    screen.BeginFrame();

    Assert.That(() => {
      screen.Write(0, 99, "below");
      screen.Write(-5, 0, "left");
      screen.Fill(4, 0, 100, '*');
    }, Throws.Nothing);
  }

  [Test]
  public void OnlyTheChangedCellsAreWritten() {
    // The whole reason for the double buffer: a frame identical to the last one costs nothing, and a
    // frame differing in one cell costs one cursor move and one character.
    var screen = new TerminalScreen(20, 2, ColorDepth.None);
    screen.BeginFrame();
    screen.Write(0, 0, "hello");

    var first = new StringWriter();
    screen.Flush(first);
    Assert.That(first.ToString(), Does.Contain("hello"));

    screen.BeginFrame();
    screen.Write(0, 0, "hello");
    var second = new StringWriter();
    screen.Flush(second);
    Assert.That(second.ToString(), Is.Empty, "an identical frame writes nothing at all");

    screen.BeginFrame();
    screen.Write(0, 0, "hellp");
    var third = new StringWriter();
    screen.Flush(third);
    Assert.That(third.ToString(), Does.Contain("p"));
    Assert.That(third.ToString(), Does.Not.Contain("hell"), "only the differing cell is rewritten");
  }

  [Test]
  public void AResizeForcesAFullRepaint() {
    var screen = new TerminalScreen(10, 2, ColorDepth.None);
    screen.BeginFrame();
    screen.Write(0, 0, "x");
    screen.Flush(new StringWriter());

    screen.Resize(20, 4);
    Assert.That(screen.NeedsFullRepaint, Is.True);
  }

  [Test]
  public void AMonochromeTerminalGetsNoColourCodes() {
    var screen = new TerminalScreen(10, 1, ColorDepth.None);
    screen.BeginFrame();
    screen.Write(0, 0, "hi", Attributes.Bad);

    var writer = new StringWriter();
    screen.Flush(writer);
    Assert.That(writer.ToString(), Does.Not.Contain("31m"), "no red on a terminal that has none");
  }

}

/// <summary>
/// The renderer against a recorded machine, compared to a checked-in frame (PRD §9.6).
/// </summary>
/// <remarks>
/// This is the only test that exercises layout at all: everything else stops at the snapshot, and a
/// column whose width is computed wrongly passes every one of those and is unreadable here.
/// </remarks>
[TestFixture]
public sealed class GoldenFrameTests {

  [Test]
  public void TheComposedFrameMatchesTheGoldenOne() {
    var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    var golden = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "tui-desktop.txt");
    Assert.That(File.Exists(golden), $"the golden frame is missing: {golden}");

    using var probe = new LinuxProbe(new() {
      ProcRoot = fixtures,
      PasswdPath = Path.Combine(fixtures, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    using var sampler = new Sampler(probe);
    var ui = new TerminalUi(sampler, probe, null, 120, 40, ColorDepth.None) { ShowTiming = false };
    ui.View.TreeMode = true;
    ui.View.SortColumn = ProcessColumn.Pid;
    ui.View.SortDescending = false;
    ui.Update();
    ui.Update();

    var actual = Normalize(ui.Screen.Capture());
    var expected = Normalize(File.ReadAllText(golden));
    Assert.That(actual, Is.EqualTo(expected));
  }

  private static string Normalize(string text)
    => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

}
