using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Terminal;
using static Hawkynt.ProcessManager.Tests.TerminalFixture;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The parts that are easy to look at and hard to assert: a set being applied rather than merely
/// listed, a column that really moves, the pages the process keys open, and what the table does when
/// it is empty or too small to draw in.
/// </summary>
[TestFixture]
public sealed class TerminalEdgeTests {

  [Test]
  public void ChoosingAColumnSetAppliesIt() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key('c'));
      ui.HandleKey(Key(ConsoleKey.Enter));                 // the cursor opens on the first set

      ProcessField[]? expected = null;
      foreach (var preset in UserSettings.Presets) {
        expected = preset.Value;
        break;
      }

      Assert.That(expected, Is.Not.Null);
      Assert.That(ui.Columns.Count, Is.EqualTo(expected!.Length));
      Assert.That(ui.Columns.FieldAt(0), Is.EqualTo(expected[0]));
      Assert.That(Frame(ui), Does.Contain("columns are the"), "and it says which set it is now");
    }
  }

  [Test]
  public void AColumnReallyMovesAndReallyResizes() {
    var (ui, probe) = Machine();
    using (probe) {
      var second = ui.Columns.FieldAt(1);
      ui.HandleKey(Key('}'));
      Assert.That(ui.Columns.FieldAt(0), Is.EqualTo(second), "the first two swapped places");

      var width = ui.Columns.WidthAt(ui.Columns.Current);
      ui.HandleKey(Key('.'));
      ui.HandleKey(Key('.'));
      Assert.That(ui.Columns.WidthAt(ui.Columns.Current), Is.EqualTo(width + 2));

      ui.HandleKey(Key(','));
      Assert.That(ui.Columns.WidthAt(ui.Columns.Current), Is.EqualTo(width + 1));
    }
  }

  [TestCase('T', "Threads")]
  [TestCase('m', "Modules")]
  [TestCase('h', "Handles")]
  [TestCase('n', "Network")]
  public void EachProcessKeyOpensItsOwnPage(char key, string page) {
    var (ui, probe) = Machine();
    using (probe) {
      ui.HandleKey(Key(key));

      var frame = Frame(ui);
      Assert.That(frame, Does.Contain(page));
      Assert.That(frame, Does.Contain("Esc back"), "and it is the detail screen, not the table");
    }
  }

  [Test]
  public void DraggingTheDividerResizesTheLowerPane() {
    var (ui, probe) = Machine(120, 30);
    using (probe) {
      ui.HandleKey(Key(ConsoleKey.Tab));
      var before = Divider(ui);

      // A press on the divider, then motion four rows up: by the time the pointer has moved, the
      // divider is no longer under it, which is what makes this a drag and not two clicks.
      ui.HandleMouse(Click(10, before));
      ui.HandleMouse(new(MouseButton.Left, 10, before - 4, true, true, false, false, false));
      Assert.That(Divider(ui), Is.EqualTo(before - 4), "the pane grew to where the pointer went");

      ui.HandleMouse(new(MouseButton.Left, 10, before - 8, false, false, false, false, false));
      ui.HandleMouse(new(MouseButton.Left, 10, before - 8, true, true, false, false, false));
      Assert.That(Divider(ui), Is.EqualTo(before - 4), "and a drag that was let go of does not follow the pointer");
    }

    static int Divider(TerminalUi ui) {
      var lines = Lines(ui);
      for (var i = lines.Length - 1; i >= 0; --i)
        if (lines[i].StartsWith("──", StringComparison.Ordinal))
          return i;

      Assert.Fail("the lower pane is not on screen");
      return -1;
    }
  }

  [Test]
  public void ATableWithNothingInItAnswersEveryKeyWithoutThrowing() {
    var (ui, probe) = Machine();
    using (probe) {
      ui.View.TextFilter = "there-is-no-such-process";
      ui.Refresh();
      Assert.That(ui.View.RowCount, Is.Zero);
      Assert.That(Frame(ui), Does.Contain("nothing matches"));

      // Every key that acts on "the selected row" when there is no selected row. Each of them is
      // supposed to say so; none of them may throw.
      Assert.That(() => {
        foreach (var character in "yYaAvU okKReSsxTmhngc0.,[]{}")
          ui.HandleKey(Key(character));

        ui.HandleKey(Key(ConsoleKey.Enter));
        ui.HandleKey(Key(ConsoleKey.Escape));
        ui.HandleKey(Key(ConsoleKey.LeftArrow));
        ui.HandleKey(Key(ConsoleKey.RightArrow));
        ui.HandleKey(Key(ConsoleKey.PageDown));
        ui.Refresh();
      }, Throws.Nothing);
    }
  }

  [TestCase(1, 1)]
  [TestCase(12, 4)]
  [TestCase(40, 10)]
  public void ATerminalTooSmallToDrawInIsStillNotACrash(int width, int height) {
    var (ui, probe) = Machine();
    using (probe) {
      Assert.That(() => {
        ui.Resize(width, height);
        ui.Refresh();
        ui.HandleKey(Key('?'));                            // an overlay wider than the screen
        ui.Refresh();
        ui.HandleKey(Key(ConsoleKey.Escape));
        ui.HandleKey(Key(ConsoleKey.Tab));                 // and a pane taller than what is left
        ui.Refresh();
        ui.HandleMouse(Click(0, 0));
        ui.Update();
      }, Throws.Nothing);
    }
  }

}

/// <summary>The tie-breaking columns, which are the engine's half of §11's multi-column sort.</summary>
[TestFixture]
public sealed class SecondarySortTests {

  [Test]
  public void TheSecondColumnDecidesTheOrderWhereTheFirstTies() {
    using var probe = Probe();
    using var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();

    var view = new ProcessView { SortColumn = ProcessField.UserName, SortDescending = false };
    view.AddSortKey(ProcessField.PrivateBytes, descending: true);
    view.Rebuild(sampler.Current, sampler.Delta);

    var processes = sampler.Current.Processes;
    var previous = ulong.MaxValue;
    var ties = 0;
    foreach (var row in view.Rows) {
      ref readonly var process = ref processes[row.Index];
      // Every alice process ties on the user name, so the second key is the only thing ordering them.
      if (process.UserName != "alice")
        continue;

      var value = process.PrivateBytes.GetValueOrDefault();
      Assert.That(value, Is.LessThanOrEqualTo(previous), "the tied rows came out largest first");
      previous = value;
      ++ties;
    }

    Assert.That(ties, Is.GreaterThan(1), "there were ties to break");
  }

  [Test]
  public void AddingTheSortColumnItselfIsIgnoredAndAKeyIsNeverListedTwice() {
    var view = new ProcessView { SortColumn = ProcessField.Pid };
    view.AddSortKey(ProcessField.Pid, false);
    Assert.That(view.SecondarySort, Is.Empty, "the primary column cannot also break its own ties");

    view.AddSortKey(ProcessField.UserName, false);
    view.AddSortKey(ProcessField.CpuPercent, true);
    view.AddSortKey(ProcessField.UserName, true);
    Assert.That(view.SecondarySort, Has.Count.EqualTo(2));
    Assert.That(view.SecondarySort[^1].Field, Is.EqualTo(ProcessField.UserName), "asking again moves it, not duplicates it");
  }

}
