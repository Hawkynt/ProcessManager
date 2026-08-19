using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The composition bar (PRD §14, §47).
/// </summary>
/// <remarks>
/// The one picture that explains why a machine reporting almost no free memory is healthy. Its whole
/// value rests on being a true partition — four bands that sum to the total exactly — so that is
/// what most of these check.
/// </remarks>
[TestFixture]
public sealed class MemoryCompositionTests {

  private const ulong _Gigabyte = 1024ul * 1024 * 1024;

  private static SystemCounters Machine(
    ulong total = 16,
    ulong available = 10,
    ulong free = 1,
    ulong modified = 0
  ) => new() {
    TotalMemoryBytes = Counter.Of(total * _Gigabyte),
    AvailableMemoryBytes = Counter.Of(available * _Gigabyte),
    FreeMemoryBytes = Counter.Of(free * _Gigabyte),
    ModifiedMemoryBytes = Counter.Of(modified * _Gigabyte),
  };

  private static ulong Band(MemoryComposition composition, string label) {
    foreach (var band in composition.Bands)
      if (band.Label == label)
        return band.Bytes;

    Assert.Fail($"no band called {label}");
    return 0;
  }

  private static ulong SumOf(MemoryComposition composition) {
    var total = 0ul;
    foreach (var band in composition.Bands)
      total += band.Bytes;

    return total;
  }

  [Test]
  public void TheBandsSumToTheTotalExactly() {
    var composition = MemoryComposition.Of(Machine());

    Assert.That(SumOf(composition), Is.EqualTo(composition.TotalBytes));
  }

  /// <summary>
  /// The reason it is drawn at all: one gigabyte free of sixteen looks like an emergency, until the
  /// bar shows that five of the rest is cache the kernel hands back on demand.
  /// </summary>
  [Test]
  public void CacheIsTheRemainderAndIsWhereTheMissingMemoryWent() {
    var composition = MemoryComposition.Of(Machine(total: 16, available: 10, free: 1));

    Assert.That(Band(composition, "In use"), Is.EqualTo(6 * _Gigabyte), "total less available");
    Assert.That(Band(composition, "Free"), Is.EqualTo(1 * _Gigabyte));
    Assert.That(Band(composition, "Cached"), Is.EqualTo(9 * _Gigabyte));
  }

  /// <summary>
  /// "In use" is total less available, which is what every other figure on the page shows. A second
  /// definition here would put two different numbers for one thing on one page.
  /// </summary>
  [Test]
  public void InUseIsTheSameFigureTheRestOfThePageShows() {
    var composition = MemoryComposition.Of(Machine(total: 32, available: 20));

    Assert.That(Band(composition, "In use"), Is.EqualTo(12 * _Gigabyte));
  }

  /// <summary>Modified is carved out of the cache, not added beside it — it is cache that is dirty.</summary>
  [Test]
  public void ModifiedComesOutOfTheCacheRatherThanBesideIt() {
    var clean = MemoryComposition.Of(Machine(total: 16, available: 10, free: 1, modified: 0));
    var dirty = MemoryComposition.Of(Machine(total: 16, available: 10, free: 1, modified: 2));

    Assert.That(Band(dirty, "Modified"), Is.EqualTo(2 * _Gigabyte));
    Assert.That(Band(dirty, "Cached"), Is.EqualTo(Band(clean, "Cached") - (2 * _Gigabyte)));
    Assert.That(Band(dirty, "In use"), Is.EqualTo(Band(clean, "In use")), "and not out of what is in use");
    Assert.That(SumOf(dirty), Is.EqualTo(dirty.TotalBytes));
  }

  #region the file is read without a lock (PRD §5.3)

  /// <summary>
  /// meminfo's lines are read one after another and the machine keeps allocating between them, so a
  /// set that does not add up is ordinary rather than impossible. A band of negative width is not.
  /// </summary>
  [Test]
  public void FiguresThatContradictEachOtherStillProduceARealBar() {
    var contradictory = new SystemCounters {
      TotalMemoryBytes = Counter.Of(8 * _Gigabyte),
      AvailableMemoryBytes = Counter.Of(6 * _Gigabyte),
      // More free than there is memory left over, which cannot be true and is what the file said.
      FreeMemoryBytes = Counter.Of(7 * _Gigabyte),
      ModifiedMemoryBytes = Counter.Of(4 * _Gigabyte),
    };

    var composition = MemoryComposition.Of(contradictory);

    Assert.That(SumOf(composition), Is.EqualTo(composition.TotalBytes));
    foreach (var band in composition.Bands)
      Assert.That(band.Bytes, Is.LessThanOrEqualTo(composition.TotalBytes), band.Label);
  }

  [Test]
  public void AvailableExceedingTotalDoesNotProduceANegativeBand() {
    var composition = MemoryComposition.Of(Machine(total: 8, available: 12, free: 2));

    Assert.That(Band(composition, "In use"), Is.Zero);
    Assert.That(SumOf(composition), Is.EqualTo(composition.TotalBytes));
  }

  #endregion

  #region machines that will not say

  [Test]
  public void AMachineThatDoesNotReportItsMemoryGetsNoBar() {
    Assert.That(MemoryComposition.Of(new()).HasValue, Is.False);
    Assert.That(MemoryComposition.Of(new() { TotalMemoryBytes = Counter.Of(0) }).HasValue, Is.False);
  }

  /// <summary>
  /// Windows reports none of the three supporting figures yet. Rather than no bar at all, everything
  /// unaccounted for lands in cache — which is where the memory of a machine that will not break it
  /// down honestly is: somewhere other than in use.
  /// </summary>
  [Test]
  public void AMachineThatReportsOnlyTotalAndAvailableStillGetsABar() {
    var composition = MemoryComposition.Of(new() {
      TotalMemoryBytes = Counter.Of(16 * _Gigabyte),
      AvailableMemoryBytes = Counter.Of(10 * _Gigabyte),
    });

    Assert.That(composition.HasValue, Is.True);
    Assert.That(Band(composition, "In use"), Is.EqualTo(6 * _Gigabyte));
    Assert.That(Band(composition, "Free"), Is.Zero);
    Assert.That(Band(composition, "Cached"), Is.EqualTo(10 * _Gigabyte));
    Assert.That(SumOf(composition), Is.EqualTo(composition.TotalBytes));
  }

  #endregion

  /// <summary>
  /// Most people meet the words "modified" and "cached" here, so every band explains itself — the
  /// bar is only useful if its bands mean something.
  /// </summary>
  [Test]
  public void EveryBandExplainsWhatItIs() {
    foreach (var band in MemoryComposition.Of(Machine()).Bands) {
      Assert.That(band.Label, Is.Not.Empty);
      Assert.That(band.Explanation, Is.Not.Empty, band.Label);
    }
  }

}
