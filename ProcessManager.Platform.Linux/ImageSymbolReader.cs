using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Turns an address in a running process into a module and, when the image still carries the names,
/// a function inside it (PRD §29, §30).
/// </summary>
/// <remarks>
/// <para>
/// Two questions in one place because the second cannot be asked without the first. A symbol table is
/// written in the image's own address space, so an address out of a process has to have its load bias
/// taken off before the table can be searched — and the bias is the difference between where
/// <c>maps</c> says the image is and where the image says it is.
/// </para>
/// <para>
/// The image's header is cached across lookups: a stack of thirty frames is usually five distinct
/// libraries, and re-reading libc's header once per frame would be most of the cost of the answer.
/// Symbols themselves are not cached — a resolved stack is a few dozen names, and keeping a symbol
/// table of a hundred thousand entries alive for them would be a memory leak with a name.
/// </para>
/// </remarks>
internal sealed class ImageSymbolReader {

  private readonly record struct CacheEntry(long Length, long ModifiedUtcTicks, ModuleType Type);

  private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

  /// <summary>As in the module reader: emptied wholesale rather than evicted one at a time.</summary>
  private const int _MaxCacheEntries = 4096;

  /// <summary>Where an address is, in as much detail as the files on disk allow.</summary>
  /// <param name="Module">The image, or null when the address is not in a mapped file.</param>
  /// <param name="Symbol">The function, or null when the image no longer names it.</param>
  /// <param name="Displacement">
  /// How far into <paramref name="Symbol"/>, or the reason there is no symbol to be inside of. Not
  /// zero: a displacement of zero says the address is the function's first instruction.
  /// </param>
  public readonly record struct Location(string? Module, string? Symbol, Counter Displacement) {

    public static readonly Location Nowhere = new(null, null, Counter.NotSupported);

  }

  /// <summary>
  /// Describes one address.
  /// </summary>
  /// <param name="map">The process's mappings, from <c>maps</c>.</param>
  /// <param name="address">The address, as it is in the running process.</param>
  /// <param name="resolveSymbols">
  /// Whether to open the image and search its symbol tables. False gives the module and nothing else,
  /// which costs no file access at all — §30's module-and-offset fallback, and the answer the UI
  /// thread is allowed to ask for.
  /// </param>
  public Location Describe(AddressMap map, ulong address, bool resolveSymbols) {
    ArgumentNullException.ThrowIfNull(map);
    if (!map.TryFind(address, out var region) || region.Path is not { Length: > 0 } path)
      return Location.Nowhere;

    if (!region.IsFile)
      // "[stack]", "[vdso]", "[heap]": a real answer about where the address is, and not a file that
      // could be opened. The vDSO does carry a symbol table, and it is not on disk to be read.
      return new(path, null, Counter.NotSupported);

    if (!resolveSymbols)
      return new(path, null, Counter.NotSampledYet);

    if (!this.TryGetType(path, out var type))
      return new(path, null, Counter.Unknown(UnknownReason.SourceGone));

    // A position-independent image states every address relative to its own zero, so where the loader
    // put it has to come back off. A fixed-address executable states them absolutely, and subtracting
    // its base would search the symbol table for an address below every symbol in it.
    var fileAddress = type == ModuleType.SharedObject ? address - region.ModuleBase : address;
    try {
      using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      return ElfSymbols.TryResolve((offset, buffer) => ReadFully(handle, buffer, offset), fileAddress, out var match)
        ? new(path, match.Name, Counter.Of(match.Displacement))
        : new(path, null, Counter.NotSupported);
    } catch (UnauthorizedAccessException) {
      return new(path, null, Counter.NotPermitted);
    } catch (NotSupportedException) {
      return new(path, null, Counter.NotSupported);
    } catch (IOException) {
      return new(path, null, Counter.Unknown(UnknownReason.SourceGone));
    }
  }

  /// <summary>What the image declares itself to be, which is all that is needed to bias an address.</summary>
  private bool TryGetType(string path, out ModuleType type) {
    type = ModuleType.Unknown;
    long length;
    long modified;
    try {
      var info = new FileInfo(path);
      if (!info.Exists)
        return false;

      length = info.Length;
      modified = info.LastWriteTimeUtc.Ticks;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }

    if (this._cache.TryGetValue(path, out var cached) && cached.Length == length && cached.ModifiedUtcTicks == modified) {
      type = cached.Type;
      return true;
    }

    try {
      using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      if (!ElfImage.TryDescribe((offset, buffer) => ReadFully(handle, buffer, offset), out var description))
        return false;

      type = description.Type;
    } catch (UnauthorizedAccessException) {
      return false;
    } catch (NotSupportedException) {
      return false;
    } catch (IOException) {
      return false;
    }

    if (this._cache.Count >= _MaxCacheEntries)
      this._cache.Clear();

    this._cache[path] = new(length, modified, type);
    return true;
  }

  /// <summary>A <c>pread</c> that keeps going until the buffer is full or the file ends.</summary>
  private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long offset) {
    var total = 0;
    while (total < buffer.Length) {
      var read = RandomAccess.Read(handle, buffer[total..], offset + total);
      if (read <= 0)
        break;

      total += read;
    }

    return total;
  }

}
