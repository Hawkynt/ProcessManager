using System.Text;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>A terminal over the recorded machine, for the tests that press keys at it.</summary>
internal static class TerminalFixture {

  public static string Root => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  public static LinuxProbe Probe() => new(new() {
    ProcRoot = Root,
    PasswdPath = Path.Combine(Root, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  });

  /// <summary>A UI that has sampled twice, sorted by pid, with the first row selected.</summary>
  public static (TerminalUi Ui, LinuxProbe Probe) Machine(int width = 120, int height = 30) {
    var probe = Probe();
    var ui = new TerminalUi(new Sampler(probe), probe, null, width, height, ColorDepth.None) {
      ShowTiming = false,
      UseBlockCharacters = true,
    };

    ui.View.SortColumn = ProcessField.Pid;
    ui.View.SortDescending = false;
    ui.Update();
    ui.Update();
    ui.HandleKey(Key(ConsoleKey.Home));
    ui.Refresh();
    return (ui, probe);
  }

  public static ConsoleKeyInfo Key(char character) => new(character, default, char.IsUpper(character), false, false);

  public static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

  public static string Frame(TerminalUi ui) {
    ui.Refresh();
    return ui.Screen.Capture();
  }

  public static string[] Lines(TerminalUi ui) => Frame(ui).Split('\n');

  /// <summary>Which screen row the column headers ended up on, found the way a person would.</summary>
  public static int HeaderRow(TerminalUi ui) {
    var lines = Lines(ui);
    for (var i = 0; i < lines.Length; ++i)
      if (lines[i].Contains("PID", StringComparison.Ordinal) && lines[i].Contains("Process", StringComparison.Ordinal))
        return i;

    Assert.Fail("no column header in the frame");
    return -1;
  }

  public static MouseEvent Click(int x, int y, MouseButton button = MouseButton.Left, bool shift = false, bool control = false)
    => new(button, x, y, true, false, shift, false, control);

}

/// <summary>
/// The column model: order, width, visibility, pinning and the sideways scroll (PRD §11, §57.2).
/// </summary>
[TestFixture]
public sealed class ColumnLayoutTests {

  private static ColumnLayout Layout() => new([
    ProcessField.Pid,
    ProcessField.UserName,
    ProcessField.CpuPercent,
    ProcessField.Name,
  ]);

  [Test]
  public void ReorderingMovesTheCursorWithTheColumn() {
    var columns = Layout();
    columns.MoveCurrent(1);
    Assert.That(columns.CurrentField, Is.EqualTo(ProcessField.UserName));

    Assert.That(columns.Reorder(-1), Is.True);
    Assert.That(columns.FieldAt(0), Is.EqualTo(ProcessField.UserName));
    Assert.That(columns.CurrentField, Is.EqualTo(ProcessField.UserName), "the cursor followed the column it moved");
  }

  [Test]
  public void AColumnCannotBeResizedIntoSomethingUnreadable() {
    var columns = Layout();
    for (var i = 0; i < 40; ++i)
      columns.ResizeCurrent(-1);

    Assert.That(columns.WidthAt(0), Is.GreaterThanOrEqualTo(3));
  }

  [Test]
  public void ResetPutsBackTheOrderTheWidthsAndTheHiddenColumns() {
    var columns = Layout();
    var width = columns.WidthAt(0);
    columns.ResizeCurrent(9);
    columns.Reorder(1);
    columns.SetVisible(2, false);

    columns.Reset();
    Assert.Multiple(() => {
      Assert.That(columns.FieldAt(0), Is.EqualTo(ProcessField.Pid));
      Assert.That(columns.WidthAt(0), Is.EqualTo(width));
      Assert.That(columns.IsVisible(2), Is.True);
      Assert.That(columns.Customised, Is.False, "a reset layout is not a customised one, so a resize may re-pick it");
    });
  }

  [Test]
  public void APinnedColumnStaysWhileTheRestScroll() {
    var columns = Layout();
    columns.ScrollBy(2);

    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[8];
    var count = columns.Place(80, placements);
    Assert.That(count, Is.GreaterThan(1));
    Assert.That(placements[0].Field, Is.EqualTo(ProcessField.Pid), "the pinned column is still first");
    Assert.That(placements[0].Frozen, Is.True);
    Assert.That(placements[1].Field, Is.EqualTo(ProcessField.CpuPercent), "the user column scrolled off");
  }

  [Test]
  public void AClickFindsTheColumnItLandedOn() {
    var columns = Layout();
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[8];
    var count = columns.Place(120, placements);
    Assert.That(count, Is.EqualTo(4));

    for (var i = 0; i < count; ++i)
      Assert.That(columns.HitTest(120, placements[i].X + 1), Is.EqualTo(placements[i].Index), $"column {i}");
  }

  /// <remarks>
  /// The process name declares 120 characters because the window has them to give. A terminal that
  /// handed it all 120 drew nothing whatever of the columns ordered after it — which nobody noticed
  /// while the name was always last, and which appeared the moment a column set put something behind
  /// it.
  /// </remarks>
  [Test]
  public void AColumnOrderedAfterTheNameIsStillDrawn() {
    var columns = new ColumnLayout([
      ProcessField.Pid,
      ProcessField.Name,
      ProcessField.CpuPercentDelta,
      ProcessField.ThreadCount,
    ]);

    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[8];
    var count = columns.Place(90, placements);

    Assert.That(count, Is.EqualTo(4), "every column reached the line");
    Assert.That(placements[3].X + placements[3].Width, Is.LessThanOrEqualTo(90), "and none of it hangs off the edge");
    for (var i = 1; i < count; ++i)
      Assert.That(placements[i].X, Is.GreaterThan(placements[i - 1].X), "in order, without overlapping");
  }

  [Test]
  public void ASqueezedColumnKeepsEnoughOfItselfToBeRecognised() {
    // Eleven columns in eighty characters: each one narrower than it asked for, none of them gone.
    var columns = new ColumnLayout([.. Settings.UserSettings.Presets["expert"]]);
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[32];
    var count = columns.Place(80, placements);

    Assert.That(count, Is.EqualTo(columns.Count));
    for (var i = 0; i < count; ++i)
      Assert.That(placements[i].Width, Is.GreaterThanOrEqualTo(Math.Min(6, columns.WidthAt(placements[i].Index))));
  }

  /// <summary>
  /// A set with more columns than the terminal can hold at their floor must not make the columns it
  /// <em>can</em> draw pay for the ones it cannot. Reserving room for all twenty-five of the
  /// forensic set's columns starved the process name to six characters at a hundred and sixty wide,
  /// which renders every row as <c>kthrea</c> (PRD §57.2).
  /// </summary>
  [Test]
  public void ColumnsThatCannotFitOnTheScreenAreNotReservedFor() {
    var columns = new ColumnLayout([.. Settings.UserSettings.Presets["forensic"]]);
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[64];
    // The width the table is really laid out in on a hundred-and-sixty-column terminal: one
    // character of it is the gutter the ticks are drawn in.
    var count = columns.Place(159, placements);

    Assert.That(count, Is.LessThan(columns.Count), "a set this wide does not fit, which is the case under test");
    Assert.That(placements[0].Field, Is.EqualTo(ProcessField.Name));
    Assert.That(placements[0].Width, Is.GreaterThanOrEqualTo(12), "the name is still a name rather than kthrea");
    Assert.That(
      placements[count - 1].X + placements[count - 1].Width,
      Is.LessThanOrEqualTo(159),
      "and nothing hangs off the edge"
    );
  }

  /// <summary>
  /// The columns that are drawn keep their floor at every width, and the table never runs off the
  /// right-hand edge — the two things the reservation exists to guarantee.
  /// </summary>
  [Test]
  public void AWideSetStaysInsideTheScreenAtEveryWidth() {
    var columns = new ColumnLayout([.. Settings.UserSettings.Presets["forensic"]]);
    // One buffer for the whole sweep: a stackalloc inside the loop would grow the frame by three
    // hundred and sixty of them.
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[64];
    for (var width = 40; width <= 400; ++width) {
      var count = columns.Place(width, placements);
      Assert.That(count, Is.GreaterThan(0), $"nothing was placed at {width}");
      Assert.That(
        placements[count - 1].X + placements[count - 1].Width,
        Is.LessThanOrEqualTo(width),
        $"the table hangs off the edge at {width}"
      );

      for (var i = 0; i < count; ++i)
        Assert.That(
          placements[i].Width,
          Is.GreaterThanOrEqualTo(Math.Min(6, columns.WidthAt(placements[i].Index))),
          $"a column vanished at {width}"
        );
    }
  }

  [Test]
  public void AHiddenColumnIsNeitherDrawnNorSorted() {
    var columns = Layout();
    columns.SetVisible(1, false);

    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[8];
    var count = columns.Place(120, placements);
    for (var i = 0; i < count; ++i)
      Assert.That(placements[i].Field, Is.Not.EqualTo(ProcessField.UserName));

    Assert.That(columns.Sortable, Does.Not.Contain(ProcessField.UserName));
  }

}

/// <summary>The bindings, the file that overrides them, and what the help page reads back (PRD §57.3).</summary>
[TestFixture]
public sealed class KeyBindingTests {

  [Test]
  public void EveryActionInTheCatalogueHasAKeyAndASentence() {
    var bindings = KeyBindings.Default;
    foreach (var entry in KeyBindings.Catalogue)
      Assert.Multiple(() => {
        Assert.That(entry.DefaultKeys, Is.Not.Empty, $"{entry.Name} has no key");
        Assert.That(entry.Description, Is.Not.Empty, $"{entry.Name} has no description");
        Assert.That(bindings.KeysFor(entry.Action), Is.Not.EqualTo("unbound"), $"{entry.Name} resolved to nothing");
      });
  }

  [Test]
  public void NoTwoActionsClaimTheSameKey() {
    // Two actions on one key is a key that does the wrong one of them, and which one depends on the
    // order of a table nobody reads.
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var entry in KeyBindings.Catalogue)
      foreach (var key in entry.DefaultKeys)
        Assert.That(seen.TryAdd(key, entry.Name), Is.True, $"{key} is claimed twice, the second time by {entry.Name}");
  }

  [Test]
  public void TheDefaultsResolveTheKeysTheySay() {
    var bindings = KeyBindings.Default;
    Assert.Multiple(() => {
      Assert.That(bindings.Resolve(new('k', default, false, false, false)), Is.EqualTo(TerminalAction.Terminate));
      Assert.That(bindings.Resolve(new('K', default, true, false, false)), Is.EqualTo(TerminalAction.TerminateTree));
      Assert.That(bindings.Resolve(new('\0', ConsoleKey.F5, false, false, false)), Is.EqualTo(TerminalAction.ToggleTree));
      Assert.That(bindings.Resolve(new(' ', ConsoleKey.Spacebar, false, false, false)), Is.EqualTo(TerminalAction.MarkToggle));
      Assert.That(bindings.Resolve(new('\u0001', ConsoleKey.A, false, false, true)), Is.EqualTo(TerminalAction.MarkAll));
    });
  }

  [Test]
  public void RebindingAnActionTakesItsOldKeyAway() {
    var bindings = KeyBindings.Parse("quit = Q\n# a comment\nhelp = F1, h\n");
    Assert.Multiple(() => {
      Assert.That(bindings.Resolve(new('Q', default, true, false, false)), Is.EqualTo(TerminalAction.Quit));
      Assert.That(bindings.Resolve(new('q', default, false, false, false)), Is.EqualTo(TerminalAction.None), "the default is gone, not merely shadowed");
      Assert.That(bindings.Resolve(new('h', default, false, false, false)), Is.EqualTo(TerminalAction.Help), "and h is no longer the handles page");
      Assert.That(bindings.Errors, Is.Empty);
    });
  }

  [Test]
  public void AMistakeInTheFileIsReportedRatherThanSwallowed() {
    var bindings = KeyBindings.Parse("qiut = Q\nquit\nquit = wombat\n");
    Assert.That(bindings.Errors, Has.Count.EqualTo(3), "the misspelt action, the line with no '=', and the key that is not one");
    Assert.That(
      bindings.Resolve(new('q', default, false, false, false)),
      Is.EqualTo(TerminalAction.Quit),
      "a typo may not leave an action unreachable"
    );
  }

  [Test]
  public void NamedKeysAreSpeltHoweverPeopleSpellThemAndCharactersAreNot() {
    var bindings = KeyBindings.Parse("page-down = PageDown\nmark-none = u\n");
    Assert.Multiple(() => {
      Assert.That(bindings.Resolve(new('\0', ConsoleKey.PageDown, false, false, false)), Is.EqualTo(TerminalAction.PageDown));
      Assert.That(bindings.Resolve(new('u', default, false, false, false)), Is.EqualTo(TerminalAction.MarkNone));
      Assert.That(bindings.Resolve(new('U', default, true, false, false)), Is.EqualTo(TerminalAction.None), "the default U went with the rebinding");
    });
  }

  [Test]
  public void KeysAreShownTheWayAKeyboardIsLabelled()
    => Assert.That(KeyBindings.Default.KeysFor(TerminalAction.ToggleTree), Is.EqualTo("F5 t"));

}

/// <summary>The mouse protocols, decoded without a mouse (PRD §57.5).</summary>
[TestFixture]
public sealed class MouseInputTests {

  [Test]
  public void ASgrPressAndItsReleaseAreTold() {
    Assert.That(MouseInput.TryDecode("\u001b[<0;34;9M", out var press), Is.True);
    Assert.That(press, Is.EqualTo(new MouseEvent(MouseButton.Left, 33, 8, true, false, false, false, false)));

    Assert.That(MouseInput.TryDecode("\u001b[<0;34;9m", out var release), Is.True);
    Assert.That(release.Pressed, Is.False);
  }

  [Test]
  public void TheWheelIsTwoButtonsAndAlwaysAPress() {
    Assert.That(MouseInput.TryDecode("\u001b[<64;1;1M", out var up), Is.True);
    Assert.That(MouseInput.TryDecode("\u001b[<65;1;1M", out var down), Is.True);
    Assert.Multiple(() => {
      Assert.That(up.Button, Is.EqualTo(MouseButton.WheelUp));
      Assert.That(down.Button, Is.EqualTo(MouseButton.WheelDown));
      Assert.That(up.Pressed, Is.True);
    });
  }

  [Test]
  public void ModifiersAndDragsSurvive() {
    // 4 shift + 16 control + 32 motion, on the left button.
    Assert.That(MouseInput.TryDecode("\u001b[<52;5;7M", out var report), Is.True);
    Assert.Multiple(() => {
      Assert.That(report.Shift, Is.True);
      Assert.That(report.Control, Is.True);
      Assert.That(report.Motion, Is.True);
      Assert.That(report.X, Is.EqualTo(4));
      Assert.That(report.Y, Is.EqualTo(6));
    });
  }

  [Test]
  public void TheOldFormIsReadToo() {
    // ESC [ M, then the button, the column and the row, each offset by 32.
    Assert.That(MouseInput.TryDecode("\u001b[M !\"", out var press), Is.True);
    Assert.Multiple(() => {
      Assert.That(press.Button, Is.EqualTo(MouseButton.Left));
      Assert.That(press.X, Is.EqualTo(0));
      Assert.That(press.Y, Is.EqualTo(1));
    });
  }

  [Test]
  public void AnythingElseIsNotAMouseReport() {
    Assert.Multiple(() => {
      Assert.That(MouseInput.TryDecode("\u001b[A", out _), Is.False, "an arrow key");
      Assert.That(MouseInput.TryDecode("\u001b[<0;1M", out _), Is.False, "a truncated one");
      Assert.That(MouseInput.TryDecode("hello", out _), Is.False);
      Assert.That(MouseInput.IsPrefix("\u001b[<0;1"), Is.True, "still growing");
      Assert.That(MouseInput.IsPrefix("\u001b[1;5D"), Is.False, "a control sequence that is not one");
    });
  }

}

/// <summary>Copying out of a terminal, which is OSC 52 or nothing (PRD §11).</summary>
[TestFixture]
public sealed class ClipboardTests {

  private const string _Prefix = "\u001b]52;c;";

  [Test]
  public void TheSequenceIsTheOneTerminalsRead() {
    Assert.That(Clipboard.TryEncode("pid 42", out var sequence), Is.True);
    Assert.That(sequence, Does.StartWith(_Prefix));
    Assert.That(sequence, Does.EndWith("\u0007"));

    var payload = sequence[_Prefix.Length..^1];
    Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(payload)), Is.EqualTo("pid 42"));
  }

  [Test]
  public void MoreThanATerminalWillTakeIsRefusedRatherThanTruncated() {
    Assert.That(Clipboard.TryEncode(new string('x', Clipboard.SizeLimit + 1), out _), Is.False);
    Assert.That(Clipboard.TryEncode(null, out _), Is.False);
  }

  [Test]
  public void AnOfferIsWrittenToTheTerminalItself() {
    var writer = new StringWriter();
    Assert.That(Clipboard.TryWrite(writer, "abc"), Is.True);
    Assert.That(writer.ToString(), Does.Contain("52;c;"));
  }

}
