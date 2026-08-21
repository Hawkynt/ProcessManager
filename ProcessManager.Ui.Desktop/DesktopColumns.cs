using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The window's columns: which ones, in what order, how wide, and which one the keyboard is on
/// (PRD §11).
/// </summary>
/// <remarks>
/// <para>
/// The terminal has had all of this since it grew a mouse; the window had a list of fields and
/// nothing else, so every §11 column row read "terminal only". This is the same model on this side —
/// deliberately a plain object rather than state smeared across the form, because the arithmetic
/// (which column a pointer landed on, where a dropped column goes) is the part that gets a table
/// wrong by one column and is the part a test can reach without a display.
/// </para>
/// <para>
/// Widths start from <see cref="FieldRegistry"/> and are only ever moved from there by a person. A
/// column that measured itself against its widest value would jitter every second as processes come
/// and go, so auto-sizing is a command, not a policy — the same call the terminal makes.
/// </para>
/// </remarks>
public sealed class DesktopColumns {

  /// <summary>What a column may be squeezed to, and stretched to, in pixels.</summary>
  /// <remarks>
  /// The floor is about four characters: narrower than that and a column shows the ellipsis and
  /// nothing else, which is a column that has been hidden by accident rather than resized. The
  /// ceiling exists because a command line is unbounded and a single drag should not be able to push
  /// every other column off the right-hand edge for good.
  /// </remarks>
  public const int MinimumWidth = 28;

  public const int MaximumWidth = 1200;

  private readonly List<Column> _columns = [];

  private struct Column {
    public ProcessField Field;
    public int Width;
  }

  public DesktopColumns(IEnumerable<ProcessField> fields) => this.Apply(fields);

  /// <summary>The column a resize, a copy or a move acts on.</summary>
  public int Current { get; private set; }

  /// <summary>
  /// How many leading columns stay put while the rest scroll sideways (PRD §11).
  /// </summary>
  /// <remarks>
  /// The leading run and not "whichever columns are ticked": a pinned third column with two
  /// scrolling ones in front of it leaves a hole beside it that nothing can fill, which is why the
  /// toolkit stops its pinned run at the first column that is not pinned. One by default, the same
  /// as the terminal — a table scrolled sideways with no name column left on it is a table of
  /// numbers belonging to nobody.
  /// </remarks>
  public int Frozen { get; private set; } = 1;

  /// <summary>True once anything here has been moved by hand, so the file is worth writing.</summary>
  public bool Customised { get; private set; }

  public int Count => this._columns.Count;

  public ProcessField FieldAt(int index) => this._columns[index].Field;

  public int WidthAt(int index) => this._columns[index].Width;

  public ProcessField CurrentField => this._columns[Math.Clamp(this.Current, 0, this._columns.Count - 1)].Field;

  /// <summary>The fields in their current order, for the settings file and for an export.</summary>
  public ProcessField[] Fields {
    get {
      var result = new ProcessField[this._columns.Count];
      for (var i = 0; i < this._columns.Count; ++i)
        result[i] = this._columns[i].Field;

      return result;
    }
  }

  /// <summary>The widths somebody chose, keyed by field, so the file can carry them.</summary>
  public IReadOnlyList<KeyValuePair<ProcessField, int>> ChosenWidths {
    get {
      var result = new List<KeyValuePair<ProcessField, int>>();
      foreach (var column in this._columns)
        if (column.Width != FieldRegistry.Get(column.Field).DesktopWidth)
          result.Add(new(column.Field, column.Width));

      return result;
    }
  }

  /// <summary>
  /// Replaces the set of columns, keeping the width of any that survive.
  /// </summary>
  /// <remarks>
  /// Keeping the widths is the point. Ticking one more column in the chooser must not undo the six
  /// widths somebody spent a minute setting, which is what rebuilding from the registry every time
  /// would do.
  /// </remarks>
  public void Apply(IEnumerable<ProcessField> fields) {
    ArgumentNullException.ThrowIfNull(fields);

    var kept = new Dictionary<ProcessField, int>();
    foreach (var column in this._columns)
      kept[column.Field] = column.Width;

    this._columns.Clear();
    foreach (var field in fields)
      this._columns.Add(new() {
        Field = field,
        Width = kept.TryGetValue(field, out var width) ? width : FieldRegistry.Get(field).DesktopWidth,
      });

    this.ClampCurrent();
    this.ClampFrozen();
  }

  /// <summary>Whether the column at this index is one of the pinned ones (PRD §11).</summary>
  public bool IsFrozen(int index) => index < this.Frozen;

  /// <summary>
  /// Pins every column up to and including the cursor, or unpins the lot.
  /// </summary>
  /// <remarks>
  /// The same gesture the terminal's <c>#</c> makes, and deliberately the same arithmetic: pressing
  /// it on the column that is already the last pinned one unpins everything, so one key both pins
  /// and releases and nobody has to find a second one.
  /// </remarks>
  public void ToggleFreeze() {
    var wanted = this.Current + 1;
    this.SetFrozen(this.Frozen == wanted ? 0 : wanted);
    this.Customised = true;
  }

  /// <summary>Pins the first <paramref name="count"/> columns — what the settings file restores.</summary>
  public void SetFrozen(int count) {
    this.Frozen = Math.Clamp(count, 0, this._columns.Count);
  }

  /// <summary>Sets a width somebody chose earlier, for a column that is showing.</summary>
  public void Restore(ProcessField field, int width) {
    for (var i = 0; i < this._columns.Count; ++i)
      if (this._columns[i].Field == field) {
        var column = this._columns[i];
        column.Width = Math.Clamp(width, MinimumWidth, MaximumWidth);
        this._columns[i] = column;
        return;
      }
  }

  /// <summary>Back to the registry's widths and the opening order (PRD §11).</summary>
  public void Reset(IEnumerable<ProcessField> defaults) {
    ArgumentNullException.ThrowIfNull(defaults);

    this._columns.Clear();
    foreach (var field in defaults)
      this._columns.Add(new() { Field = field, Width = FieldRegistry.Get(field).DesktopWidth });

    this.Current = 0;
    this.Frozen = 1;
    this.Customised = false;
  }

  public void SetCurrent(int index) {
    this.Current = index;
    this.ClampCurrent();
  }

  public void MoveCurrent(int delta) {
    this.Current += delta;
    this.ClampCurrent();
  }

  /// <summary>
  /// Moves the current column past its neighbour, taking the cursor with it (PRD §11).
  /// </summary>
  /// <returns>False at either end, where there is nothing to swap with.</returns>
  public bool Reorder(int delta) {
    var target = this.Current + Math.Sign(delta);
    if (delta == 0 || (uint)target >= (uint)this._columns.Count)
      return false;

    (this._columns[this.Current], this._columns[target]) = (this._columns[target], this._columns[this.Current]);
    this.Current = target;
    this.Customised = true;
    return true;
  }

  /// <summary>Takes the column at one index out and drops it in front of another — a header drag.</summary>
  public bool MoveTo(int from, int to) {
    if ((uint)from >= (uint)this._columns.Count || (uint)to >= (uint)this._columns.Count || from == to)
      return false;

    var moved = this._columns[from];
    this._columns.RemoveAt(from);
    this._columns.Insert(to, moved);
    this.Current = to;
    this.Customised = true;
    return true;
  }

  public void SetWidth(int index, int width) {
    if ((uint)index >= (uint)this._columns.Count)
      return;

    var column = this._columns[index];
    var wanted = Math.Clamp(width, MinimumWidth, MaximumWidth);
    if (column.Width == wanted)
      return;

    column.Width = wanted;
    this._columns[index] = column;
    this.Customised = true;
  }

  public void ResizeCurrent(int delta) => this.SetWidth(this.Current, this.WidthAt(this.Current) + delta);

  /// <summary>
  /// Sets a column to the widest value the caller measured, header included.
  /// </summary>
  /// <remarks>
  /// The measurement comes from the rows on screen rather than from every process, which is what
  /// auto-sizing means here and in the terminal: the point is that what is in front of you fits, and
  /// measuring a thousand command lines to widen a column nobody is looking at costs a frame.
  /// <para>
  /// A drawn history has no text, so it keeps the width it was given rather than collapsing to the
  /// width of its own empty string.
  /// </para>
  /// </remarks>
  public void AutoSize(int index, int measured) {
    if ((uint)index >= (uint)this._columns.Count)
      return;

    if (FieldRegistry.Get(this._columns[index].Field).IsGraph)
      return;

    this.SetWidth(index, measured);
  }

  /// <summary>
  /// Which column an x in the header's own coordinates landed on, or -1 for no column at all.
  /// </summary>
  /// <remarks>
  /// Anything past the last boundary is the last column, because the table stretches its final
  /// column to fill whatever width is left over. Answering -1 out there instead would make a click
  /// on the right-hand half of the widest column on screen do nothing.
  /// </remarks>
  public int HitTest(int x) {
    if (this._columns.Count == 0 || x < 0)
      return -1;

    var left = 0;
    for (var i = 0; i < this._columns.Count; ++i) {
      if (x >= left && x < left + this._columns[i].Width)
        return i;

      left += this._columns[i].Width;
    }

    return this._columns.Count - 1;
  }

  /// <summary>Where a column's left edge is, in the header's own coordinates.</summary>
  public int LeftOf(int index) {
    var left = 0;
    for (var i = 0; i < index && i < this._columns.Count; ++i)
      left += this._columns[i].Width;

    return left;
  }

  /// <summary>
  /// The column whose right-hand edge is within a few pixels of <paramref name="x"/>, or -1.
  /// </summary>
  /// <remarks>
  /// What a drag near a boundary means: grabbing the edge resizes, grabbing the middle reorders. The
  /// grip is deliberately wider than a pixel — a person aiming at a one-pixel line misses, and a
  /// miss here starts a column move instead of a resize.
  /// </remarks>
  public int EdgeAt(int x, int grip = 4) {
    var left = 0;
    for (var i = 0; i < this._columns.Count; ++i) {
      left += this._columns[i].Width;
      if (Math.Abs(x - left) <= grip)
        return i;
    }

    return -1;
  }

  private void ClampCurrent()
    => this.Current = this._columns.Count == 0 ? 0 : Math.Clamp(this.Current, 0, this._columns.Count - 1);

  /// <summary>
  /// Keeps the pinned run inside the column list.
  /// </summary>
  /// <remarks>
  /// Ticking six columns down to three must not leave three columns pinned out of two, which the
  /// toolkit would read as "pin everything" and then refuse to scroll at all.
  /// </remarks>
  private void ClampFrozen() => this.Frozen = Math.Clamp(this.Frozen, 0, this._columns.Count);

}
