namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Line-at-a-time and field-at-a-time scanning over text that came out of <c>/proc</c>.
/// </summary>
/// <remarks>
/// The same shape as the probe's byte scanner, over characters, because a parser in this assembly
/// carries no platform attribute and so may not reach into a platform project for it (PRD §9.2).
/// Nothing here allocates: a field is a slice of the caller's span, and only the values that are
/// kept ever become strings.
/// </remarks>
internal ref struct TextScanner(ReadOnlySpan<char> content) {

  private ReadOnlySpan<char> _rest = content;

  public readonly bool IsEmpty => this._rest.IsEmpty;

  /// <summary>The rest of the current line, then positions after it.</summary>
  public ReadOnlySpan<char> NextLine() {
    var newline = this._rest.IndexOf('\n');
    var line = newline < 0 ? this._rest : this._rest[..newline];
    this._rest = newline < 0 ? default : this._rest[(newline + 1)..];

    // The kernel writes no \r, but a fixture edited on Windows might — the same trap UeventParser
    // names, and one that would otherwise put a stray character on the end of every last field.
    return line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line;
  }

  /// <summary>The next whitespace-delimited field, or empty at the end.</summary>
  /// <remarks>
  /// The network tables are column-aligned with runs of spaces, so a delimiter-per-field split would
  /// hand back empty fields between them and put every value in the wrong column.
  /// </remarks>
  public ReadOnlySpan<char> NextField() {
    var i = 0;
    while (i < this._rest.Length && IsSpace(this._rest[i]))
      ++i;

    this._rest = this._rest[i..];
    i = 0;
    while (i < this._rest.Length && !IsSpace(this._rest[i]))
      ++i;

    var found = this._rest[..i];
    this._rest = this._rest[i..];
    return found;
  }

  /// <summary>Everything left, with leading blanks removed and nothing split.</summary>
  /// <remarks>
  /// For the one field that may contain spaces: a Unix socket's path. Splitting that on whitespace
  /// would report <c>/tmp/my socket</c> as a socket bound to <c>/tmp/my</c>.
  /// </remarks>
  public ReadOnlySpan<char> Rest() {
    var i = 0;
    while (i < this._rest.Length && IsSpace(this._rest[i]))
      ++i;

    var found = this._rest[i..];
    this._rest = default;
    return found;
  }

  public void Skip(int count) {
    for (var i = 0; i < count; ++i)
      this.NextField();
  }

  private static bool IsSpace(char c) => c is ' ' or '\t';

  /// <summary>
  /// A hexadecimal field as a number. Stops at the first character that is not a digit, which is
  /// what makes <c>0100007F:0277</c> readable one half at a time.
  /// </summary>
  public static uint ParseHex32(ReadOnlySpan<char> field) => (uint)ParseHex64(field);

  public static ulong ParseHex64(ReadOnlySpan<char> field) {
    ulong value = 0;
    foreach (var c in field) {
      var digit = c switch {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
      };

      if (digit < 0)
        break;

      value = value * 16 + (ulong)digit;
    }

    return value;
  }

  public static ulong ParseUInt64(ReadOnlySpan<char> field) {
    ulong value = 0;
    foreach (var c in field) {
      var digit = (uint)(c - '0');
      if (digit > 9)
        break;

      value = value * 10 + digit;
    }

    return value;
  }

}
