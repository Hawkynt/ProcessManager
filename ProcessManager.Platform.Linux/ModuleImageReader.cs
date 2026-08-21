using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Fills in what <c>/proc/[pid]/maps</c> cannot say about a mapped image, by looking at the file it
/// names: its size, when it was last written, and what its ELF header declares (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// On demand only. This is the file-system half of the modules view, and it opens a file per distinct
/// image — two hundred of them for a desktop application. That is affordable once, for the one
/// process somebody selected, and nowhere near affordable per process per second (PRD §5.4).
/// </para>
/// <para>
/// Cached across processes, which is where most of the cost goes: every process on the machine maps
/// the same <c>libc</c>, and the second process to be inspected should not re-read its header. The
/// cache key includes the file's length and write time, so an upgrade in place is noticed rather than
/// served stale.
/// </para>
/// </remarks>
internal sealed class ModuleImageReader {

  private readonly record struct CacheEntry(long Length, long ModifiedUtcTicks, ElfImage.Description Description);

  private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

  /// <summary>
  /// Emptied wholesale rather than evicted one at a time once it is this big. A machine does not have
  /// four thousand distinct shared objects mapped at once, so reaching this means something is opening
  /// files with generated names and the useful contents are already gone.
  /// </summary>
  private const int _MaxCacheEntries = 4096;

  /// <summary>Adds the file's own account of itself to a row that came out of <c>maps</c>.</summary>
  /// <param name="description">
  /// What the file declared, for <see cref="ModuleGraph"/>: the load reason of a row is a statement
  /// about the <em>other</em> rows — which of them names it — so the descriptions have to outlive
  /// the per-row enrichment that produced them.
  /// </param>
  public ModuleRecord Describe(ModuleRecord module, out ElfImage.Description description) {
    description = ElfImage.Unread;
    var info = new FileInfo(module.Path);
    long length;
    long modified;
    try {
      if (!info.Exists)
        // A mapping outlives the file behind it: the library was upgraded, the temporary file was
        // unlinked, or it is a memfd that never had a name to begin with. There is nothing to stat,
        // and saying "not permitted" would send the reader to the elevated helper for nothing.
        return Missing(module, UnknownReason.SourceGone);

      length = info.Length;
      modified = info.LastWriteTimeUtc.Ticks;
    } catch (UnauthorizedAccessException) {
      return Missing(module, UnknownReason.NotPermitted);
    } catch (IOException) {
      return Missing(module, UnknownReason.SourceGone);
    }

    var enriched = module with {
      FileSizeBytes = Counter.Of(length),
      FileModifiedUtcTicks = modified,
    };

    // A device node is never an image, and opening one is the only thing here that could block: a
    // serial port waits for carrier detect, and the modules view would wait with it. Shared memory
    // under /dev/shm is an ordinary file and is not excluded.
    if (module.Path.StartsWith("/dev/", StringComparison.Ordinal) && !module.Path.StartsWith("/dev/shm/", StringComparison.Ordinal)) {
      // A device is not an image and was never opened, so its hardening flags are not "none" —
      // there are none to have, and the mitigation word stays at the value that means nothing was
      // read (PRD §72.3).
      description = ElfImage.Unread with { Type = ModuleType.Data, EntryPoint = Counter.NotSupported };
      return enriched with {
        Type = ModuleType.Data,
        EntryPoint = Counter.NotSupported,
      };
    }

    if (!this.TryDescribe(module.Path, length, modified, out description, out var failure)) {
      description = ElfImage.Unread;
      return enriched with {
        Type = ModuleType.Unknown,
        EntryPoint = Counter.Unknown(failure),
      };
    }

    return enriched with {
      Type = description.Type,
      Architecture = description.Architecture,
      Mitigations = description.Mitigations,
      BuildId = description.BuildId,
      // A shared object's header states its entry point relative to wherever it was loaded, so the
      // load bias has to be added back before the number means anything in this process. An
      // executable's is already absolute, and adding the base to it would produce an address in the
      // middle of nothing.
      EntryPoint = description.Type == ModuleType.SharedObject && description.EntryPoint.TryGetValue(out var entry)
        ? Counter.Of(module.BaseAddress + entry)
        : description.EntryPoint,
      Soname = description.Soname,
      Interpreter = description.Interpreter,
    };
  }

  /// <summary>
  /// Reads the image's header, or says why it could not.
  /// </summary>
  /// <param name="failure">
  /// Meaningful only when this returns false, and never <see cref="UnknownReason.None"/> then.
  /// </param>
  private bool TryDescribe(
    string path,
    long length,
    long modified,
    out ElfImage.Description description,
    out UnknownReason failure
  ) {
    failure = UnknownReason.None;
    if (this._cache.TryGetValue(path, out var cached) && cached.Length == length && cached.ModifiedUtcTicks == modified) {
      description = cached.Description;
      return true;
    }

    description = ElfImage.Unread;
    try {
      using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      if (!ElfImage.TryDescribe((offset, buffer) => ReadFully(handle, buffer, offset), out description)) {
        // Opened, and shorter than an ELF header. Nothing is wrong with the file; there is simply no
        // header in it.
        failure = UnknownReason.NotSupportedOnPlatform;
        return false;
      }
    } catch (UnauthorizedAccessException) {
      failure = UnknownReason.NotPermitted;
      return false;
    } catch (NotSupportedException) {
      // Not everything a process maps is a file that can be read at an offset. A graphics card's
      // character device is mapped by every process with a window on screen, and asking it to pread
      // its first sixty-four bytes throws rather than failing — which took the whole window down the
      // moment somebody selected such a process (PRD §88).
      failure = UnknownReason.NotSupportedOnPlatform;
      return false;
    } catch (IOException) {
      failure = UnknownReason.SourceGone;
      return false;
    }

    if (this._cache.Count >= _MaxCacheEntries)
      this._cache.Clear();

    this._cache[path] = new(length, modified, description);
    return true;
  }

  /// <summary>
  /// A <c>pread</c> that keeps going until the buffer is full or the file ends.
  /// </summary>
  /// <remarks>
  /// <c>RandomAccess.Read</c> is allowed to return less than was asked for without having reached the
  /// end, and the ELF reader treats a short read as "this range is not there" — so a single call would
  /// occasionally lose a program header table for no reason anybody could reproduce.
  /// </remarks>
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

  /// <summary>Every field that would have come from the file, carrying the reason it did not.</summary>
  private static ModuleRecord Missing(ModuleRecord module, UnknownReason reason) => module with {
    FileSizeBytes = Counter.Unknown(reason),
    FileModifiedUtcTicks = 0,
    Type = ModuleType.Unknown,
    Architecture = null,
    EntryPoint = Counter.Unknown(reason),
    Soname = null,
    Interpreter = null,
    Mitigations = ImageMitigations.None,
    BuildId = null,
  };

}
