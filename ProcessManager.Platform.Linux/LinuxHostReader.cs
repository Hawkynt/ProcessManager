using System.Globalization;
using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Reads the machine's fixed description from <c>/proc/cpuinfo</c> and <c>/sys</c> (PRD §46, §47).
/// </summary>
/// <remarks>
/// Managed file APIs here, deliberately — unlike the sampling path, which uses raw syscalls because
/// it runs every second for every process. This runs once for the machine, so the readable version
/// is the right trade (PRD §71).
/// </remarks>
internal static class LinuxHostReader {

  /// <summary>
  /// Which logical processor sits on which core, socket and kind (PRD §46).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read once, like everything else here. Only online processors appear: an offline one has no
  /// topology directory, and inventing an entry for it would put a permanently cold cell in the
  /// middle of a heat map.
  /// </para>
  /// <para>
  /// The kind comes from the kernel's own hybrid PMUs — <c>/sys/devices/cpu_core/cpus</c> and
  /// <c>/sys/devices/cpu_atom/cpus</c> — which exist only on a hybrid part and name exactly which
  /// processors are which. That is the authoritative source and there is no need to guess when it
  /// is present; where it is absent, the machine is not hybrid, or is hybrid in a way this kernel
  /// does not describe, and the honest answer is that we do not know (§5.3).
  /// </para>
  /// </remarks>
  public static CpuTopology ReadTopology(string sysRoot) {
    var cpuRoot = Path.Combine(sysRoot, "devices", "system", "cpu");
    if (!Directory.Exists(cpuRoot))
      return CpuTopology.Empty;

    var performance = ReadCpuList(Path.Combine(sysRoot, "devices", "cpu_core", "cpus"));
    var efficiency = ReadCpuList(Path.Combine(sysRoot, "devices", "cpu_atom", "cpus"));
    var nodes = ReadNodeMap(sysRoot);
    var online = ReadCpuList(Path.Combine(cpuRoot, "online"));
    var capacities = performance.Count > 0 || efficiency.Count > 0
      ? new Dictionary<int, int>()
      : ReadCapacities(cpuRoot, online);

    var cores = new List<CoreDescriptor>();
    foreach (var logical in online) {
      var topology = Path.Combine(cpuRoot, $"cpu{logical.ToString(CultureInfo.InvariantCulture)}", "topology");
      cores.Add(new(
        logical,
        ReadInt(Path.Combine(topology, "physical_package_id")),
        ReadInt(Path.Combine(topology, "core_id")),
        performance.Contains(logical) ? CoreKind.Performance
          : efficiency.Contains(logical) ? CoreKind.Efficiency
          : KindFromCapacity(capacities, logical),
        nodes.GetValueOrDefault(logical, -1)
      ));
    }

    return cores.Count > 0 ? new(cores) : CpuTopology.Empty;
  }

  /// <summary>
  /// What each processor's scheduling capacity is, where the kernel publishes one (PRD §46).
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is how big.LITTLE is told apart. The hybrid PMU directories above are Intel's and exist on
  /// no ARM machine; what an ARM kernel with capacity-aware scheduling publishes instead is
  /// <c>cpu_capacity</c> — the number the scheduler itself uses to decide that one core does more
  /// work per second than another, normalised so the fastest is 1024. It is not a guess from
  /// differing maximum clocks: it is the kernel's own answer to exactly the question the heat map is
  /// asking, and where the kernel does not publish it there is no answer here either (PRD §5.3).
  /// </para>
  /// <para>
  /// Read only when the hybrid PMUs said nothing, because where both exist they agree and the PMU
  /// directories are two reads against one per processor.
  /// </para>
  /// </remarks>
  private static Dictionary<int, int> ReadCapacities(string cpuRoot, IReadOnlyList<int> online) {
    var capacities = new Dictionary<int, int>();
    foreach (var logical in online) {
      var text = TryReadText(Path.Combine(cpuRoot, $"cpu{logical.ToString(CultureInfo.InvariantCulture)}", "cpu_capacity"));
      if (text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity) && capacity > 0)
        capacities[logical] = capacity;
    }

    // Every processor alike is a machine that is not hybrid, and saying so as "all performance
    // cores" would put a distinction on the page that this silicon does not have.
    var distinct = new HashSet<int>(capacities.Values);
    return distinct.Count > 1 ? capacities : [];
  }

  /// <summary>
  /// The fastest capacity is a performance core and anything slower is an efficiency one.
  /// </summary>
  /// <remarks>
  /// Two kinds because that is what the page has room to say. A three-tier part — and they exist —
  /// puts its middle tier with the slow ones, which is the truthful half of the distinction: the
  /// question a heat map answers is whether the fast cores are the busy ones.
  /// </remarks>
  private static CoreKind KindFromCapacity(Dictionary<int, int> capacities, int logical) {
    if (capacities.Count == 0 || !capacities.TryGetValue(logical, out var capacity))
      return CoreKind.Unknown;

    var fastest = 0;
    foreach (var value in capacities.Values)
      fastest = Math.Max(fastest, value);

    return capacity == fastest ? CoreKind.Performance : CoreKind.Efficiency;
  }

  /// <summary>
  /// Which NUMA node each logical processor belongs to (PRD §46).
  /// </summary>
  /// <remarks>
  /// From the node's own <c>cpulist</c> rather than from a <c>nodeN</c> symlink under each
  /// processor: one file per node instead of one directory listing per processor, which on a
  /// two-node machine is two reads instead of a hundred and ninety-two.
  /// <para>
  /// The nodes are walked by number and stop at the first gap, like the memory read below. A machine
  /// with a hole in its node numbering — nodes 0 and 2, no node 1 — loses the ones past the hole,
  /// which is a machine none of us has and a shape the kernel does not produce for CPU nodes.
  /// </para>
  /// </remarks>
  private static Dictionary<int, int> ReadNodeMap(string sysRoot) {
    var map = new Dictionary<int, int>();
    var nodeRoot = Path.Combine(sysRoot, "devices", "system", "node");
    if (!Directory.Exists(nodeRoot))
      return map;

    for (var node = 0; ; ++node) {
      var path = Path.Combine(nodeRoot, $"node{node.ToString(CultureInfo.InvariantCulture)}", "cpulist");
      if (!File.Exists(path))
        break;

      foreach (var logical in ReadCpuList(path))
        map[logical] = node;
    }

    return map;
  }

  /// <summary>
  /// What this processor can do, from whichever source this architecture has.
  /// </summary>
  /// <remarks>
  /// x86 has <c>CPUID</c>; ARM has no such instruction from user code and publishes the same
  /// information as two words of bits in the auxiliary vector instead. Both decode into the same
  /// shape, so nothing above this line has to know which machine it is describing (PRD §46).
  /// </remarks>
  private static IReadOnlyList<CpuFeature> LiveFeatures() {
    if (CpuId.IsSupported)
      return CpuId.Features;

    // The two architectures share not one bit position, so the table is chosen by what this process
    // actually is rather than by what the words look like: every bit is assigned in both, and
    // decoding one with the other's table produces a full and entirely wrong list with nothing in it
    // for a check to fail on (PRD §46).
    var architecture = RuntimeInformation.ProcessArchitecture;
    if (architecture is not (Architecture.Arm64 or Architecture.Arm))
      return [];

    var (hwcap, hwcap2) = Native.HardwareCapabilities();
    return architecture == Architecture.Arm
      ? ArmFeatures.DecodeArm32(hwcap, hwcap2)
      : ArmFeatures.Decode(hwcap, hwcap2);
  }

  private static string? LiveSignature(string sysRoot) {
    if (CpuId.IsSupported)
      return CpuId.Signature;

    if (RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.Arm))
      return null;

    // MIDR_EL1 is privileged, but the kernel publishes cpu0's copy of it here — the only way a user
    // process can learn which part it is running on.
    var text = TryReadText(Path.Combine(sysRoot, "devices", "system", "cpu", "cpu0", "regs", "identification", "midr_el1"));
    if (text is null)
      return null;

    var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
    return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var midr)
      ? ArmFeatures.Signature(midr)
      : null;
  }

  private static IReadOnlyList<int> ReadCpuList(string path) {
    var text = TryReadText(path);
    return text is null ? [] : CpuList.Parse(text);
  }

  /// <summary>A number from one sysfs file, or -1 where the machine does not publish it.</summary>
  private static int ReadInt(string path)
    => int.TryParse(TryReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

  /// <param name="live">
  /// Whether this is the machine we are running on, rather than a recorded one.
  /// </param>
  /// <remarks>
  /// <c>CPUID</c> answers about the processor executing the instruction, and there is no way to ask
  /// it about anybody else's. So it may only be consulted when the files being read belong to this
  /// machine too — a <c>--probe-root</c> replay that mixed a fixture's core count with this laptop's
  /// feature list would be describing two machines in one table (PRD §9.4).
  /// </remarks>
  /// <param name="elevatedFirmware">
  /// Asked for the SMBIOS table when this process may not read it itself, and only when the helper is
  /// already running: a machine description must never be the thing that raises a password prompt
  /// (PRD §8, §5.4). Null where there is no helper, which is the ordinary case.
  /// </param>
  public static HostInfo Read(string procRoot, string sysRoot, bool live, Func<byte[]?>? elevatedFirmware = null) {
    var cpuinfo = TryReadLines(Path.Combine(procRoot, "cpuinfo"));
    var memoryHardware = ReadMemoryHardware(sysRoot, elevatedFirmware);

    // "physical id" counts sockets and "core id" counts cores within one; a machine reporting
    // neither is a container or an architecture that does not expose topology, and the honest
    // answer there is that we do not know rather than one of each.
    var sockets = new HashSet<string>(StringComparer.Ordinal);
    var cores = new HashSet<string>(StringComparer.Ordinal);
    var logical = 0;
    string? model = null, vendor = null;
    double megahertzTotal = 0;
    var megahertzCount = 0;
    string? currentSocket = null;

    foreach (var line in cpuinfo) {
      var separator = line.IndexOf(':', StringComparison.Ordinal);
      if (separator < 0)
        continue;

      var key = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      switch (key) {
        case "processor": ++logical; break;
        case "model name": model ??= value; break;
        case "vendor_id": vendor ??= value; break;
        case "physical id":
          currentSocket = value;
          sockets.Add(value);
          break;

        case "core id":
          // A core is only unique within its socket: two sockets both have a core 0.
          cores.Add($"{currentSocket}/{value}");
          break;

        case "cpu MHz":
          if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var megahertz)) {
            megahertzTotal += megahertz;
            ++megahertzCount;
          }

          break;
      }
    }

    var cpuRoot = Path.Combine(sysRoot, "devices", "system", "cpu");
    return new() {
      HostName = SafeHostName(),
      OperatingSystem = ReadPrettyName(Path.Combine(procRoot, "..", "etc", "os-release"))
        ?? System.Runtime.InteropServices.RuntimeInformation.OSDescription,
      OperatingSystemVersion = TryReadText(Path.Combine(procRoot, "sys", "kernel", "osrelease"))
        ?? Environment.OSVersion.Version.ToString(),
      Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
      BootTimeUtcTicks = ReadBootTime(procRoot),

      // /proc/cpuinfo first, because a fixture replay has to describe the fixture; CPUID fills in
      // where the file is silent, which is what a stripped container looks like.
      // /proc/cpuinfo first, because it is what the kernel believes; CPUID fills in where the file
      // is silent, which is what a stripped container looks like.
      CpuModel = model ?? (live ? CpuId.Brand : null),
      CpuVendor = vendor ?? (live ? CpuId.Vendor : null),
      CpuSignature = live ? LiveSignature(sysRoot) : null,
      CpuFeatures = live ? LiveFeatures() : [],
      CpuBaseHertz = ReadBaseFrequency(cpuRoot, model),
      // Across every processor rather than from cpu0. The parts differ: this laptop's favoured cores
      // top out at 5.0 GHz and the rest at 4.9, so cpu0's ceiling is not the machine's and the page
      // would disagree with lscpu about what the processor can do.
      CpuMinimumHertz = ReadKilohertzExtreme(cpuRoot, "cpuinfo_min_freq", highest: false),
      CpuMaximumHertz = ReadKilohertzExtreme(cpuRoot, "cpuinfo_max_freq", highest: true),
      CpuGovernor = TryReadText(Path.Combine(cpuRoot, "cpu0", "cpufreq", "scaling_governor")),
      CpuScalingDriver = TryReadText(Path.Combine(cpuRoot, "cpu0", "cpufreq", "scaling_driver")),
      CpuCurrentHertz = megahertzCount > 0
        ? Counter.Of((ulong)(megahertzTotal / megahertzCount * 1_000_000))
        : Counter.NotSupported,

      Sockets = sockets.Count > 0 ? Counter.Of((ulong)sockets.Count) : Counter.NotSupported,
      PhysicalCores = cores.Count > 0 ? Counter.Of((ulong)cores.Count) : Counter.NotSupported,
      LogicalProcessors = logical > 0 ? Counter.Of((ulong)logical) : Counter.NotSupported,
      NumaNodes = CountNumaNodes(sysRoot),
      NumaMemoryBytes = ReadNumaMemory(sysRoot),

      L1DataBytes = ReadCache(cpuRoot, level: 1, "Data"),
      L1InstructionBytes = ReadCache(cpuRoot, level: 1, "Instruction"),
      L2Bytes = ReadCache(cpuRoot, level: 2, null),
      L3Bytes = ReadCache(cpuRoot, level: 3, null),
      Virtualisation = ReadVirtualisation(sysRoot),

      // The firmware tables are root-only on every distribution that ships them at all, so these
      // read when the program is root or when the helper already is, and say "not permitted"
      // otherwise — never zero, because the difference between installed and usable is the
      // hardware-reserved figure and a guess at it would be a claim about the machine (PRD §47).
      InstalledMemoryBytes = memoryHardware.InstalledBytes,
      MemoryTransfersPerSecond = memoryHardware.TransfersPerSecond,
      MemoryFormFactor = memoryHardware.FormFactor,
      MemorySlotsUsed = memoryHardware.SlotsUsed,
      MemorySlotsTotal = memoryHardware.SlotsTotal,

      // How many channels the modules are spread over is in no SMBIOS record and in no file the
      // kernel publishes for an ordinary machine: type 17 describes a device and its slot, never the
      // controller's interleave, and the locator strings that look like channel names — "ChannelA-
      // DIMM0" — are vendor-formatted text a parser would be guessing at. Refused, and said so
      // (PRD §47, §5.3).
      MemoryChannels = Counter.NotSupported,
    };
  }

  /// <summary>
  /// What the firmware says about the memory modules (PRD §47).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Three outcomes, and they are three different sentences on the page. The table read straight out
  /// of <c>/sys</c> is the answer when the program is root — or when a <c>--probe-root</c> replay
  /// carries a recorded copy, which is what makes the whole path testable on a machine whose own
  /// table nobody here may read. A refusal is passed to the helper, but only if it is already
  /// running: describing the machine must never be the thing that raises a password prompt.
  /// And a machine with no <c>CONFIG_DMI</c> at all — every ARM board, most virtual machines — has
  /// no such file, which is "not supported here" and not "you may not look".
  /// </para>
  /// </remarks>
  private static Smbios.MemoryHardware ReadMemoryHardware(string sysRoot, Func<byte[]?>? elevatedFirmware) {
    var path = Path.Combine(sysRoot, "firmware", "dmi", "tables", "DMI");
    try {
      return Smbios.ReadMemory(File.ReadAllBytes(path));
    } catch (UnauthorizedAccessException) {
      // The ordinary case on a machine this is not root on.
    } catch (IOException) {
      return Smbios.MemoryHardware.Unreadable(
        File.Exists(path) ? UnknownReason.NotPermitted : UnknownReason.NotSupportedOnPlatform
      );
    }

    var table = elevatedFirmware?.Invoke();
    return table is { Length: > 0 }
      ? Smbios.ReadMemory(table)
      : Smbios.MemoryHardware.Unreadable(UnknownReason.NotPermitted);
  }

  private static string SafeHostName() {
    try {
      return Environment.MachineName;
    } catch (InvalidOperationException) {
      // A container with no hostname configured throws rather than returning anything.
      return "?";
    }
  }

  /// <summary>
  /// The rated speed. <c>base_frequency</c> is the kernel's own answer and is exact where it exists;
  /// otherwise the number is parsed out of the model name, which is where Intel and AMD both put it.
  /// </summary>
  private static Counter ReadBaseFrequency(string cpuRoot, string? model) {
    var text = TryReadText(Path.Combine(cpuRoot, "cpu0", "cpufreq", "base_frequency"));
    if (text is not null && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilohertz))
      return Counter.Of(kilohertz * 1000);

    if (model is null)
      return Counter.NotSupported;

    // "… i9-11950H @ 2.60GHz" — the trailing clause, when the vendor bothered to include one.
    var at = model.LastIndexOf('@');
    if (at < 0)
      return Counter.NotSupported;

    var tail = model[(at + 1)..].Trim();
    var scale = tail.EndsWith("GHz", StringComparison.OrdinalIgnoreCase) ? 1_000_000_000d
      : tail.EndsWith("MHz", StringComparison.OrdinalIgnoreCase) ? 1_000_000d
      : 0d;

    if (scale == 0)
      return Counter.NotSupported;

    return double.TryParse(tail[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
      ? Counter.Of((ulong)(number * scale))
      : Counter.NotSupported;
  }

  /// <summary>
  /// The extreme of one of cpufreq's frequency files across every processor, in kilohertz.
  /// </summary>
  /// <remarks>
  /// A kernel with no cpufreq at all — a virtual machine, a part whose clock the firmware owns —
  /// has no such files, and that is not a processor with no speed range. Unsupported rather than
  /// zero (PRD §5.3). Read once with the rest of the host description, so walking sixteen
  /// directories costs nothing per sample (§71).
  /// </remarks>
  private static Counter ReadKilohertzExtreme(string cpuRoot, string file, bool highest) {
    if (!Directory.Exists(cpuRoot))
      return Counter.NotSupported;

    var found = false;
    var extreme = 0ul;
    foreach (var logical in ReadCpuList(Path.Combine(cpuRoot, "online"))) {
      var path = Path.Combine(cpuRoot, $"cpu{logical.ToString(CultureInfo.InvariantCulture)}", "cpufreq", file);
      if (TryReadText(path) is not { } text
          || !ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilohertz))
        continue;

      extreme = found ? highest ? Math.Max(extreme, kilohertz) : Math.Min(extreme, kilohertz) : kilohertz;
      found = true;
    }

    return found ? Counter.Of(extreme * 1000) : Counter.NotSupported;
  }

  /// <summary>
  /// The size of one cache level. Reported per logical processor, so cpu0's view is representative —
  /// and it is the same view Task Manager shows.
  /// </summary>
  private static Counter ReadCache(string cpuRoot, int level, string? type) {
    var cacheRoot = Path.Combine(cpuRoot, "cpu0", "cache");
    if (!Directory.Exists(cacheRoot))
      return Counter.NotSupported;

    foreach (var index in Directory.EnumerateDirectories(cacheRoot, "index*")) {
      if (TryReadText(Path.Combine(index, "level")) is not { } levelText
          || !int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var found)
          || found != level)
        continue;

      if (type is not null && !string.Equals(TryReadText(Path.Combine(index, "type")), type, StringComparison.Ordinal))
        continue;

      if (TryReadText(Path.Combine(index, "size")) is { } size && TryParseSize(size, out var bytes))
        return Counter.Of(bytes);
    }

    return Counter.NotSupported;
  }

  /// <summary>Sizes here are written as "48K" or "24576K"; occasionally "1M".</summary>
  private static bool TryParseSize(string text, out ulong bytes) {
    bytes = 0;
    text = text.Trim();
    if (text.Length == 0)
      return false;

    var multiplier = 1ul;
    var last = char.ToUpperInvariant(text[^1]);
    if (last is 'K' or 'M' or 'G') {
      multiplier = last switch { 'K' => 1024ul, 'M' => 1024ul * 1024, _ => 1024ul * 1024 * 1024 };
      text = text[..^1];
    }

    if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
      return false;

    bytes = number * multiplier;
    return true;
  }

  private static Counter CountNumaNodes(string sysRoot) {
    var nodeRoot = Path.Combine(sysRoot, "devices", "system", "node");
    if (!Directory.Exists(nodeRoot))
      // A kernel built without NUMA has no directory at all, which is not the same as having none.
      return Counter.NotSupported;

    var count = 0;
    foreach (var _ in Directory.EnumerateDirectories(nodeRoot, "node*"))
      ++count;

    return count > 0 ? Counter.Of((ulong)count) : Counter.NotSupported;
  }

  /// <summary>
  /// How much memory sits on each NUMA node (PRD §47).
  /// </summary>
  /// <remarks>
  /// One <c>meminfo</c> per node, read once when the machine is described rather than once a sample:
  /// how much memory a node <em>has</em> does not change while it is running, and it is the
  /// distribution that answers whether a thread pinned to node 1 can allocate at all.
  /// <para>
  /// Read in node order rather than in directory order, because a directory listing is not sorted
  /// and <c>node10</c> sorts before <c>node2</c> as text — which would report the machine's memory
  /// as belonging to the wrong nodes rather than merely out of order.
  /// </para>
  /// </remarks>
  private static IReadOnlyList<Counter> ReadNumaMemory(string sysRoot) {
    var nodeRoot = Path.Combine(sysRoot, "devices", "system", "node");
    if (!Directory.Exists(nodeRoot))
      return [];

    var nodes = new List<Counter>();
    for (var node = 0; ; ++node) {
      var path = Path.Combine(nodeRoot, $"node{node.ToString(CultureInfo.InvariantCulture)}", "meminfo");
      if (!File.Exists(path))
        break;

      nodes.Add(ReadNodeTotal(path));
    }

    return nodes;
  }

  /// <summary>
  /// A node's <c>MemTotal</c>, out of lines that read <c>Node 0 MemTotal: 65900544 kB</c>.
  /// </summary>
  private static Counter ReadNodeTotal(string path) {
    var lines = TryReadLines(path);
    if (lines.Length == 0)
      // The file is there and would not open, or opened empty: a permission or a race, not a
      // machine without the node.
      return Counter.NotPermitted;

    foreach (var line in lines) {
      var marker = line.IndexOf("MemTotal:", StringComparison.Ordinal);
      if (marker < 0)
        continue;

      var fields = line[(marker + "MemTotal:".Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length > 0 && ulong.TryParse(fields[0], CultureInfo.InvariantCulture, out var kilobytes))
        return Counter.Of(kilobytes * 1024);
    }

    return Counter.NotSupported;
  }

  /// <summary>
  /// What the machine is running on, when the firmware admits it.
  /// </summary>
  /// <remarks>
  /// The DMI product name is world-readable even though the full tables are not, and hypervisors
  /// write recognisable strings into it. Reported as read rather than mapped to a fixed vocabulary:
  /// "KVM" and "VMware Virtual Platform" are more use than a boolean.
  /// </remarks>
  private static string? ReadVirtualisation(string sysRoot) {
    var product = TryReadText(Path.Combine(sysRoot, "class", "dmi", "id", "product_name"));
    if (product is null)
      return null;

    foreach (var known in (ReadOnlySpan<string>)["KVM", "QEMU", "VMware", "VirtualBox", "Xen", "Hyper-V", "Virtual Machine"])
      if (product.Contains(known, StringComparison.OrdinalIgnoreCase))
        return product;

    return null;
  }

  private static string? ReadPrettyName(string osRelease) {
    foreach (var line in TryReadLines(osRelease)) {
      if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
        continue;

      return line["PRETTY_NAME=".Length..].Trim().Trim('"');
    }

    return null;
  }

  private static string? TryReadText(string path) {
    try {
      return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>
  /// When the machine came up, from <c>/proc/stat</c>'s <c>btime</c> (PRD §104).
  /// </summary>
  /// <remarks>
  /// That line rather than <c>/proc/uptime</c>, which would have to be subtracted from the wall
  /// clock and would therefore give a slightly different answer every time it was asked. <c>btime</c>
  /// is the instant itself, in seconds since the epoch, and does not move.
  /// </remarks>
  private static Counter ReadBootTime(string procRoot) {
    foreach (var line in TryReadLines(Path.Combine(procRoot, "stat"))) {
      if (!line.StartsWith("btime ", StringComparison.Ordinal))
        continue;

      return long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
          ? Counter.Of(DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond)
          : Counter.Unknown(UnknownReason.CounterInvalid);
    }

    // The file was looked at and had no such line — a kernel too old for it, or a recorded tree that
    // did not capture one. Not "nobody asked", which is what a caller would wait for.
    return Counter.Unknown(UnknownReason.SourceGone);
  }

  private static string[] TryReadLines(string path) {
    try {
      return File.Exists(path) ? File.ReadAllLines(path) : [];
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

}
