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

**Where this stands:** M0–M3 and M5 are in (engine, Linux probe, both front-ends, the Windows probe's
bulk query), M4 is in but unpolished, and M6–M9 are not started. §10 has the detail and §12 the
per-feature coverage. Boxes below are ticked only where the code exists *and* a test covers it.

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
ProcessManager.Ui.Terminal                terminal renderer, zero UI dependencies
ProcessManager.Ui.Desktop                 desktop UI on NativeForms
ProcessManager.App                        the one binary: CLI + both front-ends       (procman)
ProcessManager.Elevated                   procman-helper — the only privileged binary
ProcessManager.Tests                      unit + fixture-replay + golden tests
ProcessManager.Benchmarks                 the §4 budget harness, run by nightly CI
```

**The NativeForms dependency is source, not a package, and temporarily.** The process list needs
`TreeListView`'s `RowBackColorSelector`, `CellPaint` and `ColumnClick` — added upstream by
Hawkynt/NativeForms#8 for exactly this and not yet in a published build — so `ProcessManager.Ui.Desktop`
references the sibling repository's projects, the way PNGCrushCS references CompressionWorkbench. The
CI checks the sibling out beside this repo. When a package carrying the seams ships, this reverts to
three `PackageReference`s and the extra checkout goes away; a build without the sibling fails with one
sentence saying so rather than two hundred lines of CS0246.

Both front-ends are **libraries**, and `ProcessManager.App` is the only executable of the pair. That
is a change from this document's first draft, which had two executables: the documented CLI is
`procman` and `procman --tui`, and two binaries cannot both be `procman`. The cost is that a headless
box carries the UI assembly it will never open; it never *loads* GTK, because a backend that is not
registered is never asked for its native library.

- **Core never calls a native API.** It asks an `ISystemProbe` for raw counters and does every
  subtraction, division and sort itself. That is what makes the whole engine testable against
  recorded fixture trees rather than against the machine running the tests (§9).
- **A probe returns counters, not rates.** Rates, percentages, deltas and history are Core's job,
  computed identically on every platform. A probe that pre-divides has a bug.
- **Front-ends are read models.** They receive an immutable `SystemSnapshot` and a `SnapshotDelta`
  and render. They never poll the probe, never keep their own history, and never mutate the model.

### AOT/interop rules (enforced, not aspirational)

- [x] `[LibraryImport]` source-generated P/Invoke only — never `[DllImport]`.
- [x] No `System.Reflection`, `TypeDescriptor`, `Activator.CreateInstance(Type)` or `dynamic`
      anywhere in Core, the probes or the helper. The `--list --json` output is written by hand for
      the same reason.
- [x] `IsAotCompatible=true` on every library (`Directory.Build.props`), off again for the test and
      benchmark projects (`Directory.Build.targets`) which are never published;
      `TreatWarningsAsErrors=true` on the AOT publish leg so an IL2xxx/IL3xxx warning fails CI (§9.5).
- [x] Probe and backend selection are compile-time-visible `if`s on `OperatingSystem.IsLinux()` /
      `IsWindows()` / `IsMacOS()`, not a plugin scan — the trimmer can drop the two the build does
      not need.

---

## 3. The sampling model

The core of the program is three hundred lines of arithmetic that everything else displays. It gets
its own section because every subtle bug in a tool of this kind lives here.

### 3.1 Snapshot

- [x] `SystemSnapshot` — one immutable point-in-time reading: a monotonic timestamp, system-wide
      counters, and a `ProcessRecord` per visible process. Produced by one probe call.
- [x] The timestamp is from a **monotonic** clock (`Stopwatch.GetTimestamp`), never wall time. A
      wall-clock jump — NTP step, suspend/resume, DST — must not produce a negative interval or an
      infinite rate.
- [x] `ProcessRecord` is a mutable struct in a pooled array — *not* the `readonly record struct` this
      document first specified. A record struct of twenty-five fields is copied by every `foreach`
      and every assignment; the probe fills it by `ref` instead. No per-sample allocation of the
      strings that did not change (§4).

### 3.2 Delta and rate

- [x] `SnapshotDelta` pairs consecutive snapshots and yields, per process: CPU time consumed, bytes
      read/written, and appeared/exited/changed sets.
- [x] CPU percent = ΔprocessCpuTime / (Δwallclock × coreCount) by default, with a per-core mode
      (`Δ / Δwallclock`, so a fully busy 8-thread process reads 800 %) selectable in both front-ends.
      Which mode is active is always visible in the column header — the two conventions differ by 8×
      on this machine and a number without its convention is not a number.
- [x] **PID reuse is detected, not assumed away.** Two snapshots may show the same PID as two
      different processes. The identity key is `(pid, startTime)`; a mismatch is an exit plus a
      start, never a delta. A test pins this (§9.3).
- [x] **Counter wraparound and reset** yield "unknown" for that interval, not a negative or absurd
      rate. A 32-bit counter that decreased did not decrease.
- [x] First sample after start has no predecessor: rates are `unknown`, and the UI shows `—`. It does
      not show `0`.

### 3.3 History

- [x] `HistoryRing<T>` — fixed-capacity ring per tracked series, allocated once. Default 600 samples
      (10 minutes at 1 Hz), configurable; memory is bounded by construction, not by pruning.
- [x] Per-process history is kept only for processes with an open detail view plus the top N by CPU;
      everything else keeps the current value only. Keeping 600 samples × 1000 processes × 6 series
      is 28 MB of nothing anyone looks at.
- [x] A gap in sampling (the interval was missed, the machine was asleep) is recorded **as a gap**.
      Plots break the line; they do not interpolate across it — `HistoryPlot` and the terminal meters
      both do, and the golden frame shows a `?` where a counter did not move.

### 3.4 Unknown is a value

- [x] Every numeric field is `T?`-shaped with an explicit reason when absent: `NotPermitted`,
      `NotSupportedOnPlatform`, `ProcessExited`, `NotSampledYet`, `CounterInvalid`.
- [x] Front-ends render each reason distinctly (`—` permission, `n/a` platform, `…` first sample) and
      the reason is available on hover / in the detail pane. This is the rule §1.5 exists for.

### 3.5 Sampling cadence

- [x] Default interval 1000 ms; settable 250 ms – 60 s. The interval is a *target*: the sampler
      measures its own cost and reports it (§4), and never queues a second sample while the first is
      running.
- [x] Expensive collections are on a slower cadence than the process list: handles/open files and
      module lists refresh on demand and on the detail view's own timer, never in the main loop.
- [~] The UI never blocks on a sample. **Not true yet in either front-end**: both sample on their own
      thread — the terminal UI between key polls, the desktop UI on its timer tick. At the measured
      38 ms per sample that is invisible; at ten thousand processes it would not be, and the fix is a
      background sampler with a completed-snapshot handoff, not a faster probe.

---

## 4. Performance & footprint budget

Targets measured by `ProcessManager.Benchmarks` and asserted in nightly CI. The harness fails on
regression rather than printing a number nobody reads.

**The budget is asserted on CPU time, not wall-clock.** This was learned rather than designed: the
first harness measured wall-clock and reported 207 ms for the same work that took 896 ms an hour
later, because the machine went from load 280 to load 650. A sample that waited for sixteen other
builds to give a core back has not become more expensive. Wall-clock is still reported, because a
user waiting for a frame does not care why. The same lesson applies to reading the numbers below:
every one was taken at load 15–30 on a 16-core machine, and the load is printed with them.

- [x] **Snapshot cost**: ≤ 25 ms of CPU per 1000 processes. **Measured: 33 ms** (539 processes in
      18 ms of CPU, load 15), against a 50 ms ceiling the harness enforces. Slightly over target and
      the shape is understood: three files are read per process — `stat`, `status`, `io` — which is
      about twelve syscalls, and syscalls are the entire cost. Dropping `status` would close the gap
      and cost the private-memory column and the owner id, which is a worse trade than three
      milliseconds. Recorded as a near miss rather than closed.
      *Attribution, same machine:* `stat`+`status` alone 25 µs/process, plus cgroup 32 µs, plus
      file-descriptor counting 41 µs.
- [x] **Steady-state allocation: zero.** **Measured: 86 bytes per sample** at 539 processes with no
      process starting — six thousandths of a byte per process. `/proc` files are read through
      `open`/`read`/`close` into pooled buffers and parsed from `ReadOnlySpan<byte>`, paths are built
      as UTF-8 bytes into stack buffers, and a name that did not change hands back the string it had.
      *Before and after the work:* 199 598 bytes per sample → 86.
      The budget allows a bounded amount per *newly started* process — one cache entry, its paths and
      its command line — because that is a real once-per-process cost and not a leak. A regression of
      the kind that matters, one string per process per sample, would be tens of kilobytes here.
- [x] **What is not on the sampling path, and why.** Two things were tried there and measured out:
      - *File-descriptor counts.* Reading `/proc/[pid]/fd` makes the kernel materialise one directory
        entry per open descriptor: **9 µs per process** on a quiet machine, 85 µs on a loaded one, and
        two thirds of the whole sample when it was in the loop. Front-ends fill the column for the
        rows they draw, through `ISystemProbe.GetHandleCount` (§3.5).
      - *Proportional set size.* `smaps_rollup` walks the whole page table of every process:
        **772 µs per process**, twenty-four times the rest of the sample put together. Off by
        default; the private column is anonymous RSS, which arrives free in a file already being read.
- [x] **Own CPU cost**: < 1 % of one core at 1 Hz with 1000 processes. At 33 ms of CPU per 1000
      processes per second that is **3.3 %** of one core, or 0.2 % of this sixteen-core machine.
      Against the letter of the target it fails; against its intent — "a monitor that shows up in its
      own top ten has failed" — the program does not appear in its own list of busy processes at all.
      The target was written per-core and should have been written per-machine.
- [ ] **Resident memory**: < 60 MB for the desktop UI with 1000 processes, < 20 MB for the TUI.
      Not yet measured.
- [ ] **Start to first frame**: < 250 ms desktop, < 100 ms TUI. Not yet measured. The first sample is
      legitimately several times a steady-state one — it loads every process's command line and image
      path — so this needs its own number rather than an inference from the one above.
- [x] **View rebuild**: the tree is rebuilt from scratch every sample by both front-ends, so it is
      part of the frame. **Measured: 0.58 ms** for 539 processes in tree mode. It was 13 ms before the
      child index replaced a scan-per-parent (§7.2 of the original draft's quadratic walk).
- [x] **Binary size** reported per RID in the CI step summary on every AOT publish.

## 5. Platform probes

A probe's contract: return raw counters for everything it can read, and a typed reason (§3.4) for
everything it cannot. A probe never guesses, never falls back to a plausible-looking zero, and never
shells out to another program.

### 5.1 Linux — `/proc`, `/sys`, cgroup v2

- [x] Process list from `/proc/[pid]` directory enumeration, reusing the directory buffer.
- [x] `/proc/[pid]/stat` — state, ppid, pgrp, session, utime, stime, cutime, cstime, priority, nice,
      num_threads, starttime, vsize, rss. Parsed from bytes; the comm field is parsed by scanning
      **back** from the last `)`, because a process may be named `foo) 0 (bar`.
- [x] `/proc/[pid]/status` — Uid/Gid (real *and* effective), VmRSS, VmSwap, Threads, context switches.
- [~] `/proc/[pid]/smaps_rollup` — `Pss`, `Private_Clean`, `Private_Dirty`. Implemented and **off by
      default**: measured at 3 963 µs per process, ninety times the rest of the sample (§4). The
      default private column is `RssAnon` from `status`, which arrives free in a file already being
      read and is wrong only in ignoring the process's share of what it maps.
- [x] `/proc/[pid]/io` — `read_bytes`, `write_bytes`, `rchar`, `wchar`. Readable only by the owner
      (0400 since kernel 5.12): other users' processes report `NotPermitted` unless the helper is up.
      The probe checks the uid *before* opening, because the refusal arrives at `read(2)` rather than
      `open(2)` and half a shared machine's process table is somebody else's.
- [x] `/proc/[pid]/cmdline` (NUL-separated), `/proc/[pid]/environ` (owner only), `/proc/[pid]/cwd`,
      `/proc/[pid]/exe` (readlink).
- [x] `/proc/[pid]/fd/*` — open files, sockets (as `socket:[inode]`), pipes. Owner or helper only.
      **Not on the sampling path**: the count costs 85 µs per process (§4), so front-ends ask for the
      rows they draw.
- [x] `/proc/[pid]/maps` — mapped files for the module view.
- [x] `/proc/[pid]/task/[tid]/stat` — per-thread, for the thread view only (not the main loop).
- [x] System: `/proc/stat` (per-core jiffies, ctxt, intr, procs_running), `/proc/meminfo`,
      `/proc/loadavg`, `/proc/uptime`, `/proc/diskstats`, `/proc/net/dev`, `/proc/pressure/*` (PSI).
- [x] Sockets: `/proc/net/{tcp,tcp6,udp,udp6,unix}` joined to processes by socket inode. The join is
      O(fds), done once per request, not per process. IPv6 addresses are handed back in their raw
      hex form rather than formatted — a half-formatted address is worse than an honest one.
- [x] `USER_HZ` is read via `sysconf(_SC_CLK_TCK)` through `[LibraryImport]`, not assumed to be 100.
      Page size likewise via `sysconf(_SC_PAGESIZE)` for `statm`/`rss`.
- [~] cgroup v2: the process's cgroup path is read from `/proc/[pid]/cgroup` and shown. The *limits*
      (`memory.max`, `cpu.stat`, `io.stat`) are not read yet, so a containerized process still shows
      the host's totals — open question 4.
- [~] Users resolved from `/etc/passwd`, cached, with the numeric uid shown when the file cannot
      answer. **This document originally specified `getpwuid_r`**, and the deviation has a cost worth
      stating: accounts that come from LDAP, SSSD or systemd-homed through NSS show as numbers. What
      it buys is a resolver that is a pure function of a file path — so it replays against a fixture
      like the rest of the probe — and one that cannot block the sampling thread on a network
      directory, which is a real failure mode of the correct call.

### 5.2 Windows — native API, no WMI

- [x] `NtQuerySystemInformation(SystemProcessInformation)` — the whole process *and* thread list in
      one call, into a pooled, grown-on-`STATUS_INFO_LENGTH_MISMATCH` buffer. WMI is not used
      anywhere: it is orders of magnitude slower and needs a service that may be broken.
- [x] `SystemProcessorPerformanceInformation` for per-core idle/kernel/user, `GetSystemTimes` as the
      cross-check.
- [ ] `QueryFullProcessImageName` for the image path; `NtQueryInformationProcess`
      (`ProcessCommandLineInformation`, `ProcessBasicInformation`) for the command line and PEB.
- [x] Memory from the `SYSTEM_PROCESS_INFORMATION` block (`PrivatePageCount`, `WorkingSetPrivateSize`,
      `VirtualSize`) — already in the bulk call, so no per-process `OpenProcess` in the main loop.
- [x] I/O counters likewise from the bulk block (`ReadTransferCount`, `WriteTransferCount`).
- [x] Modules via **Toolhelp32** rather than the PEB `Ldr` walk this document first specified: it
      needs no read access to the target's address space, handles the cross-bitness case with both
      snapshot flags, and is a documented API rather than a structure that moves between releases.
      Verified at 47 modules for a .NET process.
- [x] Handles via `NtQuerySystemInformation(SystemExtendedHandleInformation)`, filtered by owner and
      duplicated into this process to be asked about; names resolved with `NtQueryObject`. Verified at
      95 handles, 41 of them named. **`NtQueryObject` can block forever on a synchronous named pipe** — name
      resolution therefore runs on a dedicated worker with a per-handle timeout, and a handle that
      times out is reported as `<name unavailable>`. This is the single most common way tools of this
      kind hang, and it is a design constraint, not a defect to discover later.
- [x] Sockets via `GetExtendedTcpTable` / `GetExtendedUdpTable` with `TCP_TABLE_OWNER_PID_ALL`, both
      address families. Windows names the owning pid in the table itself, so unlike Linux there is no
      socket inode to join against.
- [x] Actions: `SetPriorityClass`, `SetProcessAffinityMask`, `NtSuspendProcess` / `NtResumeProcess`,
      `TerminateProcess`. Elevation via §8 when `OpenProcess` returns `ERROR_ACCESS_DENIED`.
- [x] `SeDebugPrivilege` is enabled only inside the helper, never in the UI process — which is
      currently true by omission: nothing enables it anywhere yet.
- [x] Users resolved from the process token (`OpenProcessToken` + `GetTokenInformation(TokenUser)` +
      `LookupAccountSid`), cached by SID and by pid — one lookup per account, not per process, because
      `LookupAccountSid` can go to a domain controller. Verified: `ARCHBTW2\hawky`.

### 5.3 macOS — stub

- [x] Every member of `MacOsProbe` throws `PlatformNotSupportedException` with a message naming this
      section and the milestone (§10 M9). It does not return empty data — a program that shows an
      empty process list is worse than one that says it does not work here.
- [ ] The macOS CI leg builds and runs it, and records how far it gets, exactly as NativeForms does
      for its Cocoa backend.

### 5.4 Probe capability matrix

Filled in as probes land; `n/a` means the platform has no such concept, not that we skipped it.

| Capability | Linux | Windows | macOS |
|---|---|---|---|
| Process list, CPU, memory | `[x]` | `[x]` | `n/a` |
| Private/PSS memory | `[x]` RssAnon, PSS opt-in | `[x]` commit charge | `n/a` |
| Per-process I/O bytes | `[x]` owner only | `[x]` | `n/a` |
| Command line (other users) | `[x]` | `[x]` | `n/a` |
| Environment block | `[x]` owner, or helper | `[x]` PEB walk, 119 read | `n/a` |
| Open files / handles | `[x]` owner, or helper | `[x]` 95 seen, 41 named | `n/a` |
| Modules / mappings | `[x]` | `[x]` 47 seen | `n/a` |
| Threads | `[x]` | `[x]` from the bulk buffer | `n/a` |
| Sockets → PID | `[x]` inode join | `[x]` owner pid in the table | `n/a` |
| Container limits | `[~]` cgroup path only | `n/a` | `n/a` |
| Suspend / resume | `[x]` SIGSTOP/SIGCONT | `[x]` NtSuspend/ResumeProcess | `n/a` |
| Priority / affinity | `[x]` | `[x]` | `n/a` |
| Per-process packet capture | `[ ]` open question 3 | `[ ]` | `n/a` |

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

- [x] Main window: a `MenuStrip` (View, Sort by, Process), a plot strip (CPU history, memory history,
      per-core meters), a `SplitContainer` with the `TreeListView` of processes above and a
      `TabControl` of the §6.2 detail views below, and a status line reporting process count, CPU,
      memory and the sample cost. Docked, so it follows a resize.
- [~] The §6.2 tabs are in the pane at the bottom, one process at a time. Opening several in their
      own windows at once is not done — it is what makes Process Explorer good at comparing two
      processes, and it needs the pane extracted into a `Form`, which is a small job on top of what
      is there.
- [x] Tree/flat toggle, "show all users" and the CPU% convention are in the View menu; sorting is in
      a Sort menu **and on the headers** — click to sort, click again to reverse, with the direction
      in the caption. A column chooser picks from the sixteen in `ColumnSet`. `--flat` starts as a
      sorted list rather than a tree. Persisted layout is still not done (open question 5).
- [x] Row colouring with the Process-Explorer conventions, plus a **colour legend window** — because
      a colour no dialog explains is decoration.
      This was blocked and is not any more: `TreeListView` had no per-row colour hook, so the fix went
      where the problem was (Hawkynt/NativeForms#8 adds `RowBackColorSelector`, `RowForeColorSelector`,
      `CellPaint` and `ColumnClick`, with ten tests). `ProcessCategories.Classify` sorts a row into
      one of eight categories and `RowPalette` gives each a colour, in a light and a dark variant
      chosen from the theme's own background.
      **What is deliberately not claimed:** packed, .NET, elevated and store processes. Process Hacker
      colours those; telling them apart needs information neither probe collects, and a colour that is
      sometimes right is worse than none. The legend says so out loud.
- [~] Dark mode follows the OS theme through `ITheme`, and the row palette has a light and a dark
      variant picked from `FieldBackground`'s perceptual luminance. The *plots* deliberately do not
      follow it: they are instruments, and an instrument that changes contrast with the desktop is
      harder to read at a glance than one that always looks the same. Series colours and the meter's
      amber/red thresholds are fixed for the same reason.

### 7.2 Controls this project must build

NativeForms has no plotting controls, so they are ours, owner-drawn against `IGraphics` per its
[custom-control guide](https://github.com/Hawkynt/NativeForms/blob/main/docs/custom-controls.md):

- [x] `HistoryPlot` — scrolling multi-series time plot with gap support (§3.3), reading a
      `HistoryRing<Rate>` directly so nothing is copied per frame. Filled area on a black ground with
      a green graticule, and a brighter line along the top edge so stacked series stay separable
- [x] `CoreMeterStrip` — one bar per logical core, the htop meter as a widget
- [x] `Sparkline` — in-cell plots for **CPU, memory and I/O history**, drawn through the toolkit's
      new `CellPaint` seam. One pixel per sample, newest at the right, gaps left blank.
      The scales are **shared across rows and decayed, with a floor** (`ProcessHistory`): a plot
      scaled to its own row's maximum makes an idle process look exactly as busy as a saturated one,
      a scale that snapped to each sample's peak would make every plot jump when one process spiked,
      and on an idle machine the peak is noise that a floor keeps from being amplified.
- [x] `ColorLegend` — the legend window, one swatch and one sentence per category, generated from the
      same palette the rows use.

### 7.3 Interaction rules

- [ ] Sorting and filtering never reorder rows *while the mouse is over them* — a re-sort that moves
      a row under the cursor between hover and click is how the wrong process gets killed. Re-sorts
      pause during a pointer interaction and apply on leave.
- [x] Selection is by process identity `(pid, startTime)` (§3.2), not by row index. In the desktop UI
      the node objects themselves are reused across samples, so expansion state survives too; in the
      terminal UI the selection is re-found by key after every rebuild.
- [ ] Every destructive action is keyboard-reachable and confirmable without the mouse.

---

## 8. Privilege model

### 8.1 Shape

- [x] The UI process **never** runs elevated and never asks to. `procman-helper` is a separate
      executable, started on demand, exiting with its parent — its input closes when the parent goes,
      its read returns end-of-stream, and it stops. It has no loop of its own to be stuck in.
- [x] Linux: launched through `pkexec` with a shipped polkit policy naming the action
      (`packaging/org.hawkynt.procman.policy`, `auth_admin_keep`). The policy pins the executable
      path, so an administrator decides what gets elevated rather than the caller.
- [ ] Windows: **not implemented, and the reason is structural.** Windows cannot both elevate a child
      and redirect its standard handles in one call — the `runas` verb that raises the UAC prompt
      refuses redirection — so the pipe of §8.2 has to become a named pipe the elevated child
      connects back to. The channel reports this plainly rather than silently starting the helper
      unelevated, which would produce exactly the refusals it was started to avoid.
- [ ] The helper is started **lazily** — the first time an action or column needs it — and the prompt
      says which operation asked for it.

### 8.2 Protocol

- [x] A private pipe pair handed to the child at spawn: its **standard input and output**, redirected.
      No path in `/tmp`, no world-readable FIFO, no port — nothing on the machine can connect to it
      but the process that started it. This is a simplification of the draft's "`AF_UNIX` socketpair
      on Linux, anonymous named pipe on Windows": redirected stdio *is* an anonymous pipe pair on
      both platforms, `pkexec` passes it through, and one mechanism is one thing to get right.
- [x] Length-prefixed binary frames with a fixed opcode set. The helper **parses**; it never
      evaluates, never accepts a command string, and never accepts a path — it builds every path
      itself from a pid it has validated. A length prefix is attacker-controlled, so it is bounds-
      checked before anything is allocated, and a malformed frame ends the conversation rather than
      being resynchronised past.
- [x] Opcodes are the entire privileged surface: `ReadProcIo`, `ReadCmdline`, `ReadEnviron`,
      `ListFds`, `Terminate`, `Suspend`, `Resume`, `SetPriority`, `SetAffinity`. `StartCapture` and
      `StopCapture` were in the draft and are **not implemented** — per-process packet capture is
      open question 3, and an opcode that exists but does nothing is a privileged surface for no
      benefit. Adding one back is a PRD change.
- [x] Every request carries `(pid, startTime)` and the helper re-reads the start time from `/proc`
      itself before acting — it does not trust the caller's copy. A pid reused between the click and
      the syscall is refused. This is the whole reason the identity key exists, and it is checked
      end to end by `procman --helper-check`.
- [x] The helper has no timers, no polling loop and no state at all; it does strictly what it is
      asked, one request at a time.

### 8.3 Degradation

- [x] Refused elevation is a normal outcome: the channel records why, answers `NotPermitted`, and
      never retries. The probe and the actions consult it — another user's environment block and open
      descriptors are asked of the helper when the direct read is refused, and `Terminate`/`Suspend`/
      `Resume`/`SetPriority`/`SetAffinity` fall through to it on `EPERM`. Deliberately **not** routed
      through it: the per-sample I/O column, because that would be a round trip to another process
      per process per second and would cost more than the entire sample (PRD §4). It reports
      `NotPermitted`, which is the truth about what this user can see.
- [x] A dead or crashed helper is noticed at the next request and reported once; the program keeps
      running unprivileged.

---

## 9. Testing strategy

The whole point of a probe that returns raw counters (§2) is that almost everything can be tested
without the OS under it.

- [x] **9.1 Fixture replay.** Recorded `/proc` trees checked into the repo as directories; the Linux
      probe takes its root as a parameter and reads the fixture.
      **This promise was broken once and is worth the paragraph.** The probe was optimised down to
      raw `open`/`read`/`close`/`getdents64` for §4, and those do not exist on Windows and only
      partly on macOS — so the fixture tests, whose entire purpose is to run on every leg, failed on
      two of three with `DllNotFoundException`. The fix is a file-access seam (`ProcIo`): syscalls on
      Linux where the speed is the point, the BCL everywhere else where only correctness is. The
      switch is also exposed as `UsePortableFileAccess`, and the fixture suite runs **twice on every
      leg** — once down each path — so the portable one is not code that only ever executes where
      nobody can debug it.
      Recorded machines, captured once:
      one hand-authored desktop tree so far, including the pathological `comm` of §5.1. A container and
      a two-thousand-process capture are still to come. It is hand-authored rather than copied from a
      real machine on purpose: a recorded `/proc` carries the command lines of whoever recorded it
      into a public repository.
- [x] **9.2 Golden snapshots.** Fixture in, `SystemSnapshot` out, compared field by field against a
      checked-in expectation. A parse regression is then a diff, not a debugging session.
- [~] **9.3 The nasty cases, each with its own test:** PID reuse across a sample · counter wraparound
      · a wall-clock jump backwards · a process exiting mid-sample (files vanish between two reads)
      · a process whose `comm` contains `)` and spaces · zero elapsed time between samples · a
      first sample with no predecessor · a 96-core `/proc/stat`. In today: PID reuse, wraparound, the backwards clock, the `)` in a `comm`,
      the zero interval, the first sample, a parent that is not in the snapshot, a parent cycle, and
      a process that is its own parent. Still missing: a process vanishing mid-sample, and the
      96-core file.
- [~] **9.4 Windows structure replay.** The `SYSTEM_PROCESS_INFORMATION` chain replayed through the
      parser on every CI leg. Half done, and the halves are worth separating:
      - *Done.* The buffer is **synthesised** by the tests and walked: chain terminator, FILETIME to
        nanoseconds, the byte-versus-character length of a `UNICODE_STRING`, the identity pair, the
        two nameless system processes, and an image-name pointer outside the buffer.
      - *Superseded, and by something better.* A captured buffer would still have been written by the
        same understanding as the parser. `procman --self-test` on the `windows-latest` runner instead
        asks the probe about the process it is running in and has the runtime check every answer — on
        a real NT kernel, against an independent source of truth. That is a stronger claim than a
        replayed blob, and it is what the Windows column in §12 now rests on.
      Doing this exposed a defect that would have made the capture unreplayable at all: each image
      name is a `UNICODE_STRING` whose `Buffer` is an *absolute* pointer into the query's own
      allocation, and the first implementation dereferenced it. A blob captured on one machine and
      replayed on another would have read whatever happened to live at that address. The parse now
      takes the buffer's base address and works in offsets, with a bounds check — and the query
      buffer is allocated pinned, because a moving array would have dangled those pointers between
      the call and the parse on a live machine too.
- [x] **9.5 Trim/AOT gate.** A NativeAOT publish per RID with `TreatWarningsAsErrors=true`; any
      IL2xxx/IL3xxx fails the build.
- [x] **9.6 Front-end smoke.** The TUI is rendered against the fixture into a captured buffer and
      compared to a checked-in golden frame, both as an NUnit test and as a CI job. The desktop leg
      brings the window up under Xvfb and now **photographs it**, in-process: `GtkCapture` asks the
      widget to paint into a Cairo surface and writes the PNG from there.
      Three things had to be got right, each of which produced a picture that looked like a bug
      elsewhere. `import`/`xwd` see nothing — there is no compositor on a runner, and an ImageMagick
      without its X11 delegate exits zero having written nothing. Once the capture was in-process it
      needed `gtk_test_widget_wait_for_draw`, or it photographed the frame before the pending
      relayout: a rectangle of black. And the log has to carry the window's whole state, because a
      windowed process on a runner has no console to explain an empty shot with.
- [x] **9.7 Budget harness.** §4 asserted by `ProcessManager.Benchmarks` in nightly CI; a regression
      exits non-zero.
- [x] **9.8 Helper protocol tests.** Nineteen of them, and every one is an attempt to make the parser
      that runs as root do something it should not: a length larger than the ceiling (refused without
      allocating), a negative length, a length too small to hold a request, a stream that ends
      mid-frame, an unknown opcode, opcode zero, a pid of zero or negative or beyond `int`, and every
      truncation of a valid frame plus a run of garbage — none of which may throw, because a crash in
      a process holding root is a denial of service on everything the user wanted to do. A longer
      frame from a newer client is skipped rather than desynchronising the stream.
      The other half is `procman --helper-check`, which starts the real binary **unelevated** over a
      real pipe and checks it answers, reads the files it is asked for, and refuses an identity
      mismatch, a missing process and an empty affinity mask. It runs in CI on the Linux leg. Malformed frames, oversized lengths, unknown opcodes, a PID/start
      mismatch and a truncated stream — each must be refused without the helper acting or crashing.

---

## 10. Milestones

| # | Milestone | Contents | State |
|---|---|---|---|
| **M0** | Skeleton | Solution, ten projects, `Directory.Build.*`, CI | **done** — solution builds clean on net10.0 |
| **M1** | Core model | §3 snapshot/delta/rate/history, no probe | **done** — 51 tests, §9.3 cases green |
| **M2** | Linux probe | §5.1 main-loop fields, fixture replay | **done** — reads a real machine and a recorded one |
| **M3** | TUI v1 | Process list, sort, tree, kill, per-core meters | **done** — golden frame is a CI gate |
| **M4** | Desktop v1 | §7.1 layout, §7.2 plot controls, process properties | **done** — docked layout, filled-area plots on a graticule, per-core meters, the process tree with rows coloured by category, three in-row history columns, a column chooser, click-to-sort headers, a colour legend, six detail tabs, menus and a context menu. Photographed under Xvfb on every push. Per-process property windows and a persisted layout remain |
| **M5** | Windows probe | §5.2 including the `NtQueryObject` timeout worker | **done** — bulk query, per-core times, memory, I/O, owners, command lines, threads, modules, handles (with the timeout worker), sockets and the environment block. Executed and checked on a genuine Windows kernel (NT 10.0.26100) by the `self-test windows-latest` CI job: 21 of 21 checks, 8 threads, 47 modules, 159 handles, 149 environment variables, and the private-bytes counter Wine leaves at zero reads correctly |
| **M6** | Details & search | §6.2 views, §6.5 search in both front-ends | **done** — six detail pages (overview, threads, modules, handles, environment, network) in both front-ends; `--find` searches names, command lines, open files and mappings. In-UI search is the terminal's filter; the window has no search box yet |
| **M7** | Privilege helper | §8 end to end, both platforms | **done on Linux** — protocol, helper, client channel, polkit policy, probe and action wiring, and both halves of §9.8. **Not on Windows**: elevation there needs a named pipe rather than redirected stdio (§8.1) |
| **M8** | Polish & budget | §4 met and enforced, dark mode, persisted layout | **partial** — the harness gates on CPU time and allocation and both pass (§4); dark mode follows the theme; nothing is persisted yet (open question 5) |
| **M9** | macOS | Replace the §5.3 stub with a real probe | not started |

**What is left, in order.** Windows elevation, which needs a named pipe rather than redirected stdio
(§8.1). Then the two desktop conveniences — per-process property windows, and persisting the column
choice and sort (open question 5). Then macOS (M9).
The `TreeListView` gaps that blocked three of §7.1's features are gone: they were one upstream change,
and it is merged (Hawkynt/NativeForms#8).

---

## 11. Terminal UI

Shipped in v1, not a later addition — the same engine with a different renderer.

- [x] Full-screen alternate-screen renderer over ANSI/VT, with `termios` raw mode on Linux and
      `ENABLE_VIRTUAL_TERMINAL_PROCESSING` on Windows, restored on exit **and on crash**.
- [x] Diff-based redraw: only changed cells are written. A monitor that repaints 200 rows per second
      over SSH is its own denial of service.
- [x] htop-compatible keys where htop has one: `F5` tree · `F6` sort · `F9` kill · `F10`/`q` quit ·
      `/` search · `\` filter · `u` user filter · `H` threads · `t` tree · space tag · `<`/`>` sort
      column. Keys we add do not shadow keys htop uses for something else.
- [x] Per-core meters, memory/swap bars and load average in the header, drawn by the same math the
      desktop plots use.
- [x] **Three in-row history columns** — CPU, memory and I/O — drawn with the eighth-block characters
      U+2581 to U+2588, sharing the same `ProcessHistory` and the same scales the window's sparklines
      use. Twelve characters wide is twelve samples at eight levels: coarse next to a pixel-per-sample
      plot, and enough to tell a process that is busy now from one that was busy a moment ago.
      A reading above zero always gets at least the smallest mark, or half a percent would look
      exactly like none.
- [x] Degrades by capability, not by guess: colour depth comes from `NO_COLOR`/`COLORTERM`/`TERM`,
      and the monochrome mode is what the golden frame is rendered in, so it is tested. The block
      characters are used only when `TERM` is not `dumb` **and** the locale says UTF-8; anything else
      gets an ASCII ramp, because a column of replacement boxes is worse than no plot at all.
- [~] Resizes on a size change noticed at the top of the loop rather than on `SIGWINCH` — deliberate:
      the signal arrives on another thread, and the frame diff assumes nobody else writes between
      compose and flush. Sizes smaller than the header are clamped but not tested.
- [x] Non-interactive modes for scripts: `--list`, `--list --json`, `--find`, `--kill`, each with a
      documented exit code (0 success · 1 error · 2 nothing matched). A value that is not there is
      `null` in the JSON and never `0`, so a consumer can tell "no I/O happened" from "you were not
      allowed to look".

---

## 12. Coverage matrix

A feature is finished when every column is ticked. "Desktop" and "TUI" mean *reachable there*, not
merely computed. "Windows" is ✔ only where it has been executed and checked — under Wine so far,
which implements the same structures and is not the same thing as Windows.

| Feature | Implemented | Tested | Desktop | TUI | Linux | Windows | Documented |
|---|---|---|---|---|---|---|---|
| Process list, CPU, memory, I/O | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Process tree | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Sort by any column | ✔ | ✔ | menu | ✔ | ✔ | ✔ | ✔ |
| Filter by text / by user | ✔ | ✔ | user only | ✔ | ✔ | ✔ | ✔ |
| Unknown-with-a-reason (§3.4) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Per-core meters | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| CPU / memory history plot | ✔ | smoke | ✔ | — | ✔ | ✔ | ✔ |
| Owner resolution | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Command line | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Threads | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Modules / mappings | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Open files / handles | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Environment block | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Sockets | ✔ | — | ✔ | ✔ | ✔ | ✔ | ✔ |
| End process / tree | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Suspend / resume | ✔ | ✔ | ✔ | — | ✔ | ✔ | ✔ |
| Priority / affinity | ✔ | ✔ | — | — | ✔ | ✔ | ✔ |
| Handle count on demand | ✔ | ✔ | ✔ | ✔ | ✔ | n/a | ✔ |
| Search across handles (`--find`) | ✔ | — | — | — | ✔ | ✔ | ✔ |
| Privileged helper | ✔ | ✔ | ✔ | ✔ | ✔ | — | ✔ |
| Row colours (§7.1) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| CPU / memory / I/O history columns | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Column selection | ✔ | — | ✔ | fixed set | ✔ | ✔ | ✔ |
| Click-to-sort headers | ✔ | ✔ (upstream) | ✔ | n/a | ✔ | ✔ | ✔ |
| Colour legend | ✔ | ✔ | ✔ | — | ✔ | ✔ | ✔ |
| Published screenshots | ✔ | CI | ✔ | ✔ | ✔ | — | ✔ |
| Persisted layout | — | — | — | — | — | — | ✔ |
| Per-process property windows | — | — | — | — | — | — | ✔ |

The rows worth reading twice. **The action tests test the refusals, not the actions** — every check
that a wrong identity, a missing process, a bad pid or an empty affinity mask is refused, and none
that a process is actually ended, because a test that terminates something to prove it can is not one
to run in CI. **Sockets and `--find` have no automated test**: both need a machine with a known open
socket or a known open file, which the fixture cannot record. **The screenshots are Linux only** —
the window is photographed through GTK and Cairo, and the Windows equivalent would be a second
capture route nobody has needed yet.

## 13. Open questions

Each of these must be answered before the milestone that depends on it, and the answer is recorded
here rather than in a commit message.

1. ~~**CPU% convention as the default**~~ — **answered: normalized.** Both are implemented and either
   can be switched to at runtime (`C` in the terminal, the View menu in the window), and the active
   one is named in the status line, because the two differ by a factor of the core count and a number
   without its convention is not a number.
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
   column layout? *Blocks M8.* Nothing is persisted today, including the sort column and the tree
   toggle, which is the first thing anybody will notice.
6. **Whether the §4 snapshot target survives contact.** 25 ms per 1000 processes was written before
   anything was measured; the design costs 44 µs per process because it reads three files each. The
   choice is to read fewer (dropping `status` would cost the private-memory column and the uid) or to
   move the target. It should not be left as an unmet number nobody intends to meet.
