namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>The escape sequences the renderer uses, and nothing else.</summary>
internal static class Ansi {

  public const string Reset = "\u001b[0m";
  public const string ClearScreen = "\u001b[2J\u001b[H";
  public const string EnterAlternateScreen = "\u001b[?1049h";
  public const string LeaveAlternateScreen = "\u001b[?1049l";
  public const string HideCursor = "\u001b[?25l";
  public const string ShowCursor = "\u001b[?25h";

  /// <summary>1-based, the way the terminal counts.</summary>
  public static string MoveTo(int row, int column) => $"\u001b[{row};{column}H";

}

/// <summary>
/// A cell's appearance in one byte: colour index in the low nibble, flags in the high one.
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

  public static string ToAnsi(byte attribute, ColorDepth depth) {
    if (depth == ColorDepth.None)
      return (attribute & 0x0F) switch {
        Selected or Header => "\u001b[7m",                // reverse video is the only emphasis left
        Dim => "\u001b[2m",
        _ => Ansi.Reset,
      };

    var flagged = (attribute & NewProcess) != 0
      ? Good
      : (attribute & ExitedProcess) != 0 ? Bad : attribute & 0x0F;

    return flagged switch {
      Dim => "\u001b[0;90m",
      Accent => "\u001b[0;36m",
      Good => "\u001b[0;32m",
      Warn => "\u001b[0;33m",
      Bad => "\u001b[0;31m",
      Header => "\u001b[0;30;46m",
      Selected => "\u001b[0;7m",
      _ => Ansi.Reset,
    };
  }

}
