# ProcessManager — Product Requirements & Implementation Checklist

> A cross-platform process manager in C#: a Process-Explorer-shaped desktop UI and an htop-shaped
> terminal UI over one sampling engine. The desktop UI is built on
> [NativeForms](https://github.com/Hawkynt/NativeForms). Shipping platforms are Windows and Linux —
> macOS is a stated future direction (§10 M9), not a shipped feature.

This document is the **authoritative, living checklist**. Every feature is tracked here with `[ ]` /
`[x]` boxes. When code and this document disagree, this document wins unless it is being revised in
the same change. Keep boxes honest: a box is `[x]` only when implemented **and** covered by a test
(and, for anything platform-specific, verified on that platform). Beyond the box, a feature counts as
**finished** only when it is reachable in both front-ends where §7 and §11 say it should be, and
documented under `docs/` — §12 tracks that coverage.

Status legend: `[ ]` not started · `[~]` partial · `[x]` done & tested · `n/a` not applicable to that
platform.

**Everything below is `[ ]` today.** The repository contains this document, the README and the
pipeline. M0 is the first line of code.

---

## 1. Vision & goals

1. **One engine, two faces.** A metric is implemented once in `ProcessManager.Core` and read by both
   the desktop UI and the terminal UI. Neither front-end may reach past Core to the platform, and
   neither may show a number the other physically cannot.
2. **Answers, not dumps.** The program exists for four questions: *what is using the machine*,
   *what is this process doing*, *who has this file/port open*, and *make it stop*. A feature that
   does not serve one of them is decoration.
3. **Cheaper than what it measures.** A monitor that shows up in its own top ten has failed. §4 sets
   the budget and CI enforces it.
4. **Unprivileged by default.** The program runs as you. Root/admin is a separate short-lived helper
   process for the few operations that genuinely need it (§8), never a prerequisite for starting.
5. **Honest about gaps.** Where a platform cannot supply a metric, or supplies it only to root, the
   UI says so in place of the value. No zero-instead-of-unknown, ever — §3.4.
6. **Trim & NativeAOT compatible.** No reflection, no runtime code-gen, no `TypeDescriptor`.
   `IsAotCompatible=true` everywhere; trim/AOT analyzer warnings are build errors.
7. **Terminal-first is not terminal-only.** `--tui` is a first-class front-end, shipped in v1, usable
   over SSH with no toolkit, no display and no fonts (§11).

### Non-goals

- **A kernel driver.** No signed driver, no LKM. Everything Process Explorer reaches only through its
  driver — symbolized thread stacks, kernel object internals, protected-process access, true
  per-process packet capture — is out of scope and stays out. This is the single largest honest gap
  against the tools in the README's inspiration list.
- **Replacing the OS task manager.** No `taskmgr.exe` replacement registry key, no session-critical
  role, no "run at boot".
- **Remote/agent monitoring.** One machine, the local one. No collection server, no fleet view.
- **A profiler.** Sampling at 1 Hz answers "which process", never "which function".
- **Editing the running system beyond §6.4.** No writing to arbitrary process memory, no DLL/`.so`
  injection, no handle closing in another process.
- **macOS in v1.** The probe is a stub that throws (§5.3).

---

## 2. Architecture layers

```
ProcessManager.Core                       sampling engine, model, math          [platform-agnostic]
 ├─ .Model                                snapshots, records, counters (structs, no behavior)
 ├─ .Sampling                             Sampler, RateCalculator, HistoryRing<T>, TreeBuilder
 ├─ .Query                                sort, filter, search across the snapshot
 └─ .Abstractions                         ISystemProbe, IProcessActions, IElevatedChannel
ProcessManager.Platform.Linux             /proc, /sys, cgroup v2, netlink            SHIPPING
ProcessManager.Platform.Windows           NtQuerySystemInformation, iphlpapi, psapi   SHIPPING
ProcessManager.Platform.MacOS             libproc / sysctl                            STUB (throws)
ProcessManager.Elevated                   procman-helper — the only privileged binary
ProcessManager.App                        desktop UI on NativeForms                   (procman)
ProcessManager.Tui                        terminal UI, zero UI dependencies           (procman --tui)
ProcessManager.Tests                      unit + fixture-replay + golden tests
ProcessManager.Benchmarks                 the §4 budget harness, run by nightly CI
```

- **Core never calls a native API.** It asks an `ISystemProbe` for raw counters and does every
  subtraction, division and sort itself. That is what makes the whole engine testable against
  recorded fixture trees rather than against the machine running the tests (§9).
- **A probe returns counters, not rates.** Rates, percentages, deltas and history are Core's job,
  computed identically on every platform. A probe that pre-divides has a bug.
- **Front-ends are read models.** They receive an immutable `SystemSnapshot` and a `SnapshotDelta`
  and render. They never poll the probe, never keep their own history, and never mutate the model.

### AOT/interop rules (enforced, not aspirational)

- [ ] `[LibraryImport]` source-generated P/Invoke only — never `[DllImport]`.
- [ ] No `System.Reflection`, `TypeDescriptor`, `Activator.CreateInstance(Type)` or `dynamic`
      anywhere in Core, the probes or the helper.
- [ ] `IsAotCompatible=true` on every library; `TreatWarningsAsErrors=true` on the AOT publish leg so
      an IL2xxx/IL3xxx warning fails CI (§9.5).
- [ ] Probe selection is a compile-time-visible `switch` on `OperatingSystem.IsLinux()` /
      `IsWindows()` / `IsMacOS()`, not a plugin scan — the trimmer must be able to drop the two
      probes the build does not need.

---

## 3. The sampling model

The core of the program is three hundred lines of arithmetic that everything else displays. It gets
its own section because every subtle bug in a tool of this kind lives here.

### 3.1 Snapshot

- [ ] `SystemSnapshot` — one immutable point-in-time reading: a monotonic timestamp, system-wide
      counters, and a `ProcessRecord` per visible process. Produced by one probe call.
- [ ] The timestamp is from a **monotonic** clock (`Stopwatch.GetTimestamp`), never wall time. A
      wall-clock jump — NTP step, suspend/resume, DST — must not produce a negative interval or an
      infinite rate.
- [ ] `ProcessRecord` is a `readonly record struct` of counters plus interned strings. No per-sample
      allocation of the strings that did not change (§4).

### 3.2 Delta and rate

- [ ] `SnapshotDelta` pairs consecutive snapshots and yields, per process: CPU time consumed, bytes
      read/written, and appeared/exited/changed sets.
- [ ] CPU percent = ΔprocessCpuTime / (Δwallclock × coreCount) by default, with a per-core mode
      (`Δ / Δwallclock`, so a fully busy 8-thread process reads 800 %) selectable in both front-ends.
      Which mode is active is always visible in the column header — the two conventions differ by 8×
      on this machine and a number without its convention is not a number.
- [ ] **PID reuse is detected, not assumed away.** Two snapshots may show the same PID as two
      different processes. The identity key is `(pid, startTime)`; a mismatch is an exit plus a
      start, never a delta. A test pins this (§9.3).
- [ ] **Counter wraparound and reset** yield "unknown" for that interval, not a negative or absurd
      rate. A 32-bit counter that decreased did not decrease.
- [ ] First sample after start has no predecessor: rates are `unknown`, and the UI shows `—`. It does
      not show `0`.

### 3.3 History

- [ ] `HistoryRing<T>` — fixed-capacity ring per tracked series, allocated once. Default 600 samples
      (10 minutes at 1 Hz), configurable; memory is bounded by construction, not by pruning.
- [ ] Per-process history is kept only for processes with an open detail view plus the top N by CPU;
      everything else keeps the current value only. Keeping 600 samples × 1000 processes × 6 series
      is 28 MB of nothing anyone looks at.
- [ ] A gap in sampling (the interval was missed, the machine was asleep) is recorded **as a gap**.
      Plots break the line; they do not interpolate across it.

### 3.4 Unknown is a value

- [ ] Every numeric field is `T?`-shaped with an explicit reason when absent: `NotPermitted`,
      `NotSupportedOnPlatform`, `ProcessExited`, `NotSampledYet`, `CounterInvalid`.
- [ ] Front-ends render each reason distinctly (`—` permission, `n/a` platform, `…` first sample) and
      the reason is available on hover / in the detail pane. This is the rule §1.5 exists for.

### 3.5 Sampling cadence

- [ ] Default interval 1000 ms; settable 250 ms – 60 s. The interval is a *target*: the sampler
      measures its own cost and reports it (§4), and never queues a second sample while the first is
      running.
- [ ] Expensive collections are on a slower cadence than the process list: handles/open files and
      module lists refresh on demand and on the detail view's own timer, never in the main loop.
- [ ] The UI never blocks on a sample. Sampling runs on a background thread; the front-end receives a
      completed snapshot.

---

## 4. Performance & footprint budget

Targets measured by `ProcessManager.Benchmarks` and asserted in nightly CI. The harness fails on
regression rather than printing a number nobody reads.

- [ ] **Full snapshot, 1000 processes**: ≤ 25 ms on Linux, ≤ 15 ms on Windows (one
      `NtQuerySystemInformation` call carries processes *and* threads).
- [ ] **Steady-state allocation: zero.** After warm-up, a sample allocates no managed memory —
      `/proc` files are read into pooled buffers and parsed from `ReadOnlySpan<byte>`; strings that
      did not change are reused from the previous snapshot; no LINQ in the sampling path.
      Enforced by `GC.GetAllocatedBytesForCurrentThread` around the sample loop.
- [ ] **Own CPU cost**: < 1 % of one core at 1 Hz with 1000 processes, measured by sampling ourselves
      in the harness.
- [ ] **Resident memory**: < 60 MB for the desktop UI with 1000 processes and default history,
      < 20 MB for the TUI.
- [ ] **Start to first frame**: < 250 ms desktop, < 100 ms TUI.
- [ ] **Binary size** reported per RID in the CI step summary on every AOT publish, so a regression is
      visible in the run that caused it.

---

## 5. Platform probes

A probe's contract: return raw counters for everything it can read, and a typed reason (§3.4) for
everything it cannot. A probe never guesses, never falls back to a plausible-looking zero, and never
shells out to another program.

### 5.1 Linux — `/proc`, `/sys`, cgroup v2

- [ ] Process list from `/proc/[pid]` directory enumeration, reusing the directory buffer.
- [ ] `/proc/[pid]/stat` — state, ppid, pgrp, session, utime, stime, cutime, cstime, priority, nice,
      num_threads, starttime, vsize, rss. Parsed from bytes; the comm field is parsed by scanning
      **back** from the last `)`, because a process may be named `foo) 0 (bar`.
- [ ] `/proc/[pid]/status` — Uid/Gid (real *and* effective), VmRSS, VmSwap, Threads, context switches.
- [ ] `/proc/[pid]/smaps_rollup` — `Pss`, `Private_Clean`, `Private_Dirty`. This is the honest
      "private bytes"; RSS double-counts shared pages and is labelled as such in the UI.
- [ ] `/proc/[pid]/io` — `read_bytes`, `write_bytes`, `rchar`, `wchar`. Readable only by the owner
      (0400 since kernel 5.12): other users' processes report `NotPermitted` unless the helper is up.
- [ ] `/proc/[pid]/cmdline` (NUL-separated), `/proc/[pid]/environ` (owner only), `/proc/[pid]/cwd`,
      `/proc/[pid]/exe` (readlink).
- [ ] `/proc/[pid]/fd/*` — open files, sockets (as `socket:[inode]`), pipes. Owner or helper only.
- [ ] `/proc/[pid]/maps` — mapped files for the module view.
- [ ] `/proc/[pid]/task/[tid]/stat` — per-thread, for the thread view only (not the main loop).
- [ ] System: `/proc/stat` (per-core jiffies, ctxt, intr, procs_running), `/proc/meminfo`,
      `/proc/loadavg`, `/proc/uptime`, `/proc/diskstats`, `/proc/net/dev`, `/proc/pressure/*` (PSI).
- [ ] Sockets: `/proc/net/{tcp,tcp6,udp,udp6,unix}` joined to processes by socket inode. The join is
      O(fds), done once per network refresh, not per process.
- [ ] `USER_HZ` is read via `sysconf(_SC_CLK_TCK)` through `[LibraryImport]`, not assumed to be 100.
      Page size likewise via `sysconf(_SC_PAGESIZE)` for `statm`/`rss`.
- [ ] cgroup v2 (`/sys/fs/cgroup/…`): `memory.current`, `memory.max`, `cpu.stat`, `io.stat`, so a
      containerized process shows its *limit*, not the host's total. Falls back cleanly on v1 and on
      no-cgroup systems.
- [ ] Users resolved through `getpwuid_r`, cached, with the numeric UID shown when NSS cannot answer.

### 5.2 Windows — native API, no WMI

- [ ] `NtQuerySystemInformation(SystemProcessInformation)` — the whole process *and* thread list in
      one call, into a pooled, grown-on-`STATUS_INFO_LENGTH_MISMATCH` buffer. WMI is not used
      anywhere: it is orders of magnitude slower and needs a service that may be broken.
- [ ] `SystemProcessorPerformanceInformation` for per-core idle/kernel/user, `GetSystemTimes` as the
      cross-check.
- [ ] `QueryFullProcessImageName` for the image path; `NtQueryInformationProcess`
      (`ProcessCommandLineInformation`, `ProcessBasicInformation`) for the command line and PEB.
- [ ] Memory from the `SYSTEM_PROCESS_INFORMATION` block (`PrivatePageCount`, `WorkingSetPrivateSize`,
      `VirtualSize`) — already in the bulk call, so no per-process `OpenProcess` in the main loop.
- [ ] I/O counters likewise from the bulk block (`ReadTransferCount`, `WriteTransferCount`).
- [ ] Modules via the PEB `Ldr` list (`NtReadVirtualMemory`), with Toolhelp32 as the fallback for
      cross-bitness cases.
- [ ] Handles via `NtQuerySystemInformation(SystemExtendedHandleInformation)`; names resolved with
      `NtQueryObject`. **`NtQueryObject` can block forever on a synchronous named pipe** — name
      resolution therefore runs on a dedicated worker with a per-handle timeout, and a handle that
      times out is reported as `<name unavailable>`. This is the single most common way tools of this
      kind hang, and it is a design constraint, not a defect to discover later.
- [ ] Sockets via `GetExtendedTcpTable` / `GetExtendedUdpTable` with `TCP_TABLE_OWNER_PID_ALL`.
- [ ] Actions: `SetPriorityClass`, `SetProcessAffinityMask`, `NtSuspendProcess` / `NtResumeProcess`,
      `TerminateProcess`. Elevation via §8 when `OpenProcess` returns `ERROR_ACCESS_DENIED`.
- [ ] `SeDebugPrivilege` is enabled only inside the helper, never in the UI process.
- [ ] Users resolved from the process token (`OpenProcessToken` + `LookupAccountSid`), cached by SID.

### 5.3 macOS — stub

- [ ] Every member of `MacOsProbe` throws `PlatformNotSupportedException` with a message naming this
      section and the milestone (§10 M9). It does not return empty data — a program that shows an
      empty process list is worse than one that says it does not work here.
- [ ] The macOS CI leg builds and runs it, and records how far it gets, exactly as NativeForms does
      for its Cocoa backend.

### 5.4 Probe capability matrix

Filled in as probes land; `n/a` means the platform has no such concept, not that we skipped it.

| Capability | Linux | Windows | macOS |
|---|---|---|---|
| Process list, CPU, memory | `[ ]` | `[ ]` | `n/a` |
| Private/PSS memory | `[ ]` | `[ ]` | `n/a` |
| Per-process I/O bytes | `[ ]` owner or helper | `[ ]` | `n/a` |
| Command line (other users) | `[ ]` | `[ ]` helper | `n/a` |
| Environment block | `[ ]` owner only | `[ ]` helper | `n/a` |
| Open files / handles | `[ ]` owner or helper | `[ ]` helper for full names | `n/a` |
| Modules / mappings | `[ ]` | `[ ]` | `n/a` |
| Threads | `[ ]` | `[ ]` | `n/a` |
| Sockets → PID | `[ ]` | `[ ]` | `n/a` |
| Container limits | `[ ]` cgroup v2 | `n/a` | `n/a` |
| Suspend / resume | `[ ]` SIGSTOP/SIGCONT | `[ ]` | `n/a` |
| Priority / affinity | `[ ]` | `[ ]` | `n/a` |
| Per-process packet capture | `[ ]` helper | `[ ]` helper | `n/a` |

---

## 6. What is shown and what can be done

### 6.1 Process columns

Every column is available in both front-ends; the TUI ships a default subset and lets the rest be
added. Sortable, reorderable, and persisted.

- [ ] Identity: name, PID, PPID, user, session/login, start time, state, command line, image path,
      working directory, container/cgroup
- [ ] CPU: CPU %, CPU time, kernel/user split, thread count, context switches/s, priority, nice,
      affinity mask
- [ ] Memory: private (PSS/private bytes), working set/RSS, shared, virtual size, swap, peak,
      page faults/s, memory limit + % of limit when containerized
- [ ] I/O: read bytes/s, write bytes/s, other/s, totals, open file-descriptor / handle count
- [ ] Network: connection count, listening ports (a per-process byte rate only with the helper, §8)
- [ ] Derived: CPU history sparkline in the row, "delta since I started watching" for any counter

### 6.2 Detail views (per process)

- [ ] Overview — the columns above laid out, plus a CPU and I/O plot
- [ ] Threads — TID, state, CPU %, CPU time, start address/name where the platform gives one
- [ ] Modules / mappings — path, base, size, permissions, backing file
- [ ] Handles / open files — type, name, access; files, sockets, pipes, and on Windows the kernel
      object types
- [ ] Environment — the block as key/value, copyable
- [ ] Network — endpoints with state, local/remote, and the resolved service name

### 6.3 System overview

- [ ] Per-core CPU history and an aggregate plot
- [ ] Memory and swap with the cache/buffer breakdown, and the same "limit" awareness as §6.1
- [ ] I/O and network throughput per device/interface
- [ ] Load average, uptime, context switches, interrupts, processes/threads running, PSI (Linux)

### 6.4 Actions

Every action is confirmed before it happens, names its target unambiguously (`name (pid)`), and is
refused with a reason rather than failing silently.

- [ ] End process · end process tree
- [ ] Suspend · resume
- [ ] Change priority · change CPU affinity
- [ ] Open the executable's folder · copy the command line · copy the row
- [ ] Send an arbitrary signal (Linux)
- [ ] Refuse, loudly, on PID 1 / `System` / the current process's own critical ancestors: the
      confirmation for these says what will break, and requires typing the process name.

### 6.5 Search

- [ ] One query, run across process names, command lines, open files/handles, mapped modules and
      listening ports; results grouped by process and selectable straight into the tree.
- [ ] Runs on a background thread against a snapshot, cancellable, and reports partial results as
      they arrive — the handle sweep on a busy Windows machine is not instantaneous.
- [ ] Available headless: `procman --find <pattern>` prints the same results for scripts.

---

## 7. Desktop UI (NativeForms)

### 7.1 Layout

- [ ] Main window: `MenuStrip` + `ToolStrip` (with the system plots) over a vertical
      `SplitContainer` — `TreeListView` of processes on top, `TabControl` of detail views below.
      `StatusStrip` reports process count, the sample cost (§3.5) and helper state.
- [ ] Process properties open in their own `Form`, several at once, each with the §6.2 tabs.
- [ ] Column chooser, persisted layout, per-column sort, tree/flat toggle, "show all users" toggle.
- [ ] Row coloring with the Process-Explorer conventions (new = green fade, exited = red fade, own
      user, system, suspended), plus a **color legend window** — a color no dialog explains is
      decoration.
- [ ] Dark mode follows the OS theme through `ITheme`; the plots read their colors from it rather
      than hard-coding.

### 7.2 Controls this project must build

NativeForms has no plotting controls, so they are ours, owner-drawn against `IGraphics` per its
[custom-control guide](https://github.com/Hawkynt/NativeForms/blob/main/docs/custom-controls.md):

- [ ] `HistoryPlot` — scrolling multi-series time plot with gap support (§3.3), fixed allocation
- [ ] `CoreMeterStrip` — one bar per logical core, the htop meter as a widget
- [ ] `Sparkline` — in-cell mini plot for the CPU column, drawn by the `TreeListView` cell painter
- [ ] `ColorLegend` — the legend window, generated from the same color table the rows use

### 7.3 Interaction rules

- [ ] Sorting and filtering never reorder rows *while the mouse is over them* — a re-sort that moves
      a row under the cursor between hover and click is how the wrong process gets killed. Re-sorts
      pause during a pointer interaction and apply on leave.
- [ ] Selection is by process identity `(pid, startTime)` (§3.2), not by row index. A refresh must
      not move the selection to whatever now occupies row 12.
- [ ] Every destructive action is keyboard-reachable and confirmable without the mouse.

---

## 8. Privilege model

### 8.1 Shape

- [ ] The UI process **never** runs elevated and never asks to. `procman-helper` is a separate
      executable, started on demand, exiting with its parent.
- [ ] Linux: launched through `pkexec` with a shipped polkit policy naming the actions; the policy
      file is part of the package and is reviewable.
- [ ] Windows: launched with `runas`/UAC via a manifested helper; `SeDebugPrivilege` is enabled in
      the helper only, and only while a request needs it.
- [ ] The helper is started **lazily** — the first time an action or column needs it — and the prompt
      says which operation asked for it.

### 8.2 Protocol

- [ ] A private pipe pair (`AF_UNIX` socketpair on Linux, an anonymous named pipe on Windows) handed
      to the child at spawn. No path in `/tmp`, no world-readable FIFO, no port.
- [ ] Length-prefixed binary frames with a fixed opcode set. The helper **parses**; it never
      evaluates, never accepts a command string, and never accepts a path it did not validate.
- [ ] Opcodes are the entire privileged surface, enumerated here and nowhere else: `ReadProcIo`,
      `ReadCmdline`, `ReadEnviron`, `ListFds`, `Terminate`, `Suspend`, `Resume`, `SetPriority`,
      `SetAffinity`, `StartCapture`, `StopCapture`. Adding one is a PRD change.
- [ ] Every request carries `(pid, startTime)` and the helper re-validates the pair before acting, so
      a PID reused between the click and the syscall is refused rather than acted on. This is the
      whole reason the identity key exists.
- [ ] The helper has no timers, no polling loop and no state beyond its open capture handles; it does
      strictly what it is asked, one request at a time.

### 8.3 Degradation

- [ ] Refused elevation is a normal outcome. The affected columns show `NotPermitted` (§3.4), the
      affected actions grey out with the reason in the tooltip, and nothing retries the prompt in a
      loop.
- [ ] A dead or crashed helper is noticed at the next request and reported once; the program keeps
      running unprivileged.

---

## 9. Testing strategy

The whole point of a probe that returns raw counters (§2) is that almost everything can be tested
without the OS under it.

- [ ] **9.1 Fixture replay.** Recorded `/proc` trees checked into the repo as directories; the Linux
      probe takes its root as a parameter and reads the fixture. Real machines, captured once:
      a desktop, a container, a machine with 2000 processes, and a `/proc` with the pathological
      `comm` names of §5.1.
- [ ] **9.2 Golden snapshots.** Fixture in, `SystemSnapshot` out, compared field by field against a
      checked-in expectation. A parse regression is then a diff, not a debugging session.
- [ ] **9.3 The nasty cases, each with its own test:** PID reuse across a sample · counter wraparound
      · a wall-clock jump backwards · a process exiting mid-sample (files vanish between two reads)
      · a process whose `comm` contains `)` and spaces · zero elapsed time between samples · a
      first sample with no predecessor · a 96-core `/proc/stat`.
- [ ] **9.4 Windows structure replay.** The `SYSTEM_PROCESS_INFORMATION` buffer captured to a blob and
      replayed through the parser, so the Windows parsing path is testable on the Linux CI leg too.
- [ ] **9.5 Trim/AOT gate.** A NativeAOT publish per RID with `TreatWarningsAsErrors=true`; any
      IL2xxx/IL3xxx fails the build.
- [ ] **9.6 Front-end smoke.** The desktop UI started headless against a fixture probe under Xvfb and
      photographed; the TUI rendered against the same fixture to a captured buffer and compared to a
      golden frame. Both run in CI, both are gates.
- [ ] **9.7 Budget harness.** §4 asserted by `ProcessManager.Benchmarks` in nightly CI; a regression
      exits non-zero.
- [ ] **9.8 Helper protocol tests.** Malformed frames, oversized lengths, unknown opcodes, a PID/start
      mismatch and a truncated stream — each must be refused without the helper acting or crashing.

---

## 10. Milestones

| # | Milestone | Contents | Done when |
|---|---|---|---|
| **M0** | Skeleton | Solution, projects, `Directory.Build.*`, CI green on an empty test | CI passes on all three legs |
| **M1** | Core model | §3 snapshot/delta/rate/history, no probe | §9.3 cases pass against synthetic snapshots |
| **M2** | Linux probe | §5.1 main loop fields, fixture replay | §9.1/§9.2 green; `--list --json` works |
| **M3** | TUI v1 | Process list, sort, tree, kill, per-core meters | §9.6 golden frame; usable over SSH |
| **M4** | Desktop v1 | §7.1 layout, §7.2 plot controls, process properties | §9.6 screenshot leg is a gate |
| **M5** | Windows probe | §5.2 including the `NtQueryObject` timeout worker | §9.4 replay green; parity table in §5.4 filled |
| **M6** | Details & search | §6.2 views, §6.5 search in both front-ends | Search finds a known holder of a known file on both OSes |
| **M7** | Privilege helper | §8 end to end, both platforms | §9.8 green; refused elevation degrades per §8.3 |
| **M8** | Polish & budget | §4 met and enforced, dark mode, persisted layout | Nightly budget harness green for a week |
| **M9** | macOS | Replace the §5.3 stub with a real probe | The macOS CI leg becomes a gate instead of a probe |

---

## 11. Terminal UI

Shipped in v1, not a later addition — the same engine with a different renderer.

- [ ] Full-screen alternate-screen renderer over ANSI/VT, with `termios` raw mode on Linux and
      `ENABLE_VIRTUAL_TERMINAL_PROCESSING` on Windows, restored on exit **and on crash**.
- [ ] Diff-based redraw: only changed cells are written. A monitor that repaints 200 rows per second
      over SSH is its own denial of service.
- [ ] htop-compatible keys where htop has one: `F5` tree · `F6` sort · `F9` kill · `F10`/`q` quit ·
      `/` search · `\` filter · `u` user filter · `H` threads · `t` tree · space tag · `<`/`>` sort
      column. Keys we add do not shadow keys htop uses for something else.
- [ ] Per-core meters, memory/swap bars and load average in the header, drawn by the same math the
      desktop plots use.
- [ ] Degrades by capability, not by guess: color depth from `TERM`/`COLORTERM`, box characters only
      when the locale is UTF-8, and a monochrome ASCII mode that is actually tested (§9.6).
- [ ] Resizes correctly on `SIGWINCH` / console resize events, including to sizes smaller than the
      header.
- [ ] Non-interactive modes for scripts: `--list`, `--list --json`, `--find`, `--kill`, each with a
      stable output contract and a documented exit code.

---

## 12. Coverage matrix

A feature is finished when every column is ticked. Rows are added as features land; the table starts
empty on purpose rather than pre-filled with unticked rows nobody will maintain.

| Feature | Implemented | Tested | Desktop | TUI | Documented |
|---|---|---|---|---|---|
| *(none yet — M0)* | | | | | |

---

## 13. Open questions

Each of these must be answered before the milestone that depends on it, and the answer is recorded
here rather than in a commit message.

1. **CPU% convention as the default** (§3.2) — normalized-to-100 like Task Manager, or per-core like
   htop and top? Both are implemented; the question is which one a first-run user sees. *Blocks M3.*
2. **Handle-name resolution cost on Windows** (§5.2) — is the timeout worker fast enough to make the
   handle sweep interactive on a machine with 300 000 handles, or does the search need a persistent
   index? Measure at M5, decide at M6.
3. **Per-process network rates without a driver** — `/proc/net` + inode matching gives connections but
   not byte rates. Options are a capture through the helper (accurate, heavy, needs root) or nothing.
   *Blocks the network column in §6.1, not M6 as a whole.*
4. **Container detection depth** (§5.1) — cgroup v2 limits are in scope; naming the container by
   asking a runtime (Docker/podman socket) is a dependency this program does not otherwise have.
   Default assumption: no runtime sockets, limits only.
5. **Settings storage** — a config file location per platform, or nothing persisted in v1 beyond
   column layout? *Blocks M8.*
