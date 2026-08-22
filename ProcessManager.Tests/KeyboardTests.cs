using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Working the window without a mouse (PRD §74, §99).
/// </summary>
/// <remarks>
/// <para>
/// The terminal's key bindings have been under test since they were written; the window's had never
/// been read by anything. Every accelerator was a line in a builder that compiled whatever it was
/// given, so two items claiming the same chord — of which one would silently never fire — was a
/// thing that could be written, reviewed and shipped.
/// </para>
/// <para>
/// What is asserted is the inventory and its rules, not the handlers: working an item that ends a
/// process needs a confirmation dialog, and there is no display to open one on. The handlers are
/// tested where they live, below the window.
/// </para>
/// </remarks>
[TestFixture]
public sealed class KeyboardTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();

    /// <summary>The processes the table holds. Empty for the tests that only read the menus.</summary>
    public IReadOnlyList<(int Pid, string Name)> Processes { get; init; } = [];

    public void Sample(SystemSnapshot snapshot) {
      var records = snapshot.PrepareProcesses(this.Processes.Count);
      for (var i = 0; i < this.Processes.Count; ++i) {
        records[i] = default;
        records[i].Key = new(this.Processes[i].Pid, 100);
        records[i].Name = this.Processes[i].Name;
        records[i].UserName = "alice";
        // Not sampled rather than nought: an unread counter that reports zero is the defect this
        // whole document is written against (PRD §72.3).
        records[i].HandleCount = Counter.NotSampledYet;
      }
    }

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

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var deeper in Descendants(child))
        yield return deeper;
    }
  }

  /// <summary>
  /// Every menu item in the window, menu bar and context menus alike, flattened through the
  /// submenus.
  /// </summary>
  private static List<ToolStripMenuItem> Items(MainWindow window) {
    var found = new List<ToolStripMenuItem>();
    foreach (var control in Descendants(window)) {
      if (control is MenuStrip menu)
        Collect(menu.Items, found);

      if (control.ContextMenuStrip is { } context)
        Collect(context.Items, found);
    }

    Assert.That(found, Is.Not.Empty, "the window has no menu items at all");
    return found;

    static void Collect(ToolStripItemCollection items, List<ToolStripMenuItem> into) {
      foreach (var item in items)
        if (item is ToolStripMenuItem entry) {
          into.Add(entry);
          Collect(entry.DropDownItems, into);
        }
    }
  }

  /// <summary>Only the ones that claim a chord.</summary>
  private static List<(string Text, Keys Chord)> Accelerators(MainWindow window) {
    var found = new List<(string, Keys)>();
    foreach (var item in Items(window))
      if (item.ShortcutKeys != Keys.None)
        found.Add((item.Text, item.ShortcutKeys));

    return found;
  }

  /// <summary>
  /// No chord is claimed twice.
  /// </summary>
  /// <remarks>
  /// The form dispatches a chord to the first item that wants it, so a duplicate is not an error at
  /// any level — it is one item that works and another that quietly never does, and which is which
  /// depends on the order they were added in. Nothing about the window would look wrong.
  /// </remarks>
  /// <summary>
  /// There is an inventory at all. Every rule below is written as "no accelerator may…", and a
  /// window whose accelerators had all been dropped in a refactor would satisfy every one of them
  /// vacuously. This is the floor that makes the rest mean something.
  /// </summary>
  [Test]
  public void TheWindowIsWorkableFromTheKeyboard() {
    var bound = Accelerators(Window());
    foreach (var (text, chord) in bound)
      TestContext.Out.WriteLine($"{chord}\t{text}");

    Assert.That(bound, Has.Count.GreaterThanOrEqualTo(20));
  }

  [Test]
  public void NoTwoItemsClaimTheSameChord() {
    var claimed = new Dictionary<Keys, string>();
    var clashes = new List<string>();
    foreach (var (text, chord) in Accelerators(Window()))
      if (!claimed.TryAdd(chord, text))
        clashes.Add($"{chord} is claimed by both '{claimed[chord]}' and '{text}'");

    Assert.That(clashes, Is.Empty);
  }

  /// <summary>
  /// The accelerators §74 promises are all there, by chord rather than by label.
  /// </summary>
  /// <remarks>
  /// By chord because that is what a reader's fingers know: an item renamed keeps working, and an
  /// item whose chord was dropped in a refactor stops working while its label still reads the same.
  /// </remarks>
  [TestCase(Keys.F5, TestName = "refresh now")]
  [TestCase(Keys.F3, TestName = "the filter")]
  [TestCase(Keys.F6, TestName = "sort by the next column")]
  [TestCase(Keys.F7, TestName = "reverse the sort")]
  [TestCase(Keys.Control | Keys.D, TestName = "the lower pane")]
  [TestCase(Keys.Control | Keys.F, TestName = "find")]
  [TestCase(Keys.Control | Keys.E, TestName = "export")]
  [TestCase(Keys.Control | Keys.A, TestName = "tick every row")]
  [TestCase(Keys.Control | Keys.C, TestName = "copy the row")]
  public void TheChordIsBoundToSomething(Keys chord) {
    var bound = Accelerators(Window());

    Assert.That(bound.ConvertAll(entry => entry.Chord), Does.Contain(chord));
  }

  /// <summary>
  /// Nothing that cannot be undone is one keystroke away.
  /// </summary>
  /// <remarks>
  /// A chord dispatches wherever the focus is, including in the filter box, so a bare key or a
  /// single-modifier letter on an item that ends a process is a process ended by a typing mistake.
  /// Ending a task is the one destructive action §25.1 exempts from confirmation, which makes it the
  /// one that must not be reachable by accident either.
  /// </remarks>
  [Test]
  public void NothingDestructiveIsAKeystrokeAway() {
    foreach (var (text, chord) in Accelerators(Window())) {
      var destructive = false;
      // By prefix rather than by substring: every one of these labels begins with the verb, and
      // "Send signal" contains "end" — a substring match would call it destructive for the wrong
      // reason and go on passing after the reason stopped being true.
      foreach (var verb in (string[])["End", "Kill", "Terminate", "Restart", "Send signal", "Freeze", "Suspend"])
        destructive |= text.StartsWith(verb, StringComparison.OrdinalIgnoreCase);

      Assert.That(destructive, Is.False, $"'{text}' can be reached by pressing {chord}");
    }
  }

  /// <summary>
  /// An item with a chord is an item somebody can work, so it has to do something. An accelerator on
  /// a submenu header opens nothing and runs nothing.
  /// </summary>
  [Test]
  public void EveryChordIsOnAnItemThatDoesSomething() {
    foreach (var item in Items(Window())) {
      if (item.ShortcutKeys == Keys.None)
        continue;

      Assert.That(item.DropDownItems, Is.Empty, $"'{item.Text}' is a submenu with a chord on it");
      Assert.That(item.Text, Is.Not.Empty, $"{item.ShortcutKeys} is on an item with no label");
    }
  }

  /// <summary>
  /// A chord is a modifier and a key, or a function key. A bare letter would be obeyed instead of
  /// typed the moment the filter box has focus, which is the one control in the window where every
  /// keystroke is meant to be text.
  /// </summary>
  [Test]
  public void NoChordIsABareLetterOrDigit() {
    foreach (var (text, chord) in Accelerators(Window())) {
      if ((chord & Keys.Modifiers) != Keys.None)
        continue;

      var key = chord & Keys.KeyCode;
      Assert.That(
        key is (>= Keys.F1 and <= Keys.F12) or Keys.Delete or Keys.Escape or Keys.Insert,
        Is.True,
        $"'{text}' is on the bare key {key}, which is typed rather than obeyed in the filter box"
      );
    }
  }

  #region the two things that are not menu items (PRD §74)

  /// <summary>
  /// The row tick and the splitter were the two parts of this window with no key at all, and they
  /// are the two that are not menu items: both belong to a control, so both are worked by pressing
  /// something while that control has the focus rather than by an accelerator.
  /// </summary>
  /// <remarks>
  /// <para>
  /// What is asserted here is the half that lives in this repository. The toolkit maps Space to the
  /// check box of the row under the cursor and the arrow keys to the splitter, and has its own tests
  /// for both; what a window can get wrong is leaving the check boxes off, keeping a control out of
  /// the tab order so the key never arrives, or claiming one of those keys as an accelerator — the
  /// form's dialog chain runs menu shortcuts <em>before</em> the focused control sees the key, so an
  /// accelerator on Space would silently take the tick away again.
  /// </para>
  /// <para>
  /// The last of the three is already covered from the other side by the rule that no chord is a
  /// bare key, and it is asserted again by name here: that rule could be relaxed one day for a
  /// reason that has nothing to do with either of these, and this is the assertion that would then
  /// notice.
  /// </para>
  /// </remarks>
  [Test]
  public void TheRowTickIsReachableWithoutAMouse() {
    var window = Window();
    var tree = Descendants(window).OfType<TreeListView>().FirstOrDefault();

    Assert.That(tree, Is.Not.Null, "the window has no process list");
    Assert.That(tree!.CheckBoxes, Is.True, "there is nothing for Space to tick");
    Assert.That(tree.TabStop, Is.True, "the list cannot take the focus, so no key ever reaches it");
    Assert.That(
      Accelerators(window).ConvertAll(entry => entry.Chord & Keys.KeyCode),
      Does.Not.Contain(Keys.Space),
      "an accelerator on Space would run instead of the tick"
    );
  }

  [Test]
  public void TheSplitterIsReachableWithoutAMouse() {
    var window = Window();
    var split = Descendants(window).OfType<SplitContainer>().FirstOrDefault();

    Assert.That(split, Is.Not.Null, "the window has no splitter");
    Assert.That(split!.TabStop, Is.True, "the splitter cannot take the focus, so the arrows never reach it");

    // The pane the splitter divides is the one it is worth dividing. A splitter that had ended up
    // between two things nobody resizes would satisfy everything above and be worth nothing.
    Assert.That(split.Panel1.Controls, Is.Not.Empty);
    Assert.That(split.Panel2.Controls, Is.Not.Empty);

    foreach (var arrow in (Keys[])[Keys.Up, Keys.Down, Keys.Left, Keys.Right])
      Assert.That(
        Accelerators(window).ConvertAll(entry => entry.Chord),
        Does.Not.Contain(arrow),
        $"an accelerator on {arrow} would run instead of moving the splitter"
      );
  }

  /// <summary>
  /// And the tick says so. The three bulk verbs in the menu each wrote a line to the status bar and
  /// the check box itself wrote none, so the one gesture somebody would try first was the one that
  /// looked like it had not worked.
  /// </summary>
  [Test]
  public void TickingARowSaysHowManyAreTickedNow() {
    var probe = new StubProbe { Processes = [(4242, "sshd"), (99, "bash")] };
    var window = new MainWindow(new Sampler(probe), probe, null);
    // One sample, through the public route that also refills the tree. Start() would do it and would
    // also start a timer this test has no message loop to run.
    window.RefreshOnce();
    window.ShowGrouping(Query.ProcessGrouping.None);

    var tree = Descendants(window).OfType<TreeListView>().First();
    var status = Descendants(window).First(control => control.AccessibleName == "Status");

    Assert.That(tree.NodeAt(0), Is.Not.Null, "the sample produced no rows to tick");

    tree.NodeAt(0)!.Checked = true;
    tree.NodeAt(1)!.Checked = true;
    Assert.That(status.Text, Is.EqualTo("2 rows ticked"));

    tree.NodeAt(0)!.Checked = false;
    tree.NodeAt(1)!.Checked = false;
    Assert.That(status.Text, Is.EqualTo("nothing is ticked now"));
  }

  #endregion

}
