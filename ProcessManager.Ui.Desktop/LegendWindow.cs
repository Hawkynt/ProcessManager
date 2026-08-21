using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What every colour in the process list means.
/// </summary>
/// <remarks>
/// <para>
/// PRD §7.1: a colour no dialog explains is decoration. Every category the list can paint is in here
/// with its swatch and a sentence, and the categories the program deliberately does not distinguish
/// are named at the bottom so their absence is a decision rather than an oversight.
/// </para>
/// <para>
/// The two cell marks of §23 are here for the same reason, and they were not: the window painted a
/// warm and a hot wash that no dialog anywhere admitted to, which is the definition this file opens
/// with. They also read differently from the row colours — a row's colour is a one-of-many answer
/// about what a process <em>is</em>, and a cell's mark is about what it is <em>doing</em> this
/// second — so they are a section of their own rather than nine more swatches in one list.
/// </para>
/// </remarks>
public sealed class LegendWindow : Form {

  private const int _Margin = 14;

  /// <summary>
  /// How far apart the swatch rows sit.
  /// </summary>
  /// <remarks>
  /// Twenty-four rather than twenty-six, and that is a screen-size decision rather than a taste one.
  /// This window may not be shrunk below what it holds — the whole point of computing its height —
  /// so the height it computes has to fit the smallest display anybody runs a desktop on. Twelve
  /// swatches and a three-paragraph note at twenty-six came to 776 pixels, which does not fit a
  /// 1366×768 laptop once a title bar is on it, and the buttons would have been off the bottom of
  /// the screen with no way to drag them back.
  /// </remarks>
  private const int _RowHeight = 24;

  private const int _SwatchWidth = 28;
  private const int _TextLeft = 52;

  /// <summary>
  /// How tall one line of the closing note is drawn, and how wide the window is.
  /// </summary>
  /// <remarks>
  /// Both were guesses, and both were wrong in the way only a photograph shows. The note was sized at
  /// sixteen pixels a line against a toolkit that draws them nineteen apart, so its last two lines
  /// were underneath the buttons; and every sentence past sixty-odd characters — which is both of the
  /// band descriptions and half the note — was drawn with an ellipsis on the end, in a box 460 pixels
  /// wide inside a window 520 wide. A legend whose sentences stop mid-word is the thing this window
  /// exists to prevent, one level up (PRD §9.6, §45.9).
  /// </remarks>
  private const int _NoteLineHeight = 20;

  private const int _Width = 760;

  /// <summary>What is left for a sentence that starts beside a swatch.</summary>
  private const int _TextWidth = _Width - _TextLeft - _Margin;

  /// <summary>
  /// Every row colour the list can paint, in the order the window explains them.
  /// </summary>
  /// <remarks>
  /// The transient ones first, then the permanent, then the two that only appear when an opt-in field
  /// is switched on, then the fallback. A test walks <see cref="ProcessCategory"/> against
  /// <see cref="RowPalette.BackColorOf"/> and fails if the table can paint something this list has
  /// not got — which is the check the file's opening paragraph asks for, made mechanical.
  /// </remarks>
  private static readonly ProcessCategory[] _Categories = [
    ProcessCategory.New,
    ProcessCategory.Exited,
    ProcessCategory.Own,
    ProcessCategory.System,
    ProcessCategory.Elevated,
    ProcessCategory.Service,
    ProcessCategory.Suspended,
    ProcessCategory.Zombie,
    ProcessCategory.ImageReplaced,
    ProcessCategory.Packaged,
    ProcessCategory.ManagedRuntime,
    ProcessCategory.Other,
  ];

  /// <summary>
  /// The closing note, one array entry per drawn line.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Wrapped by hand because the label draws what it is given; as an array rather than one string with
  /// newlines in it so that the window can count the lines and give itself room for them. Written out
  /// as a paragraph it was eight lines in a box ninety-two pixels tall, and the ninth would have gone
  /// under the buttons without anything failing.
  /// </para>
  /// <para>
  /// The last two paragraphs are the ones that have to stay true. Naming what the program refuses to
  /// distinguish is what makes an absent colour a decision rather than an oversight, and the list
  /// shortens as categories become provable — packaged and managed-runtime were on it until the
  /// package and runtime readings existed to prove them (PRD §23).
  /// </para>
  /// </remarks>
  private static readonly string[] _Note = [
    "The mark goes on the cell, not the row: the row already says what kind of process this is, and one",
    "wash for both would mean one of those facts quietly winning. The number stays legible under either",
    "mark, so the table reads with no colour at all.",
    "",
    "Packaged and managed-runtime rows appear only once the package or runtime field is switched on.",
    "Both cost a read, and nothing is claimed about a process nobody asked about.",
    "",
    "Not distinguished: packed, unsigned, invalid-signature and suspicious processes. A Linux binary",
    "carries no signature to check — what signs one is its package — and a colour that is sometimes",
    "right is worse than none.",
  ];

  private readonly Button _thresholds = new() { Text = "Thresholds…" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly Label _warmText = new();
  private readonly Label _hotText = new();

  private UsageThresholds _heat;

  /// <param name="heat">
  /// The bands as they currently stand, so the sentences beside the two washes are the numbers this
  /// window is actually judging by rather than the ones it shipped with.
  /// </param>
  public LegendWindow(UsageThresholds heat) {
    this._heat = heat;

    this.Text = "Colour legend";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;

    var y = _Margin;
    y = this.AddHeading("Row colour — what kind of process this is", y);

    foreach (var category in _Categories) {
      var swatch = new CategorySwatch(category) { Bounds = new(_Margin, y, _SwatchWidth, 18) };
      this.Controls.Add(swatch);
      this.Controls.Add(new Label {
        Text = ProcessCategories.Describe(category),
        Bounds = new(_TextLeft, y, _TextWidth, 18),
      });

      y += _RowHeight;
    }

    y += 8;
    y = this.AddHeading("Cell mark — how hard it is leaning on one resource", y);

    this.Controls.Add(new HeatSwatch(UsageHeat.Warm) { Bounds = new(_Margin, y, _SwatchWidth, 18) });
    this._warmText.Bounds = new(_TextLeft, y, _TextWidth, 18);
    this.Controls.Add(this._warmText);
    y += _RowHeight;

    this.Controls.Add(new HeatSwatch(UsageHeat.Hot) { Bounds = new(_Margin, y, _SwatchWidth, 18) });
    this._hotText.Bounds = new(_TextLeft, y, _TextWidth, 18);
    this.Controls.Add(this._hotText);
    y += _RowHeight;

    var noteHeight = _Note.Length * _NoteLineHeight;
    this.Controls.Add(new Label {
      Bounds = new(_Margin, y + 4, _Width - (_Margin * 2), noteHeight),
      Text = string.Join('\n', _Note),
    });

    this._thresholds.Click += (_, _) => this.EditThresholds();
    this._close.Click += (_, _) => this.Close();
    this.Controls.Add(this._thresholds);
    this.Controls.Add(this._close);

    this.Describe();

    // Sized to what it holds. A fixed height is what leaves a box with a band of nothing across the
    // middle of it the moment a row is added, and what clips the last one when two are — which is why
    // the closing note's height is counted off its own lines rather than written out as a number.
    var height = y + noteHeight + 16 + 28 + _Margin + 24;
    this.Bounds = new(0, 0, _Width, height);
    this.MinimumSize = new(_Width, height);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>The bands the window is explaining. Assigning re-words the two sentences.</summary>
  public UsageThresholds Heat {
    get => this._heat;
    set {
      this._heat = value;
      this.Describe();
    }
  }

  /// <summary>Raised when somebody changed the bands from here, with what they changed them to.</summary>
  public event EventHandler<UsageThresholds>? ThresholdsChanged;

  /// <summary>Every line the window shows, for a test with no display to read it off.</summary>
  public string Description => $"{this._warmText.Text}\n{this._hotText.Text}";

  /// <summary>Which row colours this window explains — the list a test holds the palette to.</summary>
  public static IReadOnlyList<ProcessCategory> Categories => _Categories;

  /// <summary>The closing note, for the same reason: a refusal nobody can read is not one.</summary>
  public static string Note => string.Join('\n', _Note);

  private void EditThresholds() {
    var dialog = new HighlightThresholdsDialog(this.Heat);
    dialog.ShowDialog();
    if (!dialog.Accepted)
      return;

    this.Heat = dialog.Thresholds;
    this.ThresholdsChanged?.Invoke(this, dialog.Thresholds);
  }

  /// <summary>
  /// The two bands in words.
  /// </summary>
  /// <remarks>
  /// Every resource on one line each, because the bands are one setting per resource and a reader
  /// comparing "warm" against "hot" wants the pair side by side. A band switched off says so rather
  /// than printing a nought that reads like a threshold of zero — which would mark everything.
  /// </remarks>
  private void Describe() {
    this._warmText.Text = "Warm — " + Band(this._heat.WarmCpuPercent, this._heat.WarmMemoryPercent, this._heat.WarmBytesPerSecond, this._heat.WarmGpuPercent);
    this._hotText.Text = "Hot — " + Band(this._heat.HotCpuPercent, this._heat.HotMemoryPercent, this._heat.HotBytesPerSecond, this._heat.HotGpuPercent);
  }

  private static string Band(double cpu, double memory, double bytesPerSecond, double gpu) {
    var parts = new List<string>(4);
    Add(parts, "CPU", cpu, "% of a core");
    Add(parts, "memory", memory, "% of the machine");
    if (bytesPerSecond > 0)
      parts.Add($"disk {Humanize.Bytes(Counter.Of((ulong)bytesPerSecond))}/s");

    Add(parts, "GPU", gpu, "% of the adapter");
    return parts.Count == 0 ? "every band is switched off" : string.Join(", ", parts);
  }

  private static void Add(List<string> parts, string name, double value, string unit) {
    if (value > 0)
      parts.Add($"{name} {value:0.#} {unit}");
  }

  private int AddHeading(string text, int y) {
    this.Controls.Add(new Label { Text = text, Bounds = new(_Margin, y, _Width - (_Margin * 2), 18) });
    return y + 24;
  }

  /// <remarks>
  /// The wider button is not a preference. At 120 it drew as "Threshold..." — a button whose label is
  /// truncated to a different word than the one it was given, which the photograph caught and no
  /// assertion could.
  /// </remarks>
  public void ApplyLayout() {
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - 28);
    this._close.Bounds = new(this.Width - _Margin - 84, buttons, 84, 28);
    this._thresholds.Bounds = new(this._close.Bounds.X - 150, buttons, 140, 28);
  }

  /// <summary>A colour chip. Its own control because a Label has no background of its own.</summary>
  private sealed class CategorySwatch(ProcessCategory category) : OwnerDrawnControl {

    protected override void OnPaint(PaintEventArgs e) {
      var theme = this.Theme;
      var fill = RowPalette.BackColorOf(category, theme) ?? theme.FieldBackground;
      Chip(e.Graphics, fill, theme, this.Width, this.Height);
    }

  }

  /// <summary>
  /// One of the two cell marks, drawn the way the table draws it.
  /// </summary>
  /// <remarks>
  /// With a digit on it, because that is what the mark always sits under in the table and what makes
  /// it legible: a swatch of the wash on its own would show a colour the reader never actually sees
  /// alone (PRD §23).
  /// </remarks>
  private sealed class HeatSwatch(UsageHeat heat) : OwnerDrawnControl {

    protected override void OnPaint(PaintEventArgs e) {
      var theme = this.Theme;
      var fill = RowPalette.HeatColour(heat, theme) ?? theme.FieldBackground;
      Chip(e.Graphics, fill, theme, this.Width, this.Height);
      e.Graphics.DrawText("42", theme.DefaultFont, theme.ControlText, new(0, 0, this.Width - 2, this.Height), ContentAlignment.MiddleRight);
    }

  }

  private static void Chip(IGraphics g, Color fill, ITheme theme, int width, int height) {
    g.FillRectangle(fill, new(0, 0, width, height));
    g.DrawRectangle(theme.Border, new(0, 0, width - 1, height - 1));
  }

}
