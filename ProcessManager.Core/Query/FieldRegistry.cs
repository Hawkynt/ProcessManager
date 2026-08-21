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
    new(ProcessField.ParentName, "parent.name", "Parent name", "PARENT",
      "The parent's name, or none when the parent has exited and this was reparented.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 120, 12, false, false,
      Aliases: "pname"),
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
    new(ProcessField.CpuPercentDelta, "cpu.delta", "CPU change", "ΔCPU",
      "How much this process's CPU share moved since the previous sample. A process that has just started working stands out from one that has been busy all along.",
      FieldKind.Rate, FieldUnit.Percent, _ALL, FieldCost.Derived, 72, 7, true, true),
    new(ProcessField.UserTime, "cpu.time.user", "User time", "User",
      "Processor time spent running this process's own code.",
      FieldKind.Cumulative, FieldUnit.Nanoseconds, _ALL, FieldCost.Free, 70, 9, true, true),
    new(ProcessField.KernelTime, "cpu.time.kernel", "Kernel time", "Kernel",
      "Processor time the kernel spent on this process's behalf — system calls, faults, interrupts charged to it. A process that is mostly kernel time is usually waiting on something rather than computing.",
      FieldKind.Cumulative, FieldUnit.Nanoseconds, _ALL, FieldCost.Free, 71, 9, true, true),
    new(ProcessField.CyclesDelta, "cpu.cycles.delta", "Cycles delta", "Cyc/s",
      "Processor cycles this interval. Unlike CPU time it does not flatter a process that ran while the clock was throttled.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _WINDOWS, FieldCost.Derived, 100, 8, true, true),
    new(ProcessField.ContextSwitchesDelta, "ctx.delta", "Ctx switch delta", "Ctx/s",
      "Context switches this interval.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _POSIX, FieldCost.Derived, 116, 8, true, true),
    new(ProcessField.LastCpu, "cpu.last", "Last CPU", "CPU#",
      "The logical processor this last ran on. A process pinned to one core looks different from one the scheduler is moving around.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 80, 5, true, false),
    new(ProcessField.SchedulingClass, "sched.class", "Scheduler class", "Sched",
      "Which class of the scheduler runs the process. A real-time class is not a high priority — an ordinary task cannot preempt SCHED_FIFO at all, however busy it is, and no priority number says that.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 132, 14, false, true,
      Aliases: "sched policy"),
    new(ProcessField.CpuAffinity, "cpu.affinity", "CPU affinity", "Affinity",
      "Which processors the process is allowed to run on, in the kernel's own list notation: 0-15 on a sixteen-way machine is all of them, and 15 is one pinned to the last.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 126, 12, false, false,
      Aliases: "affinity"),
    new(ProcessField.CpuThrottled, "throttled", "Throttled", "Thrtl",
      "How many times the process's cgroup has been stopped for using its whole CPU quota — the number that turns \"it is slow\" into \"it is being throttled\". A property of the group rather than of the process, so everything in the same cgroup shows the same figure.",
      FieldKind.Cumulative, FieldUnit.Count, _LINUX, FieldCost.High, 92, 6, true, true),
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
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 88, 7, true, true),
    new(ProcessField.ProportionalSet, "pss", "Proportional set", "PSS",
      "Private pages in full plus a share of every shared one. The only per-process memory figure that adds up: working set counts each shared page in full for every process mapping it, so summing it reports several times the memory that exists.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 91, 7, true, true,
      Aliases: "proportional"),
    new(ProcessField.UniqueSet, "uss", "Unique set", "USS",
      "The memory only this process maps, and so the only memory that would come back if it exited. PSS says what a process costs; USS says what killing it would recover.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 89, 7, true, true),
    new(ProcessField.MemoryPercent, "mem.percent", "Memory %", "Mem%",
      "The share of the machine's memory this process holds resident.",
      FieldKind.Instant, FieldUnit.Percent, _ALL, FieldCost.Derived, 93, 6, true, true),
    new(ProcessField.ProportionalSwap, "swap.pss", "Proportional swap", "SwPSS",
      "Swapped-out memory, shared pages divided the same way the proportional set divides them.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 60, 7, true, true),
    new(ProcessField.FileBackedSet, "ws.file", "File-backed WS", "FileWS",
      "The resident memory that came from a file, and so can be dropped and read back rather than swapped.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 74, 7, true, true),
    new(ProcessField.SharedSet, "ws.shared", "Shared WS", "ShmWS",
      "The resident memory in shared segments — tmpfs, shared anonymous mappings, System V shared memory.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 62, 7, true, true),
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

    // PRD §18. Counts of endpoints, and deliberately not counts of traffic: Linux attributes no
    // bytes to a process without packet accounting or eBPF, so the byte and rate fields of §18 are
    // absent rather than filled from the sockets a process happens to hold open at the moment
    // somebody looked (PRD §72.3). All four are High for one reason — the join from a socket to a
    // process is a readlink per open descriptor on the machine — and so none is default-visible.
    new(ProcessField.TcpConnectionCount, "tcp.count", "TCP connections", "TCP",
      "How many TCP sockets this process holds a descriptor on, listeners included.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 116, 5, true, true,
      Aliases: "tcp connections"),
    new(ProcessField.UdpSocketCount, "udp.count", "UDP sockets", "UDP",
      "How many UDP sockets this process holds a descriptor on. A datagram socket has no connection to count, so this counts the sockets themselves.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 98, 5, true, true,
      Aliases: "udp"),
    new(ProcessField.ListeningSocketCount, "net.listening", "Listening", "Lstn",
      "How many of this process's sockets are waiting for connections rather than making them. TCP only: a UDP socket bound to a port is not listening in any sense the kernel records.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 92, 5, true, true,
      Aliases: "listening"),
    new(ProcessField.RemoteEndpointCount, "net.remote.count", "Remote endpoints", "Peers",
      "How many distinct peers this process is connected to. Distinct addresses and ports rather than connections, because two connections to one machine are one correspondent.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 128, 6, true, true,
      Aliases: "peers remotes"),

    // PRD §19. Linux only so far: Windows reads its own performance counters and that is not
    // written yet, and a field claiming to work there would be worse than one that says it does not.
    // Expensive without exception — the kernel's accounting is a file per open descriptor and
    // NVIDIA's is a library call per card — so every one of these is High and none is default-visible
    // (PRD §5.4).
    new(ProcessField.GpuPercent, "gpu", "GPU %", "GPU%",
      "How much of its adapter this process is using: the busiest of the engines it is running on, never their sum — a card's engines run at once, so adding them reports a transcode at two hundred percent.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 78, 5, true, true,
      Aliases: "gpu.percent"),
    new(ProcessField.GpuEngineName, "gpu.engine", "GPU engine", "Engine",
      "Which part of the adapter the process is busiest on: 3D, compute, copy, encode or decode.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 96, 8, false, false),
    new(ProcessField.GpuEnginePercent, "gpu.engine.percent", "GPU engine %", "Eng%",
      "The busiest engine's own share of the interval — the number the engine column names.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 104, 6, true, true),
    new(ProcessField.GpuAdapter, "gpu.adapter", "GPU adapter", "Card",
      "Which graphics adapter these figures came from. A laptop has two, and a GPU figure that does not say which one is unreadable on exactly the machines where it matters.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 96, 7, false, false),
    new(ProcessField.GpuDedicatedMemory, "gpu.mem.dedicated", "GPU dedicated memory", "GPUmem",
      "Adapter memory this process holds — VRAM on a discrete card.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 148, 7, true, true,
      Aliases: "vram"),
    new(ProcessField.GpuSharedMemory, "gpu.mem.shared", "GPU shared memory", "GPUshr",
      "System memory the adapter is using for this process: GTT on a discrete card, and all of it on an integrated one, that being what integrated means.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 136, 7, true, true),
    new(ProcessField.GpuTotalMemory, "gpu.mem.total", "GPU memory", "GPUtot",
      "Dedicated and shared adapter memory together.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 100, 7, true, true),
    new(ProcessField.GpuDedicatedMemoryDelta, "gpu.mem.dedicated.delta", "GPU memory delta", "GPUm/s",
      "How fast the process's dedicated adapter memory is moving. A renderer whose VRAM only climbs is the one that will eventually stop the machine drawing anything.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _LINUX, FieldCost.High, 124, 8, true, true),
    new(ProcessField.GpuGraphicsPercent, "gpu.graphics", "GPU 3D", "3D%",
      "Share of the adapter's graphics engine — shaders and rasterisation.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 82, 5, true, true,
      Aliases: "gpu.3d"),
    new(ProcessField.GpuComputePercent, "gpu.compute", "GPU compute", "Cmp%",
      "Share of the adapter's compute engine, where the driver counts it apart from graphics. NVIDIA does not, and says so rather than reporting nought.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 104, 6, true, true),
    new(ProcessField.GpuCopyPercent, "gpu.copy", "GPU copy", "Cpy%",
      "Share of the adapter's copy engines — the ones that move memory without the shaders.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 88, 6, true, true),
    new(ProcessField.GpuEncodePercent, "gpu.encode", "GPU encode", "Enc%",
      "Share of the adapter's video encoder.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 98, 6, true, true),
    new(ProcessField.GpuDecodePercent, "gpu.decode", "GPU decode", "Dec%",
      "Share of the adapter's video decoder.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 98, 6, true, true),

    new(ProcessField.Elevated, "elevated", "Elevated", "Elev",
      "Whether the process runs with administrative authority — effective uid 0 on Unix.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.Free, 76, 5, false, true,
      Aliases: "root admin"),
    new(ProcessField.Integrity, "integrity", "Integrity", "Integ",
      "The Windows mandatory integrity level: untrusted, low, medium, high or system.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 90, 7, false, true),
    new(ProcessField.Seccomp, "seccomp", "Seccomp", "Sec",
      "Whether a seccomp filter restricts which system calls the process may make.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 84, 6, false, true),
    new(ProcessField.SeccompFilters, "seccomp.filters", "Seccomp filters", "SecF",
      "How many seccomp filter programs are attached. Several means something sandboxed the process more than once — a browser renderer inside a container, typically. Unknown on kernels before 5.9, which do not report it.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.Free, 106, 5, true, true),
    new(ProcessField.NoNewPrivileges, "nnp", "No new privs", "NNP",
      "Set when the process can never gain privileges, however it execs.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 96, 4, false, true),
    new(ProcessField.Capabilities, "caps", "Capabilities", "Caps",
      "What the process may do right now, by name: the effective set. \"all\" is every capability the kernel has, which is what an unconfined root process holds.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 300, 28, false, true,
      Aliases: "caps.effective"),
    new(ProcessField.CapabilitiesHex, "caps.hex", "Capabilities (raw)", "CapsX",
      "The effective set as the sixteen hex digits status writes, for pasting into capsh --decode.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 160, 18, false, true),
    new(ProcessField.PermittedCapabilities, "caps.permitted", "Permitted capabilities", "CapsP",
      "What the process may raise into its effective set without asking anybody. A process holding a capability here but not in the effective set has put it down, not given it up.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 300, 28, false, true),
    new(ProcessField.InheritableCapabilities, "caps.inheritable", "Inheritable capabilities", "CapsI",
      "What survives an exec of a file that carries the matching inheritable bits.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 300, 28, false, true),
    new(ProcessField.BoundingCapabilities, "caps.bounding", "Bounding capabilities", "CapsB",
      "The ceiling: nothing this process or anything it starts can ever hold a capability that is not in here. A hardened service is one that has dropped most of this set.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 300, 28, false, true),
    new(ProcessField.AmbientCapabilities, "caps.ambient", "Ambient capabilities", "CapsA",
      "What survives an exec even of a file with no capabilities of its own — the way a service manager hands a privilege to an ordinary binary.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 300, 28, false, true),
    new(ProcessField.SecurityContext, "security", "Security context", "LSM",
      "The SELinux context or AppArmor profile confining the process.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 260, 40, false, false,
      Aliases: "selinux apparmor lsm"),
    new(ProcessField.ConfinementMode, "lsm.mode", "Confinement mode", "Mode",
      "How hard the security module is holding the process to its profile: enforced, or merely watched. AppArmor states it beside the label; an SELinux context states none, and this says so rather than inventing one.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 140, 9, false, true,
      Aliases: "apparmor.mode confinement"),

    // PRD §21. What Linux has instead of a per-process mitigation policy: not a request that was
    // made for the process, but the state the kernel would report through prctl if you asked it.
    // Each of them distinguishes "the kernel says none" from "the kernel does not write this line",
    // because for a mitigation those are opposite findings (PRD §72.3).
    //
    // High, and the cost is neither a read nor an allocation — which is why the figure had to be
    // measured rather than assumed. The lines are in a file the sampler already has open, but
    // recognising five more labels in a loop that runs fifty times per process cost seven to eight
    // milliseconds per thousand processes when every run paid it. So no run pays it unless one of
    // these six columns was named (PRD §5.4, §71.2).
    new(ProcessField.SpeculationStoreBypass, "spec.ssb", "Store bypass mitigation", "SSB",
      "Whether speculative store bypass is mitigated for this process, and whether the process chose that or had it chosen for it. Sorts by exposure, so the unmitigated rows come to the top.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 168, 22, false, true,
      Aliases: "ssb spectre.ssb"),
    new(ProcessField.SpeculationIndirectBranch, "spec.ib", "Indirect branch mitigation", "IndBr",
      "Whether indirect branch speculation is restricted for this process. Its own control and its own answer: a process may have asked for one mitigation and not the other.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 180, 26, false, true,
      Aliases: "spec.indirect indirect.branch"),
    new(ProcessField.ThreadFeatures, "shadow.stack", "Shadow stack", "Shstk",
      "The hardware protections the kernel has switched on for the process — a shadow stack, and whether the program may write to it. A reading rather than a policy: a binary built for it still runs without one unless the loader turned it on, and this is what tells the two apart.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 140, 12, false, true,
      Aliases: "shstk thread.features"),
    // Textual on purpose, and not because a mask is prose. The kernel holds it to nine bits, so the
    // four octal digits are always four octal digits — which means comparing them as text gives the
    // same order as comparing them as numbers, while a filter and an exported cell both carry the
    // form somebody actually has the value in. Declaring it a state instead would have been worse
    // than merely unsortable: a state field rewrites "0" to "no" before comparing, so the one mask
    // most worth searching for would have matched nothing.
    new(ProcessField.Umask, "umask", "Umask", "Umask",
      "The file-creation mask: which permissions are withheld from every file the process makes. A daemon running with a mask of nothing creates world-writable files, which no other column on the row would show.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 84, 6, true, false),
    new(ProcessField.TracerPid, "tracer", "Traced by", "Tracer",
      "Which process is attached to this one as a debugger, or none. The pid rather than a yes or no, because \"something is reading this process's memory\" is only half the question.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.High, 96, 7, true, true,
      Aliases: "tracer.pid ptrace"),

    new(ProcessField.PrivilegeChanged, "setuid", "Privilege change", "SetID",
      "Whether the process is running as somebody other than whoever started it — real and effective ids that disagree, which is what a set-user-ID binary looks like from outside.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.Free, 118, 5, false, true),
    new(ProcessField.EffectiveUserName, "user.effective", "Effective user", "EUser",
      "The account whose authority the process is using, which for anything set-user-ID is not the account that started it.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 130, 10, false, false,
      Aliases: "euser"),
    new(ProcessField.UserId, "uid", "UID", "UID",
      "The real user id: who started the process.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 70, 6, true, false,
      Aliases: "ruid"),
    new(ProcessField.EffectiveUserId, "uid.effective", "Effective UID", "EUID",
      "The effective user id, which is the one every permission check uses.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 100, 6, true, false,
      Aliases: "euid"),
    new(ProcessField.SavedUserId, "uid.saved", "Saved UID", "SUID",
      "The identity a process that has dropped privileges may take back at any time. Real and effective ids of an ordinary user with a saved id of 0 is a process that has given up nothing.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 90, 6, true, false,
      Aliases: "suid"),
    new(ProcessField.FilesystemUserId, "uid.fs", "Filesystem UID", "FSUID",
      "The id that decides which files the process may open. Linux-only, and normally the same as the effective one.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 106, 6, true, false,
      Aliases: "fsuid"),
    new(ProcessField.GroupId, "gid", "GID", "GID",
      "The real group id.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 70, 6, true, false,
      Aliases: "rgid"),
    new(ProcessField.EffectiveGroupId, "gid.effective", "Effective GID", "EGID",
      "The effective group id, which every group permission check uses.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 100, 6, true, false,
      Aliases: "egid"),
    new(ProcessField.SavedGroupId, "gid.saved", "Saved GID", "SGID",
      "The group identity a process that has dropped it may take back.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 90, 6, true, false,
      Aliases: "sgid"),
    new(ProcessField.FilesystemGroupId, "gid.fs", "Filesystem GID", "FSGID",
      "The group id that decides which files the process may open.",
      FieldKind.Identifier, FieldUnit.None, _LINUX, FieldCost.Free, 106, 6, true, false,
      Aliases: "fsgid"),
    new(ProcessField.SupplementaryGroups, "groups", "Supplementary groups", "Groups",
      "Every other group the process belongs to, as the kernel numbers them. Empty is a real answer — a kernel thread is in none.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 200, 24, false, false),

    // PRD §21, §70. What the bytes are, and nothing about whether anybody trusts them: a hash is
    // not a verdict, and this program never lets one stand in for a signature. Read on demand only —
    // the cost of hashing is the size of the file (PRD §5.4).
    new(ProcessField.ImageSha256, "hash.sha256", "SHA-256", "SHA-256",
      "The SHA-256 of the running image, computed on request. It says what the bytes are and nothing about whether they are signed, trusted or known — those are separate questions and this is not an answer to any of them.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 460, 64, false, false,
      Aliases: "sha256 hash"),
    new(ProcessField.ImageSha1, "hash.sha1", "SHA-1", "SHA-1",
      "The SHA-1 of the running image. Collidable since 2017 and kept only because so many package manifests and threat feeds are still keyed by it; on its own it is evidence of nothing.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 300, 40, false, false,
      Aliases: "sha1"),

    // PRD §14, §70. Which package a running image belongs to, and whether it is still the file that
    // package shipped. Both are High and for the same reason: the first reads every installed
    // package's file list once — thirty megabytes of text on an ordinary desktop — and the second
    // hashes the image on top of it, so neither happens unless a column or a filter names it
    // (PRD §5.4).
    new(ProcessField.Package, "package", "Package", "Package",
      "Which package the running image belongs to: the distribution's own, or the Flatpak, snap or AppImage it came in. \"not packaged\" is a finding rather than a hole — most of what a developer runs is not in any package.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 220, 24, false, false,
      Aliases: "pkg"),
    new(ProcessField.ApplicationId, "app.id", "Application ID", "AppID",
      "The platform application id — a Flatpak's org.gimp.GIMP, a snap's name. Native Linux programs have none, and this says so rather than repeating the package name into a column that means something else.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 200, 24, false, false,
      Aliases: "appid"),
    new(ProcessField.PackageStatus, "package.status", "Package check", "PkgChk",
      "Whether the running image still matches the digest its package recorded, and whether that package was itself signed. An ELF carries no signature to verify, so this is the honest local equivalent: what pacman -Qkk and dpkg --verify ask. It is not a hash, not a trust chain and not a reputation — those are separate questions and nothing here answers them.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 200, 25, false, false,
      Aliases: "pkgcheck"),
    new(ProcessField.Runtime, "runtime", "Runtime", "Runtime",
      "What is executing inside the process: a managed runtime, or machine code. Read from the modules the process has mapped rather than from its name, because a process called java may be a shell script and a renamed one may be anything at all.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 110, 8, false, false),
    new(ProcessField.ImageCreated, "exe.created", "Image created", "Created",
      "When the image file was created, where the file system remembers one. Many do not — an ext4 built without crtime has no birth time at all — and there this is unknown rather than the epoch.",
      FieldKind.Instant, FieldUnit.Timestamp, _LINUX, FieldCost.High, 150, 19, false, true,
      Aliases: "created birth"),

    new(ProcessField.ThreadCount, "threads", "Threads", "Thr",
      "How many threads the process currently has.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.Free, 64, 4, true, true),
    new(ProcessField.HandleCount, "handles", "Handles", "Hnd",
      "Open handles on Windows, open file descriptors on Unix.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.High, 66, 5, true, true,
      Aliases: "fds fd"),
    // PRD §20. One pass over the descriptor table, and the same classification the handle view of
    // §32 uses. Expensive without exception — a link to resolve per descriptor on top of the
    // directory listing that was already the most costly read in the sampler — so all three are
    // High and none is default-visible (PRD §5.4).
    new(ProcessField.SocketCount, "socket.count", "Open sockets", "Sock",
      "How many of the process's descriptors are sockets. A server leaking connections shows it here long before the machine runs out of them.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 106, 6, true, true,
      Aliases: "sockets"),
    new(ProcessField.FileCount, "file.count", "Open files", "Files",
      "How many descriptors are open on a name in the file system, directories included. Not the same as the handle count, most of which is usually anything but a file.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 96, 6, true, true,
      Aliases: "files"),
    new(ProcessField.PipeCount, "pipe.count", "Open pipes", "Pipes",
      "How many descriptors are pipes. Both ends of one pipe are a descriptor each, so a shell pipeline holds two of them per process.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 96, 6, true, true,
      Aliases: "pipes"),
    // PRD §20. Far cheaper than the three above it — the number is a line of the status the sampler
    // already reads rather than a walk of the descriptor table — but opt-in all the same, and with
    // the mitigation columns, because it is recognised in the same pass and by the same switch. It
    // is also not the same number as any of them: a capacity is not a count and it is not the
    // high-water mark either, and the header says capacity because that is what it is.
    new(ProcessField.DescriptorTableSize, "fd.table", "Descriptor table", "FDSize",
      "How many descriptor slots the kernel has allocated for the process. Not the count of open descriptors and not a peak: the table grows when one will not fit and never shrinks, so it is an upper bound on how many the process has ever held at once — which is the nearest thing Linux has to a peak handle count, and is labelled as the different number it is.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 132, 7, true, true,
      Aliases: "fdsize"),
    new(ProcessField.Nice, "nice", "Nice", "NI",
      "The politeness a process was started with. Backwards on purpose: -20 gets the most processor and 19 the least.",
      FieldKind.Instant, FieldUnit.None, _LINUX, FieldCost.Free, 66, 4, true, true),
    new(ProcessField.Terminal, "tty", "Terminal", "TTY",
      "The controlling terminal, or none — which is the answer for every daemon and every service, and so for most of a machine.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 64, 9, false, false,
      Aliases: "terminal"),
    new(ProcessField.ExecutableName, "exe.name", "Executable", "Exe",
      "The file that is running, which is not always what the process calls itself — a process can rename itself and many do.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 96, 20, false, false),
    new(ProcessField.ContainerId, "container.id", "Container ID", "CID",
      "The container this process belongs to, taken out of its cgroup path.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 68, 14, false, false),
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
