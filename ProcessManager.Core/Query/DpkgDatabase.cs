using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Debian's package database, as text (PRD §14, §70).
/// </summary>
/// <remarks>
/// <para>
/// <c>dpkg</c> keeps a file per package under <c>/var/lib/dpkg/info</c>: <c>&lt;package&gt;.list</c>
/// names every path it installed, and <c>&lt;package&gt;.md5sums</c> carries the digest of each plain
/// file among them. <c>deb-md5sums(5)</c> gives that second format exactly — "a list of MD5 digests
/// (as 32 case-insensitive hexadecimal characters) followed by two spaces (U+0020 SPACE) and the
/// absolute pathname of a plain file, one per line" — with its own example written without the
/// leading slash, which is what the file actually contains.
/// </para>
/// <para>
/// The digests are MD5 and nothing else, because that is what <c>dpkg --verify</c> compares. MD5 has
/// been useless against a deliberate collision for twenty years; against the question actually being
/// asked — has this file been replaced since the package installed it — it is the only answer the
/// package manager kept, and the alternative is not a better digest but no answer at all. What it
/// therefore may not be used for is a claim about an attacker, and nothing here makes one.
/// </para>
/// <para>
/// <b>Held against a recording, never against a live machine.</b> This was written on an Arch system
/// with no <c>dpkg</c> on it, so every claim below is tested by replaying a captured database and
/// none of it has been held against <c>dpkg --verify</c> itself. The <c>.list</c> format in
/// particular has no format man page upstream, so the parser accepts what the file has always
/// contained — one absolute path per line — and nothing cleverer (PRD §9.1, §9.2).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class DpkgDatabase {

  /// <summary>
  /// The package name out of an info file's name: <c>bash.list</c>, <c>bash:amd64.md5sums</c>.
  /// </summary>
  /// <remarks>
  /// The architecture qualifier is dropped. A machine with both <c>libfoo:amd64</c> and
  /// <c>libfoo:i386</c> installed has two of these files and one package name, and the qualifier
  /// belongs to the file rather than to the answer.
  /// </remarks>
  public static string? PackageOf(string? fileName, string extension) {
    if (fileName is not { Length: > 0 } || !fileName.EndsWith(extension, StringComparison.Ordinal))
      return null;

    var name = fileName.AsSpan(0, fileName.Length - extension.Length);
    var colon = name.IndexOf(':');
    if (colon >= 0)
      name = name[..colon];

    return name.IsEmpty ? null : name.ToString();
  }

  /// <summary>Walks the absolute paths in a <c>.list</c> file.</summary>
  public static PathEnumerator Paths(ReadOnlySpan<byte> list) => new(list);

  /// <summary>The lines of a <c>.list</c>, which are absolute paths and nothing else.</summary>
  public ref struct PathEnumerator {

    private AsciiScanner _scanner;

    internal PathEnumerator(ReadOnlySpan<byte> list) {
      this._scanner = new(list);
      this.Current = default;
    }

    /// <summary>The path, still absolute — <c>dpkg</c> writes the leading slash here.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    public bool MoveNext() {
      while (!this._scanner.IsEmpty) {
        var line = Trim(this._scanner.NextLine());
        // "/." is the package's own root and names no file; every other line is a path, and which
        // of them are directories the file does not say. That is why an owner found here is
        // confirmed against the digest list before anything is claimed about the bytes.
        if (line.IsEmpty || line[0] != (byte)'/' || line.SequenceEqual("/."u8))
          continue;

        this.Current = line;
        return true;
      }

      return false;
    }

    public readonly PathEnumerator GetEnumerator() => this;

  }

  /// <summary>
  /// The MD5 <c>dpkg</c> recorded for one path, or null when the file lists no such path.
  /// </summary>
  /// <param name="relativePath">Without the leading slash, which is how the file writes it.</param>
  public static string? FindMd5(ReadOnlySpan<byte> md5Sums, ReadOnlySpan<byte> relativePath) {
    var scanner = new AsciiScanner(md5Sums);
    while (!scanner.IsEmpty) {
      var line = Trim(scanner.NextLine());
      // Thirty-two hex digits, two spaces, then the path — and the path may contain spaces of its
      // own, so it is taken as everything after the fixed prefix rather than as the next field.
      if (line.Length < 35 || line[32] != (byte)' ' || line[33] != (byte)' ')
        continue;

      if (line[34..].SequenceEqual(relativePath))
        return Encoding.ASCII.GetString(line[..32]);
    }

    return null;
  }

  /// <summary>
  /// The version of one package out of <c>/var/lib/dpkg/status</c>.
  /// </summary>
  /// <remarks>
  /// The status file is a stanza per package in the RFC 822 shape <c>dpkg</c> shares with control
  /// files: <c>Package:</c> opens one, <c>Version:</c> is somewhere inside it, and a blank line ends
  /// it. Only the two lines are read; the rest of the stanza is skipped rather than parsed.
  /// </remarks>
  public static string? FindVersion(ReadOnlySpan<byte> status, string package) {
    var wanted = Encoding.UTF8.GetBytes(package);
    var inPackage = false;

    var scanner = new AsciiScanner(status);
    while (!scanner.IsEmpty) {
      var line = Trim(scanner.NextLine());
      if (line.IsEmpty) {
        inPackage = false;
        continue;
      }

      if (AsciiScanner.StartsWith(line, "Package: "u8)) {
        inPackage = line[9..].SequenceEqual(wanted);
        continue;
      }

      if (inPackage && AsciiScanner.StartsWith(line, "Version: "u8))
        return Encoding.UTF8.GetString(line[9..]);
    }

    return null;
  }

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line) {
    while (!line.IsEmpty && (line[^1] == (byte)'\r' || line[^1] == (byte)'\n'))
      line = line[..^1];

    return line;
  }

}
