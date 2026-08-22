using Hawkynt.NativeForms;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A sheet of names and values in a window of its own (PRD §41, §42).
/// </summary>
/// <remarks>
/// <para>
/// The properties box the secondary views needed and did not have. A row of a table is one line high
/// and the answers are not: a unit's dependency list is forty entries, its command is two hundred
/// characters, and a startup entry's reason for not running is a sentence. None of that fits a cell,
/// and all of it is what somebody opened the row to read.
/// </para>
/// <para>
/// Deliberately not a second <see cref="ProcessFactsPage"/>: that one is a page inside a window and
/// takes its values from a <see cref="Query.ProcessRow"/>, and neither a unit nor a login entry has a
/// row of the process table to be read off. What the two share is the shape — two columns, a list
/// that scrolls, and no hand-laid sheet to draw its last four rows past the bottom edge.
/// </para>
/// </remarks>
internal sealed class FactsDialog : Form {

  private const int _Margin = 12;
  private const int _ButtonHeight = 28;

  private readonly TreeListView _list = new();
  private readonly Button _copy = new() { Text = "Copy" };
  private readonly Button _close = new() { Text = "Close" };

  /// <param name="title">What the window is about, which is the whole of its title bar.</param>
  /// <param name="facts">
  /// The rows, in the order they should be read. A value may carry newlines; the list shows the first
  /// line and Copy gives the whole of it, which is the same bargain every other list here makes.
  /// </param>
  public FactsDialog(string title, IReadOnlyList<KeyValuePair<string, string>> facts) {
    ArgumentNullException.ThrowIfNull(facts);

    this.Text = title;
    // A secondary window closing must not take the program with it: the first window shown owns the
    // message loop, and every window that is not that one has to say so.
    this.QuitsOnClose = false;

    this._list.AccessibleName = title;
    this._list.ShowColumnHeaders = true;
    this._list.ItemHeight = 17;
    this._list.Columns.Add(new("Property", 220, node => ((string[])node.Tag!)[0]));
    this._list.Columns.Add(new("Value", 640, node => ((string[])node.Tag!)[1]));

    foreach (var (name, value) in facts)
      this._list.Nodes.Add(new TreeNode(name) { Tag = new[] { name, OneLine(value) } });

    this._facts = facts;
    this._copy.Click += (_, _) => Clipboard.SetText(this.Description);
    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._list);
    this.Controls.Add(this._copy);
    this.Controls.Add(this._close);

    this.Bounds = new(0, 0, 900, Math.Min(760, (facts.Count * 17) + 120));
    this.MinimumSize = new(480, 260);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  private readonly IReadOnlyList<KeyValuePair<string, string>> _facts;

  /// <summary>
  /// The whole sheet as text — what Copy puts on the clipboard, and what a test reads instead of a
  /// picture.
  /// </summary>
  /// <remarks>
  /// From the facts rather than from the rows, so a value the list had to fold onto one line comes
  /// out of the clipboard whole. A dependency list is the case that matters: forty units, one per
  /// line, and the row can only ever show the first of them.
  /// </remarks>
  public string Description {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine(this.Text);
      foreach (var (name, value) in this._facts)
        text.AppendLine($"{name}: {value}");

      return text.ToString();
    }
  }

  /// <summary>How many rows the sheet has — the empty-box detector a picture would show.</summary>
  public int RowCount => this._list.Nodes.Count;

  private static string OneLine(string value)
    => value.Contains('\n', StringComparison.Ordinal)
      ? string.Join(" · ", value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      : value;

  public void ApplyLayout() {
    var width = Math.Max(300, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);

    this._list.Bounds = new(_Margin, _Margin, width, Math.Max(40, buttons - _Margin - 10));
    this._copy.Bounds = new(_Margin, buttons, 90, _ButtonHeight);
    this._close.Bounds = new(this.Width - _Margin - 90, buttons, 90, _ButtonHeight);

    // The value column takes whatever is left. A command line and a unit file path are the two widest
    // things on any of these sheets and the two most worth reading whole; the scrollbar's width comes
    // off whether or not one is showing, because a value drawn under the bar loses its last character
    // and a shorter value is not the same value (PRD §11).
    if (this._list.Columns.Count < 2)
      return;

    var available = width - Hawkynt.NativeForms.Drawing.DefaultTheme.Instance.ScrollBarSize - 2;
    var wanted = Math.Max(320, available - this._list.Columns[0].Width);
    if (wanted != this._list.Columns[1].Width)
      this._list.Columns[1].Width = wanted;
  }

}
