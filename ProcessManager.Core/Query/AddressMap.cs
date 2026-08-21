using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which mapping an address is in, for turning a register into somewhere a person can go
/// (PRD §29, §30).
/// </summary>
/// <remarks>
/// <para>
/// The same <c>maps</c> file <see cref="MapsParser"/> reads, kept whole rather than folded. The
/// modules view wants one row per loaded image and drops everything that is not one; an address
/// lookup wants the opposite — the anonymous mapping a thread's stack lives in has no file behind it
/// and is exactly the mapping that answers "how much stack is this thread using".
/// </para>
/// <para>
/// <c>maps</c> is written in ascending address order by the kernel, so the list needs no sorting and
/// a lookup is a binary search.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public sealed class AddressMap {

  /// <summary>
  /// One mapping.
  /// </summary>
  /// <param name="Path">
  /// The file behind it, the kernel's own name for a pseudo-mapping (<c>[stack]</c>, <c>[heap]</c>,
  /// <c>[vdso]</c>), or null for an anonymous one.
  /// </param>
  /// <param name="ModuleBase">
  /// The lowest address any mapping of the same file occupies, which is the load bias of a
  /// position-independent image. Equal to <paramref name="Start"/> for the first mapping of a file,
  /// and to the same value for every later one — so an address anywhere in a library can be turned
  /// back into an offset in the file on disk.
  /// </param>
  public readonly record struct Region(ulong Start, ulong End, string? Path, ulong ModuleBase) {

    /// <summary>True when a file on disk backs this, rather than a name in square brackets.</summary>
    public bool IsFile => this.Path is { Length: > 0 } path && path[0] == '/';

  }

  private readonly Region[] _regions;

  private AddressMap(Region[] regions) => this._regions = regions;

  /// <summary>Nothing was mapped, or nothing could be read.</summary>
  public static readonly AddressMap Empty = new([]);

  public int Count => this._regions.Length;

  /// <summary>Parses a whole <c>maps</c> file.</summary>
  public static AddressMap Parse(ReadOnlySpan<byte> content) {
    var regions = new List<Region>();
    var firstOfPath = new Dictionary<string, ulong>(StringComparer.Ordinal);

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty || !MapsParser.TryParseRegion(line, out var region, out var pathRange))
        continue;

      var path = Name(line[pathRange]);
      var moduleBase = region.Start;
      if (path is { Length: > 0 } && path[0] == '/') {
        if (firstOfPath.TryGetValue(path, out var first))
          moduleBase = first;
        else
          firstOfPath[path] = region.Start;
      }

      regions.Add(new(region.Start, region.End, path, moduleBase));
    }

    return regions.Count == 0 ? Empty : new([.. regions]);
  }

  /// <summary>The mapping containing an address, if any does.</summary>
  public bool TryFind(ulong address, out Region region) {
    var low = 0;
    var high = this._regions.Length - 1;
    while (low <= high) {
      var middle = low + (high - low) / 2;
      var candidate = this._regions[middle];
      if (address < candidate.Start)
        high = middle - 1;
      else if (address >= candidate.End)
        low = middle + 1;
      else {
        region = candidate;
        return true;
      }
    }

    region = default;
    return false;
  }

  /// <summary>
  /// The lowest address a file is mapped at, or false when this process does not map it.
  /// </summary>
  /// <remarks>
  /// The load bias of a position-independent image, which is what has to come back off an address
  /// before the image's own symbol table can be asked about it.
  /// </remarks>
  public bool TryFindModuleBase(string path, out ulong moduleBase) {
    ArgumentNullException.ThrowIfNull(path);
    for (var i = 0; i < this._regions.Length; ++i)
      if (string.Equals(this._regions[i].Path, path, StringComparison.Ordinal)) {
        moduleBase = this._regions[i].ModuleBase;
        return true;
      }

    moduleBase = 0;
    return false;
  }

  /// <summary>
  /// The mapping's name, with the suffix the kernel adds to an unlinked file taken off.
  /// </summary>
  /// <remarks>
  /// Kept for pseudo-mappings as well as for files, because <c>[stack]</c> is the answer to a stack
  /// question and dropping it would leave the one mapping this class exists to find unnamed.
  /// </remarks>
  private static string? Name(ReadOnlySpan<byte> pathBytes) {
    while (!pathBytes.IsEmpty && AsciiScanner.IsSpace(pathBytes[^1]))
      pathBytes = pathBytes[..^1];

    if (pathBytes.IsEmpty)
      return null;

    const string DeletedSuffix = " (deleted)";
    if (pathBytes.Length > DeletedSuffix.Length && pathBytes[^DeletedSuffix.Length..].SequenceEqual(" (deleted)"u8))
      pathBytes = pathBytes[..^DeletedSuffix.Length];

    return pathBytes.IsEmpty ? null : Encoding.UTF8.GetString(pathBytes);
  }

}
