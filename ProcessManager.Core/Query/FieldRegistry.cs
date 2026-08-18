using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The canonical field catalogue: the one place a field is declared, and the thing both front-ends,
/// the CLI and the filter are built from (PRD §5.1).
/// </summary>
/// <remarks>
/// A static array rather than anything reflective, so it survives trimming and NativeAOT intact
/// (PRD §8.3). Adding a field here gives it a header, a width, a sort order, a formatter and a filter
/// term in every front-end at once — which is the whole reason it exists.
/// </remarks>
public static class FieldRegistry {

  private const FieldPlatforms _WINDOWS = FieldPlatforms.Windows;
  private const FieldPlatforms _LINUX = FieldPlatforms.Linux;
  private const FieldPlatforms _POSIX = FieldPlatforms.Linux | FieldPlatforms.MacOS;
  private const FieldPlatforms _ALL = FieldPlatforms.All;

  /// <summary>Every field, in default column order.</summary>
  public static readonly FieldDescriptor[] All = [
    new(ProcessField.Name, "name", "Process", "Process",
      "The short name: comm on Linux, the image file name on Windows.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 260, 120, false, false,
      Aliases: "process comm"),
    new(ProcessField.Pid, "pid", "PID", "PID",
      "The process identifier.",
      FieldKind.Identifier, FieldUnit.None, _ALL, FieldCost.Free, 70, 7, true, false),
    new(ProcessField.PidHex, "pid.hex", "PID (hex)", "PIDx",
      "The same identifier in hexadecimal, which is how a debugger will show it.",
      FieldKind.Identifier, FieldUnit.None, _ALL, FieldCost.Free, 78, 9, true, false),
    new(ProcessField.ParentPid, "ppid", "Parent PID", "PPID",
      "The parent's identifier, or none when the parent has exited.",
      FieldKind.Identifier, FieldUnit.None, _ALL, FieldCost.Free, 88, 7, true, false,
      Aliases: "parent"),
    new(ProcessField.UserName, "user", "User", "User",
      "The account the process runs as.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 130, 10, false, false,
      Aliases: "owner username"),
    new(ProcessField.State, "state", "State", "S",
      "What the scheduler thinks of the process right now.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.Free, 62, 5, false, false,
      Aliases: "status"),

    new(ProcessField.CpuPercent, "cpu", "CPU %", "CPU%",
      "Processor use where 100% is the whole machine.",
      FieldKind.Rate, FieldUnit.Percent, _ALL, FieldCost.Derived, 78, 5, true, true,
      Aliases: "cpu.percent"),
    new(ProcessField.CpuPercentPerCore, "cpu.raw", "CPU % (per core)", "CPU%c",
      "Processor use where 100% is one core, the way top reports it.",
      FieldKind.Rate, FieldUnit.Percent, _ALL, FieldCost.Derived, 118, 6, true, true,
      Aliases: "cpu.percore"),
    new(ProcessField.CpuTime, "cpu.time", "CPU time", "Time",
      "Total processor time consumed since the process started.",
      FieldKind.Cumulative, FieldUnit.Nanoseconds, _ALL, FieldCost.Free, 88, 9, true, true),
    new(ProcessField.CyclesDelta, "cpu.cycles.delta", "Cycles delta", "Cyc/s",
      "Processor cycles this interval. Unlike CPU time it does not flatter a process that ran while the clock was throttled.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _WINDOWS, FieldCost.Derived, 100, 8, true, true),
    new(ProcessField.ContextSwitchesDelta, "ctx.delta", "Ctx switch delta", "Ctx/s",
      "Context switches this interval.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _POSIX, FieldCost.Derived, 116, 8, true, true),
    new(ProcessField.CpuHistory, "cpu.history", "CPU history", "CPU hist",
      "The last sixty seconds of processor use.",
      FieldKind.Graph, FieldUnit.Percent, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Cpu),

    new(ProcessField.PrivateBytes, "private", "Private bytes", "Private",
      "Private memory the process has committed — what it would give back if it exited.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 96, 7, true, true,
      Aliases: "mem memory commit"),
    new(ProcessField.PrivateBytesDelta, "private.delta", "Private delta", "Priv/s",
      "How fast committed private memory is moving. A process whose private bytes only climb is the one leaking.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 100, 9, true, true),
    new(ProcessField.PrivateWorkingSet, "private.ws", "Private WS", "PrivWS",
      "The resident part of the committed private memory.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 88, 7, true, true,
      Aliases: "uss"),
    new(ProcessField.MemoryHistory, "memory.history", "Memory history", "Mem hist",
      "The last sixty seconds of committed private memory.",
      FieldKind.Graph, FieldUnit.Bytes, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Memory),
    new(ProcessField.WorkingSetBytes, "ws", "Working set", "RSS",
      "Resident memory including every shared page, which is why it double-counts.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 92, 7, true, true,
      Aliases: "rss workingset resident"),
    new(ProcessField.PeakWorkingSet, "ws.peak", "Peak WS", "PkRSS",
      "The largest working set this process has ever held.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 84, 7, true, true),
    new(ProcessField.VirtualBytes, "virtual", "Virtual size", "Virt",
      "Size of the mapped address space, most of which is usually not resident.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 92, 7, true, true,
      Aliases: "virt vsize"),
    new(ProcessField.PeakVirtualBytes, "virtual.peak", "Peak virtual", "PkVirt",
      "The largest address space this process has ever mapped.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 96, 7, true, true),
    new(ProcessField.PagedPool, "pool.paged", "Paged pool", "PgPool",
      "Kernel memory charged to this process from the paged pool.",
      FieldKind.Instant, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 90, 7, true, true),
    new(ProcessField.PeakPagedPool, "pool.paged.peak", "Peak paged pool", "PkPgPool",
      "The largest paged-pool charge this process has held.",
      FieldKind.Instant, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 116, 8, true, true),
    new(ProcessField.NonPagedPool, "pool.nonpaged", "Non-paged pool", "NpPool",
      "Kernel memory charged to this process from the non-paged pool.",
      FieldKind.Instant, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 110, 7, true, true),
    new(ProcessField.PeakNonPagedPool, "pool.nonpaged.peak", "Peak non-paged", "PkNpPool",
      "The largest non-paged-pool charge this process has held.",
      FieldKind.Instant, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 112, 8, true, true),
    new(ProcessField.PageFaultsDelta, "faults.delta", "Page fault delta", "Flt/s",
      "Page faults this interval. A process faulting steadily is one the machine is paging for.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _ALL, FieldCost.Derived, 116, 8, true, true),
    new(ProcessField.Swap, "swap", "Swap", "Swap",
      "How much of this process the machine has pushed out to swap.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 78, 7, true, true),

    new(ProcessField.IoTotalRate, "io.total", "I/O total rate", "IO/s",
      "Bytes read, written and neither, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 104, 8, true, true,
      Aliases: "io"),
    new(ProcessField.ReadBytesPerSecond, "io.read", "I/O read rate", "Read/s",
      "Bytes this process caused to be read, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 100, 8, true, true,
      Aliases: "read"),
    new(ProcessField.WriteBytesPerSecond, "io.write", "I/O write rate", "Write/s",
      "Bytes this process caused to be written, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 104, 8, true, true,
      Aliases: "write"),
    new(ProcessField.IoHistory, "io.history", "I/O history", "I/O hist",
      "The last sixty seconds of read and write traffic.",
      FieldKind.Graph, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Io),

    new(ProcessField.Elevated, "elevated", "Elevated", "Elev",
      "Whether the process runs with administrative authority — effective uid 0 on Unix.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.Free, 76, 5, false, true,
      Aliases: "root admin"),
    new(ProcessField.Seccomp, "seccomp", "Seccomp", "Sec",
      "Whether a seccomp filter restricts which system calls the process may make.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 84, 6, false, true),
    new(ProcessField.NoNewPrivileges, "nnp", "No new privs", "NNP",
      "Set when the process can never gain privileges, however it execs.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 96, 4, false, true),
    new(ProcessField.Capabilities, "caps", "Capabilities", "Caps",
      "The effective Linux capability mask.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 140, 16, false, false),
    new(ProcessField.SecurityContext, "security", "Security context", "LSM",
      "The SELinux context or AppArmor profile confining the process.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 260, 40, false, false,
      Aliases: "selinux apparmor lsm"),

    new(ProcessField.ThreadCount, "threads", "Threads", "Thr",
      "How many threads the process currently has.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.Free, 64, 4, true, true),
    new(ProcessField.HandleCount, "handles", "Handles", "Hnd",
      "Open handles on Windows, open file descriptors on Unix.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.High, 66, 5, true, true,
      Aliases: "fds fd"),
    new(ProcessField.Priority, "priority", "Priority", "Pri",
      "Scheduler priority in the platform's own scale.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.Free, 74, 4, true, true,
      Aliases: "prio"),
    new(ProcessField.SessionId, "session", "Session", "Ses",
      "The login or terminal session the process belongs to.",
      FieldKind.Identifier, FieldUnit.None, _ALL, FieldCost.Free, 74, 5, true, false),
    new(ProcessField.StartTime, "start", "Start time", "Started",
      "When the process was created.",
      FieldKind.Instant, FieldUnit.Timestamp, _ALL, FieldCost.Free, 140, 19, false, true,
      Aliases: "started starttime"),
    new(ProcessField.Container, "cgroup", "Container / cgroup", "Cgroup",
      "The cgroup or container the process belongs to.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 240, 40, false, false,
      Aliases: "container"),
    new(ProcessField.ImagePath, "path", "Image path", "Path",
      "Full path of the executable image.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 320, 60, false, false,
      Aliases: "image exe"),
    new(ProcessField.CommandLine, "cmdline", "Command line", "Command",
      "The complete command the process was started with.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 420, 120, false, false,
      Aliases: "cmd commandline"),
  ];

  private static readonly FieldDescriptor[] _byId = BuildIndex();

  private static FieldDescriptor[] BuildIndex() {
    var highest = 0;
    foreach (var descriptor in All)
      highest = Math.Max(highest, (int)descriptor.Id);

    var index = new FieldDescriptor[highest + 1];
    foreach (var descriptor in All)
      index[(int)descriptor.Id] = descriptor;

    return index;
  }

  /// <summary>Everything known about one field.</summary>
  public static FieldDescriptor Get(ProcessField field) {
    var index = (int)field;
    return (uint)index < (uint)_byId.Length && _byId[index] is { } descriptor ? descriptor : _byId[0];
  }

  public static string Header(this ProcessField field) => Get(field).Header;

  public static string ShortHeader(this ProcessField field) => Get(field).ShortHeader;

  public static string Key(this ProcessField field) => Get(field).Key;

  public static bool PrefersDescending(this ProcessField field) => Get(field).PrefersDescending;

  /// <summary>
  /// Resolves a field from text: its key, one of its aliases, or its header, case-insensitively.
  /// </summary>
  /// <remarks>
  /// This is what <c>--sort</c>, a saved layout and a search term all go through, so all three accept
  /// the same spellings and none of them can drift from the others.
  /// </remarks>
  public static bool TryParse(string? text, out ProcessField field) {
    field = ProcessField.CpuPercent;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var wanted = text.Trim();
    foreach (var descriptor in All) {
      if (string.Equals(descriptor.Key, wanted, StringComparison.OrdinalIgnoreCase)
          || string.Equals(descriptor.Header, wanted, StringComparison.OrdinalIgnoreCase)) {
        field = descriptor.Id;
        return true;
      }

      if (descriptor.Aliases is not { } aliases)
        continue;

      foreach (var alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        if (string.Equals(alias, wanted, StringComparison.OrdinalIgnoreCase)) {
          field = descriptor.Id;
          return true;
        }
    }

    return false;
  }

  /// <summary>Every spelling <see cref="TryParse"/> accepts, for the help text.</summary>
  public static string SortableKeys() {
    var keys = new List<string>();
    foreach (var descriptor in All)
      if (descriptor.IsSortable)
        keys.Add(descriptor.Key);

    return string.Join(", ", keys);
  }

}
