using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.App;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Asking for one process, and for one resource, from the command line (PRD §59, §102).
/// </summary>
/// <remarks>
/// The window and the terminal both have a detail view and a performance page; until these verbs
/// existed neither was reachable from a script or from a connection with nothing to draw into, which
/// is the gap §102 exists to close.
/// </remarks>
[TestFixture]
public sealed class ProcessVerbTests {

  private static readonly ProcessKey _Key = new(4242, 99);

  private static CommandLineOptions Parse(params string[] arguments)
    => CommandLineOptions.Parse(arguments, null);

  #region the process verb

  [Test]
  public void TheProcessVerbTakesAPid() {
    var options = Parse("--process", "1234");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.TargetPid, Is.EqualTo(1234));
    Assert.That(options.Mode.ToString(), Is.EqualTo("ProcessDetail"));
  }

  /// <summary>A pid on its own is the summary, which is what somebody typing one and stopping meant.</summary>
  [Test]
  public void WithoutAPageItIsTheSummary()
    => Assert.That(Parse("--process", "1234").DetailPage, Is.EqualTo(ProcessDetailPage.Overview));

  [TestCase("threads", ProcessDetailPage.Threads)]
  [TestCase("thread", ProcessDetailPage.Threads)]
  [TestCase("modules", ProcessDetailPage.Modules)]
  [TestCase("handles", ProcessDetailPage.Handles)]
  [TestCase("fds", ProcessDetailPage.Handles)]
  [TestCase("network", ProcessDetailPage.Network)]
  [TestCase("net", ProcessDetailPage.Network)]
  [TestCase("env", ProcessDetailPage.Environment)]
  public void ThePageIsTheSecondWord(string spelling, ProcessDetailPage expected) {
    var options = Parse("--process", "1234", spelling);

    Assert.That(options.Error, Is.Null);
    Assert.That(options.DetailPage, Is.EqualTo(expected));
  }

  /// <summary>
  /// A page nobody has says so and lists the ones there are, rather than falling back to the summary
  /// — which would answer a question that was not asked and look like it worked.
  /// </summary>
  [Test]
  public void APageThatDoesNotExistNamesTheOnesThatDo() {
    var error = Parse("--process", "1234", "sockets-and-things").Error;

    Assert.That(error, Does.Contain("sockets-and-things"));
    Assert.That(error, Does.Contain("threads"));
  }

  [Test]
  public void WithoutAPidItSaysWhatIsMissing()
    => Assert.That(Parse("--process").Error, Does.Contain("pid"));

  [Test]
  public void SomethingThatIsNotAPidIsNotOne()
    => Assert.That(Parse("--process", "the-busy-one").Error, Does.Contain("pid"));

  /// <summary>The page is optional, so a switch after the pid is still a switch of ours.</summary>
  [Test]
  public void ASwitchAfterThePidIsNotAPageName() {
    var options = Parse("--process", "1234", "--ascii");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.DetailPage, Is.EqualTo(ProcessDetailPage.Overview));
    Assert.That(options.AsciiOnly, Is.True);
  }

  [Test]
  public void TheProcessVerbIsInTheHelp()
    => Assert.That(CommandLineOptions.HelpText, Does.Contain("--process"));

  #endregion

  #region the performance verb

  /// <summary>
  /// <c>perf</c> with nothing after it is the processor, which is what the word means to everybody
  /// who types it.
  /// </summary>
  [Test]
  public void ThePerformanceVerbDefaultsToTheProcessor() {
    var options = Parse("--perf");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.Mode.ToString(), Is.EqualTo("Performance"));
    Assert.That(options.PerformanceResource, Is.EqualTo("cpu"));
  }

  [TestCase("--perf", "memory")]
  [TestCase("--performance", "gpu")]
  public void TheResourceIsTheNextWord(string spelling, string resource) {
    var options = Parse(spelling, resource);

    Assert.That(options.Error, Is.Null);
    Assert.That(options.PerformanceResource, Is.EqualTo(resource));
  }

  [Test]
  public void TheResourceCanBeWrittenInline()
    => Assert.That(Parse("--perf=disk").PerformanceResource, Is.EqualTo("disk"));

  /// <summary>
  /// And a switch after it is not a resource name. A verb whose optional argument ate the next flag
  /// would make <c>--perf --ascii</c> ask for a resource called <c>--ascii</c>.
  /// </summary>
  [Test]
  public void ASwitchAfterItIsNotAResource() {
    var options = Parse("--perf", "--ascii");

    Assert.That(options.Error, Is.Null);
    Assert.That(options.PerformanceResource, Is.EqualTo("cpu"));
    Assert.That(options.AsciiOnly, Is.True);
  }

  [Test]
  public void ThePerformanceVerbIsInTheHelp()
    => Assert.That(CommandLineOptions.HelpText, Does.Contain("--perf"));

  /// <summary>
  /// An interval is only this command line's when this command line gave one. The settings file's
  /// figure is how often a window redraws, and reading it as the spacing of a forty-sample plot
  /// would turn a four-second graph into a forty-second one on any machine with <c>interval=1</c>.
  /// </summary>
  [Test]
  public void AnIntervalIsOnlyStatedWhenItWasStated() {
    Assert.That(Parse("--perf").IntervalWasStated, Is.False);
    Assert.That(Parse("--perf", "cpu", "--interval", "2").IntervalWasStated, Is.True);
  }

  #endregion

  #region the tables behind them

  private sealed class StubProbe : ISystemProbe {

    public List<ThreadRecord> Threads { get; } = [];
    public List<ModuleRecord> Modules { get; } = [];
    public List<HandleRecord> Handles { get; } = [];
    public List<ConnectionRecord> Connections { get; } = [];
    public List<KeyValuePair<string, string>> Variables { get; } = [];

    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => this.Handles;
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => this.Modules;
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => this.Connections;
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => this.Threads;
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => this.Variables;
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }

  }

  private static StubProbe Populated() {
    var probe = new StubProbe();
    probe.Threads.Add(new(
      Tid: 4242,
      State: ProcessState.Running,
      CpuTimeNs: Counter.Of(1_000_000_000ul),
      StartTimeUtcTicks: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks,
      StartAddress: Counter.NotSupported,
      StartSymbol: null,
      Priority: 20,
      Name: "worker",
      UserTimeNs: Counter.Of(600_000_000ul),
      KernelTimeNs: Counter.Of(400_000_000ul),
      ContextSwitches: Counter.Of(12ul),
      LastCpu: 3,
      WaitReason: null,
      VoluntaryContextSwitches: Counter.Of(10ul),
      InvoluntaryContextSwitches: Counter.Of(2ul),
      BasePriority: 20,
      Policy: SchedulingPolicy.Other,
      Affinity: "0-15",
      StartModule: null,
      InstructionPointer: Counter.NotSupported,
      InstructionModule: null,
      StackPointer: Counter.NotSupported,
      StackBytes: Counter.NotSupported,
      Mode: ThreadMode.User,
      SyscallNumber: Counter.NotSupported,
      QueuedNs: Counter.NotSupported
    ));

    probe.Modules.Add(new(
      Path: "/usr/lib/libc.so.6",
      BaseAddress: 0x7F0000000000,
      Size: 0x21000,
      Permissions: "r-xp",
      EndAddress: 0x7F0000021000,
      ResidentBytes: Counter.Of(0x8000ul),
      FileOffset: Counter.Of(0ul),
      Inode: Counter.Of(4711ul),
      Device: "08:02",
      IsDeleted: false,
      MappingCount: 4,
      FileSizeBytes: Counter.Of(0x30000ul),
      FileModifiedUtcTicks: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks,
      Type: ModuleType.SharedObject,
      Architecture: "x86-64",
      EntryPoint: Counter.NotSupported,
      Soname: "libc.so.6",
      Interpreter: null,
      Mitigations: default,
      BuildId: null,
      LoadReason: ModuleLoadReason.Direct,
      LoadCount: 1,
      Runtime: ModuleRuntime.Native
    ));

    probe.Handles.Add(new(
      Handle: 3,
      Kind: HandleKind.File,
      Name: "/etc/hosts",
      Access: "r",
      Position: Counter.Of(0ul),
      OpenFlags: Counter.NotSupported,
      Inode: Counter.Of(99ul),
      TargetPid: Counter.NotSupported,
      MountId: Counter.NotSupported,
      Device: null,
      FileSystem: null,
      Detail: null,
      NodeType: FileNodeType.Regular,
      NodeDevice: null
    ));

    probe.Connections.Add(new(
      Protocol: ConnectionProtocol.Tcp,
      Kind: SocketKind.Stream,
      LocalAddress: "127.0.0.1",
      LocalPort: 8080,
      RemoteAddress: "0.0.0.0",
      RemotePort: 0,
      State: "LISTEN",
      Inode: 4242,
      Pid: 4242,
      UserId: 1000,
      UserName: "alice",
      Interface: "lo",
      SendQueueBytes: Counter.NotSupported,
      ReceiveQueueBytes: Counter.NotSupported,
      Retransmits: Counter.NotSupported,
      Statistics: SocketStatistics.NotSupported,
      SendRate: Rate.NotSampledYet,
      ReceiveRate: Rate.NotSampledYet,
      OwningService: null,
      ContainerPath: null,
      References: Counter.NotSupported
    ));

    probe.Variables.Add(new("PATH", "/usr/bin"));
    return probe;
  }

  private static ProcessRecord Subject() => new() { Key = _Key, Name = "stub" };

  /// <summary>
  /// Every row carries a cell for every heading. A page whose rows are shorter than its headers is
  /// one whose last column is silently blank, and the two front-ends that read these by index would
  /// each fail differently.
  /// </summary>
  [Test]
  public void EveryRowHasACellForEveryHeading() {
    var probe = Populated();
    var subject = Subject();
    foreach (var page in Enum.GetValues<ProcessDetailPage>()) {
      var table = ProcessDetailTables.Build(page, probe, _Key, in subject);

      Assert.That(table.Widths, Has.Count.EqualTo(table.Headers.Count), $"{page}: widths");
      Assert.That(table.Rows, Is.Not.Empty, $"{page}: nothing came back");
      foreach (var row in table.Rows)
        Assert.That(row, Has.Length.EqualTo(table.Headers.Count), $"{page}: {string.Join("|", row)}");
    }
  }

  /// <summary>
  /// And the terminal's detail view reads the same tables, so the page somebody sees over ssh and the
  /// page a script asks for cannot carry different columns (PRD §58).
  /// </summary>
  [Test]
  public void TheTerminalReadsTheSameTables() {
    var probe = Populated();
    var subject = Subject();
    var view = new DetailView(probe);
    view.Open(_Key);

    foreach (var tab in Enum.GetValues<DetailTab>()) {
      view.GoTo(tab);
      view.Collect(in subject);

      var table = ProcessDetailTables.Build(Enum.Parse<ProcessDetailPage>(tab.ToString()), probe, _Key, in subject);
      Assert.That(view.RowCount, Is.EqualTo(table.Rows.Count), tab.ToString());
    }
  }

  /// <summary>
  /// The two enumerations name the same pages. They are separate types — one is a keystroke in a
  /// terminal, the other a word on a command line — and a page added to one and not the other is a
  /// page reachable from exactly one front-end.
  /// </summary>
  [Test]
  public void TheTabsAndThePagesAreTheSameList()
    => Assert.That(Enum.GetNames<DetailTab>(), Is.EqualTo(Enum.GetNames<ProcessDetailPage>()));

  [Test]
  public void APageNameThatIsNotOneIsRefused()
    => Assert.That(ProcessDetailTables.TryParsePage("stacks", out _), Is.False);

  #endregion

}
