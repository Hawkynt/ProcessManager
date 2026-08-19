using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// A process in a window of its own (PRD §26), and what happens to it when that process ends.
/// </summary>
[TestFixture]
public sealed class ProcessPropertiesWindowTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

  /// <param name="startTicks">Part of the identity: the same pid started twice is two processes.</param>
  private static (SystemSnapshot Snapshot, ProcessRow Row, ProcessKey Key) Machine(
    int pid = 4242,
    ulong startTicks = 100,
    string name = "editor"
  ) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(pid, startTicks);
    records[0].Name = name;
    records[0].UserName = "alice";
    records[0].PrivateBytes = Counter.Of(1024);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var row = new ProcessRow(records[0].Key);
    row.Update(in snapshot.Processes[0], delta, 0, Counter.NotSupported, currentUserId: 1000);
    return (snapshot, row, records[0].Key);
  }

  [Test]
  public void TheWindowIsTitledForItsProcess() {
    var (_, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    Assert.That(window.Text, Is.EqualTo("editor (4242)"));
    Assert.That(window.Key, Is.EqualTo(key));
    Assert.That(window.Ended, Is.False);
  }

  [Test]
  public void ItFollowsItsProcessWhileThatProcessLives() {
    var (snapshot, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    window.UpdateFromSample(snapshot, row);
    Assert.That(window.Ended, Is.False);
    Assert.That(window.Text, Is.EqualTo("editor (4242)"));
  }

  /// <summary>
  /// The window stays open and says so rather than closing under somebody who is reading it — the
  /// lists keep whatever they last held, which is usually why it was open (PRD §86).
  /// </summary>
  [Test]
  public void WhenTheProcessEndsTheWindowSaysSoAndStays() {
    var (_, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    var empty = new SystemSnapshot();
    empty.PrepareProcesses(0);
    window.UpdateFromSample(empty, null);

    Assert.That(window.Ended, Is.True);
    Assert.That(window.Text, Does.EndWith("— ended"));
  }

  /// <summary>
  /// The case the whole identity pair exists for: the pid is back, as somebody else's process. A
  /// window that followed the number would quietly start describing a stranger (PRD §72.2).
  /// </summary>
  [Test]
  public void ItDoesNotFollowARecycledPid() {
    var (_, row, key) = Machine(pid: 4242, startTicks: 100, name: "editor");
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    // Same pid, started later: a different process by every definition the engine uses.
    var (reused, otherRow, _) = Machine(pid: 4242, startTicks: 900, name: "something-else");
    window.UpdateFromSample(reused, otherRow);

    Assert.That(window.Ended, Is.True, "the original process is gone");
    Assert.That(window.Text, Does.StartWith("editor"), "and the window still names the one it was opened for");
  }

  [Test]
  public void OnceEndedItStopsAskingAboutThePid() {
    var (snapshot, row, key) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    var empty = new SystemSnapshot();
    empty.PrepareProcesses(0);
    window.UpdateFromSample(empty, null);

    // Even handed the process back, it stays ended: the pid may be anybody's by now.
    window.UpdateFromSample(snapshot, row);
    Assert.That(window.Ended, Is.True);
    Assert.That(window.Text, Does.EndWith("— ended"));
  }

}
