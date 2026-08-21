using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Terminal;
using static Hawkynt.ProcessManager.Tests.TerminalFixture;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What the table can be asked to do: tick rows, copy them, export them, sort by more than one
/// column, and find something in them (PRD §11).
/// </summary>
[TestFixture]
public sealed class TerminalTableTests {

  [Test]
  public void TickingARowMarksItInTheGutterAndMovesOn() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key(' '));
      ui.HandleKey(Key(' '));

      Assert.That(ui.MarkedCount, Is.EqualTo(2), "each tick moved to the next row, so two keys ticked two rows");
      var lines = Lines(ui);
      var ticked = 0;
      foreach (var line in lines)
        if (line.StartsWith('*'))
          ++ticked;

      // The mark is a character in the gutter, not only a colour: a monochrome terminal has to show
      // it too (PRD §57.4).
      Assert.That(ticked, Is.EqualTo(2));
      Assert.That(Frame(ui), Does.Contain("2 ticked"));
    }
  }

  [Test]
  public void EverythingCanBeTickedAndTheSelectionInverted() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(new('\u0001', ConsoleKey.A, false, false, true));
      Assert.That(ui.MarkedCount, Is.EqualTo(ui.View.RowCount));

      ui.HandleKey(Key('v'));
      Assert.That(ui.MarkedCount, Is.Zero, "inverting everything ticked leaves nothing ticked");

      ui.HandleKey(Key('v'));
      Assert.That(ui.MarkedCount, Is.EqualTo(ui.View.RowCount));

      ui.HandleKey(Key('U'));
      Assert.That(ui.MarkedCount, Is.Zero);
    }
  }

  [Test]
  public void CopyingACellTakesTheValueRatherThanWhatTheColumnShows() {
    var (ui, probe) = Machine();
    using (probe) {
      // The column cursor starts on the pid, and the first row is pid 1.
      ui.HandleKey(Key('y'));
      Assert.That(ui.LastCopiedText, Is.EqualTo("1"));
    }
  }

  [Test]
  public void CopyingTakesEveryTickedRowAndNamesItsColumns() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key(' '));
      ui.HandleKey(Key(' '));
      ui.HandleKey(Key('Y'));

      var lines = (ui.LastCopiedText ?? string.Empty).TrimEnd('\n').Split('\n');
      Assert.That(lines, Has.Length.EqualTo(3), "a header and the two ticked rows");
      Assert.That(lines[0], Does.StartWith("PID\t"), "the columns are named, so a paste is readable");
      Assert.That(lines[1].Split('\t')[0], Is.EqualTo("1"));
    }
  }

  [Test]
  public void ACopyWithNothingToPasteIntoSaysSoRatherThanPretending() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('y'));
      Assert.That(Frame(ui), Does.Contain("nothing is attached"));
    }
  }

  [Test]
  public void ACopyIsOfferedToTheTerminalRatherThanToTheScreen() {
    var (ui, probe) = Machine();
    using (probe) {
      var terminal = new StringWriter();
      ui.ClipboardOutput = terminal;
      ui.HandleKey(Key('y'));

      Assert.That(terminal.ToString(), Does.Contain("52;c;"), "OSC 52, which is the only clipboard an SSH session has");
      Assert.That(Frame(ui), Does.Contain("offered"), "and it says offered, because nothing answers");
    }
  }

  [Test]
  public void TheTableCanBeWrittenToAFileInTheFormatTheNameAsksFor() {
    var (ui, probe) = Machine();
    using (probe) {
      var path = Path.Combine(Path.GetTempPath(), $"procman-export-{Guid.NewGuid():N}.csv");
      try {
        ui.HandleKey(Key('X'));
        // The prompt opens with the last path in it, so a second export is one keystroke; this one
        // is typing a new one over it.
        for (var i = 0; i < 64; ++i)
          ui.HandleKey(Key(ConsoleKey.Backspace));

        foreach (var character in path)
          ui.HandleKey(Key(character));

        ui.HandleKey(Key(ConsoleKey.Enter));

        Assert.That(File.Exists(path), Is.True, Frame(ui));
        var lines = File.ReadAllLines(path);
        Assert.That(lines[0], Does.Contain(","), "the extension picked CSV");
        Assert.That(lines, Has.Length.EqualTo(ui.View.RowCount + 1));
        Assert.That(Frame(ui), Does.Contain("wrote"));
      } finally {
        File.Delete(path);
      }
    }
  }

  [Test]
  public void ASecondSortColumnBreaksTheTiesAndSaysSoInTheHeader() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.Columns.SetCurrent(1);                            // the user column
      ui.HandleKey(Key('o'));

      Assert.That(ui.View.SecondarySort, Has.Count.EqualTo(1));
      Assert.That(ui.View.SecondarySort[0].Field, Is.EqualTo(ProcessField.UserName));

      var header = Lines(ui)[HeaderRow(ui)];
      Assert.That(header, Does.Contain("User2"), "the rank, not a second arrow that says nothing about which wins");
    }
  }

  [Test]
  public void SortingByOneColumnAgainForgetsTheTieBreakers() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.Columns.SetCurrent(1);
      ui.HandleKey(Key('o'));
      ui.HandleKey(Key('P'));

      Assert.That(ui.View.SortColumn, Is.EqualTo(ProcessField.CpuPercent));
      Assert.That(ui.View.SecondarySort, Is.Empty);
    }
  }

  [Test]
  public void CaseCanBeMadeToMatter() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('\\'));
      foreach (var character in "BASH")
        ui.HandleKey(Key(character));

      ui.HandleKey(Key(ConsoleKey.Enter));
      ui.Refresh();
      Assert.That(ui.View.RowCount, Is.GreaterThan(0), "case is ignored by default");

      ui.HandleKey(Key('!'));
      ui.Refresh();
      Assert.That(ui.View.RowCount, Is.Zero, "and matters once it is asked to");
      Assert.That(Frame(ui), Does.Contain("case"), "the state is on screen, not only in the code");
    }
  }

  [Test]
  public void WhatASearchMatchedIsPickedOutOfTheRow() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('/'));
      foreach (var character in "bash")
        ui.HandleKey(Key(character));

      ui.Refresh();
      Assert.That(ui.View.RowCount, Is.EqualTo(5), "a search hides nothing — it goes to the row");

      var highlighted = 0;
      foreach (var attribute in ui.Screen.CaptureAttributes())
        if ((attribute & Attributes.Match) != 0)
          ++highlighted;

      Assert.That(highlighted, Is.EqualTo(4), "the four characters of the match, and nothing else");
    }
  }

  [Test]
  public void AColumnCanBeFittedToWhatIsOnScreen() {
    var (ui, probe) = Machine();
    using (probe) {
      var name = -1;
      for (var i = 0; i < ui.Columns.Count; ++i)
        if (ui.Columns.FieldAt(i) == ProcessField.Name)
          name = i;

      ui.Columns.SetCurrent(name);
      ui.HandleKey(Key('a'));
      Assert.That(ui.Columns.WidthAt(name), Is.GreaterThanOrEqualTo("kthreadd".Length));
    }
  }

  [Test]
  public void TheColumnsGoBackToTheDefaultsOnRequest() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('}'));
      ui.HandleKey(Key('.'));
      Assert.That(ui.Columns.Customised, Is.True);

      ui.HandleKey(Key('0'));
      Assert.That(ui.Columns.Customised, Is.False);
      Assert.That(ui.Columns.FieldAt(0), Is.EqualTo(ProcessField.Pid));
    }
  }

}

/// <summary>The mouse, driven through the UI rather than through a terminal (PRD §57.5).</summary>
[TestFixture]
public sealed class TerminalMouseTests {

  [Test]
  public void ClickingAHeaderSortsByThatColumn() {
    var (ui, probe) = Machine();
    using (probe) {
      var row = HeaderRow(ui);
      var x = Lines(ui)[row].IndexOf("CPU%", StringComparison.Ordinal);
      Assert.That(ui.HandleMouse(Click(x, row)), Is.True);

      Assert.That(ui.View.SortColumn, Is.EqualTo(ProcessField.CpuPercent));
      Assert.That(ui.View.SortDescending, Is.True, "the column's own preference, so the busiest is at the top");
    }
  }

  [Test]
  public void ShiftClickingAHeaderAddsATieBreakerRatherThanReplacingTheSort() {
    var (ui, probe) = Machine();
    using (probe) {
      var row = HeaderRow(ui);
      var x = Lines(ui)[row].IndexOf("User", StringComparison.Ordinal);
      ui.HandleMouse(Click(x, row, shift: true));

      Assert.That(ui.View.SortColumn, Is.EqualTo(ProcessField.Pid), "the primary sort is untouched");
      Assert.That(ui.View.SecondarySort[0].Field, Is.EqualTo(ProcessField.UserName));
    }
  }

  [Test]
  public void ClickingARowSelectsItAndTheColumnUnderThePointer() {
    var (ui, probe) = Machine();
    using (probe) {
      var top = HeaderRow(ui) + 1;
      ui.HandleMouse(Click(4, top + 2));
      ui.HandleKey(Key('y'));

      // Third row by pid is 1000; copying the cell under the cursor proves both the row and the
      // column landed where the click was.
      Assert.That(ui.LastCopiedText, Is.EqualTo("1000"));
    }
  }

  [Test]
  public void ClickingTheGutterTicksTheRow() {
    var (ui, probe) = Machine();
    using (probe) {
      var top = HeaderRow(ui) + 1;
      ui.HandleMouse(Click(0, top + 1));
      Assert.That(ui.MarkedCount, Is.EqualTo(1));
    }
  }

  [Test]
  public void TheWheelScrollsTheList() {
    var (ui, probe) = Machine(120, 11);
    using (probe) {
      Assert.That(Frame(ui), Does.Contain("systemd"));

      ui.HandleMouse(new(MouseButton.WheelDown, 10, 8, true, false, false, false, false));
      Assert.That(Frame(ui), Does.Not.Contain("systemd"), "the top row scrolled off");
      Assert.That(Frame(ui), Does.Contain("sleep"), "and the bottom one came on");
    }
  }

  [Test]
  public void ClickingTheTabRowChangesThePage() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleMouse(Click(15, 0));
      Assert.That(ui.Page, Is.EqualTo(TerminalPage.Performance));
      Assert.That(Frame(ui), Does.Contain("Processor, whole machine"));

      ui.HandleMouse(Click(2, 0));
      Assert.That(ui.Page, Is.EqualTo(TerminalPage.Processes));
    }
  }

  [Test]
  public void RightClickingOpensTheActionsForTheRowUnderIt() {
    var (ui, probe) = Machine();
    using (probe) {
      var top = HeaderRow(ui) + 1;
      ui.HandleMouse(Click(6, top + 1, MouseButton.Right));

      var frame = Frame(ui);
      Assert.That(frame, Does.Contain("Do to it"));
      Assert.That(frame, Does.Contain("terminate it"));
    }
  }

}

/// <summary>The pages and panes the table shares its screen with (PRD §57.1, §57.3, §57.4).</summary>
[TestFixture]
public sealed class TerminalPageTests {

  [Test]
  public void TheHelpPageListsTheKeysAsTheyAreBoundNow() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.Keys = KeyBindings.Parse("quit = Q\n");
      ui.HandleKey(Key('?'));

      var frame = Frame(ui);
      Assert.Multiple(() => {
        Assert.That(frame, Does.Contain("Keys"));
        Assert.That(frame, Does.Contain("back, and out"));
        Assert.That(frame, Does.Contain("Q"), "the rebound key, not the default");
        Assert.That(frame, Does.Contain("keys.conf"), "and where to change them");
      });

      ui.HandleKey(Key(ConsoleKey.Escape));
      Assert.That(Frame(ui), Does.Not.Contain("back, and out"));
    }
  }

  [Test]
  public void TheColumnChooserOffersTheSetsAndTheColumns() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('c'));

      var frame = Frame(ui);
      Assert.Multiple(() => {
        Assert.That(frame, Does.Contain("Column sets"));
        Assert.That(frame, Does.Contain("minimal"));
        Assert.That(frame, Does.Contain("[x] Private bytes"));
      });
    }
  }

  [Test]
  public void TheLowerPaneSaysWhatAPlotCannotBeReadFor() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key(ConsoleKey.Tab));

      var frame = Frame(ui);
      // The four figures §57.4 asks for, for the row the cursor is on.
      Assert.That(frame, Does.Contain("min"));
      Assert.That(frame, Does.Contain("avg"));
      Assert.That(frame, Does.Contain("max"));
      Assert.That(frame, Does.Contain("now"));
    }
  }

  [Test]
  public void ThePerformancePageDrawsTheMachineRatherThanTheProcesses() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('g'));

      var frame = Frame(ui);
      Assert.Multiple(() => {
        Assert.That(frame, Does.Contain("Memory in use"));
        Assert.That(frame, Does.Contain("Swap in use"));
        Assert.That(frame, Does.Not.Contain("systemd"), "the process list is not on this page");
      });
    }
  }

  [Test]
  public void ASubtreeCanBeFoldedAndUnfolded() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.View.TreeMode = true;
      ui.Refresh();
      ui.HandleKey(Key(ConsoleKey.Home));
      var whole = ui.View.RowCount;

      ui.HandleKey(Key(ConsoleKey.LeftArrow));
      Assert.That(ui.View.RowCount, Is.LessThan(whole), "the children went away");
      Assert.That(Frame(ui), Does.Contain("+ systemd"), "and the row says it is folded, in a character");

      ui.HandleKey(Key(ConsoleKey.RightArrow));
      Assert.That(ui.View.RowCount, Is.EqualTo(whole));
    }
  }

  [Test]
  public void PausingStopsTheSamplingRatherThanTheDrawing() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('p'));
      Assert.That(ui.Paused, Is.True);
      Assert.That(Frame(ui), Does.Contain("PAUSED"));

      ui.HandleKey(Key('p'));
      Assert.That(ui.Paused, Is.False);
    }
  }

  [Test]
  public void ANarrowTerminalDropsColumnsAndAWideOneGetsThemBack() {
    var (ui, probe) = Machine(160, 50);
    using (probe) {
      Assert.That(Lines(ui)[HeaderRow(ui)], Does.Contain("I/O hist"));

      ui.Resize(80, 24);
      Assert.That(Lines(ui)[HeaderRow(ui)], Does.Not.Contain("I/O hist"));
      Assert.That(Lines(ui)[HeaderRow(ui)], Does.Contain("Process"), "what is left still names the process");

      ui.Resize(160, 50);
      Assert.That(Lines(ui)[HeaderRow(ui)], Does.Contain("I/O hist"));
    }
  }

  [Test]
  public void ChosenColumnsSurviveAResizeThatWouldHaveRePickedThem() {
    var (ui, probe) = Machine(160, 50);
    using (probe) {
      ui.HandleKey(Key('.'));                              // one column made wider, by hand
      var chosen = ui.Columns.Count;
      ui.Resize(80, 24);

      Assert.That(ui.Columns.Count, Is.EqualTo(chosen), "a layout somebody chose is not undone by a resize");
      Assert.That(Frame(ui), Does.Contain("CPU hist"), "and what fits of it is still drawn");
    }
  }

}

/// <summary>What the terminal draws when it cannot draw the usual thing (PRD §57.4).</summary>
[TestFixture]
public sealed class TerminalFallbackTests {

  [Test]
  public void EachColourDepthGetsTheEscapesItUnderstands() {
    Assert.Multiple(() => {
      Assert.That(Paint(ColorDepth.TrueColor), Does.Contain("38;2;"), "24-bit");
      Assert.That(Paint(ColorDepth.Ansi256), Does.Contain("38;5;"), "the 256-colour cube");
      Assert.That(Paint(ColorDepth.Ansi16), Does.Contain("0;36m"), "the original sixteen");
      Assert.That(Paint(ColorDepth.None), Does.Not.Contain("38;"), "and none of it on a terminal with no colour");
    });

    static string Paint(ColorDepth depth) {
      var screen = new TerminalScreen(10, 1, depth);
      screen.BeginFrame();
      screen.Write(0, 0, "hi", Attributes.Accent);
      var writer = new StringWriter();
      screen.Flush(writer);
      return writer.ToString();
    }
  }

  [Test]
  public void AMatchedRunIsVisibleWithoutColourAtAll() {
    // Reverse video is all a monochrome terminal has, and a highlight that is only a colour is
    // invisible on one (PRD §57.4).
    Assert.That(Attributes.ToAnsi(Attributes.Match, ColorDepth.None), Does.Contain("7m"));
    Assert.That(Attributes.ToAnsi(Attributes.Marked, ColorDepth.None), Is.Not.EqualTo(Attributes.ToAnsi(Attributes.Normal, ColorDepth.None)));
  }

  [Test]
  public void TheBrailleRampPacksTwiceAsManySamplesAsTheBlockOne() {
    var history = new Sampling.HistoryRing<Model.Rate>(64);
    for (var i = 0; i < 16; ++i)
      history.Add(Model.Rate.Of(100));

    var braille = BrailleSparkline.Render(8, history, 100);
    Assert.That(braille, Has.Length.EqualTo(8));
    foreach (var character in braille)
      Assert.That(character, Is.GreaterThanOrEqualTo('⠀'), "every cell is a braille pattern");

    // Eight cells hold sixteen samples, where the eighth-blocks would have held eight.
    var half = BrailleSparkline.Render(8, Half(history), 100);
    Assert.That(half, Does.StartWith("⠀"), "half the samples fill half the width");
    Assert.That(half, Is.Not.EqualTo(braille));

    static Sampling.HistoryRing<Model.Rate> Half(Sampling.HistoryRing<Model.Rate> source) {
      var result = new Sampling.HistoryRing<Model.Rate>(64);
      for (var i = 0; i < source.Count / 2; ++i)
        result.Add(source[i]);

      return result;
    }
  }

  [Test]
  public void ThePlotCanBeReadAsFiguresInstead() {
    var history = new Sampling.HistoryRing<Model.Rate>(64);
    foreach (var value in (double[])[10, 50, 30])
      history.Add(Model.Rate.Of(value));

    Assert.That(
      HistorySummary.Describe(history, Sampling.HistorySeries.Cpu),
      Is.EqualTo("min 10%  avg 30%  max 50%  now 30%")
    );

    Assert.That(HistorySummary.Compact(history, Sampling.HistorySeries.Cpu, 12), Does.Contain("30%"));
    Assert.That(HistorySummary.Describe(null, Sampling.HistorySeries.Cpu), Is.EqualTo("no samples yet"));
  }

  [Test]
  public void TheHistoryColumnsBecomeNumbersWhenAskedTo() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.GraphStyle = GraphStyle.Numbers;
      var frame = Frame(ui);

      Assert.That(frame, Does.Contain("^"), "the peak marker of the compact form");
      Assert.That(frame, Does.Not.Contain("█"), "and no blocks at all");
    }
  }

}
