using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// "Which process is using this?" (PRD §33).
/// </summary>
/// <remarks>
/// <para>
/// The question that makes people install a process explorer in the first place — a file that will
/// not delete, a port already bound, a library that will not unload. The search itself has existed
/// behind <c>--find</c> for a while; this is the half that lets somebody ask it without leaving the
/// window and then click the answer.
/// </para>
/// <para>
/// The expensive half of the search — every descriptor, every mapping and every socket of every
/// process — runs only for processes the cheap fields did not already answer for, so a common word
/// costs far less than it looks (§5.4). It is still not instant on a busy machine, which is why the
/// button says what it is doing while it runs.
/// </para>
/// </remarks>
public sealed class FindResourceDialog : Form {

  private const int _Margin = 12;

  private readonly ISystemProbe _probe;
  private readonly TextBox _pattern = new();
  private readonly Button _search = new() { Text = "Find" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly CheckBox _deep = new() { Text = "Search descriptors, mappings and sockets", Checked = true };

  /// <summary>
  /// The one search option that cannot be written into the pattern (PRD §33).
  /// </summary>
  /// <remarks>
  /// The four modes are chosen by the shape of what is typed — quotes for exact, slashes for a
  /// regular expression, a star for a wildcard — so they need no controls and every front-end has
  /// them. Case is different: it is a property of the comparison rather than of the pattern, and
  /// there is no punctuation for it that would not collide with a file name.
  /// </remarks>
  private readonly CheckBox _matchCase = new() { Text = "Match case" };
  private readonly Label _status = new();
  private readonly TreeListView _results = new();
  private readonly List<ResourceMatch> _matches = [];

  private SystemSnapshot? _snapshot;

  public FindResourceDialog(ISystemProbe probe) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this.Text = "Find handles, files and modules";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 900, 520);
    this.MinimumSize = new(520, 320);

    this._results.ShowColumnHeaders = true;
    this._results.ItemHeight = 17;
    this._results.Columns.Add(new("Process", 200, node => Row(node).ProcessName));
    this._results.Columns.Add(new("PID", 70, node => Row(node).Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    this._results.Columns.Add(new("Kind", 110, node => Row(node).Kind.ToString()));
    // What the holder may do with the thing that matched: the access mode of a descriptor, the
    // permission characters of a mapping. The question after "who has my file open" is always
    // whether they have it open for writing (PRD §33).
    this._results.Columns.Add(new("Access", 64, node => Row(node).Access ?? "—"));
    this._results.Columns.Add(new("Detail", 420, node => Row(node).Detail));
    this._results.MouseDoubleClick += (_, _) => this.Choose();

    this._search.Click += (_, _) => this.Search();
    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._pattern);
    this.Controls.Add(this._search);
    this.Controls.Add(this._close);
    this.Controls.Add(this._deep);
    this.Controls.Add(this._matchCase);
    this.Controls.Add(this._status);
    this.Controls.Add(this._results);

    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
    this._status.Text =
      "A file name, a port as \":443\", a library, a service — «*.so.6» as a wildcard,"
      + " \"exactly this\" in quotes, /a regular expression/ in slashes.";
  }

  /// <summary>The process a result was double-clicked on, for the caller to select.</summary>
  public int ChosenPid { get; private set; } = -1;

  private static ResourceMatch Row(TreeNode node) => (ResourceMatch)node.Tag!;

  private void Choose() {
    if (this._results.SelectedNode?.Tag is not ResourceMatch match)
      return;

    this.ChosenPid = match.Pid;
    this.Close();
  }

  private void Search() {
    var pattern = this._pattern.Text.Trim();
    if (pattern.Length == 0) {
      // Every process contains the empty string; returning all of them is never what was meant.
      this._status.Text = "Type something to look for.";
      return;
    }

    this._status.Text = "Searching…";
    this._results.Nodes.Clear();
    this._matches.Clear();

    // Sampled here rather than shared with the window's own tick: a search that ran against a
    // snapshot from up to a second ago would attribute a descriptor to a process that has since
    // exited, and this is the one place where that matters — the whole answer is a pid.
    this._snapshot ??= new();
    this._probe.Sample(this._snapshot);

    try {
      this._matches.AddRange(ResourceSearch.Find(
        this._probe,
        this._snapshot,
        pattern,
        this._deep.Checked,
        this._matchCase.Checked
      ));
    } catch (IOException e) {
      this._status.Text = $"The search could not finish: {e.Message}";
      return;
    }

    foreach (var match in this._matches)
      this._results.Nodes.Add(new TreeNode(match.ProcessName) { Tag = match });

    // Which of the four modes the pattern was read as, always — a star inside a file name turns a
    // substring search into a wildcard one without anybody asking for it, and a result of nothing at
    // all is exactly when somebody needs to be told that (PRD §33).
    var mode = ResourceSearch.ModeOf(pattern) switch {
      SearchMode.Regex => "as a regular expression",
      SearchMode.Exact => "as an exact match",
      SearchMode.Wildcard => "as a wildcard",
      _ => "as a substring",
    };

    var sensitivity = this._matchCase.Checked ? ", case-sensitive" : string.Empty;
    this._status.Text = this._matches.Count switch {
      0 => $"Nothing is using anything matching \"{pattern}\" — read {mode}{sensitivity}.",
      1 => $"One process, matched {mode}{sensitivity}. Double-click it to go there.",
      _ => $"{this._matches.Count} processes, matched {mode}{sensitivity}. Double-click one to go there.",
    };
  }

  public void ApplyLayout() {
    var width = Math.Max(300, this.Width - (2 * _Margin));
    this._pattern.Bounds = new(_Margin, _Margin, width - 180, 26);
    this._search.Bounds = new(this.Width - _Margin - 170, _Margin, 80, 26);
    this._close.Bounds = new(this.Width - _Margin - 80, _Margin, 80, 26);
    // The two options share a row: the deep search is the one people turn off, so it keeps the left
    // edge, and the case toggle sits at the right where its label has room to be read whole.
    this._deep.Bounds = new(_Margin, _Margin + 32, Math.Max(200, width - 140), 20);
    this._matchCase.Bounds = new(this.Width - _Margin - 130, _Margin + 32, 130, 20);
    this._status.Bounds = new(_Margin, this.Height - _Margin - 18, width, 18);
    this._results.Bounds = new(_Margin, _Margin + 58, width, Math.Max(60, this.Height - _Margin - 88));
  }

}
