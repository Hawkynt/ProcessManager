using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>The process quantity used to explain one system-history point.</summary>
public enum SpikeMetric : byte {
  Cpu,
  Io,
  MemoryGrowth,
}

/// <summary>One process contributing to one sampled system spike.</summary>
/// <remarks>
/// The stable <see cref="ProcessKey"/> and the display identity are both retained. The key prevents a
/// PID reused ten minutes later from being mistaken for the process that caused the old spike; the
/// name and owner make the old record useful after the original process has exited and can no longer
/// be queried (PRD §45, §73).
/// </remarks>
public readonly record struct SpikeContributor(
  ProcessKey Key,
  string Name,
  string? UserName,
  Rate Value
);

/// <summary>
/// Bounded, allocation-free-at-steady-state history of the processes that caused sampled CPU, I/O
/// and memory-growth spikes.
/// </summary>
/// <remarks>
/// <para>
/// This follows the useful behaviour of System Informer's system-history attribution without copying
/// its implementation: keep process identity beside the historical point rather than trying to find
/// an old PID in the current process list later. We retain the top few contributors, not only the
/// maximum, because a saturated machine is commonly five build workers rather than one villain.
/// </para>
/// <para>
/// Memory means positive private-byte growth during the interval, not "largest process". A system
/// memory graph that jumped at 10:03 is asking who allocated then; a browser that has been large for
/// six hours did not cause that jump merely by still being large.
/// </para>
/// <para>
/// Storage is flat and preallocated. A process monitor's sampling path must not allocate one array per
/// sample merely so a window somebody may never open can explain it later (PRD §4, §5.4).
/// </para>
/// </remarks>
public sealed class SpikeAttributionHistory {

  private const int _MetricCount = 3;
  public const int DefaultContributors = 5;

  private readonly SpikeContributor[] _entries;
  private readonly byte[] _counts;
  private readonly long[] _utcTicks;
  private readonly int _contributors;
  private int _next;
  private int _count;

  public SpikeAttributionHistory(int capacity, int contributors = DefaultContributors) {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(contributors, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(contributors, byte.MaxValue);

    this.Capacity = capacity;
    this._contributors = contributors;
    this._entries = new SpikeContributor[checked(capacity * _MetricCount * contributors)];
    this._counts = new byte[checked(capacity * _MetricCount)];
    this._utcTicks = new long[capacity];
  }

  public int Capacity { get; }

  public int Count => this._count;

  public int ContributorCapacity => this._contributors;

  /// <summary>
  /// Records the current interval. <paramref name="utcTicks"/> is supplied by the sampler so every
  /// front-end sees one timestamp rather than each stamping the same sample when it happens to draw.
  /// </summary>
  public void Add(SystemSnapshot snapshot, SnapshotDelta delta, long utcTicks) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    var slot = this._next;
    this._utcTicks[slot] = utcTicks;
    for (var metric = 0; metric < _MetricCount; ++metric) {
      this._counts[(slot * _MetricCount) + metric] = 0;
      this.Entries(slot, (SpikeMetric)metric).Clear();
    }

    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      // Retained tombstones are useful as historical identities, but they have no activity in the
      // interval after they exited and must never re-enter a top-contributor list as stale values.
      if (process.ExitedUtcTicks != 0)
        continue;

      this.Consider(slot, SpikeMetric.Cpu, in process, delta.CpuPercent(i), positiveOnly: true);
      this.Consider(slot, SpikeMetric.Io, in process, delta.IoTotalBytesPerSecond(i), positiveOnly: true);
      this.Consider(slot, SpikeMetric.MemoryGrowth, in process, delta.PrivateBytesDelta(i), positiveOnly: true);
    }

    this._next = (slot + 1) % this.Capacity;
    if (this._count < this.Capacity)
      ++this._count;
  }

  /// <summary>
  /// Contributors for a sample age, newest at zero, ordered largest first. The returned span remains
  /// valid until the history is written again; callers that keep it must copy it themselves.
  /// </summary>
  public ReadOnlySpan<SpikeContributor> AtAge(SpikeMetric metric, int age) {
    if ((uint)metric >= _MetricCount || (uint)age >= (uint)this._count)
      return [];

    var slot = this.SlotAtAge(age);
    var count = this._counts[(slot * _MetricCount) + (int)metric];
    return this.Entries(slot, metric)[..count];
  }

  /// <summary>UTC timestamp of a retained sample, or zero when that age has fallen out of the ring.</summary>
  public long UtcTicksAtAge(int age)
    => (uint)age < (uint)this._count ? this._utcTicks[this.SlotAtAge(age)] : 0;

  private int SlotAtAge(int age) {
    var newest = (this._next - 1 + this.Capacity) % this.Capacity;
    return (newest - age + this.Capacity) % this.Capacity;
  }

  private Span<SpikeContributor> Entries(int slot, SpikeMetric metric) {
    var offset = ((slot * _MetricCount) + (int)metric) * this._contributors;
    return this._entries.AsSpan(offset, this._contributors);
  }

  private void Consider(
    int slot,
    SpikeMetric metric,
    in ProcessRecord process,
    Rate value,
    bool positiveOnly
  ) {
    if (!value.HasValue || positiveOnly && value.Value <= 0)
      return;

    var countIndex = (slot * _MetricCount) + (int)metric;
    var count = this._counts[countIndex];
    var entries = this.Entries(slot, metric);

    // Insertion into five elements is cheaper and simpler than allocating/sorting a list containing
    // every process. Ties remain in process-table order so repeated identical readings do not jitter.
    var insert = count;
    for (var i = 0; i < count; ++i)
      if (value.Value > entries[i].Value.Value) {
        insert = i;
        break;
      }

    if (insert >= this._contributors)
      return;

    var newCount = Math.Min(count + 1, this._contributors);
    for (var i = newCount - 1; i > insert; --i)
      entries[i] = entries[i - 1];

    entries[insert] = new(process.Key, process.Name, process.UserName, value);
    this._counts[countIndex] = (byte)newCount;
  }

}
