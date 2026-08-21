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
    if (depth != ColorDepth.None)
      return (depth switch {
        ColorDepth.TrueColor => _TrueColor,
        ColorDepth.Ansi256 => _Ansi256,
        _ => _Ansi16,
      })[slot];

    return slot switch {
      Selected or Header or _MatchSlot => "\u001b[7m",       // reverse video is the only emphasis left
      _MarkedSlot => "\u001b[1m",
      Dim => "\u001b[2m",
      _ => Ansi.Reset,
    };
  }

  /// <summary>Every distinct appearance, for a test that checks a depth defines all of them.</summary>
  public static int SlotCount => _Slots;

}
