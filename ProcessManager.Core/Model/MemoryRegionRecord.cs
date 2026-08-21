namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What a mapping is, as far as its name and its backing file will say (PRD §34).
/// </summary>
/// <remarks>
/// Derived from the name <c>maps</c> writes and from nothing else. That is a deliberate ceiling: the
/// kernel classifies a mapping by what created it, and the only part of that classification it
/// publishes is the bracketed pseudo-name and the path. Anything finer — which library a file-backed
/// mapping is a segment of, whether an anonymous region is a malloc arena or a thread stack — is a
/// guess dressed as a reading, and §5.3 forbids those.
/// </remarks>
public enum MemoryRegionKind : byte {

  /// <summary>Nobody classified it. Not a kind of region — a hole where the classification goes.</summary>
  Unknown = 0,

  /// <summary>A file, mapped. Which file is in the path; whether it is an image is §31's question.</summary>
  FileBacked,

  /// <summary>No file behind it: a malloc arena, a thread stack, a mapping somebody made by hand.</summary>
  Anonymous,

  /// <summary>The <c>brk</c> heap — <c>[heap]</c>, and only the one the kernel grows.</summary>
  Heap,

  /// <summary>
  /// The initial thread's stack — <c>[stack]</c>.
  /// </summary>
  /// <remarks>
  /// Only that one. Linux stopped labelling other threads' stacks in 4.5, because working out which
  /// anonymous region a given thread's stack pointer was in cost a walk of every thread per line of
  /// the file. So a threaded process shows one stack here and its other stacks appear as ordinary
  /// anonymous regions — which is what the kernel says, and saying more would be inventing it.
  /// </remarks>
  Stack,

  /// <summary>Memory with a name and no file: <c>/dev/shm</c>, a <c>memfd</c>, a System V segment.</summary>
  SharedMemory,

  /// <summary>A device, mapped — a graphics card's aperture, <c>/dev/dri/renderD128</c>, DMA buffers.</summary>
  Device,

  /// <summary>
  /// Something the kernel put there: <c>[vdso]</c>, <c>[vvar]</c>, <c>[vsyscall]</c>.
  /// </summary>
  /// <remarks>
  /// Present in every process and belonging to none of them. Worth a kind of its own because they are
  /// the regions whose size a reader must not add to the process's own footprint.
  /// </remarks>
  KernelProvided,

  /// <summary>A bracketed name this build does not recognise. The kernel adds them; this is where a new one lands.</summary>
  Pseudo,

}

/// <summary>
/// One mapping of one process's address space (PRD §34).
/// </summary>
/// <remarks>
/// <para>
/// One row per line of <c>maps</c>, and deliberately not folded. §31's module list folds a library's
/// five consecutive mappings into one row because it is answering "which code is loaded"; this is
/// answering "what is at this address and why may it be written to", and folding a read-only segment
/// together with a writable one would destroy the only fact the row exists for.
/// </para>
/// <para>
/// Everything below <see cref="Device"/> comes from <c>smaps</c>, which makes the kernel walk the
/// process's page tables. A caller that read the cheap file, or was refused the expensive one, gets
/// counters carrying that reason rather than zeroes (PRD §3.4, §5.4).
/// </para>
/// </remarks>
/// <param name="Permissions">
/// What the mapping may be done with, as the four characters of the line spell it. Never
/// <see cref="MapPermissions.None"/> for a real mapping: a region with no access at all is
/// <c>---p</c> and still carries <see cref="MapPermissions.Private"/>.
/// </param>
/// <param name="Path">
/// The backing file, the bracketed pseudo-name, or null for an anonymous mapping. Kept verbatim,
/// brackets and all, so that a name this build does not know still reaches the reader.
/// </param>
/// <param name="IsDeleted">
/// Whether the kernel appended <c>(deleted)</c> — the file is gone and the mapping outlives it, which
/// is the ordinary state of a running program whose package was upgraded underneath it.
/// </param>
/// <param name="ProportionalBytes">
/// This mapping's share of what it has resident, with every page divided by the number of processes
/// mapping it. The only resident figure that may be summed across processes without counting shared
/// pages several times.
/// </param>
/// <param name="PrivateDirtyBytes">
/// Resident, written to, and belonging to nobody else — the bytes that must go to swap rather than
/// simply being dropped. The figure that answers "what would freeing this actually cost".
/// </param>
/// <param name="AnonymousBytes">
/// How much of the mapping no longer matches its file. Nought for a clean file mapping, the whole of
/// it for an anonymous one, and something in between for a data segment that has been relocated.
/// </param>
/// <param name="HugePageBytes">
/// Backed by transparent huge pages. <c>AnonHugePages</c>, which is anonymous memory only —
/// hugetlbfs mappings are a different mechanism and are visible in <paramref name="Flags"/> as
/// <c>ht</c> instead.
/// </param>
/// <param name="Flags">
/// The kernel's own <c>VmFlags</c> line, verbatim and unabbreviated further. Two-letter codes rather
/// than a translation on purpose: it is where the answers §34 asks for that have no counter of their
/// own actually live — <c>gd</c> for a guard region that grows down, <c>ht</c> for huge pages,
/// <c>nr</c> for memory with no swap reserved, <c>dd</c> for a region excluded from core dumps — and
/// inventing English for a set the kernel extends every few releases would go stale silently
/// (PRD §5.3).
/// </param>
public readonly record struct MemoryRegionRecord(
  ulong Start,
  ulong End,
  ulong Size,
  MapPermissions Permissions,
  MemoryRegionKind Kind,
  string? Path,
  bool IsDeleted,
  Counter FileOffset,
  Counter Inode,
  string? Device,
  Counter ResidentBytes,
  Counter ProportionalBytes,
  Counter PrivateDirtyBytes,
  Counter SharedDirtyBytes,
  Counter AnonymousBytes,
  Counter SwapBytes,
  Counter LockedBytes,
  Counter HugePageBytes,
  string? Flags
);
