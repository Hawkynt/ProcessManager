using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/maps</c> and <c>/proc/[pid]/smaps</c>, folded into one row per mapped file
/// (PRD §31, §34).
/// </summary>
/// <remarks>
/// <para>
/// The two files have the same header line; <c>smaps</c> merely follows each with a block of
/// <c>Key: n kB</c> counters. So one parser reads both, and the caller decides which it can afford —
/// <c>smaps</c> makes the kernel walk the whole page table of the process, which is why it is asked
/// for only when somebody has opened the modules view (PRD §5.4).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class MapsParser {

  /// <summary>One mapping's header line, before the path.</summary>
  public readonly record struct Region(
    ulong Start,
    ulong End,
    MapPermissions Permissions,
    ulong FileOffset,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode
  );

  /// <summary>
  /// Parses a header line and hands back where its path begins.
  /// </summary>
  /// <param name="line">One line, without its newline.</param>
  /// <param name="region">The addresses, permissions, offset, device and inode.</param>
  /// <param name="path">
  /// The rest of the line: an absolute path, a pseudo-name such as <c>[heap]</c>, or empty for an
  /// anonymous mapping. Still carrying its <c>(deleted)</c> suffix, if it had one.
  /// </param>
  /// <returns>False for anything that is not a header line — every counter line of <c>smaps</c>.</returns>
  public static bool TryParseRegion(ReadOnlySpan<byte> line, out Region region, out Range path) {
    region = default;
    path = default;

    // The address range is the only field whose shape identifies the line: a counter line of smaps
    // is "Rss:  8 kB" and has no dash inside its first field, and VmFlags has no dash at all. Testing
    // for the dash is what keeps the two kinds of line apart without a state machine.
    var scanner = new AsciiScanner(line);
    var range = scanner.NextField();
    var dash = range.IndexOf((byte)'-');
    if (dash <= 0 || dash == range.Length - 1)
      return false;

    var start = AsciiScanner.ParseHex(range[..dash]);
    var end = AsciiScanner.ParseHex(range[(dash + 1)..]);
    if (end < start)
      return false;

    var permissions = ParsePermissions(scanner.NextField());
    if (permissions == MapPermissions.None)
      return false;

    var offset = AsciiScanner.ParseHex(scanner.NextField());
    var device = scanner.NextField();
    var colon = device.IndexOf((byte)':');
    if (colon < 0)
      return false;

    var inode = scanner.NextUInt64();
    var rest = scanner.RestOfLine();
    // The path is what is left of the line, spaces and all. Taking it as a whitespace-delimited field
    // truncated "/opt/My App/libfoo.so" at the space and reported a module that does not exist.
    var offsetInLine = line.Length - rest.Length;
    path = new(offsetInLine, line.Length);

    region = new(
      start,
      end,
      permissions,
      offset,
      (uint)AsciiScanner.ParseHex(device[..colon]),
      (uint)AsciiScanner.ParseHex(device[(colon + 1)..]),
      inode
    );

    return true;
  }

  /// <summary>The four permission characters of a <c>maps</c> line, as flags.</summary>
  public static MapPermissions ParsePermissions(ReadOnlySpan<byte> text) {
    if (text.Length < 4)
      return MapPermissions.None;

    var result = MapPermissions.None;
    if (text[0] == (byte)'r')
      result |= MapPermissions.Read;
    if (text[1] == (byte)'w')
      result |= MapPermissions.Write;
    if (text[2] == (byte)'x')
      result |= MapPermissions.Execute;

    // The fourth character is always one of the two, never absent, which is what makes a mapping with
    // no access at all ("---p") still parse to something other than None.
    return result | (text[3] == (byte)'s' ? MapPermissions.Shared : MapPermissions.Private);
  }

  /// <summary>The same, for a <see cref="ModuleRecord.Permissions"/> string that has come back out.</summary>
  public static MapPermissions ParsePermissions(string? text) {
    if (text is null || text.Length < 4)
      return MapPermissions.None;

    Span<byte> bytes = stackalloc byte[4];
    for (var i = 0; i < 4; ++i)
      bytes[i] = (byte)text[i];

    return ParsePermissions(bytes);
  }

  /// <summary>Renders flags back to the <c>rwxp</c> form the kernel writes.</summary>
  public static string Format(MapPermissions permissions) {
    if (permissions == MapPermissions.None)
      return string.Empty;

    Span<char> text = stackalloc char[4];
    text[0] = (permissions & MapPermissions.Read) != 0 ? 'r' : '-';
    text[1] = (permissions & MapPermissions.Write) != 0 ? 'w' : '-';
    text[2] = (permissions & MapPermissions.Execute) != 0 ? 'x' : '-';
    text[3] = (permissions & MapPermissions.Shared) != 0 ? 's' : 'p';
    return new(text);
  }

  /// <summary>
  /// Folds a whole <c>maps</c> or <c>smaps</c> file into one <see cref="ModuleRecord"/> per file.
  /// </summary>
  /// <param name="content">The file, whole.</param>
  /// <param name="residentWhenUnreported">
  /// The reason to record for a mapping whose resident size the content did not carry. Only the
  /// caller knows which file it read: <c>maps</c> carries no resident size at all, and being refused
  /// <c>smaps</c> is a different answer from a kernel that has none.
  /// </param>
  /// <remarks>
  /// Anonymous mappings and the kernel's own pseudo-regions — <c>[heap]</c>, <c>[stack]</c>,
  /// <c>[vdso]</c> — are skipped. They belong to the memory map of §34, not to a list of loaded
  /// images; the modules view exists to answer "which code is in this process".
  /// </remarks>
  public static List<ModuleRecord> Collect(ReadOnlySpan<byte> content, Counter residentWhenUnreported) {
    var result = new List<ModuleRecord>();
    var byPath = new Dictionary<string, int>(StringComparer.Ordinal);

    var current = -1;
    var currentHasResident = false;
    ulong currentResident = 0;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      if (TryParseRegion(line, out var region, out var pathRange)) {
        Flush();
        // Detached before the new mapping is identified, so that the counter block of an anonymous
        // mapping cannot be charged to whichever file happened to be mapped above it.
        current = -1;
        currentHasResident = false;
        currentResident = 0;

        var path = ParsePath(line[pathRange], out var deleted);
        if (path is null)
          continue;

        // Folded into the row above only when this mapping continues it. An image's segments are
        // laid down as one reservation and are exactly adjacent, so adjacency is what "the same
        // load" means — and .NET maps an assembly twice, two terabytes apart, which folded into one
        // row would report an image spanning 271 GB of address space.
        if (byPath.TryGetValue(path, out current) && result[current].EndAddress == region.Start) {
          var existing = result[current];
          result[current] = existing with {
            EndAddress = region.End,
            Size = existing.Size + (region.End - region.Start),
            Permissions = Format(ParsePermissions(existing.Permissions) | region.Permissions),
            FileOffset = Counter.Of(Math.Min(existing.FileOffset.GetValueOrDefault(), region.FileOffset)),
            MappingCount = existing.MappingCount + 1,
          };
        } else {
          current = result.Count;
          byPath[path] = current;
          result.Add(new(
            Path: path,
            BaseAddress: region.Start,
            Size: region.End - region.Start,
            Permissions: Format(region.Permissions),
            EndAddress: region.End,
            // Filled by Flush once the mapping's counter block has been read, or left at the caller's
            // reason when there is no counter block to read.
            ResidentBytes: residentWhenUnreported,
            FileOffset: Counter.Of(region.FileOffset),
            Inode: Counter.Of(region.Inode),
            Device: FormatDevice(region.DeviceMajor, region.DeviceMinor),
            IsDeleted: deleted,
            MappingCount: 1,
            // Everything below comes from the file on disk rather than from /proc, so it is the
            // platform reader's to fill in — see PRD §31. Unread is not zero.
            FileSizeBytes: Counter.NotSampledYet,
            FileModifiedUtcTicks: 0,
            Type: ModuleType.Unknown,
            Architecture: null,
            EntryPoint: Counter.NotSampledYet,
            Soname: null,
            Interpreter: null
          ));
        }

        continue;
      }

      if (current >= 0 && TryParseKilobytes(line, "Rss:"u8, out var kilobytes)) {
        currentResident += kilobytes * 1024;
        currentHasResident = true;
      }
    }

    Flush();
    return result;

    // Adds the resident bytes of the mapping that has just ended to the row it belongs to. Summed
    // rather than assigned, because a library's five mappings are five counter blocks and the row is
    // the whole library.
    void Flush() {
      if (current < 0 || !currentHasResident)
        return;

      var row = result[current];
      var total = row.ResidentBytes.HasValue ? row.ResidentBytes.Value : 0;
      result[current] = row with { ResidentBytes = Counter.Of(total + currentResident) };
      currentHasResident = false;
      currentResident = 0;
    }
  }

  /// <summary>One <c>Key: n kB</c> line of <c>smaps</c>, in bytes.</summary>
  public static bool TryParseKilobytes(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong kilobytes) {
    kilobytes = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    var scanner = new AsciiScanner(line[key.Length..]);
    kilobytes = scanner.NextUInt64();
    return true;
  }

  /// <summary>
  /// The path of a mapping, or null when it is not a file at all.
  /// </summary>
  /// <remarks>
  /// A file whose name genuinely ends in <c>" (deleted)"</c> is indistinguishable from a deleted one,
  /// because the kernel appends the suffix to the name without escaping it. That ambiguity is the
  /// kernel's and cannot be resolved from here; inventing a rule to break the tie would only make the
  /// wrong answer look deliberate.
  /// </remarks>
  private static string? ParsePath(ReadOnlySpan<byte> pathBytes, out bool deleted) {
    deleted = false;
    while (!pathBytes.IsEmpty && AsciiScanner.IsSpace(pathBytes[^1]))
      pathBytes = pathBytes[..^1];

    if (pathBytes.IsEmpty || pathBytes[0] != (byte)'/')
      return null;

    const string DeletedSuffix = " (deleted)";
    if (pathBytes.Length > DeletedSuffix.Length && pathBytes[^DeletedSuffix.Length..].SequenceEqual(" (deleted)"u8)) {
      deleted = true;
      pathBytes = pathBytes[..^DeletedSuffix.Length];
    }

    return pathBytes.IsEmpty ? null : Encoding.UTF8.GetString(pathBytes);
  }

  /// <summary>The device the file lives on, in the <c>major:minor</c> hex notation <c>maps</c> uses.</summary>
  private static string FormatDevice(uint major, uint minor)
    => string.Create(
      CultureInfo.InvariantCulture,
      $"{major:x2}:{minor:x2}"
    );

}
