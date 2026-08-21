using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Which page the terminal is showing (PRD §57.1).</summary>
public enum TerminalPage : byte { Processes, Performance }

/// <summary>
/// The full-screen terminal front-end: per-core meters and a process list, driven by the same
/// engine the window is (PRD §11).
/// </summary>
public sealed class TerminalUi {

  private readonly Sampler _sampler;
  private readonly IProcessActions? _actions;
  private readonly ISystemProbe _probe;
  private readonly ProcessView _view = new();
  private readonly TerminalScreen _screen;
  private readonly HistoryRing<Rate> _cpuHistory = new(240);
  private readonly HistoryRing<Rate> _memoryHistory = new(240);
  private readonly HistoryRing<Rate> _swapHistory = new(240);
  private readonly Dictionary<ProcessKey, Counter> _handleCounts = [];
  private readonly DetailView _detail;
  private readonly ProcessHistory _rowHistory = new();
  private readonly ColumnLayout _columns;
  private readonly HashSet<ProcessKey> _marked = [];

  private int _selectedRow;
  private ProcessKey _selectedKey;
  private int _scrollOffset;
  private string _message = string.Empty;
  private byte _messageAttribute = Attributes.Dim;
  private InputMode _mode;
  private string _input = string.Empty;
  private ProcessKey _confirmTarget;
  private PendingAction _pending;
  private string? _highlight;
  private int _lowerPaneHeight;
  private ListOverlay? _overlay;
  private OverlayKind _overlayKind;
  private OverlayPlacement _overlayPlacement;
  private int _tableTop;
  private int _tableHeight;
  private int _headerRow;
  private int _paneTop;
  private bool _draggingDivider;

  private enum InputMode : byte { Normal, Search, Filter, Confirm, SchedulingClass, Detail, Overlay, ExportPath }

  /// <summary>Which list is on screen, so one set of keys can drive all three.</summary>
  private enum OverlayKind : byte { None, Actions, Columns, Help, Grouping, Interval }

  /// <summary>
  /// What the pending confirmation will do if it is answered yes.
  /// </summary>
  /// <remarks>
  /// The action is decided when the key is pressed and carried out when it is confirmed, so the
  /// prompt and the deed cannot disagree about which one was asked for — which a pair of booleans
  /// was one added action away from allowing.
  /// </remarks>
  private enum PendingAction : byte { None, Terminate, TerminateTree, Restart }

  public TerminalUi(Sampler sampler, ISystemProbe probe, IProcessActions? actions, int width, int height, ColorDepth depth) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);
    this._sampler = sampler;
    this._probe = probe;
    this._actions = actions;
    this._screen = new(width, height, depth);
    this._detail = new(probe);
    this._columns = new(Layout.ColumnsFor(width));
    this._view.TreeMode = false;
    this._view.SortColumn = ProcessField.CpuPercent;
    this._view.SortDescending = true;
  }

  public ProcessView View => this._view;

  public TerminalScreen Screen => this._screen;

  /// <summary>The columns, their order, their widths and which of them are pinned (PRD §11).</summary>
  public ColumnLayout Columns => this._columns;

  /// <summary>Which key does what. Replaceable, because §57.3 says the bindings are the user's.</summary>
  public KeyBindings Keys { get; set; } = KeyBindings.Default;

  /// <summary>True once the user has asked to leave.</summary>
  public bool ShouldQuit { get; private set; }

  /// <summary>Whether sampling is stopped (PRD §57.3, §12).</summary>
  public bool Paused { get; private set; }

  /// <summary>
  /// Whether the tick is off and a sample is asked for by hand (PRD §12).
  /// </summary>
  /// <remarks>
  /// Not the same as a pause, and kept apart from it for the reason the settings file gives: a pause
  /// is flipped for a few seconds to read a row that will not hold still, and asking to refresh by
  /// hand is a preference that outlives the session.
  /// </remarks>
  public bool ManualRefresh { get; private set; }

  /// <summary>
  /// How long the host waits between samples (PRD §12).
  /// </summary>
  /// <remarks>
  /// Lives here rather than in the host so that the picker can move it: the loop reads it each time
  /// round, so a rate chosen mid-session takes effect at the next sample rather than at the next
  /// start-up.
  /// </remarks>
  public int IntervalMilliseconds {
    get;
    set => field = Math.Clamp(value, 250, 60_000);
  } = 1000;

  /// <summary>Whether the host should take a sample at all, which pausing and manual both stop.</summary>
  public bool Sampling => !this.Paused && !this.ManualRefresh;

  /// <summary>Which page is showing.</summary>
  public TerminalPage Page { get; private set; }

  /// <summary>How many rows the user has ticked for a bulk action.</summary>
  public int MarkedCount => this._marked.Count;

  /// <summary>
  /// Which row the cursor is on.
  /// </summary>
  /// <remarks>
  /// Exposed so a test can assert what a person can see: that the cursor never comes to rest on a
  /// grouping heading, which is what makes a heading un-actionable (PRD §83).
  /// </remarks>
  public int SelectedRow => this._selectedRow;

  /// <summary>
  /// Where a copy is written: the terminal's own output, which is the only clipboard reachable from
  /// the far end of an SSH session (PRD §11). Null in a test, where nothing is attached.
  /// </summary>
  public TextWriter? ClipboardOutput { get; set; }

  /// <summary>The text of the last copy, so a test can check what a key would have put on it.</summary>
  public string? LastCopiedText { get; private set; }

  /// <summary>Where an export goes when the prompt is accepted unchanged.</summary>
  public string ExportPath { get; set; } = "procman-export.tsv";

  /// <summary>
  /// How the in-row histories are drawn (PRD §57.4).
  /// </summary>
  /// <remarks>
  /// Detected from the locale by default, and settable because detection is not something a golden
  /// frame may depend on: the frame is compared byte for byte against a checked-in file, and a test
  /// whose expected output changes with the machine's <c>LANG</c> is a test that fails on somebody
  /// else's CI for a reason that has nothing to do with the code. The capture path pins it.
  /// </remarks>
  public GraphStyle GraphStyle { get; set; } = BlockSparkline.TerminalHasBlocks ? GraphStyle.Blocks : GraphStyle.Ascii;

  /// <summary>Whether the drawing uses characters outside ASCII at all.</summary>
  public bool UseBlockCharacters {
    get => this.GraphStyle is GraphStyle.Blocks or GraphStyle.Braille;
    set => this.GraphStyle = value ? GraphStyle.Blocks : GraphStyle.Ascii;
  }

  /// <summary>
  /// Whether the status bar carries the sample cost. On for a person watching, off for a captured
  /// frame — a millisecond figure is the one thing in the frame that differs between two runs of
  /// identical input, and a golden test with a moving number in it is a golden test nobody keeps
  /// (PRD §9.6).
  /// </summary>
  public bool ShowTiming { get; set; } = true;

  /// <summary>
  /// Resizes, and re-picks the columns unless somebody has chosen their own.
  /// </summary>
  /// <remarks>
  /// The re-pick is the whole of the responsive layout (PRD §57.1): the same table, with the columns
  /// that are worth the width there is. It stops the moment a person touches a column, because a
  /// layout that undoes what you just did every time the window changes is worse than a narrow one.
  /// </remarks>
  public void Resize(int width, int height) {
    var before = this._screen.Width;
    this._screen.Resize(width, height);
    if (!this._columns.Customised && Layout.BreakpointFor(before) != Layout.BreakpointFor(this._screen.Width))
      this._columns.Apply(Layout.ColumnsFor(this._screen.Width), asDefault: true);
  }

  /// <summary>Takes a sample and composes the next frame. Does not write to any terminal.</summary>
  public void Update() {
    this._sampler.Sample();
    this._cpuHistory.Add(this._sampler.Delta.SystemCpuPercent);
    this._memoryHistory.Add(this.MemoryPercent());
    this._swapHistory.Add(this.SwapPercent());
    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    this.ForgetMarksOfProcessesThatEnded();
    // History for the rows on screen only, plus a little either side so a small scroll does not
    // start every plot from nothing (PRD §3.3).
    this._rowHistory.Update(
      this._sampler.Current,
      this._sampler.Delta,
      this._view,
      Math.Max(0, this._scrollOffset - 4),
      this.ListHeight + 8
    );

    this.Compose();
  }

  /// <summary>Recomposes without sampling — for a keypress that only changes what is shown.</summary>
  public void Refresh() {
    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    this.Compose();
  }

  public void Flush(TextWriter writer) => this._screen.Flush(writer);

  #region selection

  private void RestoreSelection() {
    // The selection follows the process, not the row number. Without this a re-sort between two
    // samples moves whatever is under the cursor, and the next keystroke acts on the wrong program
    // (PRD §7.3).
    if (!this._selectedKey.IsNone) {
      var row = this._view.FindRow(this._selectedKey);
      if (row >= 0) {
        this._selectedRow = row;
        this.ClampScroll();
        return;
      }
    }

    this._selectedRow = this.NearestProcessRow(this._selectedRow, 1);
    this._selectedKey = this.KeyAt(this._selectedRow);
    this.ClampScroll();
  }

  /// <summary>
  /// Drops the ticks of processes that have gone.
  /// </summary>
  /// <remarks>
  /// A tick is a promise that a bulk action will act on that row. A process that has exited cannot be
  /// acted on, and leaving its tick would put a number on the status bar that counts rows nobody can
  /// see — and, worse, a confirmation that says "the 12 ticked processes" when there are nine.
  /// </remarks>
  private void ForgetMarksOfProcessesThatEnded() {
    if (this._marked.Count == 0)
      return;

    var snapshot = this._sampler.Current;
    this._marked.RemoveWhere(key => !snapshot.TryGetProcess(key, out _));
  }

  /// <summary>
  /// The process on a row, or none when the row is a grouping heading (PRD §83).
  /// </summary>
  /// <remarks>
  /// A heading answers <see cref="ProcessKey.None"/>, and every action in this front-end already
  /// refuses that with "nothing selected". That is the whole guard: ending a heading is impossible
  /// rather than discouraged, because there is nothing for the request to name.
  /// </remarks>
  private ProcessKey KeyAt(int row) {
    if ((uint)row >= (uint)this._view.RowCount || this._view.Rows[row].IsGroupHeader)
      return ProcessKey.None;

    return this._sampler.Current.Processes[this._view.Rows[row].Index].Key;
  }

  /// <summary>The nearest row that is a process, searching in one direction and then the other.</summary>
  private int NearestProcessRow(int row, int step) {
    var rows = this._view.Rows;
    if (rows.Length == 0)
      return 0;

    row = Math.Clamp(row, 0, rows.Length - 1);
    for (var i = row; (uint)i < (uint)rows.Length; i += step)
      if (!rows[i].IsGroupHeader)
        return i;

    // The list may end in a heading with nothing under it — a group whose rows are all folded away —
    // so a search that ran off one end tries the other rather than parking on the heading.
    for (var i = row; (uint)i < (uint)rows.Length; i -= step)
      if (!rows[i].IsGroupHeader)
        return i;

    return row;
  }

  /// <summary>How many rows of processes there is room for, once everything else has its lines.</summary>
  private int ListHeight {
    get {
      var bottom = this._lowerPaneHeight > 0
        ? this._screen.Height - 1 - this._lowerPaneHeight - 1
        : this._screen.Height - 1;

      return Math.Max(1, bottom - (this.MeterTop + this.MeterLines + this.BlankLines));
    }
  }

  /// <summary>The first line the meters may use — under the tab row.</summary>
  private int MeterTop => 1;

  /// <summary>A blank line between the meters and the table, where the height can afford one.</summary>
  private int BlankLines => this._screen.Height >= 24 ? 2 : 1;

  /// <summary>
  /// How many meters share a line, or zero for the aggregate.
  /// </summary>
  /// <remarks>
  /// A narrow terminal gets one bar for the whole machine rather than sixty-four bars four characters
  /// wide. So does a short one with many cores: the meters may not eat the table, because the table
  /// is what somebody opened this for (PRD §57.1).
  /// </remarks>
  private int MetersPerLine {
    get {
      var width = this._screen.Width;
      var cores = Math.Max(1, this._sampler.Delta.PerCoreCount);
      var perLine = width >= 160 ? 4 : width >= 80 ? 2 : 0;
      if (perLine == 0)
        return 0;

      // A third of the screen is the most the meters may take. Past that they become the aggregate.
      return (cores + perLine - 1) / perLine > Math.Max(2, this._screen.Height / 3) ? 0 : perLine;
    }
  }

  private int MeterLines {
    get {
      var cores = Math.Max(1, this._sampler.Delta.PerCoreCount);
      var perLine = this.MetersPerLine;
      // The aggregate form is one CPU bar, then memory and swap on a line each; the wide one packs
      // the cores and puts memory and swap together.
      return perLine == 0 ? 4 : (cores + perLine - 1) / perLine + 2;
    }
  }

  private void ClampScroll() {
    var height = this.ListHeight;
    if (this._selectedRow < this._scrollOffset)
      this._scrollOffset = this._selectedRow;
    else if (this._selectedRow >= this._scrollOffset + height)
      this._scrollOffset = this._selectedRow - height + 1;

    this._scrollOffset = Math.Clamp(this._scrollOffset, 0, Math.Max(0, this._view.RowCount - height));
  }

  #endregion

  #region input

  /// <summary>Handles one key. Returns true when the frame needs recomposing.</summary>
  public bool HandleKey(ConsoleKeyInfo key) {
    switch (this._mode) {
      case InputMode.Search or InputMode.Filter or InputMode.ExportPath:
        return this.HandleTextInput(key);
      case InputMode.Confirm:
        return this.HandleConfirm(key);
      case InputMode.SchedulingClass:
        return this.HandleSchedulingClass(key);
      case InputMode.Detail:
        return this.HandleDetail(key);
      case InputMode.Overlay:
        return this.HandleOverlay(key);
      default:
        return this.Execute(this.Keys.Resolve(key));
    }
  }

  /// <summary>
  /// Does one action, whatever asked for it.
  /// </summary>
  /// <remarks>
  /// A key, a menu line and a click all end up here, so the three cannot drift into doing slightly
  /// different things — which is the failure mode of every UI that grew a context menu after its
  /// keyboard (PRD §58).
  /// </remarks>
  private bool Execute(TerminalAction action) {
    switch (action) {
      case TerminalAction.MoveUp: this.MoveSelection(-1); return true;
      case TerminalAction.MoveDown: this.MoveSelection(1); return true;
      case TerminalAction.PageUp: this.MoveSelection(-this.ListHeight); return true;
      case TerminalAction.PageDown: this.MoveSelection(this.ListHeight); return true;
      case TerminalAction.MoveFirst: this.MoveSelection(int.MinValue / 2); return true;
      case TerminalAction.MoveLast: this.MoveSelection(int.MaxValue / 2); return true;
      case TerminalAction.Collapse: this.Collapse(); return true;
      case TerminalAction.Expand: this.Expand(); return true;
      case TerminalAction.Details: this.OpenDetail(DetailTab.Overview); return true;
      case TerminalAction.Quit: this.ShouldQuit = true; return false;

      case TerminalAction.ToggleTree: this._view.TreeMode = !this._view.TreeMode; return true;
      case TerminalAction.Pause:
        this.Paused = !this.Paused;
        this.Say(this.Paused ? "sampling paused" : "sampling again", Attributes.Accent);
        return true;
      case TerminalAction.RefreshNow: this.Update(); return false;
      case TerminalAction.RefreshInterval: this.OpenIntervalMenu(); return true;
      case TerminalAction.CpuMode: this.ToggleCpuMode(); return true;
      case TerminalAction.UserFilter: this.ToggleUserFilter(); return true;
      case TerminalAction.Search: this.BeginInput(InputMode.Search); return true;
      case TerminalAction.Filter: this.BeginInput(InputMode.Filter); return true;
      case TerminalAction.CaseSensitive: this.ToggleCaseSensitivity(); return true;
      case TerminalAction.Graphs: this.TogglePage(); return true;
      case TerminalAction.LowerPane: this.ToggleLowerPane(); return true;
      case TerminalAction.PaneGrow: this.ResizePane(1); return true;
      case TerminalAction.PaneShrink: this.ResizePane(-1); return true;
      case TerminalAction.Help: this.OpenHelp(); return true;
      case TerminalAction.GroupBy: this.OpenGroupingMenu(); return true;

      case TerminalAction.SortNext: this.SetSortColumn(1); return true;
      case TerminalAction.SortPrevious: this.SetSortColumn(-1); return true;
      case TerminalAction.SortReverse: this._view.SortDescending = !this._view.SortDescending; return true;
      case TerminalAction.SortAlso: this.AddSortKey(this._columns.CurrentField); return true;
      case TerminalAction.SortByCpu: this.SortBy(ProcessField.CpuPercent, true); return true;
      case TerminalAction.SortByMemory: this.SortBy(ProcessField.PrivateBytes, true); return true;
      case TerminalAction.SortByPid: this.SortBy(ProcessField.Pid, false); return true;

      case TerminalAction.ColumnPrevious: this._columns.MoveCurrent(-1); this.EnsureColumnVisible(); return true;
      case TerminalAction.ColumnNext: this._columns.MoveCurrent(1); this.EnsureColumnVisible(); return true;
      case TerminalAction.ColumnMoveLeft: this._columns.Reorder(-1); this.EnsureColumnVisible(); return true;
      case TerminalAction.ColumnMoveRight: this._columns.Reorder(1); this.EnsureColumnVisible(); return true;
      case TerminalAction.ColumnNarrower: this._columns.ResizeCurrent(-1); return true;
      case TerminalAction.ColumnWider: this._columns.ResizeCurrent(1); return true;
      case TerminalAction.ColumnAutoSize: this.AutoSize(this._columns.Current); return true;
      case TerminalAction.ColumnAutoSizeAll: this.AutoSizeAll(); return true;
      case TerminalAction.ColumnFreeze: this.Freeze(); return true;
      case TerminalAction.ColumnReset:
        this._columns.Reset();
        this.Say("columns are back to the defaults", Attributes.Accent);
        return true;
      case TerminalAction.ColumnChooser: this.OpenColumnChooser(); return true;
      case TerminalAction.ScrollLeft: this._columns.ScrollBy(-1); return true;
      case TerminalAction.ScrollRight: this._columns.ScrollBy(1); return true;

      case TerminalAction.MarkToggle: this.ToggleMark(); return true;
      case TerminalAction.MarkAll: this.MarkAll(); return true;
      case TerminalAction.MarkInvert: this.InvertMarks(); return true;
      case TerminalAction.MarkNone:
        this._marked.Clear();
        this.Say("nothing is ticked now", Attributes.Dim);
        return true;
      case TerminalAction.CopyCell: this.CopyCell(); return true;
      case TerminalAction.CopyRow: this.CopyRows(); return true;
      case TerminalAction.CopyColumn: this.CopyColumn(); return true;
      case TerminalAction.Export: this.BeginInput(InputMode.ExportPath); return true;

      case TerminalAction.ActionMenu: this.OpenActionMenu(); return true;
      case TerminalAction.EndTask: this.EndTask(); return true;
      case TerminalAction.Terminate: this.Confirm(PendingAction.Terminate); return true;
      case TerminalAction.TerminateTree: this.Confirm(PendingAction.TerminateTree); return true;
      case TerminalAction.Restart: this.Confirm(PendingAction.Restart); return true;
      case TerminalAction.SuspendResume: this.SuspendOrResume(); return true;
      case TerminalAction.SchedulingClass: this.BeginSchedulingClass(); return true;
      case TerminalAction.CountHandles: this.FillHandleCounts(); return true;
      case TerminalAction.Threads: this.OpenDetail(DetailTab.Threads); return true;
      case TerminalAction.Modules: this.OpenDetail(DetailTab.Modules); return true;
      case TerminalAction.Handles: this.OpenDetail(DetailTab.Handles); return true;
      case TerminalAction.Network: this.OpenDetail(DetailTab.Network); return true;

      default: return false;
    }
  }

  private bool HandleTextInput(ConsoleKeyInfo key) {
    switch (key.Key) {
      case ConsoleKey.Escape:
        if (this._mode != InputMode.ExportPath) {
          this._input = string.Empty;
          this.ApplyInput();
        }

        this._mode = InputMode.Normal;
        return true;
      case ConsoleKey.Enter:
        if (this._mode == InputMode.ExportPath)
          this.ExportTo(this._input);

        this._mode = InputMode.Normal;
        return true;
      case ConsoleKey.Backspace:
        if (this._input.Length > 0)
          this._input = this._input[..^1];

        this.ApplyInput();
        return true;
    }

    if (char.IsControl(key.KeyChar))
      return false;

    this._input += key.KeyChar;
    this.ApplyInput();
    return true;
  }

  /// <summary>
  /// What typing into the prompt does, which is not the same for the two of them.
  /// </summary>
  /// <remarks>
  /// Search moves the cursor to the first row that matches and leaves everything else on screen;
  /// filter hides the rows that do not. Both highlight what they matched, which is the only way to
  /// see *why* a row is a match when the word is in a command line forty characters wide (PRD §11).
  /// </remarks>
  private void ApplyInput() {
    switch (this._mode) {
      case InputMode.Search:
        this._highlight = this._input.Length > 0 ? this._input : null;
        this.JumpToMatch();
        return;
      case InputMode.Filter:
        this._view.TextFilter = string.IsNullOrEmpty(this._input) ? null : this._input;
        this._highlight = this._input.Length > 0 && this._input.AsSpan().IndexOfAny(":<>=/") < 0 ? this._input : null;
        if (this._input.Length > 0 && !ProcessQuery.TryParse(this._input, out _, out var error, this._view.CaseSensitive) && error is not null)
          this.Say(error, Attributes.Dim);
        else
          this.Say(string.Empty, Attributes.Dim);

        return;
      default:
        return;
    }
  }

  private void JumpToMatch() {
    if (this._highlight is not { Length: > 0 } needle)
      return;

    var comparison = this._view.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    var processes = this._sampler.Current.Processes;
    var rows = this._view.Rows;
    for (var i = 0; i < rows.Length; ++i) {
      if (rows[i].IsGroupHeader)
        continue;

      ref readonly var process = ref processes[rows[i].Index];
      if (!process.Name.Contains(needle, comparison)
          && (process.CommandLine is null || !process.CommandLine.Contains(needle, comparison)))
        continue;

      this._selectedRow = i;
      this._selectedKey = this.KeyAt(i);
      this.ClampScroll();
      return;
    }
  }

  private void OpenDetail(DetailTab tab) {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    this._detail.Open(this._selectedKey);
    this._detail.GoTo(tab);
    this._mode = InputMode.Detail;
    this._screen.NeedsFullRepaint = true;
  }

  private bool HandleDetail(ConsoleKeyInfo key) {
    var page = Math.Max(1, this._screen.Height - 4);
    switch (key.Key) {
      case ConsoleKey.Escape or ConsoleKey.Backspace:
        this._mode = InputMode.Normal;
        this._screen.NeedsFullRepaint = true;
        return true;
      case ConsoleKey.Tab or ConsoleKey.RightArrow: this._detail.NextTab(); return true;
      case ConsoleKey.LeftArrow: this._detail.PreviousTab(); return true;
      case ConsoleKey.UpArrow: this._detail.ScrollBy(-1, page); return true;
      case ConsoleKey.DownArrow: this._detail.ScrollBy(1, page); return true;
      case ConsoleKey.PageUp: this._detail.ScrollBy(-page, page); return true;
      case ConsoleKey.PageDown: this._detail.ScrollBy(page, page); return true;
    }

    switch (key.KeyChar) {
      case 'q' or 'i':
        this._mode = InputMode.Normal;
        this._screen.NeedsFullRepaint = true;
        return true;
      default: return false;
    }
  }

  private bool HandleConfirm(ConsoleKeyInfo key) {
    var pending = this._pending;
    this._mode = InputMode.Normal;
    this._pending = PendingAction.None;
    if (key.KeyChar is not ('y' or 'Y')) {
      this.Say("cancelled", Attributes.Dim);
      return true;
    }

    if (this._actions is null) {
      this.Say("no actions are available in this build", Attributes.Bad);
      return true;
    }

    switch (pending) {
      case PendingAction.TerminateTree:
        this.KillTree(this._confirmTarget);
        return true;
      case PendingAction.Restart: {
        var restarted = this._actions.Restart(this._confirmTarget);
        this.Report(
          restarted.Outcome,
          $"{this._confirmTarget.Pid} started again as {restarted.Pid}"
        );

        return true;
      }
      default: {
        // A tick on more than one row makes this a bulk request, and the prompt already said so.
        if (this._marked.Count > 0) {
          this.TerminateMarked();
          return true;
        }

        var result = this._actions.Terminate(this._confirmTarget);
        this.Report(result, $"sent SIGTERM to {this._confirmTarget.Pid}");
        return true;
      }
    }
  }

  /// <summary>
  /// Picks a scheduler class for the selected process with one more key (PRD §25.2).
  /// </summary>
  /// <remarks>
  /// The initial of each class rather than a numbered list, because the classes have initials that
  /// do not collide and a number would have to be looked up in the prompt every time.
  /// </remarks>
  private bool HandleSchedulingClass(ConsoleKeyInfo key) {
    this._mode = InputMode.Normal;
    var chosen = char.ToLowerInvariant(key.KeyChar) switch {
      'o' => SchedulingPolicy.Other,
      'b' => SchedulingPolicy.Batch,
      'i' => SchedulingPolicy.Idle,
      'r' => SchedulingPolicy.RoundRobin,
      'f' => SchedulingPolicy.Fifo,
      _ => SchedulingPolicy.Unknown,
    };

    if (chosen == SchedulingPolicy.Unknown) {
      this.Say("cancelled", Attributes.Dim);
      return true;
    }

    if (this._actions is null) {
      this.Say("no actions are available in this build", Attributes.Bad);
      return true;
    }

    // The real-time classes take their lowest static priority; anything above it is a decision for a
    // prompt rather than for one keystroke in a list (PRD §68).
    var priority = chosen is SchedulingPolicy.Fifo or SchedulingPolicy.RoundRobin ? 1 : 0;
    this.Report(
      this._actions.SetSchedulingClass(this._confirmTarget, chosen, priority),
      $"{this._confirmTarget.Pid} now runs under {Humanize.SchedulingPolicy(chosen)}"
    );

    return true;
  }

  private bool HandleOverlay(ConsoleKeyInfo key) {
    if (this._overlay is null) {
      this._mode = InputMode.Normal;
      return true;
    }

    var page = Math.Max(1, this._screen.Height - 6);
    switch (key.Key) {
      case ConsoleKey.UpArrow: this._overlay.MoveBy(-1); return true;
      case ConsoleKey.DownArrow: this._overlay.MoveBy(1); return true;
      case ConsoleKey.PageUp: this._overlay.MoveBy(-page); return true;
      case ConsoleKey.PageDown: this._overlay.MoveBy(page); return true;
      case ConsoleKey.Home: this._overlay.MoveBy(int.MinValue / 2); return true;
      case ConsoleKey.End: this._overlay.MoveBy(int.MaxValue / 2); return true;
      case ConsoleKey.Escape: this.CloseOverlay(); return true;
      case ConsoleKey.Enter or ConsoleKey.Spacebar: return this.ChooseFromOverlay();
    }

    return key.KeyChar switch {
      'q' or '?' => this.CloseOverlay(),
      _ => false,
    };
  }

  private bool CloseOverlay() {
    this._overlay = null;
    this._overlayKind = OverlayKind.None;
    this._mode = InputMode.Normal;
    this._screen.NeedsFullRepaint = true;
    return true;
  }

  private bool ChooseFromOverlay() {
    if (this._overlay?.Current is not { } item)
      return this.CloseOverlay();

    switch (this._overlayKind) {
      case OverlayKind.Actions:
        this.CloseOverlay();
        this.Execute((TerminalAction)item.Tag);
        return true;

      case OverlayKind.Grouping:
        this.GroupBy((ProcessGrouping)item.Tag);
        return this.CloseOverlay();

      case OverlayKind.Interval:
        this.SetRefresh(item.Tag);
        return this.CloseOverlay();

      case OverlayKind.Columns:
        // The sets come first in the list and are marked with a negative tag, so one list can offer
        // "everything about memory" and "this one column" without being two lists.
        if (item.Tag < 0)
          this.ApplyColumnSet(item.Label);
        else {
          this._columns.ToggleVisible(item.Tag);
          this._overlay.Replace(this._overlay.Selected, item with { Checked = this._columns.IsVisible(item.Tag) });
          return true;
        }

        return this.CloseOverlay();

      default:
        return this.CloseOverlay();
    }
  }

  /// <summary>
  /// A mouse report, in cells (PRD §57.5).
  /// </summary>
  /// <remarks>
  /// Every click resolves through the same placement the drawing used, so a header that moved
  /// because a column was pinned is still the header that gets clicked. Nothing here is the only way
  /// to reach anything: the mouse is a shortcut for keys that all exist (§57.5, last box).
  /// </remarks>
  public bool HandleMouse(MouseEvent mouse) {
    if (this._mode == InputMode.Overlay && this._overlay is not null) {
      if (mouse.Button != MouseButton.Left || !mouse.Pressed)
        return false;

      var line = this._overlay.HitTest(this._overlayPlacement, mouse.X, mouse.Y);
      if (line < 0)
        return this.CloseOverlay();

      this._overlay.MoveTo(line);
      return this.ChooseFromOverlay();
    }

    if (this._mode != InputMode.Normal)
      return false;

    switch (mouse.Button) {
      case MouseButton.WheelUp: this.ScrollBy(-3); return true;
      case MouseButton.WheelDown: this.ScrollBy(3); return true;
    }

    if (!mouse.Pressed) {
      this._draggingDivider = false;
      return false;
    }

    // The tab row, which is the only thing above the meters.
    if (mouse.Y == 0) {
      this.SelectTabAt(mouse.X);
      return true;
    }

    if (this.Page != TerminalPage.Processes)
      return false;

    // The divider above the lower pane. A drag is a press on it and then motion somewhere else, so
    // the press has to be remembered: by the time the pointer has moved, the divider is no longer
    // under it — which is exactly what dragging means.
    if (this._lowerPaneHeight > 0 && !mouse.Motion && mouse.Y == this._paneTop) {
      this._draggingDivider = true;
      return false;
    }

    if (this._draggingDivider && mouse.Motion) {
      this._lowerPaneHeight = Math.Clamp(this._screen.Height - 2 - mouse.Y, 2, Math.Max(2, this._screen.Height / 2));
      this.ClampScroll();
      return true;
    }

    if (mouse.Y == this._headerRow) {
      var column = this._columns.HitTest(this._screen.Width - 1, mouse.X - 1);
      if (column < 0)
        return false;

      this._columns.SetCurrent(column);
      var field = this._columns.FieldAt(column);
      if (!FieldRegistry.Get(field).IsSortable)
        return true;

      // Shift-click adds a tie-breaker rather than replacing the sort, which is the one gesture
      // every table with a multi-column sort has agreed on.
      if (mouse.Shift)
        this.AddSortKey(field);
      else
        this.SortBy(field, FieldRegistry.Get(field).PrefersDescending);

      return true;
    }

    if (mouse.Y < this._tableTop || mouse.Y >= this._tableTop + this._tableHeight)
      return false;

    var row = this._scrollOffset + (mouse.Y - this._tableTop);
    if (row >= this._view.RowCount)
      return false;

    // A heading is not a row that can be selected, so clicking one folds it instead — which is the
    // only thing there is to do to a heading (PRD §83).
    if (this._view.Rows[row].IsGroupHeader) {
      var group = this._view.Rows[row].Group;
      return this.ToggleGroup(group, !this._view.IsGroupCollapsed(this._view.Groups[group].Label));
    }

    this._selectedRow = row;
    this._selectedKey = this.KeyAt(row);
    if (mouse.X > 0) {
      var column = this._columns.HitTest(this._screen.Width - 1, mouse.X - 1);
      if (column >= 0)
        this._columns.SetCurrent(column);
    }

    switch (mouse.Button) {
      // The gutter is where the ticks are drawn, so clicking it is how one is set.
      case MouseButton.Left when mouse.X == 0 || mouse.Control: this.ToggleMark(); return true;
      case MouseButton.Right: this.OpenActionMenu(); return true;
      case MouseButton.Middle: this.OpenDetail(DetailTab.Overview); return true;
      default: return true;
    }
  }

  private void SelectTabAt(int x) {
    var processes = TabLabel(TerminalPage.Processes);
    this.Page = x < processes.Length ? TerminalPage.Processes : TerminalPage.Performance;
    this._screen.NeedsFullRepaint = true;
  }

  private void ScrollBy(int delta) {
    this._scrollOffset = Math.Clamp(this._scrollOffset + delta, 0, Math.Max(0, this._view.RowCount - this.ListHeight));
    // The selection comes with the view rather than being left behind off screen: a key pressed
    // after a scroll should act on something visible.
    this._selectedRow = Math.Clamp(this._selectedRow, this._scrollOffset, Math.Max(this._scrollOffset, this._scrollOffset + this.ListHeight - 1));
    this._selectedRow = this.NearestProcessRow(this._selectedRow, delta < 0 ? -1 : 1);
    this._selectedKey = this.KeyAt(this._selectedRow);
  }

  /// <summary>
  /// Asks the program to close rather than telling it to (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// Not confirmed, and that is the distinction from <c>k</c>: this one asks, the program may put up
  /// its own dialog and may decline, and nothing has been lost if it does.
  /// </remarks>
  private void EndTask() {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    if (this._actions is null) {
      this.Say("no actions are available in this build", Attributes.Bad);
      return;
    }

    this.Report(this._actions.EndTask(this._selectedKey), $"asked {this._selectedKey.Pid} to end");
  }

  private void SuspendOrResume() {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    if (this._actions is null) {
      this.Say("no actions are available in this build", Attributes.Bad);
      return;
    }

    var stopped = this._sampler.Current.TryGetProcess(this._selectedKey, out var process) && process.State == ProcessState.Stopped;
    this.Report(
      stopped ? this._actions.Resume(this._selectedKey) : this._actions.Suspend(this._selectedKey),
      stopped ? $"{this._selectedKey.Pid} is running again" : $"{this._selectedKey.Pid} is suspended"
    );
  }

  /// <summary>
  /// Puts an action's answer on the status line, preferring what the action itself had to say.
  /// </summary>
  /// <remarks>
  /// A success with a detail is a success that carries information the outcome does not — "its
  /// window was asked to close" against "it has no window, so SIGTERM was sent" — and losing it in
  /// favour of a generic sentence would throw away the only part worth reading (PRD §72.3).
  /// </remarks>
  private void Report(ActionResult result, string succeeded) {
    if (!result.Succeeded) {
      this.Say(result.Detail ?? "failed", Attributes.Bad);
      return;
    }

    this.Say(result.Detail is { Length: > 0 } detail ? detail : succeeded, Attributes.Good);
  }

  private void MoveSelection(int delta) {
    if (this._view.RowCount == 0)
      return;

    // Past the heading rather than onto it: an arrow key that lands the cursor on something no key
    // can act on reads as a list that skipped a row (PRD §83).
    var wanted = Math.Clamp(this._selectedRow + delta, 0, this._view.RowCount - 1);
    this._selectedRow = this.NearestProcessRow(wanted, delta < 0 ? -1 : 1);
    this._selectedKey = this.KeyAt(this._selectedRow);
    this.ClampScroll();
  }

  /// <summary>Hides this row's children, or steps out to its parent when it has none showing.</summary>
  private void Collapse() {
    if (this.FoldGroup(true))
      return;

    if (!this._view.TreeMode || this._selectedRow >= this._view.RowCount)
      return;

    var rows = this._view.Rows;
    var row = rows[this._selectedRow];
    var pid = this._sampler.Current.Processes[row.Index].Pid;
    if (row.HasChildren && !this._view.IsCollapsed(pid)) {
      this._view.SetCollapsed(pid, true);
      this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
      this.RestoreSelection();
      return;
    }

    // Nothing to fold here, so the arrow means "out one level" — which is what it means in every
    // tree that has ever been drawn in a terminal.
    for (var i = this._selectedRow - 1; i >= 0; --i)
      if (rows[i].Depth < row.Depth) {
        this._selectedRow = i;
        this._selectedKey = this.KeyAt(i);
        this.ClampScroll();
        return;
      }
  }

  private void Expand() {
    if (this.FoldGroup(false))
      return;

    if (!this._view.TreeMode || this._selectedRow >= this._view.RowCount)
      return;

    var row = this._view.Rows[this._selectedRow];
    var pid = this._sampler.Current.Processes[row.Index].Pid;
    if (!this._view.SetCollapsed(pid, false))
      return;

    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
  }

  /// <summary>
  /// Folds the heading the cursor is under, or opens it (PRD §83).
  /// </summary>
  /// <remarks>
  /// The left and right arrows already mean "fold" and "open" in the tree, so a grouped list gives
  /// them the same meaning rather than a second pair of keys. The cursor never sits on a heading, so
  /// the group being folded is the one the selected process is in.
  /// </remarks>
  /// <returns>False when there is nothing grouped, so the caller falls through to the tree.</returns>
  private bool FoldGroup(bool collapsed) {
    if (this._view.Grouping is ProcessGrouping.None or ProcessGrouping.ParentTree)
      return false;

    if ((uint)this._selectedRow >= (uint)this._view.RowCount)
      return true;

    var group = this._view.Rows[this._selectedRow].Group;
    if ((uint)group >= (uint)this._view.Groups.Count)
      return true;

    // Opening is not quite the mirror of folding. Folding a group takes the cursor's row away with
    // it, so the cursor ends up in the *next* group — and pressing the other arrow would then open
    // something that was never shut. So an open request that has nothing to do here looks upwards
    // for the nearest heading that is folded, which is the one that was just closed.
    if (!collapsed && !this._view.IsGroupCollapsed(this._view.Groups[group].Label)) {
      if (this.NearestFoldedGroupAbove() is not { } folded)
        return true;

      group = folded;
    }

    return this.ToggleGroup(group, collapsed);
  }

  private int? NearestFoldedGroupAbove() {
    for (var row = Math.Min(this._selectedRow, this._view.RowCount - 1); row >= 0; --row) {
      if (!this._view.Rows[row].IsGroupHeader)
        continue;

      var candidate = this._view.Rows[row].Group;
      if (this._view.IsGroupCollapsed(this._view.Groups[candidate].Label))
        return candidate;
    }

    return null;
  }

  private bool ToggleGroup(int group, bool collapsed) {
    var label = this._view.Groups[group].Label;
    if (!this._view.SetGroupCollapsed(label, collapsed))
      return true;

    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    this.Say(collapsed ? $"{label} is folded away" : $"{label} is open again", Attributes.Dim);
    return true;
  }

  private void BeginInput(InputMode mode) {
    this._mode = mode;
    this._input = mode switch {
      InputMode.Filter => this._view.TextFilter ?? string.Empty,
      InputMode.ExportPath => this.ExportPath,
      _ => this._highlight ?? string.Empty,
    };
  }

  private void ToggleCpuMode() {
    this._sampler.CpuPercentMode = this._sampler.CpuPercentMode == CpuPercentMode.Normalized
      ? CpuPercentMode.PerCore
      : CpuPercentMode.Normalized;
    this.Say(
      $"CPU% is now {(this._sampler.CpuPercentMode == CpuPercentMode.PerCore ? "per core (100% = one core)" : "normalized (100% = whole machine)")}",
      Attributes.Accent
    );
  }

  private void ToggleCaseSensitivity() {
    this._view.CaseSensitive = !this._view.CaseSensitive;
    this.Say(
      this._view.CaseSensitive ? "matching case from now on" : "ignoring case again",
      Attributes.Accent
    );
  }

  private void TogglePage() {
    this.Page = this.Page == TerminalPage.Processes ? TerminalPage.Performance : TerminalPage.Processes;
    this._screen.NeedsFullRepaint = true;
  }

  private void ToggleLowerPane() {
    this._lowerPaneHeight = this._lowerPaneHeight > 0 ? 0 : Math.Clamp(this._screen.Height / 5, 3, 8);
    this.ClampScroll();
  }

  private void ResizePane(int delta) {
    if (this._lowerPaneHeight == 0) {
      this.Say($"the lower pane is hidden — {this.Keys.KeysFor(TerminalAction.LowerPane)} shows it", Attributes.Dim);
      return;
    }

    this._lowerPaneHeight = Math.Clamp(this._lowerPaneHeight + delta, 2, Math.Max(2, this._screen.Height / 2));
    this.ClampScroll();
  }

  private void ToggleUserFilter() {
    this._view.UserIdFilter = this._view.UserIdFilter is null ? CurrentUserId() : null;
    this.Say(
      this._view.UserIdFilter is null ? "showing every user" : "showing only your processes",
      Attributes.Accent
    );
  }

  private static int CurrentUserId() {
    // The uid of whatever this process is; the probe reports the same number for our own row.
    foreach (var name in (ReadOnlySpan<string>)["UID", "USER_ID"])
      if (int.TryParse(Environment.GetEnvironmentVariable(name), out var value))
        return value;

    return OperatingSystem.IsWindows() ? -1 : ReadOwnUid();
  }

  private static int ReadOwnUid() {
    try {
      foreach (var line in File.ReadLines("/proc/self/status")) {
        if (!line.StartsWith("Uid:", StringComparison.Ordinal))
          continue;

        var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 0 && int.TryParse(fields[0].Trim(), out var uid))
          return uid;
      }
    } catch (IOException) {
      // Falls through to the sentinel, which filters nothing.
    }

    return -1;
  }

  private void SortBy(ProcessField field, bool descending) {
    this._view.SortColumn = field;
    this._view.SortDescending = descending;
    this._view.ClearSecondarySort();
  }

  private void AddSortKey(ProcessField field) {
    var descriptor = FieldRegistry.Get(field);
    if (!descriptor.IsSortable) {
      this.Say($"{descriptor.Header} is a plot, and plots have no order", Attributes.Dim);
      return;
    }

    this._view.AddSortKey(field, descriptor.PrefersDescending);
    this.Say($"ties are broken by {descriptor.Header} now", Attributes.Accent);
  }

  private void SetSortColumn(int direction) {
    var columns = this._columns.Sortable;
    if (columns.Length == 0)
      return;

    var index = Array.IndexOf(columns, this._view.SortColumn);
    index = ((index < 0 ? 0 : index) + direction + columns.Length) % columns.Length;
    this.SortBy(columns[index], FieldRegistry.Get(columns[index]).PrefersDescending);
    this.Say($"sorted by {FieldRegistry.Get(columns[index]).Header}", Attributes.Accent);
  }

  private void EnsureColumnVisible() => this._columns.EnsureCurrentVisible(this._screen.Width - 1);

  private void Freeze() {
    this._columns.ToggleFreeze();
    this.Say(
      this._columns.Frozen > 0
        ? $"the first {this._columns.Frozen} column(s) stay put"
        : "no columns are pinned",
      Attributes.Accent
    );
  }

  /// <summary>Fits one column to the widest value among the rows on screen (PRD §11).</summary>
  private void AutoSize(int column) {
    this._columns.AutoSize(column, this.MeasureColumn(column));
    this.EnsureColumnVisible();
  }

  private void AutoSizeAll() {
    for (var i = 0; i < this._columns.Count; ++i)
      this._columns.AutoSize(i, this.MeasureColumn(i));

    this.Say("every column fits what is on screen", Attributes.Accent);
  }

  private int MeasureColumn(int column) {
    var field = this._columns.FieldAt(column);
    var widest = FieldRegistry.Get(field).ShortHeader.Length + 1;
    var processes = this._sampler.Current.Processes;
    var rows = this._view.Rows;
    var last = Math.Min(rows.Length, this._scrollOffset + this.ListHeight);
    for (var i = this._scrollOffset; i < last; ++i) {
      if (rows[i].IsGroupHeader)
        continue;

      ref readonly var process = ref processes[rows[i].Index];
      widest = Math.Max(widest, this.CellText(field, rows[i], in process, this._sampler.Delta).Length);
    }

    return widest;
  }

  private void Confirm(PendingAction action) {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    this._confirmTarget = this._selectedKey;
    this._pending = action;
    this._mode = InputMode.Confirm;
  }

  private void BeginSchedulingClass() {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    this._confirmTarget = this._selectedKey;
    this._mode = InputMode.SchedulingClass;
  }

  private void KillTree(ProcessKey root) {
    // Deepest first — see ProcessTree.DescendantsFirst for why the order is not incidental. The walk
    // is done here and the ending in the action layer, which is where the accounting and the
    // per-process identity re-check live (PRD §25.1).
    var order = ProcessTree.DescendantsFirst(this._sampler.Current, root.Pid);
    this.Report(this._actions!.TerminateTree(order), $"sent SIGTERM to {order.Count} processes");
  }

  private void TerminateMarked() {
    var failed = 0;
    var sent = 0;
    foreach (var key in this._marked) {
      if (this._actions!.Terminate(key).Succeeded)
        ++sent;
      else
        ++failed;
    }

    this._marked.Clear();
    this.Say(
      failed == 0 ? $"sent SIGTERM to {sent} processes" : $"sent SIGTERM to {sent}; {failed} refused",
      failed == 0 ? Attributes.Good : Attributes.Warn
    );
  }

  #region ticks and the clipboard

  private void ToggleMark() {
    if (this._selectedKey.IsNone)
      return;

    if (!this._marked.Add(this._selectedKey))
      this._marked.Remove(this._selectedKey);

    // Moving on after a tick is what makes ticking a list of rows one keypress each.
    this.MoveSelection(1);
  }

  private void MarkAll() {
    var processes = this._sampler.Current.Processes;
    foreach (var row in this._view.Rows)
      if (!row.IsGroupHeader)
        this._marked.Add(processes[row.Index].Key);

    this.Say($"{this._marked.Count} rows ticked", Attributes.Accent);
  }

  private void InvertMarks() {
    var processes = this._sampler.Current.Processes;
    foreach (var row in this._view.Rows) {
      if (row.IsGroupHeader)
        continue;

      var key = processes[row.Index].Key;
      if (!this._marked.Add(key))
        this._marked.Remove(key);
    }

    this.Say($"{this._marked.Count} rows ticked", Attributes.Accent);
  }

  private void CopyCell() {
    if (this._selectedRow >= this._view.RowCount) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    var row = this._view.Rows[this._selectedRow];
    ref readonly var process = ref this._sampler.Current.Processes[row.Index];
    var field = this._columns.CurrentField;
    // The raw value rather than what the column shows: a cell copied out of a monitor is on its way
    // into something that will do arithmetic with it, and "1.5G" is not a number (PRD §76).
    var text = FieldAccessor.RawText(field, in process, this._sampler.Delta, row.Index)
      ?? this.CellText(field, row, in process, this._sampler.Delta);

    this.Copy(text, $"{FieldRegistry.Get(field).Header} of {process.Name}");
  }

  private void CopyRows() {
    var fields = this.VisibleFields();
    if (fields.Length == 0 || (this._marked.Count == 0 && this._selectedRow >= this._view.RowCount)) {
      this.Say("nothing to copy", Attributes.Dim);
      return;
    }

    var builder = new StringBuilder(256);
    foreach (var field in fields)
      builder.Append(FieldRegistry.Get(field).Header).Append('\t');

    builder.Length -= 1;
    builder.Append('\n');

    var copied = 0;
    var processes = this._sampler.Current.Processes;
    foreach (var row in this._view.Rows) {
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      if (this._marked.Count > 0 ? !this._marked.Contains(process.Key) : this._view.Rows[this._selectedRow].Index != row.Index)
        continue;

      foreach (var field in fields)
        builder
          .Append(FieldAccessor.RawText(field, in process, this._sampler.Delta, row.Index) ?? string.Empty)
          .Append('\t');

      builder.Length -= 1;
      builder.Append('\n');
      ++copied;
    }

    this.Copy(builder.ToString(), copied == 1 ? "one row" : $"{copied} rows");
  }

  /// <summary>
  /// One column, down every row that is showing — or down the ticked ones (PRD §11).
  /// </summary>
  /// <remarks>
  /// The third shape of copy §11 asks a table for, and the one that was refused for want of a cell
  /// selection to take it from. There is none here and none is needed: a column copy needs a column,
  /// and this table has had a column cursor since it grew a mouse. Which rows follows the rule the
  /// row copy already uses — the ticked ones if any are, everything on screen otherwise.
  /// </remarks>
  private void CopyColumn() {
    var field = this._columns.CurrentField;
    if (FieldRegistry.Get(field).IsGraph) {
      // A drawn history has no text. An empty column of nothing looks exactly like a copy that
      // silently failed.
      this.Say($"{FieldRegistry.Get(field).Header} is a drawn history and has nothing to copy", Attributes.Dim);
      return;
    }

    var builder = new StringBuilder(256);
    builder.Append(FieldRegistry.Get(field).Header).Append('\n');

    var copied = 0;
    var processes = this._sampler.Current.Processes;
    foreach (var row in this._view.Rows) {
      // A heading is not a process and has no cell in any column but the first (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      if (this._marked.Count > 0 && !this._marked.Contains(process.Key))
        continue;

      builder.Append(FieldAccessor.RawText(field, in process, this._sampler.Delta, row.Index) ?? string.Empty).Append('\n');
      ++copied;
    }

    this.Copy(
      builder.ToString(),
      $"{FieldRegistry.Get(field).Header} of {copied} row(s)" + (this._marked.Count > 0 ? ", the ticked ones" : string.Empty)
    );
  }

  private void Copy(string text, string what) {
    this.LastCopiedText = text;
    if (this.ClipboardOutput is null) {
      this.Say($"copied {what} — but nothing is attached to paste into", Attributes.Dim);
      return;
    }

    // "Offered", not "copied": OSC 52 answers nothing, and a terminal with it switched off looks
    // exactly like one that took the text.
    this.Say(
      Clipboard.TryWrite(this.ClipboardOutput, text)
        ? $"offered {what} to the terminal's clipboard"
        : $"{what} is more than {Clipboard.SizeLimit / 1024} KiB — use {this.Keys.KeysFor(TerminalAction.Export)} to write a file instead",
      Attributes.Good
    );
  }

  private ProcessField[] VisibleFields() {
    var fields = new List<ProcessField>();
    for (var i = 0; i < this._columns.Count; ++i) {
      if (!this._columns.IsVisible(i))
        continue;

      var field = this._columns.FieldAt(i);
      // A plot has no text, so it is not a column an export can carry; the numbers behind it are
      // reachable as their own fields.
      if (!FieldRegistry.Get(field).IsGraph)
        fields.Add(field);
    }

    return [.. fields];
  }

  private void ExportTo(string path) {
    if (string.IsNullOrWhiteSpace(path)) {
      this.Say("no file name, nothing written", Attributes.Dim);
      return;
    }

    this.ExportPath = path;
    var extension = Path.GetExtension(path).TrimStart('.');
    if (!Exporter.TryParseFormat(extension, out var format))
      format = ExportFormat.Tsv;

    try {
      using (var writer = new StreamWriter(path, false)) {
        Exporter.Write(
          writer,
          format,
          this._sampler.Current,
          this._sampler.Delta,
          this._view,
          this.VisibleFields(),
          this._view.TreeMode
        );
      }

      this.Say($"wrote {this._view.MatchCount} rows to {path} as {format}", Attributes.Good);
    } catch (IOException problem) {
      this.Say(problem.Message, Attributes.Bad);
    } catch (UnauthorizedAccessException problem) {
      this.Say(problem.Message, Attributes.Bad);
    }
  }

  #endregion

  /// <summary>
  /// Fills the handle column for the rows currently on screen. Not done automatically: on Linux the
  /// kernel builds one directory entry per descriptor, which is 85 µs per process (PRD §3.5).
  /// </summary>
  private void FillHandleCounts() {
    var processes = this._sampler.Current.Processes;
    var rows = this._view.Rows;
    var last = Math.Min(rows.Length, this._scrollOffset + this.ListHeight);
    for (var i = this._scrollOffset; i < last; ++i) {
      if (rows[i].IsGroupHeader)
        continue;

      var key = processes[rows[i].Index].Key;
      this._handleCounts[key] = this._probe.GetHandleCount(key);
    }

    this.Say("handle counts read for the visible rows", Attributes.Accent);
  }

  private void Say(string message, byte attribute) {
    this._message = message;
    this._messageAttribute = attribute;
  }

  #endregion

  #region overlays

  private void OpenActionMenu() {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    var name = this._sampler.Current.TryGetProcess(this._selectedKey, out var process) ? process.Name : "?";
    var items = new List<OverlayItem> { OverlayItem.Heading("Do to it") };
    foreach (var action in (ReadOnlySpan<TerminalAction>)[
      TerminalAction.EndTask,
      TerminalAction.Terminate,
      TerminalAction.TerminateTree,
      TerminalAction.Restart,
      TerminalAction.SuspendResume,
      TerminalAction.SchedulingClass,
    ])
      items.Add(this.MenuEntry(action));

    items.Add(OverlayItem.Heading("Look at"));
    foreach (var action in (ReadOnlySpan<TerminalAction>)[
      TerminalAction.Details,
      TerminalAction.Threads,
      TerminalAction.Modules,
      TerminalAction.Handles,
      TerminalAction.Network,
      TerminalAction.CountHandles,
    ])
      items.Add(this.MenuEntry(action));

    items.Add(OverlayItem.Heading("Take away"));
    foreach (var action in (ReadOnlySpan<TerminalAction>)[
      TerminalAction.CopyCell,
      TerminalAction.CopyRow,
      TerminalAction.Export,
    ])
      items.Add(this.MenuEntry(action));

    this.ShowOverlay(new($"{name} (PID {this._selectedKey.Pid})", items), OverlayKind.Actions);
  }

  private OverlayItem MenuEntry(TerminalAction action) {
    foreach (var entry in KeyBindings.Catalogue)
      if (entry.Action == action)
        return OverlayItem.Entry(entry.Description, this.Keys.KeysFor(action), (int)action);

    return OverlayItem.Entry(action.ToString(), string.Empty, (int)action);
  }

  /// <summary>
  /// What the rows may be grouped by (PRD §83).
  /// </summary>
  /// <remarks>
  /// The tree is in the list rather than beside it, because picking one of these is picking how the
  /// rows are arranged and the tree is one of the answers. What each grouping reads off the process
  /// is beside its name, so the list says why a machine with no containers has one heading.
  /// </remarks>
  private void OpenGroupingMenu() {
    var items = new List<OverlayItem> { OverlayItem.Heading("Arrange the rows by") };
    foreach (var (grouping, label, hint) in _Groupings)
      items.Add(OverlayItem.Toggle(label, hint, (int)grouping, this._view.Grouping == grouping));

    this.ShowOverlay(new("Grouping — Enter chooses, Esc closes", items) { HintColumn = 22 }, OverlayKind.Grouping);
  }

  private static readonly (ProcessGrouping Grouping, string Label, string Hint)[] _Groupings = [
    (ProcessGrouping.None, "Nothing", "one flat list"),
    (ProcessGrouping.ParentTree, "Parent tree", "who started what"),
    (ProcessGrouping.User, "User", "the account it runs as"),
    (ProcessGrouping.Session, "Session", "the login it belongs to"),
    (ProcessGrouping.Service, "Service", "its systemd unit"),
    (ProcessGrouping.Executable, "Executable", "the image on disk"),
    (ProcessGrouping.Container, "Container", "its container id"),
    (ProcessGrouping.Cgroup, "Cgroup", "the whole cgroup path"),
    (ProcessGrouping.Package, "Package", "where the image came from"),
  ];

  private void GroupBy(ProcessGrouping grouping) {
    this._view.Grouping = grouping;
    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    foreach (var (candidate, label, _) in _Groupings)
      if (candidate == grouping) {
        this.Say(
          grouping == ProcessGrouping.None
            ? "the rows are one flat list again"
            : $"grouped by {label.ToLowerInvariant()} — {this._view.Groups.Count} heading(s)",
          Attributes.Accent
        );

        return;
      }
  }

  /// <summary>
  /// How often the machine is sampled, and whether it is sampled at all (PRD §12).
  /// </summary>
  /// <remarks>
  /// The rates come from <see cref="UserSettings.OfferedIntervalSeconds"/>, so this picker and the
  /// window's menu cannot come to offer different ones. The tag is the interval in milliseconds,
  /// with nought for a pause and -1 for by-hand — two states that both stop the tick and are not the
  /// same request.
  /// </remarks>
  private void OpenIntervalMenu() {
    var items = new List<OverlayItem> { OverlayItem.Heading("Sample the machine") };
    foreach (var seconds in UserSettings.OfferedIntervalSeconds) {
      var milliseconds = (int)Math.Round(seconds * 1000);
      // No hint: "Every 250 ms" is the whole of what that line means, and a column of restatements
      // beside it is a column that pushes the box wider for nothing.
      items.Add(OverlayItem.Toggle(
        "Every " + UserSettings.NameOfInterval(seconds),
        string.Empty,
        milliseconds,
        this.Sampling && this.IntervalMilliseconds == milliseconds
      ));
    }

    items.Add(OverlayItem.Heading("Or not at all"));
    items.Add(OverlayItem.Toggle("Paused", "until unpaused", _PausedTag, this.Paused));
    items.Add(OverlayItem.Toggle("By hand only", "refresh samples", _ManualTag, this.ManualRefresh));
    this.ShowOverlay(new("Refresh — Enter chooses, Esc closes", items) { HintColumn = 22 }, OverlayKind.Interval);
  }

  private const int _PausedTag = 0;
  private const int _ManualTag = -1;

  /// <summary>Opens with the tick off, because the settings file said so (PRD §12).</summary>
  public void SetManualRefresh() => this.SetRefresh(_ManualTag);

  /// <summary>Takes one of the picker's answers and says what it did.</summary>
  private void SetRefresh(int milliseconds) {
    this.Paused = milliseconds == _PausedTag;
    this.ManualRefresh = milliseconds == _ManualTag;
    if (milliseconds > 0)
      this.IntervalMilliseconds = milliseconds;

    this.Say(
      this.Paused ? "sampling paused"
      : this.ManualRefresh ? $"refreshed by hand — {this.Keys.KeysFor(TerminalAction.RefreshNow)} takes a sample"
      : $"sampling every {UserSettings.NameOfInterval(this.IntervalMilliseconds / 1000d)}",
      Attributes.Accent
    );
  }

  private void OpenColumnChooser() {
    var items = new List<OverlayItem> { OverlayItem.Heading("Column sets") };
    foreach (var name in UserSettings.Presets.Keys)
      items.Add(OverlayItem.Entry(name, "set", -1));

    items.Add(OverlayItem.Heading("Columns"));
    for (var i = 0; i < this._columns.Count; ++i) {
      var descriptor = FieldRegistry.Get(this._columns.FieldAt(i));
      items.Add(OverlayItem.Toggle(descriptor.Header, descriptor.Key, i, this._columns.IsVisible(i)));
    }

    this.ShowOverlay(new("Columns — space toggles, Esc closes", items), OverlayKind.Columns);
  }

  private void ApplyColumnSet(string name) {
    if (!UserSettings.Presets.TryGetValue(name, out var fields)) {
      this.Say($"there is no column set called {name}", Attributes.Bad);
      return;
    }

    this._columns.Apply(fields);
    this.Say($"columns are the {name} set now", Attributes.Accent);
  }

  private void OpenHelp() {
    // Where the keys come from goes first, because somebody who has opened the help is one step from
    // wanting a different key, and a line at the end of seventy is a line nobody scrolls to.
    var items = new List<OverlayItem> {
      OverlayItem.Heading("Where the keys come from"),
      OverlayItem.Entry("keys.conf", $"{KeyBindings.DefaultPath} — one 'action = key' a line", -1),
    };

    var group = string.Empty;
    foreach (var entry in KeyBindings.Catalogue) {
      if (entry.Group != group) {
        items.Add(OverlayItem.Heading(group = entry.Group));
      }

      // The keys as they are bound now, not as they ship: a help page listing the defaults on a
      // machine with a keys.conf is worse than no help page (PRD §57.3).
      items.Add(OverlayItem.Entry(this.Keys.KeysFor(entry.Action), entry.Description, -1));
    }

    this.ShowOverlay(
      new("Keys — Esc closes", items, fullScreen: true) { HintColumn = 14 },
      OverlayKind.Help
    );
  }

  private void ShowOverlay(ListOverlay overlay, OverlayKind kind) {
    this._overlay = overlay;
    this._overlayKind = kind;
    this._mode = InputMode.Overlay;
    this._screen.NeedsFullRepaint = true;
  }

  #endregion

  #region drawing

  private void Compose() {
    this._screen.BeginFrame();
    if (this._mode == InputMode.Detail) {
      this.DrawDetail();
      return;
    }

    this.DrawTabRow();
    if (this.Page == TerminalPage.Performance)
      this.DrawPerformance();
    else {
      var y = this.DrawMeters();
      this._headerRow = y;
      this._tableTop = y + 1;
      this._tableHeight = this.ListHeight;
      this.DrawColumnHeader(y);
      this.DrawRows(this._tableTop);
      this.DrawLowerPane();
    }

    this.DrawStatus();
    if (this._overlay is not null)
      this._overlayPlacement = this._overlay.Draw(this._screen, this.UseBlockCharacters);
  }

  private void DrawDetail() {
    if (this._sampler.Current.TryGetProcess(this._selectedKey, out var process))
      this._detail.Draw(this._screen, in process);
    else {
      this._screen.Fill(0, 0, this._screen.Width, ' ', Attributes.Header);
      this._screen.Write(0, 0, " the process has ended ", Attributes.Header);
    }

    var y = this._screen.Height - 1;
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);
    this._screen.Write(0, y, "Tab/→ next page  ← previous  ↑↓ scroll  Esc back", Attributes.Header);
  }

  private static string TabLabel(TerminalPage page) => page switch {
    TerminalPage.Performance => " Performance ",
    _ => " Processes ",
  };

  /// <summary>
  /// The compact tab row: which page this is, and what else there is (PRD §57.1).
  /// </summary>
  private void DrawTabRow() {
    this._screen.Fill(0, 0, this._screen.Width, ' ', Attributes.Header);
    var x = 0;
    foreach (var page in (ReadOnlySpan<TerminalPage>)[TerminalPage.Processes, TerminalPage.Performance]) {
      var label = TabLabel(page);
      this._screen.Write(x, 0, label, page == this.Page ? Attributes.Selected : Attributes.Header);
      x += label.Length;
    }

    var state = new StringBuilder(48);
    if (this.Paused)
      state.Append("PAUSED  ");
    // Said whether or not it is paused as well: a table that is not following the machine looks
    // exactly like one that is, and the two reasons it might not be are not the same reason
    // (PRD §12).
    else if (this.ManualRefresh)
      state.Append("BY HAND  ");
    if (this._view.CaseSensitive)
      state.Append("case  ");
    if (this._marked.Count > 0)
      state.Append(CultureInfo.InvariantCulture, $"{this._marked.Count} ticked  ");
    if (this._view.TextFilter is { Length: > 0 } filter)
      state.Append(CultureInfo.InvariantCulture, $"filter: {filter}  ");

    // MatchCount, not RowCount: a grouping heading takes a row and is not a process (PRD §83).
    state.Append(CultureInfo.InvariantCulture, $"{this._view.MatchCount} of {this._view.TotalCount}");
    this.WriteSentenceRight(0, state.ToString(), Attributes.Header);
  }

  private int DrawMeters() {
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;
    var width = this._screen.Width;
    var perLine = this.MetersPerLine;
    var y = this.MeterTop;

    if (perLine == 0) {
      // The aggregate: one bar for the machine, then memory and swap on lines of their own, because
      // there is not enough width to put two of anything side by side.
      this.DrawMeter(0, y++, width, "CPU", delta.SystemCpuPercent);
      this.DrawMeter(0, y++, width, "Mem", this.MemoryPercent(), this.MemoryCaption());
      this.DrawMeter(0, y++, width, "Swp", this.SwapPercent(), this.SwapCaption());
    } else {
      var cell = Math.Max(12, (width - (perLine - 1)) / perLine);
      var cores = delta.PerCoreCount;
      for (var core = 0; core < cores; core += perLine) {
        for (var slot = 0; slot < perLine && core + slot < cores; ++slot)
          this.DrawMeter(slot * (cell + 1), y, cell, $"{core + slot,3}", delta.PerCoreBusyPercent(core + slot));

        ++y;
      }

      var half = Math.Max(12, width / 2 - 1);
      this.DrawMeter(0, y, half, "Mem", this.MemoryPercent(), this.MemoryCaption());
      this.DrawMeter(half + 1, y, half, "Swp", this.SwapPercent(), this.SwapCaption());
      ++y;
    }

    var system = snapshot.System;
    var tasks = $"Tasks: {this._view.TotalCount}, {system.RunningProcesses} running";
    var load = $"Load average: {system.LoadAverage1:0.00} {system.LoadAverage5:0.00} {system.LoadAverage15:0.00}";
    var uptime = $"Uptime: {FormatUptime(system.UptimeSeconds)}";
    this._screen.Write(0, y, tasks, Attributes.Dim);
    if (width < 80) {
      // Narrow: the load average is the one of the three that says whether the machine is in trouble,
      // and it is dropped rather than overwritten where even it does not fit.
      if (tasks.Length + load.Length + 2 <= width)
        this._screen.WriteRight(0, y, width - 1, load, Attributes.Dim);

      return y + this.BlankLines;
    }

    this._screen.Write(32, y, load, Attributes.Dim);
    // The uptime only where the whole of it fits. Half a timestamp is not a shorter timestamp, it is
    // a wrong one, and at exactly eighty columns this used to print "1d 1".
    if (68 + uptime.Length <= width)
      this._screen.Write(68, y, uptime, Attributes.Dim);
    else if (32 + load.Length + 2 + uptime.Length <= width)
      this._screen.WriteRight(0, y, width - 1, uptime, Attributes.Dim);

    return y + this.BlankLines;
  }

  private Rate MemoryPercent() {
    var system = this._sampler.Current.System;
    if (!system.TotalMemoryBytes.HasValue || system.TotalMemoryBytes.Value == 0 || !system.AvailableMemoryBytes.HasValue)
      return Rate.Gap;

    var used = system.TotalMemoryBytes.Value - Math.Min(system.TotalMemoryBytes.Value, system.AvailableMemoryBytes.Value);
    return Rate.Of(used * 100d / system.TotalMemoryBytes.Value);
  }

  private string MemoryCaption() {
    var system = this._sampler.Current.System;
    if (!system.TotalMemoryBytes.HasValue || !system.AvailableMemoryBytes.HasValue)
      return Humanize.Bytes(system.TotalMemoryBytes);

    var used = system.TotalMemoryBytes.Value - Math.Min(system.TotalMemoryBytes.Value, system.AvailableMemoryBytes.Value);
    return $"{Humanize.Bytes(Counter.Of(used))}/{Humanize.Bytes(system.TotalMemoryBytes)}";
  }

  private Rate SwapPercent() {
    var system = this._sampler.Current.System;
    return system.TotalSwapBytes.HasValue && system.TotalSwapBytes.Value > 0
      ? Rate.Of(system.UsedSwapBytes.GetValueOrDefault() * 100d / system.TotalSwapBytes.Value)
      : Rate.Gap;
  }

  private string SwapCaption() {
    var system = this._sampler.Current.System;
    return $"{Humanize.Bytes(system.UsedSwapBytes)}/{Humanize.Bytes(system.TotalSwapBytes)}";
  }

  private void DrawMeter(int x, int y, int width, string label, Rate value, string? text = null) {
    this._screen.Write(x, y, label, Attributes.Accent);
    var barStart = x + label.Length + 1;
    var barWidth = Math.Max(4, width - label.Length - 3);
    this._screen.Write(barStart - 1, y, "[", Attributes.Dim);
    this._screen.Write(barStart + barWidth, y, "]", Attributes.Dim);

    if (!value.HasValue) {
      this._screen.Write(barStart, y, Humanize.Placeholder(value.Reason).PadRight(barWidth), Attributes.Dim);
      return;
    }

    var percent = Math.Clamp(value.Value, 0, 100);
    var filled = (int)Math.Round(percent * barWidth / 100);
    var attribute = percent >= 90 ? Attributes.Bad : percent >= 60 ? Attributes.Warn : Attributes.Good;
    this._screen.Fill(barStart, y, filled, '|', attribute);

    // The number sits inside the bar, right-aligned, the way htop does it: the bar answers "how
    // much" at a glance and the digits answer "exactly how much" when you look.
    var caption = text ?? Humanize.Percent(value) + "%";
    if (caption.Length < barWidth)
      this._screen.Write(barStart + barWidth - caption.Length, y, caption, Attributes.Dim);
  }

  private static string FormatUptime(double seconds) {
    var span = TimeSpan.FromSeconds(seconds);
    return span.TotalDays >= 1
      ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
  }

  /// <summary>
  /// The performance page: the machine rather than any one process (PRD §57.1, §57.4).
  /// </summary>
  private void DrawPerformance() {
    var y = this.DrawMeters();
    var width = this._screen.Width;
    var plotWidth = Math.Max(8, width - 2);

    foreach (var (label, history, scale, series) in (ReadOnlySpan<(string, HistoryRing<Rate>, double, HistorySeries)>)[
      ("Processor, whole machine", this._cpuHistory, 100d, HistorySeries.Cpu),
      ("Memory in use", this._memoryHistory, 100d, HistorySeries.Cpu),
      ("Swap in use", this._swapHistory, 100d, HistorySeries.Cpu),
    ]) {
      if (y + 3 >= this._screen.Height - 1)
        break;

      this._screen.Write(0, y, label, Attributes.Accent);
      this._screen.WriteRight(0, y, width - 1, HistorySummary.Describe(history, series), Attributes.Dim);
      this._screen.Write(1, y + 1, this.Plot(plotWidth, history, scale), GraphAttribute(series));
      y += 3;
    }

    if (y < this._screen.Height - 2)
      this._screen.Write(
        0, y,
        $"{this.Keys.KeysFor(TerminalAction.Graphs)} goes back to the process list.",
        Attributes.Dim
      );
  }

  /// <summary>One history, in whichever of the four styles is switched on (PRD §57.4).</summary>
  private string Plot(int width, HistoryRing<Rate>? history, double scale) => this.GraphStyle switch {
    GraphStyle.Braille => BrailleSparkline.Render(width, history, scale),
    GraphStyle.Ascii => BlockSparkline.Render(width, history, scale, unicode: false),
    _ => BlockSparkline.Render(width, history, scale, unicode: true),
  };

  private void DrawColumnHeader(int y) {
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[64];
    var count = this._columns.Place(this._screen.Width - 1, placements);
    for (var i = 0; i < count; ++i) {
      var placement = placements[i];
      var column = FieldRegistry.Get(placement.Field);
      var header = column.ShortHeader;
      if (column.IsSortable && placement.Field == this._view.SortColumn)
        header = this._view.SortDescending ? header + "▾" : header + "▴";
      else if (this.SecondarySortRank(placement.Field) is { } rank)
        // A digit rather than a second arrow: two arrows in a header row say "sorted twice" and
        // nothing about which one wins (PRD §11).
        header += rank.ToString(CultureInfo.InvariantCulture);

      // A header that does not fit loses its tail, not its head: "Working set" clipped to "ing set"
      // names nothing, where "Workin" is still recognisable. Values are the other way round, which
      // is why WriteRight cuts the front of those.
      if (header.Length > placement.Width)
        header = header[..placement.Width];

      var attribute = placement.Index == this._columns.Current ? Attributes.Selected : Attributes.Header;
      if (column.RightAligned)
        this._screen.WriteRight(placement.X + 1, y, placement.Width, header, attribute);
      else
        this._screen.Write(placement.X + 1, y, header, attribute);

      // The pinned block ends in a wall — but only once the table has actually been scrolled, because
      // a wall on a table where nothing has moved is a line that explains nothing.
      if (this.ScrolledSideways && placement.Frozen && (i + 1 >= count || !placements[i + 1].Frozen))
        this._screen.Write(placement.X + placement.Width + 1, y, this.Wall, Attributes.Header);
    }

    if (this._columns.Scroll > this._columns.Frozen)
      this._screen.Write(0, y, "<", Attributes.Accent);
  }

  /// <summary>Whether anything is off to the left, which is what makes the pinned columns visible.</summary>
  private bool ScrolledSideways => this._columns.Scroll > this._columns.Frozen;

  private string Wall => this.UseBlockCharacters ? "\u2502" : "|";

  private int? SecondarySortRank(ProcessField field) {
    var keys = this._view.SecondarySort;
    for (var i = 0; i < keys.Count; ++i)
      if (keys[i].Field == field)
        return i + 2;

    return null;
  }

  private void DrawRows(int top) {
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;
    var rows = this._view.Rows;
    var height = this._tableHeight;
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[64];
    var count = this._columns.Place(this._screen.Width - 1, placements);

    for (var line = 0; line < height; ++line) {
      var rowIndex = this._scrollOffset + line;
      if (rowIndex >= rows.Length)
        break;

      var row = rows[rowIndex];
      if (row.IsGroupHeader) {
        this.DrawGroupHeading(top + line, row.Group);
        continue;
      }

      ref readonly var process = ref snapshot.Processes[row.Index];
      var selected = rowIndex == this._selectedRow;
      var marked = this._marked.Contains(process.Key);
      var baseAttribute = selected
        ? Attributes.Selected
        : delta.IsNew(row.Index) ? Attributes.NewProcess : Attributes.Normal;

      if (marked && !selected)
        baseAttribute |= Attributes.Marked;

      var y = top + line;
      if (selected)
        this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Selected);

      if (this.ScrolledSideways && this._columns.Frozen > 0 && count > 0)
        for (var i = 0; i < count; ++i)
          if (placements[i].Frozen && (i + 1 >= count || !placements[i + 1].Frozen))
            this._screen.Write(placements[i].X + placements[i].Width + 1, y, this.Wall, selected ? Attributes.Selected : Attributes.Dim);

      // The gutter: a tick here rather than only a colour, so the terminals that have no colour and
      // the readers who cannot see it are not left guessing (PRD §57.4).
      this._screen.Write(0, y, marked ? "*" : " ", selected ? Attributes.Selected : Attributes.Marked);

      for (var i = 0; i < count; ++i) {
        var placement = placements[i];
        var column = FieldRegistry.Get(placement.Field);
        var x = placement.X + 1;
        if (column.Series is { } series && this.GraphStyle != GraphStyle.Numbers) {
          // Drawn, not written: the ramp turns a column of text into a plot (PRD §11).
          this._screen.Write(
            x, y,
            this.Plot(placement.Width, this._rowHistory.Get(process.Key, series), this._rowHistory.ScaleOf(series)),
            selected ? Attributes.Selected : GraphAttribute(series)
          );

          continue;
        }

        var text = this.CellText(placement.Field, row, in process, delta);
        this.WriteCell(x, y, placement.Width, text, column.RightAligned, baseAttribute);
      }
    }

    if (rows.Length == 0)
      this._screen.Write(1, top, "nothing matches", Attributes.Dim);
  }

  /// <summary>
  /// One grouping heading, across the whole line (PRD §83).
  /// </summary>
  /// <remarks>
  /// Across the line and not into the columns, because it is not a row of the table: it has no pid,
  /// no CPU figure and nothing to put under any header. The count is the group's whole membership
  /// rather than what is on screen, so a folded heading still says how much it is hiding.
  /// </remarks>
  private void DrawGroupHeading(int y, int group) {
    if ((uint)group >= (uint)this._view.Groups.Count)
      return;

    var (label, count) = this._view.Groups[group];
    var folded = this._view.IsGroupCollapsed(label);
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);
    var marker = folded ? "+" : "-";
    var text = $"{marker} {label}  ({count} process{(count == 1 ? string.Empty : "es")})";
    this._screen.Write(0, y, Clip(text, this._screen.Width), Attributes.Header);
  }

  /// <summary>
  /// One cell, with whatever the search matched inside it picked out (PRD §11).
  /// </summary>
  private void WriteCell(int x, int y, int width, string text, bool rightAligned, byte attribute) {
    var drawn = text.Length > width
      ? rightAligned ? text[^width..] : text[..width]
      : text;

    var start = rightAligned ? x + width - drawn.Length : x;
    this._screen.Write(start, y, drawn, attribute);

    if (this._highlight is not { Length: > 0 } needle)
      return;

    var comparison = this._view.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    var at = drawn.IndexOf(needle, comparison);
    if (at < 0)
      return;

    this._screen.Write(start + at, y, drawn.AsSpan(at, Math.Min(needle.Length, drawn.Length - at)), (byte)(attribute | Attributes.Match));
  }

  /// <summary>Each series keeps its own colour, matching the window's plots.</summary>
  private static byte GraphAttribute(HistorySeries series) => series switch {
    HistorySeries.Cpu => Attributes.Good,
    HistorySeries.Memory => Attributes.Accent,
    _ => Attributes.Warn,
  };

  /// <summary>
  /// The text for one cell. Everything goes through the shared accessor so a value reads the same
  /// here as in the window (PRD §5.1); the exceptions below are things the terminal knows and the
  /// engine does not.
  /// </summary>
  private string CellText(ProcessField field, ViewRow row, in ProcessRecord process, SnapshotDelta delta) {
    switch (field) {
      // The tree lives in the name column, so indentation and the expander are part of its text.
      case ProcessField.Name when this._view.TreeMode: {
        var marker = !row.HasChildren ? "  " : this._view.IsCollapsed(process.Pid) ? "+ " : "- ";
        return new string(' ', Math.Min(row.Depth * 2, 32)) + marker + process.Name;
      }

      // Handles are counted on their own schedule because counting them is expensive (PRD §5.4).
      case ProcessField.HandleCount when this._handleCounts.TryGetValue(process.Key, out var handles):
        return Humanize.Count(handles);

      // A missing user name falls back to the numeric id, which is narrower and more use than a dash.
      case ProcessField.UserName when process.UserName is null:
        return process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?";

      default: {
        var descriptor = FieldRegistry.Get(field);
        if (descriptor.Series is { } series && this.GraphStyle == GraphStyle.Numbers)
          // The plot as figures: what it would have shown, for a terminal or a reader that cannot
          // see a plot at all (PRD §57.4).
          return HistorySummary.Compact(this._rowHistory.Get(process.Key, series), series, descriptor.TerminalWidth);

        return FieldAccessor.Text(field, in process, delta, row.Index);
      }
    }
  }

  /// <summary>
  /// The lower pane: the selected process in the detail a row has no room for (PRD §57.1).
  /// </summary>
  private void DrawLowerPane() {
    if (this._lowerPaneHeight <= 0)
      return;

    var top = this._screen.Height - 1 - this._lowerPaneHeight - 1;
    this._paneTop = top;
    this._screen.Fill(0, top, this._screen.Width, this.UseBlockCharacters ? '─' : '-', Attributes.Dim);

    if (!this._sampler.Current.TryGetProcess(this._selectedKey, out var process)) {
      this._screen.Write(0, top + 1, "nothing selected", Attributes.Dim);
      return;
    }

    var y = top + 1;
    this._screen.Write(0, y, $"{process.Name} (PID {process.Pid})", Attributes.Accent);
    this._screen.WriteRight(0, y, this._screen.Width - 1, $"{Humanize.State(process.State)}  started {Humanize.Timestamp(process.StartTimeUtcTicks)}", Attributes.Dim);
    ++y;

    // The four figures a plot cannot be read for, written out (PRD §57.4).
    foreach (var (label, series) in (ReadOnlySpan<(string, HistorySeries)>)[
      ("CPU", HistorySeries.Cpu),
      ("Memory", HistorySeries.Memory),
      ("I/O", HistorySeries.Io),
    ]) {
      if (y >= this._screen.Height - 1)
        return;

      this._screen.Write(0, y, label.PadRight(8), Attributes.Dim);
      this._screen.Write(8, y, HistorySummary.Describe(this._rowHistory.Get(process.Key, series), series), Attributes.Normal);
      ++y;
    }

    if (y < this._screen.Height - 1)
      this._screen.Write(0, y, Clip(process.CommandLine ?? process.ImagePath ?? "—", this._screen.Width - 1), Attributes.Dim);
  }

  /// <summary>
  /// The confirmation, which names the action, the target, its pid and what it costs (PRD §90).
  /// </summary>
  /// <remarks>
  /// One line and no dialog, so every word has to earn its place — but the count of processes under
  /// a tree does earn it. A shell on its own and a shell with a build under it are the same row and
  /// very different requests, and the number is the only thing that says which one this is.
  /// </remarks>
  private string ConfirmationText() {
    var name = this._sampler.Current.TryGetProcess(this._confirmTarget, out var record) ? record.Name : "?";
    var target = $"{name} (PID {this._confirmTarget.Pid})";
    return this._pending switch {
      PendingAction.TerminateTree
        => $"Terminate {target} and the {Math.Max(0, ProcessTree.DescendantsFirst(this._sampler.Current, this._confirmTarget.Pid).Count - 1)} "
          + "processes under it? Unsaved work in them is lost. y/N",
      PendingAction.Restart
        => $"Stop {target} and start it again with the same arguments? Unsaved work in it is lost. y/N",
      _ when this._marked.Count > 0
        => $"Terminate the {this._marked.Count} ticked processes? They are not asked to save anything first. y/N",
      _ => $"Terminate {target}? It is not asked to save anything first. y/N",
    };
  }

  private void DrawStatus() {
    var y = this._screen.Height - 1;
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);

    switch (this._mode) {
      case InputMode.Search:
        this._screen.Write(0, y, $"Search: {this._input}_", Attributes.Header);
        return;
      case InputMode.Filter:
        this._screen.Write(0, y, $"Filter: {this._input}_", Attributes.Header);
        if (this._message.Length > 0)
          this.WriteSentenceRight(y, this._message, Attributes.Header);

        return;
      case InputMode.ExportPath:
        this._screen.Write(0, y, $"Write the table to: {this._input}_", Attributes.Header);
        return;
      case InputMode.Confirm:
        this._screen.Write(0, y, this.ConfirmationText(), Attributes.Header);
        return;
      case InputMode.SchedulingClass: {
        var name = this._sampler.Current.TryGetProcess(this._confirmTarget, out var chosen) ? chosen.Name : "?";
        this._screen.Write(
          0, y,
          $"Scheduler class for {name} (PID {this._confirmTarget.Pid}): o normal · b batch · i idle · r real-time RR · f real-time FIFO",
          Attributes.Header
        );

        return;
      }
    }

    this._screen.Write(0, y, this.HelpLine(), Attributes.Header);

    var right = this._message.Length > 0 ? this._message : string.Empty;
    if (this.ShowTiming) {
      var cost = $"{this._sampler.LastSampleDuration.TotalMilliseconds:0.0} ms";
      right = right.Length > 0 ? $"{right}   {cost}" : cost;
    }

    if (right.Length > 0)
      this.WriteSentenceRight(y, right, this._message.Length > 0 ? this._messageAttribute : Attributes.Header);
  }

  /// <summary>
  /// The help line, built from the bindings so a rebound key is the key it says (PRD §57.3).
  /// </summary>
  /// <remarks>
  /// It stops when it runs out of width rather than being clipped mid-word, and the order is the
  /// order of usefulness — so an eighty-column terminal loses "restart" and keeps "quit".
  /// </remarks>
  private string HelpLine() {
    var builder = new StringBuilder(this._screen.Width);
    var budget = this._screen.Width - (this._message.Length > 0 ? this._message.Length + 4 : 12);
    foreach (var action in (ReadOnlySpan<TerminalAction>)[
      TerminalAction.Help,
      TerminalAction.ToggleTree,
      TerminalAction.SortNext,
      TerminalAction.Search,
      TerminalAction.ActionMenu,
      TerminalAction.EndTask,
      TerminalAction.Terminate,
      TerminalAction.TerminateTree,
      TerminalAction.Restart,
      TerminalAction.SchedulingClass,
      TerminalAction.MarkToggle,
      TerminalAction.Filter,
      TerminalAction.ColumnChooser,
      TerminalAction.Details,
      TerminalAction.Quit,
    ]) {
      var keys = this.Keys.KeysFor(action);
      if (keys == "unbound")
        continue;

      var label = $"{FirstKey(keys)} {ShortName(action)}  ";
      if (builder.Length + label.Length > budget)
        break;

      builder.Append(label);
    }

    return builder.ToString();

    static string FirstKey(string keys) {
      var space = keys.IndexOf(' ', StringComparison.Ordinal);
      return space < 0 ? keys : keys[..space];
    }

    static string ShortName(TerminalAction action) => action switch {
      TerminalAction.Help => "help",
      TerminalAction.ToggleTree => "tree",
      TerminalAction.SortNext => "sort",
      TerminalAction.Search => "search",
      TerminalAction.Filter => "filter",
      TerminalAction.ActionMenu => "actions",
      TerminalAction.EndTask => "end task",
      TerminalAction.Terminate => "kill",
      TerminalAction.TerminateTree => "kill tree",
      TerminalAction.Restart => "restart",
      TerminalAction.SchedulingClass => "class",
      TerminalAction.MarkToggle => "tick",
      TerminalAction.ColumnChooser => "columns",
      TerminalAction.Details => "details",
      _ => "quit",
    };
  }

  /// <summary>
  /// Writes a sentence at the right-hand end of a line, keeping its beginning.
  /// </summary>
  /// <remarks>
  /// The difference from <see cref="TerminalScreen.WriteRight"/> is the end that gets cut, and it is
  /// not a detail. A value trimmed to its column keeps its tail, because the significant digits of a
  /// number and the file name of a path are both at the end. A sentence is the other way round:
  /// "wrote 5 rows to /var/folders/…/T/procman-export-1a2b.csv as Csv" trimmed from the front is a
  /// fragment of a path with nothing to say it was written, which is exactly what a macOS runner's
  /// temporary directory turned every export message into.
  /// </remarks>
  private void WriteSentenceRight(int y, string text, byte attribute) {
    var width = this._screen.Width - 1;
    if (width <= 0)
      return;

    this._screen.WriteRight(0, y, width, Clip(text, width), attribute);
  }

  private static string Clip(string text, int width)
    => width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];

  #endregion

}
