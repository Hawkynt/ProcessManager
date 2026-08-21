using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The window's columns: order, width, the cursor, and the arithmetic behind a header drag
/// (PRD §11).
/// </summary>
/// <remarks>
/// Testable without a display for the reason the binder is: none of this needs a pixel drawn. What
/// it is worth testing is the arithmetic — which column a press landed on, and where a dropped one
/// goes — because that is the part that gets a table wrong by exactly one column and looks fine in
/// a screenshot taken at the wrong moment.
/// </remarks>
[TestFixture]
public sealed class DesktopColumnTests {

  private static DesktopColumns Columns() => new([
    ProcessField.Name,
    ProcessField.Pid,
    ProcessField.CpuPercent,
    ProcessField.PrivateBytes,
  ]);

  [Test]
  public void ItOpensWithTheRegistrysWidths() {
    var columns = Columns();
    Assert.That(columns.Count, Is.EqualTo(4));
    for (var i = 0; i < columns.Count; ++i)
      Assert.That(columns.WidthAt(i), Is.EqualTo(FieldRegistry.Get(columns.FieldAt(i)).DesktopWidth));

    Assert.That(columns.Customised, Is.False, "nothing has been touched, so nothing is worth writing down");
  }

  [Test]
  public void MovingAColumnTakesTheCursorWithIt() {
    var columns = Columns();
    columns.SetCurrent(2);

    Assert.That(columns.Reorder(-1), Is.True);
    Assert.That(columns.Current, Is.EqualTo(1));
    Assert.That(columns.FieldAt(1), Is.EqualTo(ProcessField.CpuPercent));
    Assert.That(columns.FieldAt(2), Is.EqualTo(ProcessField.Pid));
    Assert.That(columns.Customised, Is.True);
  }

  [Test]
  public void AColumnCannotBeMovedPastEitherEnd() {
    var columns = Columns();
    columns.SetCurrent(0);
    Assert.That(columns.Reorder(-1), Is.False);
    Assert.That(columns.FieldAt(0), Is.EqualTo(ProcessField.Name));

    columns.SetCurrent(columns.Count - 1);
    Assert.That(columns.Reorder(1), Is.False);
  }

  /// <summary>Dropping a column somewhere is not the same as swapping it with its neighbour.</summary>
  [Test]
  public void ADraggedColumnLandsWhereItWasDropped() {
    var columns = Columns();

    Assert.That(columns.MoveTo(3, 0), Is.True);
    Assert.That(columns.Fields, Is.EqualTo(new[] {
      ProcessField.PrivateBytes, ProcessField.Name, ProcessField.Pid, ProcessField.CpuPercent,
    }));

    Assert.That(columns.Current, Is.EqualTo(0));
    Assert.That(columns.MoveTo(0, 0), Is.False, "dropping a column where it already is changes nothing");
    Assert.That(columns.MoveTo(9, 0), Is.False);
  }

  [Test]
  public void AWidthIsClampedRatherThanAllowedToVanish() {
    var columns = Columns();
    columns.SetWidth(0, -400);
    Assert.That(columns.WidthAt(0), Is.EqualTo(DesktopColumns.MinimumWidth));

    columns.SetWidth(0, 99_999);
    Assert.That(columns.WidthAt(0), Is.EqualTo(DesktopColumns.MaximumWidth));
  }

  [Test]
  public void ResizingMovesTheCurrentColumnOnly() {
    var columns = Columns();
    columns.SetCurrent(1);
    var others = (columns.WidthAt(0), columns.WidthAt(2), columns.WidthAt(3));
    var before = columns.WidthAt(1);

    columns.ResizeCurrent(24);
    Assert.That(columns.WidthAt(1), Is.EqualTo(before + 24));
    Assert.That((columns.WidthAt(0), columns.WidthAt(2), columns.WidthAt(3)), Is.EqualTo(others));
  }

  /// <summary>
  /// Ticking one more column in the chooser must not undo the widths somebody spent a minute
  /// setting, which is what rebuilding from the registry every time would do.
  /// </summary>
  [Test]
  public void ChangingTheSetKeepsTheWidthsOfTheColumnsThatSurvive() {
    var columns = Columns();
    columns.SetWidth(1, 200);

    columns.Apply([ProcessField.Name, ProcessField.Pid, ProcessField.UserName]);

    Assert.That(columns.WidthAt(1), Is.EqualTo(200));
    Assert.That(columns.WidthAt(2), Is.EqualTo(FieldRegistry.Get(ProcessField.UserName).DesktopWidth));
  }

  [Test]
  public void ResettingPutsBackTheOrderAndTheWidths() {
    var columns = Columns();
    columns.SetCurrent(3);
    columns.Reorder(-1);
    columns.SetWidth(0, 300);

    columns.Reset([ProcessField.Name, ProcessField.Pid]);

    Assert.That(columns.Fields, Is.EqualTo(new[] { ProcessField.Name, ProcessField.Pid }));
    Assert.That(columns.WidthAt(0), Is.EqualTo(FieldRegistry.Get(ProcessField.Name).DesktopWidth));
    Assert.That(columns.Current, Is.Zero);
    Assert.That(columns.Customised, Is.False);
  }

  /// <summary>
  /// A drawn history has no text, so fitting it to its own empty string would collapse it to
  /// nothing — the plot would vanish and the column would look hidden rather than narrow.
  /// </summary>
  [Test]
  public void FittingLeavesADrawnHistoryAlone() {
    var columns = new DesktopColumns([ProcessField.Name, ProcessField.CpuHistory]);
    var before = columns.WidthAt(1);

    columns.AutoSize(1, 4);
    Assert.That(columns.WidthAt(1), Is.EqualTo(before));

    columns.AutoSize(0, 140);
    Assert.That(columns.WidthAt(0), Is.EqualTo(140));
  }

  [Test]
  public void AHitTestFindsTheColumnUnderAPress() {
    var columns = Columns();
    columns.SetWidth(0, 100);
    columns.SetWidth(1, 50);
    columns.SetWidth(2, 60);

    Assert.That(columns.HitTest(0), Is.Zero);
    Assert.That(columns.HitTest(99), Is.Zero);
    Assert.That(columns.HitTest(100), Is.EqualTo(1));
    Assert.That(columns.HitTest(149), Is.EqualTo(1));
    Assert.That(columns.HitTest(150), Is.EqualTo(2));
    // Past the last boundary is still the last column: the table stretches its final column to fill
    // what is left, so out there is inside it.
    Assert.That(columns.HitTest(100_000), Is.EqualTo(columns.Count - 1));
    Assert.That(columns.HitTest(-1), Is.EqualTo(-1));
    Assert.That(columns.LeftOf(2), Is.EqualTo(150));
  }

  /// <summary>
  /// Near a boundary a press grabs the boundary; in the middle it grabs the column. The grip is
  /// wider than a pixel because a person aiming at a one-pixel line misses, and a miss here starts a
  /// column move instead of a resize.
  /// </summary>
  [Test]
  public void TheEdgeOfAColumnIsGrabbableFromEitherSide() {
    var columns = Columns();
    columns.SetWidth(0, 100);
    columns.SetWidth(1, 50);

    Assert.That(columns.EdgeAt(100), Is.Zero);
    Assert.That(columns.EdgeAt(97), Is.Zero);
    Assert.That(columns.EdgeAt(103), Is.Zero);
    Assert.That(columns.EdgeAt(150), Is.EqualTo(1));
    Assert.That(columns.EdgeAt(60), Is.EqualTo(-1), "the middle of a column is not an edge");
  }

  /// <summary>Only the widths somebody actually chose, so a file does not pin every width forever.</summary>
  [Test]
  public void OnlyTheChosenWidthsAreWorthWritingDown() {
    var columns = Columns();
    Assert.That(columns.ChosenWidths, Is.Empty);

    columns.SetWidth(2, 220);
    Assert.That(columns.ChosenWidths, Has.Count.EqualTo(1));
    Assert.That(columns.ChosenWidths[0].Key, Is.EqualTo(ProcessField.CpuPercent));
    Assert.That(columns.ChosenWidths[0].Value, Is.EqualTo(220));
  }

  [Test]
  public void AWidthSurvivesTheSettingsFile() {
    var written = new UserSettings {
      DesktopColumnWidths = [new(ProcessField.Name, 240), new(ProcessField.Pid, 48)],
    }.Write();

    var parsed = UserSettings.Parse(written);
    Assert.That(parsed.DesktopColumnWidths, Has.Count.EqualTo(2));
    Assert.That(parsed.DesktopColumnWidths[0], Is.EqualTo(new KeyValuePair<ProcessField, int>(ProcessField.Name, 240)));
    Assert.That(parsed.DesktopColumnWidths[1], Is.EqualTo(new KeyValuePair<ProcessField, int>(ProcessField.Pid, 48)));
  }

  /// <summary>
  /// A pair this build cannot make sense of is skipped rather than failing the line, the same way an
  /// unknown field key is: a file written by a newer version must still open an older build.
  /// </summary>
  [Test]
  public void ANonsenseWidthIsSkippedRatherThanBreakingTheFile() {
    var parsed = UserSettings.Parse("columns.desktop.widths=name:240,nosuchfield:10,pid:notanumber,cpu:-4\ninterval=2");

    Assert.That(parsed.DesktopColumnWidths, Has.Count.EqualTo(1));
    Assert.That(parsed.DesktopColumnWidths[0].Key, Is.EqualTo(ProcessField.Name));
    Assert.That(parsed.IntervalSeconds, Is.EqualTo(2), "the rest of the file still parsed");
  }

  #region the pinned run (PRD §11)

  /// <summary>
  /// One column pinned to start with, the same as the terminal: a table scrolled sideways with no
  /// name column left on it is a table of numbers belonging to nobody.
  /// </summary>
  [Test]
  public void TheFirstColumnIsPinnedToBeginWith() {
    var columns = Columns();
    Assert.That(columns.Frozen, Is.EqualTo(1));
    Assert.That(columns.IsFrozen(0), Is.True);
    Assert.That(columns.IsFrozen(1), Is.False);
  }

  /// <summary>The same arithmetic the terminal's <c>#</c> does: pin up to the cursor, or let go.</summary>
  [Test]
  public void PinningTakesEverythingUpToTheCursorAndPressingAgainLetsGo() {
    var columns = Columns();
    columns.SetCurrent(2);

    columns.ToggleFreeze();
    Assert.That(columns.Frozen, Is.EqualTo(3));
    Assert.That(columns.IsFrozen(2), Is.True);
    Assert.That(columns.IsFrozen(3), Is.False);
    Assert.That(columns.Customised, Is.True);

    columns.ToggleFreeze();
    Assert.That(columns.Frozen, Is.Zero, "the same key on the same column releases the lot");
  }

  /// <summary>
  /// Ticking columns off must not leave three pinned out of two, which the toolkit reads as "pin
  /// everything" and then refuses to scroll at all.
  /// </summary>
  [Test]
  public void TheNumberPinnedCannotOutrunTheColumns() {
    var columns = Columns();
    columns.SetCurrent(3);
    columns.ToggleFreeze();
    Assert.That(columns.Frozen, Is.EqualTo(4));

    columns.Apply([ProcessField.Name, ProcessField.Pid]);
    Assert.That(columns.Frozen, Is.EqualTo(2));

    columns.SetFrozen(99);
    Assert.That(columns.Frozen, Is.EqualTo(2));
    columns.SetFrozen(-1);
    Assert.That(columns.Frozen, Is.Zero);
  }

  [Test]
  public void ResettingTheColumnsPinsTheFirstOneAgain() {
    var columns = Columns();
    columns.SetFrozen(0);
    columns.Reset([ProcessField.Name, ProcessField.Pid]);

    Assert.That(columns.Frozen, Is.EqualTo(1));
    Assert.That(columns.Customised, Is.False);
  }

  /// <summary>
  /// Only when it is not the one column every table opens with: a line in everybody's settings file
  /// saying the first column is pinned is a line nobody reads.
  /// </summary>
  [Test]
  public void ThePinnedRunSurvivesTheSettingsFile() {
    var written = new UserSettings { PinnedDesktopColumns = 3, PinnedTerminalColumns = 2 }.Write();

    var parsed = UserSettings.Parse(written);
    Assert.That(parsed.PinnedDesktopColumns, Is.EqualTo(3));
    Assert.That(parsed.PinnedTerminalColumns, Is.EqualTo(2));

    Assert.That(new UserSettings().Write(), Does.Not.Contain("pinned"));
  }

  [Test]
  public void ANonsensePinnedCountLeavesTheSettingAlone() {
    var parsed = UserSettings.Parse("columns.desktop.pinned=-3\ncolumns.terminal.pinned=nine\ninterval=2");

    Assert.That(parsed.PinnedDesktopColumns, Is.EqualTo(1));
    Assert.That(parsed.PinnedTerminalColumns, Is.EqualTo(1));
    Assert.That(parsed.IntervalSeconds, Is.EqualTo(2), "the rest of the file still parsed");
  }

  #endregion

}
