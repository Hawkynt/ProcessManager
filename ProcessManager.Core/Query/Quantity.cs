using System.Globalization;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Parses the numbers a human types into a filter: <c>1GiB</c>, <c>500MB</c>, <c>50%</c>,
/// <c>1.5s</c>, <c>10k</c> (PRD §56, §76).
/// </summary>
/// <remarks>
/// Unit-aware, and the unit it is aware of is the field's. <c>1G</c> against a byte field is
/// 1073741824 and against a count field is 1000000000, because a gigabyte and a billion context
/// switches are different things and a filter that got that wrong by 7% would be quietly useless.
/// Spelling it <c>GiB</c> or <c>GB</c> overrides the guess in the usual way.
/// </remarks>
public static class Quantity {

  /// <summary>
  /// Reads a quantity in the units of <paramref name="unit"/>, returning it in the same units the
  /// engine stores that field in — bytes, nanoseconds, percent or a plain count.
  /// </summary>
  public static bool TryParse(ReadOnlySpan<char> text, FieldUnit unit, out double value) {
    value = 0;
    text = text.Trim();
    if (text.IsEmpty)
      return false;

    // Split the digits from the suffix. Everything up to the last digit (or '.') is the number.
    var end = 0;
    while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+'))
      ++end;

    if (end == 0)
      return false;

    if (!double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
      return false;

    var suffix = text[end..].Trim();
    if (suffix.IsEmpty) {
      // A bare number is already in the field's own units, except for time: nobody types 5000000000
      // when they mean five seconds.
      value = unit == FieldUnit.Nanoseconds ? number * 1_000_000_000d : number;
      return true;
    }

    if (suffix is "%" && unit is FieldUnit.Percent) {
      value = number;
      return true;
    }

    if (unit is FieldUnit.Nanoseconds && TryTimeScale(suffix, out var timeScale)) {
      value = number * timeScale;
      return true;
    }

    if (!TryMagnitude(suffix, unit, out var scale))
      return false;

    value = number * scale;
    return true;
  }

  private static bool TryTimeScale(ReadOnlySpan<char> suffix, out double scale) {
    scale = suffix switch {
      "ns" => 1d,
      "us" or "µs" => 1_000d,
      "ms" => 1_000_000d,
      "s" or "sec" => 1_000_000_000d,
      "m" or "min" => 60d * 1_000_000_000d,
      "h" or "hr" => 3600d * 1_000_000_000d,
      "d" => 86400d * 1_000_000_000d,
      _ => 0d,
    };

    return scale > 0;
  }

  private static bool TryMagnitude(ReadOnlySpan<char> suffix, FieldUnit unit, out double scale) {
    // A trailing "/s" is noise on a rate field — "1MB/s" and "1MB" mean the same thing when the
    // field is already per-second, and refusing the more natural spelling would be pedantry.
    if (suffix.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
      suffix = suffix[..^2];

    if (suffix.IsEmpty) {
      scale = 1;
      return true;
    }

    // Explicit spellings win: "KiB" is always 1024 and "kB" is always 1000, whatever the field is.
    var binary = suffix.EndsWith("iB", StringComparison.OrdinalIgnoreCase);
    var decimalSi = !binary && suffix.EndsWith("B", StringComparison.OrdinalIgnoreCase) && suffix.Length > 1;

    var letter = char.ToUpperInvariant(suffix[0]);
    if (suffix.Length == 1 && letter == 'B') {
      scale = 1;
      return true;
    }

    var power = letter switch {
      'K' => 1,
      'M' => 2,
      'G' => 3,
      'T' => 4,
      'P' => 5,
      _ => 0,
    };

    if (power == 0) {
      scale = 0;
      return false;
    }

    // No explicit spelling: a byte field is binary and a count is decimal, because that is what each
    // of them means everywhere else in the program (PRD §76).
    var basis = binary ? 1024d
      : decimalSi ? 1000d
      : unit is FieldUnit.Bytes or FieldUnit.BytesPerSecond ? 1024d
      : 1000d;

    scale = Math.Pow(basis, power);
    return true;
  }

}
