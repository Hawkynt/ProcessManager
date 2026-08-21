using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The modules and descriptors tabs of the detail pane (PRD §31, §32).
/// </summary>
/// <remarks>
/// Each column reads its cell out of the row's tag array by index, so a row with fewer cells than
/// the list has columns throws while painting — in the window, on a machine with a display, long
/// after the tests went green. Both these lists grew columns in the middle of themselves, which is
/// the change that also silently moves every cell after it: the descriptor list finds its selected
/// row by parsing the second cell as a number, and a column inserted above that cell would hand
/// every action the wrong descriptor.
/// </remarks>
[TestFixture]
public sealed class DetailPaneResourceTests {

  private static readonly ProcessKey _Key = new(4242, 99);

  private sealed class StubProbe : ISystemProbe {

    public List<ModuleRecord> Modules { get; } = [];
    public List<HandleRecord> Handles { get; } = [];
    public List<ConnectionRecord> Connections { get; } = [];

    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => this.Handles;
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => this.Modules;
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => this.Connections;
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

  private static ModuleRecord Module(
    string path,
    ModuleRuntime runtime = ModuleRuntime.Native,
    int loads = 1,
    ModuleLoadReason reason = ModuleLoadReason.Direct
  ) => new(
    Path: path,
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
    Type: runtime == ModuleRuntime.Native ? ModuleType.SharedObject : ModuleType.Data,
    Architecture: "x86-64",
    EntryPoint: Counter.Of(0x7F0000001040ul),
    Soname: System.IO.Path.GetFileName(path),
    Interpreter: null,
    Mitigations: ImageMitigations.Read | ImageMitigations.PositionIndependent | ImageMitigations.NonExecutableStack,
    BuildId: "a0a1a2a3a4a5a6a7",
    LoadReason: reason,
    LoadCount: loads,
    Runtime: runtime
  );

  private static HandleRecord Handle(
    ulong fd,
    HandleKind kind,
    string? name,
    FileNodeType node = FileNodeType.Regular,
    string? nodeDevice = null,
    ulong inode = 4711
  ) => new(
    Handle: fd,
    Kind: kind,
    Name: name,
    Access: "rw",
    Position: Counter.Of(40ul),
    OpenFlags: Counter.Of(0x8002ul),
    Inode: Counter.Of(inode),
    TargetPid: Counter.NotSupported,
    MountId: Counter.Of(29ul),
    Device: "08:02",
    FileSystem: "ext4",
    Detail: null,
    NodeType: node,
    NodeDevice: nodeDevice
  );

  private static ConnectionRecord Socket(ulong inode, Counter references) => new(
    ConnectionProtocol.Tcp,
    SocketKind.Stream,
    "192.168.1.5",
    38658,
    "93.184.216.34",
    443,
    "ESTABLISHED",
    inode,
    4242,
    1000,
    "alice",
    "eth0",
    Counter.NotSupported,
    Counter.NotSupported,
    Counter.NotSupported,
    SocketStatistics.NotRead,
    Rate.NotSampledYet,
    Rate.NotSampledYet,
    null,
    null,
    references
  );

  private readonly List<DetailPane> _panes = [];

  [TearDown]
  public void CloseThePanes() {
    foreach (var pane in this._panes)
      pane.Dispose();

    this._panes.Clear();
  }

  private TreeListView Tab(StubProbe probe, string name) {
    var pane = new DetailPane(probe);
    this._panes.Add(pane);
    var tabs = (TabControl)pane.Control;
    pane.Select(_Key);

    for (var i = 0; i < tabs.TabPages.Count; ++i)
      if (tabs.TabPages[i].Text == name) {
        tabs.SelectedIndex = i;
        foreach (var control in tabs.TabPages[i].Controls)
          if (control is TreeListView list)
            return list;
      }

    Assert.Fail($"the pane has no {name} tab");
    return null!;
  }

  private static string Cell(TreeListView list, int row, string header) {
    for (var i = 0; i < list.Columns.Count; ++i)
      if (list.Columns[i].Text == header)
        return list.Columns[i].TextSelector!(list.Nodes[row]);

    Assert.Fail($"there is no '{header}' column");
    return string.Empty;
  }

  private static void EveryCellRenders(TreeListView list) {
    for (var row = 0; row < list.Nodes.Count; ++row)
      for (var column = 0; column < list.Columns.Count; ++column)
        Assert.That(
          list.Columns[column].TextSelector!(list.Nodes[row]),
          Is.Not.Null,
          $"row {row}, column '{list.Columns[column].Text}'"
        );
  }

  #region modules (PRD §31)

  [Test]
  public void EveryColumnOfEveryModuleRowRenders() {
    var probe = new StubProbe();
    probe.Modules.Add(Module("/usr/lib/libc.so.6"));
    probe.Modules.Add(Module("/opt/app/App.dll", ModuleRuntime.Managed, loads: 2, ModuleLoadReason.RunTime));

    var list = Tab(probe, "Modules");
    Assert.That(list.Nodes, Has.Count.EqualTo(2));
    EveryCellRenders(list);
  }

  /// <summary>
  /// The row a .NET process is full of. Before the runtime was read from the file's own header,
  /// every managed assembly in the list said <c>data</c> — the same word as a font.
  /// </summary>
  [Test]
  public void AManagedAssemblyIsNamedAsOneRatherThanAsData() {
    var probe = new StubProbe();
    probe.Modules.Add(Module("/opt/app/App.dll", ModuleRuntime.Managed, loads: 2, ModuleLoadReason.RunTime));

    var list = Tab(probe, "Modules");
    Assert.Multiple(() => {
      Assert.That(Cell(list, 0, "Runtime"), Is.EqualTo(".NET"));
      Assert.That(Cell(list, 0, "Type"), Is.EqualTo("data"), "the format is still what the format is");
      // The count that makes a second copy of a library visible, which is what .NET does to every
      // assembly it loads.
      Assert.That(Cell(list, 0, "Loads"), Is.EqualTo("2"));
    });
  }

  /// <summary>
  /// §31's ASLR and CFG boxes, in the cell that shows them, in the vocabulary <c>checksec</c> uses.
  /// </summary>
  [Test]
  public void TheHardeningColumnNamesWhatTheFileAsksFor() {
    var probe = new StubProbe();
    probe.Modules.Add(Module("/usr/lib/libc.so.6"));

    var list = Tab(probe, "Modules");
    Assert.That(Cell(list, 0, "Mitigations"), Does.Contain("PIE"));
    Assert.That(Cell(list, 0, "Mitigations"), Does.Contain("NX"));
    Assert.That(Cell(list, 0, "Load"), Is.EqualTo("linked"));
  }

  /// <summary>
  /// A row the graph never reached must not report a load count of nought, which reads as a library
  /// that is not loaded (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUncountedModuleShowsTheReasonAndNotAZero() {
    var probe = new StubProbe();
    probe.Modules.Add(Module("/usr/lib/libc.so.6", loads: 0, reason: ModuleLoadReason.Unknown));

    var list = Tab(probe, "Modules");
    Assert.Multiple(() => {
      Assert.That(Cell(list, 0, "Loads"), Is.Not.EqualTo("0"));
      Assert.That(Cell(list, 0, "Loads"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
      Assert.That(Cell(list, 0, "Load"), Is.EqualTo("—"));
    });
  }

  /// <summary>The modules menu offers §31's four actions and no fifth one that could only refuse.</summary>
  [Test]
  public void TheModulesMenuOffersWhatCanBeDoneWithAnImage() {
    var probe = new StubProbe();
    probe.Modules.Add(Module("/usr/lib/libc.so.6"));

    var items = ItemsOf(Tab(probe, "Modules"));
    Assert.Multiple(() => {
      Assert.That(items, Does.Contain("Copy path"));
      Assert.That(items, Does.Contain("Copy row"));
      Assert.That(items, Does.Contain("Open folder"));
      Assert.That(items, Does.Contain("File properties…"));
      // Unloading a shared object out of another process is not a thing Linux offers, and an item
      // that could only ever refuse is a lie dressed as a feature (PRD §25.6, §32).
      Assert.That(items, Has.None.Contains("Unload"));
    });
  }

  #endregion

  #region descriptors (PRD §32)

  [Test]
  public void EveryColumnOfEveryDescriptorRowRenders() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(3, HandleKind.File, "/home/alice/report.txt"));
    probe.Handles.Add(Handle(7, HandleKind.Device, "/dev/null", FileNodeType.CharacterDevice, "1:3"));
    probe.Handles.Add(Handle(9, HandleKind.Event, "anon_inode:[eventfd]", FileNodeType.None));
    probe.Handles.Add(Handle(11, HandleKind.Socket, "socket:[4712]", FileNodeType.Socket, inode: 4712));
    probe.Connections.Add(Socket(4712, Counter.Of(3ul)));

    var list = Tab(probe, "Handles");
    Assert.That(list.Nodes, Has.Count.EqualTo(4));
    EveryCellRenders(list);
  }

  /// <summary>
  /// §32's file type, which is a second axis and not a finer resource kind: the device a node
  /// <em>is</em> is not the device it is <em>on</em>, and both are columns.
  /// </summary>
  [Test]
  public void TheFileTypeColumnCarriesTheKernelsAnswerAndTheDeviceNumber() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(7, HandleKind.Device, "/dev/null", FileNodeType.CharacterDevice, "1:3"));

    var list = Tab(probe, "Handles");
    Assert.Multiple(() => {
      Assert.That(Cell(list, 0, "File type"), Is.EqualTo("character 1:3"));
      Assert.That(Cell(list, 0, "Device"), Is.EqualTo("08:02"), "the file system it is on, which is the other question");
    });
  }

  /// <summary>
  /// An anonymous inode has no file type. Its <c>st_mode</c> is <c>0600</c> with the type bits
  /// clear, and that nought must not be filed under a type it does not have (PRD §72.3).
  /// </summary>
  [Test]
  public void AnAnonymousInodeSaysItHasNoTypeAndNotThatNobodyLooked() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(9, HandleKind.Event, "anon_inode:[eventfd]", FileNodeType.None));
    probe.Handles.Add(Handle(10, HandleKind.File, "/relayed", FileNodeType.Unknown));

    var list = Tab(probe, "Handles");
    Assert.Multiple(() => {
      Assert.That(Cell(list, 0, "File type"), Is.EqualTo("no type"));
      Assert.That(Cell(list, 1, "File type"), Is.EqualTo("—"));
      Assert.That(Cell(list, 0, "File type"), Is.Not.EqualTo(Cell(list, 1, "File type")));
    });
  }

  /// <summary>
  /// §32's reference count. It is a socket's, and only a socket's: nothing else a process holds has
  /// a count any file under <c>/proc</c> publishes, so the rest say there is no such number rather
  /// than showing one.
  /// </summary>
  [Test]
  public void OnlyASocketHasAReferenceCountAndTheRestSayThereIsNone() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(11, HandleKind.Socket, "socket:[4712]", FileNodeType.Socket, inode: 4712));
    probe.Handles.Add(Handle(3, HandleKind.File, "/home/alice/report.txt"));
    probe.Connections.Add(Socket(4712, Counter.Of(3ul)));

    var list = Tab(probe, "Handles");
    Assert.Multiple(() => {
      Assert.That(Cell(list, 0, "Refs"), Is.EqualTo("3"));
      Assert.That(Cell(list, 1, "Refs"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
      Assert.That(Cell(list, 1, "Refs"), Is.Not.EqualTo("0"));
    });
  }

  /// <summary>
  /// A kernel that stopped short of the column, versus a socket with no holders. The second does
  /// not exist and must not be what an unread column looks like.
  /// </summary>
  [Test]
  public void AnUnreadReferenceCountIsNotAnUnheldSocket() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(11, HandleKind.Socket, "socket:[4712]", FileNodeType.Socket, inode: 4712));
    probe.Connections.Add(Socket(4712, Counter.NotSupported));

    var list = Tab(probe, "Handles");
    Assert.That(Cell(list, 0, "Refs"), Is.Not.EqualTo("0"));
  }

  /// <summary>
  /// The descriptor number is found by parsing the second cell, and the actions are handed whatever
  /// that parse returns — so a column inserted above it silently gives every action a different
  /// descriptor from the one under the pointer.
  /// </summary>
  [Test]
  public void TheDescriptorNumberIsStillTheSecondCell() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(3, HandleKind.File, "/home/alice/report.txt"));
    probe.Handles.Add(Handle(7, HandleKind.Device, "/dev/null", FileNodeType.CharacterDevice, "1:3"));

    var list = Tab(probe, "Handles");
    Assert.Multiple(() => {
      Assert.That(list.Columns[1].Text, Is.EqualTo("FD"));
      Assert.That(list.Columns[1].TextSelector!(list.Nodes[0]), Is.EqualTo("3"));
      Assert.That(list.Columns[1].TextSelector!(list.Nodes[1]), Is.EqualTo("7"));
    });
  }

  /// <summary>
  /// §32's five actions: four that can be done and one that cannot. Closing a descriptor in another
  /// process has no supported mechanism on Linux, and the item is absent rather than disabled.
  /// </summary>
  [Test]
  public void TheDescriptorMenuOffersTheFourActionsAndNotTheFifth() {
    var probe = new StubProbe();
    probe.Handles.Add(Handle(3, HandleKind.File, "/home/alice/report.txt"));

    var items = ItemsOf(Tab(probe, "Handles"));
    Assert.Multiple(() => {
      Assert.That(items, Does.Contain("Copy row"));
      Assert.That(items, Does.Contain("Copy all descriptors"));
      Assert.That(items, Does.Contain("Open folder"));
      Assert.That(items, Does.Contain("Resource properties…"));
      Assert.That(items, Does.Contain("Go to the process this names"));
      Assert.That(items, Has.None.Contains("Close"));
    });
  }

  #endregion

  private static List<string> ItemsOf(TreeListView list) {
    Assert.That(list.ContextMenuStrip, Is.Not.Null, "the list has no menu");
    var names = new List<string>();
    foreach (var item in list.ContextMenuStrip!.Items)
      names.Add(item.Text);

    return names;
  }

}
