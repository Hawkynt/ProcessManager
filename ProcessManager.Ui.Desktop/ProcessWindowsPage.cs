using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The windows one process has on screen, and what may be asked of them (PRD §39).
/// </summary>
/// <remarks>
/// <para>
/// The list and the actions arrive together, deliberately. §25.4 and §26 both refused this page while
/// it would have been a list and nothing else — "a page that could show a window and do nothing to it
/// is half a feature" — so the row menu is not an extra here, it is the half that was missing.
/// </para>
/// <para>
/// Read when the page is asked for and not on the tick. Enumerating the desktop opens the display,
/// walks <c>_NET_CLIENT_LIST</c> and reads four properties off every window on the machine, which is
/// the same bargain the memory map makes and for the same reason (PRD §5.4). A window's title changes
/// while somebody watches, so the heading carries the time the list was taken and there is a button.
/// </para>
/// <para>
/// <b>An empty list is not the same as no answer.</b> A Wayland session refuses to describe other
/// programs' surfaces by design, and a machine with no display has nothing to describe; both look
/// like a process with no windows unless the page says which it is (PRD §5.3, §72.3).
/// </para>
/// </remarks>
internal sealed class ProcessWindowsPage {

  private readonly Panel _panel = new();
  private readonly Panel _buttons = new();
  private readonly Button _reread = new() { Text = "Re-read" };

  /// <summary>Where the window id is, for the row menu. A column index in one place, not six.</summary>
  private const int _HandleColumn = 1;

  private readonly RecordTable _table = new(
    // The title first, because it is the only cell a person recognises a window by. It is also the
    // one that runs longest, and it is not last only because the columns that say what a row *is*
    // would then be off the right-hand edge of a page that does not scroll sideways (PRD §11).
    ("Title", 380),
    ("Window", 130),
    ("Class", 190),
    ("On screen", 96),
    ("Position", 120),
    ("Size", 120),
    // Last, so the table stretches into it. What §39 asks for and X11 does not hand over here, said
    // in the row rather than left as six absent columns: a column that is missing is a requirement
    // quietly dropped, and one that says it has nothing is a fact about this build (PRD §72.3).
    ("Not read", 260)
  );

  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private WindowList _windows = WindowList.NotImplemented;

  public ProcessWindowsPage(ISystemProbe probe, IProcessActions? actions) {
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;
    this._actions = actions;

    this._table.Control.Dock = DockStyle.Fill;
    this._buttons.Dock = DockStyle.Bottom;
    this._buttons.Height = 40;
    this._reread.Click += (_, _) => this.Reread();
    this._buttons.Controls.Add(this._reread);

    this._table.ContextMenuStrip = this.BuildMenu();

    // The strip is added first so it claims its edge, and the table takes what is left.
    this._panel.Controls.Add(this._buttons);
    this._panel.Controls.Add(this._table.Control);
  }

  public Control Control => this._panel;

  /// <summary>Which process this page is about. Set once, by the window that owns it.</summary>
  public ProcessKey Key { get; set; }

  /// <summary>What the process is called, for the sentences that name it.</summary>
  public string Name { get; set; } = string.Empty;

  /// <summary>What the page says, for a test and for the capture log (PRD §9.6).</summary>
  public string Description => this._table.Description;

  /// <summary>How many windows are listed — the empty-page detector a picture would show.</summary>
  public int RowCount => this._table.RowCount;

  /// <summary>The sentence above the list, which is the half that says why an empty one is empty.</summary>
  public string Heading => this._table.Heading;

  /// <summary>Whether the desktop answered, and which way it did not — for the tab-hiding preference.</summary>
  public WindowSourceState State => this._windows.State;

  public void Stretch() {
    const int Margin = 10;
    this._reread.Bounds = new(Margin, (this._buttons.Height - 28) / 2, 110, 28);
    this._table.Stretch();
  }

  /// <summary>Fills the page if it has not been filled yet.</summary>
  /// <remarks>
  /// Called when the page becomes the visible one, and once — a window list is not a rate. Whoever
  /// wants a newer one presses the button, which is the bargain every other on-demand list here makes
  /// (PRD §5.4).
  /// </remarks>
  public void EnsureFilled() {
    if (this._filled)
      return;

    this.Reread();
  }

  private bool _filled;

  /// <summary>Re-reads the desktop and keeps the windows belonging to this process.</summary>
  public void Reread() {
    this._filled = true;
    this._windows = this._probe.GetWindows();

    // Filtered here rather than in the probe: the desktop is enumerated once for the whole machine —
    // the picker and the network view read the same list — and asking it per process would walk every
    // window on screen once per properties window that happens to be open.
    var mine = new List<WindowRecord>();
    foreach (var window in this._windows.Windows)
      if (window.Pid == this.Key.Pid)
        mine.Add(window);

    this._table.Fill(this.Describe(mine.Count), mine.Count, index => {
      var window = mine[index];
      return [
        window.Title is { Length: > 0 } title ? title : "— this window carries no title",
        // Hexadecimal, because that is how every X11 tool prints a window id: a number pasted into
        // `xprop -id` or `xwininfo -id` has to match the form they take (PRD §5.3).
        "0x" + window.Handle.ToString("x", CultureInfo.InvariantCulture),
        window.Class ?? "—",
        // Mapped, which is what X11 answers. A window that is minimised is not mapped, and one behind
        // another is: this says "on screen" rather than "visible" because the second would be read as
        // "not covered up", which nothing here knows.
        window.IsVisible ? "yes" : "no — not mapped",
        string.Create(CultureInfo.InvariantCulture, $"{window.Bounds.X}, {window.Bounds.Y}"),
        string.Create(CultureInfo.InvariantCulture, $"{window.Bounds.Width} × {window.Bounds.Height}"),
        "thread, minimised/maximised, responding, workspace, monitor, parent",
      ];
    });
  }

  /// <summary>
  /// The sentence above the list.
  /// </summary>
  /// <remarks>
  /// Never left to the row count. Nought windows means four different things here — this process has
  /// none, this is a Wayland session that will not say, there is no display at all, or this build does
  /// not look — and only one of them is a fact about the process (PRD §5.3, §72.3).
  /// </remarks>
  private string Describe(int count) {
    var who = this.Name is { Length: > 0 } name ? name : "this process";
    if (this._windows.State != WindowSourceState.Available && count == 0)
      return this._windows.Explain();

    var text = count switch {
      0 => $"{who} has no window on this desktop. Most of a machine is like that: a daemon, a helper "
        + "and every kernel thread have none.",
      1 => $"{who} has one window.",
      _ => string.Create(CultureInfo.InvariantCulture, $"{who} has {count} windows."),
    };

    // Said even when the list is not empty, because on a Wayland session it is the reason the list is
    // *short*: what is here is whatever XWayland hosts, and an unexplained short list reads as a
    // broken program (PRD §39).
    if (this._windows.State == WindowSourceState.WaylandRefuses)
      text += " " + this._windows.Explain();

    return text + $"  Read at {DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}.";
  }

  #region what may be asked of a window (PRD §39)

  /// <summary>
  /// The five requests, and the two copies.
  /// </summary>
  /// <remarks>
  /// None of these changes the process, which is why they sit together and why closing is at the
  /// bottom behind a separator: it is the only one of the five that can lose somebody's unsaved work
  /// if the program does not ask first (PRD §5.5).
  /// </remarks>
  private ContextMenuStrip BuildMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Bring to the front", () => this.Command(WindowCommand.Foreground)));
    menu.Items.Add(Item("Minimise", () => this.Command(WindowCommand.Minimize)));
    menu.Items.Add(Item("Maximise", () => this.Command(WindowCommand.Maximize)));
    menu.Items.Add(Item("Restore", () => this.Command(WindowCommand.Restore)));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("Copy window id", this.CopyHandle));
    menu.Items.Add(Item("Copy row", this.CopyRow));
    menu.Items.Add(new ToolStripSeparator());
    // Last and alone, because it is the one that asks a program to stop. It asks rather than tells:
    // an editor with a modified buffer answers with its own dialog (PRD §25.1).
    menu.Items.Add(Item("Ask this window to close", () => this.Command(WindowCommand.Close)));
    return menu;

    static ToolStripMenuItem Item(string text, Action action) {
      var item = new ToolStripMenuItem(text);
      item.Click += (_, _) => action();
      return item;
    }
  }

  /// <summary>The window id in the selected row, or nought when there is no row to act on.</summary>
  /// <remarks>
  /// Read back out of the cell rather than kept beside the list, so that a row menu opened after the
  /// list was re-read acts on the window the reader is looking at rather than the one that was in
  /// that position last time.
  /// </remarks>
  private ulong SelectedHandle
    => this._table.Selected is { } cells
      && cells.Length > _HandleColumn
      && cells[_HandleColumn].StartsWith("0x", StringComparison.Ordinal)
      && ulong.TryParse(cells[_HandleColumn].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle)
        ? handle
        : 0;

  private void Command(WindowCommand command) {
    var handle = this.SelectedHandle;
    if (handle == 0) {
      MessageBox.Show("No window is selected.", "Process Manager");
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    var result = this._actions.CommandWindow(this.Key, handle, command);
    // Both outcomes are said. A request granted silently looks the same as one that went nowhere, and
    // the difference between "asked to close" and "closed" is the whole of §25.1's distinction.
    MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");
    if (result.Succeeded)
      this.Reread();
  }

  private void CopyHandle() {
    if (this._table.Selected is { } cells && cells.Length > _HandleColumn)
      Clipboard.SetText(cells[_HandleColumn]);
  }

  private void CopyRow() {
    if (this._table.Selected is { } cells)
      Clipboard.SetText(string.Join("  ", cells));
  }

  #endregion

}
