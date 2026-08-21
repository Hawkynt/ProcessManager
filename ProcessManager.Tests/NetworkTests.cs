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

  /// <summary>
  /// The tables have no column for bytes, segments or round-trip time, and the two protocols are
  /// unanswerable for different reasons — which the rows have to say, because "we have not looked
  /// yet" and "there is no such thing" send a reader to two different places.
  /// </summary>
  /// <remarks>
  /// A TCP row waits for the socket diagnostics to be merged onto it. A UDP row waits for nothing:
  /// Linux keeps no byte total, no segment count and no round-trip time for a datagram socket, and
  /// <c>udp_diag</c> has none to give either (PRD §40, §72.3).
  /// </remarks>
  [Test]
  public void TheTablesLeaveTheDiagnosticsUnansweredForTheRightReason() {
    foreach (var connection in Parse("tcp", ConnectionProtocol.Tcp)) {
      Assert.That(connection.Statistics, Is.EqualTo(SocketStatistics.NotRead));
      Assert.That(connection.SendRate.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    }

    foreach (var connection in Parse("udp", ConnectionProtocol.Udp)) {
      Assert.That(connection.Statistics, Is.EqualTo(SocketStatistics.NotSupported));
      Assert.That(connection.ReceiveRate.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    }

    foreach (var connection in Unix())
      Assert.That(connection.Statistics, Is.EqualTo(SocketStatistics.NotSupported));
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

  /// <summary>
  /// A recorded tree is somebody else's machine, so the live kernel is not asked about its sockets.
  /// </summary>
  /// <remarks>
  /// The socket diagnostics answer about the machine running the test and know nothing of a fixture.
  /// Merging them onto recorded rows would put this machine's byte counts against another machine's
  /// connections whenever an inode happened to collide — and the whole point of a replay is that it
  /// gives the same answers everywhere (PRD §9.1).
  /// </remarks>
  [Test]
  public void ARecordedTreeIsNotAnnotatedWithThisMachinesSockets() {
    using var probe = new LinuxProbe(new LinuxProbeOptions {
      ProcRoot = FixtureRoot,
      PasswdPath = Path.Combine(FixtureRoot, "passwd"),
      EffectiveUserId = 0,
      UsePortableFileAccess = !OperatingSystem.IsLinux(),
    });

    foreach (var connection in probe.GetConnections()) {
      Assert.That(connection.Statistics.HasAny, Is.False, $"{connection.LocalPort} carried a live reading");
      Assert.That(connection.SendRate.HasValue, Is.False);
      Assert.That(connection.ReceiveRate.HasValue, Is.False);
    }
  }

  #endregion

  #region the socket diagnostics

  /// <summary>
  /// A netlink reply, built to the layout the running kernel's own headers describe.
  /// </summary>
  /// <remarks>
  /// Synthesised rather than recorded, for two reasons. A recording of this machine's sockets is a
  /// list of who it has been talking to, and that does not belong in a repository; and the awkward
  /// cases — an old kernel's short <c>tcp_info</c>, a truncated datagram, an error where an answer
  /// was expected — cannot be produced to order on a live machine anyway. The layout itself is not
  /// guessed: it was taken from <c>linux/inet_diag.h</c> and <c>linux/tcp.h</c> and then checked
  /// against <c>ss</c> on a live connection, field for field.
  /// </remarks>
  private static byte[] Message(ushort type, ReadOnlySpan<byte> body) {
    var message = new byte[16 + body.Length];
    BitConverter.TryWriteBytes(message.AsSpan(0), 16 + body.Length);
    BitConverter.TryWriteBytes(message.AsSpan(4), type);
    body.CopyTo(message.AsSpan(16));
    return message;
  }

  /// <summary><c>SOCK_DIAG_BY_FAMILY</c>: an answer describing one socket.</summary>
  private const ushort _Answer = 20;
  private const ushort _Done = 3;
  private const ushort _Error = 2;

  /// <summary>Where the fields this reads sit in <c>struct tcp_info</c>.</summary>
  private const int _RoundTripTime = 68;
  private const int _TotalRetransmits = 100;
  private const int _BytesReceived = 128;
  private const int _SegmentsOut = 136;
  private const int _SegmentsIn = 140;
  private const int _BytesSent = 200;

  /// <summary>The length of the structure a current kernel sends.</summary>
  private const int _TcpInfoLength = 280;

  /// <summary>
  /// One <c>inet_diag_msg</c>, optionally with a <c>tcp_info</c> attached.
  /// </summary>
  /// <param name="state">1 is ESTABLISHED and 10 is LISTEN.</param>
  /// <param name="infoLength">
  /// How many bytes of <c>tcp_info</c> the kernel sent, or -1 for no attribute at all. The structure
  /// grows with every release — it was 240 bytes not long ago and is 280 now — so a build talking to
  /// an older kernel gets a short one.
  /// </param>
  private static byte[] Socket(
    uint inode,
    byte state = 1,
    int infoLength = _TcpInfoLength,
    ulong bytesSent = 0,
    ulong bytesReceived = 0,
    uint segmentsOut = 0,
    uint segmentsIn = 0,
    uint totalRetransmits = 0,
    uint roundTripTime = 0
  ) {
    var attribute = infoLength < 0 ? 0 : 4 + ((infoLength + 3) & ~3);
    var body = new byte[72 + attribute];
    body[1] = state;
    BitConverter.TryWriteBytes(body.AsSpan(68), inode);
    if (infoLength < 0)
      return body;

    BitConverter.TryWriteBytes(body.AsSpan(72), (ushort)(4 + infoLength));
    BitConverter.TryWriteBytes(body.AsSpan(74), (ushort)2);        // INET_DIAG_INFO
    var info = body.AsSpan(76, infoLength);
    Write64(info, _BytesSent, bytesSent);
    Write64(info, _BytesReceived, bytesReceived);
    Write32(info, _SegmentsOut, segmentsOut);
    Write32(info, _SegmentsIn, segmentsIn);
    Write32(info, _TotalRetransmits, totalRetransmits);
    Write32(info, _RoundTripTime, roundTripTime);
    return body;

    static void Write32(Span<byte> into, int offset, uint value) {
      if (into.Length >= offset + 4)
        BitConverter.TryWriteBytes(into[offset..], value);
    }

    static void Write64(Span<byte> into, int offset, ulong value) {
      if (into.Length >= offset + 8)
        BitConverter.TryWriteBytes(into[offset..], value);
    }
  }

  private static byte[] Datagram(params byte[][] messages) {
    var total = 0;
    foreach (var message in messages)
      total += message.Length;

    var buffer = new byte[total];
    var offset = 0;
    foreach (var message in messages) {
      message.CopyTo(buffer.AsSpan(offset));
      offset += message.Length;
    }

    return buffer;
  }

  private static Dictionary<ulong, SocketStatistics> Parse(byte[] datagram, out bool finished) {
    var into = new Dictionary<ulong, SocketStatistics>();
    Assert.That(InetDiagParser.Parse(datagram, into, out finished, out var error), Is.True);
    Assert.That(error, Is.Zero);
    return into;
  }

  /// <summary>
  /// The request is what the kernel rejects or answers, and every byte of it has to be in the right
  /// place: a wrong family or an unset state mask comes back as EINVAL rather than as no sockets.
  /// </summary>
  [Test]
  public void TheRequestIsTheShapeTheKernelExpects() {
    Span<byte> request = stackalloc byte[InetDiagParser.RequestLength];
    var written = InetDiagParser.BuildRequest(request, 2, 6, InetDiagParser.ExtensionInfo, InetDiagParser.AllStates, 7);

    Assert.That(written, Is.EqualTo(72), "sixteen bytes of netlink header and fifty-six of inet_diag_req_v2");
    Assert.That(BitConverter.ToUInt32(request[..4]), Is.EqualTo(72u), "the length the kernel reads first");
    Assert.That(BitConverter.ToUInt16(request.Slice(4, 2)), Is.EqualTo(InetDiagParser.SockDiagByFamily));
    Assert.That(BitConverter.ToUInt16(request.Slice(6, 2)), Is.EqualTo(0x301), "request and dump");
    Assert.That(BitConverter.ToUInt32(request.Slice(8, 4)), Is.EqualTo(7u), "the sequence, echoed back");
    Assert.That(request[16], Is.EqualTo(2), "AF_INET");
    Assert.That(request[17], Is.EqualTo(6), "IPPROTO_TCP");
    Assert.That(request[18], Is.EqualTo(2), "the extension bitmask asks for attribute 2 by setting bit 1");
    Assert.That(BitConverter.ToUInt32(request.Slice(20, 4)), Is.EqualTo(0xFFFEu), "every state, and never bit 0");
  }

  /// <summary>
  /// The four figures <c>/proc/net/tcp</c> has no column for. These are the whole reason for opening
  /// a netlink socket at all.
  /// </summary>
  [Test]
  public void AConnectionCarriesItsBytesSegmentsAndRoundTripTime() {
    var sockets = Parse(
      Datagram(
        Message(_Answer, Socket(
          inode: 4242,
          bytesSent: 43_985_955,
          bytesReceived: 177_498,
          segmentsOut: 31_099,
          segmentsIn: 7_266,
          totalRetransmits: 60,
          roundTripTime: 55_776
        )),
        Message(_Done, [])
      ),
      out var finished
    );

    Assert.That(finished, Is.True);
    var statistics = sockets[4242];
    Assert.That(statistics.BytesSent.Value, Is.EqualTo(43_985_955ul));
    Assert.That(statistics.BytesReceived.Value, Is.EqualTo(177_498ul));
    Assert.That(statistics.PacketsSent.Value, Is.EqualTo(31_099ul));
    Assert.That(statistics.PacketsReceived.Value, Is.EqualTo(7_266ul));
    Assert.That(statistics.TotalRetransmits.Value, Is.EqualTo(60ul));
    Assert.That(statistics.RoundTripTimeMicroseconds.Value, Is.EqualTo(55_776ul));
  }

  /// <summary>
  /// A listening socket's <c>tcp_info</c> is a block of zeros the kernel never wrote to.
  /// </summary>
  /// <remarks>
  /// <c>tcp_get_info</c> clears the structure, fills in the four fields that mean anything for a
  /// listener — none of which is read here — and returns. Passing the rest on would say a listening
  /// socket has moved no bytes, sent no segments, never retransmitted and has a round-trip time of
  /// nought, about something nobody measured. It is the same trap the queue columns of
  /// <c>/proc/net/tcp</c> set (PRD §72.3).
  /// </remarks>
  [Test]
  public void AListeningSocketsStatisticsAreZerosAndNotReadings() {
    var sockets = Parse(Datagram(Message(_Answer, Socket(inode: 99, state: 10))), out _);

    var statistics = sockets[99];
    Assert.That(statistics.HasAny, Is.False);
    Assert.That(statistics.BytesSent.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(statistics.RoundTripTimeMicroseconds.HasValue, Is.False);
    Assert.That(statistics.TotalRetransmits.HasValue, Is.False);
  }

  /// <summary>
  /// A round-trip time of zero is a connection that has never measured one, not a connection with no
  /// latency.
  /// </summary>
  /// <remarks>
  /// The kernel starts the smoothed figure at zero and clamps every sample it takes to at least a
  /// microsecond, so anything that has measured anything reports at least 1. A socket still in
  /// <c>SYN_SENT</c> is the ordinary way to see the zero, and reporting it as a latency would make
  /// the connection that has not connected yet the fastest one on the machine.
  /// </remarks>
  [Test]
  public void AnUnmeasuredRoundTripTimeIsNotZeroLatency() {
    var sockets = Parse(Datagram(Message(_Answer, Socket(inode: 5, state: 2, bytesSent: 1, roundTripTime: 0))), out _);

    var statistics = sockets[5];
    Assert.That(statistics.RoundTripTimeMicroseconds.HasValue, Is.False);
    Assert.That(statistics.RoundTripTimeMicroseconds.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    // And the rest of the structure is still read: only the latency was missing.
    Assert.That(statistics.BytesSent.Value, Is.EqualTo(1ul));
  }

  /// <summary>
  /// <c>tcp_info</c> grows with every kernel release, and a field past the end of what arrived is a
  /// field this kernel does not have — not a zero.
  /// </summary>
  /// <remarks>
  /// It was 240 bytes a few releases ago and is 280 now. A kernel without <c>tcpi_bytes_sent</c> has
  /// not said the connection sent nothing; it has said nothing at all, and the two must not render
  /// the same (PRD §72.3).
  /// </remarks>
  [Test]
  public void AnOlderKernelsShorterStructureIsShortAndNotEmpty() {
    // 144 bytes reaches segs_in and stops before notsent_bytes — and well before bytes_sent at 200.
    var sockets = Parse(
      Datagram(Message(_Answer, Socket(
        inode: 11,
        infoLength: 144,
        bytesReceived: 4096,
        segmentsOut: 9,
        segmentsIn: 11,
        roundTripTime: 158
      ))),
      out _
    );

    var statistics = sockets[11];
    Assert.That(statistics.BytesReceived.Value, Is.EqualTo(4096ul), "at 128, inside what arrived");
    Assert.That(statistics.PacketsSent.Value, Is.EqualTo(9ul));
    Assert.That(statistics.PacketsReceived.Value, Is.EqualTo(11ul));
    Assert.That(statistics.RoundTripTimeMicroseconds.Value, Is.EqualTo(158ul));
    Assert.That(statistics.BytesSent.HasValue, Is.False, "at 200, past the end of this kernel's structure");
    Assert.That(statistics.BytesSent.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// A reply with no <c>tcp_info</c> on it is the ordinary answer for a datagram socket, and it says
  /// so rather than reporting a connection that has moved nothing.
  /// </summary>
  [Test]
  public void ASocketWithNoInfoAttachedIsUnknownAndNotEmpty() {
    var sockets = Parse(Datagram(Message(_Answer, Socket(inode: 21, infoLength: -1))), out _);

    Assert.That(sockets[21].HasAny, Is.False);
    Assert.That(sockets[21], Is.EqualTo(SocketStatistics.NotSupported));
  }

  /// <summary>
  /// An inode of zero is no inode: no descriptor refers to the socket, so nothing can join it to a
  /// process, and several such sockets in one dump would all land on the same key.
  /// </summary>
  [Test]
  public void ASocketWithNoInodeIsNotKeyedOnZero() {
    var sockets = Parse(
      Datagram(Message(_Answer, Socket(inode: 0, bytesSent: 5)), Message(_Answer, Socket(inode: 0, bytesSent: 9))),
      out _
    );

    Assert.That(sockets, Is.Empty);
  }

  /// <summary>The kernel's own end-of-dump marker, which is what stops the read loop.</summary>
  [Test]
  public void ADumpEndsWhenTheKernelSaysItHas() {
    Parse(Datagram(Message(_Answer, Socket(inode: 1))), out var unfinished);
    Assert.That(unfinished, Is.False, "more datagrams to come");

    Parse(Datagram(Message(_Done, [])), out var finished);
    Assert.That(finished, Is.True);
  }

  /// <summary>
  /// An error where an answer was expected. The payload is a negated errno, and it ends the dump
  /// just as firmly as a done does.
  /// </summary>
  [Test]
  public void AnErrorIsReportedRatherThanReadAsASocket() {
    var body = new byte[4];
    BitConverter.TryWriteBytes(body.AsSpan(), -1);        // -EPERM, the way netlink writes it
    var into = new Dictionary<ulong, SocketStatistics>();

    Assert.That(InetDiagParser.Parse(Message(_Error, body), into, out var finished, out var code), Is.False);
    Assert.That(finished, Is.True);
    Assert.That(code, Is.EqualTo(1));
    Assert.That(into, Is.Empty);
  }

  /// <summary>
  /// A datagram cut short is not walked past its end. A length longer than what arrived would
  /// otherwise send the walk on to read a message header out of the middle of somebody's payload.
  /// </summary>
  [Test]
  public void ATruncatedDatagramStopsRatherThanReadingPastIt() {
    var first = Message(_Answer, Socket(inode: 3, bytesSent: 77));
    var whole = Datagram(first, Message(_Answer, Socket(inode: 4, bytesSent: 88)), Message(_Done, []));

    // Cut inside the second message, so its header is readable and claims more than arrived. That is
    // the case the length check exists for: walking on from it would read the next header out of the
    // middle of this one's payload.
    var sockets = Parse(whole.AsSpan(0, first.Length + 100).ToArray(), out var finished);

    Assert.That(finished, Is.False, "the done marker was in the part that did not arrive");
    Assert.That(sockets[3].BytesSent.Value, Is.EqualTo(77ul), "the message that did arrive whole is still read");
    Assert.That(sockets.ContainsKey(4), Is.False, "and the one that did not is not half-read");
  }

  #endregion

  #region the owning service

  /// <summary>
  /// A systemd unit is a cgroup, so the unit a socket's holder belongs to is read off its cgroup
  /// path and nowhere else.
  /// </summary>
  [Test]
  public void AUnitIsReadOffTheCgroupPath() {
    Assert.That(CgroupUnit.Of("/system.slice/sshd.service"), Is.EqualTo("sshd.service"));
    Assert.That(CgroupUnit.Of("/system.slice/system-getty.slice/getty@tty1.service"), Is.EqualTo("getty@tty1.service"));
    Assert.That(CgroupUnit.Of("/user.slice/user-1000.slice/session-2.scope"), Is.EqualTo("session-2.scope"));
    Assert.That(CgroupUnit.Of("/system.slice/sshd.socket"), Is.EqualTo("sshd.socket"));
  }

  /// <summary>
  /// The innermost unit wins. A desktop application sits inside its user's session manager, which is
  /// itself a unit — naming the outer one would report every program a user has started as belonging
  /// to the manager that started it.
  /// </summary>
  [Test]
  public void TheInnermostUnitIsTheOwner() {
    Assert.That(
      CgroupUnit.Of("/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope"),
      Is.EqualTo("app-firefox.scope")
    );
  }

  /// <summary>
  /// A slice holds no processes of its own — it only groups other units — so reporting one as the
  /// owner of a socket would name a container rather than an owner. A path with no unit in it says
  /// so rather than picking the nearest thing that looks like one.
  /// </summary>
  [Test]
  public void ACgroupThatIsNotAUnitHasNoOwningService() {
    Assert.That(CgroupUnit.Of("/system.slice"), Is.Null);
    Assert.That(CgroupUnit.Of("/user.slice/user-1000.slice"), Is.Null);
    Assert.That(CgroupUnit.Of("/"), Is.Null);
    Assert.That(CgroupUnit.Of(""), Is.Null);
    Assert.That(CgroupUnit.Of(null), Is.Null);
    Assert.That(CgroupUnit.Of("/docker/3f2a9b8c1d4e"), Is.Null, "a container runtime's layout is not systemd's");
  }

  #endregion

  #region live sockets

  /// <summary>
  /// The whole path against a connection this test made itself, so the expected answer is arranged
  /// rather than guessed at (PRD §9).
  /// </summary>
  /// <remarks>
  /// Linux only, and skipped elsewhere rather than failed: there is no <c>NETLINK_SOCK_DIAG</c> to
  /// open on the other two legs. A kernel built without the diagnostics, or a sandbox that refuses
  /// the netlink family, is also a skip — the point of the test is the arithmetic on the reply, and
  /// there is no reply to do arithmetic on.
  /// </remarks>
  [Test]
  public void AnArrangedConnectionReportsTheBytesItWasGiven() {
    // Split rather than guarded in place: the analyzer reads an `if` on the platform as a guard and
    // does not know that Assert.Ignore never returns, so the body has to sit behind one.
    if (!OperatingSystem.IsLinux()) {
      Assert.Ignore("the socket diagnostics are a Linux netlink family");
      return;
    }

    ArrangeAConnectionAndReadItBack();
  }

  [System.Runtime.Versioning.SupportedOSPlatform("linux")]
  private static void ArrangeAConnectionAndReadItBack() {
    const int Payload = 200_000;
    using var listener = new System.Net.Sockets.Socket(
      System.Net.Sockets.AddressFamily.InterNetwork,
      System.Net.Sockets.SocketType.Stream,
      System.Net.Sockets.ProtocolType.Tcp
    );

    listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    listener.Listen(1);
    using var client = new System.Net.Sockets.Socket(
      System.Net.Sockets.AddressFamily.InterNetwork,
      System.Net.Sockets.SocketType.Stream,
      System.Net.Sockets.ProtocolType.Tcp
    );

    client.Connect(listener.LocalEndPoint!);
    using var accepted = listener.Accept();
    client.Send(new byte[Payload]);

    // Read it all, so that the bytes are not merely queued: the counter this checks is what the
    // connection put on the wire, and a send that is still sitting in a buffer has not.
    var drained = 0;
    var buffer = new byte[65536];
    while (drained < Payload)
      drained += accepted.Receive(buffer);

    var statistics = new Dictionary<ulong, SocketStatistics>();
    if (!Platform.Linux.InetDiagReader.TryRead(statistics, out var reason))
      Assert.Ignore($"this kernel would not answer the socket diagnostics: {reason}");

    var inode = InodeOf(client);
    Assert.That(statistics.ContainsKey(inode), Is.True, "the dump did not describe the socket this test opened");

    var arranged = statistics[inode];
    Assert.That(arranged.BytesSent.Value, Is.EqualTo((ulong)Payload));
    Assert.That(arranged.BytesReceived.Value, Is.Zero, "the peer sent nothing back");
    Assert.That(arranged.PacketsSent.Value, Is.GreaterThan(0ul));
    Assert.That(arranged.RoundTripTimeMicroseconds.HasValue, Is.True, "a loopback round trip is still a round trip");
  }

  /// <summary>
  /// The inode behind a socket, through the same <c>socket:[n]</c> link the probe joins on.
  /// </summary>
  [System.Runtime.Versioning.SupportedOSPlatform("linux")]
  private static ulong InodeOf(System.Net.Sockets.Socket socket) {
    var target = File.ResolveLinkTarget($"/proc/self/fd/{socket.Handle}", returnFinalTarget: false)?.Name
      ?? throw new InvalidOperationException("the descriptor has no link to read");

    Assert.That(ProcNetParser.TryParseSocketInode(target, out var inode), Is.True, $"unexpected link target {target}");
    return inode;
  }

  #endregion

}
