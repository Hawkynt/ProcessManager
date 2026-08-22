using Hawkynt.NativeForms;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A list of things the machine has, with a line above it saying what is being shown (PRD §9).
/// </summary>
/// <remarks>
/// <para>
/// The four secondary views — startup, sessions, services, sockets — are all the same shape: ask the
/// probe for a list, put it in a table, and say how many of what came back. What differs between
/// them is the columns and where the rows come from, so that is all each of them writes.
/// </para>
/// <para>
/// The heading is not decoration. An empty list and a list this user is not allowed to read look
/// identical, and every one of these can be either (PRD §5.3, §72.3). It also carries the time the
/// rows were collected, because unlike the process table these are not refreshed every second —
/// enumerating every unit on the machine once a second would be the monitor becoming the thing worth
/// monitoring (PRD §5.4).
/// </para>
/// </remarks>
internal sealed class RecordTable {

  private readonly Panel _panel = new();
  private readonly Label _heading = new();
  private readonly TreeListView _list = new();

  /// <param name="what">
  /// What this is a list of, for a screen reader. The heading above it says the same thing and more,
  /// but a heading is a separate control: a reader who tabs into the table hears the table's own
  /// name and nothing of the label beside it (PRD §74).
  /// </param>
  public RecordTable(string what, params (string Header, int Width)[] columns) {
    ArgumentNullException.ThrowIfNull(columns);

    this._minimumLast = columns.Length > 0 ? columns[^1].Width : 120;
    this._panel.Dock = DockStyle.Fill;
    this._heading.Dock = DockStyle.Top;
    this._heading.Height = 22;

    this._list.AccessibleName = what;
    this._list.Dock = DockStyle.Fill;
    this._list.ShowColumnHeaders = true;
    // The same seventeen pixels the process tree uses, so two tables in one window are one table's
    // worth of visual noise rather than two.
    this._list.ItemHeight = 17;
    for (var i = 0; i < columns.Length; ++i) {
      var column = i;
      // Every column left-aligned, deliberately. A right-aligned column draws a left-aligned header
      // in this toolkit, so a narrow numeric column and its heading collide; until that is fixed
      // upstream, a number that lines up on the left is worth more than one whose unit is hidden
      // under the header beside it (PRD §11).
      this._list.Columns.Add(new(columns[i].Header, columns[i].Width, node => ((string[])node.Tag!)[column]));
    }

    // The heading is added after the list, because docked children claim their edge in reverse
    // order: the label gets its band and the list keeps everything that is left.
    this._panel.Controls.Add(this._list);
    this._panel.Controls.Add(this._heading);
  }

  public Control Control => this._panel;

  /// <summary>
  /// Widens the last column to fill the table.
  /// </summary>
  /// <remarks>
  /// The scrollbar's width comes off whether or not one is showing, because whether one is showing
  /// is not something this side can ask; the cost when there is none is a few pixels at the right
  /// edge, and the cost of getting it wrong the other way is a value drawn underneath the bar with
  /// its last character cut off (PRD §11).
  /// </remarks>
  public void Stretch() {
    var count = this._list.Columns.Count;
    if (count == 0)
      return;

    var used = 0;
    for (var i = 0; i < count - 1; ++i)
      used += this._list.Columns[i].Width;

    var available = this._list.Width - Hawkynt.NativeForms.Drawing.DefaultTheme.Instance.ScrollBarSize - 2;
    var wanted = Math.Max(this._minimumLast, available - used);
    if (wanted != this._list.Columns[count - 1].Width)
      this._list.Columns[count - 1].Width = wanted;
  }

  private readonly int _minimumLast;

  /// <summary>What the view says above its list.</summary>
  public string Heading => this._heading.Text;

  public int RowCount => this._list.Nodes.Count;

  /// <summary>What the table holds, for a test and for the capture log.</summary>
  public string Description {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine(this._heading.Text);
      foreach (var node in this._list.Nodes)
        if (node.Tag is string[] cells)
          text.AppendLine(string.Join("  ", cells));

      return text.ToString();
    }
  }

  /// <summary>
  /// Replaces the rows.
  /// </summary>
  /// <param name="heading">
  /// What came back, in words. Never left to the row count: nought rows is the one answer that needs
  /// a sentence, because it is the one that can mean two different things.
  /// </param>
  public void Fill(string heading, int count, Func<int, string[]> row) {
    ArgumentNullException.ThrowIfNull(row);

    this._heading.Text = heading;
    this._list.Nodes.Clear();
    for (var i = 0; i < count; ++i) {
      var cells = row(i);
      this._list.Nodes.Add(new TreeNode(cells[0]) { Tag = cells });
    }
  }

  /// <summary>The selected row's cells, or null when nothing is selected.</summary>
  public string[]? Selected => this._list.SelectedNode?.Tag as string[];

  /// <summary>
  /// Every row as tab-separated text, with the column headers above it (PRD §95).
  /// </summary>
  /// <remarks>
  /// The headers and not only the values: a row of fourteen cells pasted into a message is unreadable
  /// without them, and this is the only way to the columns a narrow window puts off the right-hand
  /// edge.
  /// </remarks>
  public string Describe() {
    var text = new System.Text.StringBuilder();
    text.Append(this.Headers());
    foreach (var node in this._list.Nodes)
      if (node.Tag is string[] cells)
        text.Append('\n').Append(string.Join('\t', cells));

    return text.ToString();
  }

  /// <summary>The selected row as text, headers included. Empty when nothing is selected.</summary>
  public string DescribeSelected()
    => this.Selected is { } cells ? this.Headers() + "\n" + string.Join('\t', cells) : string.Empty;

  private string Headers() {
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < this._list.Columns.Count; ++i) {
      if (i > 0)
        text.Append('\t');

      text.Append(this._list.Columns[i].Text);
    }

    return text.ToString();
  }

  /// <summary>
  /// Selects the row whose first cell is <paramref name="name"/> (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// The first cell rather than a row number, because the number is the collection order and a caller
  /// that has one has already had to agree with this table about sorting. The name is what the caller
  /// actually holds — a unit, a user, an interface — and it is what the reader sees.
  /// </remarks>
  /// <returns>Whether there was such a row. False is an answer and not a failure: a navigation that
  /// lands nowhere has to say so rather than leaving the previous selection looking like the
  /// destination.</returns>
  public bool Select(string name) {
    ArgumentNullException.ThrowIfNull(name);

    foreach (var node in this._list.Nodes) {
      if (node.Tag is not string[] cells || cells.Length == 0)
        continue;

      if (!string.Equals(cells[0], name, StringComparison.Ordinal))
        continue;

      this._list.SelectedNode = node;
      return true;
    }

    return false;
  }

  /// <summary>Hangs a menu on the rows, for the views that have something to offer.</summary>
  public ContextMenuStrip? ContextMenuStrip {
    get => this._list.ContextMenuStrip;
    set => this._list.ContextMenuStrip = value;
  }

  /// <summary>Raised when a row is opened, which every list in this program treats as "tell me more".</summary>
  public event EventHandler<MouseEventArgs>? RowOpened {
    add => this._list.MouseDoubleClick += value;
    remove => this._list.MouseDoubleClick -= value;
  }

}
