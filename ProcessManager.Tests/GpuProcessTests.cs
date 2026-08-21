using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Per-process graphics accounting (PRD §19).
/// </summary>
/// <remarks>
/// Against a recorded tree and not against whatever card the runner happens to have: the DRM
/// interface is a text file, and a text file is testable on every CI leg (PRD §9.1, §9.2). What
/// cannot be recorded is NVML, which is a library — so the tests that matter about it are the ones
/// asserting what happens when it is not there, which is the case on every machine running this.
/// </remarks>
[TestFixture]
public sealed class GpuProcessTests {

  #region the kernel's own client accounting

  private const string _I915 = """
    pos:	0
    flags:	02100002
    mnt_id:	28
    ino:	515
    drm-driver:	i915
    drm-client-id:	34
    drm-pdev:	0000:00:02.0
    drm-total-system0:	32768 KiB
    drm-shared-system0:	8 MiB
    drm-active-system0:	0
    drm-resident-system0:	24576 KiB
    drm-purgeable-system0:	708 KiB
    drm-total-stolen-system0:	4096 KiB
    drm-engine-render:	4000000000 ns
    drm-engine-copy:	1000000000 ns
    drm-engine-video:	0 ns
    drm-engine-capacity-video:	2
    drm-engine-video-enhance:	0 ns
    """;

  private const string _Amdgpu = """
    pos:	0
    flags:	02100002
    drm-driver:	amdgpu
    drm-client-id:	7
    drm-pdev:	0000:03:00.0
    drm-memory-vram:	524288 KiB
    drm-memory-gtt:	1024 KiB
    drm-memory-cpu:	0 KiB
    drm-engine-gfx:	500000000 ns
    drm-engine-compute:	2000000000 ns
    drm-engine-dma:	1000000 ns
    drm-engine-dec:	0 ns
    drm-engine-enc:	3000000000 ns
    drm-engine-enc_1:	1000000000 ns
    """;

  private static DrmClient Parse(string text) {
    Assert.That(DrmFdinfoParser.TryParse(Encoding.UTF8.GetBytes(text), out var client), Is.True);
    return client;
  }

  [Test]
  public void ADescriptorThatIsNotAGraphicsOneIsRejectedOutright() {
    // The cheap rejection the whole scan rests on: a machine has thousands of descriptors and a
    // handful of DRM clients, so anything else has to cost one look at the first line.
    Assert.That(DrmFdinfoParser.TryParse("pos:\t0\nflags:\t02\nmnt_id:\t15\n"u8, out _), Is.False);
    Assert.That(DrmFdinfoParser.TryParse([], out _), Is.False);
  }

  [Test]
  public void IntelsEngineNamesLandInTheRightColumns() {
    var client = Parse(_I915);

    Assert.That(client.GraphicsNs, Is.EqualTo(4_000_000_000ul), "render is the graphics engine");
    Assert.That(client.CopyNs, Is.EqualTo(1_000_000_000ul));
    Assert.That(client.Engines.HasFlag(DrmEngineFlags.Graphics), Is.True);
    Assert.That(client.Engines.HasFlag(DrmEngineFlags.Copy), Is.True);
    // i915 publishes no compute engine of its own; its render engine does both. Saying so is the
    // point — a nought here would claim the part has one that nothing ever uses.
    Assert.That(client.Engines.HasFlag(DrmEngineFlags.Compute), Is.False);
  }

  /// <summary>
  /// <c>drm-engine-capacity-video</c> is how many video engines the part has, not a time in
  /// nanoseconds. Reading it as one charges two nanoseconds of work to an engine that did none.
  /// </summary>
  [Test]
  public void TheEngineCapacityLineIsNotAnEngineTime() {
    var client = Parse(_I915);

    Assert.That(client.DecodeNs, Is.EqualTo(0ul));
    Assert.That(client.EncodeNs, Is.EqualTo(0ul));
  }

  [Test]
  public void AmdsEngineNamesLandInTheRightColumnsToo() {
    var client = Parse(_Amdgpu);

    Assert.That(client.GraphicsNs, Is.EqualTo(500_000_000ul), "gfx is the graphics engine");
    Assert.That(client.ComputeNs, Is.EqualTo(2_000_000_000ul));
    Assert.That(client.CopyNs, Is.EqualTo(1_000_000ul), "dma is a copy engine");
    // enc and enc_1 are two of the same engine, and one column: a card with two encoders running is
    // not a card with an engine nobody can name.
    Assert.That(client.EncodeNs, Is.EqualTo(4_000_000_000ul));
    Assert.That(client.Engines.HasFlag(DrmEngineFlags.Decode), Is.True);
  }

  /// <summary>
  /// Reading <c>28596 KiB</c> as bytes under-reports a client's memory by a factor of a thousand,
  /// and reads on screen as a process using nothing at all.
  /// </summary>
  [Test]
  public void TheUnitBesideAMemoryFigureIsNotDecoration() {
    var client = Parse(_Amdgpu);

    Assert.That(client.HasDedicated, Is.True);
    Assert.That(client.DedicatedBytes, Is.EqualTo(524_288ul * 1024));
    Assert.That(client.SharedBytes, Is.EqualTo(1024ul * 1024), "gtt and cpu together, in bytes");
  }

  /// <summary>
  /// An integrated part has no video memory to report, and its whole working set is system memory.
  /// The resident figure wins over the total for the same reason a resident set beats a virtual
  /// size: the total counts buffers that have been evicted and are costing the part nothing.
  /// </summary>
  [Test]
  public void AnIntegratedPartReportsSharedMemoryAndNoDedicated() {
    var client = Parse(_I915);

    Assert.That(client.HasDedicated, Is.False);
    Assert.That(client.HasShared, Is.True);
    Assert.That(client.SharedBytes, Is.EqualTo(24_576ul * 1024), "resident, not total");
  }

  /// <summary>
  /// Stolen memory is carved out of system memory and is already counted in the system region.
  /// Adding it reports it twice.
  /// </summary>
  [Test]
  public void StolenMemoryIsNotCountedTwice() {
    var client = Parse(_I915);

    Assert.That(client.SharedBytes, Is.LessThan((24_576ul + 4096) * 1024));
  }

  [Test]
  public void TheClientNumberAndThePciAddressComeOutWhole() {
    var client = Parse(_I915);
    var content = Encoding.UTF8.GetBytes(_I915);

    Assert.That(client.ClientId, Is.EqualTo(34));
    Assert.That(
      Encoding.UTF8.GetString(content.AsSpan(client.PciAddressOffset, client.PciAddressLength)),
      Is.EqualTo("0000:00:02.0")
    );
  }

  /// <summary>A driver that numbers no clients still has its engines read.</summary>
  [Test]
  public void AClientWithNoNumberIsStillAClient() {
    var client = Parse("drm-driver:\tpanfrost\ndrm-engine-fragment:\t5 ns\n");

    Assert.That(client.ClientId, Is.EqualTo(-1));
    Assert.That(client.PciAddressLength, Is.Zero);
  }

  #endregion

  #region the probe, against a recorded tree

  private static string ProcRoot => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-gpu");

  private static string SysRoot => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-gpu");

  /// <summary>The same two cards with a third that needs a library no machine running this will have.</summary>
  private static string SysRootWithNvidia
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "sys-gpu-nvidia");

  private static LinuxProbeOptions Options(bool gpu, string? sysRoot = null) => new() {
    ProcRoot = ProcRoot,
    SysRoot = sysRoot ?? SysRoot,
    PasswdPath = Path.Combine(ProcRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
    ReadGpuUsage = gpu,
  };

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"no process {pid} in the fixture");
    return default;
  }

  private static SystemSnapshot Sampled(bool gpu, string? sysRoot = null) {
    using var probe = new LinuxProbe(Options(gpu, sysRoot));
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    return snapshot;
  }

  /// <summary>
  /// The bug this whole file is written against: a field nobody collected must say so. Reading
  /// nought GPU memory for every process because the collector was switched off is exactly the
  /// confident zero §72.3 forbids.
  /// </summary>
  [Test]
  public void WithTheCollectorOffEveryGraphicsFieldSaysSoRatherThanReadingNought() {
    var snapshot = Sampled(gpu: false);
    var record = Find(snapshot, 100);

    Assert.Multiple(() => {
      Assert.That(record.GpuDedicatedBytes.HasValue, Is.False);
      Assert.That(record.GpuDedicatedBytes.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
      Assert.That(record.GpuSharedBytes.HasValue, Is.False);
      Assert.That(record.GpuGraphicsNs.HasValue, Is.False);
      Assert.That(record.GpuBusyPercent.HasValue, Is.False);
      Assert.That(record.GpuAdapter, Is.Null);
      Assert.That(record.GpuAdapterReason, Is.EqualTo(UnknownReason.NotSampledYet));
    });
  }

  [Test]
  public void OneClientHeldThroughTwoDescriptorsIsCountedOnce() {
    var record = Find(Sampled(gpu: true), 100);

    // The fixture holds the same client on descriptors 3 and 7. Summing them would report eight
    // seconds of rendering where the kernel counted four, and 48 MB of memory where it counted 24.
    Assert.That(record.GpuGraphicsNs.Value, Is.EqualTo(4_000_000_000ul));
    Assert.That(record.GpuSharedBytes.Value, Is.EqualTo(24_576ul * 1024));
  }

  [Test]
  public void TwoDifferentClientsOfOneProcessDoAddUp() {
    var record = Find(Sampled(gpu: true), 200);

    Assert.That(record.GpuGraphicsNs.Value, Is.EqualTo(1_000_000_000ul), "500 ms on each of two clients");
    Assert.That(record.GpuDedicatedBytes.Value, Is.EqualTo((524_288ul + 1024) * 1024));
  }

  [Test]
  public void EachProcessIsNamedWithTheAdapterItsClientsAreOn() {
    var snapshot = Sampled(gpu: true);

    Assert.That(Find(snapshot, 100).GpuAdapter, Is.EqualTo("card0"), "the Intel part, by PCI address");
    Assert.That(Find(snapshot, 200).GpuAdapter, Is.EqualTo("card1"), "the AMD card, by PCI address");
  }

  /// <summary>
  /// A process that has looked at no adapter has measurably used none of one, which is a different
  /// statement from "nobody looked" and reads as a nought rather than as a placeholder.
  /// </summary>
  [Test]
  public void AProcessWithNoClientReadsAsAMeasuredNoughtAndNamesNoAdapter() {
    var record = Find(Sampled(gpu: true), 300);

    Assert.Multiple(() => {
      Assert.That(record.GpuGraphicsNs.HasValue, Is.True);
      Assert.That(record.GpuGraphicsNs.Value, Is.Zero);
      Assert.That(record.GpuDedicatedBytes.Value, Is.Zero);
      Assert.That(record.GpuAdapter, Is.Null);
      Assert.That(record.GpuAdapterReason, Is.EqualTo(UnknownReason.None), "an empty cell, not a reason");
    });
  }

  /// <summary>
  /// An engine the driver does not publish carries its own reason. i915 counts no compute engine,
  /// and a nought in that column would tell a reader the part has one that nothing uses.
  /// </summary>
  [Test]
  public void AnEngineTheDriverDoesNotPublishSaysSoRatherThanReadingNought() {
    var record = Find(Sampled(gpu: true), 100);

    Assert.That(record.GpuComputeNs.HasValue, Is.False);
    Assert.That(record.GpuComputeNs.Reason, Is.EqualTo(UnknownReason.NotImplementedHere));
    Assert.That(record.GpuGraphicsNs.HasValue, Is.True, "and the ones it does publish still read");
  }

  /// <summary>
  /// The defect this was written against, found by hiding NVML on a machine that has a card needing
  /// it: a process holding 15.5 GB of that card's memory rendered as <c>0 B</c>, because it had no
  /// kernel-visible client and the Intel part beside it answered perfectly well. One readable
  /// adapter does not make "this process is using no GPU" sayable while another cannot be read at
  /// all — and NVIDIA's stack publishes nothing to the kernel, so without its library there is
  /// nothing left to look at.
  /// </summary>
  /// <remarks>
  /// The fixture's third card carries <c>DRIVER=nvidia</c> at a PCI address no machine has, so the
  /// answer is the same whether or not the library is installed on the machine running this: an
  /// adapter that is known to be there and cannot be asked anything.
  /// </remarks>
  [Test]
  public void AnAdapterNothingCanBeAskedAboutMakesNoughtUnsayableForEveryProcess() {
    var record = Find(Sampled(gpu: true, SysRootWithNvidia), 300);

    Assert.Multiple(() => {
      Assert.That(record.GpuDedicatedBytes.HasValue, Is.False, "a card here cannot be read, so this is not nought");
      Assert.That(record.GpuDedicatedBytes.Reason, Is.EqualTo(UnknownReason.NotImplementedHere));
      Assert.That(record.GpuGraphicsNs.HasValue, Is.False);
      Assert.That(record.GpuAdapterReason, Is.EqualTo(UnknownReason.NotImplementedHere));
    });
  }

  /// <summary>
  /// And the process that <em>is</em> visible to the kernel is still read in full: an unreadable
  /// card must not blank the ones that answer.
  /// </summary>
  [Test]
  public void AnUnreadableCardDoesNotSilenceTheOnesThatAnswer() {
    var record = Find(Sampled(gpu: true, SysRootWithNvidia), 100);

    Assert.That(record.GpuGraphicsNs.Value, Is.EqualTo(4_000_000_000ul));
    Assert.That(record.GpuAdapter, Is.EqualTo("card0"));
  }

  /// <summary>
  /// §19's own requirement, and the one most easily got wrong: a machine whose adapters answer
  /// neither NVML nor the kernel's client accounting has to render capability state and never a
  /// nought. The tree here is the ordinary desktop fixture, whose processes hold no graphics
  /// descriptor at all — which is indistinguishable from a driver that publishes nothing until
  /// somebody has looked, and so is exactly the case that must not read as a card sitting idle.
  /// </summary>
  [Test]
  public void AMachineWhoseAdaptersCannotBeReadRendersCapabilityStateAndNotANought() {
    var desktop = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new LinuxProbe(new() {
      ProcRoot = desktop,
      // A tree with no /sys/class/drm in it at all, which is what a machine with no adapter the
      // program can name looks like from here.
      SysRoot = Path.Combine(desktop, "nothing"),
      PasswdPath = Path.Combine(desktop, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
      ReadGpuUsage = true,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    var record = Find(snapshot, 1000);

    Assert.Multiple(() => {
      Assert.That(record.GpuGraphicsNs.HasValue, Is.False);
      Assert.That(record.GpuGraphicsNs.Reason, Is.EqualTo(UnknownReason.NotImplementedHere));
      Assert.That(record.GpuDedicatedBytes.HasValue, Is.False);
      Assert.That(record.GpuSharedBytes.HasValue, Is.False);
      Assert.That(record.GpuBusyPercent.HasValue, Is.False);
      Assert.That(record.GpuAdapterReason, Is.EqualTo(UnknownReason.NotImplementedHere));
    });

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    Assert.That(
      FieldAccessor.Text(ProcessField.GpuPercent, in snapshot.Processes[0], delta, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotImplementedHere)),
      "the column says the machine cannot answer, not that the card is idle"
    );
    Assert.That(FieldAccessor.Number(ProcessField.GpuPercent, in snapshot.Processes[0], delta, 0), Is.Null);
  }

  /// <summary>
  /// NVML is a library, not a file, so a recorded tree has none of it. Every NVIDIA-only reading
  /// must therefore carry a reason rather than a figure — which is also what happens on the very
  /// many machines that have no proprietary driver installed at all.
  /// </summary>
  [Test]
  public void WithoutTheVendorLibraryTheReadingsItAloneHasCarryAReason() {
    var record = Find(Sampled(gpu: true), 100);

    Assert.That(record.GpuBusyPercent.HasValue, Is.False);
    Assert.That(record.GpuBusyEngine, Is.EqualTo(GpuEngine.Unknown));
  }

  #endregion

  #region turning counters into columns

  /// <summary>
  /// Two samples of the fixture with an interval stated, so the engine percentages are arithmetic
  /// rather than whatever the clock did while the test ran.
  /// </summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) TwoSamples(
    Action<Span<ProcessRecord>> first,
    Action<Span<ProcessRecord>> second
  ) {
    var before = new SystemSnapshot();
    var records = before.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "renderer";
    first(records);
    before.TimestampTicks = 0;

    var now = new SystemSnapshot();
    var current = now.PrepareProcesses(1);
    current[0] = records[0];
    second(current);
    // One second, in the monotonic ticks the delta measures its interval on — Stopwatch's, whose
    // frequency is the platform's and is not the 100 ns of a TimeSpan.
    now.TimestampTicks = System.Diagnostics.Stopwatch.Frequency;

    var delta = new SnapshotDelta();
    delta.Update(before, now, CpuPercentMode.Normalized);
    return (now, delta);
  }

  [Test]
  public void HalfASecondOfEngineTimeInOneSecondIsFiftyPercent() {
    var (_, delta) = TwoSamples(
      static records => records[0].GpuGraphicsNs = Counter.Of(0ul),
      static records => records[0].GpuGraphicsNs = Counter.Of(500_000_000ul)
    );

    Assert.That(delta.GpuGraphicsPercent(0).Value, Is.EqualTo(50).Within(0.01));
    Assert.That(delta.GpuPercent(0).Value, Is.EqualTo(50).Within(0.01));
    Assert.That(delta.BusiestGpuEngine(0), Is.EqualTo(GpuEngine.Graphics));
  }

  /// <summary>
  /// The engines run at once, so their shares are each of the whole interval and adding them is
  /// meaningless: a process at 60 % decode and 60 % copy is not using 120 % of a card.
  /// </summary>
  [Test]
  public void TheHeadlineFigureIsTheBusiestEngineAndNotTheSum() {
    var (_, delta) = TwoSamples(
      static records => {
        records[0].GpuCopyNs = Counter.Of(0ul);
        records[0].GpuDecodeNs = Counter.Of(0ul);
      },
      static records => {
        records[0].GpuCopyNs = Counter.Of(600_000_000ul);
        records[0].GpuDecodeNs = Counter.Of(700_000_000ul);
      }
    );

    Assert.That(delta.GpuPercent(0).Value, Is.EqualTo(70).Within(0.01));
    Assert.That(delta.BusiestGpuEngine(0), Is.EqualTo(GpuEngine.Decode));
  }

  /// <summary>
  /// A process using none of the adapter is on no engine at all. Naming whichever engine happened to
  /// report its nought first put "3D" against every kernel thread on the machine.
  /// </summary>
  [Test]
  public void AProcessUsingNoneOfTheAdapterIsOnNoEngine() {
    var (snapshot, delta) = TwoSamples(
      static records => records[0].GpuGraphicsNs = Counter.Of(0ul),
      static records => records[0].GpuGraphicsNs = Counter.Of(0ul)
    );

    Assert.That(delta.GpuPercent(0).Value, Is.Zero);
    Assert.That(delta.BusiestGpuEngine(0), Is.EqualTo(GpuEngine.Unknown));
    Assert.That(FieldAccessor.Text(ProcessField.GpuEngineName, in snapshot.Processes[0], delta, 0), Is.Empty);
  }

  /// <summary>
  /// A driver that samples a percentage instead of keeping a counter has an answer on the first
  /// sample, and blanking it for an interval would hide a process at full utilisation for no reason.
  /// </summary>
  [Test]
  public void ASampledPercentageNeedsNoPreviousSample() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "cuda";
    records[0].GpuGraphicsNs = Counter.Unknown(UnknownReason.NotImplementedHere);
    records[0].GpuComputeNs = Counter.Unknown(UnknownReason.NotImplementedHere);
    records[0].GpuCopyNs = Counter.Unknown(UnknownReason.NotImplementedHere);
    records[0].GpuEncodeNs = Counter.Unknown(UnknownReason.NotImplementedHere);
    records[0].GpuDecodeNs = Counter.Unknown(UnknownReason.NotImplementedHere);
    records[0].GpuBusyPercent = Counter.Of(87ul);
    records[0].GpuBusyEngine = GpuEngine.Compute;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(delta.GpuPercent(0).Value, Is.EqualTo(87));
    Assert.That(delta.BusiestGpuEngine(0), Is.EqualTo(GpuEngine.Compute));
    // And it belongs to the engine the driver attributed it to, not to both: showing one figure in
    // two columns would claim a compute client is also drawing.
    Assert.That(delta.GpuComputePercent(0).Value, Is.EqualTo(87));
    Assert.That(delta.GpuGraphicsPercent(0).HasValue, Is.False);
  }

  /// <summary>
  /// A card that reports one half of its memory still has a total, and it is the half that is known:
  /// an integrated part has no dedicated memory and NVML publishes no shared figure, so insisting on
  /// both would empty the column on every machine there is.
  /// </summary>
  [Test]
  public void TheMemoryTotalIsWhicheverHalvesAreKnown() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "renderer";
    records[0].GpuDedicatedBytes = Counter.Of(2048ul);
    records[0].GpuSharedBytes = Counter.Unknown(UnknownReason.NotImplementedHere);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(FieldAccessor.Number(ProcessField.GpuTotalMemory, in snapshot.Processes[0], delta, 0), Is.EqualTo(2048));

    records[0].GpuDedicatedBytes = Counter.Unknown(UnknownReason.NotImplementedHere);
    Assert.That(
      FieldAccessor.Number(ProcessField.GpuTotalMemory, in snapshot.Processes[0], delta, 0),
      Is.Null,
      "with neither half known there is nothing to add, and no nought to claim"
    );
  }

  #endregion

}
