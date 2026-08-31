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
/// not delete, a port already bound, a library that will not unload. The cheap process search still
/// exists behind <c>--find</c>; this window deliberately performs the exhaustive reverse lookup,
/// because somebody asking about a locked file needs every reference rather than the first excuse
/// for putting a process in the result list.
/// </para>
/// <para>
/// The expensive half — handles, mappings and sockets of every process — is opt-out and runs only
/// when somebody presses Find. Its coverage is reported explicitly: an ordinary account is often
/// forbidden from inspecting system processes, and "no match in the 37 processes we could inspect"
/// is not the same result as "nothing on this machine has it open" (PRD §72.3).
/// </para>
/// </remarks>
public sealed class FindResourceDialog : Form {

  private const int _Margin = 12;

  private readonly ISystemProbe _probe;
  private readonly TextBox _pattern = new();
  private readonly Button _search = new() { Text = "Find" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly CheckBox _deep = new() { Text = "Search handles, modules, mapped files and sockets", Checked = true };

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
  private readonly List<ReverseResourceMatch> _matches = [];

  private SystemSnapshot? _snapshot;

  public FindResourceDialog(ISystemProbe probe) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this.Text = "Find handles, modules and mapped files";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 980, 540);
    this.MinimumSize = new(600, 340);

    this._results.ShowColumnHeaders = true;
    this._results.ItemHeight = 17;
    this._results.Columns.Add(new("Process", 190, node => Row(node).ProcessName));
    this._results.Columns.Add(new("PID", 68, node => Row(node).Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    this._results.Columns.Add(new("Kind", 100, node => Name(Row(node).Kind)));
    this._results.Columns.Add(new("Type", 100, node => Row(node).ObjectType));
    // What the holder may do with the thing that matched: the access mode of a descriptor, the
    // permission characters of a mapping. The question after "who has my file open" is always
    // whether they have it open for writing (PRD §33).
    this._results.Columns.Add(new("Access", 64, node => Row(node).Access ?? "—"));
    this._results.Columns.Add(new("Detail", 430, node => Row(node).Detail));
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
      "A file, DLL, mapped database, device, pipe, port or service — *.so.6 as a wildcard,"
      + " \"exactly this\" in quotes, /a regular expression/ in slashes.";
  }

  /// <summary>
  /// Stable identity of the process a result was opened on. A PID-only caller can use
  /// <see cref="ChosenPid"/> for compatibility.
  /// </summary>
  public ProcessKey ChosenProcess { get; private set; }

  public int ChosenPid => this.ChosenProcess.Pid == 0 ? -1 : this.ChosenProcess.Pid;

  /// <summary>The current results, exposed so capture/tests can verify what the window claims.</summary>
  public IReadOnlyList<ReverseResourceMatch> Matches => this._matches;

  /// <summary>The exact coverage/result sentence shown under the list.</summary>
  public string StatusText => this._status.Text;

  private static ReverseResourceMatch Row(TreeNode node) => (ReverseResourceMatch)node.Tag!;

  private void Choose() {
    if (this._results.SelectedNode?.Tag is not ReverseResourceMatch match)
      return;

    this.ChosenProcess = match.Key;
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
    // snapshot from up to a second ago would attribute a handle to a process that has since exited.
    this._snapshot ??= new();
    this._probe.Sample(this._snapshot);

    ReverseSearchReport report;
    try {
      report = ResourceReverseSearch.Find(
        this._probe,
        this._snapshot,
        pattern,
        this._deep.Checked,
        this._matchCase.Checked
      );
    } catch (IOException e) {
      this._status.Text = $"The search could not finish: {e.Message}";
      return;
    }

    this._matches.AddRange(report.Matches);
    foreach (var match in this._matches)
      this._results.Nodes.Add(new TreeNode(match.ProcessName) { Tag = match });

    // Which of the four modes the pattern was read as, always — a star inside a file name turns a
    // substring search into a wildcard one without anybody asking for it, and a result of nothing at
    // all is exactly when somebody needs to be told that (PRD §33).
    var mode = ResourceSearch.ModeOf(pattern) switch {
      SearchMode.Regex => "regular expression",
      SearchMode.Exact => "exact",
      SearchMode.Wildcard => "wildcard",
      _ => "substring",
    };

    var pids = new HashSet<int>();
    foreach (var match in this._matches)
      pids.Add(match.Pid);

    var sensitivity = this._matchCase.Checked ? ", case-sensitive" : string.Empty;
    var found = this._matches.Count switch {
      0 => $"No references matched \"{pattern}\" ({mode}{sensitivity}).",
      1 => $"1 reference in 1 process ({mode}{sensitivity}). Double-click to go there.",
      _ => $"{this._matches.Count} references in {pids.Count} processes ({mode}{sensitivity}). Double-click one to go there.",
    };

    if (!this._deep.Checked || report.DeepAttempted == 0) {
      this._status.Text = found;
      return;
    }

    this._status.Text = report.IsComplete
      ? $"{found} Deep scan: {report.DeepScanned}/{report.DeepAttempted} processes read."
      : $"{found} Partial deep scan: {report.DeepScanned}/{report.DeepAttempted} processes read; the rest denied or exited.";
  }

  private static string Name(ReverseResourceKind kind) => kind switch {
    ReverseResourceKind.Process => "Process",
    ReverseResourceKind.CommandLine => "Command line",
    ReverseResourceKind.ImagePath => "Image",
    ReverseResourceKind.Handle => "Handle",
    ReverseResourceKind.Module => "Module",
    ReverseResourceKind.MappedFile => "Mapped file",
    ReverseResourceKind.Socket => "Socket",
    _ => "Service",
  };

  public void ApplyLayout() {
    var width = Math.Max(300, this.Width - (2 * _Margin));
    this._pattern.Bounds = new(_Margin, _Margin, width - 180, 26);
    this._search.Bounds = new(this.Width - _Margin - 170, _Margin, 80, 26);
    this._close.Bounds = new(this.Width - _Margin - 80, _Margin, 80, 26);
    // The two options share a row: the deep search is the one people turn off, so it keeps the left
    // edge, and the case toggle sits at the right where its label has room to be read whole.
    this._deep.Bounds = new(_Margin, _Margin + 32, Math.Max(260, width - 140), 20);
    this._matchCase.Bounds = new(this.Width - _Margin - 130, _Margin + 32, 130, 20);
    this._status.Bounds = new(_Margin, this.Height - _Margin - 18, width, 18);
    this._results.Bounds = new(_Margin, _Margin + 58, width, Math.Max(60, this.Height - _Margin - 88));
  }

}
