using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

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

  /// <summary>
  /// Every graphics adapter the kernel knows about (PRD §50).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>/sys/class/drm</c> holds one entry per card and one per connector — <c>card0</c> beside
  /// <c>card0-HDMI-A-1</c> — and only the cards are adapters. The connectors are filtered by shape
  /// rather than by asking each one what it is, because a laptop with a dock has a dozen of them.
  /// </para>
  /// <para>
  /// What comes back depends entirely on the driver, and mostly it is nothing. AMD publishes
  /// <c>gpu_busy_percent</c> and the VRAM figures; Intel's i915 publishes neither, because its
  /// engine busyness lives in a perf counter that needs a privileged open; NVIDIA's proprietary
  /// driver publishes nothing at all here and wants NVML. Each missing reading carries its own
  /// reason rather than a zero, so the page says "not implemented here" about the adapter it cannot
  /// read and stays honest about the one it can (PRD §5.3).
  /// </para>
  /// </remarks>
  public static IReadOnlyList<GpuInfo> DescribeGpus(string sysRoot) {
    var drm = Path.Combine(sysRoot, "class", "drm");
    if (!Directory.Exists(drm))
      return [];

    var cards = new List<GpuInfo>();
    foreach (var entry in Directory.EnumerateDirectories(drm)) {
      var name = Path.GetFileName(entry);
      if (!IsCard(name))
        continue;

      cards.Add(DescribeGpu(entry, name));
    }

    // The kernel enumerates in whatever order the filesystem hands back; card0 before card1 is what
    // a reader expects, and what the numbers in the names already promise.
    cards.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
    return cards;
  }

  /// <summary>
  /// <c>card0</c> yes, <c>card0-HDMI-A-1</c> no: a card's name is the word and a run of digits, and
  /// nothing else.
  /// </summary>
  internal static bool IsCard(string name) {
    if (!name.StartsWith("card", StringComparison.Ordinal) || name.Length == 4)
      return false;

    for (var i = 4; i < name.Length; ++i)
      if (!char.IsAsciiDigit(name[i]))
        return false;

    return true;
  }

  private static GpuInfo DescribeGpu(string cardPath, string name) {
    var device = Path.Combine(cardPath, "device");
    var uevent = Read(Path.Combine(device, "uevent")) ?? string.Empty;
    var driver = UeventParser.Value(uevent, "DRIVER");
    var model = PciNames.Describe(UeventParser.Value(uevent, "PCI_ID"));
    var slot = UeventParser.Value(uevent, "PCI_SLOT_NAME");

    var card = new GpuInfo(
      name,
      model,
      driver.IsEmpty ? null : new string(driver),
      // Percent, and only AMD writes it. Anything above 100 is a driver bug rather than a busy card.
      ReadCounter(Path.Combine(device, "gpu_busy_percent"), 1, 100),
      ReadCounter(Path.Combine(device, "mem_info_vram_used"), 1),
      ReadCounter(Path.Combine(device, "mem_info_vram_total"), 1),
      ReadHwmon(device, "temp1_input"),
      ReadHwmon(device, "power1_average"),
      Read(Path.Combine(device, "power_state")),
      // The cap is what makes the draw mean anything: 30 W says nothing until you know whether the
      // ceiling is 40 or 400.
      ReadHwmon(device, "power1_cap_max"),
      PowerCapMicrowatts: ReadHwmon(device, "power1_cap"),
      MemoryBusyPercent: Counter.Unknown(UnknownReason.NotImplementedHere),
      CoreClockHertz: ReadClock(cardPath),
      MemoryClockHertz: Counter.Unknown(UnknownReason.NotImplementedHere),
      FanPercent: ReadFan(device)
    );

    // The vendor's own library last, because it knows more than sysfs does about every card it
    // recognises — and about NVIDIA's, sysfs knows nothing at all.
    return NvmlReader.Describe(card, slot.IsEmpty ? null : new string(slot)) ?? card;
  }

  /// <summary>
  /// Intel's i915 publishes its render clock and nothing else useful.
  /// </summary>
  /// <remarks>
  /// <c>gt_act_freq_mhz</c> is what the hardware is actually running at and reads 0 when the render
  /// engine is parked; <c>gt_cur_freq_mhz</c> is what the driver has requested and keeps its last
  /// value. The requested one is the one that matches what other tools show, and a zero here would
  /// be read as "no clock" rather than "idle".
  /// </remarks>
  private static Counter ReadClock(string cardPath) {
    var actual = ReadCounter(Path.Combine(cardPath, "gt_act_freq_mhz"), 1_000_000);
    if (actual.HasValue && actual.Value > 0)
      return actual;

    return ReadCounter(Path.Combine(cardPath, "gt_cur_freq_mhz"), 1_000_000);
  }

  /// <summary>Percent, from whichever of hwmon's two spellings the driver uses.</summary>
  private static Counter ReadFan(string devicePath) {
    var percent = ReadHwmon(devicePath, "fan1_input");
    return percent.HasValue ? percent : ReadHwmon(devicePath, "pwm1");
  }

  /// <summary>
  /// A number from one sysfs file, scaled, or the reason it is not there.
  /// </summary>
  /// <remarks>
  /// A file that does not exist is <see cref="UnknownReason.NotImplementedHere"/> and not
  /// <see cref="UnknownReason.NotSupportedOnPlatform"/>: Linux can report this, and does on other
  /// hardware. It is this driver that does not, which is a different sentence and the one that tells
  /// a reader whether a different machine would answer (PRD §5.3).
  /// </remarks>
  private static Counter ReadCounter(string path, ulong scale, ulong ceiling = ulong.MaxValue) {
    var text = Read(path);
    if (text is null)
      return Counter.Unknown(UnknownReason.NotImplementedHere);

    return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value <= ceiling
      ? Counter.Of(value * scale)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// A reading from whichever hwmon node the driver hung off this card, since the number in
  /// <c>hwmon/hwmonN</c> is assigned in probe order and is not the card's to choose.
  /// </summary>
  private static Counter ReadHwmon(string devicePath, string file) {
    var hwmon = Path.Combine(devicePath, "hwmon");
    if (!Directory.Exists(hwmon))
      return Counter.Unknown(UnknownReason.NotImplementedHere);

    foreach (var node in Directory.EnumerateDirectories(hwmon)) {
      var counter = ReadCounter(Path.Combine(node, file), 1);
      if (counter.HasValue)
        return counter;
    }

    return Counter.Unknown(UnknownReason.NotImplementedHere);
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
