using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Where one column ended up on screen, so drawing and clicking cannot disagree.</summary>
public readonly record struct ColumnPlacement(ProcessField Field, int Index, int X, int Width, bool Frozen);

/// <summary>
/// The terminal's columns: which ones, in what order, how wide, which are pinned, and how far the
/// rest have been scrolled sideways (PRD §11, §57.2).
/// </summary>
/// <remarks>
/// <para>
/// One object answers both questions a table asks — what to draw at column <c>x</c>, and which column
/// a click at <c>x</c> landed on. They were a formatting loop and a hit test in two places once, and
/// the two disagreed by one character for every pinned column.
/// </para>
/// <para>
/// Widths start from <see cref="FieldRegistry"/> and are only ever moved from there by a person. A
/// column that measures itself against its widest value jitters every second as processes come and
/// go, so auto-sizing is a key, not a policy.
/// </para>
/// </remarks>
public sealed class ColumnLayout {

  private struct Column {
    public ProcessField Field;
    public int Width;
    public bool Visible;
  }

  private readonly List<Column> _columns = [];
  private ProcessField[] _defaults;

  public ColumnLayout(ReadOnlySpan<ProcessField> fields) {
    this._defaults = fields.ToArray();
    this.Reset();
  }

  /// <summary>How many leading columns stay put while the rest scroll (PRD §11, §57.2).</summary>
  public int Frozen { get; private set; } = 1;

  /// <summary>The first non-frozen column to draw — the horizontal scroll position.</summary>
  public int Scroll { get; private set; }

  /// <summary>The column the keyboard is on, for copy, resize and reorder.</summary>
  public int Current { get; private set; }

  /// <summary>True once anything here has been moved by hand, so a resize stops re-deciding it.</summary>
  public bool Customised { get; private set; }

  public int Count => this._columns.Count;

  public ProcessField FieldAt(int index) => this._columns[index].Field;

  public int WidthAt(int index) => this._columns[index].Width;

  public bool IsVisible(int index) => this._columns[index].Visible;

  public ProcessField CurrentField => this._columns[this.Current].Field;

  /// <summary>The columns a sort can cycle through — the drawn histories are not among them.</summary>
  public ProcessField[] Sortable {
    get {
      var result = new List<ProcessField>();
      // Not "field": inside a property accessor C# 14 reads that as the backing field.
      foreach (var column in this._columns)
        if (column.Visible && FieldRegistry.Get(column.Field).IsSortable)
          result.Add(column.Field);

      return [.. result];
    }
  }

  /// <summary>Replaces the set of columns — a named column set, or a narrower terminal's list.</summary>
  public void Apply(ReadOnlySpan<ProcessField> fields, bool asDefault = false) {
    if (asDefault)
      this._defaults = fields.ToArray();

    this._columns.Clear();
    foreach (var field in fields)
      this._columns.Add(new() { Field = field, Width = FieldRegistry.Get(field).TerminalWidth, Visible = true });

    this.Frozen = 1;
    this.Scroll = 0;
    this.Current = 0;
    this.Customised = !asDefault;
  }

  /// <summary>Back to the registry's widths, the opening order and nothing hidden (PRD §11).</summary>
  public void Reset() {
    this.Apply(this._defaults, asDefault: true);
    this.Customised = false;
  }

  public void SetVisible(int index, bool visible) {
    var column = this._columns[index];
    column.Visible = visible;
    this._columns[index] = column;
    this.Customised = true;
    this.ClampCurrent();
  }

  public void ToggleVisible(int index) => this.SetVisible(index, !this._columns[index].Visible);

  /// <summary>Moves the keyboard's column cursor, skipping whatever is hidden.</summary>
  public void MoveCurrent(int delta) {
    var step = Math.Sign(delta);
    if (step == 0)
      return;

    for (var remaining = Math.Abs(delta); remaining > 0; --remaining) {
      var next = this.Current;
      do {
        next += step;
        if ((uint)next >= (uint)this._columns.Count)
          return;
      } while (!this._columns[next].Visible);

      this.Current = next;
    }
  }

  /// <summary>Moves the current column past its neighbour, taking the cursor with it.</summary>
  public bool Reorder(int delta) {
    var target = this.Current + Math.Sign(delta);
    if ((uint)target >= (uint)this._columns.Count)
      return false;

    (this._columns[this.Current], this._columns[target]) = (this._columns[target], this._columns[this.Current]);
    this.Current = target;
    this.Customised = true;
    return true;
  }

  /// <summary>Widens or narrows the current column. Four characters is the narrowest useful one.</summary>
  public void ResizeCurrent(int delta) => this.SetWidth(this.Current, this._columns[this.Current].Width + delta);

  public void SetWidth(int index, int width) {
    var column = this._columns[index];
    column.Width = Math.Clamp(width, 3, 120);
    this._columns[index] = column;
    this.Customised = true;
  }

  /// <summary>
  /// Sets a column to the widest value the caller measured, header included.
  /// </summary>
  /// <remarks>
  /// The measurement comes from the rows on screen rather than from every process: the point of
  /// auto-sizing is that what is in front of you fits, and reading a thousand command lines to widen
  /// a column nobody is looking at costs a frame.
  /// </remarks>
  public void AutoSize(int index, int measured) {
    var descriptor = FieldRegistry.Get(this._columns[index].Field);
    // A drawn history has no text to measure, so it keeps whatever it was given.
    this.SetWidth(index, descriptor.IsGraph ? this._columns[index].Width : Math.Max(measured, descriptor.ShortHeader.Length + 1));
  }

  /// <summary>Pins every column up to and including the cursor, or unpins them all.</summary>
  public void ToggleFreeze() {
    var wanted = this.Current + 1;
    this.SetFrozen(this.Frozen == wanted ? 0 : wanted);
    this.Customised = true;
  }

  /// <summary>Pins the first <paramref name="count"/> columns — what the settings file restores.</summary>
  public void SetFrozen(int count) {
    this.Frozen = Math.Clamp(count, 0, this._columns.Count);
    this.Scroll = Math.Max(this.Scroll, this.Frozen);
  }

  public void ScrollBy(int delta) => this.Scroll = Math.Clamp(this.Scroll + delta, this.Frozen, Math.Max(this.Frozen, this._columns.Count - 1));

  /// <summary>
  /// Lays the visible columns out across <paramref name="screenWidth"/>.
  /// </summary>
  /// <returns>How many placements were written.</returns>
  public int Place(int screenWidth, Span<ColumnPlacement> destination) {
    Span<int> reserved = stackalloc int[this._columns.Count + 1];
    this.ReserveForTheColumnsAfterEachOne(reserved);

    var written = 0;
    var x = 0;
    for (var i = 0; i < this._columns.Count && written < destination.Length; ++i) {
      var frozen = i < this.Frozen;
      if (!frozen && i < this.Scroll)
        continue;
      if (!this._columns[i].Visible)
        continue;
      if (x >= screenWidth)
        break;

      // A column takes what it asks for, what is left, or what is left once the columns after it
      // have their share — whichever is smallest. The last clause is what stops the process name
      // from swallowing the line: it declares 120 characters because the window has them, and a
      // terminal that gave it all of those drew nothing at all of whatever somebody had ordered
      // after it. The floor keeps a squeezed column readable rather than letting it vanish.
      var available = screenWidth - x;
      var width = Math.Min(this._columns[i].Width, available);
      var after = reserved[i + 1];
      if (after > 0)
        // The floor is never more than the column asked for: a five-character CPU% column squeezed
        // up to a six-character minimum is a column that moved everything after it along by one.
        width = Math.Max(Math.Min(width, available - after), Math.Min(width, _MinimumWidth));

      destination[written++] = new(this._columns[i].Field, i, x, width, frozen);
      x += width + 1;
    }

    return written;
  }

  /// <summary>The width the columns drawn after each one need, so it can leave them room.</summary>
  private void ReserveForTheColumnsAfterEachOne(Span<int> reserved) {
    reserved[this._columns.Count] = 0;
    for (var i = this._columns.Count - 1; i >= 0; --i) {
      var drawn = this._columns[i].Visible && (i < this.Frozen || i >= this.Scroll);
      // Reserved at the declared width but no more than a squeezed one needs: reserving 120 for a
      // trailing name column would starve everything in front of it instead.
      reserved[i] = reserved[i + 1] + (drawn ? Math.Min(this._columns[i].Width, _MinimumWidth) + 1 : 0);
    }
  }

  /// <summary>What a column squeezed by its neighbours still gets.</summary>
  private const int _MinimumWidth = 6;

  /// <summary>Scrolls sideways until the cursor's column is on screen, the way a selection does.</summary>
  public void EnsureCurrentVisible(int screenWidth) {
    this.ClampCurrent();
    if (this.Current < this.Frozen)
      return;

    if (this.Current < this.Scroll) {
      this.Scroll = this.Current;
      return;
    }

    // Walk the scroll forward until the cursor fits. Widths differ per column, so this is a loop
    // rather than arithmetic — and it runs at most once per keystroke over a dozen columns.
    for (var guard = this._columns.Count; guard > 0; --guard) {
      if (this.EndOf(this.Current, screenWidth) <= screenWidth || this.Scroll >= this.Current)
        return;

      ++this.Scroll;
    }
  }

  private int EndOf(int index, int screenWidth) {
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[Math.Min(64, this._columns.Count)];
    var count = this.Place(screenWidth, placements);
    for (var i = 0; i < count; ++i)
      if (placements[i].Index == index)
        return placements[i].X + placements[i].Width;

    return int.MaxValue;
  }

  private void ClampCurrent() {
    if (this._columns.Count == 0)
      return;

    this.Current = Math.Clamp(this.Current, 0, this._columns.Count - 1);
    if (this._columns[this.Current].Visible)
      return;

    for (var i = this.Current; i < this._columns.Count; ++i)
      if (this._columns[i].Visible) {
        this.Current = i;
        return;
      }

    for (var i = this.Current; i >= 0; --i)
      if (this._columns[i].Visible) {
        this.Current = i;
        return;
      }
  }

  /// <summary>Finds the column under a click, or -1 for the margin past the last one.</summary>
  public int HitTest(int screenWidth, int x) {
    Span<ColumnPlacement> placements = stackalloc ColumnPlacement[Math.Min(64, Math.Max(1, this._columns.Count))];
    var count = this.Place(screenWidth, placements);
    for (var i = 0; i < count; ++i)
      // The separator after a column belongs to it, so clicking between two headers picks the left
      // one rather than nothing at all.
      if (x >= placements[i].X && x <= placements[i].X + placements[i].Width)
        return placements[i].Index;

    return -1;
  }

  public void SetCurrent(int index) {
    this.Current = index;
    this.ClampCurrent();
  }

}
