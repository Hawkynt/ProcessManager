using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// <c>/proc/diskstats</c> and <c>/proc/net/dev</c>, parsed (PRD §48, §49).
/// </summary>
/// <remarks>
/// One file each for the whole machine, which is what makes these affordable on the sampling path at
/// all: the per-process figures in §18 need a descriptor scan per process and are not. Spans and no
/// platform attribute, so both parsers are tested on every CI leg against recorded text.
/// </remarks>
internal static class DeviceStatParser {

  /// <summary>Sector size in diskstats is fixed at 512 bytes regardless of the device's own.</summary>
  private const ulong _SectorBytes = 512;

  /// <summary>
  /// Reads <c>/proc/diskstats</c>.
  /// </summary>
  /// <param name="isWholeDevice">
  /// Decides what to keep. Every partition appears alongside its parent and is charged the same
  /// I/O, so counting both reports twice the traffic; loop and ram devices are noise.
  /// </param>
  public static int ParseDiskStats(
    ReadOnlySpan<byte> content,
    Func<string, bool> isWholeDevice,
    Span<DiskCounters> destination,
    DeviceNameCache names
  ) {
    var written = 0;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty && written < destination.Length) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var fields = new AsciiScanner(line);
      fields.Skip(2);                                    // 1 major, 2 minor
      var name = fields.NextField();
      if (name.IsEmpty)
        continue;

      var deviceName = names.Resolve(name);
      if (!isWholeDevice(deviceName))
        continue;

      var reads = fields.NextUInt64();                   // 4 reads completed
      fields.Skip(1);                                    // 5 reads merged
      var sectorsRead = fields.NextUInt64();             // 6 sectors read
      fields.Skip(1);                                    // 7 ms reading
      var writes = fields.NextUInt64();                  // 8 writes completed
      fields.Skip(1);                                    // 9 writes merged
      var sectorsWritten = fields.NextUInt64();          // 10 sectors written
      fields.Skip(2);                                    // 11 ms writing, 12 in flight
      var busy = fields.NextUInt64();                    // 13 ms doing I/O

      destination[written++] = new() {
        Name = deviceName,
        ReadOperations = Counter.Of(reads),
        WriteOperations = Counter.Of(writes),
        ReadBytes = Counter.Of(sectorsRead * _SectorBytes),
        WriteBytes = Counter.Of(sectorsWritten * _SectorBytes),
        BusyMilliseconds = Counter.Of(busy),
      };
    }

    return written;
  }

  /// <summary>
  /// Reads <c>/proc/net/dev</c>.
  /// </summary>
  /// <remarks>
  /// Two header lines, then one interface per line as <c>name: …</c>. The name is separated by a
  /// colon rather than by space and may butt straight up against its first number on a busy
  /// interface — "eth0:1234567" is valid and is why this splits on the colon rather than on
  /// whitespace.
  /// </remarks>
  public static int ParseNetDev(
    ReadOnlySpan<byte> content,
    Span<NetworkCounters> destination,
    DeviceNameCache names
  ) {
    var written = 0;
    var scanner = new AsciiScanner(content);
    var line = 0;
    while (!scanner.IsEmpty && written < destination.Length) {
      var text = scanner.NextLine();
      if (++line <= 2 || text.IsEmpty)
        continue;

      var colon = text.IndexOf((byte)':');
      if (colon < 0)
        continue;

      var name = text[..colon].Trim((byte)' ');
      if (name.IsEmpty)
        continue;

      var fields = new AsciiScanner(text[(colon + 1)..]);
      var receivedBytes = fields.NextUInt64();
      var receivedPackets = fields.NextUInt64();
      var receiveErrors = fields.NextUInt64();
      var receiveDropped = fields.NextUInt64();
      fields.Skip(4);                                    // fifo, frame, compressed, multicast
      var sentBytes = fields.NextUInt64();
      var sentPackets = fields.NextUInt64();
      var sendErrors = fields.NextUInt64();
      var sendDropped = fields.NextUInt64();

      destination[written++] = new() {
        Name = names.Resolve(name),
        ReceivedBytes = Counter.Of(receivedBytes),
        ReceivedPackets = Counter.Of(receivedPackets),
        ReceiveErrors = Counter.Of(receiveErrors),
        ReceiveDropped = Counter.Of(receiveDropped),
        SentBytes = Counter.Of(sentBytes),
        SentPackets = Counter.Of(sentPackets),
        SendErrors = Counter.Of(sendErrors),
        SendDropped = Counter.Of(sendDropped),
      };
    }

    return written;
  }

}
