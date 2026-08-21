using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;
using Hawkynt.NativeForms;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The actions actually being reachable from the window (PRD §25).
/// </summary>
/// <remarks>
/// This exists because of the failure the PRD names outright: priority and affinity were implemented
/// in the actions layer for months, appeared in no menu and no flag, and were therefore the same as
/// not existing. A test on the action alone would have passed the whole time. What is asserted here
/// is the menu, because the menu is the feature.
/// <para>
/// No display is needed: the controls are owner-drawn and their items are real before anything is
/// realised, which is what <see cref="MainWindowSettingsTests"/> relies on too.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ProcessMenuTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) => snapshot.PrepareProcesses(0);
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

  private static MainWindow Window() {
    var probe = new StubProbe();
    return new(new Sampler(probe), probe, null);
  }

  /// <summary>Every label in a menu, submenus included, so a nested item still counts as reachable.</summary>
  private static List<string> Labels(ToolStripItemCollection items) {
    var found = new List<string>();
    Collect(items);
    return found;

    void Collect(ToolStripItemCollection collection) {
      foreach (var item in collection) {
        if (item is not ToolStripMenuItem entry)
          continue;

        found.Add(entry.Text);
        Collect(entry.DropDownItems);
      }
    }
  }

  private static List<string> ContextMenuLabels(MainWindow window) {
    foreach (var control in Descendants(window))
      if (control.ContextMenuStrip is { } menu)
        return Labels(menu.Items);

    Assert.Fail("no control in the window carries a context menu");
    return [];
  }

  private static List<string> MenuBarLabels(MainWindow window) {
    foreach (var control in Descendants(window))
      if (control is MenuStrip menu)
        return Labels(menu.Items);

    Assert.Fail("the window has no menu bar");
    return [];
  }

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var deeper in Descendants(child))
        yield return deeper;
    }
  }

  /// <summary>
  /// The detail pane hangs one menu on the threads list and one on the modules list, so they are
  /// told apart by what is in them rather than by the order the pane happened to build them.
  /// </summary>
  private static List<string> PaneMenuContaining(string label) {
    var pane = new DetailPane(new StubProbe());
    foreach (var control in Descendants(pane.Control))
      if (control.ContextMenuStrip is { } menu) {
        var labels = Labels(menu.Items);
        if (labels.Contains(label))
          return labels;
      }

    Assert.Fail($"no list in the detail pane has a menu with '{label}' on it");
    return [];
  }

  [Test]
  public void TheModuleActionsAreOnTheModulesList() {
    var labels = PaneMenuContaining("Copy path");

    Assert.Multiple(() => {
      Assert.That(labels, Does.Contain("Copy path"));
      Assert.That(labels, Does.Contain("Open folder"));
      Assert.That(labels, Does.Contain("File properties…"));
      Assert.That(labels, Does.Not.Contain("Unload module"), "there is no supported way to do it on Linux (PRD §32)");
    });
  }

  [Test]
  public void ThePerThreadActionsAreStillOnTheThreadsList() {
    var labels = PaneMenuContaining("Set thread affinity…");
    Assert.That(labels, Does.Contain("Thread priority"));
  }

  [Test]
  public void TheLifecycleActionsAreOnTheContextMenu() {
    var labels = ContextMenuLabels(Window());

    Assert.Multiple(() => {
      Assert.That(labels, Does.Contain("End task"), "the one that asks (PRD §25.1)");
      Assert.That(labels, Does.Contain("End process"), "the one that does not");
      Assert.That(labels, Does.Contain("End process tree"));
      Assert.That(labels, Does.Contain("Restart"));
      Assert.That(labels, Does.Contain("Suspend"));
      Assert.That(labels, Does.Contain("Resume"));
    });
  }

  /// <summary>
  /// "End task" and "End process" are both there and are not the same item.
  /// </summary>
  /// <remarks>
  /// The PRD's rule, and the reason it is worth a test of its own: the first asks, the second does
  /// not, and a menu that offered only one of them would silently pick which of those somebody meant
  /// (PRD §25.1, §5.5).
  /// </remarks>
  [Test]
  public void AskingAndTellingAreSeparateItemsInBothMenus() {
    foreach (var labels in new[] { ContextMenuLabels(Window()), MenuBarLabels(Window()) }) {
      Assert.That(labels.Count(l => l == "End task"), Is.EqualTo(1));
      Assert.That(labels.Count(l => l == "End process"), Is.EqualTo(1));
    }
  }

  [Test]
  public void EverySchedulerClassTheCatalogueOffersIsOnTheMenu() {
    var labels = ContextMenuLabels(Window());

    Assert.That(labels, Does.Contain("Scheduling class"));
    foreach (var choice in SchedulingClasses.Offered)
      Assert.That(labels, Does.Contain(choice.Name), "a class in the catalogue and not in the menu is a class nobody can pick");
  }

  /// <summary>
  /// Getting from a process to the things around it (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// Grouped under one item, and none of them changes anything — which is what keeps them apart from
  /// the items that do (PRD §5.5).
  /// </remarks>
  [Test]
  public void TheNavigationItemsAreThereAndAreTheirOwnGroup() {
    var labels = ContextMenuLabels(Window());

    Assert.Multiple(() => {
      Assert.That(labels, Does.Contain("Go to"));
      Assert.That(labels, Does.Contain("Parent process"));
      Assert.That(labels, Does.Contain("Child processes"));
      Assert.That(labels, Does.Contain("Executable folder"));
      Assert.That(labels, Does.Contain("Executable properties…"));
      Assert.That(labels, Does.Contain("Search the web for this name…"));
    });
  }

  /// <summary>
  /// The file box shows what it read and nothing it did not (PRD §25.3, §70).
  /// </summary>
  /// <remarks>
  /// The hash is not computed on opening, and the box says so rather than leaving the row blank —
  /// blank is what a hash of nothing would also look like. It is also the only place in this program
  /// that reads a whole file, which is why it waits to be asked.
  /// </remarks>
  [Test]
  [Platform("Linux")]
  public void TheFileBoxReadsTheFactsAndLeavesTheHashUntilItIsAsked() {
    var dialog = new FilePropertiesDialog("/usr/bin/env", [new("architecture", "x86-64")]);
    var description = dialog.Description;

    Assert.Multiple(() => {
      Assert.That(description, Does.Contain("/usr/bin/env"));
      Assert.That(description, Does.Contain("architecture"));
      Assert.That(description, Does.Contain("x86-64"));
      Assert.That(description, Does.Contain("sha-256"));
      Assert.That(description, Does.Contain("not computed"), "reading a whole file waits to be asked");
    });
  }

  [Test]
  public void AFileThatIsNotThereGivesTheReasonRatherThanAnEmptyBox() {
    var description = new FilePropertiesDialog("/no/such/program").Description;

    Assert.That(description, Does.Contain("/no/such/program"));
    Assert.That(description, Does.Contain("no such file"));
    Assert.That(description, Does.Not.Contain("0 B"), "the reason, not a zero (PRD §72.3)");
  }

  [Test]
  public void TheLifecycleActionsAreAlsoOnTheMenuBar() {
    var labels = MenuBarLabels(Window());

    Assert.Multiple(() => {
      Assert.That(labels, Does.Contain("End task"));
      Assert.That(labels, Does.Contain("End process tree"));
      Assert.That(labels, Does.Contain("Restart"));
    });
  }

}
