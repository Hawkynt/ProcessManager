using System.Text;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>How much colour the terminal admits to having.</summary>
public enum ColorDepth : byte { None, Ansi16, Ansi256, TrueColor }

/// <summary>
/// A character cell buffer that writes only what changed.
/// </summary>
/// <remarks>
/// <para>
/// Repainting eighty rows of eight columns every second is four thousand cells; over SSH that is a
/// monitor that costs more bandwidth than the thing it monitors. Two buffers are kept, the new frame
/// is composed into one, and only the runs of cells that differ are written — with the cursor moved
/// once per run rather than once per cell (PRD §11).
/// </para>
/// <para>
/// Nothing here queries the terminal or reads input; it composes and flushes. That keeps it testable
/// against a golden frame with no terminal at all (PRD §9.6).
/// </para>
/// </remarks>
public sealed class TerminalScreen {

  private char[] _front = [];
  private char[] _back = [];
  private byte[] _frontAttribute = [];
  private byte[] _backAttribute = [];
  private readonly StringBuilder _output = new(16 * 1024);
  private byte _currentAttribute = Attributes.Normal;

  public TerminalScreen(int width, int height, ColorDepth depth = ColorDepth.Ansi256) {
    this.Depth = depth;
    this.Resize(width, height);
  }

  public int Width { get; private set; }
  public int Height { get; private set; }
  public ColorDepth Depth { get; set; }

  /// <summary>
  /// Whether the next flush repaints everything. Set by a resize, and by the caller after anything
  /// else has written to the terminal underneath us.
  /// </summary>
  public bool NeedsFullRepaint { get; set; } = true;

  public void Resize(int width, int height) {
    width = Math.Max(1, width);
    height = Math.Max(1, height);
    if (width == this.Width && height == this.Height)
      return;

    this.Width = width;
    this.Height = height;
    var cells = width * height;
    this._front = new char[cells];
    this._back = new char[cells];
    this._frontAttribute = new byte[cells];
    this._backAttribute = new byte[cells];
    Array.Fill(this._front, ' ');
    Array.Fill(this._back, ' ');
    this.NeedsFullRepaint = true;
  }

  /// <summary>Blanks the frame being composed. The previous frame is kept, for the diff.</summary>
  public void BeginFrame() {
    Array.Fill(this._back, ' ');
    Array.Clear(this._backAttribute);
  }

  /// <summary>Writes text at a position, clipped to the screen. Returns the column after it.</summary>
  public int Write(int x, int y, ReadOnlySpan<char> text, byte attribute = Attributes.Normal) {
    if ((uint)y >= (uint)this.Height)
      return x;

    var offset = y * this.Width;
    for (var i = 0; i < text.Length; ++i) {
      var column = x + i;
      if (column < 0)
        continue;
      if (column >= this.Width)
        return this.Width;

      this._back[offset + column] = text[i];
      this._backAttribute[offset + column] = attribute;
    }

    return x + text.Length;
  }

  /// <summary>Writes text right-aligned in a field of <paramref name="width"/> ending at x+width.</summary>
  public void WriteRight(int x, int y, int width, ReadOnlySpan<char> text, byte attribute = Attributes.Normal) {
    // A field with no width is a field nothing fits in, which a terminal narrower than its own
    // furniture really does produce. Writing nothing is the answer; the alternative was an exception
    // from the slice below.
    if (width <= 0)
      return;

    // A value too long for its column loses its head, not its tail: the significant digits of a
    // number and the file name of a path are both at the end.
    if (text.Length > width)
      text = text[^width..];

    this.Write(x + width - text.Length, y, text, attribute);
  }

  /// <summary>Fills a run of cells, for bars and separators.</summary>
  public void Fill(int x, int y, int count, char c, byte attribute = Attributes.Normal) {
    if ((uint)y >= (uint)this.Height)
      return;

    var offset = y * this.Width;
    for (var i = 0; i < count; ++i) {
      var column = x + i;
      if ((uint)column >= (uint)this.Width)
        break;

      this._back[offset + column] = c;
      this._backAttribute[offset + column] = attribute;
    }
  }

  /// <summary>
  /// Writes the difference between the composed frame and the one on screen, and swaps them.
  /// </summary>
  public void Flush(TextWriter writer) {
    this._output.Clear();
    if (this.NeedsFullRepaint) {
      this._output.Append(Ansi.ClearScreen);
      Array.Fill(this._front, '\0');
    }

    for (var y = 0; y < this.Height; ++y) {
      var offset = y * this.Width;
      var x = 0;
      while (x < this.Width) {
        var index = offset + x;
        if (this._back[index] == this._front[index] && this._backAttribute[index] == this._frontAttribute[index]) {
          ++x;
          continue;
        }

        // One cursor move per run of changed cells, not per cell: a scrolling plot changes one
        // column of every row, and moving the cursor 80 times costs more than the text does.
        var runStart = x;
        this._output.Append(Ansi.MoveTo(y + 1, x + 1));
        while (x < this.Width) {
          var runIndex = offset + x;
          if (x > runStart
              && this._back[runIndex] == this._front[runIndex]
              && this._backAttribute[runIndex] == this._frontAttribute[runIndex])
            break;

          var attribute = this._backAttribute[runIndex];
          if (attribute != this._currentAttribute) {
            this._output.Append(Attributes.ToAnsi(attribute, this.Depth));
            this._currentAttribute = attribute;
          }

          this._output.Append(this._back[runIndex]);
          ++x;
        }
      }
    }

    if (this._currentAttribute != Attributes.Normal) {
      this._output.Append(Ansi.Reset);
      this._currentAttribute = Attributes.Normal;
    }

    if (this._output.Length > 0)
      writer.Write(this._output);

    writer.Flush();
    (this._front, this._back) = (this._back, this._front);
    (this._frontAttribute, this._backAttribute) = (this._backAttribute, this._frontAttribute);
    this.NeedsFullRepaint = false;
  }

  /// <summary>
  /// The composed frame's attributes, one byte per cell, row-major.
  /// </summary>
  /// <remarks>
  /// For the screenshot writer, which needs the colours as well as the characters. The golden test
  /// deliberately compares only <see cref="Capture"/> — a frame's *text* is the thing whose change is
  /// worth failing a build over, and pinning its colours too would make every palette tweak a golden
  /// update.
  /// </remarks>
  public byte[] CaptureAttributes() {
    var result = new byte[this.Width * this.Height];
    Array.Copy(this._backAttribute, result, result.Length);
    return result;
  }

  /// <summary>The composed frame as text, for the golden-frame test (PRD §9.6).</summary>
  public string Capture() {
    var builder = new StringBuilder((this.Width + 1) * this.Height);
    for (var y = 0; y < this.Height; ++y) {
      var line = new string(this._back, y * this.Width, this.Width).TrimEnd();
      builder.Append(line).Append('\n');
    }

    return builder.ToString();
  }

}
