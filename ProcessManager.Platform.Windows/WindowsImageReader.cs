using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What each running image says about itself, read once per image (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Per <em>image</em> and not per process, which is the whole reason this class exists: three
/// hundred processes of one runtime share one binary, and reading it three hundred times would be
/// three hundred file reads for one answer. The cache is keyed on the path and is invalidated by the
/// file's own size and modification time, so an image replaced underneath a running process is read
/// again rather than reported as it used to be — which is the case somebody watching a version
/// column is watching for (PRD §5.4).
/// </para>
/// <para>
/// Not marked as Windows-only, and it calls nothing that is: it opens a file and hands the bytes to
/// <see cref="PortableExecutable"/>. That is what lets the whole of this be exercised on the Linux
/// leg against real PE images, which is where it was actually tested (PRD §9.4).
/// </para>
/// </remarks>
internal sealed class WindowsImageReader {

  /// <summary>
  /// A cache entry, with what it was read from so a replaced file can be noticed.
  /// </summary>
  /// <param name="Facts">
  /// Null when the file exists and is not a PE image, which is a different answer from not having
  /// looked and is cached just as firmly — otherwise every sample would re-read it.
  /// </param>
  private readonly record struct Entry(long Length, long ModifiedTicks, PeImageFacts? Facts, UnknownReason Reason);

  private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Refuses to read anything larger than this, because the answer is in the first few hundred
  /// kilobytes and a single-file publish can be hundreds of megabytes.
  /// </summary>
  /// <remarks>
  /// The whole file is read rather than seeking about in it: the resource section is at the end as
  /// often as not, and a version resource sits behind a section table whose offsets can point
  /// anywhere. Sixty-four megabytes covers every ordinary program and refuses the handful that would
  /// stall a sample; those say why rather than being read halfway (PRD §72.3).
  /// </remarks>
  private const long _MaximumBytes = 64L * 1024 * 1024;

  /// <summary>
  /// What the image at <paramref name="path"/> says about itself, or why there is no answer.
  /// </summary>
  public PeImageFacts? Read(string? path, out UnknownReason reason) {
    reason = UnknownReason.None;
    if (path is not { Length: > 0 }) {
      // No path at all: a kernel thread, or a process this user may not open. Which of the two it is
      // was decided where the path was asked for, and both mean there is nothing here to read.
      reason = UnknownReason.NotPermitted;
      return null;
    }

    FileInfo info;
    try {
      info = new(path);
      if (!info.Exists) {
        // The running image can be deleted underneath the process, which keeps running from it.
        reason = UnknownReason.SourceGone;
        return null;
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
      reason = UnknownReason.NotPermitted;
      return null;
    }

    var length = info.Length;
    var modified = info.LastWriteTimeUtc.Ticks;
    if (this._byPath.TryGetValue(path, out var cached) && cached.Length == length && cached.ModifiedTicks == modified) {
      reason = cached.Reason;
      return cached.Facts;
    }

    var entry = ReadFile(path, length);
    this._byPath[path] = entry;
    reason = entry.Reason;
    return entry.Facts;
  }

  private static Entry ReadFile(string path, long length) {
    var modified = File.GetLastWriteTimeUtc(path).Ticks;
    if (length is <= 0 or > _MaximumBytes)
      return new(length, modified, null, UnknownReason.NotImplementedHere);

    byte[] bytes;
    try {
      bytes = File.ReadAllBytes(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(length, modified, null, UnknownReason.NotPermitted);
    }

    if (!PortableExecutable.TryRead(bytes, out var facts))
      // A file that runs and is not a PE image is a real thing on Windows — a script through its
      // interpreter, whatever a subsystem is running — and this is a finding about it rather than a
      // failure to read (PRD §72.3).
      return new(length, modified, null, UnknownReason.NotSupportedOnPlatform);

    return new(length, modified, facts, UnknownReason.None);
  }

  /// <summary>Drops entries for images nothing is running any more.</summary>
  /// <remarks>
  /// Bounded rather than swept every sample: the set of distinct images on a machine is small and
  /// stable, and walking it once a second to find nothing would cost more than the entries do.
  /// </remarks>
  public void Prune(HashSet<string> livePaths) {
    if (this._byPath.Count < 4096)
      return;

    foreach (var path in this._byPath.Keys.Where(path => !livePaths.Contains(path)).ToList())
      this._byPath.Remove(path);
  }

  /// <summary>
  /// Copies what was read into a record, or the reason there is nothing (PRD §14).
  /// </summary>
  /// <remarks>
  /// A PE image that carries no version resource at all leaves the five strings null with the reason
  /// unset, which the column renders as an empty cell rather than as a placeholder — because
  /// "this program ships no version resource" is a true statement about a great many programs and is
  /// not the same finding as "nobody could look". The subsystem is filled either way: it is in the
  /// header rather than in the resource.
  /// </remarks>
  public static void Apply(ref ProcessRecord record, PeImageFacts? facts, UnknownReason reason) {
    if (facts is not { } value) {
      record.ImageVersionReason = reason;
      record.Subsystem = Counter.Unknown(reason == UnknownReason.None ? UnknownReason.NotSupportedOnPlatform : reason);
      return;
    }

    record.ImageDescription = value.Description;
    record.ImageCompany = value.Company;
    record.ImageProduct = value.Product;
    record.ImageProductVersion = value.ProductVersion;
    record.ImageFileVersion = value.FileVersion;
    record.ImageVersionReason = UnknownReason.None;
    record.Subsystem = Counter.Of(value.Subsystem);
  }

}
