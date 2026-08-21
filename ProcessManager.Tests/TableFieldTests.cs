using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Fields the machine was already telling us and nothing was showing (PRD §14, §15, §16, §20).
/// </summary>
/// <remarks>
/// Every one of these was parsed or derivable before this; the work was surfacing it. Which is why
/// the tests are mostly about formatting and about the values that must not be invented.
/// </remarks>
[TestFixture]
public sealed class TableFieldTests {

  #region the controlling terminal

  /// <summary>
  /// The packing is the awkward part: minor is split across the low eight bits and bits 20–31, with
  /// major in between — how Linux has encoded <c>dev_t</c> since it ran out of minor numbers. A
  /// naive <c>(dev &gt;&gt; 8, dev &amp; 0xFF)</c> is right for small numbers and wrong for large ones.
  /// </summary>
  [Test]
  public void PseudoTerminalsAreNamedTheWayPsNamesThem() {
    // Major 136 is the pty range; these are the numbers /proc/[pid]/stat actually carries.
    Assert.That(Humanize.Terminal((136 << 8) | 1), Is.EqualTo("pts/1"));
    Assert.That(Humanize.Terminal((136 << 8) | 6), Is.EqualTo("pts/6"));
  }

  [Test]
  public void TheSplitMinorIsReassembled() {
    // Minor 300 does not fit in eight bits: 300 = 0x12C, so 0x2C goes low and 0x1 goes into bits 20+.
    var device = (136 << 8) | 0x2C | (0x1 << 20);

    Assert.That(Humanize.Terminal(device), Is.EqualTo("pts/300"));
  }

  [Test]
  public void ConsolesAndSerialLinesAreToldApart() {
    Assert.That(Humanize.Terminal((4 << 8) | 2), Is.EqualTo("tty2"));
    Assert.That(Humanize.Terminal((4 << 8) | 65), Is.EqualTo("ttyS1"));
  }

  /// <summary>
  /// Zero is not device 0:0 — it is the answer for every daemon and every service, and so for most
  /// of a machine's process table.
  /// </summary>
  [Test]
  public void NoControllingTerminalIsNotDeviceZero() {
    Assert.That(Humanize.Terminal(0), Is.EqualTo("—"));
    Assert.That(Humanize.Terminal(0), Does.Not.Contain("0:0"));
  }

  [Test]
  public void ADeviceNobodyKnowsIsReportedAsItsNumbers() =>
    Assert.That(Humanize.Terminal((250 << 8) | 7), Is.EqualTo("250:7"));

  #endregion

  #region the container id

  /// <summary>
  /// Every runtime writes its own cgroup shape and they all bury a long hexadecimal id somewhere,
  /// so the id is looked for rather than the layout — there is always another layout.
  /// </summary>
  [Test]
  public void EveryRuntimesLayoutYieldsTheSameId() {
    const string Id = "3f2b1a0c9d8e7f60514243343526271809aabbccddeeff00112233445566778899";

    foreach (var path in new[] {
      $"/docker/{Id}",
      $"/kubepods/besteffort/pod1234/docker-{Id}.scope",
      $"/system.slice/docker-{Id}.scope",
      $"/machine.slice/libpod-{Id}.scope",
    })
      Assert.That(Humanize.ContainerId(path), Is.EqualTo(Id[..12]), path);
  }

  /// <summary>
  /// A systemd slice is not a container, and a vte-spawn scope carries a UUID whose longest run of
  /// hex is twelve characters — reporting either as a container id would put a container column
  /// beside every process on an ordinary desktop.
  /// </summary>
  [Test]
  public void OrdinaryCgroupsAreNotContainers() {
    foreach (var path in new[] {
      "/user.slice/user-1000.slice/user@1000.service/app.slice/vte-spawn-63a2e373-e1be-4269-92e6-284c7c37b082.scope",
      "/system.slice/NetworkManager.service",
      "/init.scope",
      "/",
    })
      Assert.That(Humanize.ContainerId(path), Is.Null, path);
  }

  [Test]
  public void AProcessWithNoCgroupHasNoContainer() {
    Assert.That(Humanize.ContainerId(null), Is.Null);
    Assert.That(Humanize.ContainerId(string.Empty), Is.Null);
  }

  #endregion

  #region memory as a share of the machine

  private static SystemSnapshot Machine(ulong totalBytes, params ulong[] residents) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(residents.Length);
    for (var i = 0; i < residents.Length; ++i) {
      records[i] = default;
      records[i].Key = new(i + 1, 1000);
      records[i].Name = $"p{i}";
      records[i].WorkingSetBytes = Counter.Of(residents[i]);
    }

    snapshot.System.TotalMemoryBytes = totalBytes > 0 ? Counter.Of(totalBytes) : Counter.NotSupported;
    return snapshot;
  }

  [Test]
  public void MemoryPercentIsTheShareOfTheMachine() {
    var snapshot = Machine(1000, 250, 500);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(delta.MemoryPercent(0).Value, Is.EqualTo(25).Within(0.001));
    Assert.That(delta.MemoryPercent(1).Value, Is.EqualTo(50).Within(0.001));
  }

  /// <summary>
  /// Unlike every rate beside it this needs no previous sample, so it must be right on the first
  /// one — a column that is blank for a second on every start is a column people stop trusting.
  /// </summary>
  [Test]
  public void ItIsAnsweredOnTheVeryFirstSample() {
    var delta = new SnapshotDelta();
    delta.Update(null, Machine(1000, 100), CpuPercentMode.Normalized);

    Assert.That(delta.MemoryPercent(0).HasValue, Is.True);
  }

  /// <summary>
  /// A percentage of an unknown total is not a percentage. A machine that will not say how much
  /// memory it has must not report every process at nought (PRD §5.3).
  /// </summary>
  [Test]
  public void AMachineThatWillNotSayItsTotalYieldsNoPercentage() {
    var delta = new SnapshotDelta();
    delta.Update(null, Machine(0, 100), CpuPercentMode.Normalized);

    Assert.That(delta.MemoryPercent(0).HasValue, Is.False);
  }

  [Test]
  public void AProcessWhoseMemoryIsUnreadableYieldsNoPercentageEither() {
    var snapshot = Machine(1000, 100);
    var records = snapshot.PrepareProcesses(1);
    records[0].WorkingSetBytes = Counter.NotPermitted;
    snapshot.System.TotalMemoryBytes = Counter.Of(1000);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(delta.MemoryPercent(0).HasValue, Is.False);
    Assert.That(delta.MemoryPercent(0).Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  #endregion

  /// <summary>
  /// A field that cannot be sorted on is half a field. The rate-shaped ones go through a separate
  /// path in the comparer, and a new one added only to the text path renders and refuses to sort.
  /// </summary>
  [Test]
  public void EveryNewFieldSortsAsWellAsRenders() {
    var snapshot = Machine(1000, 250, 500);
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var view = new ProcessView { SortColumn = ProcessField.MemoryPercent, SortDescending = true, TreeMode = false };
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(2));
    Assert.That(snapshot.Processes[view.Rows[0].Index].Name, Is.EqualTo("p1"), "the larger share first");
  }

  [Test]
  public void EveryNewFieldIsInTheRegistryAndParsesFromItsKey() {
    foreach (var id in new[] {
      ProcessField.UniqueSet, ProcessField.MemoryPercent, ProcessField.Nice,
      ProcessField.Terminal, ProcessField.ExecutableName, ProcessField.ContainerId,
    }) {
      var descriptor = FieldRegistry.Get(id);
      Assert.That(descriptor.Key, Is.Not.Empty, id.ToString());
      Assert.That(FieldRegistry.TryParse(descriptor.Key, out var parsed), Is.True, descriptor.Key);
      Assert.That(parsed, Is.EqualTo(id), descriptor.Key);
    }
  }

  /// <summary>
  /// "uss" used to be an alias for the anonymous resident set, which is close to the unique set and
  /// is not it. Now that the real one is read from <c>smaps_rollup</c>, the alias has to name it.
  /// </summary>
  [Test]
  public void UssNamesTheUniqueSetAndNotTheAnonymousResidentSet() {
    Assert.That(FieldRegistry.TryParse("uss", out var parsed), Is.True);
    Assert.That(parsed, Is.EqualTo(ProcessField.UniqueSet));
  }

}
