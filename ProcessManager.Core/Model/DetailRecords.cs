namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Whose code a thread is executing: its own, or the kernel's on its behalf (PRD §29).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the ordinary answer rather than the exceptional one. Linux will only say
/// which side of the boundary a thread is on to a reader that could have attached a debugger to it,
/// so on a desktop this is <see cref="Kernel"/> for everything blocked in a named wait channel and
/// <see cref="Unknown"/> for the rest. Guessing "user" from a thread that is merely runnable would
/// be a coin toss dressed as a reading (PRD §5.3).
/// </remarks>
public enum ThreadMode : byte {
  Unknown = 0,
  User,
  Kernel,
}

/// <summary>
/// What <c>/proc/[pid]/task/[tid]/syscall</c> says a thread is in the middle of (PRD §29).
/// </summary>
/// <remarks>
/// The only file on Linux that names the user-space instruction pointer and stack pointer of a
/// thread that is not the caller. It needs <c>PTRACE_MODE_ATTACH</c>, which same-uid ownership does
/// not grant under the default <c>yama/ptrace_scope</c>, so on most machines every field here is
/// <see cref="UnknownReason.NotPermitted"/> — and that is a different statement from zero.
/// </remarks>
/// <param name="Number">
/// The system call the thread is executing, or <see cref="UnknownReason.NotSupportedOnPlatform"/>
/// when it is in the kernel without being in one — the kernel writes <c>-1</c> for that, and -1 is
/// not a system call number that could be looked up.
/// </param>
public readonly record struct ThreadSyscall(
  ThreadMode Mode,
  Counter Number,
  Counter StackPointer,
  Counter InstructionPointer
) {

  /// <summary>A file nobody could read, with the reason in every field it would have had.</summary>
  public static ThreadSyscall Unreadable(UnknownReason reason) => new(
    ThreadMode.Unknown,
    Counter.Unknown(reason),
    Counter.Unknown(reason),
    Counter.Unknown(reason)
  );

  /// <summary>
  /// A thread the kernel reports as <c>running</c>.
  /// </summary>
  /// <remarks>
  /// It is on a processor, so it is executing its own code and there are no registers to hand out —
  /// the kernel does not stop a running task to take a snapshot of it. That makes the mode an answer
  /// and the three numbers a hole, which is exactly the shape this record exists for.
  /// </remarks>
  public static readonly ThreadSyscall Running = new(
    ThreadMode.User,
    Counter.NotSupported,
    Counter.NotSupported,
    Counter.NotSupported
  );

}

/// <summary>
/// What the three numbers of <c>/proc/[pid]/task/[tid]/schedstat</c> say (PRD §29).
/// </summary>
/// <remarks>
/// Readable without any privilege at all, which makes it the one scheduling file that answers for
/// another user's threads. It needs <c>CONFIG_SCHEDSTATS</c>: a kernel built without it has no such
/// file, and the absence is <see cref="UnknownReason.NotSupportedOnPlatform"/> rather than a thread
/// that has never waited (PRD §72.3).
/// </remarks>
/// <param name="QueuedNs">
/// Time spent on a run queue wanting a processor and not having one, since the thread started. It is
/// emphatically <em>not</em> "how long the current wait has lasted" — see §29 — and is named for
/// what it is so that nobody reads it as the other thing.
/// </param>
/// <param name="Timeslices">How many times the scheduler has given it a processor.</param>
public readonly record struct ThreadSchedStat(
  Counter RunNs,
  Counter QueuedNs,
  Counter Timeslices
) {

  /// <summary>Nothing was read, and every field says why.</summary>
  public static ThreadSchedStat Unreadable(UnknownReason reason)
    => new(Counter.Unknown(reason), Counter.Unknown(reason), Counter.Unknown(reason));

}

/// <summary>
/// One thread of a process. Collected on demand for the thread view only — walking every thread of
/// every process on every tick is most of what makes a monitor expensive (PRD §3.5).
/// </summary>
/// <param name="Name">
/// The thread's own name. Linux keeps one per thread and it is in the same <c>stat</c> line the rest
/// of this comes from, so it costs nothing; Windows names threads only when a program bothers to.
/// </param>
/// <param name="LastCpu">Which logical processor it last ran on, or -1 when unknown.</param>
/// <param name="WaitReason">
/// What the thread is blocked on. The answer to "why is this hanging" more often than any other
/// field here (PRD §2), and the one that needs no stack walk to get.
/// </param>
/// <param name="VoluntaryContextSwitches">
/// Switches the thread asked for by blocking. Split from the total because the two halves mean
/// opposite things: a thread with millions of these is waiting on something, while the same number
/// of involuntary ones is a thread being pushed off a contended processor.
/// </param>
/// <param name="InvoluntaryContextSwitches">Switches the scheduler imposed — see above.</param>
/// <param name="BasePriority">
/// The priority the thread was given rather than the one it is running at: the nice value on Linux,
/// where the effective priority in <see cref="Priority"/> moves with it. <see langword="null"/> when
/// the platform did not say, because every integer in the Unix range is a legal nice value and none
/// of them can stand in for "unknown".
/// </param>
/// <param name="Policy">Which scheduler class runs it (PRD §5.3).</param>
/// <param name="Affinity">
/// The processors it is allowed on, in the kernel's own list notation (<c>0-7,16</c>), or
/// <see langword="null"/> when unreadable. Kept as the kernel wrote it rather than expanded to a
/// mask: on a 128-way machine the list is the readable form and the mask is not.
/// </param>
/// <param name="StartAddress">
/// Where the thread began executing. A <see cref="Counter"/> and not an address, because zero is an
/// address and the platforms disagree about whether they have one. Windows records the start routine
/// of every thread. Linux records none: <c>clone</c> is handed a stack and a register set, and the
/// entry point is gone by the time the thread exists — so the only thread whose start is knowable is
/// the first one, which began at the executable's ELF entry point. Every other thread reports
/// <see cref="UnknownReason.NotSupportedOnPlatform"/> rather than the zero that used to sit here and
/// render as <c>0x0</c> (PRD §29, §72.3).
/// </param>
/// <param name="StartModule">The image <paramref name="StartAddress"/> falls inside, or null.</param>
/// <param name="StartSymbol">
/// The function <paramref name="StartAddress"/> falls inside, from the image's own symbol table. Null
/// for a stripped image, which is most of them on a distribution that ships debug symbols separately.
/// </param>
/// <param name="InstructionPointer">
/// The user-space instruction the thread will resume at, from <c>syscall</c>. Not from <c>stat</c>:
/// its <c>kstkeip</c> field has read zero for every task that is not core-dumping since Linux 4.9,
/// and parsing it would put <c>0x0</c> in this column for the whole machine.
/// </param>
/// <param name="InstructionModule">The image <paramref name="InstructionPointer"/> falls inside.</param>
/// <param name="StackBytes">
/// How much of its stack the thread is using: the distance from the stack pointer to the top of the
/// mapping the stack pointer is in. Unknown whenever the stack pointer is, which is whenever the
/// reader could not have attached a debugger.
/// </param>
/// <param name="Mode">Which side of the user/kernel boundary the thread is on.</param>
/// <param name="SyscallNumber">The system call it is in, if it is in one.</param>
/// <param name="QueuedNs">
/// Cumulative time on a run queue wanting a processor. The honest neighbour of the wait duration §29
/// declines to invent: this is how long the thread has been kept waiting by other threads since it
/// started, not how long its current wait has lasted.
/// </param>
/// <param name="Cycles">
/// Processor cycles the thread has been charged with, from <c>QueryThreadCycleTime</c> on Windows.
/// A different measurement from <paramref name="CpuTimeNs"/> rather than the same one in other units,
/// which is why both are columns: time is what the clock says the thread held a processor for and
/// cycles are what the processor actually retired. On a machine whose frequency moves — every laptop
/// — a thread on a core parked at 800 MHz looks exactly as busy by time as one at 4.8 GHz.
/// <para>
/// Linux reports <see cref="UnknownReason.NotImplementedHere"/> and not <c>n/a</c>: the kernel will
/// count cycles per thread through <c>perf_event_open</c>, subject to
/// <c>kernel.perf_event_paranoid</c>. Nothing here opens one yet, which is a fact about this program
/// and not about the machine (PRD §7, §72.3).
/// </para>
/// </param>
/// <param name="IdealProcessor">
/// The processor the Windows scheduler prefers to run the thread on, from
/// <c>GetThreadIdealProcessorEx</c>. Not the same thing as <paramref name="LastCpu"/> or
/// <paramref name="Affinity"/>: affinity is where a thread is <i>allowed</i>, last CPU is where it
/// happened to run, and this is where the scheduler would put it given a free choice — the hint that
/// keeps a thread near its own cache. Linux has no per-thread equivalent a caller can read, so the
/// answer there is <see cref="UnknownReason.NotSupportedOnPlatform"/>.
/// </param>
/// <param name="TebBase">
/// Where the thread's environment block lives, from <c>NtQueryInformationThread</c>'s
/// <c>ThreadBasicInformation</c>. The address only: what is <i>inside</i> a TEB — the TLS slot array,
/// the last error, the stack limits — is another process's memory, and reading it means attaching,
/// which §4 rules out along with the kernel driver. So this is the pointer a debugger would start
/// from and not the contents it would go on to read.
/// <para>
/// Linux has no TEB. It has a thread pointer serving a similar purpose, and it lives in a register
/// only <c>ptrace</c> will hand over — again the attach §4 refuses — so the answer there is
/// <see cref="UnknownReason.NotSupportedOnPlatform"/> about the structure that does not exist rather
/// than a claim about the one that does.
/// </para>
/// <param name="Owner">
/// The process the thread belongs to, which with <paramref name="Tid"/> and
/// <paramref name="StartTimeUtcTicks"/> is <see cref="Key"/> (PRD §104). Last and defaulted because
/// it is the caller's own argument to <see cref="Abstractions.ISystemProbe.GetThreads"/> handed back;
/// <see cref="ProcessKey.None"/> means nobody stamped it, which <see cref="ThreadKey.IsNone"/> can
/// be asked about rather than being mistaken for pid zero.
/// </param>
public readonly record struct ThreadRecord(
  int Tid,
  ProcessState State,
  Counter CpuTimeNs,
  long StartTimeUtcTicks,
  Counter StartAddress,
  string? StartSymbol,
  int Priority,
  string? Name,
  Counter UserTimeNs,
  Counter KernelTimeNs,
  Counter ContextSwitches,
  int LastCpu,
  string? WaitReason,
  Counter VoluntaryContextSwitches,
  Counter InvoluntaryContextSwitches,
  int? BasePriority,
  SchedulingPolicy Policy,
  string? Affinity,
  string? StartModule,
  Counter InstructionPointer,
  string? InstructionModule,
  Counter StackPointer,
  Counter StackBytes,
  ThreadMode Mode,
  Counter SyscallNumber,
  Counter QueuedNs,
  Counter Cycles,
  Counter IdealProcessor,
  Counter TebBase,
  ProcessKey Owner = default
) {

  /// <summary>
  /// What makes this the same thread across two readings (PRD §104).
  /// </summary>
  /// <remarks>
  /// Not <see cref="Tid"/> on its own: thread ids recycle, and they are unique only inside a
  /// process. <see cref="Sampling.ThreadDelta"/> matches on this, so a pool that ends a worker and
  /// gets its number back for the next one does not charge the new thread with the old one's
  /// processor time.
  /// </remarks>
  public ThreadKey Key => new(this.Owner, this.Tid, this.StartTimeUtcTicks);

}

/// <summary>What a stack frame is a frame of (PRD §30).</summary>
public enum FrameKind : byte {
  Unknown = 0,

  /// <summary>Kernel code, from the thread's kernel stack.</summary>
  Kernel,

  /// <summary>The program's own code, or a library's.</summary>
  User,

  /// <summary>A managed frame, which nothing on this platform produces yet.</summary>
  Managed,
}

/// <summary>
/// One frame of a thread's stack (PRD §30).
/// </summary>
/// <param name="Displacement">
/// How far into <paramref name="Symbol"/> the frame's address is. Meaningless without a symbol, and
/// unknown rather than zero when there is none — a displacement of zero says the address is the
/// first instruction of the function, which is a claim and not a placeholder.
/// </param>
/// <param name="SourceFile">
/// The file the frame's code was compiled from, which needs DWARF this program does not read. Always
/// null today; the column exists because §30 asks for it and an empty column that says why beats a
/// column that is not there.
/// </param>
/// <param name="SourceLine">The line in it, or 0 when there is no source information.</param>
public readonly record struct StackFrame(
  int Index,
  FrameKind Kind,
  Counter Address,
  string? Symbol,
  Counter Displacement,
  string? Module,
  string? SourceFile,
  int SourceLine
);

/// <summary>
/// A thread's stack, as far as the operating system will describe it (PRD §30).
/// </summary>
/// <remarks>
/// <para>
/// The reasons are the point of this record. Linux keeps two stacks per thread and hands out neither
/// freely: the kernel stack behind <c>/proc/[pid]/task/[tid]/stack</c> needs <c>CAP_SYS_ADMIN</c>,
/// and the user-space stack is not exposed at all — walking it means unwinding another process's
/// memory with its debug information, which §4 rules out along with the driver. So a stack here is
/// usually a short list and a sentence about what is missing, and the sentence is the honest part.
/// </para>
/// <para>
/// A viewer that showed an empty list instead would be indistinguishable from a thread with no
/// stack, which no thread has.
/// </para>
/// </remarks>
/// <param name="KernelReason">
/// <see cref="UnknownReason.None"/> when the kernel stack was read, and why it was not otherwise.
/// </param>
/// <param name="UserReason">
/// Why the user-space frames below the boundary are not here. Never <see cref="UnknownReason.None"/>
/// on any platform this program supports, because none of them is unwound.
/// </param>
public readonly record struct ThreadStack(
  int ThreadId,
  IReadOnlyList<StackFrame> Frames,
  UnknownReason KernelReason,
  UnknownReason UserReason
) {

  /// <summary>A stack that could not be taken at all.</summary>
  public static ThreadStack None(int threadId, UnknownReason reason)
    => new(threadId, [], reason, UnknownReason.NotSupportedOnPlatform);

  /// <summary>How many frames came from the kernel side of the boundary.</summary>
  public int KernelFrameCount {
    get {
      var count = 0;
      for (var i = 0; i < this.Frames.Count; ++i)
        if (this.Frames[i].Kind == FrameKind.Kernel)
          ++count;

      return count;
    }
  }

}

/// <summary>
/// The per-thread half of <c>/proc/[pid]/task/[tid]/status</c> (PRD §29).
/// </summary>
/// <remarks>
/// A record rather than a handful of out-parameters because the whole point is that the failure
/// cases travel with the numbers: a status nobody could open has to reach the view as
/// <see cref="UnknownReason.NotPermitted"/> and not as a thread that has never been switched off a
/// processor in its life (PRD §72.3).
/// </remarks>
public readonly record struct ThreadStatus(
  Counter VoluntaryContextSwitches,
  Counter InvoluntaryContextSwitches,
  string? Affinity
) {

  /// <summary>A status that could not be read, with the reason in every counter it would have had.</summary>
  public static ThreadStatus Unreadable(UnknownReason reason)
    => new(Counter.Unknown(reason), Counter.Unknown(reason), null);

  /// <summary>
  /// Both halves added up, or the reason one of them is missing.
  /// </summary>
  /// <remarks>
  /// Adding a known half to an unknown one would produce a total that is quietly too small, so the
  /// unknown wins — which is <see cref="Counter.Since"/>'s rule applied to a sum.
  /// </remarks>
  public Counter TotalContextSwitches
    => this.VoluntaryContextSwitches.TryGetValue(out var voluntary)
      && this.InvoluntaryContextSwitches.TryGetValue(out var involuntary)
        ? Counter.Of(voluntary + involuntary)
        : this.VoluntaryContextSwitches.HasValue
          ? this.InvoluntaryContextSwitches
          : this.VoluntaryContextSwitches;

}

/// <summary>
/// What a mapping may be done with, as the four characters of a <c>maps</c> line spell it.
/// </summary>
/// <remarks>
/// <see cref="None"/> is "nobody read the permissions", not "no permission is granted": every real
/// mapping carries either <see cref="Shared"/> or <see cref="Private"/>, so a mapping with no access
/// at all still parses to <see cref="Private"/> rather than to zero (PRD §72.3). Windows has no
/// per-module equivalent — its protection is per-region, not per-image — and reports
/// <see cref="None"/> for exactly that reason.
/// </remarks>
[Flags]
public enum MapPermissions : byte {
  None = 0,
  Read = 1,
  Write = 2,
  Execute = 4,
  Shared = 8,
  Private = 16,
}

/// <summary>
/// The hardening an image asks for, as its own program headers and dynamic section declare it
/// (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// This is what the <em>file</em> requests, which is not always what the kernel granted: a
/// non-executable stack is honoured everywhere, and address-space randomisation additionally needs
/// the process not to have turned it off with <c>ADDR_NO_RANDOMIZE</c>. The two are separate claims
/// and only the first is readable from a mapped file.
/// </para>
/// <para>
/// <see cref="None"/> means the headers were never read, which is why <see cref="Read"/> exists:
/// without it "this image has no mitigations" and "nobody looked" would be the same value, and the
/// first is a finding while the second is a hole (PRD §72.3).
/// </para>
/// </remarks>
[Flags]
public enum ImageMitigations : ushort {
  None = 0,

  /// <summary>The program headers were read, so the absence of a flag below is a statement.</summary>
  Read = 1,

  /// <summary>
  /// <c>ET_DYN</c>: the image can be loaded anywhere, which is what lets the kernel randomise it.
  /// An <c>ET_EXEC</c> image names its own addresses and is placed where it says.
  /// </summary>
  PositionIndependent = 2,

  /// <summary><c>PT_GNU_STACK</c> without the execute bit.</summary>
  NonExecutableStack = 4,

  /// <summary>
  /// <c>PT_GNU_STACK</c> <em>with</em> it — a finding rather than a mitigation, and the reason the
  /// stack has two flags instead of one: a segment that is absent altogether leaves the decision to
  /// the ABI and neither bit is set.
  /// </summary>
  ExecutableStack = 8,

  /// <summary><c>PT_GNU_RELRO</c>: the relocations are made read-only once they are resolved.</summary>
  Relro = 16,

  /// <summary>
  /// <c>DT_BIND_NOW</c>, or the same request spelled as a bit of <c>DT_FLAGS</c> or
  /// <c>DT_FLAGS_1</c>. Only with this is <see cref="Relro"/> the full protection people mean by it.
  /// </summary>
  BindNow = 32,

  /// <summary>x86 indirect branch tracking — half of what Intel calls CET.</summary>
  IndirectBranchTracking = 64,

  /// <summary>The other half: a shadow stack.</summary>
  ShadowStack = 128,

  /// <summary>AArch64 branch target identification, which is the same idea as x86's.</summary>
  BranchTargetIdentification = 256,

  /// <summary>AArch64 pointer authentication.</summary>
  PointerAuthentication = 512,
}

/// <summary>
/// How an image came to be in the process, as far as the dependency graph can say (PRD §31).
/// </summary>
/// <remarks>
/// Windows records this per module and Linux records nothing at all, so this is derived rather than
/// read: the executable names its interpreter in <c>PT_INTERP</c> and its libraries in
/// <c>DT_NEEDED</c>, and every image that nothing loaded names arrived some other way. That last
/// case is the interesting one and also the least precise — see <see cref="RunTime"/>.
/// </remarks>
public enum ModuleLoadReason : byte {
  /// <summary>Nothing was read. Not "no reason": the file's headers were never opened.</summary>
  Unknown = 0,

  /// <summary>The program itself.</summary>
  Image,

  /// <summary>The dynamic loader the program asked for in <c>PT_INTERP</c>.</summary>
  Interpreter,

  /// <summary>Named in the executable's own <c>DT_NEEDED</c>.</summary>
  Direct,

  /// <summary>Named by another loaded library, and not by the executable.</summary>
  Dependency,

  /// <summary>
  /// Nothing loaded names it.
  /// </summary>
  /// <remarks>
  /// Usually <c>dlopen</c> — a plugin, a graphics driver, a name-service module — and sometimes
  /// <c>LD_PRELOAD</c>. The two are indistinguishable from outside, and so is the third case: a
  /// library named by another image whose own headers could not be read. It says "nothing that could
  /// be read names this", which is exactly the claim the graph supports.
  /// </remarks>
  RunTime,

  /// <summary>Mapped, and not an image: a locale archive, a font, a database.</summary>
  Data,
}

/// <summary>What an image declares itself to be in its own header.</summary>
public enum ModuleType : byte {
  Unknown = 0,
  Executable,
  SharedObject,
  Relocatable,
  CoreDump,
  /// <summary>Readable, and not an image at all: a locale archive, a font, a mapped database.</summary>
  Data,
}

/// <summary>
/// Whose machine code — or whose bytecode — is in a mapped file (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ModuleType"/> answers what the file's own format calls it and stops at the file's
/// first four bytes; this answers which execution engine will read it. The two are not the same
/// question on any machine that runs more than one runtime: every assembly a .NET process loads is
/// a mapped file that is not an ELF, and the modules view called all of them <c>data</c> — the same
/// word it uses for a font and a locale archive. A managed assembly is not data, and saying so is
/// the difference between a wall of identical rows and a list of what the process is running.
/// </para>
/// <para>
/// Read from the file's own header and never from its name. <c>.dll</c> is a managed assembly on
/// one machine and a Windows library on the next, and under Wine a process maps both.
/// </para>
/// </remarks>
public enum ModuleRuntime : byte {

  /// <summary>Nothing was read. Not "no runtime": the file's header was never opened.</summary>
  Unknown = 0,

  /// <summary>An ELF: machine code this kernel loads and runs itself.</summary>
  Native,

  /// <summary>
  /// A PE with a CLI header — a .NET assembly, whatever the extension says.
  /// </summary>
  /// <remarks>
  /// On Linux this is what a .NET process maps, twice per assembly and never executed as a PE: the
  /// runtime reads the metadata out of the mapping and generates the machine code elsewhere.
  /// </remarks>
  Managed,

  /// <summary>A PE without one: a Windows binary, which on Linux means a process under Wine.</summary>
  WindowsNative,

  /// <summary>
  /// A ZIP container — a <c>.jar</c>, a <c>.whl</c>, an Android <c>.apk</c>.
  /// </summary>
  /// <remarks>
  /// Named for the container and not for the language, because the container is what was read. A
  /// JVM maps its jars, and so does anything else that ships a class path as an archive.
  /// </remarks>
  Archive,

  /// <summary>
  /// Read, and none of the above: a font, a locale archive, a mapped database, an icon cache.
  /// </summary>
  /// <remarks>
  /// A finding rather than a hole, and the reason this enum has both this and
  /// <see cref="Unknown"/>: "we looked and it is not code" and "nobody looked" are different
  /// statements about a row (PRD §72.3).
  /// </remarks>
  NotCode,
}

/// <summary>
/// A file mapped into a process: a shared library, the image itself, or a data mapping.
/// </summary>
/// <remarks>
/// One row per load, not per mapping. A shared library appears in <c>maps</c> as four or five
/// consecutive lines — text, rodata, relro, data — and listing it five times helps nobody, so
/// adjacent lines naming the same file are folded: <paramref name="Size"/> is their total,
/// <paramref name="BaseAddress"/> and <paramref name="EndAddress"/> span them, and
/// <paramref name="MappingCount"/> says how many were folded (PRD §31). A file mapped twice at
/// unrelated addresses — which is what .NET does to every assembly it loads — is two rows, because
/// one row spanning the gap between them would describe an image occupying most of the address space.
/// </remarks>
/// <param name="Permissions">
/// The union of the mappings' permission characters, or empty when the platform does not report any.
/// Empty is a hole, not "no access" — see <see cref="MapPermissions"/>.
/// </param>
/// <param name="ResidentBytes">
/// How much of the image is actually in RAM, summed over its mappings. From <c>smaps</c>, which the
/// cheaper <c>maps</c> does not carry — so this is unknown rather than zero whenever the caller read
/// the cheap file or was refused the expensive one (PRD §5.4).
/// </param>
/// <param name="FileModifiedUtcTicks">
/// The backing file's last-write time, or 0 when it could not be stat'ed. Zero here is year 1 rather
/// than a plausible date, which is what makes it safe to render as "unknown".
/// </param>
/// <param name="EntryPoint">
/// Where execution starts, already biased by the load address for a position-independent image, so
/// that it is an address in <em>this</em> process and can be compared with a stack frame.
/// </param>
/// <param name="Interpreter">
/// The program interpreter the image asks for — the dynamic loader. Only an executable declares one;
/// a library, and anything that is not an image, leave it null.
/// </param>
/// <param name="Mitigations">
/// The hardening the file asks for. <see cref="ImageMitigations.None"/> is "the headers were not
/// read" and not "this image is unprotected"; the two are told apart by
/// <see cref="ImageMitigations.Read"/>.
/// </param>
/// <param name="BuildId">
/// The <c>NT_GNU_BUILD_ID</c> note, as hex. The identity a distribution's symbol server, its debug
/// packages and its crash reports are all keyed by, and the one field that says two files are the
/// same build without reading either of them whole. Null where the image carries no such note —
/// which is what a binary built without <c>--build-id</c> looks like, and is a fact about the build
/// rather than about our access to it.
/// </param>
/// <param name="LoadReason">Why this image is here, derived from the dependency graph.</param>
/// <param name="LoadCount">
/// How many times this file is loaded into this process — how many rows of the list, this one
/// included, name it.
/// </param>
/// <remarks>
/// Not the loader's reference count, which is a different number answering a different question.
/// <c>link_map.l_direct_opencount</c> counts the <c>dlopen</c> calls that have not yet been undone,
/// and no file under <c>/proc</c> publishes it; this counts the separate loads of one file that are
/// in the address space right now, which is what the map does say. One is the answer for nearly
/// every row, and that is what makes a two worth seeing: two copies of a library in one process is
/// two sets of its global state, and it is how a plugin that shipped its own <c>libstdc++</c>
/// announces itself. Zero is never right — a row exists because a mapping named it — so a zero here
/// means the pass that fills this in never ran (PRD §72.3).
/// </remarks>
/// <param name="Runtime">
/// Which execution engine reads this file, from its header. <see cref="ModuleRuntime.Unknown"/> is
/// "the header was not read" and <see cref="ModuleRuntime.NotCode"/> is "it was, and this is not
/// code".
/// </param>
public readonly record struct ModuleRecord(
  string Path,
  ulong BaseAddress,
  ulong Size,
  string Permissions,
  ulong EndAddress,
  Counter ResidentBytes,
  Counter FileOffset,
  Counter Inode,
  string? Device,
  bool IsDeleted,
  int MappingCount,
  Counter FileSizeBytes,
  long FileModifiedUtcTicks,
  ModuleType Type,
  string? Architecture,
  Counter EntryPoint,
  string? Soname,
  string? Interpreter,
  ImageMitigations Mitigations,
  string? BuildId,
  ModuleLoadReason LoadReason,
  int LoadCount,
  ModuleRuntime Runtime
);

/// <summary>What a handle or file descriptor refers to.</summary>
public enum HandleKind : byte {
  Unknown = 0,
  File,
  Directory,
  Socket,
  Pipe,
  Event,
  Mutex,
  Section,
  Key,
  Thread,
  Process,
  Device,
  AnonInode,
  /// <summary>An epoll set: a descriptor whose whole purpose is to watch other descriptors.</summary>
  EventPoll,
  Timer,
  Signal,
  /// <summary>An inotify or fanotify watch — a descriptor that reports file-system events.</summary>
  Notify,
  /// <summary>A memfd, or a file under a <c>tmpfs</c> that exists to be shared between processes.</summary>
  SharedMemory,
}

/// <summary>
/// What kind of node a descriptor's target is, as the kernel's own <c>st_mode</c> says (PRD §32).
/// </summary>
/// <remarks>
/// <para>
/// A second axis, not a finer <see cref="HandleKind"/>. The kind is worked out from the name the
/// symlink has — <c>socket:[…]</c>, a path under <c>/dev</c> — and the name is a good guess that is
/// sometimes wrong: a FIFO in <c>/run</c> is a path like any other and read as a file, and every
/// device node is filed by the directory it sits in rather than by what it is. This is the kernel
/// answering the same question with the bits it keeps, and where the two disagree the kernel wins.
/// </para>
/// <para>
/// <see cref="None"/> is the one that has to exist. An anonymous inode — an eventfd, a timerfd, an
/// epoll set — has <c>st_mode</c> <c>0600</c>: the file-type bits are <em>zero</em>, which is not
/// any of the seven POSIX types. A table mapping that nought to a regular file, or to "unknown",
/// would file every event descriptor on the machine under a type it does not have (PRD §72.3).
/// </para>
/// </remarks>
public enum FileNodeType : byte {

  /// <summary>Nobody stat'ed it, or the stat failed. Not an answer about the descriptor.</summary>
  Unknown = 0,

  Regular,
  Directory,
  CharacterDevice,
  BlockDevice,
  Fifo,
  Socket,
  SymbolicLink,

  /// <summary>
  /// Stat'ed, and the type bits were clear: an anonymous inode, which has no file type at all.
  /// </summary>
  None,
}

/// <summary>
/// One open handle (Windows) or file descriptor (Unix). <paramref name="Name"/> is null when the
/// platform would not name it — on Windows that includes handles whose name resolution timed out,
/// which is a normal outcome rather than a failure (PRD §5.2).
/// </summary>
/// <param name="Access">
/// What the holder may do with it: <c>r</c>, <c>w</c> or <c>rw</c> on Unix, from the access mode in
/// the open flags; the access mask on Windows. Null when nothing said.
/// </param>
/// <param name="Position">
/// The file offset the next read or write would use. Meaningless for a socket or an event descriptor,
/// which is why it is a <see cref="Counter"/> and not a number that is always there.
/// </param>
/// <param name="OpenFlags">The raw <c>O_*</c> word the descriptor was opened with.</param>
/// <param name="Inode">
/// The inode behind the descriptor. For a socket this is the number that joins it to a row of
/// <c>/proc/net/tcp</c> — the one field that turns "this process holds a socket" into "this process
/// holds <em>that</em> connection" (PRD §32, §40).
/// </param>
/// <param name="TargetPid">
/// The process a pidfd refers to. Unknown for every other kind, because every other kind refers to
/// something that is not a process.
/// </param>
/// <param name="MountId">
/// Which mount the descriptor's inode lives on, from <c>fdinfo</c>'s <c>mnt_id</c>. On its own it is
/// a number nobody can act on; joined to <c>mountinfo</c> it becomes
/// <paramref name="Device"/> and <paramref name="FileSystem"/>.
/// </param>
/// <param name="Device">
/// The device the descriptor's inode is on, in the <c>major:minor</c> notation the file system uses,
/// followed by where it is mounted. Null when no mount answers for it, which is the ordinary case
/// for a socket, a pipe or an anonymous inode: those live on kernel-internal filesystems that are
/// mounted nowhere and appear in no process's mount table (PRD §32).
/// </param>
/// <param name="FileSystem">The type of that mount — <c>ext4</c>, <c>btrfs</c>, <c>tmpfs</c>.</param>
/// <param name="Detail">
/// What <c>fdinfo</c> said that is specific to this one kind of descriptor: an eventfd's count, the
/// descriptors an epoll set is watching, an inotify watch list, the namespaced pids of a pidfd. Kept
/// as the kernel wrote it, because every kind spells its own state differently and inventing a
/// common shape for them would lose most of it (PRD §5.3). Null when there was none.
/// </param>
/// <param name="NodeType">
/// What the kernel's <c>st_mode</c> says the target is, which is the answer where the name is only
/// a good guess. <see cref="FileNodeType.Unknown"/> means nobody asked.
/// </param>
/// <param name="NodeDevice">
/// The device a character or block node <em>is</em>, as <c>major:minor</c>. Not
/// <paramref name="Device"/>, which is the device the node's inode is <em>on</em>: <c>/dev/null</c>
/// is character device 1:3 and lives on the <c>devtmpfs</c> at 0:7, and confusing the two would
/// report every device node on the machine as the same device. Null for everything that is not a
/// device node, because everything else is not one.
/// </param>
public readonly record struct HandleRecord(
  ulong Handle,
  HandleKind Kind,
  string? Name,
  string? Access,
  Counter Position,
  Counter OpenFlags,
  Counter Inode,
  Counter TargetPid,
  Counter MountId,
  string? Device,
  string? FileSystem,
  string? Detail,
  FileNodeType NodeType,
  string? NodeDevice
);

public enum ConnectionProtocol : byte { Tcp, Tcp6, Udp, Udp6, Unix }

/// <summary>
/// What a socket delivers: a byte stream, individual messages, or messages with record boundaries.
/// </summary>
/// <remarks>
/// Redundant for TCP and UDP, where the protocol already says it, and the whole point for a Unix
/// socket — a stream socket and a datagram socket on the same path are different endpoints and only
/// this tells them apart. Named <c>SocketKind</c> rather than <c>SocketType</c> so that a file with
/// <c>using System.Net.Sockets</c> in it does not silently bind to the other one.
/// </remarks>
public enum SocketKind : byte { Unknown = 0, Stream, Datagram, SeqPacket }

/// <summary>
/// A socket, with the state the kernel reports for it (PRD §40).
/// </summary>
/// <param name="Inode">
/// The socket's inode, which on Linux is the only thing joining it to a process: a descriptor points
/// at <c>socket:[n]</c> and the network tables are keyed by the same <c>n</c>. Zero where the
/// platform has no such number — Windows names the owning process in the table itself and needs no
/// join.
/// </param>
/// <param name="Pid">
/// The process holding a descriptor on it, or 0 when none was found. Zero is never a real owner —
/// pid 0 is the kernel — so it reads as "nobody visible", which on Linux usually means the socket
/// belongs to another user's process whose descriptors we may not list, and sometimes means nothing
/// holds it at all: a <c>TIME_WAIT</c> socket outlives the process that closed it.
/// </param>
/// <param name="UserId">
/// The uid the kernel charges the socket to, or -1 where the platform does not say. This is the
/// socket's own owner and not the owning process's — they are the same in almost every case, and
/// the exception is a socket passed between processes over a Unix socket, which keeps the uid of
/// whoever created it.
/// </param>
/// <param name="Interface">
/// Which interface the local address lives on, or null when nothing claims it. <c>*</c> means the
/// socket is bound to the wildcard address and so is on all of them.
/// </param>
/// <param name="SendQueueBytes">
/// Bytes written and not yet acknowledged by the peer, and bytes received and not yet read. A
/// send queue that stays high is a peer that is not keeping up; a receive queue that stays high is
/// this process not keeping up. On a listening TCP socket the kernel reuses the same two fields for
/// the accept backlog instead, which is why <see cref="State"/> has to be read alongside them.
/// </param>
/// <param name="Retransmits">
/// How often the segment currently awaiting acknowledgement has been sent again — not a total of
/// everything ever retransmitted on this connection, which is
/// <see cref="SocketStatistics.TotalRetransmits"/>. Non-zero here means this connection is losing
/// packets <em>now</em>.
/// </param>
/// <param name="Statistics">
/// What the kernel's socket diagnostics said about this connection, or the reason they said nothing.
/// The socket tables under <c>/proc/net</c> cannot fill this: they carry no byte counters and no
/// round-trip time, and the only way to those is <c>NETLINK_INET_DIAG</c>.
/// </param>
/// <param name="SendRate">
/// Bytes per second in each direction over the interval between the two readings this was derived
/// from. <see cref="UnknownReason.NotSampledYet"/> on the first sight of a socket, because a rate
/// needs two — and a one-shot listing never gets a second.
/// </param>
/// <param name="OwningService">
/// The service or scope the owning process belongs to — a systemd unit name on Linux — or null when
/// nothing owns it, no process was attributed, or the process is not under a unit at all. A socket
/// is not charged to a unit by the kernel; this is the unit of whoever holds the descriptor.
/// </param>
/// <param name="ContainerPath">
/// The owning process's cgroup path, which is what says whether a listening port belongs to the host
/// or to a container. Null for the same three reasons <paramref name="OwningService"/> is.
/// </param>
/// <param name="References">
/// How many references the kernel holds to this socket — <c>sk_refcnt</c>, straight out of the
/// network table's own column.
/// </param>
/// <remarks>
/// This is §32's reference count, and it is a real number rather than a derived one: the five
/// <c>/proc/net</c> tables print it beside every row. It is the closest thing Linux publishes to
/// the reference count Windows keeps on a kernel object, and it is published for sockets and for
/// nothing else — a file, a pipe and an event descriptor have a <c>struct file</c> with a count in
/// it that no file under <c>/proc</c> shows. Note that a socket held by one descriptor commonly
/// reads two or three: the descriptor is one reference and the protocol's own hash tables are the
/// rest, so this counts holders of the socket and not descriptors on it.
/// </remarks>
public readonly record struct ConnectionRecord(
  ConnectionProtocol Protocol,
  SocketKind Kind,
  string LocalAddress,
  int LocalPort,
  string RemoteAddress,
  int RemotePort,
  string State,
  ulong Inode,
  int Pid,
  int UserId,
  string? UserName,
  string? Interface,
  Counter SendQueueBytes,
  Counter ReceiveQueueBytes,
  Counter Retransmits,
  SocketStatistics Statistics,
  Rate SendRate,
  Rate ReceiveRate,
  string? OwningService,
  string? ContainerPath,
  Counter References
);

/// <summary>
/// What <c>NETLINK_INET_DIAG</c> reports about one socket: the counters <c>/proc/net/tcp</c> has no
/// column for (PRD §40).
/// </summary>
/// <remarks>
/// <para>
/// This is <c>ss -i</c>'s source. The kernel answers a <c>SOCK_DIAG_BY_FAMILY</c> request with an
/// <c>inet_diag_msg</c> per socket and, where asked for it, a <c>tcp_info</c> attached — and
/// <c>tcp_info</c> is where the bytes, the segments, the round-trip time and the lifetime
/// retransmission count live.
/// </para>
/// <para>
/// Every member is a <see cref="Counter"/> and none of them is ever a plain zero, because the four
/// ways to have no reading are all common here: the kernel may be built without the diagnostics, the
/// socket may be a datagram socket the module does not describe, the process may not be allowed to
/// ask, or the connection may be in a state that has no <c>tcp_info</c> left. Constructing one of
/// these positionally is deliberate: <c>default</c> would say every counter is a measured nought
/// (PRD §72.3), so there is no parameterless way to make one.
/// </para>
/// </remarks>
/// <param name="BytesSent">
/// Payload bytes handed to the peer over this connection's life, retransmissions included — the
/// figure <c>ss</c> prints as <c>bytes_sent</c>. Not what the interface carried: headers are not in
/// it.
/// </param>
/// <param name="BytesReceived">Payload bytes taken from the peer, the same way round.</param>
/// <param name="PacketsSent">
/// Segments in each direction, which is the honest packet count for a TCP connection: the kernel
/// counts segments and the wire carries one packet per segment except where something fragments.
/// </param>
/// <param name="TotalRetransmits">
/// Every retransmission this connection has ever made, as against <see cref="ConnectionRecord.Retransmits"/>,
/// which is only the segment being retried right now.
/// </param>
/// <param name="RoundTripTimeMicroseconds">
/// The smoothed round-trip time, in microseconds, and the variance the kernel keeps beside it. This
/// is the latency of the path this connection is actually using, measured by the connection itself
/// — not a ping to the same address, which may take a different route.
/// </param>
public readonly record struct SocketStatistics(
  Counter BytesSent,
  Counter BytesReceived,
  Counter PacketsSent,
  Counter PacketsReceived,
  Counter TotalRetransmits,
  Counter RoundTripTimeMicroseconds,
  Counter RoundTripVarianceMicroseconds
) {

  /// <summary>
  /// Nothing was read, and the reason is the same for every counter.
  /// </summary>
  /// <remarks>
  /// The one supported way to make an empty set. A <c>TryGetValue</c> that misses leaves
  /// <c>default</c> behind, whose reason is <see cref="UnknownReason.None"/> — "the value is here and
  /// it is zero" — and that is the defect this exists to make impossible to write by accident.
  /// </remarks>
  public static SocketStatistics Unknown(UnknownReason reason) {
    var counter = Counter.Unknown(reason);
    return new(counter, counter, counter, counter, counter, counter, counter);
  }

  /// <summary>The socket diagnostics were not consulted, or have not answered about this socket.</summary>
  public static readonly SocketStatistics NotRead = Unknown(UnknownReason.NotSampledYet);

  /// <summary>There is no such source here: another platform, or a protocol with no such counters.</summary>
  public static readonly SocketStatistics NotSupported = Unknown(UnknownReason.NotSupportedOnPlatform);

  /// <summary>True when at least one counter carries a reading.</summary>
  public bool HasAny
    => this.BytesSent.HasValue
    || this.BytesReceived.HasValue
    || this.PacketsSent.HasValue
    || this.PacketsReceived.HasValue
    || this.TotalRetransmits.HasValue
    || this.RoundTripTimeMicroseconds.HasValue;

}
