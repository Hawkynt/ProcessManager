using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Which page of a process's detail is showing.</summary>
public enum DetailTab : byte { Overview, Threads, Modules, Handles, Environment, Network }

/// <summary>
/// The terminal's answer to a process-properties window: one process, in as much detail as the
/// platform will give (PRD §6.2, §11).
/// </summary>
/// <remarks>
/// Collected when the view is opened or its page is changed, and not on the sampling tick — the same
/// rule the desktop pane follows, and for the same reason: enumerating one process's handles is
/// expensive and enumerating every process's would be absurd (PRD §3.5).
/// </remarks>
public sealed class DetailView(ISystemProbe probe) {

  private static readonly DetailTab[] _tabs = Enum.GetValues<DetailTab>();

  private readonly List<string[]> _rows = [];
  private string[] _headers = [];
  private int[] _widths = [];
  private ProcessKey _key;
  private bool _stale = true;

  public DetailTab Tab { get; private set; }

  public int Scroll { get; private set; }

  public int RowCount => this._rows.Count;

  public void Open(ProcessKey key) {
    this._key = key;
    this._stale = true;
    this.Scroll = 0;
  }

  /// <summary>Opens a named page directly, for the keys that jump straight to one (PRD §57.3).</summary>
  public void GoTo(DetailTab tab) {
    if (this.Tab == tab)
      return;

    this.Tab = tab;
    this._stale = true;
    this.Scroll = 0;
  }

  public void NextTab() {
    this.Tab = _tabs[(Array.IndexOf(_tabs, this.Tab) + 1) % _tabs.Length];
    this._stale = true;
    this.Scroll = 0;
  }

  public void PreviousTab() {
    this.Tab = _tabs[(Array.IndexOf(_tabs, this.Tab) - 1 + _tabs.Length) % _tabs.Length];
    this._stale = true;
    this.Scroll = 0;
  }

  public void ScrollBy(int delta, int pageHeight) {
    var maximum = Math.Max(0, this._rows.Count - pageHeight);
    this.Scroll = Math.Clamp(this.Scroll + delta, 0, maximum);
  }

  /// <summary>Re-collects the current page if it has been invalidated.</summary>
  /// <remarks>
  /// The rows come from <see cref="ProcessDetailTables"/>, which is also what <c>--process</c> prints
  /// — so the page a reader sees here and the page a script asks for cannot carry different columns,
  /// which is the disagreement §58 exists to stop.
  /// </remarks>
  public void Collect(in ProcessRecord process) {
    if (!this._stale)
      return;

    this._stale = false;
    this._rows.Clear();

    var table = ProcessDetailTables.Build(PageOf(this.Tab), probe, this._key, in process);
    this._headers = [.. table.Headers];
    this._widths = [.. table.Widths];
    this._rows.AddRange(table.Rows);
  }

  private static ProcessDetailPage PageOf(DetailTab tab) => tab switch {
    DetailTab.Threads => ProcessDetailPage.Threads,
    DetailTab.Modules => ProcessDetailPage.Modules,
    DetailTab.Handles => ProcessDetailPage.Handles,
    DetailTab.Environment => ProcessDetailPage.Environment,
    DetailTab.Network => ProcessDetailPage.Network,
    _ => ProcessDetailPage.Overview,
  };

  /// <summary>Draws the whole detail screen, tab strip included.</summary>
  public void Draw(TerminalScreen screen, in ProcessRecord process) {
    this.Collect(in process);

    var title = $" {process.Name} ({process.Pid}) ";
    screen.Fill(0, 0, screen.Width, ' ', Attributes.Header);
    screen.Write(0, 0, title, Attributes.Header);

    var x = title.Length + 2;
    foreach (var tab in _tabs) {
      var label = $" {tab} ";
      screen.Write(x, 0, label, tab == this.Tab ? Attributes.Selected : Attributes.Header);
      x += label.Length;
    }

    // Column headers.
    var y = 2;
    var columnX = 0;
    for (var i = 0; i < this._headers.Length; ++i) {
      screen.Write(columnX, y, this._headers[i], Attributes.Accent);
      columnX += this._widths[i] + 1;
    }

    var pageHeight = screen.Height - 4;
    if (this._rows.Count == 0) {
      // An empty page and a page we may not read look the same, so it says which (PRD §1.5).
      screen.Write(0, y + 2, "nothing to show — this process may not permit it, or has none", Attributes.Dim);
      return;
    }

    for (var line = 0; line < pageHeight; ++line) {
      var index = this.Scroll + line;
      if (index >= this._rows.Count)
        break;

      var row = this._rows[index];
      columnX = 0;
      for (var i = 0; i < row.Length && i < this._widths.Length; ++i) {
        var text = row[i];
        if (text.Length > this._widths[i])
          text = text[..this._widths[i]];

        screen.Write(columnX, y + 1 + line, text);
        columnX += this._widths[i] + 1;
      }
    }

    if (this._rows.Count > pageHeight)
      screen.WriteRight(0, y, screen.Width - 1, $"{this.Scroll + 1}–{Math.Min(this.Scroll + pageHeight, this._rows.Count)} of {this._rows.Count}", Attributes.Dim);
  }

}
