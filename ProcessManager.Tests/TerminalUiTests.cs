using Hawkynt.ProcessManager.Model;
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

  /// <summary>
  /// One frame per breakpoint, because the layout is not the same table at three sizes (PRD §57.1).
  /// </summary>
  /// <remarks>
  /// The three widths are the ones people have: the eighty-column default, an SSH window, and a
  /// maximised desktop terminal. Each drops or keeps different columns, so a golden at one of them
  /// proves nothing about the other two — which is how a name column ten characters wide survived
  /// review at 160 and shipped as "kthread" for every row at 80.
  /// </remarks>
  [TestCase(80, 24, "tui-80x24.txt")]
  [TestCase(120, 30, "tui-120x30.txt")]
  [TestCase(160, 50, "tui-160x50.txt")]
  public void TheComposedFrameMatchesTheGoldenOne(int width, int height, string file) {
    var golden = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", file);
    Assert.That(File.Exists(golden), $"the golden frame is missing: {golden}");

    var actual = Normalize(Frame(width, height));
    var expected = Normalize(File.ReadAllText(golden));
    Assert.That(actual, Is.EqualTo(expected));
  }

  /// <summary>Composes the frame the golden files were taken from.</summary>
  internal static string Frame(int width, int height) {
    var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new LinuxProbe(new() {
      ProcRoot = fixtures,
      PasswdPath = Path.Combine(fixtures, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    using var sampler = new Sampler(probe);
    // Both pinned. The frame is compared byte for byte, so nothing about it may come from the
    // machine: the sample cost would differ every run, and the block characters would differ between
    // a UTF-8 desktop and a CI runner whose LANG is C — which is exactly how this first broke.
    var ui = new TerminalUi(sampler, probe, null, width, height, ColorDepth.None) {
      ShowTiming = false,
      UseBlockCharacters = true,
    };
    ui.View.TreeMode = true;
    ui.View.SortColumn = ProcessField.Pid;
    ui.View.SortDescending = false;
    ui.Update();
    ui.Update();
    return ui.Screen.Capture();
  }

  /// <summary>
  /// The name column is the one every other column steals from, so it is checked by name.
  /// </summary>
  /// <remarks>
  /// A golden frame catches a change; it does not say whether the change was any good. This says
  /// what "good" is at each size: the process names are all there, whole, at every breakpoint.
  /// </remarks>
  [TestCase(80, 24)]
  [TestCase(120, 30)]
  [TestCase(160, 50)]
  public void EveryProcessNameFitsAtEverySize(int width, int height) {
    var frame = Frame(width, height);
    foreach (var name in (string[])["systemd", "kthreadd", "bash", "sleep"])
      Assert.That(frame, Does.Contain(name), $"{name} is clipped at {width}×{height}");
  }

  private static string Normalize(string text)
    => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

}

/// <summary>
/// The terminal's process-properties view, against the recorded machine (PRD §6.2, §11).
/// </summary>
[TestFixture]
public sealed class DetailViewTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProbe Probe() => new(new() {
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  });

  private static (LinuxProbe Probe, SystemSnapshot Snapshot, ProcessRecord Process) Sample(int pid) {
    var probe = Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return (probe, snapshot, process);

    Assert.Fail($"pid {pid} is not in the fixture");
    return default;
  }

  [Test]
  public void TheOverviewNamesTheProcessAndItsNumbers() {
    var (probe, _, process) = Sample(1001);
    using (probe) {
      var view = new DetailView(probe);
      view.Open(process.Key);
      view.Collect(in process);

      var screen = new TerminalScreen(120, 40, ColorDepth.None);
      screen.BeginFrame();
      view.Draw(screen, in process);
      var frame = screen.Capture();

      Assert.That(frame, Does.Contain("foo) 0 (bar"), "the title names the process");
      Assert.That(frame, Does.Contain("Overview"));
      Assert.That(frame, Does.Contain("alice"), "the owner is resolved");
      Assert.That(frame, Does.Contain("--stress"), "the command line is shown");
    }
  }

  [Test]
  public void EveryTabCollectsWithoutThrowing() {
    // The pages hit five different probe queries, three of which return nothing for a fixture that
    // records no threads, handles or sockets. Nothing may throw on that — an empty page is a normal
    // answer and has to render as one.
    var (probe, _, process) = Sample(1000);
    using (probe) {
      var view = new DetailView(probe);
      view.Open(process.Key);

      for (var i = 0; i < 6; ++i) {
        var screen = new TerminalScreen(120, 40, ColorDepth.None);
        screen.BeginFrame();
        Assert.That(() => view.Draw(screen, in process), Throws.Nothing, $"tab {view.Tab}");
        view.NextTab();
      }
    }
  }

  [Test]
  public void APageWithNothingOnItSaysSoRatherThanLookingBroken() {
    var (probe, _, process) = Sample(1002);
    using (probe) {
      var view = new DetailView(probe);
      view.Open(process.Key);
      view.NextTab();                                    // Overview -> Threads
      while (view.Tab != DetailTab.Network)
        view.NextTab();

      var screen = new TerminalScreen(120, 40, ColorDepth.None);
      screen.BeginFrame();
      view.Draw(screen, in process);

      Assert.That(screen.Capture(), Does.Contain("nothing to show"));
    }
  }

  [Test]
  public void TheFrameDoesNotDependOnTheMachinesLocale() {
    // The regression that took three CI legs down: the golden was generated on a UTF-8 desktop and
    // compared on runners whose LANG is C, where the renderer had quietly fallen back to ASCII.
    var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new LinuxProbe(new() {
      ProcRoot = fixtures,
      PasswdPath = Path.Combine(fixtures, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    using var sampler = new Sampler(probe);
    var blocks = Compose(sampler, probe, true);
    var ascii = Compose(sampler, probe, false);

    Assert.That(blocks, Is.Not.EqualTo(ascii), "the two ramps really do render differently");
    Assert.That(Compose(sampler, probe, true), Is.EqualTo(blocks), "and each is stable");

    static string Compose(Sampler sampler, LinuxProbe probe, bool unicode) {
      var ui = new TerminalUi(sampler, probe, null, 120, 40, ColorDepth.None) {
        ShowTiming = false,
        UseBlockCharacters = unicode,
      };

      ui.View.TreeMode = true;
      ui.View.SortColumn = ProcessField.Pid;
      ui.View.SortDescending = false;
      ui.Update();
      ui.Update();
      return ui.Screen.Capture();
    }
  }

  [Test]
  public void TabsWrapInBothDirections() {
    using var probe = Probe();
    var view = new DetailView(probe);
    Assert.That(view.Tab, Is.EqualTo(DetailTab.Overview));

    view.PreviousTab();
    Assert.That(view.Tab, Is.EqualTo(DetailTab.Network), "backwards from the first is the last");

    view.NextTab();
    Assert.That(view.Tab, Is.EqualTo(DetailTab.Overview));
  }

}
