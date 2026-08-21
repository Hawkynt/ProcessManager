using System.Globalization;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Which button a mouse report was about. The wheel counts as two of them.</summary>
public enum MouseButton : byte { None, Left, Middle, Right, WheelUp, WheelDown }

/// <summary>One mouse report, in cells counted from the top left of the screen.</summary>
/// <param name="X">Column, zero-based — the wire protocol counts from one and this does not.</param>
/// <param name="Y">Row, zero-based.</param>
/// <param name="Pressed">True for a press, false for a release.</param>
/// <param name="Motion">True when the pointer moved with a button held: a drag.</param>
public readonly record struct MouseEvent(
  MouseButton Button,
  int X,
  int Y,
  bool Pressed,
  bool Motion,
  bool Shift,
  bool Alt,
  bool Control
);

/// <summary>
/// Decodes the mouse reports a terminal sends (PRD §57.5).
/// </summary>
/// <remarks>
/// <para>
/// Two forms, because both are still in the wild. SGR — <c>ESC [ &lt; b ; x ; y M</c> — is the one
/// asked for, and the only one that can name a column past 223; the original X10 form encodes each
/// coordinate as a single byte offset by 32 and is what a terminal that ignored the request keeps
/// sending. Reading both means a mouse works on the terminal somebody actually has.
/// </para>
/// <para>
/// Pure text in, one event out: no terminal, no state beyond the half-read sequence, so the whole
/// protocol is testable without anything to click on.
/// </para>
/// </remarks>
public static class MouseInput {

  /// <summary>Whether a sequence could still become a mouse report if more arrived.</summary>
  public static bool IsPrefix(ReadOnlySpan<char> sequence) {
    if (sequence.IsEmpty)
      return true;
    if (sequence[0] != '\u001b')
      return false;
    if (sequence.Length == 1)
      return true;
    if (sequence[1] != '[')
      return false;
    if (sequence.Length == 2)
      return true;

    return sequence[2] is '<' or 'M';
  }

  /// <summary>Decodes a complete report. Returns false for anything that is not one.</summary>
  public static bool TryDecode(ReadOnlySpan<char> sequence, out MouseEvent result) {
    result = default;
    if (sequence.Length >= 1 && sequence[0] == '\u001b')
      sequence = sequence[1..];
    if (sequence.Length < 3 || sequence[0] != '[')
      return false;

    return sequence[1] switch {
      '<' => TryDecodeSgr(sequence[2..], out result),
      'M' => TryDecodeX10(sequence[2..], out result),
      _ => false,
    };
  }

  private static bool TryDecodeSgr(ReadOnlySpan<char> body, out MouseEvent result) {
    result = default;
    var final = body.Length > 0 ? body[^1] : '\0';
    if (final is not ('M' or 'm'))
      return false;

    body = body[..^1];
    if (!TryTake(ref body, ';', out var code)
        || !TryTake(ref body, ';', out var column)
        || !int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var row))
      return false;

    result = Compose(code, column - 1, row - 1, pressed: final == 'M');
    return true;
  }

  private static bool TryDecodeX10(ReadOnlySpan<char> body, out MouseEvent result) {
    result = default;
    if (body.Length < 3)
      return false;

    // Each byte carries its value offset by 32, which is why this form cannot express a column past
    // 223: the byte would stop being printable.
    var code = body[0] - 32;
    var column = body[1] - 32 - 1;
    var row = body[2] - 32 - 1;
    if (code < 0)
      return false;

    // The old form has no release code per button: 3 means "whichever one it was, it is up now".
    var released = (code & 0x03) == 3 && (code & 0x40) == 0;
    result = Compose(code, column, row, pressed: !released);
    if (released)
      result = result with { Button = MouseButton.None };

    return true;
  }

  private static MouseEvent Compose(int code, int x, int y, bool pressed) {
    var wheel = (code & 0x40) != 0;
    var button = wheel
      ? (code & 0x01) == 0 ? MouseButton.WheelUp : MouseButton.WheelDown
      : (code & 0x03) switch {
        0 => MouseButton.Left,
        1 => MouseButton.Middle,
        2 => MouseButton.Right,
        _ => MouseButton.None,
      };

    return new(
      button,
      Math.Max(0, x),
      Math.Max(0, y),
      // The wheel has no release, so a wheel report is always a press; treating it as one keeps the
      // caller from having to know that.
      pressed || wheel,
      Motion: (code & 0x20) != 0,
      Shift: (code & 0x04) != 0,
      Alt: (code & 0x08) != 0,
      Control: (code & 0x10) != 0
    );
  }

  private static bool TryTake(ref ReadOnlySpan<char> body, char separator, out int value) {
    value = 0;
    var index = body.IndexOf(separator);
    if (index < 0)
      return false;

    if (!int.TryParse(body[..index], NumberStyles.None, CultureInfo.InvariantCulture, out value))
      return false;

    body = body[(index + 1)..];
    return true;
  }

}
