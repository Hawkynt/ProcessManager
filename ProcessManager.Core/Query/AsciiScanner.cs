namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Field-at-a-time parsing over a <c>/proc</c> file's bytes.
/// </summary>
/// <remarks>
/// <para>
/// Everything under <c>/proc</c> is ASCII whitespace-separated text. Splitting it into strings first
/// would allocate roughly forty strings per process per sample — the single easiest way to miss the
/// allocation budget in §4 — so nothing here produces a string that is not going to be kept.
/// </para>
/// <para>
/// In Core rather than in the Linux probe, and public rather than internal, because the parsers that
/// use it are in Core: §9.2 wants every parser exercised on every CI leg, and a scanner that only
/// exists inside the platform assembly forces its callers to live there too.
/// </para>
/// </remarks>
public ref struct AsciiScanner(ReadOnlySpan<byte> content) {

  private ReadOnlySpan<byte> _rest = content;

  public readonly bool IsEmpty => this._rest.IsEmpty;

  /// <summary>Skips spaces and tabs, but not newlines: a field is missing, a line is not.</summary>
  private void SkipBlanks() {
    var i = 0;
    while (i < this._rest.Length && (this._rest[i] == (byte)' ' || this._rest[i] == (byte)'\t'))
      ++i;

    this._rest = this._rest[i..];
  }

  /// <summary>The next whitespace-delimited field, or empty at end of line/content.</summary>
  public ReadOnlySpan<byte> NextField() {
    this.SkipBlanks();
    var i = 0;
    while (i < this._rest.Length && !IsSpace(this._rest[i]))
      ++i;

    var field = this._rest[..i];
    this._rest = this._rest[i..];
    return field;
  }

  /// <summary>The next field as an unsigned integer; 0 when it is missing or not a number.</summary>
  public ulong NextUInt64() => ParseUInt64(this.NextField());

  public long NextInt64() {
    var field = this.NextField();
    if (field.IsEmpty)
      return 0;

    return field[0] == (byte)'-' ? -(long)ParseUInt64(field[1..]) : (long)ParseUInt64(field);
  }

  public int NextInt32() => (int)this.NextInt64();

  /// <summary>Skips <paramref name="count"/> fields without parsing them.</summary>
  public void Skip(int count) {
    for (var i = 0; i < count; ++i)
      this.NextField();
  }

  /// <summary>
  /// Everything left on the current line with the leading blanks removed, then positions after it.
  /// </summary>
  /// <remarks>
  /// For the last field of a <c>maps</c> line, which is a path and may contain spaces. Taking it with
  /// <see cref="NextField"/> truncated <c>/opt/My App/libfoo.so</c> at the space and reported a module
  /// nobody has.
  /// </remarks>
  public ReadOnlySpan<byte> RestOfLine() {
    this.SkipBlanks();
    return this.NextLine();
  }

  /// <summary>The rest of the current line, then positions after it.</summary>
  public ReadOnlySpan<byte> NextLine() {
    var newline = this._rest.IndexOf((byte)'\n');
    if (newline < 0) {
      var all = this._rest;
      this._rest = default;
      return all;
    }

    var line = this._rest[..newline];
    this._rest = this._rest[(newline + 1)..];
    return line;
  }

  public static ulong ParseUInt64(ReadOnlySpan<byte> field) {
    ulong value = 0;
    for (var i = 0; i < field.Length; ++i) {
      var digit = (uint)(field[i] - (byte)'0');
      if (digit > 9)
        break;

      value = value * 10 + digit;
    }

    return value;
  }

  /// <summary>
  /// A hexadecimal field, stopping at the first byte that is not a hex digit.
  /// </summary>
  /// <remarks>
  /// Addresses in <c>maps</c>, device numbers, and the capability masks in <c>status</c> are all
  /// written in hex without an <c>0x</c>, so there is no prefix to skip.
  /// </remarks>
  public static ulong ParseHex(ReadOnlySpan<byte> field) {
    ulong value = 0;
    for (var i = 0; i < field.Length; ++i) {
      var c = field[i];
      var digit = c switch {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => -1,
      };

      if (digit < 0)
        break;

      value = value * 16 + (ulong)digit;
    }

    return value;
  }

  /// <summary>
  /// An octal field, stopping at the first byte that is not an octal digit.
  /// </summary>
  /// <remarks>
  /// <c>/proc/[pid]/fdinfo/[fd]</c> writes the open flags in octal with a leading zero, which is the
  /// notation the <c>O_*</c> constants themselves are defined in. Reading <c>0100002</c> as decimal
  /// produces a number that is not wrong so much as meaningless.
  /// </remarks>
  public static ulong ParseOctal(ReadOnlySpan<byte> field) {
    ulong value = 0;
    for (var i = 0; i < field.Length; ++i) {
      var digit = (uint)(field[i] - (byte)'0');
      if (digit > 7)
        break;

      value = value * 8 + digit;
    }

    return value;
  }

  public static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

  /// <summary>
  /// Whether <paramref name="line"/> starts with <paramref name="prefix"/>. Used for the
  /// <c>Key:</c> lines of <c>status</c>, <c>meminfo</c> and <c>io</c>.
  /// </summary>
  public static bool StartsWith(ReadOnlySpan<byte> line, ReadOnlySpan<byte> prefix)
    => line.Length >= prefix.Length && line[..prefix.Length].SequenceEqual(prefix);

}
