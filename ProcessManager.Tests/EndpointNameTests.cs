using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Ports and addresses as somebody would say them (PRD §40).
/// </summary>
[TestFixture]
public sealed class EndpointNameTests {

  #region service names

  private const string Services = """
    # A comment, and the blank line below it.

    tcpmux          1/tcp                           # TCP port service multiplexer
    ssh             22/tcp                          # SSH Remote Login Protocol
    ssh             22/udp
    http            80/tcp          www www-http
    https           443/tcp
    dhcpv6-client   546/udp
    not-a-port      here/tcp
    malformed
    """;

  private static ServiceNames Parsed() => ServiceNames.Parse(Services);

  [Test]
  public void APortIsNamed() {
    Assert.That(Parsed().Find(22, datagram: false), Is.EqualTo("ssh"));
    Assert.That(Parsed().Find(443, datagram: false), Is.EqualTo("https"));
  }

  /// <summary>
  /// Stream and datagram registrations are different facts. 546 is the DHCPv6 client over UDP and
  /// nothing at all over TCP, and answering with the wrong one is worse than not answering.
  /// </summary>
  [Test]
  public void AStreamPortAndADatagramPortAreNotTheSameRegistration() {
    var names = Parsed();

    Assert.That(names.Find(546, datagram: true), Is.EqualTo("dhcpv6-client"));
    Assert.That(names.Find(546, datagram: false), Is.Null, "nothing claims 546 over TCP here");
    Assert.That(names.Find(80, datagram: true), Is.Null);
  }

  /// <summary>The canonical name comes first in the file, and it is a better answer than its aliases.</summary>
  [Test]
  public void TheCanonicalNameWinsOverItsAliases() =>
    Assert.That(Parsed().Find(80, datagram: false), Is.EqualTo("http"));

  /// <summary>
  /// A port nobody named stays a number. Inventing a name would be indistinguishable from one the
  /// machine actually declares.
  /// </summary>
  [Test]
  public void AnUnnamedPortIsNotGivenAName() {
    var names = Parsed();

    Assert.That(names.Find(51413, datagram: false), Is.Null);
    Assert.That(names.Describe(51413, datagram: false), Is.EqualTo("51413"));
    Assert.That(names.Describe(22, datagram: false), Is.EqualTo("ssh"));
  }

  /// <summary>
  /// This file is edited by hand on plenty of machines. One unparseable line must not cost every
  /// name after it.
  /// </summary>
  [Test]
  public void ABadLineCostsOnlyItself() {
    var names = Parsed();

    Assert.That(names.Find(1, datagram: false), Is.EqualTo("tcpmux"), "before the bad lines");
    Assert.That(names.Find(546, datagram: true), Is.EqualTo("dhcpv6-client"), "after them");
  }

  [Test]
  public void AMachineWithNoSuchFileNamesNothingRatherThanFailing() {
    Assert.That(ServiceNames.Empty.Count, Is.Zero);
    Assert.That(ServiceNames.Empty.Describe(22, datagram: false), Is.EqualTo("22"));
    Assert.That(ServiceNames.Parse(string.Empty).Count, Is.Zero);
  }

  /// <summary>A comment is not a registration, however much it looks like one.</summary>
  [Test]
  public void CommentsAreNotRead() =>
    Assert.That(ServiceNames.Parse("# ssh 22/tcp").Count, Is.Zero);

  #endregion

  #region hostnames

  /// <summary>
  /// Off unless somebody turns it on. A reverse lookup tells whoever runs the resolver which
  /// addresses this machine is talking to, and that disclosure is not ours to make by default.
  /// </summary>
  [Test]
  public void NothingIsLookedUpUntilItIsTurnedOn() {
    var asked = 0;
    using var cache = new HostnameCache(_ => { Interlocked.Increment(ref asked); return "never"; });

    for (var i = 0; i < 10; ++i)
      Assert.That(cache.Lookup("192.0.2.1"), Is.Null);

    Thread.Sleep(50);
    Assert.That(asked, Is.Zero, "not one query left this machine");
    Assert.That(cache.Describe("192.0.2.1"), Is.EqualTo("192.0.2.1"), "the address, which is always true");
  }

  /// <summary>
  /// The point of the whole class: a resolver that never answers must not stop the caller. This one
  /// blocks until the test lets it go, and the lookup has to return anyway.
  /// </summary>
  [Test]
  public void ALookupNeverWaitsForTheResolver() {
    using var stuck = new ManualResetEventSlim(false);
    using var cache = new HostnameCache(_ => { stuck.Wait(); return "late"; }) { Enabled = true };

    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 100; ++i)
      cache.Lookup($"192.0.2.{i}");

    clock.Stop();
    stuck.Set();

    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(500)), "a hundred lookups behind a hung resolver");
  }

  [Test]
  public void AnAnswerArrivesForALaterFrame() {
    using var cache = new HostnameCache(address => $"host-{address}") { Enabled = true };

    Assert.That(cache.Lookup("192.0.2.7"), Is.Null, "nothing is known yet");

    Assert.That(
      () => cache.Lookup("192.0.2.7"),
      Is.EqualTo("host-192.0.2.7").After(2_000).PollEvery(10),
      "the name appeared once the lookup finished"
    );
  }

  /// <summary>
  /// An address with no name is the ordinary case, and asking again every second is exactly the load
  /// this class exists to avoid. The answer "no name" has to stick as firmly as a name does.
  /// </summary>
  [Test]
  public void AnAddressWithNoNameIsNotAskedAboutAgain() {
    var asked = 0;
    using var cache = new HostnameCache(_ => { Interlocked.Increment(ref asked); return null; }) { Enabled = true };

    cache.Lookup("192.0.2.9");
    Assert.That(() => cache.Count, Is.EqualTo(1).After(2_000).PollEvery(10), "the failure was remembered");

    for (var i = 0; i < 50; ++i)
      Assert.That(cache.Lookup("192.0.2.9"), Is.Null);

    Thread.Sleep(50);
    Assert.That(asked, Is.EqualTo(1), "asked once, not fifty-one times");
  }

  /// <summary>The same address asked for repeatedly before any answer must not queue repeatedly.</summary>
  [Test]
  public void AnAddressAlreadyBeingLookedUpIsNotQueuedTwice() {
    using var stuck = new ManualResetEventSlim(false);
    var asked = 0;
    using var cache = new HostnameCache(_ => { Interlocked.Increment(ref asked); stuck.Wait(); return "late"; }) { Enabled = true };

    for (var i = 0; i < 100; ++i)
      cache.Lookup("192.0.2.11");

    Thread.Sleep(50);
    stuck.Set();

    Assert.That(asked, Is.EqualTo(1));
  }

  /// <summary>
  /// A resolver that throws answered "no name", as far as a table is concerned. It must not take the
  /// thread down and with it every later lookup.
  /// </summary>
  [Test]
  public void AResolverThatThrowsDoesNotStopTheOnesAfterIt() {
    using var cache = new HostnameCache(
      address => address.EndsWith('1') ? throw new InvalidOperationException("no resolver") : "fine"
    ) { Enabled = true };

    cache.Lookup("192.0.2.1");
    cache.Lookup("192.0.2.2");

    Assert.That(() => cache.Lookup("192.0.2.2"), Is.EqualTo("fine").After(2_000).PollEvery(10));
  }

  /// <summary>
  /// Ten thousand sockets must not become ten thousand queries. Past the queue's depth an address is
  /// simply not resolved this time round, which costs nothing because the table is redrawn anyway.
  /// </summary>
  [Test]
  public void TheQueueIsBounded() {
    using var stuck = new ManualResetEventSlim(false);
    using var cache = new HostnameCache(_ => { stuck.Wait(); return "late"; }, queueDepth: 8) { Enabled = true };

    for (var i = 0; i < 10_000; ++i)
      Assert.That(cache.Lookup($"192.0.2.{i}"), Is.Null);

    stuck.Set();
  }

  [Test]
  public void TurningItOffStopsTheAsking() {
    var asked = 0;
    using var cache = new HostnameCache(_ => { Interlocked.Increment(ref asked); return "yes"; }) { Enabled = true };

    cache.Lookup("192.0.2.20");
    Assert.That(() => cache.Count, Is.EqualTo(1).After(2_000).PollEvery(10));

    cache.Enabled = false;
    cache.Lookup("192.0.2.21");
    Thread.Sleep(50);

    Assert.That(asked, Is.EqualTo(1), "the second address was never asked about");
    Assert.That(cache.Lookup("192.0.2.20"), Is.Null, "and nothing is shown while it is off");
  }

  #endregion

  #region what a column shows

  private static Model.ConnectionRecord Connection(
    Model.ConnectionProtocol protocol = Model.ConnectionProtocol.Tcp,
    string local = "192.168.1.5",
    int localPort = 38658,
    string remote = "93.184.216.34",
    int remotePort = 443
  ) => new(
    protocol,
    Model.SocketKind.Stream,
    local,
    localPort,
    remote,
    remotePort,
    "ESTABLISHED",
    0,
    0,
    // Not zero, which is root — an unfilled id has to be one nobody holds.
    -1,
    null,
    null,
    Model.Counter.NotSupported,
    Model.Counter.NotSupported,
    Model.Counter.NotSupported,
    Model.SocketStatistics.NotRead,
    Model.Rate.NotSampledYet,
    Model.Rate.NotSampledYet,
    null,
    null,
    Model.Counter.NotSupported
  );

  [Test]
  public void APortIsNamedInTheColumn() {
    var named = Humanize.RemoteEndpoint(Connection(), Parsed(), null);
    Assert.That(named, Is.EqualTo("93.184.216.34:https"));
  }

  /// <summary>
  /// The same port over UDP is a different registration, and the protocol on the record is what
  /// decides which table is consulted.
  /// </summary>
  [Test]
  public void TheProtocolDecidesWhichRegistrationIsRead() {
    var udp = Connection(Model.ConnectionProtocol.Udp, remotePort: 546);
    var tcp = Connection(Model.ConnectionProtocol.Tcp, remotePort: 546);

    Assert.That(Humanize.RemoteEndpoint(udp, Parsed(), null), Does.EndWith(":dhcpv6-client"));
    Assert.That(Humanize.RemoteEndpoint(tcp, Parsed(), null), Does.EndWith(":546"), "nothing claims it over TCP");
  }

  [Test]
  public void WithoutTheFileAPortStaysANumber() =>
    Assert.That(Humanize.RemoteEndpoint(Connection(), null, null), Is.EqualTo("93.184.216.34:443"));

  /// <summary>An IPv6 address is full of colons, so it keeps its brackets once the port is a name.</summary>
  [Test]
  public void AnIpv6AddressIsStillBracketed() {
    var six = Connection(Model.ConnectionProtocol.Tcp6, remote: "2606:2800:220:1:248:1893:25c8:1946");
    Assert.That(Humanize.RemoteEndpoint(six, Parsed(), null), Is.EqualTo("[2606:2800:220:1:248:1893:25c8:1946]:https"));
  }

  /// <summary>
  /// A Unix socket has a path and no port, so neither naming applies to it — and it must not acquire
  /// a <c>:0</c> that would read as one.
  /// </summary>
  [Test]
  public void AUnixSocketIsUntouchedByEitherNaming() {
    var unix = Connection(Model.ConnectionProtocol.Unix, local: "/run/dbus/system_bus_socket", localPort: 0, remotePort: 0);

    Assert.That(Humanize.LocalEndpoint(unix, Parsed(), null), Is.EqualTo("/run/dbus/system_bus_socket"));
    Assert.That(Humanize.RemoteEndpoint(unix, Parsed(), null), Is.EqualTo("n/a"));
  }

  /// <summary>
  /// A socket with no peer says so rather than acquiring a name for an endpoint it does not have.
  /// </summary>
  [Test]
  public void AListeningSocketHasNoRemoteToName() {
    var listening = Connection(remote: "0.0.0.0", remotePort: 0);
    Assert.That(Humanize.RemoteEndpoint(listening, Parsed(), null), Is.EqualTo("—"));
  }

  [Test]
  public void AKnownHostnameReplacesTheAddress() {
    using var hosts = new HostnameCache(_ => "example.test") { Enabled = true };
    hosts.Lookup("93.184.216.34");
    Assert.That(() => hosts.Lookup("93.184.216.34"), Is.Not.Null.After(2_000).PollEvery(10));

    Assert.That(Humanize.RemoteEndpoint(Connection(), Parsed(), hosts), Is.EqualTo("example.test:https"));
  }

  /// <summary>
  /// Until a name is known — and forever, if resolution is off — the address is what is shown. A
  /// blank would read as though the socket had no address at all.
  /// </summary>
  [Test]
  public void AnAddressWithNoNameYetIsStillShown() {
    using var hosts = new HostnameCache(_ => "late") { Enabled = false };
    Assert.That(Humanize.RemoteEndpoint(Connection(), Parsed(), hosts), Is.EqualTo("93.184.216.34:https"));
  }

  #endregion

  #region the host back out of an endpoint (PRD §40)

  /// <summary>
  /// The bracket is what makes this possible: <c>fe80::1:22</c> could be port 22 on <c>fe80::1</c> or
  /// no port at all on <c>fe80::1:22</c>, and everything that writes endpoints brackets an IPv6
  /// address for exactly that reason.
  /// </summary>
  [TestCase("93.184.216.34:443", "93.184.216.34")]
  [TestCase("93.184.216.34:https", "93.184.216.34")]
  [TestCase("[fe80::1]:22", "fe80::1")]
  [TestCase("[::1]:ssh", "::1")]
  [TestCase("example.test:https", "example.test")]
  [TestCase("example.test", "example.test")]
  public void TheHostComesBackOutOfAnEndpoint(string endpoint, string expected)
    => Assert.That(Humanize.EndpointHost(endpoint), Is.EqualTo(expected));

  /// <summary>
  /// The placeholders are not hosts. Handing one to a search would open a browser looking for an em
  /// dash, which is the shape of bug that ships because nothing throws.
  /// </summary>
  [TestCase("—")]
  [TestCase("n/a")]
  [TestCase("<unnamed>")]
  [TestCase("")]
  [TestCase(null)]
  public void APlaceholderIsNotAHost(string? endpoint)
    => Assert.That(Humanize.EndpointHost(endpoint), Is.Null);

  /// <summary>
  /// Whatever this class writes, it can read back. Held over the same two writers a table cell goes
  /// through, so a change to either format fails here rather than in a context menu.
  /// </summary>
  [Test]
  public void EveryEndpointThisClassWritesRoundTrips() {
    var services = Parsed();

    Assert.That(Humanize.EndpointHost(Humanize.RemoteEndpoint(Connection(), services, null)), Is.EqualTo("93.184.216.34"));
    Assert.That(Humanize.EndpointHost(Humanize.LocalEndpoint(Connection(), services, null)), Is.EqualTo("192.168.1.5"));

    var six = Connection(local: "::1", remote: "2606:2800:220:1:248:1893:25c8:1946");
    Assert.That(Humanize.EndpointHost(Humanize.RemoteEndpoint(six, services, null)), Is.EqualTo("2606:2800:220:1:248:1893:25c8:1946"));
    Assert.That(Humanize.EndpointHost(Humanize.LocalEndpoint(six, services, null)), Is.EqualTo("::1"));
  }

  #endregion

}
