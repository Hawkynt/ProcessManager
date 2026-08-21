using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Ending the selected process, the lower pane, and the named column sets (PRD §25.1, §10, §94, §99).
/// </summary>
/// <remarks>
/// <para>
/// Over the recorded machine rather than a stub, because every one of these acts on a selected row
/// and a window with no rows in it would pass each of them by doing nothing.
/// </para>
/// <para>
/// The confirmation is answered through <see cref="MainWindow.Confirm"/>. Before that seam existed
/// every prompt went straight to a dialog that throws without a display, so the sentence somebody is
/// answering — the whole subject of §90 — was the least tested text in the program, and "no" was a
/// path nothing had ever taken.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowActionTests {

  /// <summary>What the window asked the action layer to do, and nothing else.</summary>
  private sealed class RecordingActions : IProcessActions {

    public List<string> Done { get; } = [];

    public ActionResult Result { get; set; } = ActionResult.Ok;

    public ActionResult EndTask(ProcessKey key) {
      this.Done.Add($"endtask {key.Pid}");
      return this.Result;
    }

    public ActionResult Terminate(ProcessKey key) {
      this.Done.Add($"terminate {key.Pid}");
      return this.Result;
    }

    public ActionResult TerminateTree(IReadOnlyList<ProcessKey> order) {
      this.Done.Add($"tree {order.Count}");
      return this.Result;
    }

    public ActionResult Suspend(ProcessKey key) {
      this.Done.Add($"suspend {key.Pid}");
      return this.Result;
    }

    public ActionResult Resume(ProcessKey key) {
      this.Done.Add($"resume {key.Pid}");
      return this.Result;
    }

    public ActionResult SetPriority(ProcessKey key, int priority) => ActionResult.Ok;
    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Ok;
    public ActionResult SendSignal(ProcessKey key, int signal) => ActionResult.Ok;
  }

  /// <summary>The window over the recorded machine, with the first row selected.</summary>
  private static (MainWindow Window, LinuxProbe Probe, RecordingActions Actions, List<string> Asked, List<string> Said)
    Machine(bool answer = true, UserSettings? settings = null) {
    var probe = TerminalFixture.Probe();
    var actions = new RecordingActions();
    var window = new MainWindow(new Sampler(probe), probe, actions);
    var asked = new List<string>();
    var said = new List<string>();
    window.Confirm = question => { asked.Add(question); return answer; };
    window.Announce = said.Add;
    window.ApplySettings(settings ?? new() { DesktopColumns = [ProcessField.Name, ProcessField.Pid] }, _ => true);
    window.Start();
    window.SelectFirstRow();
    return (window, probe, actions, asked, said);
  }

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var deeper in Descendants(child))
        yield return deeper;
    }
  }

  /// <summary>
  /// Finds a menu item by its label and works it, which is the only way to reach the handler behind
  /// it — and the way that also proves the item is on a menu at all.
  /// </summary>
  private static ToolStripMenuItem MenuItem(MainWindow window, string label) {
    foreach (var control in Descendants(window)) {
      if (control is MenuStrip menu && Find(menu.Items, label) is { } onBar)
        return onBar;

      if (control.ContextMenuStrip is { } context && Find(context.Items, label) is { } onContext)
        return onContext;
    }

    Assert.Fail($"no menu item is labelled '{label}'");
    return null!;

    static ToolStripMenuItem? Find(ToolStripItemCollection items, string label) {
      foreach (var item in items)
        if (item is ToolStripMenuItem entry) {
          if (entry.Text == label)
            return entry;

          if (Find(entry.DropDownItems, label) is { } deeper)
            return deeper;
        }

      return null;
    }
  }

  #region ending the selected process (PRD §25.1, §90)

  /// <summary>
  /// The prompt names the action, the process, its pid and what is lost. "Are you sure?" is not a
  /// question anybody can answer, and a pid on its own is not a name (PRD §90).
  /// </summary>
  [Test]
  public void EndingTheSelectedProcessNamesItAndSaysWhatIsLost() {
    var (window, probe, actions, asked, _) = Machine();
    using (probe) {
      MenuItem(window, "End process").PerformClick();

      Assert.That(asked, Has.Count.EqualTo(1), "asked exactly once");
      Assert.That(asked[0], Does.StartWith("End "));
      Assert.That(asked[0], Does.Contain("PID 1"), "the row the window had selected");
      Assert.That(asked[0], Does.Contain("systemd"), "named, not only numbered");
      Assert.That(asked[0], Does.Contain("unsaved work"), "and what it costs");
      Assert.That(actions.Done, Is.EqualTo(new[] { "terminate 1" }));
    }
  }

  /// <summary>
  /// Answering no ends nothing. The path that had never been taken: every prompt used to go straight
  /// to a dialog no test could answer, so the refusal branch was written and never run.
  /// </summary>
  [Test]
  public void SayingNoEndsNothing() {
    var (window, probe, actions, asked, _) = Machine(answer: false);
    using (probe) {
      MenuItem(window, "End process").PerformClick();

      Assert.That(asked, Has.Count.EqualTo(1));
      Assert.That(actions.Done, Is.Empty);
    }
  }

  /// <summary>
  /// End task is the reversible one and is deliberately not confirmed: the program is asked to
  /// close, may put up its own "save your changes?" and may decline. Confirming it would put a
  /// dialog in front of a dialog (PRD §25.1).
  /// </summary>
  [Test]
  public void EndTaskAsksTheProgramAndNotTheUser() {
    var (window, probe, actions, asked, _) = Machine();
    using (probe) {
      MenuItem(window, "End task").PerformClick();

      Assert.That(asked, Is.Empty);
      Assert.That(actions.Done, Is.EqualTo(new[] { "endtask 1" }));
    }
  }

  /// <summary>
  /// Ending a tree counts what goes with it. "And everything under it" is the part somebody needs to
  /// weigh, and a plain confirmation hides it: a shell with a build under it and a shell on its own
  /// are the same row and very different requests (PRD §90).
  /// </summary>
  [Test]
  public void EndingATreeCountsWhatGoesWithIt() {
    var (window, probe, actions, asked, _) = Machine();
    using (probe) {
      MenuItem(window, "End process tree").PerformClick();

      Assert.That(asked, Has.Count.EqualTo(1));
      // The recorded machine's pid 1 has three processes under it.
      Assert.That(asked[0], Does.Match(@"and the \d+ processes running under it"));
      Assert.That(actions.Done, Has.Count.EqualTo(1));
      Assert.That(actions.Done[0], Does.StartWith("tree "));
    }
  }

  /// <summary>
  /// A failure is put in front of somebody rather than swallowed. A row that does not disappear and
  /// no message at all is indistinguishable from a window that has stopped working (PRD §88).
  /// </summary>
  [Test]
  public void AnActionThatFailedSaysSo() {
    var (window, probe, actions, _, said) = Machine();
    using (probe) {
      actions.Result = new(ActionOutcome.NotPermitted, "you are not allowed to end pid 1");
      MenuItem(window, "End process").PerformClick();

      Assert.That(said, Has.Count.EqualTo(1));
      Assert.That(said[0], Does.Contain("not allowed"));
    }
  }

  /// <summary>
  /// With nothing selected, nothing is asked and nothing happens — rather than a prompt about a row
  /// that is not there, or a pid of nought.
  /// </summary>
  [Test]
  public void WithNoRowSelectedNothingIsAskedAndNothingHappens() {
    using var probe = TerminalFixture.Probe();
    var actions = new RecordingActions();
    var window = new MainWindow(new Sampler(probe), probe, actions);
    var asked = new List<string>();
    window.Confirm = question => { asked.Add(question); return true; };
    window.ApplySettings(new() { DesktopColumns = [ProcessField.Name, ProcessField.Pid] }, _ => true);
    window.Start();

    MenuItem(window, "End process").PerformClick();

    Assert.That(asked, Is.Empty);
    Assert.That(actions.Done, Is.Empty);
  }

  #endregion

  #region the lower pane (PRD §10)

  /// <summary>
  /// The pane follows the selection. It is the whole point of it: a detail pane showing a process
  /// nobody has selected is a pane showing the wrong process.
  /// </summary>
  [Test]
  public void TheLowerPaneShowsTheSelectedProcess() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      Assert.That(window.LowerPaneVisible, Is.True, "it opens showing");
      Assert.That(window.DescribeForCapture(), Does.Contain("lower pane shown"), "and the capture can see it");
    }
  }

  /// <summary>
  /// Hiding it and showing it again leaves it as it was, and the toggle is the same state the menu
  /// item and the command-bar button both read.
  /// </summary>
  [Test]
  public void HidingThePaneAndShowingItAgainIsANoOp() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      var before = window.DescribeForCapture();

      window.LowerPaneVisible = false;
      Assert.That(window.LowerPaneVisible, Is.False);

      window.LowerPaneVisible = true;
      Assert.That(window.DescribeForCapture(), Is.EqualTo(before));
    }
  }

  /// <summary>
  /// Whether the pane was showing survives a save and a load, because it is a layout choice and not
  /// a mode: somebody who closed it wants it closed the next time too (PRD §67).
  /// </summary>
  [Test]
  public void WhetherThePaneShowsIsRemembered() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      window.LowerPaneVisible = false;
      var saved = window.DescribeSettings();
      Assert.That(saved.LowerPaneVisible, Is.False);

      var reopened = new MainWindow(new Sampler(probe), probe, null);
      reopened.ApplySettings(saved, _ => true);
      Assert.That(reopened.LowerPaneVisible, Is.False);
    }
  }

  /// <summary>
  /// Every tab of the pane fills for the selected process without throwing. The tabs read their
  /// cells out of a row's tag array by index, so a row shorter than the list has columns throws
  /// while painting — in front of somebody, long after a test that only selected the tab went green.
  /// </summary>
  [Test]
  public void EveryTabOfThePaneFillsForTheSelectedProcess() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      foreach (var control in Descendants(window)) {
        if (control is not TabControl tabs || tabs.TabPages.Count == 0)
          continue;

        for (var i = 0; i < tabs.TabPages.Count; ++i) {
          var page = tabs.TabPages[i];
          Assert.That(() => tabs.SelectedIndex = i, Throws.Nothing, page.Text);
          foreach (var inner in page.Controls)
            if (inner is TreeListView list)
              for (var row = 0; row < list.Nodes.Count; ++row)
                for (var column = 0; column < list.Columns.Count; ++column)
                  Assert.That(
                    list.Columns[column].TextSelector!(list.Nodes[row]),
                    Is.Not.Null,
                    $"{page.Text}: row {row}, column '{list.Columns[column].Text}'"
                  );
        }

        return;
      }

      Assert.Fail("the window has no tabbed pane");
    }
  }

  #endregion

  #region named column sets (PRD §11, §58, §94)

  /// <summary>
  /// The sets §94 names are on the window's menu. They were parsed, saved and carried through
  /// untouched, and reachable from the terminal alone — which is the front-end disagreement §58
  /// exists to stop.
  /// </summary>
  [Test]
  public void EveryPresetIsOnTheMenu() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      var offered = MenuItem(window, "Column sets");
      var labels = new List<string>();
      foreach (var item in offered.DropDownItems)
        if (item is ToolStripMenuItem entry)
          labels.Add(entry.Text);

      Assert.That(labels, Is.EquivalentTo(UserSettings.Presets.Keys));
    }
  }

  /// <summary>Choosing one puts its columns in the table, in its order.</summary>
  [Test]
  public void ChoosingASetShowsItsColumns() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      MenuItem(window, "memory").PerformClick();

      Assert.That(window.ShownColumns.Fields, Is.EqualTo(UserSettings.Presets["memory"]));
    }
  }

  /// <summary>
  /// A set somebody wrote in the settings file is offered beside the built-in ones, and shadows a
  /// preset of the same name — which is what makes a preset improvable rather than a thing to be
  /// worked around.
  /// </summary>
  [Test]
  public void ASavedSetIsOfferedAndShadowsAPresetOfTheSameName() {
    var mine = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
      ["mine"] = [ProcessField.Name, ProcessField.ThreadCount],
      ["memory"] = [ProcessField.Name, ProcessField.Swap],
    };

    var (window, probe, _, _, _) = Machine(settings: new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid],
      ColumnSets = mine,
    });

    using (probe) {
      Assert.That(window.ColumnSetNames, Does.Contain("mine"));

      MenuItem(window, "mine").PerformClick();
      Assert.That(window.ShownColumns.Fields, Is.EqualTo(mine["mine"]));

      MenuItem(window, "memory").PerformClick();
      Assert.That(window.ShownColumns.Fields, Is.EqualTo(mine["memory"]), "the file's set, not the preset");
    }
  }

  /// <summary>
  /// A pinned run is kept and clamped rather than dropped. Somebody who pinned the name column
  /// pinned it because they want it there whatever else the table shows; a set with fewer columns
  /// than the pin counted into would otherwise hold the whole table still.
  /// </summary>
  [Test]
  public void ChoosingASetKeepsThePinnedRunAndClampsIt() {
    var (window, probe, _, _, _) = Machine(settings: new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid, ProcessField.CpuPercent],
      PinnedDesktopColumns = 2,
      ColumnSets = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
        ["one"] = [ProcessField.Name],
      },
    });

    using (probe) {
      Assert.That(window.ShowColumnSet("expert"), Is.True);
      Assert.That(window.ShownColumns.Pinned, Is.EqualTo(2), "wider set, same pin");

      Assert.That(window.ShowColumnSet("one"), Is.True);
      Assert.That(window.ShownColumns.Pinned, Is.LessThanOrEqualTo(1), "clamped, not left counting into nothing");
    }
  }

  /// <summary>A set nobody declared is refused by name rather than emptying the table.</summary>
  [Test]
  public void AnUnknownSetChangesNothing() {
    var (window, probe, _, _, _) = Machine();
    using (probe) {
      var before = window.ShownColumns.Fields;

      Assert.That(window.ShowColumnSet("no-such-set"), Is.False);
      Assert.That(window.ShownColumns.Fields, Is.EqualTo(before));
    }
  }

  #endregion

}
