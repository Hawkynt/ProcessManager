namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Where an overlay ended up, so a click can be turned back into a line.</summary>
public readonly record struct OverlayPlacement(int ContentTop, int Left, int Width, int Height, int FirstIndex);

/// <summary>One line of an overlay: a heading, or something that can be chosen.</summary>
/// <param name="Tag">Whatever the opener needs to identify this line again.</param>
public readonly record struct OverlayItem(string Label, string Hint, bool IsHeading, bool Checked, int Tag, bool Checkable = false) {

  public static OverlayItem Heading(string label) => new(label, string.Empty, true, false, -1);

  /// <summary>Something that happens when it is chosen: no checkbox, because it has no state.</summary>
  public static OverlayItem Entry(string label, string hint, int tag)
    => new(label, hint, false, false, tag);

  /// <summary>Something that is on or off, and stays that way.</summary>
  public static OverlayItem Toggle(string label, string hint, int tag, bool ticked)
    => new(label, hint, false, ticked, tag, Checkable: true);

}

/// <summary>
/// A list drawn over the table: the action menu, the column chooser and the help page (PRD §57.5).
/// </summary>
/// <remarks>
/// One class for all three because they are the same thing — a titled list with a cursor — and three
/// of them would be three sets of scrolling arithmetic to get wrong. It draws into the same screen
/// buffer as everything else, so an overlay is part of the frame the diff renderer sends rather than
/// a separate write that would tear.
/// </remarks>
public sealed class ListOverlay {

  private readonly List<OverlayItem> _items;

  public ListOverlay(string title, IEnumerable<OverlayItem> items, bool fullScreen = false) {
    ArgumentNullException.ThrowIfNull(items);
    this.Title = title;
    this._items = [.. items];
    this.FullScreen = fullScreen;
    this.Selected = this.FirstSelectable(0, 1);
  }

  /// <summary>
  /// Where the second column starts, or zero to push it against the right edge.
  /// </summary>
  /// <remarks>
  /// A key and what it does belong next to each other; a hundred columns of nothing between them is
  /// a table nobody can read across. A menu is the other way round — its keys are an aside, and they
  /// belong out of the way at the edge.
  /// </remarks>
  public int HintColumn { get; init; }

  public string Title { get; }

  public bool FullScreen { get; }

  public int Selected { get; private set; }

  public int Count => this._items.Count;

  public IReadOnlyList<OverlayItem> Items => this._items;

  /// <summary>The chosen line, or null when the cursor is on a heading or the list is empty.</summary>
  public OverlayItem? Current
    => (uint)this.Selected < (uint)this._items.Count && !this._items[this.Selected].IsHeading
      ? this._items[this.Selected]
      : null;

  private int _scroll;

  public void MoveBy(int delta) {
    if (this._items.Count == 0)
      return;

    var step = Math.Sign(delta);
    for (var remaining = Math.Abs(delta); remaining > 0; --remaining) {
      var next = this.FirstSelectable(this.Selected + step, step);
      if (next < 0)
        break;

      this.Selected = next;
    }
  }

  public void MoveTo(int index) {
    if ((uint)index >= (uint)this._items.Count || this._items[index].IsHeading)
      return;

    this.Selected = index;
  }

  /// <summary>Replaces one line, for a chooser whose ticks change as they are pressed.</summary>
  public void Replace(int index, OverlayItem item) {
    if ((uint)index < (uint)this._items.Count)
      this._items[index] = item;
  }

  private int FirstSelectable(int from, int step) {
    for (var i = from; (uint)i < (uint)this._items.Count; i += step)
      if (!this._items[i].IsHeading)
        return i;

    return -1;
  }

  /// <summary>Draws the overlay and returns the rows it covered, for the mouse to hit-test against.</summary>
  public OverlayPlacement Draw(TerminalScreen screen, bool unicode) {
    ArgumentNullException.ThrowIfNull(screen);

    var contentWidth = this.Title.Length + 2;
    foreach (var item in this._items)
      contentWidth = Math.Max(contentWidth, item.Label.Length + item.Hint.Length + 6);

    // A hint column past the right-hand edge silently drew nothing: the clip below took a negative
    // width and there was no hint at all, with nothing to say so. The box is as wide as it needs to
    // be for the widest label and its hint, so a column that does not fit is a caller's arithmetic
    // being wrong rather than a request to hide the hints.
    if (this.HintColumn > 0)
      contentWidth = Math.Max(contentWidth, this.HintColumn + 8);

    var width = this.FullScreen ? screen.Width : Math.Min(screen.Width - 2, Math.Max(24, contentWidth + 4));
    var height = this.FullScreen
      ? screen.Height
      : Math.Min(screen.Height - 2, this._items.Count + 4);

    var left = this.FullScreen ? 0 : Math.Max(0, (screen.Width - width) / 2);
    var top = this.FullScreen ? 0 : Math.Max(0, (screen.Height - height) / 2);
    var rows = Math.Max(1, height - (this.FullScreen ? 3 : 4));
    if (width <= 4 || height <= 2)
      // Nothing this small can hold a list. The frame beneath stays on screen, which is more use
      // than a border with no room inside it.
      return new(top, left, Math.Max(0, width), Math.Max(0, height), this._scroll);

    if (this.Selected < this._scroll)
      this._scroll = this.Selected;
    else if (this.Selected >= this._scroll + rows)
      this._scroll = this.Selected - rows + 1;

    this._scroll = Math.Clamp(this._scroll, 0, Math.Max(0, this._items.Count - rows));

    this.DrawFrame(screen, left, top, width, height, unicode);

    var first = top + (this.FullScreen ? 2 : 2);
    for (var line = 0; line < rows; ++line) {
      var index = this._scroll + line;
      if (index >= this._items.Count)
        break;

      var item = this._items[index];
      var y = first + line;
      var x = left + 2;
      if (item.IsHeading) {
        screen.Write(x, y, Clip(item.Label, width - 4), Attributes.Accent);
        continue;
      }

      var selected = index == this.Selected;
      if (selected)
        screen.Fill(left + 1, y, width - 2, ' ', Attributes.Selected);

      // The tick is a character, not a colour: on a monochrome terminal a colour-only checkbox is
      // not a checkbox at all (PRD §57.4).
      var label = item.Checkable ? (item.Checked ? "[x] " : "[ ] ") + item.Label : item.Label;
      var hintAttribute = selected ? Attributes.Selected : Attributes.Dim;
      if (this.HintColumn > 0) {
        screen.Write(x, y, Clip(label, this.HintColumn - 1), selected ? Attributes.Selected : Attributes.Accent);
        screen.Write(x + this.HintColumn, y, Clip(item.Hint, width - 4 - this.HintColumn), hintAttribute);
        continue;
      }

      screen.Write(x, y, Clip(label, width - 4), selected ? Attributes.Selected : Attributes.Normal);
      if (item.Hint.Length > 0 && width - 4 > label.Length + item.Hint.Length + 1)
        screen.WriteRight(x, y, width - 5, item.Hint, hintAttribute);
    }

    if (this._items.Count > rows)
      screen.WriteRight(left, top + height - 1, width - 2, $"{this._scroll + 1}–{Math.Min(this._scroll + rows, this._items.Count)} of {this._items.Count}", Attributes.Dim);

    return new(first, left, width, height, this._scroll);
  }

  private void DrawFrame(TerminalScreen screen, int left, int top, int width, int height, bool unicode) {
    var horizontal = unicode ? '─' : '-';
    var vertical = unicode ? '│' : '|';

    for (var y = top; y < top + height; ++y)
      screen.Fill(left, y, width, ' ', Attributes.Normal);

    screen.Fill(left, top, width, horizontal, Attributes.Header);
    screen.Fill(left, top + height - 1, width, horizontal, Attributes.Dim);
    screen.Write(left + 2, top, $" {this.Title} ", Attributes.Header);
    for (var y = top + 1; y < top + height - 1; ++y) {
      screen.Write(left, y, [vertical], Attributes.Dim);
      screen.Write(left + width - 1, y, [vertical], Attributes.Dim);
    }
  }

  /// <summary>Which line a click at this row landed on, or -1.</summary>
  public int HitTest(OverlayPlacement placement, int x, int y) {
    if (x < placement.Left || x >= placement.Left + placement.Width)
      return -1;

    var index = placement.FirstIndex + (y - placement.ContentTop);
    return (uint)index < (uint)this._items.Count && !this._items[index].IsHeading ? index : -1;
  }

  private static string Clip(string text, int width)
    => width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];

}
