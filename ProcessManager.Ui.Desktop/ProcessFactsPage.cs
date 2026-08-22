using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One page of a process's properties: a column of names and a column of values (PRD §26).
/// </summary>
/// <remarks>
/// <para>
/// The values come out of <see cref="ProcessRow"/>, which already holds every field formatted, so a
/// page is a <em>list of field ids</em> and nothing else — no second formatter, no second set of
/// units, and no way for this window to disagree with the column of the same name in the table
/// behind it (PRD §5.1).
/// </para>
/// <para>
/// A list rather than a sheet of labels. A properties page is thirty rows on a machine with a lot to
/// say and eight on one without, the values run from "1" to a kilobyte of command line, and a list
/// scrolls where a hand-laid sheet clips — which is how a page ends up with its last four rows drawn
/// past the bottom edge while every test around it passes.
/// </para>
/// </remarks>
internal sealed class ProcessFactsPage {

  private const string _NameColumn = "Property";

  private readonly TreeListView _list = new();
  private readonly ProcessField[] _fields;

  /// <param name="fields">
  /// Which fields the page shows, in the order it shows them. Every one must be in the registry —
  /// <see cref="FieldRegistry.Get"/> answers with the first descriptor for anything it does not know,
  /// so an unregistered id here would draw a row labelled after a different field entirely.
  /// </param>
  public ProcessFactsPage(params ProcessField[] fields) {
    ArgumentNullException.ThrowIfNull(fields);
    this._fields = fields;

    this._list.Dock = DockStyle.Fill;
    this._list.ShowColumnHeaders = true;
    this._list.Columns.Add(new(_NameColumn, 220, node => ((string[])node.Tag!)[0]));
    // Wide, and last: a command line or an image path is the widest thing on the page and the one
    // most worth reading whole.
    this._list.Columns.Add(new("Value", 620, node => ((string[])node.Tag!)[1]));
  }

  /// <summary>
  /// The fields this page reads off a row, so whoever fills the row knows to format them.
  /// </summary>
  /// <remarks>
  /// A page asks a row for fields no column shows, and a row only formats what it was asked to. The
  /// two have to agree or the page shows blanks — so the list is published rather than remembered,
  /// and a test walks every page and requires the binder's set to contain it.
  /// </remarks>
  public IReadOnlyList<ProcessField> Fields => this._fields;

  public Control Control => this._list;

  /// <summary>
  /// Widens the value column to whatever the page is.
  /// </summary>
  /// <remarks>
  /// A command line and an image path are the two widest things on the page and the two most worth
  /// reading whole, and a fixed column truncated both while leaving a hand's width of empty page to
  /// the right of them. The scrollbar's width comes off whether or not one is showing, because
  /// whether one is showing is not something this side can ask — and a value drawn underneath the
  /// bar loses its last character, which is not a shorter value, it is a different one (PRD §11).
  /// </remarks>
  public void Stretch() {
    if (this._list.Columns.Count < 2)
      return;

    var available = this._list.Width - Hawkynt.NativeForms.Drawing.DefaultTheme.Instance.ScrollBarSize - 2;
    var wanted = Math.Max(320, available - this._list.Columns[0].Width);
    if (wanted != this._list.Columns[1].Width)
      this._list.Columns[1].Width = wanted;
  }

  /// <summary>What the page currently shows, one <c>name: value</c> per line, for a test.</summary>
  public string Description {
    get {
      var lines = new List<string>(this._list.Nodes.Count);
      foreach (var node in this._list.Nodes)
        if (node.Tag is string[] cells)
          lines.Add($"{cells[0]}: {cells[1]}");

      return string.Join('\n', lines);
    }
  }

  /// <summary>How many rows are on the page — the empty-page detector a picture would show.</summary>
  public int RowCount => this._list.Nodes.Count;

  /// <summary>
  /// Refills the page from the row.
  /// </summary>
  /// <param name="row">The process, already formatted.</param>
  /// <param name="extra">
  /// Facts the field catalogue does not carry — a running duration, what the ELF header said, what
  /// the file on disk weighs. Appended after the fields so the catalogue's order is never disturbed.
  /// </param>
  public void Update(ProcessRow row, IReadOnlyList<KeyValuePair<string, string>>? extra = null) {
    ArgumentNullException.ThrowIfNull(row);

    this._list.Nodes.Clear();
    foreach (var field in this._fields)
      this.Add(FieldRegistry.Get(field).Header, row.TextOf(field));

    if (extra is null)
      return;

    foreach (var (name, value) in extra)
      this.Add(name, value);
  }

  /// <summary>
  /// Refills the page from facts the field catalogue does not carry at all.
  /// </summary>
  /// <remarks>
  /// For a page whose subject is not the process row: a cgroup's ceilings belong to the group rather
  /// than to the process, so there is no field of the table for any of them and no row to read them
  /// off. Same list, same two columns, no second formatter (PRD §5.1).
  /// </remarks>
  public void Update(IReadOnlyList<KeyValuePair<string, string>> facts) {
    ArgumentNullException.ThrowIfNull(facts);

    this._list.Nodes.Clear();
    foreach (var (name, value) in facts)
      this.Add(name, value);
  }

  /// <summary>
  /// Replaces the page with the reason there is nothing on it (PRD §26, §72.3).
  /// </summary>
  /// <remarks>
  /// This is what the settings file calls a <em>disabled</em> tab: present, so that "can this machine
  /// do it" has a visible answer, and saying which of the two reasons applies rather than showing a
  /// column of dashes that could mean either. Hiding it instead is the other preference, and it is
  /// the caller's decision because the two answer different questions.
  /// </remarks>
  public void ShowUnavailable(string reason) {
    ArgumentNullException.ThrowIfNull(reason);
    this._list.Nodes.Clear();
    this.Add("not available here", reason);
  }

  private void Add(string name, string value) =>
    // The node's own text is the first cell, which is what the tree column draws and what a keyboard
    // search matches against.
    this._list.Nodes.Add(new TreeNode(name) { Tag = new[] { name, value } });

}
