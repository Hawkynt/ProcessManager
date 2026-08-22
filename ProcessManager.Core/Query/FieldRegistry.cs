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
    new(ProcessField.Category, "category", "Kind", "KIND",
      "What a row's colour means, in words: yours, the system's, elevated, a service, suspended, a "
      + "zombie, newly started, packaged, or running a managed runtime. The same sentence the legend "
      + "gives, so somebody who cannot tell the colours apart is not reading a different program.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 190, 18, false, false,
      Aliases: "kind"),
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
      Aliases: "cpu.percent cpu.usage",
      Series: HistorySeries.Cpu,
      History: FieldHistory.Row | FieldHistory.Process),
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
    // PRD §15. Its own column beside the affinity mask rather than folded into it, because the two
    // are different promises: an affinity mask is a wall a process cannot run outside, a CPU set is a
    // preference the scheduler honours when it can. Windows-only — the Linux near-relative is the
    // cgroup cpuset, and the kernel narrows Cpus_allowed to it, so it is already in the affinity
    // column and is not a second answer (PRD §5.3). High, because it is a call per process per
    // sample on a handle nothing else in the sampler holds.
    new(ProcessField.CpuSets, "cpu.set", "CPU sets", "CPUSet",
      "Which CPU sets the process has been assigned to. Not the affinity mask beside it: a mask is a restriction the process cannot run outside, and a set is a preference the scheduler honours when it can. \"default\" is a real answer and the ordinary one — a process with no set assigned gets the system's, which is every processor.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 126, 12, false, false,
      Aliases: "cpuset cpu.sets"),
    new(ProcessField.CpuThrottled, "throttled", "Throttled", "Thrtl",
      "How many times the process's cgroup has been stopped for using its whole CPU quota — the number that turns \"it is slow\" into \"it is being throttled\". A property of the group rather than of the process, so everything in the same cgroup shows the same figure.",
      FieldKind.Cumulative, FieldUnit.Count, _LINUX, FieldCost.High, 92, 6, true, true),
    // PRD §15's `qos` is not here: it is the same reading as §22's `qos.background` and `eco.state`,
    // which are one call and two questions further down this array. A second column over the same
    // counter would be a third spelling of one answer, which is the drift the one catalogue exists
    // to stop (§5.1).
    new(ProcessField.CpuHistory, "cpu.history", "CPU history", "CPU hist",
      "The last sixty seconds of processor use, on the same scale as the CPU column beside it: 100% is the whole machine, or one core if the per-core convention is chosen. A plot with no stated scale is a shape rather than a measurement.",
      FieldKind.Graph, FieldUnit.Percent, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Cpu,
      History: FieldHistory.Row),

    new(ProcessField.PrivateBytes, "private", "Private bytes", "Private",
      "Private memory the process has committed — what it would give back if it exited.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 96, 7, true, true,
      Aliases: "mem memory commit",
      Series: HistorySeries.Memory,
      History: FieldHistory.Row | FieldHistory.Process),
    new(ProcessField.PrivateBytesDelta, "private.delta", "Private delta", "Priv/s",
      "How fast committed private memory is moving. A process whose private bytes only climb is the one leaking.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 100, 9, true, true),
    // PRD §16. The high-water mark of the column above it, and the peak of the same charge — Windows
    // keeps it as PeakPagefileUsage, next to the commit charge in the structure the sampler already
    // reads, so it costs nothing. Linux has no peak of VmData anywhere: VmPeak is the peak of the
    // address space and VmHWM the peak of the resident set, and reporting either under this heading
    // would be a different number wearing this one's name (PRD §5.3).
    new(ProcessField.PeakPrivateBytes, "private.peak", "Peak private", "PkPriv",
      "The largest private commit this process has ever held. A process sitting at fifty megabytes with a peak of four gigabytes has been somewhere the current row cannot show, which is the only reason a peak is worth a column.",
      FieldKind.Instant, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 104, 8, true, true,
      Aliases: "private.bytes.peak commit.peak"),
    new(ProcessField.PrivateWorkingSet, "private.ws", "Private WS", "PrivWS",
      "The resident part of the committed private memory.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 88, 7, true, true),
    new(ProcessField.ProportionalSet, "pss", "Proportional set", "PSS",
      "Private pages in full plus a share of every shared one. The only per-process memory figure that adds up: working set counts each shared page in full for every process mapping it, so summing it reports several times the memory that exists.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 91, 7, true, true,
      Aliases: "proportional",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.UniqueSet, "uss", "Unique set", "USS",
      "The memory only this process maps, and so the only memory that would come back if it exited. PSS says what a process costs; USS says what killing it would recover.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 89, 7, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.MemoryPercent, "mem.percent", "Memory %", "Mem%",
      "The share of the machine's memory this process holds resident.",
      FieldKind.Instant, FieldUnit.Percent, _ALL, FieldCost.Derived, 93, 6, true, true),
    new(ProcessField.ProportionalSwap, "swap.pss", "Proportional swap", "SwPSS",
      "Swapped-out memory, shared pages divided the same way the proportional set divides them.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.High, 60, 7, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.FileBackedSet, "ws.file", "File-backed WS", "FileWS",
      "The resident memory that came from a file, and so can be dropped and read back rather than swapped.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 74, 7, true, true),
    new(ProcessField.SharedSet, "ws.shared", "Shared WS", "ShmWS",
      "The resident memory in shared segments — tmpfs, shared anonymous mappings, System V shared memory.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 62, 7, true, true),
    // PRD §16. The working set less its private half — the same subtraction Windows makes for its
    // own "Shareable WS", and on Linux exactly the file-backed and shared columns beside it added
    // together. Derived here rather than read, because both halves are already on the row: a third
    // counter in the record would be a number that can disagree with the two it is made of.
    new(ProcessField.ShareableWorkingSet, "ws.shareable", "Shareable WS", "ShrWS",
      "The resident memory another process could be mapping too — the working set less its private part. A large shareable set is a process holding libraries and file data the rest of the machine is holding as well, and it costs far less than the same figure in private pages.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 106, 7, true, true),
    // PRD §16. The mapped size of everything file-backed, which is not the resident figure two
    // columns up: a process that has mapped a four-gigabyte database and touched a megabyte of it
    // reports four gigabytes here and a megabyte in the file-backed working set, and neither is the
    // other's approximation. High, and honestly so — it is a read of maps per process per sample, and
    // the kernel formats that file a page at a time (PRD §5.4).
    new(ProcessField.MappedFileBytes, "mapped.file", "Mapped files", "MapFile",
      "How much of the address space is backed by a file rather than by anonymous memory. The mapped size and not the resident one: a process that maps a large database and touches little of it is large here and small in the file-backed working set, and the gap between the two is the point.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 112, 8, true, true,
      Aliases: "mapped mapped.file.bytes",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.StackBytes, "stack.commit", "Main stack", "Stack",
      "How much stack the kernel accounts to the process. The main thread's and only the main thread's: every other thread's stack is an ordinary anonymous mapping and the kernel keeps no figure for it, so a thread pool with two hundred stacks reports the same few kilobytes here as a single-threaded program.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.Free, 92, 7, true, true,
      Aliases: "stack vmstk"),
    new(ProcessField.MemoryHistory, "memory.history", "Memory history", "Mem hist",
      "The last sixty seconds of committed private memory.",
      FieldKind.Graph, FieldUnit.Bytes, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Memory,
      History: FieldHistory.Row),
    new(ProcessField.WorkingSetBytes, "ws", "Working set", "RSS",
      "Resident memory including every shared page, which is why it double-counts.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 92, 7, true, true,
      Aliases: "rss workingset resident",
      History: FieldHistory.Process),
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
    // PRD §16. Which pages the memory manager takes back first when the machine is short. A backup or
    // an indexer sets itself low so that its pages go before anybody else's, and no other column on
    // the row says that. Nought is the lowest priority there is and a real reading, which is why the
    // absence of one has to be a reason instead (PRD §72.3). Linux has no per-process equivalent:
    // reclaim there is driven by the LRU lists and by the cgroup's knobs, and neither belongs to a
    // process.
    new(ProcessField.PagePriority, "page.priority", "Page priority", "PgPri",
      "Which of this process's pages the memory manager takes back first when the machine runs short — \"lowest\" is trimmed before everything else and \"normal\" is trimmed last. A backup or an indexer that has set itself low is the case this column exists for; \"normal\" is the ordinary answer.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 128, 10, false, true,
      Aliases: "pagepriority"),
    new(ProcessField.Swap, "swap", "Swap", "Swap",
      "How much of this process the machine has pushed out to swap.",
      FieldKind.Instant, FieldUnit.Bytes, _ALL, FieldCost.Free, 78, 7, true, true),

    new(ProcessField.IoTotalRate, "io.total", "I/O total rate", "IO/s",
      "Bytes read, written and neither, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 104, 8, true, true,
      Aliases: "io",
      Series: HistorySeries.Io,
      Privilege: FieldPrivilege.Owner,
      // The row ring keeps the total and the properties window keeps read and write apart, because a
      // forty-pixel plot has room for one line and a page has room for two — so this field is kept
      // by one of the two and the pair beside it by the other. Not the same statement, and declared
      // as the two it is.
      History: FieldHistory.Row),
    new(ProcessField.ReadBytesPerSecond, "io.read", "I/O read rate", "Read/s",
      "Bytes this process caused to be read, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 100, 8, true, true,
      Aliases: "read",
      Privilege: FieldPrivilege.Owner,
      History: FieldHistory.Process),
    new(ProcessField.WriteBytesPerSecond, "io.write", "I/O write rate", "Write/s",
      "Bytes this process caused to be written, per second.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 104, 8, true, true,
      Aliases: "write",
      Privilege: FieldPrivilege.Owner,
      History: FieldHistory.Process),
    // PRD §94's I/O set names an "other rate" beside the read and write ones, and the delta has
    // computed it since the rates were written — it was simply never given a column, so the only way
    // to see it was folded into the total. Windows-only, like the counter under it: /proc/[pid]/io
    // has no third figure of any kind, so on Linux this is a quantity the platform does not keep
    // rather than a nought, and the rate inherits that reason from the counter it differences.
    new(ProcessField.OtherBytesPerSecond, "io.other", "I/O other rate", "Other/s",
      "Bytes per second moved by calls that were neither a read nor a write — a device control, a "
      + "mapping faulted in. The rate beside the two ordinary ones, and the part of the I/O total "
      + "that neither of them accounts for.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _WINDOWS, FieldCost.Derived, 104, 8, true, true,
      Aliases: "other io.other.rate"),
    // PRD §17. The operation counts beside the byte counts, and the pair is the point: a process
    // moving a gigabyte in a thousand operations and one moving it in a million are the same row
    // under the byte columns and very different machines to be sitting in front of. Free on both
    // platforms — syscr and syscw are lines of a file the sampler already reads, and Windows has
    // ReadOperationCount and WriteOperationCount in the structure it already queries.
    new(ProcessField.ReadBytesTotal, "io.read.bytes", "I/O read total", "Rd total",
      "Bytes this process has caused to be read since it started. The rate beside it says what it is "
      + "doing now; this says what it has done, which is the question a process that spent an hour "
      + "reading and is idle when you look at it answers differently.",
      FieldKind.Cumulative, FieldUnit.Bytes, _ALL, FieldCost.Free, 140, 10, true, true,
      Aliases: "read.total",
      // The same file as the rate derived from it, and the same authority: 0400 since 5.12, so
      // another user's total is an em dash without the elevated helper. Declared Ordinary while the
      // rate over it declared Owner, which is one reading described two ways (PRD §5.1).
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.WriteBytesTotal, "io.write.bytes", "I/O write total", "Wr total",
      "Bytes this process has caused to be written since it started, counted the same way as the "
      + "read total beside it.",
      FieldKind.Cumulative, FieldUnit.Bytes, _ALL, FieldCost.Free, 140, 10, true, true,
      Aliases: "write.total",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.OtherBytesTotal, "io.other.bytes", "I/O other total", "Oth total",
      "Bytes moved by calls that were neither a read nor a write — a device control, a mapping "
      + "faulted in. Windows accounts these separately and Linux does not, so on Linux the cell says "
      + "the platform has no such figure rather than nought, which would claim the process made none.",
      // Windows, and its own description said so while it declared every platform: the Linux probe
      // sets OtherBytes to NotSupported for every process and always will, because /proc/[pid]/io
      // has no third figure of any kind. A platform listed here is a platform that can fill the
      // field, so listing Linux claimed a reading that does not exist (PRD §17, §72.3).
      FieldKind.Cumulative, FieldUnit.Bytes, _WINDOWS, FieldCost.Free, 140, 10, true, true,
      Aliases: "other.total"),
    new(ProcessField.ReadOperations, "io.read.ops", "I/O read operations", "Rd ops",
      "How many read calls this process has made. Not the same question as how many bytes: a million one-byte reads and one large one move the same data and cost the machine completely differently.",
      FieldKind.Cumulative, FieldUnit.Count, _ALL, FieldCost.Free, 150, 8, true, true,
      Aliases: "syscr",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ReadOperationsDelta, "io.read.ops.delta", "I/O read op rate", "Rd op/s",
      "Read calls this interval. A process making tens of thousands a second is one to look at whatever its byte rate says.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _ALL, FieldCost.Derived, 132, 8, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.WriteOperations, "io.write.ops", "I/O write operations", "Wr ops",
      "How many write calls this process has made.",
      FieldKind.Cumulative, FieldUnit.Count, _ALL, FieldCost.Free, 154, 8, true, true,
      Aliases: "syscw",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.WriteOperationsDelta, "io.write.ops.delta", "I/O write op rate", "Wr op/s",
      "Write calls this interval.",
      FieldKind.Rate, FieldUnit.CountPerSecond, _ALL, FieldCost.Derived, 136, 8, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.OtherOperations, "io.other.ops", "I/O other operations", "Ot ops",
      "Operations that were neither reads nor writes — ioctls, mostly. Windows keeps this count; /proc/[pid]/io has no third figure of any kind, so on Linux it is absent rather than nought.",
      FieldKind.Cumulative, FieldUnit.Count, _WINDOWS, FieldCost.Free, 156, 8, true, true),
    // PRD §17. The column that separates "slow because it is computing" from "slow because it is
    // waiting for a disk". Free — field 42 of the stat line the sampler already parses — and
    // conditional on the machine's delay accounting being switched on, which since 5.14 it is not
    // by default: with it off the kernel writes a literal nought there for every process, and a
    // table-wide column of noughts reading "nothing here ever waits" is the same lie as any other
    // unfilled counter (PRD §72.3).
    new(ProcessField.BlockIoWait, "io.wait", "I/O wait", "IOwait",
      "How long this process has spent waiting for block I/O rather than running. Needs the kernel's delay accounting, which is compiled in and switched off on an ordinary machine — sysctl kernel.task_delayacct=1 turns it on, and until it is, this says so rather than reporting nought.",
      FieldKind.Cumulative, FieldUnit.Nanoseconds, _LINUX, FieldCost.Free, 96, 9, true, true,
      Aliases: "iowait blkio"),
    // PRD §17. A syscall per process per sample — ioprio_get has no file to read it out of — so it
    // is High and nothing turns it on but somebody naming the column (PRD §5.4).
    new(ProcessField.IoPriority, "io.priority", "I/O priority", "IOPri",
      "Which class of the disk scheduler this process's requests belong to. The one control that stops a backup or an indexer making a machine unusable without slowing it down much: \"default\" is the ordinary answer and means the kernel derives it from the nice value.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 120, 12, false, false,
      Aliases: "ioprio ionice"),
    new(ProcessField.IoHistory, "io.history", "I/O history", "I/O hist",
      "The last sixty seconds of read and write traffic.",
      FieldKind.Graph, FieldUnit.BytesPerSecond, _ALL, FieldCost.Derived, 90, 12, false, false,
      HistorySeries.Io,
      History: FieldHistory.Row),

    // PRD §18. Counts of endpoints, and deliberately not counts of traffic: Linux attributes no
    // bytes to a process without packet accounting or eBPF, so the byte and rate fields of §18 are
    // absent rather than filled from the sockets a process happens to hold open at the moment
    // somebody looked (PRD §72.3). All four are High for one reason — the join from a socket to a
    // process is a readlink per open descriptor on the machine — and so none is default-visible.
    new(ProcessField.TcpConnectionCount, "tcp.count", "TCP connections", "TCP",
      "How many TCP sockets this process holds a descriptor on, listeners included.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 116, 5, true, true,
      Aliases: "tcp connections",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.UdpSocketCount, "udp.count", "UDP sockets", "UDP",
      "How many UDP sockets this process holds a descriptor on. A datagram socket has no connection to count, so this counts the sockets themselves.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 98, 5, true, true,
      Aliases: "udp",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ListeningSocketCount, "net.listening", "Listening", "Lstn",
      "How many of this process's sockets are waiting for connections rather than making them. TCP only: a UDP socket bound to a port is not listening in any sense the kernel records.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 92, 5, true, true,
      Aliases: "listening",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.RemoteEndpointCount, "net.remote.count", "Remote endpoints", "Peers",
      "How many distinct peers this process is connected to. Distinct addresses and ports rather than connections, because two connections to one machine are one correspondent.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 128, 6, true, true,
      Aliases: "peers remotes",
      Privilege: FieldPrivilege.Owner),

    // PRD §19. Linux only so far: Windows reads its own performance counters and that is not
    // written yet, and a field claiming to work there would be worse than one that says it does not.
    // Expensive without exception — the kernel's accounting is a file per open descriptor and
    // NVIDIA's is a library call per card — so every one of these is High and none is default-visible
    // (PRD §5.4).
    new(ProcessField.GpuPercent, "gpu", "GPU %", "GPU%",
      "How much of its adapter this process is using: the busiest of the engines it is running on, never their sum — a card's engines run at once, so adding them reports a transcode at two hundred percent.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 78, 5, true, true,
      Aliases: "gpu.percent",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuEngineName, "gpu.engine", "GPU engine", "Engine",
      "Which part of the adapter the process is busiest on: 3D, compute, copy, encode or decode.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 96, 8, false, false,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuEnginePercent, "gpu.engine.percent", "GPU engine %", "Eng%",
      "The busiest engine's own share of the interval — the number the engine column names.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 104, 6, true, true,
      Privilege: FieldPrivilege.Owner,
      History: FieldHistory.Process),
    new(ProcessField.GpuAdapter, "gpu.adapter", "GPU adapter", "Card",
      "Which graphics adapter these figures came from. A laptop has two, and a GPU figure that does not say which one is unreadable on exactly the machines where it matters.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 96, 7, false, false,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuDedicatedMemory, "gpu.mem.dedicated", "GPU dedicated memory", "GPUmem",
      "Adapter memory this process holds — VRAM on a discrete card.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 148, 7, true, true,
      Aliases: "vram",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuSharedMemory, "gpu.mem.shared", "GPU shared memory", "GPUshr",
      "System memory the adapter is using for this process: GTT on a discrete card, and all of it on an integrated one, that being what integrated means.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 136, 7, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuTotalMemory, "gpu.mem.total", "GPU memory", "GPUtot",
      "Dedicated and shared adapter memory together.",
      FieldKind.Instant, FieldUnit.Bytes, _LINUX, FieldCost.High, 100, 7, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuDedicatedMemoryDelta, "gpu.mem.dedicated.delta", "GPU memory delta", "GPUm/s",
      "How fast the process's dedicated adapter memory is moving. A renderer whose VRAM only climbs is the one that will eventually stop the machine drawing anything.",
      FieldKind.Rate, FieldUnit.BytesPerSecond, _LINUX, FieldCost.High, 124, 8, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuGraphicsPercent, "gpu.graphics", "GPU 3D", "3D%",
      "Share of the adapter's graphics engine — shaders and rasterisation.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 82, 5, true, true,
      Aliases: "gpu.3d",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuComputePercent, "gpu.compute", "GPU compute", "Cmp%",
      "Share of the adapter's compute engine, where the driver counts it apart from graphics. NVIDIA does not, and says so rather than reporting nought.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 104, 6, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuCopyPercent, "gpu.copy", "GPU copy", "Cpy%",
      "Share of the adapter's copy engines — the ones that move memory without the shaders.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 88, 6, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuEncodePercent, "gpu.encode", "GPU encode", "Enc%",
      "Share of the adapter's video encoder.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 98, 6, true, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.GpuDecodePercent, "gpu.decode", "GPU decode", "Dec%",
      "Share of the adapter's video decoder.",
      FieldKind.Rate, FieldUnit.Percent, _LINUX, FieldCost.High, 98, 6, true, true,
      Privilege: FieldPrivilege.Owner),

    new(ProcessField.Elevated, "elevated", "Elevated", "Elev",
      "Whether the process runs with administrative authority — effective uid 0 on Unix.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.Free, 76, 5, false, true,
      Aliases: "root admin"),
    new(ProcessField.Integrity, "integrity", "Integrity", "Integ",
      "The Windows mandatory integrity level: untrusted, low, medium, high or system.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 90, 7, false, true),
    // PRD §21. What the token and the process handle say about how much the kernel is holding this
    // process apart from the rest of the machine. All three are Windows-only, all three are constant
    // for a process's life, and all three come off a handle the owner lookup already opens — so they
    // cost nothing that was not already being paid (PRD §5.4).
    new(ProcessField.Protected, "protected", "Protected", "Prot",
      "Whether the kernel is keeping other processes out of this one. A protected process cannot be opened for reading, injected into or debugged even by an administrator, which is why a debugger that will not attach to it is not a fault.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 90, 5, false, true,
      Aliases: "pp ppl"),
    new(ProcessField.ProtectionLevel, "protection.level", "Protection level", "ProtLvl",
      "Which protection the process holds, by the signer class that granted it: the Windows trusted computing base, an antimalware service, the store, or an Authenticode publisher. \"none\" is a real answer and the answer for nearly every process on a machine.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 160, 12, false, true,
      Aliases: "protlevel"),
    new(ProcessField.AppContainer, "appcontainer", "AppContainer", "AppCtr",
      "Whether the process runs inside an AppContainer — the sandbox a packaged application and a browser renderer are put in, which decides what the process may reach rather than who it runs as.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 120, 6, false, true,
      Aliases: "sandbox"),

    // PRD §21. The six per-process mitigation policies, which are Windows' own idea and not a
    // spelling of anything Linux has: they are what was *asked for* on behalf of the process, where
    // what Linux publishes is what is switched on for a task — spec.ssb, spec.ib and shadow.stack
    // above (PRD §5.3). High and asked for, because unlike the token fields these need a handle
    // opened with PROCESS_QUERY_INFORMATION, which is a stronger right than the owner lookup takes
    // and a separate open for every process (PRD §5.4).
    new(ProcessField.DataExecutionPrevention, "dep", "DEP", "DEP",
      "Whether the process may execute data pages, and whether that can still be changed. Permanent is the interesting half: a process that has locked DEP on cannot be talked out of it later, which is a stronger statement than merely having it enabled.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 110, 12, false, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.AddressSpaceRandomisation, "aslr", "ASLR", "ASLR",
      "Which parts of address-space randomisation the process asked for: bottom-up allocations, forced relocation of images that would rather not move, and high entropy. \"off\" here is a finding about a program rather than about the machine.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 170, 22, false, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ControlFlowGuard, "cfg", "Control flow guard", "CFG",
      "Whether indirect calls in this process are checked against the set of functions the compiler said were callable. Strict mode is the stronger form: every image loaded must have it too, or it does not load.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 160, 14, false, true,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ShadowStackPolicy, "cet", "Shadow stack policy", "CET",
      "Whether hardware-enforced stack protection was asked for on this process, and how hard. A request rather than a reading: what a process asked for and what the CPU underneath it can actually do are two questions, and the machine's own support for the feature is in the CPU page.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 170, 14, false, true,
      Aliases: "cet.policy shadowstack.policy",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ArbitraryCodeGuard, "acg", "Dynamic code", "ACG",
      "Whether the process is forbidden to generate code at runtime or to make existing code writable. A just-in-time compiler cannot run under it, which is why a browser enables it in the processes that have no need to.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 130, 12, false, true,
      Aliases: "dynamiccode",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.CodeIntegrityGuard, "cig", "Code integrity", "CIG",
      "Which signatures an image must carry before this process will load it: Microsoft's, the store's, or Microsoft's plus the hardware labs'. It restricts what may be loaded into the process and says nothing about whether what is already loaded was signed, which is the signature columns' question.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 140, 14, false, true,
      Aliases: "signaturepolicy",
      Privilege: FieldPrivilege.Owner),

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
      Aliases: "sha256 hash",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ImageSha1, "hash.sha1", "SHA-1", "SHA-1",
      "The SHA-1 of the running image. Collidable since 2017 and kept only because so many package manifests and threat feeds are still keyed by it; on its own it is evidence of nothing.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 300, 40, false, false,
      Aliases: "sha1",
      Privilege: FieldPrivilege.Owner),

    // PRD §14, §70. Which package a running image belongs to, and whether it is still the file that
    // package shipped. Both are High and for the same reason: the first reads every installed
    // package's file list once — thirty megabytes of text on an ordinary desktop — and the second
    // hashes the image on top of it, so neither happens unless a column or a filter names it
    // (PRD §5.4).
    new(ProcessField.Package, "package", "Package", "Package",
      "Which package the running image belongs to: the distribution's own, or the Flatpak, snap or AppImage it came in, or on Windows the MSIX package it was installed as. \"not packaged\" is a finding rather than a hole — most of what a developer runs is not in any package, and on Windows most of what anybody runs is not.",
      FieldKind.Text, FieldUnit.None, _WINDOWS | _LINUX, FieldCost.High, 220, 24, false, false,
      Aliases: "pkg",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ApplicationId, "app.id", "Application ID", "AppID",
      "The platform application id — a Flatpak's org.gimp.GIMP, a snap's name, an MSIX package's family name. Native Linux programs and ordinary Windows ones have none, and this says so rather than repeating the package name into a column that means something else.",
      FieldKind.Text, FieldUnit.None, _WINDOWS | _LINUX, FieldCost.High, 200, 24, false, false,
      Aliases: "appid",
      Privilege: FieldPrivilege.Owner),
    // PRD §14's `app.name`. A Windows binary carries its product name in a version resource and an
    // ELF has no such section, so the same fact lives elsewhere on Linux: in the desktop entry that
    // starts the program, which is where every menu and taskbar on the machine already reads it
    // from. High because answering it means reading the machine's three hundred desktop files, once.
    new(ProcessField.ApplicationName, "app.name", "Application", "App",
      "What a person calls the program: the name out of the desktop entry that starts it, which is the string the machine's own menu shows. \"none\" means there is no entry for it, which most of a process table is and which is a finding rather than a gap. \"several\" means more than one application starts the same program and nothing says which is running — eight entries start libreoffice, and naming one of them would be wrong most of the time.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.High, 190, 20, false, false,
      Aliases: "application app",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.PackageStatus, "package.status", "Package check", "PkgChk",
      "Whether the running image still matches the digest its package recorded, and whether that package was itself signed. An ELF carries no signature to verify, so this is the honest local equivalent: what pacman -Qkk and dpkg --verify ask. It is not a hash, not a trust chain and not a reputation — those are separate questions and nothing here answers them.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 200, 25, false, false,
      Aliases: "pkgcheck",
      Privilege: FieldPrivilege.Owner),
    // PRD §21, §70. The same one of §70's five questions the package check answers, asked of the
    // other kind of evidence there is: a signature the publisher put inside the file, rather than a
    // digest the machine's package manager wrote down about it. Two columns and not one, because a
    // PE carries a signature and an ELF does not, and a Linux package database records a digest
    // where Windows has no such database — one column would mean a different thing on each
    // (PRD §5.3). All five are High for one reason: filling them means reading and digesting the
    // whole image, once per image rather than once per process (PRD §5.4).
    new(ProcessField.ImageSignature, "signature.status", "Signature", "Sig",
      "Whether the image's own embedded signature still covers the bytes that are running: the Authenticode digest recomputed over the file and compared with the one the signature records, and the signature over that digest checked against the signing certificate's key. It is not a trust chain — nothing here asks whether that certificate chains to a root this machine believes in, which is the next column and routinely a different answer.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 200, 25, false, false,
      Aliases: "signature"),
    new(ProcessField.ImageSigner, "signer", "Signer", "Signer",
      "Who the signing certificate says signed the image. Not the company name in the version resource, which anybody may type: this one is bound to the key the signature was made with, and the column beside it says whether that binding still holds.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 220, 24, false, false,
      Aliases: "signed.by"),
    new(ProcessField.CertificateSubject, "cert.subject", "Certificate subject", "CertSubj",
      "The signing certificate's whole subject, for the cases where a common name is not enough to tell two publishers apart.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 380, 40, false, false,
      Aliases: "certificate.subject"),
    new(ProcessField.CertificateIssuer, "cert.issuer", "Certificate issuer", "CertIss",
      "Who issued the signing certificate. Who put their name to the signer, and not who this machine trusts — nothing here has looked at a root store.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 340, 36, false, false,
      Aliases: "certificate.issuer"),
    new(ProcessField.SignatureTimestamp, "signature.timestamp", "Signature timestamp", "SigTime",
      "When the signature was countersigned by a timestamping authority. Its own column because this is what keeps a signature valid after the certificate behind it has expired, which is the ordinary state of most signed software. \"none\" is a real answer: a great deal of software is signed and never dated, and such a signature dies with its certificate.",
      FieldKind.Instant, FieldUnit.Timestamp, _WINDOWS, FieldCost.High, 160, 19, false, true,
      Aliases: "countersigned"),
    // PRD §70's five questions, in five slots that cannot be read off one another. The hash is
    // above; this is the third, and the fourth and fifth are below it. What each column may say is
    // one vocabulary — Verified, Unsigned, Expired and the rest — and which question it answers is
    // the column it is in, never the word.
    new(ProcessField.TrustChain, "trust.chain", "Trust chain", "Chain",
      "Whether anybody this machine trusts signed for the image. Its own question and routinely its own answer: a package built here ships files that match their record exactly and carries nobody's signature, which is Verified in the package check and Unsigned in this one. On Linux it is what the packaging system recorded about the package — pacman's %VALIDATION%, the line pacman -Qi prints as \"Validated By\" — and never anything read out of the ELF, which carries no signature to read. On Windows the question is a real one and the answer is not written: there are certificates and a root store, the verifier beside this deliberately stops at the signer\u0027s own signature, and the column says so rather than not existing \u2014 declaring the field Linux-only would have said Windows has no such thing, which is the one platform where it plainly does.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.High, 190, 25, false, false,
      Aliases: "chain validated.by",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.Reputation, "reputation", "Reputation", "Rep",
      "What an online service says about this image, which is nothing and always will be: no provider ships, none can be configured, and nothing about the executable is sent anywhere. It reads \"not asked\" rather than \"not implemented\", because §70 refuses the provider rather than deferring it and a cell that says \"not yet\" about a settled decision is a promise nobody made. It has a column of its own so that a digest computed on this machine can never be read as a file submitted from it — §3 promises no silent transmission, and a blank where the question should be is how that promise gets quietly broken.",
      FieldKind.State, FieldUnit.None, _ALL, FieldCost.Free, 120, 9, false, false),
    new(ProcessField.Runtime, "runtime", "Runtime", "Runtime",
      "What is executing inside the process: a managed runtime, or machine code. Read from the modules the process has mapped rather than from its name, because a process called java may be a shell script and a renamed one may be anything at all.",
      FieldKind.State, FieldUnit.None, _LINUX, FieldCost.High, 110, 8, false, false,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ImageCreated, "exe.created", "Image created", "Created",
      "When the image file was created, where the file system remembers one. On Windows that is always — NTFS has recorded a creation time for every file since it was written — while on Linux many file systems carry no birth time at all, and there this is unknown rather than the epoch.",
      FieldKind.Instant, FieldUnit.Timestamp, _WINDOWS | _LINUX, FieldCost.High, 150, 19, false, true,
      Aliases: "created birth",
      Privilege: FieldPrivilege.Owner),

    // PRD §14. The five strings a PE keeps in its own version resource, plus the subsystem out of
    // the same file's optional header. Windows-only because the resource is: an ELF has no such
    // section and never did, and the nearest Linux facts are the package's — which are a different
    // question with a different answer and are the `package` and `app.name` columns above (PRD §5.3).
    // All six are High and for one reason: filling them means opening and reading the image. Once
    // per image rather than once per process, because three hundred processes of one runtime share
    // one binary (PRD §5.4).
    new(ProcessField.ImageDescription, "description", "Description", "Descr",
      "What the publisher calls the program, out of the image's version resource. Not the process's name, which is whatever the program decided to call itself, and routinely the only cell on the row that says what a service actually is.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 260, 24, false, false,
      Aliases: "file.description"),
    new(ProcessField.ImageCompany, "company", "Company", "Company",
      "Who publishes the image, as its version resource claims. A claim and not a verification: anybody may write anything here, and whether somebody this machine trusts signed for it is the signature columns' question, not this one.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 180, 20, false, false,
      Aliases: "publisher company.name"),
    new(ProcessField.ImageProduct, "product", "Product", "Product",
      "Which product the image belongs to, as its version resource claims. Several files of one suite share it, which is what makes it worth a column beside the file's own description.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 180, 20, false, false,
      Aliases: "product.name"),
    new(ProcessField.ImageProductVersion, "product.version", "Product version", "ProdVer",
      "The version of the product the image belongs to, as the publisher wrote it. A string rather than a number: \"10.0.19041.1 (WinBuild.160101.0800)\" is a real value of this field.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 150, 14, false, false,
      Aliases: "prodver"),
    new(ProcessField.ImageFileVersion, "file.version", "File version", "FileVer",
      "The version of this file, as the publisher wrote it. Routinely not the same as the product version and routinely not the same as the four numbers the installer compares, which is why it is its own column and is kept as the string it is.",
      FieldKind.Text, FieldUnit.None, _WINDOWS, FieldCost.High, 150, 14, false, false,
      Aliases: "fileversion ver"),
    new(ProcessField.Subsystem, "subsystem", "Subsystem", "Subsys",
      "What the loader is expected to give the program: a window, a console, or nothing at all. Read from the image's own header, so a console program started without one still reads as a console program. PE only — an ELF declares no subsystem, and that is not the unknown subsystem.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 120, 10, false, false),
    // PRD §14. One extra call on a handle the identity read already has open, and constant for a
    // process's life, so it is free in the sense that matters: nothing on the sampling path pays for
    // it twice.
    new(ProcessField.Emulation, "emulation", "Emulation", "Emul",
      "Which instruction set the process is being translated from, or none. An x86 program on an x64 machine and an x64 program on an ARM64 one are both this column's business; a program running on the machine's own instruction set is \"native\", which is an answer rather than an empty cell.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 130, 10, false, true,
      Aliases: "wow64 translation"),

    // PRD §22. The whole of what this program will report per process about energy, and deliberately
    // so: the eight columns beside these in §22 would each be a machine-level figure divided by a
    // guess, and a model shown where a measurement is claimed is the dishonesty §72.3 exists to
    // prevent. This one is neither a model nor a measurement — it is a documented state, read
    // through a documented call, and it says what was asked for rather than what is being spent.
    // One reading answers both: the second is the first wearing the name the operating system's own
    // window uses for it, the way `protected` and `protection.level` are one call and two questions.
    new(ProcessField.BackgroundQualityOfService, "qos.background", "Background QoS", "QoS",
      "Which energy behaviour Windows has been asked to give the process: throttled to the efficient cores and the slower clock, held at full speed, or left for the system to decide. \"system managed\" is the ordinary answer and a real one — it means nobody has asked for either, not that nobody could read it.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 160, 16, false, false,
      Aliases: "qos ecoqos"),
    new(ProcessField.EcoMode, "eco.state", "Efficiency mode", "Eco",
      "The same reading as the column beside it, in the words Task Manager uses: whether the process has been put into efficiency mode. Kept apart from it because \"eco: off\" and \"eco: system managed\" are different findings, and a yes-or-no column would have to round one of them into the other.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.High, 140, 14, false, false,
      Aliases: "eco efficiency"),

    new(ProcessField.ThreadCount, "threads", "Threads", "Thr",
      "How many threads the process currently has.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.Free, 64, 4, true, true,
      History: FieldHistory.Process),
    new(ProcessField.HandleCount, "handles", "Handles", "Hnd",
      "Open handles on Windows, open file descriptors on Unix.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.High, 66, 5, true, true,
      Aliases: "fds fd",
      Privilege: FieldPrivilege.Owner,
      History: FieldHistory.Process),
    // PRD §20. One pass over the descriptor table, and the same classification the handle view of
    // §32 uses. Expensive without exception — a link to resolve per descriptor on top of the
    // directory listing that was already the most costly read in the sampler — so all three are
    // High and none is default-visible (PRD §5.4).
    new(ProcessField.SocketCount, "socket.count", "Open sockets", "Sock",
      "How many of the process's descriptors are sockets. A server leaking connections shows it here long before the machine runs out of them.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 106, 6, true, true,
      Aliases: "sockets",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.FileCount, "file.count", "Open files", "Files",
      "How many descriptors are open on a name in the file system, directories included. Not the same as the handle count, most of which is usually anything but a file.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 96, 6, true, true,
      Aliases: "files",
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.PipeCount, "pipe.count", "Open pipes", "Pipes",
      "How many descriptors are pipes. Both ends of one pipe are a descriptor each, so a shell pipeline holds two of them per process.",
      FieldKind.Instant, FieldUnit.Count, _LINUX, FieldCost.High, 96, 6, true, true,
      Aliases: "pipes",
      Privilege: FieldPrivilege.Owner),
    // PRD §20. The kernel object types Windows has and Unix does not, tallied out of the machine's
    // own handle table. Windows-only because the objects are: an eventfd is a descriptor and is
    // counted as one above, a futex has no kernel object to count, a POSIX semaphore is a mapped
    // file, a section's Linux equivalent is a mapping and belongs to the memory map, and there is no
    // registry (PRD §5.3). All five come out of one pass over one table, so naming any of them buys
    // all five — and nothing names them but a column or a filter, because the table is the whole
    // machine's (PRD §5.4).
    new(ProcessField.EventObjectCount, "event.count", "Events", "Evt",
      "How many event objects the process holds a handle on. A thread pool that leaks them shows it here long before anything else on the row moves.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 90, 6, true, true,
      Aliases: "events"),
    new(ProcessField.SemaphoreObjectCount, "semaphore.count", "Semaphores", "Sem",
      "How many semaphore objects the process holds a handle on.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 110, 6, true, true,
      Aliases: "semaphores"),
    new(ProcessField.MutexObjectCount, "mutex.count", "Mutexes", "Mtx",
      "How many mutex objects — mutants, in the kernel's own vocabulary — the process holds a handle on.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 96, 6, true, true,
      Aliases: "mutexes mutants"),
    new(ProcessField.SectionObjectCount, "section.count", "Sections", "Sect",
      "How many section objects the process holds a handle on. A section is what Windows maps memory through — a file mapping, a shared segment — so a process holding many is one sharing memory with many others.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 100, 6, true, true,
      Aliases: "sections"),
    new(ProcessField.RegistryKeyCount, "regkey.count", "Registry keys", "Keys",
      "How many registry keys the process holds open. Keys left open are the classic reason a configuration change does not take effect until something is restarted.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 130, 6, true, true,
      Aliases: "regkeys keys"),

    // PRD §20, §39. Not handles and not in that table: the desktop's own quotas, which a program can
    // exhaust while every other counter on its row still looks healthy. They move constantly, so
    // unlike the tallies above they cannot be cached for a process's life and cost a call each per
    // process per sample — which is why they are asked for rather than sampled (PRD §5.4).
    new(ProcessField.UserObjectCount, "user.objects", "USER objects", "USER",
      "How many window-manager objects the process holds — windows, menus, cursors, hooks. There is a per-process quota of ten thousand by default, and a program that reaches it stops being able to make windows while looking otherwise healthy.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 120, 6, true, true,
      Aliases: "userobjects"),
    new(ProcessField.GdiObjectCount, "gdi.objects", "GDI objects", "GDI",
      "How many graphics objects the process holds — device contexts, brushes, pens, bitmaps, fonts. The same ten-thousand quota and the same failure: drawing simply stops working.",
      FieldKind.Instant, FieldUnit.Count, _WINDOWS, FieldCost.High, 116, 6, true, true,
      Aliases: "gdiobjects"),

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
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 96, 20, false, false,
      Privilege: FieldPrivilege.Owner),
    new(ProcessField.ContainerId, "container.id", "Container ID", "CID",
      "The container this process belongs to, taken out of its cgroup path.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 68, 14, false, false),
    new(ProcessField.Priority, "priority", "Priority", "Pri",
      "Scheduler priority in the platform's own scale.",
      FieldKind.Instant, FieldUnit.Count, _ALL, FieldCost.Free, 74, 4, true, true,
      Aliases: "prio"),
    // PRD §15. The band beside the number, because on Windows the band is what everything else on
    // the machine speaks in: SetPriorityClass takes it, Task Manager shows it, and the base priority
    // in the column above is what the kernel derived from it. Free, being that derivation inverted
    // rather than a call. Linux has no such band — nice orders tasks inside SCHED_OTHER and the class
    // is sched.class — so folding either into the other would be the false equivalence §5.3 forbids.
    new(ProcessField.PriorityClass, "priority.class", "Priority class", "PriCls",
      "Which priority band Windows runs the process in: idle, below normal, normal, above normal, high or real time. The band rather than the number, because it is what every other tool on the machine speaks in and what the base priority beside it is derived from.",
      FieldKind.State, FieldUnit.None, _WINDOWS, FieldCost.Free, 132, 12, false, true,
      Aliases: "priorityclass class"),
    new(ProcessField.SessionId, "session", "Session", "Ses",
      "The login or terminal session the process belongs to.",
      FieldKind.Identifier, FieldUnit.None, _ALL, FieldCost.Free, 74, 5, true, false),
    new(ProcessField.StartTime, "start", "Start time", "Started",
      "When the process was created.",
      // 152 rather than 140: the value is nineteen characters exactly and the old width fitted
      // eighteen of them, so every timestamp in the window lost the units digit of its seconds —
      // 22:03:2, which reads as a time rather than as a truncation and so went unnoticed.
      FieldKind.Instant, FieldUnit.Timestamp, _ALL, FieldCost.Free, 152, 19, false, true,
      Aliases: "started starttime"),
    new(ProcessField.Container, "cgroup", "Container / cgroup", "Cgroup",
      "The cgroup or container the process belongs to.",
      FieldKind.Text, FieldUnit.None, _LINUX, FieldCost.Free, 240, 40, false, false,
      Aliases: "container"),
    new(ProcessField.ImagePath, "path", "Image path", "Path",
      "Full path of the executable image.",
      FieldKind.Text, FieldUnit.None, _ALL, FieldCost.Free, 320, 60, false, false,
      Aliases: "image exe",
      Privilege: FieldPrivilege.Owner),
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
    // "process.name" is "name". PRD §84 writes its rules that way, and a rule language and a filter
    // language that disagreed about how to spell a field would be two languages. Stripped here, once,
    // rather than added as an alias to every one of the entries above — a prefix is a prefix, and a
    // hundred and sixty extra alias strings would be a hundred and sixty chances to miss one.
    if (wanted.StartsWith("process.", StringComparison.OrdinalIgnoreCase) && wanted.Length > 8)
      wanted = wanted[8..];

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
