using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class ResourceReverseSearchTests {

  private sealed class StubProbe : ISystemProbe {

    public Dictionary<int, IReadOnlyList<HandleRecord>> Handles { get; } = [];
    public Dictionary<int, IReadOnlyList<ModuleRecord>> Modules { get; } = [];
    public HashSet<int> Denied { get; } = [];

    public string Description => "reverse-search stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;

    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
      this.Check(key);
      return this.Handles.TryGetValue(key.Pid, out var found) ? found : [];
    }

    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) {
      this.Check(key);
      return this.Modules.TryGetValue(key.Pid, out var found) ? found : [];
    }

    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) {
      this.Check(key);
      return [];
    }

    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }

    private void Check(ProcessKey key) {
      if (this.Denied.Contains(key.Pid))
        throw new UnauthorizedAccessException("not ours");
    }

  }

  private static SystemSnapshot Snapshot() {
    var snapshot = new SystemSnapshot();
    var rows = snapshot.PrepareProcesses(2);
    rows[0] = default;
    rows[0].Key = new(100, 10);
    rows[0].Name = "worker";
    rows[0].UserName = "alice";
    rows[0].CommandLine = "/opt/worker --cache /tmp/cache.db";
    rows[0].ImagePath = "/opt/worker";

    rows[1] = default;
    rows[1].Key = new(200, 20);
    rows[1].Name = "other";
    rows[1].UserName = "root";
    rows[1].CommandLine = "/opt/other";
    rows[1].ImagePath = "/opt/other";
    return snapshot;
  }

  private static HandleRecord Handle(ulong number, string name) => new(
    Handle: number,
    Kind: HandleKind.File,
    Name: name,
    Access: "rw",
    Position: Counter.NotSampledYet,
    OpenFlags: Counter.NotSampledYet,
    Inode: Counter.NotSampledYet,
    TargetPid: Counter.NotSampledYet,
    MountId: Counter.NotSampledYet,
    Device: null,
    FileSystem: null,
    Detail: null,
    NodeType: FileNodeType.Unknown,
    NodeDevice: null
  );

  private static ModuleRecord Module(string path, ModuleLoadReason reason, ModuleRuntime runtime) => new(
    Path: path,
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
    Type: reason == ModuleLoadReason.Data ? ModuleType.Unknown : ModuleType.SharedObject,
    Architecture: "x86-64",
    EntryPoint: Counter.NotSampledYet,
    Soname: null,
    Interpreter: null,
    Mitigations: ImageMitigations.None,
    BuildId: null,
    LoadReason: reason,
    LoadCount: 1,
    Runtime: runtime
  );

  [Test]
  public void EveryMatchingReferenceIsReturnedEvenWhenTheProcessIdentityAlsoMatches() {
    var probe = new StubProbe();
    probe.Handles[100] = [
      Handle(3, "/tmp/worker-state"),
      Handle(4, "/tmp/worker-state"),
    ];
    probe.Modules[100] = [Module("/opt/libworker.so", ModuleLoadReason.Direct, ModuleRuntime.Native)];

    var report = ResourceReverseSearch.Find(probe, Snapshot(), "worker");

    Assert.Multiple(() => {
      Assert.That(report.Matches.Count(match => match.Kind == ReverseResourceKind.Process), Is.EqualTo(1));
      Assert.That(report.Matches.Count(match => match.Kind == ReverseResourceKind.Handle), Is.EqualTo(2), "two opens are two references");
      Assert.That(report.Matches.Count(match => match.Kind == ReverseResourceKind.Module), Is.EqualTo(1));
      Assert.That(report.DeepScanned, Is.EqualTo(2));
      Assert.That(report.DeepAttempted, Is.EqualTo(2));
    });
  }

  [Test]
  public void CodeAndDataMappingsAreNotPresentedAsTheSameThing() {
    var probe = new StubProbe();
    probe.Modules[100] = [
      Module("/usr/lib/libssl.so.3", ModuleLoadReason.Direct, ModuleRuntime.Native),
      Module("/var/lib/app/cache.db", ModuleLoadReason.Data, ModuleRuntime.NotCode),
    ];

    Assert.Multiple(() => {
      Assert.That(
        ResourceReverseSearch.Find(probe, Snapshot(), "libssl").Matches.Single().Kind,
        Is.EqualTo(ReverseResourceKind.Module)
      );
      Assert.That(
        ResourceReverseSearch.Find(probe, Snapshot(), "cache.db").Matches.Single().Kind,
        Is.EqualTo(ReverseResourceKind.MappedFile)
      );
    });
  }

  [Test]
  public void APartialMachineScanSaysItWasPartial() {
    var probe = new StubProbe();
    probe.Denied.Add(200);
    probe.Handles[100] = [Handle(7, "/tmp/answer")];

    var report = ResourceReverseSearch.Find(probe, Snapshot(), "answer");

    Assert.Multiple(() => {
      Assert.That(report.Matches, Has.Count.EqualTo(1));
      Assert.That(report.DeepAttempted, Is.EqualTo(2));
      Assert.That(report.DeepScanned, Is.EqualTo(1));
      Assert.That(report.IsComplete, Is.False);
    });
  }

  [Test]
  public void ShallowSearchDoesNoDeepReadsAndHasNoFakeCoverage() {
    var probe = new StubProbe();
    probe.Denied.Add(100);
    probe.Denied.Add(200);

    var report = ResourceReverseSearch.Find(probe, Snapshot(), "worker", deep: false);

    Assert.Multiple(() => {
      Assert.That(report.Matches.Single().Kind, Is.EqualTo(ReverseResourceKind.Process));
      Assert.That(report.DeepAttempted, Is.Zero);
      Assert.That(report.DeepScanned, Is.Zero);
      Assert.That(report.IsComplete, Is.True);
    });
  }

}
