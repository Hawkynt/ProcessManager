using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The network tab of the detail pane (PRD §40).
/// </summary>
/// <remarks>
/// Every column reads its cell out of the row's tag array by index, so a row with fewer cells than
/// the list has columns throws while painting — in the window, on a machine with a display, long
/// after the tests went green. That is not hypothetical here: this tab grew ten columns at once.
/// Rendering every column of every row is what catches it.
/// <para>
/// The connections come from a stub rather than from a recorded tree, because a fixture's
/// descriptors are files where a live machine's are links and nothing in one can be attributed to a
/// process. The point of these tests is what the cells say once there is something to say it about
/// — and in particular what they say when there is not.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DetailPaneNetworkTests {

  private static readonly ProcessKey _Key = new(4242, 99);

  private sealed class StubProbe(params ConnectionRecord[] connections) : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => connections;
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

  private static ConnectionRecord Established => new(
    ConnectionProtocol.Tcp,
    SocketKind.Stream,
    "192.168.1.5",
    38658,
    "93.184.216.34",
    443,
    "ESTABLISHED",
    77,
    _Key.Pid,
    1000,
    "hawky",
    "wlp148s0",
    Counter.Of(0ul),
    Counter.Of(1024ul),
    Counter.Of(0ul),
    new(
      Counter.Of(43_985_955ul),
      Counter.Of(177_498ul),
      Counter.Of(31_099ul),
      Counter.Of(7_266ul),
      Counter.Of(60ul),
      Counter.Of(55_776ul),
      Counter.Of(54_587ul)
    ),
    Rate.NotSampledYet,
    Rate.NotSampledYet,
    "sshd.service",
    "/system.slice/sshd.service",
    Counter.NotSupported
  );

  /// <summary>
  /// A listening socket, whose queue columns are a backlog and whose <c>tcp_info</c> is a block of
  /// zeros the kernel never wrote to. Every one of its numbers is a placeholder.
  /// </summary>
  private static ConnectionRecord Listener => Established with {
    LocalPort = 22,
    RemoteAddress = string.Empty,
    RemotePort = 0,
    State = "LISTEN",
    Inode = 78,
    SendQueueBytes = Counter.NotSupported,
    ReceiveQueueBytes = Counter.NotSupported,
    Retransmits = Counter.NotSupported,
    Statistics = SocketStatistics.NotSupported,
  };

  /// <summary>
  /// The panes this fixture has made, so their name-lookup threads are stopped when it is done.
  /// </summary>
  private readonly List<DetailPane> _panes = [];

  [TearDown]
  public void CloseThePanes() {
    foreach (var pane in this._panes)
      pane.Dispose();

    this._panes.Clear();
  }

  private TreeListView Network(params ConnectionRecord[] connections) {
    var pane = new DetailPane(new StubProbe(connections));
    this._panes.Add(pane);
    var tabs = (TabControl)pane.Control;
    pane.Select(_Key);

    for (var i = 0; i < tabs.TabPages.Count; ++i)
      if (tabs.TabPages[i].Text == "Network") {
        // Setting the index raises the change the pane listens for, which is what fills the list.
        tabs.SelectedIndex = i;
        return (TreeListView)tabs.TabPages[i].Controls[0];
      }

    Assert.Fail("the pane has no network tab");
    return null!;
  }

  private static string Cell(TreeListView list, int row, string header) {
    for (var i = 0; i < list.Columns.Count; ++i)
      if (list.Columns[i].Text == header)
        return list.Columns[i].TextSelector!(list.Nodes[row]);

    Assert.Fail($"there is no '{header}' column");
    return string.Empty;
  }

  /// <summary>
  /// Every cell of every column, drawn. A row with fewer cells than the list has columns throws
  /// here rather than in front of somebody.
  /// </summary>
  [Test]
  public void EveryColumnOfEveryRowRenders() {
    var list = Network(Established, Listener);

    Assert.That(list.Nodes, Has.Count.EqualTo(2));
    Assert.That(list.Columns, Has.Count.EqualTo(20));
    for (var row = 0; row < list.Nodes.Count; ++row)
      for (var column = 0; column < list.Columns.Count; ++column)
        Assert.That(
          list.Columns[column].TextSelector!(list.Nodes[row]),
          Is.Not.Null,
          $"row {row}, column '{list.Columns[column].Text}'"
        );
  }

  /// <summary>The figures the socket diagnostics exist to provide, in the cells that show them.</summary>
  [Test]
  public void AConnectionShowsWhatTheDiagnosticsSaid() {
    var list = Network(Established);

    Assert.That(Cell(list, 0, "Local"), Is.EqualTo("192.168.1.5:38658"));
    Assert.That(Cell(list, 0, "Remote"), Is.EqualTo("93.184.216.34:443"));
    Assert.That(Cell(list, 0, "Sent"), Is.EqualTo("41.9M"));
    Assert.That(Cell(list, 0, "Received"), Is.EqualTo("173K"));
    Assert.That(Cell(list, 0, "Pkts out"), Is.EqualTo("31099"));
    Assert.That(Cell(list, 0, "Pkts in"), Is.EqualTo("7266"));
    Assert.That(Cell(list, 0, "RTT"), Is.EqualTo("55.776ms"));
    Assert.That(Cell(list, 0, "Retrans total"), Is.EqualTo("60"));
    Assert.That(Cell(list, 0, "Service"), Is.EqualTo("sshd.service"));
    Assert.That(Cell(list, 0, "Cgroup"), Is.EqualTo("/system.slice/sshd.service"));
  }

  /// <summary>
  /// The first fill of a tab has one reading of each socket, and a rate needs two. It says so rather
  /// than reporting the connection's whole lifetime of traffic as one interval's worth.
  /// </summary>
  [Test]
  public void TheFirstFillHasNoRatesYet() {
    var list = Network(Established);

    Assert.That(Cell(list, 0, "Send rate"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
    Assert.That(Cell(list, 0, "Recv rate"), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)));
  }

  /// <summary>
  /// A listening socket has none of these numbers, and the cells say so rather than showing a nought
  /// that would read as a measured idle connection (PRD §72.3).
  /// </summary>
  [Test]
  public void AListeningSocketShowsPlaceholdersAndNotZeroes() {
    var list = Network(Listener);

    var placeholder = Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);
    foreach (var column in new[] { "Send-Q", "Recv-Q", "Retrans", "Sent", "Received", "Pkts out", "Pkts in", "RTT", "Retrans total" })
      Assert.That(Cell(list, 0, column), Is.EqualTo(placeholder), column);

    Assert.That(Cell(list, 0, "Remote"), Is.EqualTo("—"), "a listener is talking to nobody");
  }

  /// <summary>
  /// An empty list and a list nobody was allowed to read look identical, so the empty case says
  /// which it is — with one cell per column, counted from the list rather than written out.
  /// </summary>
  [Test]
  public void AProcessWithNoSocketsSaysSoInEveryColumn() {
    var list = Network();

    Assert.That(list.Nodes, Has.Count.EqualTo(1));
    for (var column = 0; column < list.Columns.Count; ++column)
      Assert.That(list.Columns[column].TextSelector!(list.Nodes[0]), Is.Not.Null, list.Columns[column].Text);

    Assert.That(Cell(list, 0, "Protocol"), Does.Contain("nothing to show"));
  }

  /// <summary>
  /// Nothing is resolved until somebody asks. A reverse lookup tells whoever runs the resolver which
  /// addresses this machine is talking to, and that is not a disclosure to make on one's own
  /// initiative (PRD §40).
  /// </summary>
  [Test]
  public void AddressesAreNotResolvedUntilTheMenuAsks() {
    var list = Network(Established);

    Assert.That(Cell(list, 0, "Remote"), Is.EqualTo("93.184.216.34:443"), "the address, not a name");
  }

  #region searching for the far end (PRD §40)

  /// <summary>What the pane was told to start, and nothing else.</summary>
  private sealed class RecordingLauncher : IProcessActions {

    public List<LaunchRequest> Launched { get; } = [];

    public LaunchResult Launch(LaunchRequest request) {
      this.Launched.Add(request);
      return new(ActionResult.Ok, 0, default);
    }

    public ActionResult Terminate(ProcessKey key) => ActionResult.Ok;
    public ActionResult Suspend(ProcessKey key) => ActionResult.Ok;
    public ActionResult Resume(ProcessKey key) => ActionResult.Ok;
    public ActionResult SetPriority(ProcessKey key, int priority) => ActionResult.Ok;
    public ActionResult SetAffinity(ProcessKey key, ulong mask) => ActionResult.Ok;
    public ActionResult SendSignal(ProcessKey key, int signal) => ActionResult.Ok;
  }

  /// <summary>
  /// Finds the item by its label and clicks it, which is the only way to reach the handler behind it
  /// — and the way that also proves the item is on the menu at all.
  /// </summary>
  private static ToolStripMenuItem NetworkItem(TreeListView list, string startsWith) {
    var menu = list.ContextMenuStrip;
    Assert.That(menu, Is.Not.Null, "the network list has no menu");
    foreach (var item in menu!.Items)
      if (item is ToolStripMenuItem entry && entry.Text.StartsWith(startsWith, StringComparison.Ordinal))
        return entry;

    Assert.Fail($"no item on the network menu begins '{startsWith}'");
    return null!;
  }

  /// <summary>
  /// The item names where it is going. This is the one thing in the program that reaches the network,
  /// and §97's promise is that nothing goes out unasked — an item reading only "search online" would
  /// be collecting consent without saying to what.
  /// </summary>
  [Test]
  public void TheSearchItemNamesTheEngineItWillOpen() {
    var list = Network(Established);
    var labels = new List<string>();
    foreach (var item in list.ContextMenuStrip!.Items)
      if (item is ToolStripMenuItem entry)
        labels.Add(entry.Text);

    Assert.That(labels, Has.Some.Contains(DesktopOpen.SearchEngine));
  }

  [Test]
  public void SearchingTheFarEndOpensTheAddressWithoutItsPort() {
    var list = Network(Established);
    var actions = new RecordingLauncher();
    // The pane the fixture built has no actions; this is the same pane with a launcher on it.
    var pane = this._panes[^1];
    pane.Actions = actions;

    list.SelectedNode = list.Nodes[0];
    // What a right-click does before the menu appears. The toolkit will not open one without a
    // backend to open it on, so the state is set here and the item worked directly.
    pane.RefreshNetworkMenu();
    NetworkItem(list, "Search remote address").PerformClick();

    Assert.That(actions.Launched, Has.Count.EqualTo(1));
    var argument = actions.Launched[0].Arguments[0];
    Assert.That(argument, Does.Contain(DesktopOpen.SearchEngine));
    Assert.That(argument, Does.Contain("93.184.216.34"));
    // The port is noise in a search: the question is who the address belongs to, and ":443" only
    // narrows it to pages that happen to mention the port too.
    Assert.That(argument, Does.Not.Contain("443"));
  }

  /// <summary>
  /// A listening socket has no peer, so the item is greyed rather than shown and then apologising —
  /// and nothing is started even if it is worked anyway. The alternative, a search for the em dash
  /// the cell shows, is the failure mode this guards.
  /// </summary>
  [Test]
  public void ARowWithNoFarEndGreysTheItemAndStartsNothing() {
    var list = Network(Listener);
    var actions = new RecordingLauncher();
    var pane = this._panes[^1];
    pane.Actions = actions;

    list.SelectedNode = list.Nodes[0];
    pane.RefreshNetworkMenu();
    var item = NetworkItem(list, "Search remote address");

    Assert.That(item.Enabled, Is.False);
    item.PerformClick();
    Assert.That(actions.Launched, Is.Empty);
  }

  /// <summary>
  /// The host is taken out of the drawn cell rather than out of the record behind it, so the two have
  /// to agree — this is that agreement, over the endpoints the table actually writes.
  /// </summary>
  [Test]
  public void TheHostComesBackOutOfEveryEndpointTheTableWrites() {
    var list = Network(Established, Listener);

    Assert.That(Humanize.EndpointHost(Cell(list, 0, "Remote")), Is.EqualTo("93.184.216.34"));
    Assert.That(Humanize.EndpointHost(Cell(list, 0, "Local")), Is.EqualTo("192.168.1.5"));
    Assert.That(Humanize.EndpointHost(Cell(list, 1, "Remote")), Is.Null, "a listener has no peer");
  }

  #endregion

}
