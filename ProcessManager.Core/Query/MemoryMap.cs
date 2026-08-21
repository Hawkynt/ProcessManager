using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/maps</c> and <c>/proc/[pid]/smaps</c>, one row per mapping (PRD §34).
/// </summary>
/// <remarks>
/// <para>
/// The same two files <see cref="MapsParser"/> reads, and the header line is parsed by the same
/// method — there is one parser for the shape of that line and this is not a second one. What differs
/// is the question: §31 asks which images are loaded and folds a library's five consecutive mappings
/// into one row; §34 asks what is at an address and must not, because the read-only segment and the
/// writable one are the two halves the row exists to keep apart.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg against a recorded file
/// (PRD §9.1, §9.2).
/// </para>
/// </remarks>
public static class MemoryMap {

  /// <summary>
  /// Every mapping in a <c>maps</c> or <c>smaps</c> file, in the order the kernel wrote them.
  /// </summary>
  /// <param name="content">The file, whole.</param>
  /// <param name="detailWhenUnreported">
  /// The reason to record for the per-mapping counters this content does not carry. Only the caller
  /// knows which file it read: <c>maps</c> has no counters at all, and being refused <c>smaps</c> is
  /// a different answer from a kernel that has none (PRD §3.4).
  /// </param>
  /// <remarks>
  /// The kernel writes the regions in ascending address order and this keeps that order. Sorting by
  /// anything else is the front-end's business; re-ordering here would lose the one property a memory
  /// map has that a list of modules does not — that the row above is the memory below.
  /// </remarks>
  public static List<MemoryRegionRecord> Collect(ReadOnlySpan<byte> content, Counter detailWhenUnreported) {
    var result = new List<MemoryRegionRecord>();

    // The mapping whose counter block is being read. Held as fields rather than by mutating the list
    // entry per line, because a record struct in a list is copied on every read and the counter block
    // of one mapping is twenty-five lines long.
    var open = false;
    var region = default(MapsParser.Region);
    string? path = null;
    var deleted = false;
    var detail = default(Detail);

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      if (MapsParser.TryParseRegion(line, out var parsed, out var pathRange)) {
        Flush();
        open = true;
        region = parsed;
        path = ReadPath(line[pathRange], out deleted);
        detail = new(detailWhenUnreported);
        continue;
      }

      if (open)
        detail.Take(line);
    }

    Flush();
    return result;

    // Adds the mapping whose counter block has just ended. Deferred to here rather than done at the
    // header, because everything below the permissions arrives after the row it belongs to.
    void Flush() {
      if (!open)
        return;

      open = false;
      result.Add(new(
        Start: region.Start,
        End: region.End,
        Size: region.End - region.Start,
        Permissions: region.Permissions,
        Kind: Classify(path),
        Path: path,
        IsDeleted: deleted,
        FileOffset: Counter.Of(region.FileOffset),
        // Nought is what the kernel writes for a mapping with no file behind it, and it is a real
        // answer rather than a missing one — so it stays a nought and does not become a reason.
        Inode: Counter.Of(region.Inode),
        Device: FormatDevice(region.DeviceMajor, region.DeviceMinor),
        ResidentBytes: detail.Resident,
        ProportionalBytes: detail.Proportional,
        PrivateDirtyBytes: detail.PrivateDirty,
        SharedDirtyBytes: detail.SharedDirty,
        AnonymousBytes: detail.Anonymous,
        SwapBytes: detail.Swap,
        LockedBytes: detail.Locked,
        HugePageBytes: detail.HugePages,
        Flags: detail.Flags
      ));
    }
  }

  /// <summary>
  /// What a mapping is, from the name the kernel wrote beside it.
  /// </summary>
  /// <remarks>
  /// Prefix tests rather than a table of known names, so that a pseudo-region added by a later kernel
  /// — <c>[vvar_vclock]</c> was one, in 6.13 — lands as <see cref="MemoryRegionKind.Pseudo"/> and
  /// reaches the reader under its own name, instead of being classified as a file that does not exist.
  /// </remarks>
  public static MemoryRegionKind Classify(string? path) {
    if (path is not { Length: > 0 })
      return MemoryRegionKind.Anonymous;

    if (path[0] == '[')
      return path switch {
        "[heap]" => MemoryRegionKind.Heap,
        "[stack]" => MemoryRegionKind.Stack,
        _ => path.StartsWith("[v", StringComparison.Ordinal) || path == "[uprobes]"
          ? MemoryRegionKind.KernelProvided
          : MemoryRegionKind.Pseudo,
      };

    // Named memory that is not a file on any disk. The kernel writes all three as paths, and a
    // reader adding their sizes to "what this process has open" would be counting its own shared
    // memory as file cache.
    if (path.StartsWith("/memfd:", StringComparison.Ordinal)
      || path.StartsWith("/dev/shm/", StringComparison.Ordinal)
      || path.StartsWith("/SYSV", StringComparison.Ordinal)
      || path.StartsWith("/anon_hugepage", StringComparison.Ordinal))
      return MemoryRegionKind.SharedMemory;

    // A device's mapping is memory on the card, not memory on the machine, and it is the single
    // largest thing in a browser's map on a machine with a graphics driver.
    return path.StartsWith("/dev/", StringComparison.Ordinal)
      ? MemoryRegionKind.Device
      : MemoryRegionKind.FileBacked;
  }

  /// <summary>
  /// The name beside a mapping, kept whether or not it is a path.
  /// </summary>
  /// <remarks>
  /// The one place this deliberately differs from <see cref="MapsParser"/>: the module list throws
  /// away everything that is not an absolute path, because <c>[heap]</c> is not a loaded image. Here
  /// it is a row, and the name is what identifies it.
  /// </remarks>
  private static string? ReadPath(ReadOnlySpan<byte> pathBytes, out bool deleted) {
    deleted = false;
    while (!pathBytes.IsEmpty && AsciiScanner.IsSpace(pathBytes[^1]))
      pathBytes = pathBytes[..^1];

    if (pathBytes.IsEmpty)
      return null;

    const string DeletedSuffix = " (deleted)";
    if (pathBytes.Length > DeletedSuffix.Length && pathBytes[^DeletedSuffix.Length..].SequenceEqual(" (deleted)"u8)) {
      deleted = true;
      pathBytes = pathBytes[..^DeletedSuffix.Length];
    }

    return pathBytes.IsEmpty ? null : Encoding.UTF8.GetString(pathBytes);
  }

  private static string FormatDevice(uint major, uint minor)
    => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{major:x2}:{minor:x2}");

  /// <summary>
  /// The counter block under one mapping's header line, as it is being read.
  /// </summary>
  /// <remarks>
  /// Every counter starts at the caller's reason and is replaced only by a line that was actually
  /// there, which is what makes a <c>maps</c> file and an old kernel's <c>smaps</c> produce holes
  /// rather than zeroes. <c>Locked</c> especially: it has been in the file since 2.6.14 and
  /// <c>AnonHugePages</c> has not, so a kernel without transparent huge pages must not be reported as
  /// a process using none of them (PRD §3.4).
  /// </remarks>
  private struct Detail(Counter unreported) {

    public Counter Resident = unreported;
    public Counter Proportional = unreported;
    public Counter PrivateDirty = unreported;
    public Counter SharedDirty = unreported;
    public Counter Anonymous = unreported;
    public Counter Swap = unreported;
    public Counter Locked = unreported;
    public Counter HugePages = unreported;
    public string? Flags = null;

    /// <summary>Reads one line of the block, and ignores the twenty this view has no column for.</summary>
    public void Take(ReadOnlySpan<byte> line) {
      // Ordered by how often each key appears rather than alphabetically: every mapping has an Rss
      // line and almost none has a Locked one, and this runs twenty-five times per mapping over as
      // many as ten thousand mappings.
      if (MapsParser.TryParseKilobytes(line, "Rss:"u8, out var kilobytes))
        this.Resident = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Pss:"u8, out kilobytes))
        this.Proportional = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Private_Dirty:"u8, out kilobytes))
        this.PrivateDirty = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Shared_Dirty:"u8, out kilobytes))
        this.SharedDirty = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Anonymous:"u8, out kilobytes))
        this.Anonymous = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Swap:"u8, out kilobytes))
        // Swap and not SwapPss, which is the line below it: this row is one mapping of one process,
        // and the proportional figure only means something summed over all of them.
        this.Swap = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "Locked:"u8, out kilobytes))
        this.Locked = Counter.Of(kilobytes * 1024);
      else if (MapsParser.TryParseKilobytes(line, "AnonHugePages:"u8, out kilobytes))
        this.HugePages = Counter.Of(kilobytes * 1024);
      else if (AsciiScanner.StartsWith(line, "VmFlags:"u8))
        // Trailing space and all — the kernel writes one after the last code. An empty flags line
        // does not happen, so an empty string here would be a fact about the parse rather than about
        // the mapping, and it stays null instead.
        this.Flags = Text(line["VmFlags:"u8.Length..]);
    }

    private static string? Text(ReadOnlySpan<byte> value) {
      value = value.Trim(" \t"u8);
      return value.IsEmpty ? null : Encoding.ASCII.GetString(value);
    }

  }

}
