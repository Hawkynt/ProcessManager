using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Arch's local package database, as text (PRD §14, §70).
/// </summary>
/// <remarks>
/// <para>
/// <c>libalpm</c> keeps one directory per installed package under <c>/var/lib/pacman/local</c>, and
/// three of its files answer everything asked here: <c>desc</c> says what the package is and how it
/// was validated when it was installed, <c>files</c> lists every path it owns, and <c>mtree</c>
/// carries the size and SHA-256 of each of those paths as they were shipped. That last one is what
/// <c>pacman -Qkk</c> compares against, and it is what lets this program answer "is the running
/// binary still the one the distribution shipped" without a signature inside the ELF, which an ELF
/// does not have (PRD §70).
/// </para>
/// <para>
/// Reading the database rather than running <c>pacman</c>: a process manager that shells out per
/// process is a process manager that spawns four hundred processes a second, and the format below
/// is the same one the library reads.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class PacmanLocalDatabase {

  /// <summary>
  /// What <c>pacman</c> checked before it installed the package, out of <c>%VALIDATION%</c>.
  /// </summary>
  /// <remarks>
  /// This is the only signature record on the machine. The package was signed, its signature was
  /// checked once at install time, and the database remembers that it was — the signature itself is
  /// long gone with the downloaded archive. So "the file matches its package" and "the package was
  /// signed" are two separate readings that together are worth a verdict, and neither is one alone.
  /// </remarks>
  public enum Validation : byte {

    /// <summary>No <c>%VALIDATION%</c> line, or a word this program does not know.</summary>
    Unknown = 0,

    /// <summary>Installed with checking explicitly turned off.</summary>
    None,

    /// <summary>Only the archive's checksum was compared. Nobody signed anything.</summary>
    Checksum,

    /// <summary>A PGP signature was verified against the keyring at install time.</summary>
    Signature,

  }

  /// <summary>What <c>desc</c> says the package is.</summary>
  public readonly record struct Description(string? Name, string? Version, Validation Validation);

  /// <summary>One line of <c>mtree</c>: what the package shipped at that path.</summary>
  /// <param name="Sha256">
  /// Null for anything with no digest of its own — a directory, a symbolic link — which is a
  /// different answer from a digest that did not match.
  /// </param>
  public readonly record struct Entry(string? Sha256, Counter SizeBytes);

  private static ReadOnlySpan<byte> _name => "%NAME%"u8;
  private static ReadOnlySpan<byte> _version => "%VERSION%"u8;
  private static ReadOnlySpan<byte> _validation => "%VALIDATION%"u8;
  private static ReadOnlySpan<byte> _files => "%FILES%"u8;

  /// <summary>
  /// Reads <c>desc</c>: the name, the version and how the package was validated.
  /// </summary>
  /// <remarks>
  /// The format is a header line in percent signs followed by its values, one per line, until a
  /// blank line. Only three headers are read; the rest — dependencies, licences, install date — are
  /// skipped rather than parsed, because nothing here asks for them.
  /// </remarks>
  public static Description ReadDescription(ReadOnlySpan<byte> desc) {
    string? name = null, version = null;
    var validation = Validation.Unknown;

    var scanner = new AsciiScanner(desc);
    while (!scanner.IsEmpty) {
      var header = Trim(scanner.NextLine());
      if (header.IsEmpty || header[0] != (byte)'%')
        continue;

      var wantsName = header.SequenceEqual(_name);
      var wantsVersion = header.SequenceEqual(_version);
      var wantsValidation = header.SequenceEqual(_validation);
      if (!wantsName && !wantsVersion && !wantsValidation)
        continue;

      var value = Trim(scanner.NextLine());
      if (value.IsEmpty)
        continue;

      if (wantsName)
        name = Encoding.UTF8.GetString(value);
      else if (wantsVersion)
        version = Encoding.UTF8.GetString(value);
      else
        validation = ReadValidation(value);
    }

    return new(name, version, validation);
  }

  private static Validation ReadValidation(ReadOnlySpan<byte> value) {
    // The words libalpm writes. "sha256" and "md5" both mean the archive's checksum was compared and
    // nobody signed it, which is one answer and not two — the difference between two checksums is
    // not a difference in what is known about who made the package.
    if (value.SequenceEqual("pgp"u8))
      return Validation.Signature;
    if (value.SequenceEqual("sha256"u8) || value.SequenceEqual("md5"u8))
      return Validation.Checksum;

    return value.SequenceEqual("none"u8) ? Validation.None : Validation.Unknown;
  }

  /// <summary>
  /// Walks the paths in a <c>files</c> list, without a leading slash and directories included.
  /// </summary>
  /// <remarks>
  /// A <c>ref struct</c> rather than a list of strings: this is read for every installed package to
  /// build the index, which is more than half a million lines on an ordinary desktop, and turning
  /// each of them into a string to throw it away again is the whole cost of the feature.
  /// </remarks>
  public static PathEnumerator Paths(ReadOnlySpan<byte> files) => new(files);

  /// <summary>The lines of a <c>files</c> list after its <c>%FILES%</c> header.</summary>
  public ref struct PathEnumerator {

    private AsciiScanner _scanner;
    private bool _started;

    internal PathEnumerator(ReadOnlySpan<byte> files) {
      this._scanner = new(files);
      this.Current = default;
    }

    /// <summary>The path, relative and unescaped — <c>files</c> writes the bytes as they are.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    public bool MoveNext() {
      while (!this._scanner.IsEmpty) {
        var line = Trim(this._scanner.NextLine());
        if (line.IsEmpty)
          continue;

        // Everything before %FILES% is another section — a backup list, most often — and its paths
        // are the same paths, so reading them too would be harmless. The header is honoured anyway:
        // a section this parser has never seen is not something to guess the meaning of.
        if (line[0] == (byte)'%') {
          this._started = line.SequenceEqual(_files);
          continue;
        }

        if (!this._started)
          continue;

        // Directories end in a slash and own nothing that can be running.
        if (line[^1] == (byte)'/')
          continue;

        this.Current = line;
        return true;
      }

      return false;
    }

    public readonly PathEnumerator GetEnumerator() => this;

  }

  /// <summary>Whether a <c>files</c> list names this exact path, which is what confirms an owner.</summary>
  public static bool Owns(ReadOnlySpan<byte> files, ReadOnlySpan<byte> relativePath) {
    foreach (var path in Paths(files))
      if (path.SequenceEqual(relativePath))
        return true;

    return false;
  }

  /// <summary>
  /// What the package shipped at one path, out of its <c>mtree</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The format is libarchive's: <c>/set</c> lines carry defaults for what follows, and each entry
  /// is a path beginning <c>./</c> followed by <c>key=value</c> pairs. Only the entry's own keys are
  /// read — the defaults matter for mode and type, and neither a size nor a digest is ever inherited.
  /// </para>
  /// <para>
  /// Paths are escaped: a space is written <c>\040</c>, which is why the path is decoded rather than
  /// compared as it stands. A machine here has such files — <c>alsa-ucm-conf</c> ships
  /// <c>Librem\0405.conf</c> — so a comparison of raw bytes would report those files as missing from
  /// the package that owns them.
  /// </para>
  /// </remarks>
  public static bool TryFindEntry(ReadOnlySpan<byte> mtree, ReadOnlySpan<byte> relativePath, out Entry entry) {
    entry = default;

    Span<byte> decoded = stackalloc byte[512];
    var scanner = new AsciiScanner(mtree);
    while (!scanner.IsEmpty) {
      var line = Trim(scanner.NextLine());
      // "#mtree" and its comments, "/set" and "/unset": none of them is an entry.
      if (line.IsEmpty || line[0] == (byte)'#' || line[0] == (byte)'/')
        continue;

      var lineScanner = new AsciiScanner(line);
      var path = lineScanner.NextField();
      if (!AsciiScanner.StartsWith(path, "./"u8))
        continue;

      path = path[2..];
      var length = Unescape(path, decoded);
      if (length < 0 || !decoded[..length].SequenceEqual(relativePath))
        continue;

      string? digest = null;
      var size = Counter.Unknown(UnknownReason.NotSupportedOnPlatform);
      while (!lineScanner.IsEmpty) {
        var field = lineScanner.NextField();
        if (AsciiScanner.StartsWith(field, "sha256digest="u8))
          digest = Encoding.ASCII.GetString(field[13..]);
        else if (AsciiScanner.StartsWith(field, "size="u8))
          size = Counter.Of(AsciiScanner.ParseUInt64(field[5..]));
      }

      entry = new(digest, size);
      return true;
    }

    return false;
  }

  /// <summary>
  /// Decodes one <c>mtree</c> path in place.
  /// </summary>
  /// <returns>The decoded length, or -1 when it does not fit or the escape makes no sense.</returns>
  private static int Unescape(ReadOnlySpan<byte> path, Span<byte> destination) {
    var written = 0;
    for (var i = 0; i < path.Length; ++i) {
      if (written >= destination.Length)
        return -1;

      var b = path[i];
      if (b != (byte)'\\') {
        destination[written++] = b;
        continue;
      }

      // "\\" is one backslash; "\nnn" is one octal byte. Anything else is a form this parser has
      // never seen, and guessing at it would silently rename somebody's file.
      if (i + 1 < path.Length && path[i + 1] == (byte)'\\') {
        destination[written++] = (byte)'\\';
        ++i;
        continue;
      }

      if (i + 3 >= path.Length)
        return -1;

      var value = 0;
      for (var digit = 1; digit <= 3; ++digit) {
        var c = path[i + digit];
        if (c is < (byte)'0' or > (byte)'7')
          return -1;

        value = value * 8 + (c - '0');
      }

      if (value > 255)
        return -1;

      destination[written++] = (byte)value;
      i += 3;
    }

    return written;
  }

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line) {
    while (!line.IsEmpty && (line[^1] == (byte)'\r' || line[^1] == (byte)'\n'))
      line = line[..^1];

    return line;
  }

}
