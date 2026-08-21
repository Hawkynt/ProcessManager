namespace Hawkynt.ProcessManager.Model;

/// <summary>Why a process's address space cannot be listed (PRD §34).</summary>
public enum MemoryMapState : byte {

  /// <summary>The mappings came back.</summary>
  Available,

  /// <summary>
  /// The kernel refused.
  /// </summary>
  /// <remarks>
  /// Which is the ordinary answer for anybody else's process, and surprising enough to be worth its
  /// own state: <c>/proc/[pid]/maps</c> is mode 0444 and reading it still fails with <c>EPERM</c>,
  /// because the permission the kernel actually checks is <c>PTRACE_MODE_READ</c> and not the mode
  /// bits it shows. A reader who looked at the mode and concluded the program was at fault would be
  /// looking in the wrong place.
  /// </remarks>
  NotPermitted,

  /// <summary>
  /// There is no such process any more.
  /// </summary>
  /// <remarks>
  /// Not the same as a process with nothing mapped. A kernel thread has no address space of its own
  /// and its <c>maps</c> is an empty file that reads perfectly well, so that arrives as
  /// <see cref="Available"/> with no regions — which is a fact about the process, where this is a
  /// fact about the read.
  /// </remarks>
  Gone,

  /// <summary>This platform's memory map is not read here yet.</summary>
  NotImplemented,

}

/// <summary>
/// One process's address space, and whether the kernel was willing to describe it (PRD §34).
/// </summary>
/// <remarks>
/// A list on its own cannot say why it is empty, and every one of the reasons above produces an empty
/// one. A kernel thread genuinely has no mappings; another user's process has plenty and will not
/// show them; and the two must not look alike (PRD §5.3, §72.3).
/// </remarks>
/// <param name="Detailed">
/// Whether the per-mapping counters were read as well as the addresses — that is, whether
/// <c>smaps</c> was readable or only <c>maps</c>. The counters carry their own reason either way;
/// this is what lets a page say the reason once at the top instead of ten thousand times.
/// </param>
public sealed record MemoryMapReading(
  MemoryMapState State,
  bool Detailed,
  IReadOnlyList<MemoryRegionRecord> Regions
) {

  public static readonly MemoryMapReading NotImplemented = new(MemoryMapState.NotImplemented, false, []);

  /// <summary>One sentence a page can put where the list would be.</summary>
  public string Explain() => this.State switch {
    MemoryMapState.Available => string.Empty,
    MemoryMapState.NotPermitted =>
      "The kernel will not show this process's address space. Reading it needs the same permission as "
      + "attaching a debugger, which is why another user's process is refused however readable the "
      + "file looks.",
    MemoryMapState.Gone => "The process has ended.",
    _ => "Reading a process's memory map is not implemented on this platform yet.",
  };

}
