using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What NVIDIA's own library says about a card (PRD §50).
/// </summary>
/// <remarks>
/// <para>
/// The driver publishes nothing useful through <c>sysfs</c> — no utilisation, no VRAM, no
/// temperature, no power — and everything through NVML instead. Reading only <c>/sys/class/drm</c>
/// and concluding the numbers do not exist is exactly the mistake this file exists to correct: the
/// card that rendered as a column of <c>n/i</c> was at the time sitting at 100 % with 15.9 of its
/// 16 GB in use.
/// </para>
/// <para>
/// <c>libnvidia-ml.so.1</c> is optional in the way GTK is: absent on every machine without the
/// proprietary driver, which is most of them. The first call throws
/// <see cref="DllNotFoundException"/>, that is caught once, and the answer is remembered — the
/// same shape <c>DesktopApp</c> uses for a missing toolkit. Nothing here is on the sample path.
/// </para>
/// <para>
/// Devices are found by PCI address rather than by index. NVML's enumeration order is its own and
/// need not match the kernel's <c>cardN</c> numbering, so matching by position would confidently
/// attribute one card's readings to another on any machine with two.
/// </para>
/// </remarks>
internal static partial class NvmlReader {

  private const string _Library = "libnvidia-ml.so.1";

  private const uint _Success = 0;

  /// <summary>NVML_TEMPERATURE_GPU: the die, as opposed to a board sensor.</summary>
  private const uint _TemperatureGpu = 0;

  private const uint _ClockGraphics = 0;
  private const uint _ClockMemory = 2;

  /// <summary>Null until the first attempt; false once the library has been found missing.</summary>
  private static bool? _available;

  [StructLayout(LayoutKind.Sequential)]
  private struct Utilization {
    public uint Gpu;
    public uint Memory;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MemoryInfo {
    public ulong Total;
    public ulong Free;
    public ulong Used;
  }

  [LibraryImport(_Library, EntryPoint = "nvmlInit_v2")]
  private static partial uint Init();

  [LibraryImport(_Library, EntryPoint = "nvmlShutdown")]
  private static partial uint Shutdown();

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetHandleByPciBusId_v2")]
  private static partial uint GetHandleByPciBusId(ref byte busId, out nint device);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetName")]
  private static partial uint GetName(nint device, ref byte name, uint length);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
  private static partial uint GetUtilization(nint device, out Utilization utilization);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetMemoryInfo")]
  private static partial uint GetMemory(nint device, out MemoryInfo memory);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetTemperature")]
  private static partial uint GetTemperature(nint device, uint sensor, out uint celsius);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetPowerUsage")]
  private static partial uint GetPowerUsage(nint device, out uint milliwatts);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
  private static partial uint GetPowerCap(nint device, out uint milliwatts);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
  private static partial uint GetPowerConstraints(nint device, out uint minimum, out uint maximum);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetClockInfo")]
  private static partial uint GetClock(nint device, uint type, out uint megahertz);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetFanSpeed")]
  private static partial uint GetFanSpeed(nint device, out uint percent);

  /// <summary>
  /// Whether NVML is here at all, asked once.
  /// </summary>
  /// <remarks>
  /// The probe is <c>nvmlInit_v2</c> itself rather than a search for the file: a library that is
  /// present but too old to have the entry point fails the same way from a caller's point of view,
  /// and both mean "do not ask this again".
  /// </remarks>
  public static bool Available {
    get {
      if (_available is { } known)
        return known;

      try {
        var result = Init();
        _available = result == _Success;
        if (result == _Success)
          AppDomain.CurrentDomain.ProcessExit += static (_, _) => {
            try {
              Shutdown();
            } catch (DllNotFoundException) {
              // Unreachable in practice — it was loaded a moment ago — but an exception thrown
              // while the process is exiting would be reported as a crash.
            }
          };
      } catch (DllNotFoundException) {
        _available = false;
      } catch (EntryPointNotFoundException) {
        _available = false;
      }

      return _available.Value;
    }
  }

  /// <summary>
  /// Fills in whatever NVML knows about the card at a PCI address, leaving the rest alone.
  /// </summary>
  /// <param name="pciAddress">
  /// The kernel's own <c>PCI_SLOT_NAME</c>, <c>0000:01:00.0</c>, which is the format NVML's lookup
  /// wants.
  /// </param>
  /// <returns>The card as NVML sees it, or null when it does not know this address.</returns>
  public static GpuInfo? Describe(GpuInfo card, string? pciAddress) {
    if (pciAddress is null || !Available)
      return null;

    nint device;
    try {
      Span<byte> busId = stackalloc byte[64];
      var written = Encoding.ASCII.GetBytes(pciAddress, busId);
      busId[written] = 0;
      if (GetHandleByPciBusId(ref MemoryMarshal.GetReference(busId), out device) != _Success)
        return null;
    } catch (DllNotFoundException) {
      _available = false;
      return null;
    }

    return card with {
      Model = ReadName(device) ?? card.Model,
      BusyPercent = Percent(device, memory: false),
      MemoryBusyPercent = Percent(device, memory: true),
      MemoryUsedBytes = Memory(device, used: true),
      MemoryTotalBytes = Memory(device, used: false),
      // NVML counts whole degrees; the record counts thousandths, like hwmon does.
      TemperatureMilliCelsius = Scaled(GetTemperature(device, _TemperatureGpu, out var celsius), celsius, 1000),
      PowerMicrowatts = Scaled(GetPowerUsage(device, out var milliwatts), milliwatts, 1000),
      // The ceiling the card can ever reach, not the one dynamic boost has imposed this second: the
      // enforced cap moves constantly on a laptop, and the instantaneous draw routinely exceeds it,
      // so using it as the denominator renders "28.5 W of 20.0 W" and reads as a bug.
      PowerLimitMicrowatts = Scaled(GetPowerConstraints(device, out _, out var maximum), maximum, 1000),
      PowerCapMicrowatts = Scaled(GetPowerCap(device, out var cap), cap, 1000),
      CoreClockHertz = Scaled(GetClock(device, _ClockGraphics, out var core), core, 1_000_000),
      MemoryClockHertz = Scaled(GetClock(device, _ClockMemory, out var clock), clock, 1_000_000),
      FanPercent = Scaled(GetFanSpeed(device, out var fan), fan, 1),
    };
  }

  private static string? ReadName(nint device) {
    Span<byte> buffer = stackalloc byte[96];
    if (GetName(device, ref MemoryMarshal.GetReference(buffer), (uint)buffer.Length) != _Success)
      return null;

    var end = buffer.IndexOf((byte)0);
    return end <= 0 ? null : Encoding.ASCII.GetString(buffer[..end]);
  }

  private static Counter Percent(nint device, bool memory)
    => GetUtilization(device, out var utilization) == _Success
      ? Counter.Of(memory ? utilization.Memory : utilization.Gpu)
      : Counter.Unknown(UnknownReason.NotImplementedHere);

  private static Counter Memory(nint device, bool used)
    => GetMemory(device, out var memory) == _Success
      ? Counter.Of(used ? memory.Used : memory.Total)
      : Counter.Unknown(UnknownReason.NotImplementedHere);

  /// <summary>
  /// One reading, converted into the unit the record keeps it in.
  /// </summary>
  /// <remarks>
  /// Every one of these fails on its own: a laptop card has no fan of its own to report, a card with
  /// no configured cap has no enforced power limit, and both answer
  /// <c>NVML_ERROR_NOT_SUPPORTED</c> while every other reading beside them works. That is why the
  /// result is per-reading rather than the whole card being abandoned.
  /// </remarks>
  private static Counter Scaled(uint result, uint value, ulong scale)
    => result == _Success ? Counter.Of(value * scale) : Counter.Unknown(UnknownReason.NotImplementedHere);

}
