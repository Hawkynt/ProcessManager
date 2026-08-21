using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Terminal;
using static Hawkynt.ProcessManager.Tests.TerminalFixture;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Grouping, in the terminal, against the recorded machine (PRD §83).
/// </summary>
/// <remarks>
/// Rendered rather than merely computed: the engine's half of this is covered by
/// <see cref="GroupingTests"/>, and what is left is the half a screenshot catches — that a heading
/// reaches the frame, that the cursor never lands on one, and that no key can act on one.
/// </remarks>
[TestFixture]
public sealed class TerminalGroupingTests {

  private static (TerminalUi Ui, LinuxProbe Probe) Grouped(ProcessGrouping grouping = ProcessGrouping.User) {
    var (ui, probe) = Machine();
    ui.View.Grouping = grouping;
    ui.Refresh();
    ui.HandleKey(Key(ConsoleKey.Home));
    ui.Refresh();
    return (ui, probe);
  }

  [Test]
  public void AHeadingIsDrawnAcrossTheLine() {
    var (ui, probe) = Grouped();
    using (probe) {
      var frame = Frame(ui);
      Assert.That(frame, Does.Contain("process"), "the heading counts its members");
      var headings = 0;
      foreach (var line in Lines(ui))
        if (line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("process", StringComparison.Ordinal))
          ++headings;

      Assert.That(headings, Is.EqualTo(ui.View.Groups.Count));
    }
  }

  /// <summary>
  /// The status line counts processes. A heading takes a row and is not one, and a grouped list that
  /// counted them would claim more processes than the machine is running.
  /// </summary>
  [Test]
  public void TheCountIsProcessesAndNotRows() {
    var (ui, probe) = Grouped();
    using (probe) {
      Assert.That(ui.View.RowCount, Is.GreaterThan(ui.View.MatchCount));
      Assert.That(Frame(ui), Does.Contain($"{ui.View.MatchCount} of {ui.View.TotalCount}"));
      Assert.That(Frame(ui), Does.Not.Contain($"{ui.View.RowCount} of {ui.View.TotalCount}"));
    }
  }

  /// <summary>
  /// The arrows step past a heading rather than onto it: a cursor parked on something no key can act
  /// on reads as a list that skipped a row.
  /// </summary>
  [Test]
  public void TheCursorNeverLandsOnAHeading() {
    var (ui, probe) = Grouped();
    using (probe) {
      for (var i = 0; i < ui.View.RowCount + 4; ++i) {
        Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, $"after {i} moves down");
        ui.HandleKey(Key(ConsoleKey.DownArrow));
      }

      for (var i = 0; i < ui.View.RowCount + 4; ++i) {
        Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, $"after {i} moves up");
        ui.HandleKey(Key(ConsoleKey.UpArrow));
      }

      ui.HandleKey(Key(ConsoleKey.End));
      Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, "nor at the end");
      ui.HandleKey(Key(ConsoleKey.Home));
      Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, "nor at the beginning");
    }
  }

  /// <summary>
  /// Clicking a heading folds it. It is the only thing there is to do to one — it cannot be selected,
  /// so it cannot be ended, suspended or restarted either.
  /// </summary>
  [Test]
  public void ClickingAHeadingFoldsItRatherThanSelectingIt() {
    var (ui, probe) = Grouped();
    using (probe) {
      var table = HeaderRow(ui) + 1;
      var before = ui.View.RowCount;

      Assert.That(ui.View.Rows[0].IsGroupHeader, Is.True, "the first row is a heading");
      ui.HandleMouse(Click(4, table));
      ui.Refresh();

      Assert.That(ui.View.RowCount, Is.LessThan(before), "its members went away");
      Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, "and the cursor is not on the heading");
      Assert.That(ui.View.Groups[0].Count, Is.GreaterThan(0), "and it still says how many it hides");

      // Folding takes the selected row away with it, so the cursor moves on. The selection is not
      // restored by opening the group again — the process it was on has been off screen since, and
      // silently moving the cursor back onto a row somebody has not looked at for a while is how a
      // key ends up acting on the wrong program (PRD §7.3).
      ui.HandleMouse(Click(4, table));
      ui.Refresh();
      Assert.That(ui.View.RowCount, Is.EqualTo(before));
      Assert.That(SelectedRow(ui).IsGroupHeader, Is.False);
    }
  }

  /// <summary>The arrows mean fold and open in a grouped list, as they do in the tree.</summary>
  [Test]
  public void TheArrowsFoldAndOpenTheGroupTheCursorIsIn() {
    var (ui, probe) = Grouped();
    using (probe) {
      var before = ui.View.RowCount;
      ui.HandleKey(Key(ConsoleKey.LeftArrow));
      Assert.That(ui.View.RowCount, Is.LessThan(before));

      ui.HandleKey(Key(ConsoleKey.RightArrow));
      Assert.That(ui.View.RowCount, Is.EqualTo(before));
    }
  }

  /// <summary>
  /// Ticking, copying and exporting all walk the rows. A heading is not a row any of them may take,
  /// and the failure would be an index of -1 into the snapshot rather than a wrong number.
  /// </summary>
  [Test]
  public void TickingAndCopyingSkipTheHeadings() {
    var (ui, probe) = Grouped();
    using (probe) {
      ui.HandleKey(new('\u0001', ConsoleKey.A, false, false, true));
      Assert.That(ui.MarkedCount, Is.EqualTo(ui.View.MatchCount), "the headings were not ticked");

      ui.HandleKey(Key('Y'));
      var lines = (ui.LastCopiedText ?? string.Empty).TrimEnd('\n').Split('\n');
      Assert.That(lines, Has.Length.EqualTo(ui.View.MatchCount + 1), "a header line and one line per process");
    }
  }

  [Test]
  public void TheGroupingMenuOffersEveryGroupingAndSwitchesToIt() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('G'));
      var frame = Frame(ui);
      foreach (var word in (string[])[
        "Nothing", "Parent tree", "User", "Session", "Service", "Executable", "Container", "Cgroup", "Package",
      ])
        Assert.That(frame, Does.Contain(word));

      // Down to "User" — the heading, then Nothing, Parent tree, User.
      ui.HandleKey(Key(ConsoleKey.DownArrow));
      ui.HandleKey(Key(ConsoleKey.DownArrow));
      ui.HandleKey(Key(ConsoleKey.Enter));

      Assert.That(ui.View.Grouping, Is.EqualTo(ProcessGrouping.User));
      Assert.That(ui.View.Groups, Is.Not.Empty);
    }
  }

  /// <summary>
  /// A heading is not something an action can be aimed at, because there is nothing for the request
  /// to name: the row carries no process, so the action declines the way it does with an empty list.
  /// </summary>
  [Test]
  public void NoActionCanBeAimedAtAHeading() {
    var (ui, probe) = Grouped();
    using (probe) {
      // The cursor cannot be put on a heading by any key, so the guard is checked at its source:
      // a heading row's key is none, and every action refuses that.
      for (var row = 0; row < ui.View.RowCount; ++row) {
        if (!ui.View.Rows[row].IsGroupHeader)
          continue;

        Assert.That(ui.View.Rows[row].Index, Is.LessThan(0));
      }

      Assert.That(ui.View.Rows[0].IsGroupHeader, Is.True);
      Assert.That(SelectedRow(ui).IsGroupHeader, Is.False, "the cursor moved past it instead");
    }
  }

  private static ViewRow SelectedRow(TerminalUi ui)
    => ui.View.RowCount == 0 ? default : ui.View.Rows[Math.Clamp(ui.SelectedRow, 0, ui.View.RowCount - 1)];

}
