using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// A socket's send and receive rate (PRD §40).
/// </summary>
/// <remarks>
/// The kernel publishes totals and never a rate, so every rate here is two readings and the interval
/// between them. The interval is passed in rather than taken from a clock, which is what lets a test
/// arrange one instead of waiting it out — and what lets the awkward ones be arranged at all: an
/// inode reused by a new socket, two readings from the same instant, a counter that was never read.
/// </remarks>
[TestFixture]
public sealed class ConnectionRateTests {

  private const long _Second = TimeSpan.TicksPerSecond;

  private static ConnectionRecord Socket(ulong inode, Counter sent, Counter received) => new(
    ConnectionProtocol.Tcp,
    SocketKind.Stream,
    "192.168.1.5",
    38658,
    "93.184.216.34",
    443,
    "ESTABLISHED",
    inode,
    0,
    -1,
    null,
    null,
    Counter.Of(0ul),
    Counter.Of(0ul),
    Counter.Of(0ul),
    new(sent, received, Counter.NotSupported, Counter.NotSupported, Counter.NotSupported, Counter.NotSupported, Counter.NotSupported),
    Rate.NotSampledYet,
    Rate.NotSampledYet,
    null,
    null,
    Counter.NotSupported
  );

  private static ConnectionRecord Socket(ulong inode, ulong sent, ulong received)
    => Socket(inode, Counter.Of(sent), Counter.Of(received));

  #region rates

  /// <summary>
  /// The first sight of a socket has nothing to subtract from. Reporting the connection's whole
  /// lifetime of traffic as one interval's worth is what the alternative looks like.
  /// </summary>
  [Test]
  public void TheFirstReadingOfASocketHasNoRate() {
    var rates = new ConnectionRates();
    var connections = new List<ConnectionRecord> { Socket(1, 1_000_000, 2_000_000) };

    rates.Observe(connections, 0);

    Assert.That(connections[0].SendRate.HasValue, Is.False);
    Assert.That(connections[0].SendRate.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(connections[0].ReceiveRate.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>The second reading is the difference over the interval, and nothing else.</summary>
  [Test]
  public void TheSecondReadingIsTheDifferenceOverTheInterval() {
    var rates = new ConnectionRates();
    var first = new List<ConnectionRecord> { Socket(1, 1_000, 4_000) };
    rates.Observe(first, 0);

    var second = new List<ConnectionRecord> { Socket(1, 3_000, 4_000) };
    rates.Observe(second, 2 * _Second);

    Assert.That(second[0].SendRate.Value, Is.EqualTo(1_000d).Within(0.001));
    Assert.That(second[0].ReceiveRate.Value, Is.Zero, "nothing arrived, and that is a reading");
  }

  /// <summary>
  /// An inode is reused. A closed socket and a new one on the same number would otherwise produce a
  /// rate out of two unrelated connections' counters, and the giveaway is a total that has gone
  /// backwards — the same rule a process counter follows (PRD §3.2).
  /// </summary>
  [Test]
  public void ATotalThatWentBackwardsIsADifferentSocketAndNotNegativeTraffic() {
    var rates = new ConnectionRates();
    rates.Observe([Socket(1, 5_000_000, 0)], 0);

    var reused = new List<ConnectionRecord> { Socket(1, 12, 0) };
    rates.Observe(reused, _Second);

    Assert.That(reused[0].SendRate.HasValue, Is.False);
    Assert.That(reused[0].SendRate.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  /// <summary>
  /// Two readings from the same instant have no interval to divide by, and any answer would be an
  /// artefact of the division rather than of the traffic.
  /// </summary>
  [Test]
  public void TwoReadingsFromTheSameInstantAreNotARate() {
    var rates = new ConnectionRates();
    rates.Observe([Socket(1, 100, 100)], 500);

    var again = new List<ConnectionRecord> { Socket(1, 900, 900) };
    rates.Observe(again, 500);

    Assert.That(again[0].SendRate.HasValue, Is.False);
    Assert.That(again[0].SendRate.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  /// <summary>
  /// A counter that was never read cannot become a rate however many times it is looked at, so its
  /// own reason travels through rather than being replaced by "not sampled yet" — which would
  /// promise a number the next look will not produce either.
  /// </summary>
  [Test]
  public void ACounterThatWasNeverReadCarriesItsOwnReasonIntoTheRate() {
    var rates = new ConnectionRates();
    var unread = Counter.Unknown(UnknownReason.NotPermitted);
    var connections = new List<ConnectionRecord> { Socket(1, unread, unread) };

    rates.Observe(connections, 0);
    Assert.That(connections[0].SendRate.Reason, Is.EqualTo(UnknownReason.NotPermitted));

    var second = new List<ConnectionRecord> { Socket(1, unread, unread) };
    rates.Observe(second, _Second);
    Assert.That(second[0].SendRate.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  /// <summary>
  /// A Unix socket has no inode to key on in this table and no byte totals to divide, so it says
  /// there is no such thing rather than that nobody has looked twice yet.
  /// </summary>
  [Test]
  public void ASocketWithNoInodeHasNoRateAndNeverWill() {
    var rates = new ConnectionRates();
    var connections = new List<ConnectionRecord> { Socket(0, 10, 10) };

    rates.Observe(connections, 0);

    Assert.That(connections[0].SendRate.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(rates.Tracked, Is.Zero, "and nothing is remembered about it");
  }

  /// <summary>
  /// Sockets that have gone are forgotten. A machine that opens a connection per request would
  /// otherwise grow this map for the life of the program.
  /// </summary>
  [Test]
  public void AClosedSocketIsForgotten() {
    var rates = new ConnectionRates();
    rates.Observe([Socket(1, 1, 1), Socket(2, 1, 1), Socket(3, 1, 1)], 0);
    Assert.That(rates.Tracked, Is.EqualTo(3));

    rates.Observe([Socket(2, 2, 2)], _Second);
    Assert.That(rates.Tracked, Is.EqualTo(1));
  }

  #endregion

  #region what a socket's statistics say when nothing was read

  /// <summary>
  /// <c>default(SocketStatistics)</c> is seven counters that each claim to be a measured nought.
  /// Every way of making an empty one has to say why it is empty instead (PRD §72.3).
  /// </summary>
  [Test]
  public void AnEmptySetOfStatisticsStatesItsReason() {
    Assert.That(SocketStatistics.NotRead.HasAny, Is.False);
    Assert.That(SocketStatistics.NotRead.BytesSent.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(SocketStatistics.NotSupported.BytesReceived.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(SocketStatistics.Unknown(UnknownReason.NotPermitted).RoundTripTimeMicroseconds.Reason,
      Is.EqualTo(UnknownReason.NotPermitted));

    // The trap itself, written down: a dictionary miss leaves this behind, and its reason is "the
    // value is here".
    Assert.That(default(SocketStatistics).BytesSent.HasValue, Is.True);
    Assert.That(default(SocketStatistics), Is.Not.EqualTo(SocketStatistics.NotRead));
  }

  #endregion

}
