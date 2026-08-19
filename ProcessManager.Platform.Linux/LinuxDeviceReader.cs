using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What the machine's disks and network interfaces are, from <c>/sys</c> (PRD §48, §49).
/// </summary>
/// <remarks>
/// The descriptions, not the counters: a disk does not change its model or capacity while the
/// program runs, so this is read once and cached. The counters come from
/// <see cref="DeviceStatParser"/> on every sample. Managed file APIs, like the host reader —
/// this runs once, not at one hertz.
/// </remarks>
internal static class LinuxDeviceReader {

  /// <summary>
  /// Whether a name from diskstats is a whole device rather than a partition.
  /// </summary>
  /// <remarks>
  /// <c>/sys/block</c> holds exactly the whole devices — partitions live inside them — so this is
  /// the kernel's own answer rather than a guess from the name. Guessing by trailing digits gets
  /// <c>nvme0n1</c> wrong, which is every NVMe disk there is.
  /// </remarks>
  public static bool IsWholeDevice(string sysRoot, string name) {
    if (name.StartsWith("loop", StringComparison.Ordinal)
        || name.StartsWith("ram", StringComparison.Ordinal)
        || name.StartsWith("zram", StringComparison.Ordinal))
      return false;

    return Directory.Exists(Path.Combine(sysRoot, "block", name));
  }

  public static DiskInfo Describe(string sysRoot, string name) {
    var root = Path.Combine(sysRoot, "block", name);

    // "size" is in 512-byte sectors whatever the device's own sector size, the same unit diskstats
    // uses and a classic way to be off by a factor of eight.
    var capacity = ulong.TryParse(Read(Path.Combine(root, "size")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectors)
      ? Counter.Of(sectors * 512)
      : Counter.NotSupported;

    var rotationalText = Read(Path.Combine(root, "queue", "rotational"));
    bool? rotational = rotationalText switch { "1" => true, "0" => false, _ => null };

    return new(name, Read(Path.Combine(root, "device", "model")), rotational, capacity);
  }

  public static NetworkInterfaceInfo DescribeInterface(string sysRoot, string name) {
    var root = Path.Combine(sysRoot, "class", "net", name);

    // Only meaningful on a link that is up, and absent entirely on anything virtual. -1 is the
    // kernel's way of saying it does not know, and reporting that as a zero-speed link would be
    // wrong in a way somebody would act on.
    var speedText = Read(Path.Combine(root, "speed"));
    var speed = int.TryParse(speedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var megabits) && megabits > 0
      ? Counter.Of((ulong)megabits * 1_000_000)
      : Counter.NotSupported;

    var mtu = ulong.TryParse(Read(Path.Combine(root, "mtu")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
      ? Counter.Of(bytes)
      : Counter.NotSupported;

    // Type 772 is ARPHRD_LOOPBACK. Worth knowing because loopback traffic is real but is not the
    // network, and a chart that includes it reports a machine talking to itself as bandwidth.
    var isLoopback = Read(Path.Combine(root, "type")) == "772";

    return new(name, Read(Path.Combine(root, "address")), speed, Read(Path.Combine(root, "operstate")), mtu, isLoopback);
  }

  private static string? Read(string path) {
    try {
      return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}
