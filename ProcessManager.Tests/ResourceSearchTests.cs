using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// "Which process is using this?" (PRD §33) — the search behind <c>--find</c>.
/// </summary>
/// <remarks>
/// Driven by a stub probe rather than by a recorded machine, because the interesting cases are the
/// expensive ones: descriptors, mappings and sockets, none of which a fixture directory can hold
/// without committing symlinks.
/// </remarks>
[TestFixture]
public sealed class ResourceSearchTests {

  /// <summary>A probe that answers from lists the test sets, and nothing else.</summary>
  private sealed class StubProbe : ISystemProbe {

    public Dictionary<int, List<HandleRecord>> Handles { get; } = [];
    public Dictionary<int, List<ModuleRecord>> Modules { get; } = [];
    public Dictionary<int, List<ConnectionRecord>> Connections { get; } = [];
    public List<ServiceRecord> Services { get; } = [];

    /// <summary>Counts what was asked for, so the "expensive only when needed" rule is testable.</summary>
    public int DeepReads { get; private set; }

    public string Description => "stub";

    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
      ++this.DeepReads;
      return this.Handles.TryGetValue(key.Pid, out var found) ? found : [];
    }

    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key)
      => this.Modules.TryGetValue(key.Pid, out var found) ? found : [];

    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key)
      => this.Connections.TryGetValue(key.Pid, out var found) ? found : [];

    public IReadOnlyList<ServiceRecord> GetServices() => this.Services;

    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }

  }

  private static SystemSnapshot Snapshot() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(3);

    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "nginx";
    records[0].UserName = "root";
    records[0].CommandLine = "/usr/sbin/nginx -g daemon off;";
    // Deliberately somewhere the command line does not mention, so "matched on its image path" is
    // a case that can actually happen.
    records[0].ImagePath = "/opt/vendor/sbin/nginx";

    records[1] = default;
    records[1].Key = new(200, 2);
    records[1].Name = "editor";
    records[1].UserName = "alice";
    records[1].CommandLine = "/usr/bin/editor";
    records[1].ImagePath = "/usr/bin/editor";

    records[2] = default;
    records[2].Key = new(300, 3);
    records[2].Name = "journald";
    records[2].UserName = "root";
    records[2].CommandLine = "/usr/lib/systemd/systemd-journald";
    records[2].ImagePath = "/usr/lib/systemd/systemd-journald";
    return snapshot;
  }

  private static (StubProbe Probe, SystemSnapshot Snapshot) Machine() {
    var probe = new StubProbe();
    // Only the fields the search reads are stated; everything else carries the reason it is not
    // there, because a stub that answers 0 where a probe would answer "unknown" tests the wrong
    // thing (PRD §72.3).
    probe.Handles[200] = [new(
      Handle: 42,
      Kind: HandleKind.File,
      Name: "/home/alice/report.txt",
      Access: "rw",
      Position: Counter.NotSampledYet,
      OpenFlags: Counter.NotSampledYet,
      Inode: Counter.NotSampledYet,
      TargetPid: Counter.NotSampledYet,
      MountId: Counter.NotSampledYet,
      Device: null,
      FileSystem: null,
      Detail: null
    )];
    probe.Modules[100] = [new(
      Path: "/usr/lib/libssl.so.3",
      BaseAddress: 0x1000,
      Size: 4096,
      Permissions: "r-xp",
      EndAddress: 0x2000,
      ResidentBytes: Counter.NotSampledYet,
      FileOffset: Counter.Of(0ul),
      Inode: Counter.Of(7ul),
      Device: "00:1e",
      IsDeleted: false,
      MappingCount: 1,
      FileSizeBytes: Counter.NotSampledYet,
      FileModifiedUtcTicks: 0,
      Type: ModuleType.SharedObject,
      Architecture: "x86-64",
      EntryPoint: Counter.NotSampledYet,
      Soname: "libssl.so.3",
      Interpreter: null,
      Mitigations: ImageMitigations.None,
      BuildId: null,
      LoadReason: ModuleLoadReason.Unknown
    )];
    probe.Connections[100] = [
      new(
        ConnectionProtocol.Tcp,
        SocketKind.Stream,
        "0.0.0.0",
        443,
        "10.0.0.5",
        51234,
        "ESTABLISHED",
        7,
        100,
        0,
        "root",
        "*",
        Counter.Of(0ul),
        Counter.Of(0ul),
        Counter.Of(0ul),
        SocketStatistics.NotRead,
        Rate.NotSampledYet,
        Rate.NotSampledYet,
        null,
        null
      ),
    ];
    probe.Services.Add(new("systemd-journald.service", "Journal Service", ServiceState.Running, true, false, 300, null, "/x", null));
    return (probe, Snapshot());
  }

  private static IReadOnlyList<ResourceMatch> Find(string pattern, bool deep = true) {
    var (probe, snapshot) = Machine();
    return ResourceSearch.Find(probe, snapshot, pattern, deep);
  }

  #region the cheap fields

  [Test]
  public void AProcessIsFoundByName() {
    var matches = Find("nginx");

    Assert.That(matches, Is.Not.Empty);
    Assert.That(matches[0].Pid, Is.EqualTo(100));
    Assert.That(matches[0].Kind, Is.EqualTo(ResourceKind.Name));
  }

  [Test]
  public void AProcessIsFoundByItsCommandLine() {
    var matches = Find("daemon off");

    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(matches[0].Kind, Is.EqualTo(ResourceKind.CommandLine));
    Assert.That(matches[0].Detail, Does.Contain("daemon off"));
  }

  /// <summary>
  /// Name, command line and image path are three spellings of one identity, and "nginx" is in all
  /// three. One reason per process is information; three is noise.
  /// </summary>
  [Test]
  public void OnlyOneIdentityReasonIsReportedPerProcess() {
    var byName = Find("nginx");

    Assert.That(byName, Has.Count.EqualTo(1));
    Assert.That(byName[0].Kind, Is.EqualTo(ResourceKind.Name), "the most specific one that answered");

    // Reachable only when neither the name nor the command line mentions it.
    var byPath = Find("/opt/vendor/");
    Assert.That(byPath, Has.Count.EqualTo(1));
    Assert.That(byPath[0].Kind, Is.EqualTo(ResourceKind.ImagePath));
  }

  [Test]
  public void SearchingIsCaseInsensitive() =>
    Assert.That(Find("NGINX"), Is.Not.Empty);

  #endregion

  #region the expensive ones

  [Test]
  public void AProcessIsFoundByAFileItHasOpen() {
    var matches = Find("report.txt");

    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(matches[0].Pid, Is.EqualTo(200), "the process holding the file");
    Assert.That(matches[0].Kind, Is.EqualTo(ResourceKind.OpenFile));
    Assert.That(matches[0].Detail, Is.EqualTo("/home/alice/report.txt"));
  }

  [Test]
  public void AProcessIsFoundByALibraryItHasMapped() {
    var matches = Find("libssl");

    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(matches[0].Kind, Is.EqualTo(ResourceKind.MappedModule));
  }

  [Test]
  public void AProcessIsFoundByAnEndpointOrAPort() {
    // "who is talking to 10.0.0.5" and "who is on 443" are the same question asked two ways.
    Assert.That(Find("10.0.0.5")[0].Kind, Is.EqualTo(ResourceKind.Socket));
    Assert.That(Find(":443")[0].Pid, Is.EqualTo(100));
  }

  [Test]
  public void AProcessIsFoundByTheServiceItBacks() {
    var matches = Find("systemd-journald.service");

    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(matches[0].Pid, Is.EqualTo(300));
    Assert.That(matches[0].Kind, Is.EqualTo(ResourceKind.Service));
  }

  /// <summary>
  /// Enumerating every descriptor and mapping of every process costs more than the rest of the
  /// program does in a second, so it runs only for processes the cheap fields did not answer for
  /// (PRD §5.4).
  /// </summary>
  [Test]
  public void TheExpensiveSearchIsSkippedForProcessesAlreadyMatched() {
    var (probe, snapshot) = Machine();
    ResourceSearch.Find(probe, snapshot, "nginx");

    // Three processes, one answered by its name — so the descriptors of two were read, not three.
    Assert.That(probe.DeepReads, Is.EqualTo(2));
  }

  [Test]
  public void TheExpensiveSearchCanBeTurnedOffAltogether() {
    var (probe, snapshot) = Machine();
    var matches = ResourceSearch.Find(probe, snapshot, "report.txt", deep: false);

    Assert.That(matches, Is.Empty);
    Assert.That(probe.DeepReads, Is.Zero);
  }

  #endregion

  #region patterns

  [Test]
  public void ASlashDelimitedPatternIsARegularExpression() {
    var matches = Find("/^ngin/");

    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(matches[0].ProcessName, Is.EqualTo("nginx"));
    // …and it really is anchored, rather than being searched for literally.
    Assert.That(Find("/^ginx/"), Is.Empty);
  }

  /// <summary>
  /// A pattern that will not compile is searched for literally, which is what somebody looking for a
  /// path with brackets in it meant anyway — better than an error they have to escape their way out
  /// of.
  /// </summary>
  [Test]
  public void APatternThatIsNotValidRegexIsSearchedForLiterally() =>
    Assert.That(() => Find("/[unclosed/"), Throws.Nothing);

  [Test]
  public void AnEmptyPatternMatchesNothingRatherThanEverything() {
    // Every process contains the empty string; returning all of them is never what was meant.
    Assert.That(Find(string.Empty), Is.Empty);
  }

  [Test]
  public void SomethingThatIsNowhereFindsNothing() =>
    Assert.That(Find("no-such-thing-anywhere"), Is.Empty);

  #endregion

  /// <summary>
  /// A process may exit between being listed and being asked about. That is ordinary, not an error
  /// worth abandoning a search for (PRD §73).
  /// </summary>
  [Test]
  public void AProbeThatThrowsForOneProcessDoesNotStopTheSearch() {
    var probe = new ThrowingProbe();
    var matches = ResourceSearch.Find(probe, Snapshot(), "nginx");

    Assert.That(matches, Has.Count.EqualTo(1));
  }

  private sealed class ThrowingProbe : StubProbeBase {
    public override IReadOnlyList<HandleRecord> GetHandles(ProcessKey key)
      => throw new IOException("the process went away");
  }

  private class StubProbeBase : ISystemProbe {
    public string Description => "throwing";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public virtual IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

}
