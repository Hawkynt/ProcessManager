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

  /// <summary>NVML_ERROR_NOT_FOUND: nothing matched, which for a sample window means nothing ran.</summary>
  private const uint _NotFound = 6;

  /// <summary>NVML_ERROR_INSUFFICIENT_SIZE: the count that came back is the count needed.</summary>
  private const uint _InsufficientSize = 7;

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

  #region per-process (PRD §19)

  /// <summary>
  /// One process's use of one card, merged from the three calls that each know part of it.
  /// </summary>
  /// <remarks>
  /// The <c>Has</c> flags are not decoration. NVML answers the memory question and the utilisation
  /// question through different calls that fail independently — a card will list its processes and
  /// their VRAM while refusing to sample their utilisation at all — so a sample that carries a
  /// utilisation of nought because nobody asked is exactly the confident zero §72.3 forbids.
  /// </remarks>
  internal struct NvmlProcessSample {

    public ulong DedicatedBytes;
    public bool HasMemory;

    /// <summary>NVML's <c>sm</c>: graphics and compute together, the driver not splitting them.</summary>
    public uint BusyPercent;
    public uint EncodePercent;
    public uint DecodePercent;
    public bool HasUtilization;

    /// <summary>When the driver took the reading, in its own microseconds. Newest wins.</summary>
    public ulong TimeStamp;

    /// <summary>True when the pid appeared in the compute list, false when only in the graphics one.</summary>
    public bool IsCompute;

  }

  /// <summary>nvmlProcessInfo_v2_t, which is what both <c>_v2</c> and <c>_v3</c> take.</summary>
  [StructLayout(LayoutKind.Sequential)]
  internal struct ProcessInfo {
    public uint Pid;
    public ulong UsedGpuMemory;
    public uint GpuInstanceId;
    public uint ComputeInstanceId;
  }

  /// <summary>
  /// nvmlProcessInfo_v1_t: the shape the unsuffixed entry point still takes.
  /// </summary>
  /// <remarks>
  /// Two fields rather than four, and passing the wider struct to it would have NVML stride through
  /// the array at the wrong pitch — every entry after the first would be read out of the middle of
  /// its neighbour. That is why the fallback needs its own buffer rather than only its own name.
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  internal struct ProcessInfoV1 {
    public uint Pid;
    public ulong UsedGpuMemory;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct ProcessUtilization {
    public uint Pid;
    public ulong TimeStamp;
    public uint SmUtil;
    public uint MemUtil;
    public uint EncUtil;
    public uint DecUtil;
  }

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetComputeRunningProcesses_v3")]
  private static partial uint GetComputeProcessesV3(nint device, ref uint count, ref ProcessInfo first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetComputeRunningProcesses_v2")]
  private static partial uint GetComputeProcessesV2(nint device, ref uint count, ref ProcessInfo first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetComputeRunningProcesses")]
  private static partial uint GetComputeProcessesV1(nint device, ref uint count, ref ProcessInfoV1 first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses_v3")]
  private static partial uint GetGraphicsProcessesV3(nint device, ref uint count, ref ProcessInfo first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses_v2")]
  private static partial uint GetGraphicsProcessesV2(nint device, ref uint count, ref ProcessInfo first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses")]
  private static partial uint GetGraphicsProcessesV1(nint device, ref uint count, ref ProcessInfoV1 first);

  [LibraryImport(_Library, EntryPoint = "nvmlDeviceGetProcessUtilization")]
  private static partial uint GetProcessUtilization(nint device, ref ProcessUtilization first, ref uint count, ulong since);

  /// <summary>
  /// Which spelling of the process list this driver has, decided once per entry point.
  /// </summary>
  /// <remarks>
  /// The suffix is the whole problem. <c>_v3</c> is what a current driver exports; a driver from
  /// before 2022 has only <c>_v2</c>, and one older still only the unsuffixed name with a narrower
  /// struct behind it. Binding the newest and giving up on <see cref="EntryPointNotFoundException"/>
  /// would report a machine with a working card as having no per-process accounting, so each is
  /// tried in turn and the answer is remembered — the probe, not the exception, is what costs
  /// anything.
  /// </remarks>
  private enum ListVersion : byte { Unknown = 0, V3, V2, V1, None }

  private static ListVersion _computeVersion;
  private static ListVersion _graphicsVersion;

  /// <summary>Null until the first attempt; false once the call has been found missing or refused.</summary>
  private static bool? _utilizationAvailable;

  /// <summary>
  /// The handle for a card, by the kernel's PCI address, or 0 when NVML does not know it.
  /// </summary>
  /// <remarks>
  /// By address rather than by index, for the reason the file's own remarks give: NVML's enumeration
  /// order is its own. Handles do not change while the library is loaded, so a caller may keep one.
  /// </remarks>
  public static nint DeviceAt(string pciAddress) {
    ArgumentNullException.ThrowIfNull(pciAddress);
    if (!Available)
      return 0;

    try {
      Span<byte> busId = stackalloc byte[64];
      var written = Encoding.ASCII.GetBytes(pciAddress, busId);
      busId[written] = 0;
      return GetHandleByPciBusId(ref MemoryMarshal.GetReference(busId), out var device) == _Success ? device : 0;
    } catch (DllNotFoundException) {
      _available = false;
      return 0;
    }
  }

  /// <summary>
  /// The processes with memory on one card, merged into <paramref name="processes"/> by pid.
  /// </summary>
  /// <remarks>
  /// Both lists, and their union rather than either alone. A pid appears in the compute list when it
  /// has a CUDA context and in the graphics list when it has a rendering one, and plenty have both —
  /// but a process that only ever draws is in the graphics list only, which is why reading compute
  /// alone shows an empty table on a desktop that is plainly using its card. Where a pid is in both
  /// the memory is the same allocation counted twice, so the larger is taken rather than the sum.
  /// </remarks>
  /// <param name="buffer">
  /// Reused across samples by the caller. Grown by the caller, not here, so that this allocates
  /// nothing in the steady state (PRD §4).
  /// </param>
  /// <returns>False when the card cannot be asked at all, so the caller can say why rather than zero.</returns>
  public static bool TryReadProcessMemory(
    nint device,
    Dictionary<int, NvmlProcessSample> processes,
    ProcessInfo[] buffer,
    ProcessInfoV1[] narrowBuffer
  ) {
    ArgumentNullException.ThrowIfNull(processes);
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentNullException.ThrowIfNull(narrowBuffer);
    if (device == 0 || buffer.Length == 0 || narrowBuffer.Length == 0)
      return false;

    var compute = ReadList(device, ref _computeVersion, compute: true, processes, buffer, narrowBuffer);
    var graphics = ReadList(device, ref _graphicsVersion, compute: false, processes, buffer, narrowBuffer);
    return compute || graphics;
  }

  private static bool ReadList(
    nint device,
    ref ListVersion version,
    bool compute,
    Dictionary<int, NvmlProcessSample> processes,
    ProcessInfo[] buffer,
    ProcessInfoV1[] narrowBuffer
  ) {
    while (true) {
      switch (version) {
        case ListVersion.None:
          return false;

        case ListVersion.V1: {
          var count = (uint)narrowBuffer.Length;
          var result = compute
            ? GetComputeProcessesV1(device, ref count, ref narrowBuffer[0])
            : GetGraphicsProcessesV1(device, ref count, ref narrowBuffer[0]);
          if (result != _Success)
            return result == _NotFound;

          for (var i = 0; i < count && i < narrowBuffer.Length; ++i)
            Merge(processes, (int)narrowBuffer[i].Pid, narrowBuffer[i].UsedGpuMemory, compute);

          return true;
        }

        case ListVersion.V2:
        case ListVersion.V3: {
          var count = (uint)buffer.Length;
          var result = version == ListVersion.V3
            ? compute
              ? GetComputeProcessesV3(device, ref count, ref buffer[0])
              : GetGraphicsProcessesV3(device, ref count, ref buffer[0])
            : compute
              ? GetComputeProcessesV2(device, ref count, ref buffer[0])
              : GetGraphicsProcessesV2(device, ref count, ref buffer[0]);
          if (result != _Success)
            return result == _NotFound;

          for (var i = 0; i < count && i < buffer.Length; ++i)
            Merge(processes, (int)buffer[i].Pid, buffer[i].UsedGpuMemory, compute);

          return true;
        }

        default:
          version = Probe(device, compute, buffer, narrowBuffer);
          if (version == ListVersion.None)
            return false;

          continue;
      }
    }
  }

  /// <summary>
  /// Which of the three names this driver answers to, found by calling each once.
  /// </summary>
  /// <remarks>
  /// A missing entry point throws on the call rather than on the declaration — the source-generated
  /// import binds lazily — so the probe is three calls and not a symbol table walk. The buffer is
  /// long enough that a real answer is a real answer, and the result is discarded either way: this
  /// runs once, and the caller reads properly the moment it knows which name to use.
  /// </remarks>
  private static ListVersion Probe(nint device, bool compute, ProcessInfo[] buffer, ProcessInfoV1[] narrowBuffer) {
    try {
      var count = (uint)buffer.Length;
      var result = compute
        ? GetComputeProcessesV3(device, ref count, ref buffer[0])
        : GetGraphicsProcessesV3(device, ref count, ref buffer[0]);
      if (result is _Success or _NotFound or _InsufficientSize)
        return ListVersion.V3;
    } catch (EntryPointNotFoundException) {
      // A driver from before the v3 lists. Fall through.
    } catch (DllNotFoundException) {
      _available = false;
      return ListVersion.None;
    }

    try {
      var count = (uint)buffer.Length;
      var result = compute
        ? GetComputeProcessesV2(device, ref count, ref buffer[0])
        : GetGraphicsProcessesV2(device, ref count, ref buffer[0]);
      if (result is _Success or _NotFound or _InsufficientSize)
        return ListVersion.V2;
    } catch (EntryPointNotFoundException) {
      // Older still.
    }

    try {
      var count = (uint)narrowBuffer.Length;
      var result = compute
        ? GetComputeProcessesV1(device, ref count, ref narrowBuffer[0])
        : GetGraphicsProcessesV1(device, ref count, ref narrowBuffer[0]);
      if (result is _Success or _NotFound or _InsufficientSize)
        return ListVersion.V1;
    } catch (EntryPointNotFoundException) {
      // No spelling of it at all, which is a library too old to be worth asking again.
    }

    return ListVersion.None;
  }

  private static void Merge(Dictionary<int, NvmlProcessSample> processes, int pid, ulong bytes, bool compute) {
    if (pid <= 0)
      return;

    processes.TryGetValue(pid, out var sample);
    // The same allocation seen twice, not two allocations: a process with both a compute and a
    // graphics context is listed by both calls with one figure behind them.
    if (!sample.HasMemory || bytes > sample.DedicatedBytes)
      sample.DedicatedBytes = bytes;

    sample.HasMemory = true;
    sample.IsCompute |= compute;
    processes[pid] = sample;
  }

  /// <summary>
  /// How far back to ask for utilisation samples, in microseconds.
  /// </summary>
  /// <remarks>
  /// The parameter is a timestamp, not a duration, and getting it wrong produces numbers that look
  /// entirely plausible: pass 0 and NVML returns every sample it still holds — several seconds of
  /// them, hundreds of entries, oldest first — and reading the first is reporting what a process was
  /// doing some time ago. Pass the previous call's timestamp and roughly one call in three comes back
  /// <c>NOT_FOUND</c>, because the driver's own sampler runs on its own clock and has published
  /// nothing new; the column then flickers between a figure and a blank while the process is plainly
  /// busy.
  /// <para>
  /// A fixed window solves both and needs no state to carry between samples. Two seconds was
  /// measured on an RTX A5000 against a continuous load: every call inside it returned exactly one
  /// sample per busy process, and none returned none.
  /// </para>
  /// </remarks>
  private const ulong _UtilizationWindowMicroseconds = 2_000_000;

  /// <summary>
  /// What each process did to the card recently: shaders, encoder, decoder (PRD §19).
  /// </summary>
  /// <remarks>
  /// Percentages sampled by the driver, not a counter to difference. Where a pid has more than one
  /// sample in the window the newest is taken rather than the mean — the column says what is
  /// happening, and averaging two seconds of it would smooth away the moment a process started.
  /// </remarks>
  /// <param name="buffer">Reused by the caller; grown by the caller.</param>
  /// <returns>
  /// False when this driver will not sample per-process utilisation at all. An empty window is
  /// <see langword="true"/> with nothing merged, which is a different statement.
  /// </returns>
  public static bool TryReadProcessUtilization(
    nint device,
    Dictionary<int, NvmlProcessSample> processes,
    ProcessUtilization[] buffer
  ) {
    ArgumentNullException.ThrowIfNull(processes);
    ArgumentNullException.ThrowIfNull(buffer);
    if (device == 0 || buffer.Length == 0 || _utilizationAvailable is false)
      return false;

    var since = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    since = since > _UtilizationWindowMicroseconds ? since - _UtilizationWindowMicroseconds : 0;

    uint result;
    var count = (uint)buffer.Length;
    try {
      result = GetProcessUtilization(device, ref buffer[0], ref count, since);
    } catch (EntryPointNotFoundException) {
      _utilizationAvailable = false;
      return false;
    } catch (DllNotFoundException) {
      _available = false;
      return false;
    }

    _utilizationAvailable = true;
    // Nothing ran on the card in the window, which is an answer and not a failure. Insufficient size
    // means more processes than the buffer holds; the caller grows it and the next sample is whole.
    if (result != _Success)
      return result is _NotFound or _InsufficientSize;

    for (var i = 0; i < count && i < buffer.Length; ++i) {
      ref readonly var reading = ref buffer[i];
      var pid = (int)reading.Pid;
      if (pid <= 0)
        continue;

      processes.TryGetValue(pid, out var sample);
      if (sample.HasUtilization && reading.TimeStamp < sample.TimeStamp)
        continue;

      sample.TimeStamp = reading.TimeStamp;
      sample.BusyPercent = reading.SmUtil;
      sample.EncodePercent = reading.EncUtil;
      sample.DecodePercent = reading.DecUtil;
      sample.HasUtilization = true;
      processes[pid] = sample;
    }

    return true;
  }

  /// <summary>Buffers the caller owns, sized here so the shapes stay private to this file.</summary>
  public static ProcessInfo[] NewProcessBuffer(int length) => new ProcessInfo[length];

  public static ProcessInfoV1[] NewNarrowProcessBuffer(int length) => new ProcessInfoV1[length];

  public static ProcessUtilization[] NewUtilizationBuffer(int length) => new ProcessUtilization[length];

  #endregion

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
