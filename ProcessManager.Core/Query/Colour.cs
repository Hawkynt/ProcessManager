using System.Globalization;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// A colour somebody wrote down, as text (PRD §66, §67).
/// </summary>
/// <remarks>
/// In Core and not in a front-end because two things read colours out of files now — the palette
/// overrides in the settings and the colour on a rule — and a hash that means one thing in the
/// settings file and another in the rules file is exactly the drift §5.1 put the field catalogue in
/// one place to prevent.
/// </remarks>
public static class Colour {

  /// <summary>
  /// <c>#rrggbb</c>, with or without the hash, and <c>#rgb</c> for the people who write CSS.
  /// </summary>
  /// <remarks>
  /// The alpha is never taken from the text. A half-transparent row colour is a bug report rather
  /// than a preference, and a file that could specify one would make an unreadable table something
  /// somebody could ask for by accident.
  /// </remarks>
  public static bool TryParse(string? text, out uint argb) {
    argb = 0;
    if (text is not { Length: > 0 })
      return false;

    var digits = text.StartsWith('#') ? text[1..] : text;
    if (digits.Length == 3) {
      Span<char> expanded = stackalloc char[6];
      for (var i = 0; i < 3; ++i) {
        expanded[i * 2] = digits[i];
        expanded[(i * 2) + 1] = digits[i];
      }

      digits = new(expanded);
    }

    if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
      return false;

    argb = 0xFF000000u | rgb;
    return true;
  }

}
