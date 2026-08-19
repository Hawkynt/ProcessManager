using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Per-device disk and network counters (PRD §48, §49), parsed from recorded text so they are
/// checked on every CI leg.
/// </summary>
[TestFixture]
public sealed class DeviceStatTests {

  /// <summary>Real lines, trimmed to the fields that are read.</summary>
  private const string _DiskStats = """
     259       0 nvme0n1 173170 1 2652872 12741 1833 261347 2105408 5705 0 8045 18466 0 0 0 0 4 20
     259       1 nvme0n1p1 41 0 3312 15 0 0 0 0 0 16 15 0 0 0 0 0 0
       8       0 sda 100 0 200 10 50 0 400 20 0 3000 30 0 0 0 0 0 0
       7       0 loop0 5 0 10 1 0 0 0 0 0 1 1 0 0 0 0 0 0
    """;

  private const string _NetDev = """
    Inter-|   Receive                                                |  Transmit
     face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
        lo: 14055545    6729    0    0    0     0          0         0 14055545    6729    0    0    0     0       0          0
    wlp148s0: 82319681422 54750618    0    3    0     0          0         0 1059761314 5691384    0  645    0     0       0          0
    """;

  private static DiskCounters[] Disks(string text, Func<string, bool>? filter = null) {
    var buffer = new DiskCounters[16];
    var count = DeviceStatParser.ParseDiskStats(
      Encoding.UTF8.GetBytes(text),
      filter ?? (name => !name.StartsWith("loop", StringComparison.Ordinal) && !name.Contains('p')),
      buffer,
      new()
    );

    return buffer[..count];
  }

  private static NetworkCounters[] Networks(string text) {
    var buffer = new NetworkCounters[16];
    var count = DeviceStatParser.ParseNetDev(Encoding.UTF8.GetBytes(text), buffer, new());
    return buffer[..count];
  }

  #region disks

  [Test]
  public void EveryFieldOfADiskLineLands() {
    var disk = Disks(_DiskStats)[0];

    Assert.That(disk.Name, Is.EqualTo("nvme0n1"));
    Assert.That(disk.ReadOperations.Value, Is.EqualTo(173170ul));
    Assert.That(disk.WriteOperations.Value, Is.EqualTo(1833ul));
    // Sectors are 512 bytes in diskstats whatever the device's own sector size — a classic way to
    // be wrong by a factor of eight.
    Assert.That(disk.ReadBytes.Value, Is.EqualTo(2652872ul * 512));
    Assert.That(disk.WriteBytes.Value, Is.EqualTo(2105408ul * 512));
    // Field 13, io_ticks. Field 14 is the queue-weighted time and is 18466 on this line — a
    // different measurement, and the one a miscount lands on.
    Assert.That(disk.BusyMilliseconds.Value, Is.EqualTo(8045ul));
  }

  /// <summary>
  /// A partition is charged the same I/O as the disk that holds it, so counting both reports twice
  /// the traffic the machine actually did.
  /// </summary>
  [Test]
  public void PartitionsAndLoopDevicesAreLeftOut() {
    var names = new List<string>();
    foreach (var disk in Disks(_DiskStats))
      names.Add(disk.Name);

    Assert.That(names, Is.EqualTo(new[] { "nvme0n1", "sda" }));
  }

  [Test]
  public void TheFilterDecidesAndNothingElseDoes() {
    // The name says nothing useful: nvme0n1 ends in a digit and is a whole disk, nvme0n1p1 ends in
    // a digit and is not. /sys/block is the kernel's own answer, and this parser just asks.
    var everything = Disks(_DiskStats, _ => true);
    Assert.That(everything, Has.Length.EqualTo(4));

    var nothing = Disks(_DiskStats, _ => false);
    Assert.That(nothing, Is.Empty);
  }

  #endregion

  #region interfaces

  [Test]
  public void EveryFieldOfAnInterfaceLineLands() {
    var wireless = Networks(_NetDev)[1];

    Assert.That(wireless.Name, Is.EqualTo("wlp148s0"));
    Assert.That(wireless.ReceivedBytes.Value, Is.EqualTo(82319681422ul));
    Assert.That(wireless.ReceivedPackets.Value, Is.EqualTo(54750618ul));
    Assert.That(wireless.ReceiveDropped.Value, Is.EqualTo(3ul));
    // Transmit begins after four fields nothing reads; landing on the wrong one puts the received
    // byte count in the sent column, which looks entirely plausible.
    Assert.That(wireless.SentBytes.Value, Is.EqualTo(1059761314ul));
    Assert.That(wireless.SentPackets.Value, Is.EqualTo(5691384ul));
    Assert.That(wireless.SendDropped.Value, Is.EqualTo(645ul));
    Assert.That(wireless.SendErrors.Value, Is.EqualTo(0ul));
  }

  [Test]
  public void TheTwoHeaderLinesAreNotInterfaces() {
    var interfaces = Networks(_NetDev);

    Assert.That(interfaces, Has.Length.EqualTo(2));
    Assert.That(interfaces[0].Name, Is.EqualTo("lo"));
  }

  /// <summary>
  /// On a busy interface the counter runs into the colon — "eth0:1234567" with no space — which is
  /// why the name is split on the colon rather than on whitespace.
  /// </summary>
  [Test]
  public void ANameRunningIntoItsFirstNumberStillParses() {
    var text = _NetDev.Replace("wlp148s0: 82319681422", "wlp148s0:82319681422", StringComparison.Ordinal);
    var wireless = Networks(text)[1];

    Assert.That(wireless.Name, Is.EqualTo("wlp148s0"));
    Assert.That(wireless.ReceivedBytes.Value, Is.EqualTo(82319681422ul));
  }

  [Test]
  public void AnEmptyFileYieldsNothingRatherThanThrowing() {
    Assert.That(Disks(string.Empty, _ => true), Is.Empty);
    Assert.That(Networks(string.Empty), Is.Empty);
  }

  [Test]
  public void MoreDevicesThanTheBufferHoldsAreTruncatedRatherThanOverrunning() {
    var lines = new StringBuilder();
    for (var i = 0; i < 40; ++i)
      lines.AppendLine($" 8 {i} sd{i} 1 0 2 0 3 0 4 0 0 5 0");

    var buffer = new DiskCounters[4];
    var count = DeviceStatParser.ParseDiskStats(Encoding.UTF8.GetBytes(lines.ToString()), _ => true, buffer, new());
    Assert.That(count, Is.EqualTo(4));
  }

  #endregion

  #region rates

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) TwoSamples(
    ulong firstReadSectors,
    ulong secondReadSectors,
    ulong firstBusyMs,
    ulong secondBusyMs,
    double seconds
  ) {
    static SystemSnapshot Build(ulong sectors, ulong busy) {
      var snapshot = new SystemSnapshot();
      snapshot.PrepareProcesses(0);
      var disks = snapshot.PrepareDisks(1);
      disks[0] = new() {
        Name = "sda",
        ReadBytes = Counter.Of(sectors * 512),
        WriteBytes = Counter.Of(0),
        ReadOperations = Counter.Of(sectors),
        WriteOperations = Counter.Of(0),
        BusyMilliseconds = Counter.Of(busy),
      };

      var networks = snapshot.PrepareNetworks(1);
      networks[0] = new() {
        Name = "eth0",
        ReceivedBytes = Counter.Of(sectors * 1000),
        SentBytes = Counter.Of(0),
        ReceivedPackets = Counter.Of(0),
        SentPackets = Counter.Of(0),
      };

      return snapshot;
    }

    var previous = Build(firstReadSectors, firstBusyMs);
    var current = Build(secondReadSectors, secondBusyMs);
    previous.TimestampTicks = 0;
    current.TimestampTicks = (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    var delta = new SnapshotDelta();
    delta.Update(previous, current, CpuPercentMode.Normalized);
    return (current, delta);
  }

  [Test]
  public void DiskRatesAreBytesPerSecond() {
    // 2000 sectors in two seconds: 1000 sectors a second, 512000 bytes.
    var (_, delta) = TwoSamples(0, 2000, 0, 0, seconds: 2);
    var rates = delta.DiskRatesOf("sda");

    Assert.That(rates.ReadBytesPerSecond.Value, Is.EqualTo(512_000d).Within(1));
    Assert.That(rates.ReadOperationsPerSecond.Value, Is.EqualTo(1000d).Within(1));
  }

  /// <summary>
  /// Active time is milliseconds busy against wall-clock seconds: a device busy the whole time gains
  /// a thousand of them a second.
  /// </summary>
  [Test]
  public void ActiveTimeIsAPercentageOfTheInterval() {
    var (_, half) = TwoSamples(0, 0, 0, 1000, seconds: 2);
    Assert.That(half.DiskRatesOf("sda").BusyPercent.Value, Is.EqualTo(50d).Within(0.5));

    var (_, full) = TwoSamples(0, 0, 0, 2000, seconds: 2);
    Assert.That(full.DiskRatesOf("sda").BusyPercent.Value, Is.EqualTo(100d).Within(0.5));
  }

  /// <summary>
  /// A device cannot be busy for longer than the interval, so anything above 100 is the counter and
  /// the clock disagreeing rather than something worth showing.
  /// </summary>
  [Test]
  public void ActiveTimeIsClampedAtOneHundred() {
    var (_, delta) = TwoSamples(0, 0, 0, 5000, seconds: 2);
    Assert.That(delta.DiskRatesOf("sda").BusyPercent.Value, Is.EqualTo(100d));
  }

  [Test]
  public void NetworkRatesAreBytesPerSecond() {
    var (_, delta) = TwoSamples(0, 2000, 0, 0, seconds: 2);
    Assert.That(delta.NetworkRatesOf("eth0").ReceivedBytesPerSecond.Value, Is.EqualTo(1_000_000d).Within(1));
  }

  /// <summary>
  /// Devices are matched between samples by name. A disk appearing renumbers everything after it,
  /// and matching by position would attribute one device's traffic to another.
  /// </summary>
  [Test]
  public void ADeviceThatAppearsDoesNotInheritAnothersCounters() {
    var previous = new SystemSnapshot();
    previous.PrepareProcesses(0);
    var before = previous.PrepareDisks(1);
    before[0] = new() { Name = "sdb", ReadBytes = Counter.Of(0), BusyMilliseconds = Counter.Of(0) };

    var current = new SystemSnapshot();
    current.PrepareProcesses(0);
    var now = current.PrepareDisks(2);
    // The new disk sorts first, taking the index the old one had.
    now[0] = new() { Name = "sda", ReadBytes = Counter.Of(9_999_999), BusyMilliseconds = Counter.Of(0) };
    now[1] = new() { Name = "sdb", ReadBytes = Counter.Of(1024), BusyMilliseconds = Counter.Of(0) };

    previous.TimestampTicks = 0;
    current.TimestampTicks = System.Diagnostics.Stopwatch.Frequency;

    var delta = new SnapshotDelta();
    delta.Update(previous, current, CpuPercentMode.Normalized);

    Assert.That(delta.DiskRatesOf("sdb").ReadBytesPerSecond.Value, Is.EqualTo(1024d).Within(1));
    // Nothing to compare against, so no rate at all — not a rate against a stranger's counter.
    Assert.That(delta.DiskRatesOf("sda").ReadBytesPerSecond.HasValue, Is.False);
  }

  #endregion

  #region the page

  [Test]
  public void EachDeviceGetsItsOwnSection() {
    var (snapshot, delta) = TwoSamples(0, 2000, 0, 1000, seconds: 2);
    var sections = PerformanceReport.Build(new(), snapshot, delta);

    var titles = new List<string>();
    foreach (var section in sections)
      titles.Add(section.Title);

    Assert.That(titles, Does.Contain("Disk — sda"));
    Assert.That(titles, Does.Contain("Network — eth0"));
  }

  [Test]
  public void LoopbackIsNotListedBesideTheAdapters() {
    var (snapshot, delta) = TwoSamples(0, 2000, 0, 0, seconds: 2);
    var sections = PerformanceReport.Build(
      new(),
      snapshot,
      delta,
      describeInterface: name => new(name, null, Counter.NotSupported, "up", Counter.NotSupported, IsLoopback: true)
    );

    foreach (var section in sections)
      Assert.That(section.Title, Does.Not.StartWith("Network —"), "a machine talking to itself is not bandwidth");
  }

  [Test]
  public void ADiskWhoseMediaTypeIsUnknownIsNotCalledAHardDisk() {
    var (snapshot, delta) = TwoSamples(0, 0, 0, 0, seconds: 1);
    var sections = PerformanceReport.Build(
      new(),
      snapshot,
      delta,
      describeDisk: name => new(name, "Some Disk", null, Counter.Of(1024))
    );

    foreach (var section in sections)
      if (section.Title.StartsWith("Disk", StringComparison.Ordinal))
        foreach (var row in section.Rows)
          if (row.Label == "Media")
            Assert.That(row.Value, Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
  }

  #endregion

}
