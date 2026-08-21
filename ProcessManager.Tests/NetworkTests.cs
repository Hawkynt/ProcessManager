using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The socket tables (PRD §40).
/// </summary>
/// <remarks>
/// Against recorded <c>/proc/net</c> files, so this runs on every CI leg and not only on the one
/// that has a <c>/proc</c> to read (PRD §9.2). The fixture is deliberately full of the awkward rows:
/// a listening socket whose queue columns are not byte counts, a socket in TIME_WAIT whose owner
/// column is a zero the kernel wrote, a Unix socket path with a space in it, and an IPv4 address
/// wearing an IPv6 socket.
/// </remarks>
[TestFixture]
public sealed class NetworkTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static string Net(string name) => File.ReadAllText(Path.Combine(FixtureRoot, "net", name));

  private static NetworkInterfaceMap Interfaces()
    => NetworkInterfaceMap.Parse(Net("route"), Net("if_inet6"));

  private static List<ConnectionRecord> Parse(string table, ConnectionProtocol protocol) {
    var into = new List<ConnectionRecord>();
    ProcNetParser.ParseInet(Net(table), protocol, Interfaces(), null, into);
    return into;
  }

  private static ConnectionRecord ByPort(List<ConnectionRecord> connections, int port) {
    foreach (var connection in connections)
      if (connection.LocalPort == port)
        return connection;

    Assert.Fail($"no socket on local port {port}");
    return default;
  }

  #region addresses

  /// <summary>
  /// The kernel writes an address as one 32-bit word per four bytes in its own byte order, so the
  /// octets arrive back to front. Getting this wrong reports 127.0.0.1 as 1.0.0.127 — a plausible
  /// address that is not the one the socket is bound to.
  /// </summary>
  [Test]
  public void AnAddressIsReadWordByWordAndNotByteByByte() {
    var connections = Parse("tcp", ConnectionProtocol.Tcp);

    Assert.That(ByPort(connections, 631).LocalAddress, Is.EqualTo("127.0.0.1"));
    Assert.That(ByPort(connections, 22).LocalAddress, Is.EqualTo("192.168.44.27"));
    Assert.That(ByPort(connections, 22).RemoteAddress, Is.EqualTo("140.82.121.4"));
    Assert.That(ByPort(connections, 22).RemotePort, Is.EqualTo(50001));
  }

  /// <summary>
  /// The same reversal over four words, plus the compression rules for an IPv6 address. The mapped
  /// form is what a dual-stack listener accepting an IPv4 connection looks like.
  /// </summary>
  [Test]
  public void AnIPv6AddressIsFormattedTheWayEverythingElseWritesIt() {
    var connections = Parse("tcp6", ConnectionProtocol.Tcp6);

    Assert.That(ByPort(connections, 631).LocalAddress, Is.EqualTo("::1"));
    Assert.That(ByPort(connections, 56706).LocalAddress, Is.EqualTo("::ffff:127.0.0.1"));
    Assert.That(ByPort(connections, 22).LocalAddress, Is.EqualTo("fe80::8c90:b483:c4:230a"));
  }

  /// <summary>The header line is not a socket, however many columns it happens to have.</summary>
  [Test]
  public void TheColumnTitlesAreNotReadAsASocket() {
    Assert.That(Parse("tcp", ConnectionProtocol.Tcp), Has.Count.EqualTo(4));
    Assert.That(Parse("udp", ConnectionProtocol.Udp), Has.Count.EqualTo(2));
  }

  #endregion

  #region what the kernel did not measure

  /// <summary>
  /// A listening socket's queue columns hold the Fast Open queue length and the accept backlog, not
  /// bytes. Reading them as bytes gives a number that is precise, plausible and wrong.
  /// </summary>
  [Test]
  public void AListeningSocketHasNoByteQueues() {
    var listener = ByPort(Parse("tcp", ConnectionProtocol.Tcp), 631);

    Assert.That(listener.State, Is.EqualTo("LISTEN"));
    Assert.That(listener.SendQueueBytes.HasValue, Is.False);
    Assert.That(listener.ReceiveQueueBytes.HasValue, Is.False);
    Assert.That(listener.SendQueueBytes.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// An established socket's queues are real, and are the pair that says which end of a stalled
  /// connection is the slow one.
  /// </summary>
  [Test]
  public void AnEstablishedSocketReportsBothQueuesAndItsRetransmits() {
    var established = ByPort(Parse("tcp", ConnectionProtocol.Tcp), 22);

    Assert.That(established.State, Is.EqualTo("ESTABLISHED"));
    Assert.That(established.SendQueueBytes.Value, Is.EqualTo(1234ul));
    Assert.That(established.ReceiveQueueBytes.Value, Is.EqualTo(3600ul));
    Assert.That(established.Retransmits.Value, Is.EqualTo(3ul));
  }

  /// <summary>
  /// A socket in TIME_WAIT has no socket structure left to ask, so the kernel writes zeros for the
  /// owner, both queues and the retransmit count. Passing those on would report the connection as
  /// root's, idle and healthy — three claims about something nobody measured (PRD §72.3).
  /// </summary>
  [Test]
  public void ATimeWaitSocketIsNotOwnedByRootAndHasNoQueues() {
    var lingering = ByPort(Parse("tcp", ConnectionProtocol.Tcp), 50002);

    Assert.That(lingering.State, Is.EqualTo("TIME_WAIT"));
    Assert.That(lingering.UserId, Is.EqualTo(-1));
    Assert.That(lingering.Inode, Is.Zero);
    Assert.That(lingering.SendQueueBytes.HasValue, Is.False);
    Assert.That(lingering.ReceiveQueueBytes.HasValue, Is.False);
    Assert.That(lingering.Retransmits.HasValue, Is.False);
  }

  /// <summary>
  /// UDP has a retransmission column and the kernel prints a literal zero in it for every datagram
  /// socket. Reading it would report "no retransmissions" for a protocol that never retransmits.
  /// </summary>
  [Test]
  public void ADatagramSocketHasNoRetransmissions() {
    foreach (var connection in Parse("udp", ConnectionProtocol.Udp))
      Assert.That(connection.Retransmits.HasValue, Is.False);
  }

  /// <summary>But a datagram socket's queues are real, and are what a dropped-packet hunt starts from.</summary>
  [Test]
  public void ADatagramSocketStillReportsItsQueues() {
    var multicast = ByPort(Parse("udp", ConnectionProtocol.Udp), 5353);

    Assert.That(multicast.SendQueueBytes.Value, Is.EqualTo(256ul));
    Assert.That(multicast.ReceiveQueueBytes.Value, Is.EqualTo(512ul));
  }

  #endregion

  #region states

  /// <summary>
  /// A UDP socket reuses the TCP state numbers for something else: an ordinary open one reads as
  /// TCP's CLOSE. Passing it through the TCP names would report every listening UDP socket on the
  /// machine as a dead connection (PRD §5.3).
  /// </summary>
  [Test]
  public void AnOpenDatagramSocketIsNotAClosedConnection() {
    var connections = Parse("udp", ConnectionProtocol.Udp);

    Assert.That(ByPort(connections, 5353).State, Is.EqualTo("UNCONNECTED"));
    Assert.That(ByPort(connections, 68).State, Is.EqualTo("ESTABLISHED"));
  }

  /// <summary>
  /// A listening Unix socket's state column says "unconnected", the same as one that was never
  /// bound: the listening bit is in the flags column instead. Reading only the state reports every
  /// server on the machine as idle.
  /// </summary>
  [Test]
  public void AListeningUnixSocketIsFoundInTheFlagsAndNotTheState() {
    var sockets = Unix();

    Assert.That(ByPath(sockets, "/run/user/1000/bus").State, Is.EqualTo("LISTEN"));
    Assert.That(ByPath(sockets, "/run/dbus/system_bus_socket").State, Is.EqualTo("CONNECTED"));
    Assert.That(ByPath(sockets, "/run/systemd/journal/socket").State, Is.EqualTo("UNCONNECTED"));
  }

  #endregion

  #region unix sockets

  private static List<ConnectionRecord> Unix() {
    var into = new List<ConnectionRecord>();
    ProcNetParser.ParseUnix(Net("unix"), null, into);
    return into;
  }

  private static ConnectionRecord ByPath(List<ConnectionRecord> sockets, string path) {
    foreach (var socket in sockets)
      if (socket.LocalAddress == path)
        return socket;

    Assert.Fail($"no Unix socket bound to {path}");
    return default;
  }

  /// <summary>
  /// A stream socket and a datagram socket on the same path are different endpoints, and the type
  /// column is the only thing that tells them apart.
  /// </summary>
  [Test]
  public void AUnixSocketCarriesWhatItDelivers() {
    var sockets = Unix();

    Assert.That(ByPath(sockets, "/run/user/1000/bus").Kind, Is.EqualTo(SocketKind.Stream));
    Assert.That(ByPath(sockets, "/run/systemd/journal/socket").Kind, Is.EqualTo(SocketKind.Datagram));
    Assert.That(ByPath(sockets, "/tmp/my socket").Kind, Is.EqualTo(SocketKind.SeqPacket));
  }

  /// <summary>
  /// The path runs to the end of the line and a filename may contain spaces, so splitting the last
  /// column on whitespace reports a socket bound to somewhere it is not.
  /// </summary>
  [Test]
  public void AUnixSocketPathWithASpaceInItSurvives() {
    Assert.That(ByPath(Unix(), "/tmp/my socket").LocalAddress, Is.EqualTo("/tmp/my socket"));
  }

  /// <summary>An abstract socket has no filesystem entry; the kernel writes its leading NUL as @.</summary>
  [Test]
  public void AnAbstractSocketKeepsItsAtSign() {
    Assert.That(ByPath(Unix(), "@/tmp/.X11-unix/X0").LocalAddress, Does.StartWith("@"));
  }

  /// <summary>
  /// Both halves of a socketpair are bound to nothing and are still real sockets, so the row has an
  /// empty path rather than being dropped.
  /// </summary>
  [Test]
  public void AnUnnamedSocketIsListedWithNoPath() {
    var unnamed = ByPath(Unix(), string.Empty);

    Assert.That(unnamed.Inode, Is.EqualTo(4249628ul));
    Assert.That(unnamed.State, Is.EqualTo("CONNECTED"));
  }

  /// <summary>
  /// This table carries no uid and no interface, and says so rather than reporting root and a card.
  /// </summary>
  [Test]
  public void AUnixSocketHasNoOwnerColumnAndNoInterface() {
    foreach (var socket in Unix()) {
      Assert.That(socket.UserId, Is.EqualTo(-1));
      Assert.That(socket.Interface, Is.Null);
      Assert.That(socket.SendQueueBytes.HasValue, Is.False);
    }
  }

  #endregion

  #region interfaces

  /// <summary>
  /// An address is on the interface whose on-link subnet contains it, and the longest prefix wins —
  /// the same rule the kernel picks a route by.
  /// </summary>
  [Test]
  public void AnAddressIsOnTheInterfaceWithTheLongestMatchingPrefix() {
    // 192.168.44.27 is inside eth1's /16 and inside eth0's /22; the /22 is the one it is on.
    Assert.That(ByPort(Parse("tcp", ConnectionProtocol.Tcp), 22).Interface, Is.EqualTo("eth0"));
  }

  /// <summary>
  /// The loopback net is not in the routing table at all — those routes live in the kernel's local
  /// table, which /proc does not publish — so it is answered by the rule that puts all of 127/8
  /// on lo.
  /// </summary>
  [Test]
  public void LoopbackIsNamedEvenThoughNoRouteClaimsIt() {
    Assert.That(ByPort(Parse("tcp", ConnectionProtocol.Tcp), 631).Interface, Is.EqualTo("lo"));
    Assert.That(ByPort(Parse("tcp6", ConnectionProtocol.Tcp6), 631).Interface, Is.EqualTo("lo"));
  }

  /// <summary>
  /// An IPv4 address wearing an IPv6 socket is resolved as the IPv4 address it is, because it is not
  /// configured on any interface as an IPv6 one and the v6 table would find nothing.
  /// </summary>
  [Test]
  public void AMappedIPv4AddressIsResolvedAsIPv4() {
    Assert.That(ByPort(Parse("tcp6", ConnectionProtocol.Tcp6), 56706).Interface, Is.EqualTo("lo"));
  }

  /// <summary>
  /// The two files disagree about byte order: if_inet6 prints the sixteen bytes straight through
  /// while the socket tables reverse each word, so comparing the text finds nothing.
  /// </summary>
  [Test]
  public void AnIPv6AddressIsMatchedAgainstIfInet6DespiteTheDifferentByteOrder() {
    Assert.That(ByPort(Parse("tcp6", ConnectionProtocol.Tcp6), 22).Interface, Is.EqualTo("eth0"));
  }

  /// <summary>A socket bound to the wildcard is on every interface, which is a different answer from "unknown".</summary>
  [Test]
  public void TheWildcardAddressIsOnAllOfThem() {
    Assert.That(ByPort(Parse("udp6", ConnectionProtocol.Udp6), 5353).Interface, Is.EqualTo("*"));
  }

  /// <summary>
  /// A multicast group is not an address on any interface. Unknown rather than a plausible guess:
  /// naming a card here would be inventing a fact (PRD §72.3).
  /// </summary>
  [Test]
  public void AnAddressNoRouteClaimsIsLeftUnknown() {
    Assert.That(ByPort(Parse("udp", ConnectionProtocol.Udp), 5353).Interface, Is.Null);
  }

  /// <summary>
  /// A route through a gateway says where to send traffic, not which card an address sits on, and
  /// the default route would otherwise claim every address on the machine.
  /// </summary>
  [Test]
  public void TheDefaultRouteDoesNotClaimEveryAddress() {
    var interfaces = NetworkInterfaceMap.Parse(
      "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n"
      + "eth0\t00000000\t012CA8C0\t0003\t0\t0\t600\t00000000\t0\t0\t0\n",
      string.Empty
    );

    Assert.That(interfaces.Resolve([93, 184, 216, 34]), Is.Null);
  }

  /// <summary>A machine with neither file answers nothing rather than throwing.</summary>
  [Test]
  public void AnEmptyMapAnswersNothing() {
    Assert.That(NetworkInterfaceMap.Empty.Resolve([93, 184, 216, 34]), Is.Null);
    Assert.That(NetworkInterfaceMap.Empty.Resolve([0, 0, 0, 0]), Is.EqualTo("*"));
    Assert.That(NetworkInterfaceMap.Empty.Resolve([]), Is.Null);
  }

  #endregion

  #region the join to a process

  /// <summary>
  /// The only join Linux offers between a process and its sockets: the network tables know nothing
  /// about processes and the descriptor knows nothing about addresses.
  /// </summary>
  [Test]
  public void ADescriptorNamesTheSocketItPointsAt() {
    Assert.That(ProcNetParser.TryParseSocketInode("socket:[3271551]", out var inode), Is.True);
    Assert.That(inode, Is.EqualTo(3271551ul));
  }

  /// <summary>
  /// The portable file access used to replay a recorded tree resolves a relative link target against
  /// its directory, so the same target arrives with a path in front of it on that leg.
  /// </summary>
  [Test]
  public void ADescriptorIsRecognisedWithOrWithoutItsDirectory() {
    Assert.That(ProcNetParser.TryParseSocketInode("/proc/1000/fd/socket:[42]", out var inode), Is.True);
    Assert.That(inode, Is.EqualTo(42ul));
  }

  /// <summary>
  /// An IPv6 address is full of colons, so an endpoint written without brackets cannot be read back:
  /// <c>fe80::1:22</c> is either port 22 or an address ending in 22, and nothing says which.
  /// </summary>
  [Test]
  public void AnIPv6EndpointIsBracketed() {
    var connections = Parse("tcp6", ConnectionProtocol.Tcp6);

    Assert.That(Humanize.LocalEndpoint(ByPort(connections, 22)), Is.EqualTo("[fe80::8c90:b483:c4:230a]:22"));
    Assert.That(Humanize.LocalEndpoint(ByPort(connections, 631)), Is.EqualTo("[::1]:631"));
    Assert.That(Humanize.LocalEndpoint(ByPort(Parse("tcp", ConnectionProtocol.Tcp), 631)), Is.EqualTo("127.0.0.1:631"));
  }

  /// <summary>
  /// A Unix socket has a path and no port, and must not be given a <c>:0</c> that reads as one.
  /// </summary>
  [Test]
  public void AUnixSocketEndpointIsItsPath() {
    Assert.That(Humanize.LocalEndpoint(ByPath(Unix(), "/tmp/my socket")), Is.EqualTo("/tmp/my socket"));
    Assert.That(Humanize.LocalEndpoint(ByPath(Unix(), string.Empty)), Is.EqualTo("<unnamed>"));
    Assert.That(Humanize.RemoteEndpoint(ByPath(Unix(), "/tmp/my socket")), Is.EqualTo("n/a"));
  }

  /// <summary>Everything else a descriptor may point at is not a socket, including the ones with brackets.</summary>
  [TestCase("pipe:[12345]")]
  [TestCase("anon_inode:[eventfd]")]
  [TestCase("/home/alice/report.txt")]
  [TestCase("socket:[]")]
  [TestCase("socket:[abc]")]
  public void SomethingThatIsNotASocketIsNotJoined(string target)
    => Assert.That(ProcNetParser.TryParseSocketInode(target, out _), Is.False);

  /// <summary>
  /// The per-process query asks for a handful of inodes out of a table with thousands of rows in it,
  /// so the filter has to happen while the table is read and not afterwards.
  /// </summary>
  [Test]
  public void OnlyTheAskedForSocketsComeBack() {
    var into = new List<ConnectionRecord>();
    ProcNetParser.ParseInet(Net("tcp"), ConnectionProtocol.Tcp, Interfaces(), new HashSet<ulong> { 21741 }, into);

    Assert.That(into, Has.Count.EqualTo(1));
    Assert.That(into[0].LocalPort, Is.EqualTo(631));
  }

  #endregion

  #region through the probe

  /// <summary>
  /// The whole path, from the files on disk to the records: the probe reads a directory, and the
  /// fixture is a directory (PRD §9.1).
  /// </summary>
  [Test]
  public void TheProbeReadsEveryTableInTheRecordedTree() {
    using var probe = new LinuxProbe(new LinuxProbeOptions {
      ProcRoot = FixtureRoot,
      PasswdPath = Path.Combine(FixtureRoot, "passwd"),
      // The fixture was recorded by somebody else, so the live uid would refuse every file in it.
      EffectiveUserId = 0,
      // The syscall path where there are syscalls to make, and the managed one everywhere else: the
      // Windows and macOS legs have no libc to call, and this must run on all three (PRD §9.2).
      UsePortableFileAccess = !OperatingSystem.IsLinux(),
    });

    var connections = probe.GetConnections();
    var protocols = new HashSet<ConnectionProtocol>();
    foreach (var connection in connections)
      protocols.Add(connection.Protocol);

    Assert.That(connections, Has.Count.EqualTo(16));
    Assert.That(protocols, Is.EquivalentTo(new[] {
      ConnectionProtocol.Tcp,
      ConnectionProtocol.Tcp6,
      ConnectionProtocol.Udp,
      ConnectionProtocol.Udp6,
      ConnectionProtocol.Unix,
    }));
  }

  /// <summary>
  /// A socket nothing readable holds is reported unowned rather than left out: "port 22 is listening
  /// and I may not see whose it is" and "nothing is listening on port 22" are different answers, and
  /// only one of them is true (PRD §5.3).
  /// </summary>
  [Test]
  public void ASocketWithNoVisibleOwnerIsStillListed() {
    using var probe = new LinuxProbe(new LinuxProbeOptions {
      ProcRoot = FixtureRoot,
      PasswdPath = Path.Combine(FixtureRoot, "passwd"),
      // The fixture was recorded by somebody else, so the live uid would refuse every file in it.
      EffectiveUserId = 0,
      // The syscall path where there are syscalls to make, and the managed one everywhere else: the
      // Windows and macOS legs have no libc to call, and this must run on all three (PRD §9.2).
      UsePortableFileAccess = !OperatingSystem.IsLinux(),
    });

    // The recorded tree holds files where a live one holds descriptor links, so nothing in it can be
    // attributed — which is exactly the shape of an unreadable process on a live machine.
    foreach (var connection in probe.GetConnections())
      Assert.That(connection.Pid, Is.Zero);
  }

  #endregion

}
