using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>The escape sequences the renderer uses, and nothing else.</summary>
internal static class Ansi {

  public const string Reset = "\u001b[0m";
  public const string ClearScreen = "\u001b[2J\u001b[H";
  public const string EnterAlternateScreen = "\u001b[?1049h";
  public const string LeaveAlternateScreen = "\u001b[?1049l";
  public const string HideCursor = "\u001b[?25l";
  public const string ShowCursor = "\u001b[?25h";

  /// <summary>
  /// Button presses, drag reporting and SGR coordinates, in that order (PRD §57.5).
  /// </summary>
  /// <remarks>
  /// 1006 is the part that matters: the original protocol encodes a coordinate as one byte offset by
  /// 32, so nothing past column 223 can be clicked at all, and a maximised terminal is wider than
  /// that. Terminals that do not know 1006 ignore it and keep sending the old form, which the decoder
  /// also reads, so asking for it costs nothing where it is not understood.
  /// </remarks>
  public const string EnableMouse = "\u001b[?1000h\u001b[?1002h\u001b[?1006h";

  public const string DisableMouse = "\u001b[?1006l\u001b[?1002l\u001b[?1000l";

  /// <summary>1-based, the way the terminal counts.</summary>
  public static string MoveTo(int row, int column) => $"\u001b[{row};{column}H";

}

/// <summary>
/// A cell's appearance in one byte: a colour meaning in the low bits, flags in the high ones.
/// </summary>
/// <remarks>
/// One byte per cell rather than a struct, because the diff in <see cref="TerminalScreen"/> compares
/// it once per cell and an 80×50 frame is four thousand comparisons per redraw. The palette is
/// deliberately small — this has to degrade to a monochrome terminal, so colour may never be the
/// only thing carrying a meaning (PRD §11).
/// </remarks>
public static class Attributes {

  public const byte Normal = 0;
  public const byte Dim = 1;
  public const byte Accent = 2;
  public const byte Good = 3;
  public const byte Warn = 4;
  public const byte Bad = 5;
  public const byte Header = 6;
  public const byte Selected = 7;

  /// <summary>Set on top of a colour to mark a row that appeared since the last sample.</summary>
  public const byte NewProcess = 8;

  /// <summary>Set on a row whose process has gone.</summary>
  public const byte ExitedProcess = 16;

  /// <summary>Set on a row the user has ticked for a bulk action (PRD §11).</summary>
  /// <remarks>
  /// The colour is never the only sign of it: a ticked row also carries a mark in the gutter, because
  /// a monochrome terminal and a colour-blind reader have to be able to see the same thing (§57.4).
  /// </remarks>
  public const byte Marked = 32;

  /// <summary>Set on the run of characters a search matched (PRD §11).</summary>
  public const byte Match = 64;

  /// <summary>How many distinct appearances the palette has, one per <c>Slot</c> value.</summary>
  private const int _Slots = 10;

  private const int _MarkedSlot = 8;
  private const int _MatchSlot = 9;

  /// <summary>
  /// The one meaning a cell ends up with, once the flags have argued it out.
  /// </summary>
  /// <remarks>
  /// A matched run wins over everything, because the reason to highlight it is that it should be
  /// findable on a selected row too. Then a row that has just started or just ended, then a ticked
  /// one, then whatever colour was asked for.
  /// </remarks>
  private static int Slot(byte attribute) {
    if ((attribute & Match) != 0)
      return _MatchSlot;
    if ((attribute & NewProcess) != 0)
      return Good;
    if ((attribute & ExitedProcess) != 0)
      return Bad;

    var colour = attribute & 0x07;
    return (attribute & Marked) != 0 && colour != Selected ? _MarkedSlot : colour;
  }

  private static readonly string[] _Ansi16 = [
    Ansi.Reset,
    "\u001b[0;90m",
    "\u001b[0;36m",
    "\u001b[0;32m",
    "\u001b[0;33m",
    "\u001b[0;31m",
    "\u001b[0;30;46m",
    "\u001b[0;7m",
    "\u001b[0;1;93m",
    "\u001b[0;30;43m",
  ];

  private static readonly string[] _Ansi256 = [
    Ansi.Reset,
    "\u001b[0;38;5;244m",
    "\u001b[0;38;5;44m",
    "\u001b[0;38;5;77m",
    "\u001b[0;38;5;178m",
    "\u001b[0;38;5;167m",
    "\u001b[0;38;5;236;48;5;44m",
    "\u001b[0;7m",
    "\u001b[0;38;5;214m",
    "\u001b[0;38;5;16;48;5;220m",
  ];

  private static readonly string[] _TrueColor = [
    Ansi.Reset,
    "\u001b[0;38;2;138;138;138m",
    "\u001b[0;38;2;42;161;152m",
    "\u001b[0;38;2;63;191;63m",
    "\u001b[0;38;2;200;160;44m",
    "\u001b[0;38;2;208;64;64m",
    "\u001b[0;38;2;12;43;43;48;2;42;161;152m",
    "\u001b[0;7m",
    "\u001b[0;38;2;235;160;60m",
    "\u001b[0;38;2;12;12;12;48;2;222;196;60m",
  ];

  /// <summary>
  /// The escape sequence for an attribute at the depth the terminal admitted to.
  /// </summary>
  /// <remarks>
  /// Four palettes rather than one that is scaled down: a 256-colour terminal can show a grey that
  /// reads as dim rather than as "bright black", and a 24-bit one can use the same figures the window
  /// paints with — while a monochrome one has only reverse video and dim, so everything that carried
  /// a meaning in colour has to carry it in a glyph as well (PRD §57.4).
  /// </remarks>
  public static string ToAnsi(byte attribute, ColorDepth depth) {
    var slot = Slot(attribute);
    if (depth != ColorDepth.None) {
      var chosen = depth switch {
        ColorDepth.TrueColor => _chosenTrueColor,
        ColorDepth.Ansi256 => _chosenAnsi256,
        _ => _chosenAnsi16,
      };

      return chosen?[slot] ?? (depth switch {
        ColorDepth.TrueColor => _TrueColor,
        ColorDepth.Ansi256 => _Ansi256,
        _ => _Ansi16,
      })[slot];
    }

    return slot switch {
      Selected or Header or _MatchSlot => "\u001b[7m",       // reverse video is the only emphasis left
      _MarkedSlot => "\u001b[1m",
      Dim => "\u001b[2m",
      _ => Ansi.Reset,
    };
  }

  /// <summary>Every distinct appearance, for a test that checks a depth defines all of them.</summary>
  public static int SlotCount => _Slots;

  #region a palette from the settings file (PRD §67)

  /// <summary>
  /// What the file said, already turned into escape sequences — one array per depth, null where the
  /// file named nothing at all.
  /// </summary>
  /// <remarks>
  /// Composed once in <see cref="Apply"/> rather than looked up per cell: the diff writes an
  /// attribute run for every change of appearance on every line of every frame, and a dictionary
  /// probe on a string key there would be paid a few thousand times a second to answer a question
  /// whose answer never changes.
  /// </remarks>
  private static string?[]? _chosenAnsi16;
  private static string?[]? _chosenAnsi256;
  private static string?[]? _chosenTrueColor;

  /// <summary>
  /// Takes the terminal's palette from a settings file (PRD §67).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Keyed the way the file is: <c>good</c> is an ink and <c>good.bg</c> a ground, and naming either
  /// replaces that appearance outright. Tinting the built-in instead would mean a header whose ink
  /// somebody chose keeps a cyan bar they did not, and there would be no way to say "no bar" at all.
  /// </para>
  /// <para>
  /// Every depth that has colour is composed, including the two that cannot show the figure asked
  /// for. Somebody on a sixteen-colour terminal who writes <c>#ff0000</c> means red, and the nearest
  /// red that terminal has is a truer answer than the built-in they were trying to replace — which
  /// is the one outcome that would look like the line had been ignored.
  /// </para>
  /// </remarks>
  public static void Apply(IReadOnlyDictionary<string, uint>? colours) {
    if (colours is not { Count: > 0 }) {
      _chosenAnsi16 = _chosenAnsi256 = _chosenTrueColor = null;
      return;
    }

    string?[]? ansi16 = null, ansi256 = null, trueColor = null;
    for (var slot = 0; slot < _Slots; ++slot) {
      var name = _SlotNames[slot];
      var hasInk = colours.TryGetValue(name, out var ink);
      var hasGround = colours.TryGetValue(name + ".bg", out var ground);
      if (!hasInk && !hasGround)
        continue;

      (ansi16 ??= new string?[_Slots])[slot] = Compose(hasInk, ink, hasGround, ground, Ansi16Code);
      (ansi256 ??= new string?[_Slots])[slot] = Compose(hasInk, ink, hasGround, ground, Ansi256Code);
      (trueColor ??= new string?[_Slots])[slot] = Compose(hasInk, ink, hasGround, ground, TrueColorCode);
    }

    _chosenAnsi16 = ansi16;
    _chosenAnsi256 = ansi256;
    _chosenTrueColor = trueColor;
  }

  /// <summary>The name each slot answers to in the settings file, in slot order.</summary>
  private static readonly string[] _SlotNames = [
    "normal", "dim", "accent", "good", "warn", "bad", "header", "selected", "marked", "match",
  ];

  /// <summary>Every appearance the file may name, so a test can hold the two lists together.</summary>
  public static IReadOnlyList<string> SlotNames => _SlotNames;

  private static string Compose(bool hasInk, uint ink, bool hasGround, uint ground, Func<uint, bool, string> code) {
    // The prefix Ansi.Reset carries, without its terminating 'm': every appearance begins by
    // clearing the last one, exactly as the built-in palettes above do.
    var text = new StringBuilder(Ansi.Reset[..^1]);
    if (hasInk)
      text.Append(';').Append(code(ink, false));

    if (hasGround)
      text.Append(';').Append(code(ground, true));

    return text.Append('m').ToString();
  }

  private static string TrueColorCode(uint argb, bool ground) {
    var (r, g, b) = Channels(argb);
    return $"{(ground ? 48 : 38)};2;{r};{g};{b}";
  }

  private static string Ansi256Code(uint argb, bool ground) => $"{(ground ? 48 : 38)};5;{Nearest256(argb)}";

  /// <summary>
  /// The three- and four-bit codes, which are all a sixteen-colour terminal understands — 30–37 and
  /// 40–47 for the first eight, 90–97 and 100–107 for the bright ones.
  /// </summary>
  private static string Ansi16Code(uint argb, bool ground) {
    var index = Nearest16(argb);
    var code = index < 8
      ? (ground ? 40 : 30) + index
      : (ground ? 100 : 90) + (index - 8);

    return code.ToString(CultureInfo.InvariantCulture);
  }

  private static (int R, int G, int B) Channels(uint argb)
    => ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));

  /// <summary>
  /// The nearest of xterm's 256 — the 6×6×6 cube or the 24-step grey ramp, whichever lands closer.
  /// </summary>
  /// <remarks>
  /// The greys are weighed separately rather than left to the cube, because the cube's own grey
  /// diagonal has six steps and the ramp has twenty-four: a light grey quantised into the cube lands
  /// on a visibly different colour while a ramp entry sits two units away.
  /// </remarks>
  private static int Nearest256(uint argb) {
    var (r, g, b) = Channels(argb);

    var cube = (Level(r) * 36) + (Level(g) * 6) + Level(b) + 16;
    var cubeDistance = Distance(r, g, b, _CubeSteps[Level(r)], _CubeSteps[Level(g)], _CubeSteps[Level(b)]);

    // The ramp runs 8, 18, 28 … 238 at indices 232–255, so the nearest step is the rounded average.
    var average = (r + g + b) / 3;
    var step = Math.Clamp((average - 8 + 5) / 10, 0, 23);
    var grey = 8 + (step * 10);
    return Distance(r, g, b, grey, grey, grey) < cubeDistance ? 232 + step : cube;

    static int Level(int channel) {
      var best = 0;
      var bestDistance = int.MaxValue;
      for (var i = 0; i < _CubeSteps.Length; ++i) {
        var distance = Math.Abs(_CubeSteps[i] - channel);
        if (distance >= bestDistance)
          continue;

        best = i;
        bestDistance = distance;
      }

      return best;
    }
  }

  private static readonly int[] _CubeSteps = [0, 95, 135, 175, 215, 255];

  /// <summary>
  /// The nearest of the sixteen, by the figures xterm gives them.
  /// </summary>
  /// <remarks>
  /// An approximation twice over: these are xterm's numbers, and a terminal's sixteen are the user's
  /// to reconfigure — so what arrives on screen is the colour they set for that slot rather than the
  /// one they wrote here. Which is the whole of what sixteen colours can promise.
  /// </remarks>
  private static int Nearest16(uint argb) {
    var (r, g, b) = Channels(argb);
    var best = 0;
    var bestDistance = int.MaxValue;
    for (var i = 0; i < _Basic16.Length; ++i) {
      var (cr, cg, cb) = Channels(_Basic16[i]);
      var distance = Distance(r, g, b, cr, cg, cb);
      if (distance >= bestDistance)
        continue;

      best = i;
      bestDistance = distance;
    }

    return best;
  }

  private static readonly uint[] _Basic16 = [
    0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xC0C0C0,
    0x808080, 0xFF0000, 0x00FF00, 0xFFFF00, 0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
  ];

  private static int Distance(int r, int g, int b, int or, int og, int ob) {
    var dr = r - or;
    var dg = g - og;
    var db = b - ob;
    return (dr * dr) + (dg * dg) + (db * db);
  }

  #endregion

}
