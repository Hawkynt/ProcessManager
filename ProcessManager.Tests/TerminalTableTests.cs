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

  /// <summary>
  /// Ctrl+Y as a terminal actually delivers it: the control character itself, with the modifier
  /// flag set beside it because not every console layer sets one.
  /// </summary>
  private static ConsoleKeyInfo CtrlY => new('\u0019', ConsoleKey.Y, false, false, true);

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

  /// <summary>
  /// The third shape of copy §11 asks for. It needs a column, not a cell selection: this table has
  /// a column cursor and that is enough to say which column is meant.
  /// </summary>
  [Test]
  public void CopyingAColumnTakesItDownEveryRowThatIsShowing() {
    var (ui, probe) = Machine();
    using (probe) {
      // The column cursor starts on the pid, and the recorded machine has five processes.
      ui.HandleKey(CtrlY);

      var lines = (ui.LastCopiedText ?? string.Empty).TrimEnd('\n').Split('\n');
      Assert.That(lines[0], Is.EqualTo("PID"), "the column is named, so a paste is readable");
      Assert.That(lines, Has.Length.EqualTo(ui.View.MatchCount + 1));
      Assert.That(lines[1], Is.EqualTo("1"));
    }
  }

  /// <summary>Ticked rows mean those rows, the same way they do for a row copy.</summary>
  [Test]
  public void CopyingAColumnTakesTheTickedRowsWhenAnyAreTicked() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key(' '));
      ui.HandleKey(Key(' '));
      ui.HandleKey(CtrlY);

      var lines = (ui.LastCopiedText ?? string.Empty).TrimEnd('\n').Split('\n');
      Assert.That(lines, Has.Length.EqualTo(3), "a header and the two ticked rows");
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

  /// <remarks>
  /// The path is deliberately a long one, nested rather than merely named, because the length is
  /// what this caught: a macOS runner's temporary directory is a fifty-character
  /// <c>/var/folders/…</c> path, the status message built from it was longer than the terminal, and
  /// it was being trimmed from the front — so the export said nothing about having happened. A short
  /// path in <c>/tmp</c> hid that on two platforms out of three.
  /// </remarks>
  [Test]
  public void TheTableCanBeWrittenToAFileInTheFormatTheNameAsksFor() {
    var (ui, probe) = Machine();
    using (probe) {
      var directory = Path.Combine(
        Path.GetTempPath(),
        $"procman-tests-{Guid.NewGuid():N}",
        "a-directory-with-a-long-name",
        "and-another-one-under-it"
      );

      Directory.CreateDirectory(directory);
      var path = Path.Combine(directory, $"procman-export-{Guid.NewGuid():N}.csv");
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

        // The beginning of the sentence, which is the half that says what happened. A message too
        // long for the line loses its tail; a value too long for its column loses its head, and the
        // status line is not a column.
        Assert.That(Frame(ui), Does.Contain($"wrote {ui.View.RowCount} rows to"));
      } finally {
        File.Delete(path);
        Directory.Delete(directory);
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

      // Drawn, not merely kept in the model: at eighty columns the header is clipped to "CPU hi",
      // so this asks the layout where the column went rather than reading the letters back.
      Span<ColumnPlacement> placements = stackalloc ColumnPlacement[64];
      var count = ui.Columns.Place(79, placements);
      var found = false;
      for (var i = 0; i < count; ++i)
        found |= placements[i].Field == ProcessField.CpuHistory;

      Assert.That(found, Is.True, "a chosen column is squeezed by a narrow terminal, never dropped from it");
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

  /// <summary>
  /// Every appearance the palette has, at 256 colours. The one-slot test above would pass on a
  /// palette in which nine of the ten had been left the same colour, which is a table that has lost
  /// nine of its meanings while still emitting the escape the test looks for.
  /// </summary>
  [Test]
  public void EverySlotIsDefinedAndDistinctAtEveryDepth() {
    foreach (var depth in (ColorDepth[])[ColorDepth.Ansi16, ColorDepth.Ansi256, ColorDepth.TrueColor]) {
      var seen = new Dictionary<string, byte>();
      foreach (var attribute in Slots()) {
        var escape = Attributes.ToAnsi(attribute, depth);
        Assert.That(escape, Is.Not.Empty, $"{depth} slot {attribute}");
        Assert.That(
          seen.TryAdd(escape, attribute),
          Is.True,
          $"{depth}: attribute {attribute} paints the same as {(seen.TryGetValue(escape, out var other) ? other : (byte)0)}"
        );
      }

      Assert.That(seen, Has.Count.EqualTo(Attributes.SlotCount), $"{depth} defines every slot");
    }
  }

  /// <summary>
  /// The 256-colour palette uses the 256-colour form and the 24-bit one uses the 24-bit form. The
  /// two tables sit one under the other in the source and are edited by copying a line from one into
  /// the other, which is a silent way to send a terminal an escape it will render as text.
  /// </summary>
  [Test]
  public void NoPaletteSpeaksAnotherPalettesLanguage() {
    foreach (var attribute in Slots()) {
      var ansi256 = Attributes.ToAnsi(attribute, ColorDepth.Ansi256);
      Assert.That(ansi256, Does.Not.Contain("38;2;"), $"slot {attribute} at 256 colours");
      Assert.That(ansi256, Does.Not.Contain("48;2;"), $"slot {attribute} at 256 colours");

      var trueColor = Attributes.ToAnsi(attribute, ColorDepth.TrueColor);
      Assert.That(trueColor, Does.Not.Contain("38;5;"), $"slot {attribute} at 24 bits");
      Assert.That(trueColor, Does.Not.Contain("48;5;"), $"slot {attribute} at 24 bits");
    }

    // And the slots that carry a background really do carry one at 256 colours: a header drawn as a
    // foreground colour on the terminal's own background is not a header bar, it is a line of
    // coloured text (PRD §57.4).
    Assert.That(Attributes.ToAnsi(Attributes.Header, ColorDepth.Ansi256), Does.Contain("48;5;"));
    Assert.That(Attributes.ToAnsi((byte)(Attributes.Normal | Attributes.Marked), ColorDepth.Ansi256), Does.Contain("38;5;"));
  }

  /// <summary>
  /// No combination of the flags can fall off the end of a palette. The flags are a bitfield and the
  /// palettes are arrays, so an attribute nobody thought of is an index nobody sized for — and it
  /// would throw while painting, on somebody's terminal, long after the tests went green.
  /// </summary>
  [Test]
  public void EveryAttributeAnyCellCanCarryPaintsSomething() {
    for (var attribute = 0; attribute <= byte.MaxValue; ++attribute)
      foreach (var depth in Enum.GetValues<ColorDepth>()) {
        string? painted = null;
        Assert.That(
          () => painted = Attributes.ToAnsi((byte)attribute, depth),
          Throws.Nothing,
          $"attribute {attribute} at {depth}"
        );

        Assert.That(painted, Is.Not.Null.And.Not.Empty, $"attribute {attribute} at {depth}");
      }
  }

  /// <summary>
  /// A 256-colour terminal gets the same characters a monochrome one does. Colour is an attribute
  /// plane beside the text and must not move a single cell — a palette change that shifted the
  /// layout would be caught by no golden frame, because the goldens are text.
  /// </summary>
  [Test]
  public void ColourChangesNothingAboutWhereTheCharactersGo() {
    var monochrome = GoldenFrameTests.Frame(120, 30, ColorDepth.None);
    var rich = GoldenFrameTests.Frame(120, 30, ColorDepth.Ansi256);

    Assert.That(rich, Is.EqualTo(monochrome));
  }

  /// <summary>
  /// And the frame really is painted in 256 colours rather than merely permitted to be: flushed to
  /// a writer it carries the escapes, and more than one of them.
  /// </summary>
  [Test]
  public void AWholeFrameAt256ColoursCarriesTheEscapes() {
    var (ui, probe) = Machine(depth: ColorDepth.Ansi256);
    using (probe) {
      var written = new StringWriter();
      ui.Screen.Flush(written);
      var text = written.ToString();

      Assert.That(text, Does.Contain("38;5;"), "the 256-colour cube reached the terminal");
      Assert.That(Distinct(text), Is.GreaterThan(3), "and more than one colour of it");
    }

    static int Distinct(string text) {
      var found = new HashSet<string>(StringComparer.Ordinal);
      var at = text.IndexOf("[", StringComparison.Ordinal);
      while (at >= 0) {
        var end = text.IndexOf('m', at);
        if (end < 0)
          break;

        found.Add(text[at..(end + 1)]);
        at = text.IndexOf("[", end, StringComparison.Ordinal);
      }

      return found.Count;
    }
  }

  /// <summary>
  /// Which palette the terminal actually gets, decided from the environment rather than guessed.
  /// </summary>
  /// <remarks>
  /// The tests above all pass the depth in, which proves the palettes are right and says nothing
  /// about whether the program ever picks the right one. This is the only place that choice is made,
  /// and until now nothing read it — so a 256-colour terminal getting sixteen, or an honest
  /// <c>NO_COLOR</c> being ignored, was invisible.
  /// </remarks>
  [TestCase(null, null, null, ColorDepth.None, TestName = "nothing said at all")]
  [TestCase(null, null, "dumb", ColorDepth.None, TestName = "a dumb terminal")]
  [TestCase(null, null, "xterm", ColorDepth.Ansi16, TestName = "the original sixteen")]
  [TestCase(null, null, "xterm-256color", ColorDepth.Ansi256, TestName = "the 256-colour cube")]
  [TestCase(null, null, "xterm-direct", ColorDepth.Ansi256, TestName = "a direct-colour terminfo")]
  [TestCase(null, "truecolor", "xterm", ColorDepth.TrueColor, TestName = "COLORTERM outranks TERM")]
  [TestCase(null, "24bit", "xterm-256color", ColorDepth.TrueColor, TestName = "and its other spelling")]
  [TestCase("1", "truecolor", "xterm-256color", ColorDepth.None, TestName = "NO_COLOR outranks everything")]
  [TestCase(null, "yes", "xterm-256color", ColorDepth.Ansi256, TestName = "a COLORTERM nobody defined is not 24-bit")]
  [NonParallelizable]
  public void TheDepthIsReadOffTheEnvironment(string? noColor, string? colorTerm, string? term, ColorDepth expected) {
    var was = (
      No: Environment.GetEnvironmentVariable("NO_COLOR"),
      Color: Environment.GetEnvironmentVariable("COLORTERM"),
      Term: Environment.GetEnvironmentVariable("TERM")
    );

    try {
      Environment.SetEnvironmentVariable("NO_COLOR", noColor);
      Environment.SetEnvironmentVariable("COLORTERM", colorTerm);
      Environment.SetEnvironmentVariable("TERM", term);

      Assert.That(TerminalHost.DetectColorDepth(), Is.EqualTo(expected));
    } finally {
      Environment.SetEnvironmentVariable("NO_COLOR", was.No);
      Environment.SetEnvironmentVariable("COLORTERM", was.Color);
      Environment.SetEnvironmentVariable("TERM", was.Term);
    }
  }

  /// <summary>Every attribute a cell can carry that is not one of the flag combinations.</summary>
  private static IEnumerable<byte> Slots() {
    for (byte colour = Attributes.Normal; colour <= Attributes.Selected; ++colour)
      yield return colour;

    // The two the flags reach and a plain colour does not.
    yield return Attributes.Marked;
    yield return Attributes.Match;
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
