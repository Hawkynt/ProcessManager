using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>One band of the composition bar.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Bytes">How much of physical memory it accounts for.</param>
/// <param name="Explanation">What it means, for the tooltip — most people meet these words here.</param>
public readonly record struct MemoryBand(string Label, ulong Bytes, string Explanation);

/// <summary>
/// How physical memory divides up, as the four bands of the composition bar (PRD §14, §47).
/// </summary>
/// <remarks>
/// <para>
/// The one picture that explains why a machine reporting almost no free memory is perfectly healthy,
/// and the reason people open a memory page at all. A number cannot say it: 4.5 GB free out of
/// 125 GB reads as an emergency until you can see that 68 GB of the rest is cache the kernel will
/// hand back the moment anything asks.
/// </para>
/// <para>
/// <b>The bands are a partition, and they sum to the total exactly.</b> That is what makes it a bar
/// rather than four numbers, and it is the constraint every definition here is bent to fit:
/// </para>
/// <list type="bullet">
/// <item><b>In use</b> is total less available — the same figure the rest of the page shows, so the
/// bar and the statistics beside it cannot disagree.</item>
/// <item><b>Modified</b> is dirty and in-writeback pages: cache whose contents differ from the disk,
/// so it cannot simply be dropped. Carved out of the cache rather than added beside it, which is
/// what it is on both operating systems.</item>
/// <item><b>Free</b> is physically unallocated, which on a healthy machine is nearly nothing.</item>
/// <item><b>Cached</b> is the remainder — reclaimable cache. Deliberately the remainder and not
/// <c>Cached + Buffers + SReclaimable</c>: that sum counts pages the kernel does not consider
/// reclaimable (shared memory, mostly), so using it would make the four bands overrun the total by
/// a few percent and the bar would lie about its own scale.</item>
/// </list>
/// </remarks>
public readonly record struct MemoryComposition(ulong TotalBytes, IReadOnlyList<MemoryBand> Bands) {

  /// <summary>Whether the machine reported enough to draw one.</summary>
  public bool HasValue => this.TotalBytes > 0 && this.Bands.Count > 0;

  /// <summary>
  /// Divides physical memory up, or an empty composition when the machine will not say how much it
  /// has.
  /// </summary>
  public static MemoryComposition Of(in SystemCounters system) {
    if (!system.TotalMemoryBytes.HasValue || system.TotalMemoryBytes.Value == 0)
      return default;

    var total = system.TotalMemoryBytes.Value;

    // Clamped at every step, because these come from separate lines of a file that is read without
    // a lock: a machine that allocated a gigabyte between two of those lines can produce a set that
    // does not add up, and a band of negative width is not a thing (PRD §5.3).
    var inUse = system.AvailableMemoryBytes.HasValue
      ? total - Math.Min(system.AvailableMemoryBytes.Value, total)
      : 0;

    var free = Math.Min(system.FreeMemoryBytes.GetValueOrDefault(), total - inUse);
    var modified = Math.Min(system.ModifiedMemoryBytes.GetValueOrDefault(), total - inUse - free);
    var cached = total - inUse - free - modified;

    return new(total, [
      new("In use", inUse, "Memory processes and the kernel are actually using."),
      new("Modified", modified, "Cache that has been changed and not yet written to disk, so it cannot just be dropped."),
      new("Cached", cached, "File data kept in memory in case it is needed again. Reclaimed the moment anything asks."),
      new("Free", free, "Not allocated to anything. A healthy machine keeps very little here — the rest is cache."),
    ]);
  }

}
