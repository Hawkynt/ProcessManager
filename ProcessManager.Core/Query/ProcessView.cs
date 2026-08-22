using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// One visible row: where the process is in the snapshot, how deep in the tree, and which group it
/// belongs to.
/// </summary>
/// <param name="Index">
/// The process's position in the snapshot, or -1 for a group header — which is not a process and has
/// none. A caller that indexes the snapshot with this without asking
/// <see cref="IsGroupHeader"/> first gets an exception rather than the wrong process, which is
/// deliberate: a header row silently rendering process zero is exactly the class of bug §83 forbids.
/// </param>
/// <param name="Group">Which of <see cref="ProcessView.Groups"/> this row is in, or -1 when off.</param>
public readonly record struct ViewRow(int Index, int Depth, bool HasChildren, int Group = -1) {

  /// <summary>True for a heading row. It is not a process: not selectable, not counted, not actionable.</summary>
  public bool IsGroupHeader => this.Index < 0;

}

/// <summary>
/// One heading in a grouped list (PRD §83).
/// </summary>
/// <param name="Label">What the heading says — the user, the session, the unit, the image.</param>
/// <param name="Count">How many processes are under it, whether or not they are on screen.</param>
public readonly record struct ProcessGroup(string Label, int Count);

/// <summary>
/// What the rows are grouped by (PRD §83).
/// </summary>
/// <remarks>
/// The parent tree is one of these rather than a flag beside them, because it is the same decision:
/// a list is nested by parentage, or headed by user, or neither, and it can never be two of those at
/// once. <see cref="ProcessView.TreeMode"/> remains as the name the rest of the program already uses
/// for <see cref="ParentTree"/>.
/// <para>
/// §83's remaining two — application and publisher — are not here. Naming a group needs something to
/// read it off, and this program has no notion of an application and no signature verification; a
/// grouping that guessed would put processes under headings that are not true.
/// </para>
/// </remarks>
public enum ProcessGrouping : byte {

  /// <summary>One flat list.</summary>
  None,

  /// <summary>Children nested under their parents — the process tree.</summary>
  ParentTree,

  /// <summary>By the account the process runs as.</summary>
  User,

  /// <summary>By login session.</summary>
  Session,

  /// <summary>By the systemd unit the process lives in.</summary>
  Service,

  /// <summary>By the executable image.</summary>
  Executable,

  /// <summary>By container.</summary>
  Container,

  /// <summary>By the whole cgroup path, which is finer than either service or container.</summary>
  Cgroup,

  /// <summary>By the package the executable came out of.</summary>
  Package,

  /// <summary>
  /// By what kind of process it is: yours, the system's, a service, and the rest (PRD §13).
  /// </summary>
  /// <remarks>
  /// §13 calls this the friendly view, and it is the arrangement somebody who is not a systems
  /// programmer actually wants: not four hundred rows sorted by a number, but "these are your
  /// programs, these are the machine's". It is the row palette's own classification rather than a
  /// second opinion beside it, so the headings and the colours cannot come to disagree.
  /// </remarks>
  Category,

}

/// <summary>A column the order is decided by, and which way round (PRD §11).</summary>
public readonly record struct SortKey(ProcessField Field, bool Descending);

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
  private int[] _groupOf = [];
  private int[] _groupStart = [];
  private int[] _groupCursor = [];
  private readonly List<ProcessGroup> _groups = [];
  private readonly Dictionary<string, int> _groupIndex = new(StringComparer.Ordinal);
  private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
  private readonly Dictionary<int, int> _byPid = [];
  private readonly HashSet<int> _collapsed = [];
  private readonly List<SortKey> _secondary = [];
  private readonly Stack<int> _walk = new();
  private readonly IComparer<int> _comparer;

  private SystemSnapshot? _snapshot;
  private SnapshotDelta? _delta;

  public ProcessView() => this._comparer = Comparer<int>.Create(this.Compare);

  public ProcessField SortColumn { get; set; } = ProcessField.CpuPercent;

  public bool SortDescending { get; set; } = true;

  /// <summary>
  /// The columns that decide the order when the primary one ties (PRD §11).
  /// </summary>
  /// <remarks>
  /// Sorting by state and then by memory is the request behind most of the two-column sorts anybody
  /// asks for: a group whose rows have identical values in the first column is exactly where a
  /// second one earns its place. Ties past the last key still fall through to the pid, so the order
  /// remains total and rows never swap places between two identical samples.
  /// </remarks>
  public IReadOnlyList<SortKey> SecondarySort => this._secondary;

  /// <summary>Adds a tie-breaking column, or moves it to the end if it is already one.</summary>
  public void AddSortKey(ProcessField field, bool descending) {
    if (field == this.SortColumn)
      return;

    this._secondary.RemoveAll(key => key.Field == field);
    this._secondary.Add(new(field, descending));
  }

  /// <summary>Back to one sort column.</summary>
  public void ClearSecondarySort() => this._secondary.Clear();

  /// <summary>
  /// What the rows are grouped by (PRD §83).
  /// </summary>
  /// <remarks>
  /// Changing it leaves the collapsed headings alone. They are keyed by label, so grouping by user,
  /// looking at something else and coming back finds the same headings folded the same way.
  /// </remarks>
  public ProcessGrouping Grouping { get; set; }

  /// <summary>Nest children under their parent instead of showing one flat sorted list.</summary>
  /// <remarks>
  /// The tree is one of <see cref="ProcessGrouping"/>'s options rather than a flag of its own; this
  /// is the name everything from the settings file to the command line already calls it by.
  /// </remarks>
  public bool TreeMode {
    get => this.Grouping == ProcessGrouping.ParentTree;
    set => this.Grouping = value ? ProcessGrouping.ParentTree : ProcessGrouping.None;
  }

  /// <summary>The headings, in the order they appear (PRD §83). Empty unless something is grouped.</summary>
  public IReadOnlyList<ProcessGroup> Groups => this._groups;

  /// <summary>Whether a heading is folded shut.</summary>
  public bool IsGroupCollapsed(string label) {
    ArgumentNullException.ThrowIfNull(label);
    return this._collapsedGroups.Contains(label);
  }

  /// <summary>Folds a heading, or opens it. Returns whether anything changed.</summary>
  public bool SetGroupCollapsed(string label, bool collapsed) {
    ArgumentNullException.ThrowIfNull(label);
    return collapsed ? this._collapsedGroups.Add(label) : this._collapsedGroups.Remove(label);
  }

  /// <summary>
  /// Whether text matching distinguishes case (PRD §11).
  /// </summary>
  /// <remarks>
  /// Setting it re-parses the filter, because the comparison is baked into the parsed query rather
  /// than read at every row: a filter is matched once per process per sample, and a branch per
  /// comparison is a branch a thousand times a second.
  /// </remarks>
  public bool CaseSensitive {
    get => this._caseSensitive;
    set {
      if (this._caseSensitive == value)
        return;

      this._caseSensitive = value;
      this._query = ProcessQuery.ParseOrSubstring(this._filterText, value);
    }
  }

  private bool _caseSensitive;

  /// <summary>Whether this pid's children are hidden in tree mode.</summary>
  public bool IsCollapsed(int pid) => this._collapsed.Contains(pid);

  /// <summary>Hides or shows the children of a pid. Returns whether anything changed.</summary>
  public bool SetCollapsed(int pid, bool collapsed)
    => collapsed ? this._collapsed.Add(pid) : this._collapsed.Remove(pid);

  /// <summary>Everything expanded again.</summary>
  public void ExpandAll() => this._collapsed.Clear();

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
      this._query = ProcessQuery.ParseOrSubstring(value, this._caseSensitive);
    }
  }

  private string? _filterText;
  private ProcessQuery _query = ProcessQuery.Empty;

  /// <summary>The parsed filter, so a caller can report what was wrong with it.</summary>
  public ProcessQuery Query => this._query;

  /// <summary>Show only this user's processes; null for every user.</summary>
  public int? UserIdFilter { get; set; }

  /// <summary>The rows to draw, in order. Some of them may be headings rather than processes.</summary>
  public ReadOnlySpan<ViewRow> Rows => this._rows.AsSpan(0, this.RowCount);

  /// <summary>How many rows there are to draw, headings included.</summary>
  public int RowCount { get; private set; }

  /// <summary>
  /// How many of the rows are processes.
  /// </summary>
  /// <remarks>
  /// What "N of M processes" counts. A heading is not a process, and counting one would make a
  /// grouped list claim more processes than the machine is running (PRD §83).
  /// </remarks>
  public int MatchCount { get; private set; }

  /// <summary>How many processes the snapshot held, before filtering.</summary>
  public int TotalCount => this._snapshot?.ProcessCount ?? 0;

  /// <summary>Finds the row showing a given process, or -1. Used to keep a selection across samples.</summary>
  public int FindRow(ProcessKey key) {
    if (this._snapshot is null)
      return -1;

    var processes = this._snapshot.Processes;
    var rows = this.Rows;
    for (var i = 0; i < rows.Length; ++i)
      if (!rows[i].IsGroupHeader && processes[rows[i].Index].Key == key)
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

    switch (this.Grouping) {
      case ProcessGrouping.ParentTree: this.BuildTree(processes, count); break;
      case ProcessGrouping.None: this.BuildFlat(count); break;
      default: this.BuildGrouped(processes, delta, count); break;
    }
  }

  private void BuildFlat(int count) {
    this._groups.Clear();
    var written = 0;
    for (var i = 0; i < count; ++i)
      if (this._visible[i])
        this._order[written++] = i;

    Array.Sort(this._order, 0, written, this._comparer);
    for (var i = 0; i < written; ++i)
      this._rows[i] = new(this._order[i], 0, false);

    this.RowCount = this.MatchCount = written;
  }

  /// <summary>
  /// A heading per group, with its processes under it (PRD §83).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The groups come out in the order their first row does, which means the order the current sort
  /// put them in: sorted by CPU, the busiest user's heading is at the top. Ordering them
  /// alphabetically instead would answer a question nobody asked and bury the group somebody sorted
  /// the table to find.
  /// </para>
  /// <para>
  /// Two passes and no nested loop. Counting the members first and then placing each one at its
  /// group's running cursor is linear; walking the sorted list once per group is not, and "once per
  /// group" is once per executable on a machine with three hundred of them.
  /// </para>
  /// </remarks>
  private void BuildGrouped(ReadOnlySpan<ProcessRecord> processes, SnapshotDelta delta, int count) {
    var written = 0;
    for (var i = 0; i < count; ++i)
      if (this._visible[i])
        this._order[written++] = i;

    Array.Sort(this._order, 0, written, this._comparer);

    this._groups.Clear();
    this._groupIndex.Clear();
    EnsureLength(ref this._groupOf, count);
    for (var i = 0; i < written; ++i) {
      var index = this._order[i];
      var label = this.LabelOf(in processes[index], delta, index);
      if (!this._groupIndex.TryGetValue(label, out var group)) {
        group = this._groups.Count;
        this._groupIndex[label] = group;
        this._groups.Add(new(label, 0));
      }

      this._groups[group] = this._groups[group] with { Count = this._groups[group].Count + 1 };
      this._groupOf[index] = group;
    }

    // Where each heading lands, and where its first member goes. A folded group takes one row and
    // its members take none — they are still counted in the heading, because the count is a fact
    // about the machine rather than about what is on screen.
    EnsureLength(ref this._groupStart, this._groups.Count);
    EnsureLength(ref this._groupCursor, this._groups.Count);
    var running = 0;
    for (var group = 0; group < this._groups.Count; ++group) {
      this._groupStart[group] = running;
      this._groupCursor[group] = running + 1;
      running += 1 + (this._collapsedGroups.Contains(this._groups[group].Label) ? 0 : this._groups[group].Count);
    }

    EnsureLength(ref this._rows, running);
    for (var group = 0; group < this._groups.Count; ++group)
      this._rows[this._groupStart[group]] = new(-1, 0, true, group);

    for (var i = 0; i < written; ++i) {
      var index = this._order[i];
      var group = this._groupOf[index];
      if (this._collapsedGroups.Contains(this._groups[group].Label))
        continue;

      this._rows[this._groupCursor[group]++] = new(index, 1, false, group);
    }

    this.RowCount = running;
    // The process rows that were actually emitted, so a folded group takes its members out of the
    // count the way a collapsed subtree already does in the tree. The heading still says how many it
    // is hiding, which is where that number belongs.
    this.MatchCount = running - this._groups.Count;
  }

  /// <summary>
  /// The heading one process belongs under.
  /// </summary>
  /// <remarks>
  /// Read through <see cref="FieldAccessor"/> wherever the thing being grouped by is a field, so a
  /// heading says exactly what the column of the same name would (PRD §5.1). The fallbacks are
  /// statements rather than placeholders: a process in no container belongs under "not in a
  /// container", which is true, and not under an em dash, which would read as a value nobody could
  /// obtain (PRD §72.3).
  /// </remarks>
  private string LabelOf(in ProcessRecord process, SnapshotDelta delta, int index) {
    switch (this.Grouping) {
      // The same words the legend gives and the same call the palette makes, so a heading and a row
      // colour under it can never say different things about the same process.
      case ProcessGrouping.Category:
        return ProcessCategories.Describe(
          ProcessCategories.Classify(in process, ProcessCategories.CurrentUserId, delta.IsNew(index))
        );

      case ProcessGrouping.User:
        return FieldAccessor.RawText(ProcessField.UserName, in process, delta, index)
          ?? (process.UserId >= 0
            ? "uid " + process.UserId.ToString(CultureInfo.InvariantCulture)
            : "user unknown");

      case ProcessGrouping.Session:
        return "session " + process.SessionId.ToString(CultureInfo.InvariantCulture);

      // A unit rather than the slice above it: see CgroupUnit for why the innermost one is the answer.
      case ProcessGrouping.Service:
        return CgroupUnit.Of(process.ContainerPath) ?? "not a service";

      case ProcessGrouping.Executable:
        return FieldAccessor.RawText(ProcessField.ExecutableName, in process, delta, index)
          ?? "no executable";

      case ProcessGrouping.Container:
        return FieldAccessor.RawText(ProcessField.ContainerId, in process, delta, index)
          ?? "not in a container";

      // Reading a package costs a database lookup per image, so it is collected only when the run
      // asked for it (PRD §5.4). "Nobody looked" and "nothing claims this file" are different
      // answers, and only the second of them is a finding — a heading that said "not packaged" for
      // a session that never looked would be the confident zero this project keeps finding
      // (PRD §72.3).
      case ProcessGrouping.Package:
        return FieldAccessor.RawText(ProcessField.Package, in process, delta, index)
          // Not the reason on its own: a record nobody filled in carries the default reason, which
          // reads "the value is present". PackageSource.Unknown is the signal, exactly as that type
          // says it is.
          ?? (process.Package.Reason is UnknownReason.None or UnknownReason.NotSampledYet
            ? "package not looked up"
            : "package unknown — " + Humanize.Placeholder(process.Package.Reason));

      default:
        return FieldAccessor.RawText(ProcessField.Container, in process, delta, index) ?? "no cgroup";
    }
  }

  private void BuildTree(ReadOnlySpan<ProcessRecord> processes, int count) {
    this._groups.Clear();
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
      // A collapsed row still says it has children — that is the whole point of the marker — it just
      // does not put them on screen (PRD §57.3).
      if (childCount == 0 || this._collapsed.Contains(processes[index].Pid))
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

    // Every row in a tree is a process; a collapsed subtree is rows nobody can see rather than rows
    // that are not processes.
    this.MatchCount = this.RowCount;
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
    if (result == 0 && this._secondary.Count > 0) {
      var processes = this._snapshot!.Processes;
      foreach (var key in this._secondary) {
        var tie = FieldAccessor.Compare(key.Field, in processes[left], left, in processes[right], right, this._delta);
        if (tie == 0)
          continue;

        // Each key carries its own direction, so "state ascending, memory descending" is one order
        // and not two conflicting ones.
        return key.Descending ? -tie : tie;
      }
    }

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
