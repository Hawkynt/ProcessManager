using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a disk is to the machine (PRD §48): where it is mounted, whether the system is on it, and
/// how long it takes to answer.
/// </summary>
[TestFixture]
public sealed class StorageLayoutTests {

  #region the swap list

  private const string _Swaps = """
    Filename				Type		Size		Used		Priority
    /dev/nvme0n1p4                          partition	8388604		131072		-2
    /swap/swapfile                          file		67108860	4207748		-1
    """;

  [Test]
  public void APartitionAndAFileAreNotTheSameKindOfSwap() {
    var areas = SwapAreaParser.Parse(Encoding.UTF8.GetBytes(_Swaps));

    Assert.That(areas, Has.Count.EqualTo(2));
    Assert.That(areas[0].Path, Is.EqualTo("/dev/nvme0n1p4"));
    Assert.That(areas[0].Kind, Is.EqualTo(SwapAreaParser.SwapKind.Partition));
    Assert.That(areas[0].SizeKilobytes, Is.EqualTo(8_388_604));
    Assert.That(areas[1].Path, Is.EqualTo("/swap/swapfile"));
    Assert.That(areas[1].Kind, Is.EqualTo(SwapAreaParser.SwapKind.File));
    Assert.That(areas[1].UsedKilobytes, Is.EqualTo(4_207_748));
  }

  [Test]
  public void AMachineWithNoSwapHasNoAreasRatherThanOneOfNothing() {
    Assert.That(SwapAreaParser.Parse(Encoding.UTF8.GetBytes("Filename\tType\tSize\tUsed\tPriority\n")), Is.Empty);
  }

  #endregion

  #region the mount table

  /// <summary>
  /// The source is the only column that names a device on a btrfs or ZFS mount: the device number
  /// those file systems report is a synthetic one of their own.
  /// </summary>
  [Test]
  public void AMountCarriesWhatWasMountedAsWellAsWhereItIs() {
    const string Line = "33 2 0:30 /@ / rw,relatime shared:1 - btrfs /dev/mapper/vg-root rw,compress=zstd:3";

    Assert.That(MountInfoParser.TryParse(Encoding.UTF8.GetBytes(Line), out var mount), Is.True);
    Assert.That(mount.MountPoint, Is.EqualTo("/"));
    Assert.That(mount.FileSystem, Is.EqualTo("btrfs"));
    Assert.That(mount.Source, Is.EqualTo("/dev/mapper/vg-root"));
    // The synthetic number is still reported as it is written; it is simply not what the disk is
    // found by.
    Assert.That(mount.Device, Is.EqualTo("0:30"));
  }

  [Test]
  public void AMountSourceWithASpaceInItIsUnescapedLikeTheMountPoint() {
    const string Line = "40 33 8:1 / /mnt rw,relatime - ext4 /dev/disk/by-label/My\\040Disk rw";

    Assert.That(MountInfoParser.TryParse(Encoding.UTF8.GetBytes(Line), out var mount), Is.True);
    Assert.That(mount.Source, Is.EqualTo("/dev/disk/by-label/My Disk"));
  }

  #endregion

  #region the stack from a mount down to a disk

  /// <summary>
  /// A recorded machine: one disk with two partitions, an encrypted container on the second, and a
  /// logical volume inside that — the shape an ordinary installer produces and the one a lookup by
  /// device number cannot follow.
  /// </summary>
  private static string BuildTree() {
    var root = Path.Combine(Path.GetTempPath(), $"procman storage {Guid.NewGuid():N}");
    var sys = Path.Combine(root, "sys");
    var proc = Path.Combine(root, "proc");

    Directory.CreateDirectory(Path.Combine(sys, "block", "nvme0n1", "nvme0n1p1"));
    Directory.CreateDirectory(Path.Combine(sys, "block", "nvme0n1", "nvme0n1p2"));
    Directory.CreateDirectory(Path.Combine(sys, "block", "sdb"));

    // dm-0 is the encrypted container on nvme0n1p2; dm-1 is the volume inside dm-0.
    Directory.CreateDirectory(Path.Combine(sys, "block", "dm-0", "slaves", "nvme0n1p2"));
    Directory.CreateDirectory(Path.Combine(sys, "block", "dm-0", "dm"));
    File.WriteAllText(Path.Combine(sys, "block", "dm-0", "dm", "name"), "cryptlvm\n");
    Directory.CreateDirectory(Path.Combine(sys, "block", "dm-1", "slaves", "dm-0"));
    Directory.CreateDirectory(Path.Combine(sys, "block", "dm-1", "dm"));
    File.WriteAllText(Path.Combine(sys, "block", "dm-1", "dm", "name"), "vg-root\n");

    Directory.CreateDirectory(Path.Combine(proc, "self"));
    File.WriteAllText(Path.Combine(proc, "self", "mountinfo"), """
      33 2 0:30 /@ / rw,relatime shared:1 - btrfs /dev/mapper/vg-root rw
      36 33 259:1 / /boot rw,relatime shared:2 - vfat /dev/nvme0n1p1 rw
      41 33 8:16 / /data rw,relatime shared:3 - xfs /dev/sdb rw
      44 33 0:24 / /proc rw,relatime shared:4 - proc proc rw

      """);

    File.WriteAllText(Path.Combine(proc, "swaps"), """
      Filename				Type		Size		Used		Priority
      /swap/swapfile                          file		67108860	4207748		-1

      """);

    return root;
  }

  [Test]
  public void AMountIsFoundThroughEveryLayerBetweenItAndTheDisk() {
    var root = BuildTree();
    try {
      var layout = new LinuxStorageLayout(Path.Combine(root, "sys"), Path.Combine(root, "proc"));
      Assert.That(layout.Known, Is.True);

      // The root file system is on the volume, on the container, and on the disk under both.
      Assert.That(layout.VolumesOf("nvme0n1"), Does.Contain("/").And.Contain("/boot"));
      Assert.That(layout.VolumesOf("dm-1"), Does.Contain("/"));
      Assert.That(layout.VolumesOf("dm-0"), Does.Contain("/"));
      Assert.That(layout.IsSystemDisk("nvme0n1"), Is.True);
      Assert.That(layout.IsSystemDisk("sdb"), Is.False);
      Assert.That(layout.VolumesOf("sdb"), Is.EqualTo(new[] { "/data" }));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>
  /// A swap file is on whichever file system holds its path, which is the longest mount point the
  /// path begins with — here the root, because nothing else covers <c>/swap</c>.
  /// </summary>
  [Test]
  public void ASwapFileIsChargedToTheDiskUnderTheFileSystemHoldingIt() {
    var root = BuildTree();
    try {
      var layout = new LinuxStorageLayout(Path.Combine(root, "sys"), Path.Combine(root, "proc"));

      Assert.That(layout.HoldsSwap("nvme0n1"), Is.True);
      Assert.That(layout.HoldsSwap("sdb"), Is.False);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>
  /// No mount table is not an empty one. A machine whose table could not be read must not describe
  /// every disk in it as unmounted (PRD §5.3).
  /// </summary>
  [Test]
  public void AnUnreadableMountTableIsUnknownRatherThanEmpty() {
    var layout = new LinuxStorageLayout(
      Path.Combine(Path.GetTempPath(), "procman-missing-sys"),
      Path.Combine(Path.GetTempPath(), "procman-missing-proc")
    );

    Assert.That(layout.Known, Is.False);
    Assert.That(layout.VolumesOf("nvme0n1"), Is.Null);
    Assert.That(layout.IsSystemDisk("nvme0n1"), Is.Null);
    Assert.That(layout.HoldsSwap("nvme0n1"), Is.Null);
  }

  #endregion

  #region response time and queue depth

  private static SnapshotDelta.DiskRates Rates(
    ulong reads = 0,
    ulong writes = 0,
    ulong readWaitMs = 0,
    ulong writeWaitMs = 0,
    ulong weightedMs = 0
  ) {
    var before = new SystemSnapshot { TimestampTicks = 0 };
    before.PrepareProcesses(0);
    var after = new SystemSnapshot { TimestampTicks = System.Diagnostics.Stopwatch.Frequency };
    after.PrepareProcesses(0);

    before.PrepareDisks(1)[0] = new() {
      Name = "sda",
      ReadOperations = Counter.Of(0),
      WriteOperations = Counter.Of(0),
      ReadBytes = Counter.Of(0),
      WriteBytes = Counter.Of(0),
      BusyMilliseconds = Counter.Of(0),
      ReadWaitMilliseconds = Counter.Of(0),
      WriteWaitMilliseconds = Counter.Of(0),
      WeightedQueueMilliseconds = Counter.Of(0),
      QueuedRequests = Counter.Of(0),
    };

    after.PrepareDisks(1)[0] = new() {
      Name = "sda",
      ReadOperations = Counter.Of(reads),
      WriteOperations = Counter.Of(writes),
      ReadBytes = Counter.Of(0),
      WriteBytes = Counter.Of(0),
      BusyMilliseconds = Counter.Of(0),
      ReadWaitMilliseconds = Counter.Of(readWaitMs),
      WriteWaitMilliseconds = Counter.Of(writeWaitMs),
      WeightedQueueMilliseconds = Counter.Of(weightedMs),
      QueuedRequests = Counter.Of(0),
    };

    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.Normalized);
    return delta.DiskRatesOf("sda");
  }

  /// <summary>The same arithmetic <c>iostat</c> does: milliseconds waited over requests made.</summary>
  [Test]
  public void LatencyIsTheWaitPerRequest() {
    var rates = Rates(reads: 100, readWaitMs: 250, writes: 400, writeWaitMs: 200);

    Assert.That(rates.ReadLatencyMilliseconds.Value, Is.EqualTo(2.5).Within(0.001));
    Assert.That(rates.WriteLatencyMilliseconds.Value, Is.EqualTo(0.5).Within(0.001));
    // Weighted by how many requests each direction had, not the mean of the two figures.
    Assert.That(rates.ResponseTimeMilliseconds.Value, Is.EqualTo(0.9).Within(0.001));
  }

  /// <summary>
  /// An idle disk has no latency. Nought would draw it as infinitely fast, which is the opposite of
  /// what a reader would conclude (PRD §72.3).
  /// </summary>
  [Test]
  public void ADiskNobodyAskedAnythingOfHasNoLatency() {
    var rates = Rates(weightedMs: 0);

    Assert.That(rates.ReadLatencyMilliseconds.HasValue, Is.False);
    Assert.That(rates.WriteLatencyMilliseconds.HasValue, Is.False);
    Assert.That(rates.ResponseTimeMilliseconds.HasValue, Is.False);
  }

  /// <summary>
  /// The weighted counter gains a millisecond per outstanding request per millisecond, so a second
  /// with 2000 of them is an average depth of two.
  /// </summary>
  [Test]
  public void QueueLengthIsTheTimeWeightedDepth() {
    Assert.That(Rates(reads: 1, readWaitMs: 1, weightedMs: 2000).QueueLength.Value, Is.EqualTo(2).Within(0.01));
    Assert.That(Rates(reads: 1, readWaitMs: 1, weightedMs: 50).QueueLength.Value, Is.EqualTo(0.05).Within(0.001));
  }

  #endregion

}
