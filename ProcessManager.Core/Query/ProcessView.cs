using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One visible row: where the process is in the snapshot, and how deep in the tree.</summary>
public readonly record struct ViewRow(int Index, int Depth, bool HasChildren);

/// <summary>
/// Turns a snapshot into the ordered, filtered, optionally-nested list of rows a front-end draws.
/// Both front-ends use this one, so tree building, sorting and filtering behave identically in the
/// window and in the terminal (PRD §1.1).
/// </summary>
public sealed class ProcessView {

  private int[] _order = [];
  private int[] _depth = [];
  private int[] _parentOf = [];
  private int[] _childStart = [];
  private int[] _childCount = [];
  private int[] _children = [];
  private int[] _cursor = [];
  private byte[] _state = [];
  private bool[] _visible = [];
  private ViewRow[] _rows = [];
  private readonly Dictionary<int, int> _byPid = [];
  private readonly Stack<int> _walk = new();
  private readonly IComparer<int> _comparer;

  private SystemSnapshot? _snapshot;
  private SnapshotDelta? _delta;

  public ProcessView() => this._comparer = Comparer<int>.Create(this.Compare);

  public ProcessField SortColumn { get; set; } = ProcessField.CpuPercent;

  public bool SortDescending { get; set; } = true;

  /// <summary>Nest children under their parent instead of showing one flat sorted list.</summary>
  public bool TreeMode { get; set; }

  /// <summary>Case-insensitive substring matched against name and command line; null for all.</summary>
  /// <summary>
  /// The filter, in the query language of PRD §56 — <c>chrome</c>, <c>cpu:&gt;50</c>,
  /// <c>user:alice AND memory:&gt;1GiB</c>.
  /// </summary>
  /// <remarks>
  /// Anything that does not parse falls back to a plain substring search rather than matching
  /// nothing, because somebody typing "chrome:" is halfway through a working query and blanking the
  /// list at every keystroke makes the box unusable.
  /// </remarks>
  public string? TextFilter {
    get => this._filterText;
    set {
      this._filterText = value;
      this._query = ProcessQuery.ParseOrSubstring(value);
    }
  }

  private string? _filterText;
  private ProcessQuery _query = ProcessQuery.Empty;

  /// <summary>The parsed filter, so a caller can report what was wrong with it.</summary>
  public ProcessQuery Query => this._query;

  /// <summary>Show only this user's processes; null for every user.</summary>
  public int? UserIdFilter { get; set; }

  /// <summary>The rows to draw, in order.</summary>
  public ReadOnlySpan<ViewRow> Rows => this._rows.AsSpan(0, this.RowCount);

  public int RowCount { get; private set; }

  /// <summary>How many processes the snapshot held, before filtering.</summary>
  public int TotalCount => this._snapshot?.ProcessCount ?? 0;

  /// <summary>Finds the row showing a given process, or -1. Used to keep a selection across samples.</summary>
  public int FindRow(ProcessKey key) {
    if (this._snapshot is null)
      return -1;

    var processes = this._snapshot.Processes;
    var rows = this.Rows;
    for (var i = 0; i < rows.Length; ++i)
      if (processes[rows[i].Index].Key == key)
        return i;

    return -1;
  }

  /// <summary>
  /// Recomputes the row list. Called once per sample; every buffer is reused across calls.
  /// </summary>
  public void Rebuild(SystemSnapshot snapshot, SnapshotDelta delta) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    this._snapshot = snapshot;
    this._delta = delta;

    var count = snapshot.ProcessCount;
    EnsureLength(ref this._order, count);
    EnsureLength(ref this._depth, count);
    EnsureLength(ref this._parentOf, count);
    EnsureLength(ref this._childStart, count + 1);
    EnsureLength(ref this._childCount, count + 1);
    EnsureLength(ref this._children, count);
    EnsureLength(ref this._visible, count);
    EnsureLength(ref this._rows, count);

    var processes = snapshot.Processes;
    this._byPid.Clear();
    for (var i = 0; i < count; ++i)
      this._byPid[processes[i].Pid] = i;

    for (var i = 0; i < count; ++i)
      this._visible[i] = this.Matches(processes[i], i);

    if (this.TreeMode)
      this.BuildTree(processes, count);
    else
      this.BuildFlat(count);
  }

  private void BuildFlat(int count) {
    var written = 0;
    for (var i = 0; i < count; ++i)
      if (this._visible[i])
        this._order[written++] = i;

    Array.Sort(this._order, 0, written, this._comparer);
    for (var i = 0; i < written; ++i)
      this._rows[i] = new(this._order[i], 0, false);

    this.RowCount = written;
  }

  private void BuildTree(ReadOnlySpan<ProcessRecord> processes, int count) {
    this.LinkParents(processes, count);
    this.PromoteAncestorsOfMatches(count);
    this.IndexChildren(count);

    var rootCount = 0;
    for (var i = 0; i < count; ++i)
      if (this._parentOf[i] < 0 && this._visible[i])
        this._order[rootCount++] = i;

    Array.Sort(this._order, 0, rootCount, this._comparer);

    // Depth-first, siblings in sort order. The stack is pushed in reverse so the first sibling comes
    // off first; the alternative is recursion, and a process tree's depth is not ours to bound.
    this.RowCount = 0;
    this._walk.Clear();
    for (var i = rootCount - 1; i >= 0; --i) {
      this._depth[this._order[i]] = 0;
      this._walk.Push(this._order[i]);
    }

    while (this._walk.TryPop(out var index)) {
      var start = this._childStart[index];
      var childCount = this._childCount[index];
      var visibleChildren = 0;
      for (var i = 0; i < childCount; ++i)
        if (this._visible[this._children[start + i]])
          ++visibleChildren;

      this._rows[this.RowCount++] = new(index, this._depth[index], visibleChildren > 0);
      if (childCount == 0)
        continue;

      Array.Sort(this._children, start, childCount, this._comparer);
      for (var i = childCount - 1; i >= 0; --i) {
        var child = this._children[start + i];
        if (!this._visible[child])
          continue;

        this._depth[child] = this._depth[index] + 1;
        this._walk.Push(child);
      }
    }
  }

  private void LinkParents(ReadOnlySpan<ProcessRecord> processes, int count) {
    for (var i = 0; i < count; ++i) {
      this._parentOf[i] = -1;
      var parentPid = processes[i].ParentPid;
      // A process whose parent is not in this snapshot (it exited, or lives in another PID
      // namespace) is a root; so is one that claims itself, which /proc reports for pid 1 inside a
      // container.
      if (parentPid <= 0 || parentPid == processes[i].Pid)
        continue;
      if (!this._byPid.TryGetValue(parentPid, out var parentIndex) || parentIndex == i)
        continue;

      this._parentOf[i] = parentIndex;
    }

    // A cycle would make the walk below run forever. It should not be possible, and it has been
    // observed anyway across namespace boundaries, so the link that closes one is cut rather than
    // trusted.
    //
    // Each process is walked up from at most once. Counting steps per process instead — stopping
    // after `count` of them because a longer chain must have revisited something — is correct but
    // costs the depth of the tree for every row: a ten-thousand-deep chain took ninety milliseconds
    // and twice that depth took four times as long, which is the whole sampling budget spent on a
    // check for something that almost never happens (PRD §4).
    EnsureLength(ref this._state, count);
    Array.Clear(this._state, 0, count);
    for (var i = 0; i < count; ++i) {
      if (this._state[i] != Unvisited)
        continue;

      // Up the chain, marking as we go, until we reach a root or something already accounted for.
      var node = i;
      while (node >= 0 && this._state[node] == Unvisited) {
        this._state[node] = OnPath;
        node = this._parentOf[node];
      }

      // Stopping on a node still marked from *this* walk means the chain closed on itself. That
      // node is the one the cycle runs through, so it becomes a root and the cycle is a chain.
      if (node >= 0 && this._state[node] == OnPath)
        this._parentOf[node] = -1;

      // Everything just walked ends at a root now, so no later walk needs to look past it.
      for (var walker = i; walker >= 0 && this._state[walker] == OnPath; walker = this._parentOf[walker])
        this._state[walker] = Safe;
    }
  }

  private const byte Unvisited = 0;
  private const byte OnPath = 1;
  private const byte Safe = 2;

  private void PromoteAncestorsOfMatches(int count) {
    if (this._query.IsEmpty && this.UserIdFilter is null)
      return;

    // A process is shown when it matches, or when something under it does. Without this, filtering in
    // tree mode hides the very rows it found, because their parents did not match.
    for (var i = 0; i < count; ++i) {
      if (!this._visible[i])
        continue;

      for (var parent = this._parentOf[i]; parent >= 0 && !this._visible[parent]; parent = this._parentOf[parent])
        this._visible[parent] = true;
    }
  }

  private void IndexChildren(int count) {
    // Counting sort into one flat array, so walking a node's children is a slice rather than a scan
    // of every process. The scan is what makes a naive tree builder quadratic, and quadratic at a
    // thousand processes is the whole sampling budget (PRD §4).
    Array.Clear(this._childCount, 0, count + 1);
    for (var i = 0; i < count; ++i)
      if (this._parentOf[i] >= 0)
        ++this._childCount[this._parentOf[i]];

    var running = 0;
    for (var i = 0; i < count; ++i) {
      this._childStart[i] = running;
      running += this._childCount[i];
    }

    EnsureLength(ref this._cursor, count);
    Array.Copy(this._childStart, this._cursor, count);
    for (var i = 0; i < count; ++i) {
      var parent = this._parentOf[i];
      if (parent >= 0)
        this._children[this._cursor[parent]++] = i;
    }
  }

  private bool Matches(in ProcessRecord process, int index) {
    if (this.UserIdFilter is { } uid && process.UserId != uid)
      return false;

    return this._query.Matches(in process, this._delta, index);
  }

  private int Compare(int left, int right) {
    var result = this.CompareAscending(left, right);
    if (result == 0)
      // Ties by pid, always ascending: an unstable order makes rows jump between samples for no
      // visible reason, and a row that jumps is a row somebody kills by accident (PRD §7.3).
      return this._snapshot!.Processes[left].Pid.CompareTo(this._snapshot.Processes[right].Pid);

    return this.SortDescending ? -result : result;
  }

  private int CompareAscending(int left, int right) {
    var processes = this._snapshot!.Processes;

    // Every field, in one place, shared with both front-ends: sorting by a column and the text that
    // column shows are now read out of the same accessor, so they cannot drift apart (PRD §5.1).
    return FieldAccessor.Compare(this.SortColumn, in processes[left], left, in processes[right], right, this._delta);
  }

  private static void EnsureLength<T>(ref T[] array, int length) {
    if (array.Length < length)
      array = new T[Math.Max(length, array.Length * 2)];
  }

}
