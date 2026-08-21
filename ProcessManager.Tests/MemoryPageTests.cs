using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The memory page (PRD §47), from a recorded <c>/proc</c> so it is checked on every CI leg.
/// </summary>
/// <remarks>
/// The fixture is a modern <c>meminfo</c> with two lines deliberately missing — <c>Zswap</c> and
/// <c>Zswapped</c>, which a kernel built without compression does not publish. Every figure here is
/// therefore checked twice over: that the ones present are read, and that the ones absent say so
/// rather than reading as a machine that has the feature and is not using it.
/// </remarks>
[TestFixture]
public sealed class MemoryPageTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static SystemSnapshot Sample() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    return snapshot;
  }

  private static PerformanceSection Memory() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    foreach (var section in PerformanceReport.Build(probe.DescribeHost(), snapshot))
      if (section.Title == "Memory")
        return section;

    Assert.Fail("no memory section");
    return default;
  }

  private static string ValueOf(PerformanceSection section, string label) {
    foreach (var row in section.Rows)
      if (row.Label == label)
        return row.Value;

    Assert.Fail($"no row '{label}'");
    return string.Empty;
  }

  private static PerformanceRowLevel LevelOf(PerformanceSection section, string label) {
    foreach (var row in section.Rows)
      if (row.Label == label)
        return row.Level;

    Assert.Fail($"no row '{label}'");
    return default;
  }

  #region what meminfo actually says (PRD §47)

  /// <summary>
  /// Every line the fixture carries, read as itself.
  /// </summary>
  /// <remarks>
  /// One test rather than twenty, because what is being checked is the same mistake twenty times:
  /// a key matched against the wrong line. <c>SwapCached</c> against <c>Cached</c> and
  /// <c>Active(anon)</c> against <c>Active</c> are the two that a prefix match gets wrong, and both
  /// are in here.
  /// </remarks>
  [Test]
  public void EveryFigureTheKernelPublishesIsRead() {
    var system = Sample().System;

    Assert.Multiple(() => {
      Assert.That(system.TotalMemoryBytes.Value, Is.EqualTo(16_384_000ul * 1024), "MemTotal");
      Assert.That(system.FreeMemoryBytes.Value, Is.EqualTo(2_048_000ul * 1024), "MemFree");
      Assert.That(system.AvailableMemoryBytes.Value, Is.EqualTo(8_192_000ul * 1024), "MemAvailable");
      Assert.That(system.CachedMemoryBytes.Value, Is.EqualTo(4_096_000ul * 1024), "Cached, not SwapCached");
      Assert.That(system.SwapCachedBytes.Value, Is.Zero, "SwapCached, which is genuinely nought here");
      Assert.That(system.AnonymousBytes.Value, Is.EqualTo(3_500_000ul * 1024), "AnonPages");
      Assert.That(system.MappedBytes.Value, Is.EqualTo(1_200_000ul * 1024), "Mapped");
      Assert.That(system.DirtyBytes.Value, Is.EqualTo(12_000ul * 1024), "Dirty");
      Assert.That(system.WritebackBytes.Value, Is.EqualTo(4_000ul * 1024), "Writeback");
      Assert.That(system.ModifiedMemoryBytes.Value, Is.EqualTo(16_000ul * 1024), "dirty plus writeback");
      Assert.That(system.ActiveAnonymousBytes.Value, Is.EqualTo(3_000_000ul * 1024), "Active(anon), not Active");
      Assert.That(system.InactiveFileBytes.Value, Is.EqualTo(2_600_000ul * 1024), "Inactive(file)");
      Assert.That(system.SlabBytes.Value, Is.EqualTo(450_000ul * 1024), "Slab");
      Assert.That(system.ReclaimableKernelBytes.Value, Is.EqualTo(300_000ul * 1024), "SReclaimable");
      Assert.That(system.UnreclaimableKernelBytes.Value, Is.EqualTo(150_000ul * 1024), "SUnreclaim");
      Assert.That(system.UnevictableBytes.Value, Is.EqualTo(64_000ul * 1024), "Unevictable");
      Assert.That(system.LockedBytes.Value, Is.EqualTo(8_000ul * 1024), "Mlocked");
      Assert.That(system.VmallocUsedBytes.Value, Is.EqualTo(80_000ul * 1024), "VmallocUsed, not VmallocTotal");
      Assert.That(system.PerCpuBytes.Value, Is.EqualTo(4_000ul * 1024), "Percpu");
      Assert.That(system.HugePageSizeBytes.Value, Is.EqualTo(2_048ul * 1024), "Hugepagesize");
      Assert.That(system.HugePagesTotal.Value, Is.EqualTo(64ul), "a count of pages, not a size");
      Assert.That(system.HugePagesFree.Value, Is.EqualTo(48ul));
      Assert.That(system.HugeTlbBytes.Value, Is.EqualTo(131_072ul * 1024), "Hugetlb");
      Assert.That(system.AnonymousHugePagesBytes.Value, Is.EqualTo(204_800ul * 1024), "AnonHugePages");
      Assert.That(system.HardwareCorruptedBytes.Value, Is.Zero, "and this one had better be nought");
    });
  }

  /// <summary>
  /// A line this kernel does not publish is refused, not answered with a zero.
  /// </summary>
  /// <remarks>
  /// The whole bug class in one assertion. <c>default(Counter)</c> is a confident zero, so a counter
  /// nobody filled in reports a machine that has zswap configured and is not using it — which is a
  /// different machine from one built without it, and the difference matters to anybody wondering
  /// where their swap went (PRD §5.3, §72.3).
  /// </remarks>
  [Test]
  public void ALineThisKernelDoesNotPublishIsRefusedRatherThanZero() {
    var system = Sample().System;

    Assert.That(system.CompressedBytes.HasValue, Is.False, "the fixture has no Zswap line");
    Assert.That(system.CompressedBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(system.CompressedOriginalBytes.HasValue, Is.False);
  }

  /// <summary>
  /// A snapshot nobody has sampled says so about every counter it holds.
  /// </summary>
  /// <remarks>
  /// The same trap one level up: a probe that fills in twelve fields of a zeroed struct leaves the
  /// rest reading as measured zeros. Every front-end draws those.
  /// </remarks>
  [Test]
  public void AnUnsampledSnapshotClaimsNothing() {
    var system = SystemCounters.Unread;

    Assert.Multiple(() => {
      Assert.That(system.TotalMemoryBytes.HasValue, Is.False);
      Assert.That(system.CommittedBytes.HasValue, Is.False);
      Assert.That(system.HugePagesTotal.HasValue, Is.False);
      Assert.That(system.MemoryPressure.HasValue, Is.False, "a machine that cannot say is not a calm one");
      Assert.That(system.ContextSwitches.HasValue, Is.False);
    });
  }

  #endregion

  #region what the page shows (PRD §47)

  /// <summary>
  /// Free is not available, and the page says both.
  /// </summary>
  /// <remarks>
  /// The most misread number in memory. A healthy machine keeps almost nothing free because it
  /// caches with the rest; reading free as "how much you can use" is what makes people believe a
  /// machine doing its job is about to fall over.
  /// </remarks>
  [Test]
  public void FreeAndAvailableAreTwoDifferentRows() {
    var memory = Memory();

    Assert.That(ValueOf(memory, "Free"), Is.Not.EqualTo(ValueOf(memory, "Available")));
    Assert.That(ValueOf(memory, "In use"), Is.EqualTo(Humanize.Bytes(Counter.Of((16_384_000ul - 8_192_000) * 1024))));
  }

  /// <summary>
  /// The firmware facts are refused rather than guessed, and the reserved figure with them.
  /// </summary>
  /// <remarks>
  /// Installed less usable is what Task Manager calls hardware-reserved, and on Linux the installed
  /// half comes from root-only SMBIOS tables. Subtracting a figure nobody read gives a plausible
  /// zero, and "nothing is reserved" is a claim about the machine that this program has no business
  /// making (PRD §47).
  /// </remarks>
  [Test]
  public void HardwareReservedIsRefusedWhenNobodyCanSayWhatIsInstalled() {
    var memory = Memory();
    var refused = Humanize.Placeholder(UnknownReason.NotPermitted);

    Assert.That(ValueOf(memory, "Installed"), Is.EqualTo(refused));
    Assert.That(ValueOf(memory, "Hardware reserved"), Is.EqualTo(refused));
    Assert.That(ValueOf(memory, "Usable"), Is.EqualTo(Humanize.Bytes(Counter.Of(16_384_000ul * 1024))), "which the kernel does know");
  }

  /// <summary>
  /// The twenty figures that answer "why" are level four, and the twelve that answer "how much" are
  /// not (PRD §45.2).
  /// </summary>
  [Test]
  public void TheEngineeringFiguresAreMarkedAsSuch() {
    var memory = Memory();

    Assert.That(LevelOf(memory, "In use"), Is.EqualTo(PerformanceRowLevel.Live));
    Assert.That(LevelOf(memory, "Usable"), Is.EqualTo(PerformanceRowLevel.Hardware));
    Assert.That(LevelOf(memory, "Page tables"), Is.EqualTo(PerformanceRowLevel.Diagnostic));
    Assert.That(LevelOf(memory, "Huge pages"), Is.EqualTo(PerformanceRowLevel.Diagnostic));
    Assert.That(LevelOf(memory, "Kernel, fixed"), Is.EqualTo(PerformanceRowLevel.Diagnostic));
  }

  /// <summary>
  /// The reclaim lists are shown as a pair, because either one alone says nothing.
  /// </summary>
  [Test]
  public void TheReclaimListsAreReadAsAPair() {
    var memory = Memory();

    Assert.That(ValueOf(memory, "Anonymous, by list"), Does.Contain("active").And.Contain("inactive"));
    Assert.That(ValueOf(memory, "File, by list"), Does.Contain("active"));
  }

  /// <summary>Reserved huge pages are counted in pages and named with their size.</summary>
  [Test]
  public void HugePagesAreCountedInPagesAndSaidToBe() {
    Assert.That(ValueOf(Memory(), "Huge pages"), Is.EqualTo("16 of 64 in use, 2.0M each"));
  }

  /// <summary>
  /// Compression is only ever shown as a pair: the pool and what is in it.
  /// </summary>
  /// <remarks>
  /// A gigabyte holding two and a half is a machine that has saved itself a gigabyte and a half of
  /// swapping. The pool size alone reads as a gigabyte of memory gone.
  /// </remarks>
  [Test]
  public void CompressionIsShownAsAPoolAndItsContents() {
    var snapshot = Sample();
    snapshot.System.CompressedBytes = Counter.Of(1024ul * 1024 * 1024);
    snapshot.System.CompressedOriginalBytes = Counter.Of(2560ul * 1024 * 1024);

    var sections = PerformanceReport.Build(new() { HostName = "fixture" }, snapshot);
    foreach (var section in sections) {
      if (section.Title != "Memory")
        continue;

      Assert.That(ValueOf(section, "Compressed"), Is.EqualTo("1.0G holding 2.5G  (2.5×)"));
      return;
    }

    Assert.Fail("no memory section");
  }

  /// <summary>A machine that does not compress gets no row rather than a row reading nought.</summary>
  [Test]
  public void AMachineThatDoesNotCompressHasNoCompressionRow() {
    foreach (var row in Memory().Rows)
      Assert.That(row.Label, Is.Not.EqualTo("Compressed"));
  }

  #endregion

  #region the graphs (PRD §47)

  private static PerformanceGraph? GraphNamed(PerformanceSection section, string label) {
    foreach (var graph in section.Series)
      if (graph.Label == label)
        return graph;

    return null;
  }

  /// <summary>
  /// Four series, and each one on the scale that makes it readable.
  /// </summary>
  /// <remarks>
  /// Cache falling while physical memory stays put is the kernel giving up its cache to something;
  /// swap rising after that is the point where it has run out of cache to give. Neither is visible
  /// in the physical-memory series, which stays pinned near the top through all of it.
  /// </remarks>
  [Test]
  public void ThePageGraphsPhysicalCommittedCacheAndSwap() {
    var memory = Memory();

    Assert.That(GraphNamed(memory, "Physical memory"), Is.Not.Null);
    Assert.That(GraphNamed(memory, "Committed"), Is.Not.Null);
    Assert.That(GraphNamed(memory, "Cache"), Is.Not.Null);
    Assert.That(GraphNamed(memory, "Swap"), Is.Not.Null);

    // Scaled to the machine rather than to a hundred: the useful question is how much is gone, and
    // 60 % means nothing until you know whether the machine has 8 GB or 128.
    Assert.That(GraphNamed(memory, "Physical memory")!.Value.Maximum, Is.EqualTo(16_384_000d * 1024));
    Assert.That(GraphNamed(memory, "Swap")!.Value.Maximum, Is.EqualTo(4_194_304d * 1024));
  }

  /// <summary>
  /// The cache series is what the kernel is holding, buffers and all.
  /// </summary>
  [Test]
  public void TheCacheSeriesIsCachedPlusBuffers() {
    Assert.That(
      GraphNamed(Memory(), "Cache")!.Value.Value.Value,
      Is.EqualTo((4_096_000d + 256_000) * 1024)
    );
  }

  /// <summary>
  /// A machine with no swap device gets no swap graph.
  /// </summary>
  /// <remarks>
  /// §45.6: a category the hardware does not have is hidden rather than emptied. A flat line along
  /// the floor of a scale of zero draws the absence of a device as an idle one.
  /// </remarks>
  [Test]
  public void AMachineWithNoSwapGetsNoSwapGraph() {
    var snapshot = Sample();
    snapshot.System.TotalSwapBytes = Counter.Of(0ul);
    snapshot.System.UsedSwapBytes = Counter.Of(0ul);

    foreach (var section in PerformanceReport.Build(new() { HostName = "fixture" }, snapshot)) {
      if (section.Title != "Memory")
        continue;

      Assert.That(GraphNamed(section, "Swap"), Is.Null);
      Assert.That(GraphNamed(section, "Physical memory"), Is.Not.Null, "and the rest are still there");
      return;
    }

    Assert.Fail("no memory section");
  }

  #endregion

  #region the composition bar (PRD §14, §47)

  /// <summary>
  /// The bands are a partition of the machine's memory and sum to the total exactly.
  /// </summary>
  /// <remarks>
  /// What makes it a bar rather than four numbers. Checked against a real <c>meminfo</c> rather than
  /// a constructed one, because the constraint is only interesting when the figures come from
  /// separate lines of a file that is read without a lock.
  /// </remarks>
  [Test]
  public void TheBandsSumToTheTotalOnARealMachinesFigures() {
    var composition = MemoryComposition.Of(in Sample().System);
    var sum = 0ul;
    foreach (var band in composition.Bands)
      sum += band.Bytes;

    Assert.That(composition.HasValue, Is.True);
    Assert.That(sum, Is.EqualTo(composition.TotalBytes));
  }

  /// <summary>
  /// In use is the same figure the statistics beside the bar show, so the two cannot disagree.
  /// </summary>
  [Test]
  public void TheBarAndTheNumbersBesideItAgree() {
    var snapshot = Sample();
    var composition = MemoryComposition.Of(in snapshot.System);
    var inUse = snapshot.System.TotalMemoryBytes.Value - snapshot.System.AvailableMemoryBytes.Value;

    Assert.That(composition.Bands[0].Label, Is.EqualTo("In use"));
    Assert.That(composition.Bands[0].Bytes, Is.EqualTo(inUse));
  }

  #endregion

  #region NUMA (PRD §47)

  /// <summary>
  /// How the memory is spread across the nodes, which the node count cannot say.
  /// </summary>
  /// <remarks>
  /// Two nodes with half each and two nodes with all of it on one are very different machines to run
  /// a thread on.
  /// </remarks>
  [Test]
  public void TheMemoryOnEachNumaNodeIsRead() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      SysRoot = Path.Combine(Fixtures, "sys-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
    });

    var host = probe.DescribeHost();

    Assert.That(host.NumaMemoryBytes, Has.Count.EqualTo(2));
    Assert.That(host.NumaMemoryBytes[0].Value, Is.EqualTo(8_192_000ul * 1024));
    Assert.That(host.NumaMemoryBytes[1].Value, Is.EqualTo(8_192_000ul * 1024));
  }

  /// <summary>
  /// A machine with one node gets no distribution rows: "node 0 has all of it" is not a distribution.
  /// </summary>
  [Test]
  public void ASingleNodeMachineIsNotGivenADistribution() {
    var snapshot = Sample();
    var sections = PerformanceReport.Build(new() { HostName = "fixture", NumaMemoryBytes = [Counter.Of(1024)] }, snapshot);

    foreach (var section in sections) {
      if (section.Title != "Memory")
        continue;

      foreach (var row in section.Rows)
        Assert.That(row.Label, Does.Not.StartWith("Node "));

      return;
    }

    Assert.Fail("no memory section");
  }

  #endregion

}
