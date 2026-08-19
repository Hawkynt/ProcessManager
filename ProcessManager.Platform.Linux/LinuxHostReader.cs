using System.Globalization;
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

    var cores = new List<CoreDescriptor>();
    foreach (var logical in ReadCpuList(Path.Combine(cpuRoot, "online"))) {
      var topology = Path.Combine(cpuRoot, $"cpu{logical.ToString(CultureInfo.InvariantCulture)}", "topology");
      cores.Add(new(
        logical,
        ReadInt(Path.Combine(topology, "physical_package_id")),
        ReadInt(Path.Combine(topology, "core_id")),
        performance.Contains(logical) ? CoreKind.Performance
          : efficiency.Contains(logical) ? CoreKind.Efficiency
          : CoreKind.Unknown
      ));
    }

    return cores.Count > 0 ? new(cores) : CpuTopology.Empty;
  }

  private static IReadOnlyList<int> ReadCpuList(string path) {
    var text = TryReadText(path);
    return text is null ? [] : CpuList.Parse(text);
  }

  /// <summary>A number from one sysfs file, or -1 where the machine does not publish it.</summary>
  private static int ReadInt(string path)
    => int.TryParse(TryReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

  public static HostInfo Read(string procRoot, string sysRoot) {
    var cpuinfo = TryReadLines(Path.Combine(procRoot, "cpuinfo"));

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

      CpuModel = model,
      CpuVendor = vendor,
      CpuBaseHertz = ReadBaseFrequency(cpuRoot, model),
      CpuCurrentHertz = megahertzCount > 0
        ? Counter.Of((ulong)(megahertzTotal / megahertzCount * 1_000_000))
        : Counter.NotSupported,

      Sockets = sockets.Count > 0 ? Counter.Of((ulong)sockets.Count) : Counter.NotSupported,
      PhysicalCores = cores.Count > 0 ? Counter.Of((ulong)cores.Count) : Counter.NotSupported,
      LogicalProcessors = logical > 0 ? Counter.Of((ulong)logical) : Counter.NotSupported,
      NumaNodes = CountNumaNodes(sysRoot),

      L1DataBytes = ReadCache(cpuRoot, level: 1, "Data"),
      L1InstructionBytes = ReadCache(cpuRoot, level: 1, "Instruction"),
      L2Bytes = ReadCache(cpuRoot, level: 2, null),
      L3Bytes = ReadCache(cpuRoot, level: 3, null),
      Virtualisation = ReadVirtualisation(sysRoot),

      // The firmware tables are root-only on every distribution that ships them at all, so the
      // module facts are a privileged read we do not do yet. Not permitted, rather than zero.
      MemoryTransfersPerSecond = Counter.NotPermitted,
      MemorySlotsUsed = Counter.NotPermitted,
      MemorySlotsTotal = Counter.NotPermitted,
    };
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
