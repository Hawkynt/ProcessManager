using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Turns a socket's byte totals into a send and a receive rate, by remembering what they were last
/// time (PRD §40, §5.1).
/// </summary>
/// <remarks>
/// <para>
/// The kernel publishes totals and never a rate, so a rate is always the difference between two
/// readings divided by the time between them — the same arithmetic <see cref="Sampling.SnapshotDelta"/>
/// does for a process, kept separately because a socket is not a process and outlives none of the
/// same things.
/// </para>
/// <para>
/// A rate needs two readings and there is no honest way round it. The first sight of a socket is
/// <see cref="UnknownReason.NotSampledYet"/> and stays that way until somebody looks again, which is
/// why a one-shot listing has no rate column at all rather than a column of dashes.
/// </para>
/// <para>
/// Inodes are reused. A socket closing and a new one landing on the same inode would otherwise
/// produce a rate out of two unrelated connections' counters, so a total that has gone backwards is
/// treated as a different socket rather than as negative traffic — the same rule
/// <see cref="Counter.Since"/> applies to a process counter.
/// </para>
/// <para>
/// No platform attribute and no clock of its own: the caller passes the timestamp, so a test can
/// arrange an interval instead of waiting one out (PRD §9.2).
/// </para>
/// </remarks>
public sealed class ConnectionRates {

  private readonly Dictionary<ulong, Previous> _previous = [];

  private readonly record struct Previous(Counter BytesSent, Counter BytesReceived, long Ticks);

  /// <summary>How many sockets are being remembered, for the tests and for a leak that would show here.</summary>
  public int Tracked => this._previous.Count;

  /// <summary>Forgets everything, so the next reading of every socket is a first one.</summary>
  public void Clear() => this._previous.Clear();

  /// <summary>
  /// Fills the send and receive rate of every connection in <paramref name="connections"/> and
  /// remembers this reading for the next call.
  /// </summary>
  /// <param name="timestampTicks">
  /// When these readings were taken, in <see cref="DateTime.Ticks"/>. Taken once for the whole list:
  /// the sockets were read in one pass and pretending each row has its own interval would give
  /// several slightly different denominators for one measurement.
  /// </param>
  public void Observe(List<ConnectionRecord> connections, long timestampTicks) {
    ArgumentNullException.ThrowIfNull(connections);

    // Sockets that have gone are dropped, or the map grows for the life of the program on a machine
    // that opens a connection per request.
    var seen = new HashSet<ulong>(connections.Count);
    for (var i = 0; i < connections.Count; ++i) {
      var connection = connections[i];
      var statistics = connection.Statistics;
      if (connection.Inode == 0) {
        // Nothing to key on, so nothing can be remembered about it and nothing can be derived. The
        // reason is the socket's own rather than "not sampled yet", which would promise a number
        // that a second look will never produce either.
        connections[i] = connection with {
          SendRate = Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
          ReceiveRate = Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
        };

        continue;
      }

      seen.Add(connection.Inode);
      var had = this._previous.TryGetValue(connection.Inode, out var previous);
      this._previous[connection.Inode] = new(statistics.BytesSent, statistics.BytesReceived, timestampTicks);
      if (!had) {
        // Deliberately not a fall-through to the arithmetic below: `previous` is `default` here, and
        // a default Counter reads as a measured zero, which would make the first sight of a busy
        // socket report its whole lifetime's traffic as one interval's worth (PRD §72.3).
        connections[i] = connection with {
          SendRate = Unknown(statistics.BytesSent, UnknownReason.NotSampledYet),
          ReceiveRate = Unknown(statistics.BytesReceived, UnknownReason.NotSampledYet),
        };

        continue;
      }

      var seconds = (timestampTicks - previous.Ticks) / (double)TimeSpan.TicksPerSecond;
      connections[i] = connection with {
        SendRate = Divide(statistics.BytesSent, previous.BytesSent, seconds),
        ReceiveRate = Divide(statistics.BytesReceived, previous.BytesReceived, seconds),
      };
    }

    if (seen.Count == this._previous.Count)
      return;

    var gone = new List<ulong>();
    foreach (var inode in this._previous.Keys)
      if (!seen.Contains(inode))
        gone.Add(inode);

    foreach (var inode in gone)
      this._previous.Remove(inode);
  }

  /// <summary>
  /// Why there is no rate. A counter that was never read cannot produce one however many times it is
  /// looked at, and saying "not sampled yet" about it would be a promise nothing can keep.
  /// </summary>
  private static Rate Unknown(Counter counter, UnknownReason otherwise)
    => counter.HasValue ? Rate.Unknown(otherwise) : Rate.Unknown(counter.Reason);

  private static Rate Divide(Counter now, Counter previous, double seconds) {
    if (!now.HasValue)
      return Rate.Unknown(now.Reason);
    if (!previous.HasValue)
      return Rate.Unknown(previous.Reason == UnknownReason.None ? UnknownReason.NotSampledYet : previous.Reason);

    // Two readings from the same instant, or from a clock that went backwards. There is no interval
    // to divide by and any answer would be an artefact of the division rather than of the traffic.
    if (seconds <= 0d)
      return Rate.Gap;

    var difference = now.Since(previous);
    return difference.HasValue ? Rate.Of(difference.Value / seconds) : Rate.Unknown(difference.Reason);
  }

}
