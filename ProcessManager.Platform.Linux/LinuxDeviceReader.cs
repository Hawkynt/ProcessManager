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

  /// <param name="layout">
  /// Where the machine's mounts and swap areas sit, or null when nobody has worked that out — in
  /// which case the volumes and the two indicators say they are unknown rather than saying no.
  /// </param>
  public static DiskInfo Describe(string sysRoot, string name, LinuxStorageLayout? layout = null) {
    var root = Path.Combine(sysRoot, "block", name);

    // "size" is in 512-byte sectors whatever the device's own sector size, the same unit diskstats
    // uses and a classic way to be off by a factor of eight.
    var capacity = ulong.TryParse(Read(Path.Combine(root, "size")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectors)
      ? Counter.Of(sectors * 512)
      : Counter.NotSupported;

    var rotationalText = Read(Path.Combine(root, "queue", "rotational"));
    bool? rotational = rotationalText switch { "1" => true, "0" => false, _ => null };

    return new(
      name,
      Read(Path.Combine(root, "device", "model")),
      rotational,
      capacity,
      // NVMe publishes a serial of its own; a SCSI or SATA disk publishes a vendor-page one under
      // another name, and a device-mapper target has none at all because it is not a device.
      Serial(root),
      Bus(root),
      layout?.VolumesOf(name),
      layout?.IsSystemDisk(name),
      layout?.HoldsSwap(name)
    );
  }

  /// <summary>The device's serial, from whichever of the two files this class of device uses.</summary>
  /// <remarks>
  /// <c>serial</c> is NVMe's; <c>vpd_pg80</c> is the SCSI inquiry page and is a binary blob whose
  /// tail is the serial as ASCII, which is more decoding than a description needs — so a SCSI disk
  /// falls back to <c>wwid</c>, which the kernel has already made into text.
  /// </remarks>
  private static string? Serial(string root) {
    var device = Path.Combine(root, "device");
    return Read(Path.Combine(device, "serial")) ?? Read(Path.Combine(device, "wwid"));
  }

  /// <summary>
  /// What the device hangs off, in the kernel's own vocabulary.
  /// </summary>
  /// <remarks>
  /// The subsystem the device's driver registered with — <c>nvme</c>, <c>scsi</c>, <c>virtio</c>,
  /// <c>mmc</c>. Deliberately not translated into "SATA" or "USB": both of those are <c>scsi</c>
  /// from here, and the words would be a guess dressed as a reading (PRD §5.3).
  /// </remarks>
  private static string? Bus(string root) {
    var subsystem = Path.Combine(root, "device", "subsystem");
    try {
      // A symlink into /sys/class or /sys/bus, whose leaf is the name. Resolved rather than followed
      // as a directory, so a recorded tree without the target still answers.
      var target = Directory.ResolveLinkTarget(subsystem, returnFinalTarget: false);
      var name = target is null ? null : Path.GetFileName(target.FullName.TrimEnd('/'));
      return name is { Length: > 0 } ? name : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <param name="live">
  /// Whether these files are this machine's, rather than a recorded tree's.
  /// </param>
  /// <remarks>
  /// The addresses and the wireless association are asked of the kernel directly — <c>getifaddrs</c>
  /// and an <c>ioctl</c> — and there is no way to ask either about anybody else's interfaces. So
  /// they may only be read when the files beside them belong to this machine too: a
  /// <c>--probe-root</c> replay that put this laptop's address and Wi-Fi network beside a fixture's
  /// counters would be describing two machines in one table, which is the rule §9.4 states for
  /// <c>CPUID</c> and which holds for exactly the same reason here.
  /// </remarks>
  public static NetworkInterfaceInfo DescribeInterface(
    string sysRoot,
    string name,
    string? procRoot = null,
    bool live = false
  ) {
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
    var wireless = Directory.Exists(Path.Combine(root, "phy80211"));

    return new(
      name,
      Read(Path.Combine(root, "address")),
      speed,
      Read(Path.Combine(root, "operstate")),
      mtu,
      isLoopback,
      int.TryParse(Read(Path.Combine(root, "ifindex")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
        ? index
        : null,
      Kind(root, isLoopback, wireless),
      Driver(root),
      live ? LinuxAddressReader.Read(name) : null,
      procRoot is null ? null : Gateway(procRoot, name),
      procRoot is null ? null : Resolvers(procRoot),
      live && wireless ? LinuxWirelessReader.Ssid(name) : null,
      wireless && procRoot is not null ? Signal(procRoot, name) : null,
      live && wireless ? LinuxWirelessReader.FrequencyMegahertz(name) : null
    );
  }

  /// <summary>
  /// What sort of interface this is, from what the kernel publishes about it.
  /// </summary>
  /// <remarks>
  /// By structure and not by name. <c>phy80211</c> exists only on a wireless interface;
  /// <c>bridge</c> and <c>bonding</c> likewise; a <c>device</c> symlink is what a real piece of
  /// hardware has and its absence is what makes an interface virtual. Names — <c>eth0</c>,
  /// <c>wlan0</c>, <c>tun0</c> — are a convention that predictable interface naming already broke
  /// once, and <c>enp0s31f6</c> begins with none of them.
  /// </remarks>
  private static string Kind(string root, bool isLoopback, bool wireless) {
    if (isLoopback)
      return "loopback";

    if (wireless)
      return "wireless";

    if (Directory.Exists(Path.Combine(root, "bridge")))
      return "bridge";

    if (Directory.Exists(Path.Combine(root, "bonding")))
      return "bonding";

    if (Directory.Exists(Path.Combine(root, "tun_flags")) || File.Exists(Path.Combine(root, "tun_flags")))
      return "tunnel";

    return Directory.Exists(Path.Combine(root, "device")) ? "ethernet" : "virtual";
  }

  /// <summary>The module behind the interface, from the driver symlink the kernel hangs off it.</summary>
  private static string? Driver(string root) {
    try {
      var target = Directory.ResolveLinkTarget(Path.Combine(root, "device", "driver"), returnFinalTarget: false);
      var name = target is null ? null : Path.GetFileName(target.FullName.TrimEnd('/'));
      return name is { Length: > 0 } ? name : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>The default route through this interface, IPv4 first and IPv6 where there is no IPv4.</summary>
  /// <remarks>
  /// One or the other rather than both: the row answers "which way off this machine", and on a
  /// dual-stack network both gateways are the same router wearing two addresses.
  /// </remarks>
  private static string? Gateway(string procRoot, string name) {
    var v4 = ReadBytes(Path.Combine(procRoot, "net", "route"));
    if (v4 is not null && RouteTableParser.DefaultGateway(v4, name) is { } gateway)
      return gateway;

    var v6 = ReadBytes(Path.Combine(procRoot, "net", "ipv6_route"));
    return v6 is null ? null : RouteTableParser.DefaultGatewayV6(v6, name);
  }

  /// <summary>
  /// The nameservers this machine uses.
  /// </summary>
  /// <remarks>
  /// Where <c>/etc/resolv.conf</c> holds nothing but systemd-resolved's stub listener, the file
  /// describes this machine talking to itself and not the network — so the resolver's own upstream
  /// list is read instead, and the stub is reported only when that is unreadable too. Both are shown
  /// as they are found; neither is rewritten (PRD §5.3).
  /// </remarks>
  private static IReadOnlyList<string>? Resolvers(string procRoot) {
    // Relative to the proc root's parent, so a recorded tree carries its own /etc beside its /proc.
    var etc = Path.Combine(procRoot, "..", "etc", "resolv.conf");
    var content = ReadBytes(etc);
    if (content is null)
      return null;

    var config = ResolverConfigParser.Parse(content);
    if (!config.IsStubOnly)
      return config.Servers;

    var upstream = ReadBytes(Path.Combine(procRoot, "..", "run", "systemd", "resolve", "resolv.conf"));
    if (upstream is null)
      return config.Servers;

    var resolved = ResolverConfigParser.Parse(upstream);
    return resolved.Servers.Count > 0 ? resolved.Servers : config.Servers;
  }

  private static int? Signal(string procRoot, string name) {
    var content = ReadBytes(Path.Combine(procRoot, "net", "wireless"));
    return content is null ? null : WirelessStatusParser.Find(content, name)?.SignalDbm;
  }

  private static byte[]? ReadBytes(string path) {
    try {
      // Through the text reader: several files under /proc report a length of nought, and reading
      // by size would return an empty array for a file that has plenty in it.
      return File.Exists(path) ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path)) : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
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
      FanPercent: ReadFanPercent(device),
      FanRpm: ReadHwmon(device, "fan1_input"),
      FanCount: CountFans(device),
      // Neither AMD's sysfs nor Intel's publishes a per-engine figure at all: i915's lives behind a
      // perf counter that needs a privileged open, and amdgpu has only the one busy percentage.
      EncodePercent: Counter.Unknown(UnknownReason.NotImplementedHere),
      DecodePercent: Counter.Unknown(UnknownReason.NotImplementedHere)
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

  /// <summary>
  /// How hard the fan is being driven, as a percentage (PRD §50.1).
  /// </summary>
  /// <remarks>
  /// From <c>pwm1</c>, which is the duty cycle the driver has set, on hwmon's fixed scale of 0–255.
  /// It was previously taken from <c>fan1_input</c> as well — and that is revolutions a minute, so a
  /// fan at 1800 rpm was reported as being 1800 % of the way up its range. The two are different
  /// readings of the same fan, and the tachometer now goes to its own row (PRD §76).
  /// </remarks>
  private static Counter ReadFanPercent(string devicePath) {
    var duty = ReadHwmon(devicePath, "pwm1");
    if (!duty.HasValue)
      return duty;

    return duty.Value <= 255
      ? Counter.Of(duty.Value * 100 / 255)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// How many fans the card has, counted by the tachometers its hwmon node publishes.
  /// </summary>
  /// <remarks>
  /// Nought is a real answer and not a failure: a laptop card cooled by the chassis has no fan of
  /// its own, which is why its speed is unreadable. A node that publishes no tachometer at all says
  /// nothing about the count rather than claiming there are none.
  /// </remarks>
  private static Counter CountFans(string devicePath) {
    var hwmon = Path.Combine(devicePath, "hwmon");
    if (!Directory.Exists(hwmon))
      return Counter.Unknown(UnknownReason.NotImplementedHere);

    var found = 0;
    foreach (var node in Directory.EnumerateDirectories(hwmon))
      for (var fan = 1; fan <= 8; ++fan)
        if (File.Exists(Path.Combine(node, $"fan{fan.ToString(CultureInfo.InvariantCulture)}_input")))
          ++found;

    return Counter.Of((ulong)found);
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
