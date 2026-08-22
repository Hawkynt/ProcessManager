using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>Which of the three encodings a run of text was found in (PRD §35).</summary>
/// <remarks>
/// Reported rather than merged. A name that appears once as ASCII and once as UTF-16 is two facts
/// about the file — a Windows binary keeps its wide strings in the second and its format strings in
/// the first — and a list that had folded them together would have thrown away which is which.
/// </remarks>
public enum TextEncodingKind : byte {

  /// <summary>Printable seven-bit characters and tab: what <c>strings</c> prints by default.</summary>
  Ascii,

  /// <summary>The same, plus at least one valid multi-byte sequence. A run with none is ASCII.</summary>
  Utf8,

  /// <summary>Sixteen-bit units, low byte first.</summary>
  Utf16LittleEndian,

  /// <summary>Sixteen-bit units, high byte first.</summary>
  Utf16BigEndian,

}

/// <summary>One run of text, and where in the file it starts.</summary>
/// <param name="Offset">The offset of its first byte, which is what makes a hit checkable.</param>
public readonly record struct TextRun(long Offset, TextEncodingKind Encoding, string Text);

/// <summary>
/// What to look for, and how much of the file to look at (PRD §35).
/// </summary>
/// <param name="MinimumLength">
/// How many characters a run needs before it counts. Four is what <c>strings</c> uses and what makes
/// the output of either readable: at two, every table of pointers in the file is a hit.
/// </param>
/// <param name="From">Where to start. Non-zero for a scan restricted to one section or segment.</param>
/// <param name="Length">
/// How many bytes to scan from <paramref name="From"/>. Negative means "to the end of the file".
/// </param>
/// <param name="Pattern">
/// A filter over the text, in §33's grammar: a substring, <c>*</c> or <c>?</c> for a wildcard,
/// <c>"quoted"</c> for the whole value, <c>/slashed/</c> for a regular expression. Null keeps
/// everything. The same grammar as the resource search on purpose — a program with two spellings of
/// "find" is a program in which neither is learnt (PRD §33, §58).
/// </param>
/// <param name="MatchCase">Whether <paramref name="Pattern"/> distinguishes upper from lower case.</param>
/// <param name="MaximumRuns">
/// How many hits to keep. A scan that found more says so rather than silently stopping, because a
/// truncated list that does not admit it is a list somebody will read as complete.
/// </param>
public readonly record struct TextScanOptions(
  int MinimumLength = 4,
  bool Ascii = true,
  bool Utf8 = true,
  bool Utf16 = true,
  long From = 0,
  long Length = -1,
  string? Pattern = null,
  bool MatchCase = false,
  int MaximumRuns = 200_000
) {

  /// <summary>
  /// The defaults, spelled out.
  /// </summary>
  /// <remarks>
  /// <b><c>default(TextScanOptions)</c> is a scan that finds nothing</b>, and silently: a struct's
  /// zero has no encodings selected, a minimum length of nought and a cap of nought runs. The
  /// defaults on the parameters above apply only when the constructor is actually called, so
  /// anything that wants "the ordinary scan" asks for it by name rather than by writing
  /// <c>new()</c> and trusting the shape of the type (PRD §72.3).
  /// </remarks>
  public static TextScanOptions Default => new(MinimumLength: 4);

}

/// <summary>What a scan found, and what it had to leave out.</summary>
/// <param name="BytesScanned">How much of the file was actually read.</param>
/// <param name="Truncated">
/// Set when <see cref="TextScanOptions.MaximumRuns"/> stopped the scan. The runs that are here are
/// still true; there are simply more of them.
/// </param>
public readonly record struct TextScanResult(IReadOnlyList<TextRun> Runs, long BytesScanned, bool Truncated);

/// <summary>
/// The runs of readable text inside a file (PRD §35).
/// </summary>
/// <remarks>
/// <para>
/// <c>strings</c>, over the reader every other parser in this folder takes, so it runs against a
/// byte array in a test and against a three-hundred-megabyte runtime image on a machine, and neither
/// needs the other (PRD §9.2).
/// </para>
/// <para>
/// The ASCII rule is deliberately the one <c>strings(1)</c> uses — a printable seven-bit character
/// or a tab — so that an answer here can be held against the tool rather than only against itself.
/// The other two are additions: UTF-8 accepts a validated multi-byte sequence whose character is not
/// a control, and UTF-16 the same test over sixteen-bit units in either byte order, which is where a
/// Windows binary keeps most of its text and where <c>strings</c> without <c>-el</c> finds nothing.
/// </para>
/// <para>
/// <b>A run is classified by what it contains, never counted twice.</b> One pass finds the byte-wise
/// text and calls it UTF-8 only when a multi-byte sequence was actually in it; the same run does not
/// also appear as ASCII. Without that rule every string in the file would be reported two or three
/// times and the count at the top of the page would be meaningless.
/// </para>
/// <para>
/// Nothing here reads another process's memory. §35's other half wanted exactly that, and
/// §25.5 records why it is not offered: <c>process_vm_readv</c> and <c>/proc/[pid]/mem</c> are both
/// governed by <c>PTRACE_MODE_ATTACH</c>, which Yama refuses by default for anything this program
/// did not start — and a memory reverse-engineering suite is §4's first non-goal.
/// </para>
/// </remarks>
public static class BinaryStrings {

  /// <summary>
  /// How much of one run is kept.
  /// </summary>
  /// <remarks>
  /// A file with a megabyte of contiguous printable bytes in it — an embedded certificate bundle, a
  /// linked-in JSON schema — is one run, and putting a megabyte in one cell of a table is how a view
  /// stops rendering. The rest of the run is still counted in the offsets of what follows it.
  /// </remarks>
  public const int MaximumRunLength = 4096;

  /// <summary>Read in blocks rather than a byte at a time; the scanners themselves are byte-wise.</summary>
  private const int _ChunkBytes = 256 * 1024;

  /// <summary>
  /// Finds every run of text in a range of a file.
  /// </summary>
  /// <param name="read">Reads the file at an absolute offset.</param>
  /// <param name="fileLength">
  /// How long the file is, so that a range asking for more than there is stops at the end rather
  /// than looping on a reader that keeps returning nothing.
  /// </param>
  public static TextScanResult Scan(ElfImage.ElfRead read, long fileLength, in TextScanOptions options) {
    ArgumentNullException.ThrowIfNull(read);

    var from = Math.Max(0, options.From);
    var available = Math.Max(0, fileLength - from);
    var wanted = options.Length < 0 ? available : Math.Min(options.Length, available);
    var minimum = Math.Max(1, options.MinimumLength);
    var limit = Math.Max(1, options.MaximumRuns);
    var filter = options.Pattern is { Length: > 0 } pattern
      ? ResourceSearch.Compile(pattern, options.MatchCase)
      : null;

    var runs = new List<TextRun>();
    var truncated = false;
    // The byte-wise pass answers for ASCII and UTF-8 together, because they are the same pass: a run
    // is UTF-8 exactly when a multi-byte sequence turned up inside it, and running the two
    // separately would report every plain string twice.
    var text = options.Ascii || options.Utf8
      ? new ByteScanner(options.Utf8, options.Ascii, minimum, Keep)
      : null;
    var little = options.Utf16 ? new WideScanner(littleEndian: true, minimum, Keep) : null;
    var big = options.Utf16 ? new WideScanner(littleEndian: false, minimum, Keep) : null;

    var buffer = new byte[(int)Math.Min(_ChunkBytes, Math.Max(1, wanted))];
    long scanned = 0;
    while (scanned < wanted && !truncated) {
      var ask = (int)Math.Min(buffer.Length, wanted - scanned);
      var got = read(from + scanned, buffer.AsSpan(0, ask));
      if (got <= 0)
        break;

      var block = buffer.AsSpan(0, got);
      var at = from + scanned;
      for (var i = 0; i < block.Length; ++i) {
        var b = block[i];
        text?.Feed(b, at + i);
        little?.Feed(b, at + i);
        big?.Feed(b, at + i);
      }

      scanned += got;
    }

    // The tail: a run that reaches the end of the range is still a run, and dropping it would lose
    // the last string of every file whose text runs up to its final byte.
    text?.Flush();
    little?.Flush();
    big?.Flush();

    // By offset, because the three scanners each walk the whole range and their hits would otherwise
    // arrive in three separate ascending sequences. Ordering by where a thing is in the file is what
    // lets somebody hold the list against a hex dump.
    runs.Sort(static (a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Encoding.CompareTo(b.Encoding));
    return new(runs, scanned, truncated);

    void Keep(long offset, TextEncodingKind encoding, string value) {
      if (truncated)
        return;

      if (filter is not null && !filter.Matches(value))
        return;

      if (runs.Count >= limit) {
        truncated = true;
        return;
      }

      runs.Add(new(offset, encoding, value));
    }
  }

  /// <summary>Whether a character is one <c>strings(1)</c> would print: printable ASCII, or a tab.</summary>
  private static bool IsGraphicAscii(byte b) => b is (>= 0x20 and <= 0x7E) or 0x09;

  /// <summary>
  /// Whether a decoded character counts as text.
  /// </summary>
  /// <remarks>
  /// The C0 and C1 control blocks are out, the surrogate range is not a character at all, and the
  /// two non-characters at the end of the plane are what a decoder writes when it gave up. Everything
  /// else is somebody's alphabet.
  /// </remarks>
  private static bool IsGraphicRune(uint rune)
    => rune is (>= 0x20 and <= 0x7E) or 0x09
      || (rune >= 0xA0 && rune is not (>= 0xD800 and <= 0xDFFF) and not (0xFFFE or 0xFFFF) && rune <= 0x10FFFF);

  /// <summary>
  /// The same test for a sixteen-bit unit, and deliberately a narrower one.
  /// </summary>
  /// <remarks>
  /// <b>Latin-1 and no further, which is what <c>strings -el</c> accepts.</b> The wide pass has no
  /// validation to lean on — every pair of bytes is a code unit — so the acceptance rule is the only
  /// thing separating text from machine code, and roughly nine byte pairs in ten land somewhere in
  /// the CJK block. Accepting the whole plane was tried against <c>/usr/bin/ls</c> and produced
  /// thirty runs of ideographs, every one of them a stretch of the ELF header or of compiled code.
  /// The cost is that a wide string in a script outside Latin-1 is not found here, and the UTF-8
  /// pass — which does have validation — is where such text is found instead.
  /// </remarks>
  private static bool IsGraphicUnit(uint unit) => unit is (>= 0x20 and <= 0x7E) or 0x09 or (>= 0xA0 and <= 0xFF);

  /// <summary>
  /// The byte-wise pass: ASCII, and UTF-8 where the caller asked for it.
  /// </summary>
  /// <remarks>
  /// A state machine over single bytes rather than a decoder over a block, because a multi-byte
  /// sequence straddles a read boundary about once per megabyte and a scanner that lost those would
  /// cut a string in half at an offset nobody could predict.
  /// </remarks>
  private sealed class ByteScanner(bool utf8, bool keepAscii, int minimum, Action<long, TextEncodingKind, string> found) {

    private readonly StringBuilder _run = new();
    private long _start = -1;
    private bool _wide;

    /// <summary>The bytes of a multi-byte sequence seen so far, and how many are still owed.</summary>
    private uint _pending;

    private int _owed;
    private int _sequence;
    private long _sequenceStart;

    public void Feed(byte b, long offset) {
      if (this._owed > 0) {
        // A continuation byte, or the sequence was a lie. UTF-8 is self-synchronising precisely so
        // that the second case can be recognised rather than guessed at.
        if ((b & 0xC0) == 0x80) {
          this._pending = (this._pending << 6) | (uint)(b & 0x3F);
          --this._owed;
          if (this._owed > 0)
            return;

          if (IsGraphicRune(this._pending) && !IsOverlong(this._pending, this._sequence)) {
            this.Start(this._sequenceStart);
            this._run.Append(char.ConvertFromUtf32((int)this._pending));
            this._wide = true;
            return;
          }

          this.Emit();
          return;
        }

        // Not a continuation. Whatever the lead byte promised, it was not this — so the run ends and
        // this byte is reconsidered from scratch, which is what keeps a truncated sequence from
        // swallowing the character after it.
        this._owed = 0;
        this.Emit();
      }

      if (IsGraphicAscii(b)) {
        this.Start(offset);
        this._run.Append((char)b);
        return;
      }

      if (utf8 && b >= 0xC2) {
        var length = b switch {
          >= 0xC2 and <= 0xDF => 2,
          >= 0xE0 and <= 0xEF => 3,
          >= 0xF0 and <= 0xF4 => 4,
          _ => 0,
        };

        if (length > 0) {
          this._pending = b & (uint)(0xFF >> (length + 1));
          this._owed = length - 1;
          this._sequence = length;
          this._sequenceStart = offset;
          return;
        }
      }

      this.Emit();
    }

    public void Flush() {
      this._owed = 0;
      this.Emit();
    }

    private void Start(long offset) {
      if (this._start < 0)
        this._start = offset;
    }

    private void Emit() {
      var start = this._start;
      var wide = this._wide;
      // Taken before the builder is cleared, and not as a reference to it: holding the builder and
      // then emptying it hands every caller an empty string, which is how this reported that a
      // hundred and sixty kilobytes of ELF contained no text at all.
      var run = this._run.Length >= minimum ? Trim(this._run) : null;
      this._run.Clear();
      this._start = -1;
      this._wide = false;
      if (start < 0 || run is null)
        return;

      // A run with no multi-byte sequence in it is ASCII whatever the caller asked about UTF-8, and a
      // caller that wanted only UTF-8 does not want it. Classifying by content is what keeps one run
      // out of two lists.
      if (wide) {
        if (utf8)
          found(start, TextEncodingKind.Utf8, run);
      } else if (keepAscii) {
        found(start, TextEncodingKind.Ascii, run);
      }
    }

    /// <summary>
    /// Whether a sequence encoded a character in more bytes than it needed.
    /// </summary>
    /// <remarks>
    /// The lead-byte range above already rejects the two-byte overlongs, which is why it starts at
    /// <c>0xC2</c> rather than <c>0xC0</c>. The three- and four-byte ones still have to be checked:
    /// an overlong is the classic way to smuggle a <c>/</c> past a comparison, and a strings view
    /// that decoded them would print text no decoder on the machine agrees exists.
    /// </remarks>
    private static bool IsOverlong(uint rune, int length) => length switch {
      3 => rune < 0x800,
      4 => rune < 0x10000,
      _ => false,
    };

  }

  /// <summary>
  /// The sixteen-bit pass, one byte order at a time.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>The pairing is fixed to the parity of the scan's first byte</b>, which is what <c>strings
  /// -el</c> does and is not an oversight. Re-pairing from the byte after a failed unit finds a
  /// wide string at an odd offset, and it also turns every ordinary Latin run into a second run of
  /// ideographs — <c>"AB"</c> read one byte late is <c>U+4300</c>, which is a perfectly graphic
  /// character. Wide data inside a binary is two-byte aligned within an aligned section, so the
  /// parity is right in practice and the alternative is noise nobody can filter.
  /// </para>
  /// <para>
  /// Surrogate pairs are decoded, so an emoji or a rarely-used ideograph is one character rather than
  /// two halves neither of which is one.
  /// </para>
  /// </remarks>
  private sealed class WideScanner(bool littleEndian, int minimum, Action<long, TextEncodingKind, string> found) {

    private readonly StringBuilder _run = new();
    private long _start = -1;
    private bool _havePartial;
    private byte _partial;
    private long _partialAt;
    private int _highSurrogate = -1;

    public void Feed(byte b, long offset) {
      if (!this._havePartial) {
        this._havePartial = true;
        this._partial = b;
        this._partialAt = offset;
        return;
      }

      this._havePartial = false;
      var unit = littleEndian
        ? (uint)this._partial | ((uint)b << 8)
        : ((uint)this._partial << 8) | b;

      if (this._highSurrogate >= 0) {
        if (unit is >= 0xDC00 and <= 0xDFFF) {
          var rune = 0x10000u + (((uint)this._highSurrogate - 0xD800) << 10) + (unit - 0xDC00);
          this._highSurrogate = -1;
          // A surrogate pair is four bytes that had to survive two acceptance tests to get here, so
          // it is not the false-positive risk a lone unit is and the whole plane is allowed for it.
          if (IsGraphicRune(rune)) {
            this._run.Append(char.ConvertFromUtf32((int)rune));
            return;
          }

          this.Emit();
          return;
        }

        // A high surrogate followed by anything else is not a character. The run ends there, and the
        // unit that ended it is judged on its own below.
        this._highSurrogate = -1;
        this.Emit();
      }

      if (unit is >= 0xD800 and <= 0xDBFF) {
        this.StartAt(this._partialAt);
        this._highSurrogate = (int)unit;
        return;
      }

      if (IsGraphicUnit(unit)) {
        this.StartAt(this._partialAt);
        this._run.Append((char)unit);
        return;
      }

      this.Emit();
    }

    public void Flush() {
      this._highSurrogate = -1;
      this._havePartial = false;
      this.Emit();
    }

    private void StartAt(long offset) {
      if (this._start < 0)
        this._start = offset;
    }

    private void Emit() {
      var start = this._start;
      var run = this._run.Length >= minimum ? Trim(this._run) : null;
      this._run.Clear();
      this._start = -1;
      if (start < 0 || run is null)
        return;

      found(start, littleEndian ? TextEncodingKind.Utf16LittleEndian : TextEncodingKind.Utf16BigEndian, run);
    }

  }

  private static string Trim(StringBuilder run)
    => run.Length <= MaximumRunLength ? run.ToString() : run.ToString(0, MaximumRunLength);

  /// <summary>How an encoding is named in a column.</summary>
  public static string Name(TextEncodingKind encoding) => encoding switch {
    TextEncodingKind.Utf8 => "utf-8",
    TextEncodingKind.Utf16LittleEndian => "utf-16le",
    TextEncodingKind.Utf16BigEndian => "utf-16be",
    _ => "ascii",
  };

}
