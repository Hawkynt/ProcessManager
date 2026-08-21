using System.Globalization;
using System.Security.Cryptography;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a file on disk is, for the properties of an executable or a loaded module (PRD §25.3, §25.6).
/// </summary>
/// <remarks>
/// Every field carries the reason it is missing rather than a zero. A file nobody may read and a
/// file of zero bytes are different answers, and a properties box that renders both as "0 B" is the
/// exact failure §72.3 exists to stop.
/// </remarks>
/// <param name="Reason">
/// Why nothing could be read: it is gone, or this user may not look at it. Null when it was read.
/// </param>
public readonly record struct FileFacts(
  string Path,
  bool Exists,
  long SizeBytes,
  DateTime? ModifiedUtc,
  string? Permissions,
  string? Reason
) {

  /// <summary>
  /// Reads what the file system knows without opening the file.
  /// </summary>
  /// <remarks>
  /// Metadata only, and deliberately: opening a device node blocks, and a properties box that hangs
  /// on <c>/dev/nvidia0</c> has already taken the window down before anybody sees a field. The hash
  /// is the one thing here that reads the bytes, and it is asked for separately.
  /// </remarks>
  public static FileFacts Describe(string? path) {
    if (string.IsNullOrWhiteSpace(path))
      return new(string.Empty, false, 0, null, null, "there is no path to describe");

    try {
      var info = new FileInfo(path);
      if (!info.Exists)
        return new(path, false, 0, null, null, "there is no such file; the image may have been replaced since the process started");

      return new(path, true, info.Length, info.LastWriteTimeUtc, ReadMode(path), null);
    } catch (UnauthorizedAccessException) {
      return new(path, false, 0, null, null, "this file may not be read as this user");
    } catch (IOException e) {
      return new(path, false, 0, null, null, e.Message);
    }
  }

  /// <summary>The mode in the <c>rwxr-xr-x</c> notation <c>ls</c> writes, or null off Unix.</summary>
  private static string? ReadMode(string path) {
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
      return null;

    try {
      var mode = File.GetUnixFileMode(path);
      Span<char> text = stackalloc char[9];
      var bits = (UnixFileMode[])[
        UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute,
        UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute,
        UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute,
      ];

      for (var i = 0; i < 9; ++i)
        text[i] = (mode & bits[i]) == 0 ? '-' : "rwx"[i % 3];

      return new(text);
    } catch (PlatformNotSupportedException) {
      return null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}

/// <summary>
/// A file's SHA-256, computed on request and never as a side effect (PRD §25.6, §70).
/// </summary>
/// <remarks>
/// <para>
/// Asked for explicitly, because hashing is the one operation here whose cost is the size of the
/// file: a 300 MB runtime image is a second of disk on a cold cache, and doing it automatically for
/// every module a process has loaded would read a gigabyte to fill a column nobody looked at.
/// </para>
/// <para>
/// <b>A hash is not a verdict.</b> It says what the bytes are and nothing about whether they are
/// signed, trusted or known — the four are separate operations and this program never conflates
/// them (PRD §70).
/// </para>
/// </remarks>
/// <param name="Sha1">
/// The same bytes under the older digest, because that is what a great many vulnerability
/// databases, package manifests and threat feeds are still keyed by. Collidable since 2017 and
/// therefore not evidence of anything on its own — which is one more reason a hash is not a verdict.
/// </param>
/// <param name="Why">
/// The same failure as <paramref name="Reason"/> in the form a column can render: prose belongs in
/// a properties box and a table cell needs the mark and the reason behind it (PRD §72.3).
/// </param>
public readonly record struct FileDigest(string? Sha256, string? Sha1, string? Reason, UnknownReason Why) {

  /// <summary>
  /// Both digests of one file, in a single read of it.
  /// </summary>
  /// <remarks>
  /// One pass rather than two: the cost of hashing is the disk, not the arithmetic, and reading a
  /// 300 MB image twice to answer two questions about the same bytes would double the only part
  /// that is expensive.
  /// </remarks>
  public static FileDigest Of(string? path) {
    if (string.IsNullOrWhiteSpace(path))
      return Failed("there is no path to hash", UnknownReason.NotSupportedOnPlatform);

    try {
      // Streamed rather than read whole: the point of hashing a large image is not to hold it in
      // memory first.
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

      var buffer = new byte[128 * 1024];
      while (true) {
        var read = stream.Read(buffer);
        if (read <= 0)
          break;

        sha256.AppendData(buffer, 0, read);
        sha1.AppendData(buffer, 0, read);
      }

      return new(
        Convert.ToHexStringLower(sha256.GetHashAndReset()),
        Convert.ToHexStringLower(sha1.GetHashAndReset()),
        null,
        UnknownReason.None
      );
    } catch (UnauthorizedAccessException) {
      return Failed("this file may not be read as this user", UnknownReason.NotPermitted);
    } catch (FileNotFoundException) {
      return Failed("the file is gone", UnknownReason.SourceGone);
    } catch (DirectoryNotFoundException) {
      return Failed("the file is gone", UnknownReason.SourceGone);
    } catch (IOException e) {
      return Failed(e.Message, UnknownReason.CounterInvalid);
    }
  }

  private static FileDigest Failed(string reason, UnknownReason why) => new(null, null, reason, why);

  /// <summary>The hash in groups of eight, which is how a person compares two of them by eye.</summary>
  public string Display {
    get {
      if (this.Sha256 is not { Length: 64 } hex)
        return this.Reason ?? "not computed";

      var parts = new string[8];
      for (var i = 0; i < 8; ++i)
        parts[i] = hex.Substring(i * 8, 8);

      return string.Join(' ', parts);
    }
  }

  public override string ToString() => this.Sha256 ?? this.Reason ?? "not computed";

}

/// <summary>Sizes and timestamps in the shape the properties box wants them.</summary>
public static class FileFactsFormatting {

  public static string Size(in FileFacts facts)
    => facts.Reason is { } reason
      ? reason
      : $"{Humanize.Bytes((ulong)facts.SizeBytes)}  ({facts.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";

  public static string Modified(in FileFacts facts)
    => facts.ModifiedUtc is { } when
      ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
      : facts.Reason ?? "unknown";

}
