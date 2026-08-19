# ProcessManager — Product Requirements & Implementation Checklist

**Status:** living document — the specification and the progress board are the same file
**Platforms:** Windows, Linux, macOS
**Replaces:** Windows Task Manager · Sysinternals Process Explorer · System Informer / Process Hacker · DBC Task Manager
**Licence:** LGPL-3.0-or-later

---

## How to read this document

Every requirement is a checkbox. Tick it when it is **implemented and covered by a test or the
self-test** — not when the code is written, and not when it works on one machine.

```
- [x] done, and proven
- [ ] not done
- [ ] 🟡 partly done — the note says which part is missing
- ∅  deliberately out of scope (not a checkbox; see §4)
```

Two marks appear inside field notes, and they are what the UI actually renders — not documentation
shorthand:

| Mark | Meaning |
|:---:|---|
| `n/a` | The platform has no such concept; the UI shows this, never a zero |
| `—` | Readable in principle, but not with the privilege we currently hold |

**The rule that governs the whole document (§72.3):** a value that is not known renders *the reason
it is not known*. An unticked box must never become a zero on screen. This is restated here because
it is the single requirement most likely to be broken while filling the tables in.

**Counting, as of the last update:** **469 of 1254 boxes are ticked** — 62 of 189 in the field
registry (§14–22), 407 of 1065 across the capabilities. A further 137 are marked 🟡, meaning some of
the work behind them is already done. §100 tracks the phases; §101 defines when this may be called
finished.

---

# 1. Executive summary

ProcessManager is a cross-platform system-monitoring and process-management application intended to
functionally replace Windows Task Manager, Process Explorer, System Informer/Process Hacker and DBC
Task Manager with one coherent product.

The application shall provide:

- [x] A modern graphical desktop application
- [x] A terminal user interface
- [x] A shared system-information and process-inspection engine
- [ ] 🟡 A stable CLI and machine-readable API — the CLI exists; the JSON schema and API do not
- [x] Basic task-management workflows suitable for ordinary users
- [ ] 🟡 Advanced process, thread, module, handle, memory, network, security, service and resource
      inspection — the engine reads most of it; the views for it are largely unbuilt
- [ ] Platform-specific advanced capabilities wherever the OS exposes them
- [x] Predictable fallbacks when a capability does not exist on a platform
- [x] An information architecture familiar to users of the named tools without copying their
      branding, icons, trademarks or protected visual assets

Windows Task Manager covers process/resource inspection, startup applications, active user sessions,
services, performance graphs, process dumps, wait-chain analysis and administrative controls. Process
Explorer adds process-tree-oriented inspection and a lower pane for handles or loaded DLLs and memory
mappings. System Informer adds substantially deeper process, thread, stack, service, network, disk,
security, module and handle inspection. DBC Task Manager's contribution is the simplified Windows
8-era presentation with resource pages and graph-oriented performance views.

This product is therefore designed as a **superset**, not a clone of any one of them.

---

# 2. Product vision

ProcessManager should answer, from one application:

What is running? Why is it running? Who started it? What launched it? What resources is it consuming?
What has changed since the last refresh? Which files, modules, sockets, objects or resources does it
own? Which processes are using a particular file? Which process owns a particular connection? Why is
a process hanging? Which threads are busy? What code are those threads executing? What executable or
library is actually loaded? Is that binary signed or otherwise trusted? Which security mitigations
apply? Can the process be safely suspended, terminated, restarted or reprioritised? Which services
and startup mechanisms caused it to exist? How healthy is the machine as a whole? And can the same
investigation be performed without a desktop environment?

It serves both ends of the spectrum:

**Simple.** "Firefox is using too much memory; end it."

**Expert.** "PID 8421 spawned from PID 1172 in session 3, is running elevated ARM64EC code, has 146
threads, three TCP connections, 18 mapped images, 2.4 GB private commit, an unsigned module, and one
thread continuously consuming logical CPU 7."

---

# 3. Goals

## 3.1 Primary — MUST

- [ ] 🟡 Replace everyday Task Manager workflows — process management yes; services, startup, users no
- [ ] 🟡 Replace Process Explorer tree and lower-pane workflows — tree yes, lower pane no
- [ ] Replace the majority of System Informer process-inspection workflows
- [x] Provide the same canonical information in GUI and TUI
- [ ] 🟡 Operate natively on Windows, Linux and macOS — Windows and Linux done; macOS is a stub (§6.3)
- [x] Expose platform-specific fields without pretending different OS abstractions are identical
- [x] Remain useful without elevation or root
- [x] Dynamically expose additional information after elevation
- [x] Start fast enough to remain useful when the machine is under significant load
- [x] Avoid requiring Internet access for ordinary operation
- [x] Provide opt-in reputation services rather than silently transmitting executable information
      — trivially satisfied today: there is no network code at all
- [ ] 🟡 Remain usable with thousands of processes, threads, mappings, connections or handles
      — measured to 1000 processes (§71); 10 000 is untested
- [ ] 🟡 Permit customisation of columns, layouts, refresh intervals, highlighting, shortcuts and
      defaults — columns and interval yes; nothing persists between runs
- [ ] 🟡 Support keyboard-driven operation throughout GUI and TUI — TUI complete, GUI partial
- [x] Permit information to be copied or exported without screenshots

## 3.2 Secondary — SHOULD

- [ ] Serve as a lightweight incident-response triage utility
- [ ] Serve as a developer debugging companion
- [ ] Provide long-running metric history when requested
- [ ] Support plugins and extensions
- [ ] 🟡 Support scripting and automation through CLI/API
- [x] Provide a portable, no-install distribution — single-file NativeAOT binary, 3.2 MB, no runtime
- [x] Support dark, light and system themes
- [ ] Provide an optional tray / menu-bar mode

---

# 4. Explicit non-goals

Not attempted, in any release:

- ∅ A full debugger comparable to WinDbg, LLDB or GDB
- ∅ A full profiler comparable to ETW/WPA, Instruments, perf or commercial profilers
- ∅ A packet-capture application comparable to Wireshark
- ∅ A full endpoint detection and response product
- ∅ An antivirus scanner
- ∅ A kernel debugger
- ∅ A complete replacement for Services MMC, systemd tooling or launchctl
- ∅ A remote fleet-management platform
- ∅ A task-scheduler or cron replacement
- ∅ A generic hardware-information utility such as HWiNFO
- ∅ A process-memory reverse-engineering suite

ProcessManager may integrate with or launch specialised tools when deeper analysis is required.

## 4.1 The kernel driver, and what it costs us

**∅ ProcessManager will not ship a kernel-mode driver.** This is permanent, and it is the single
largest deliberate difference from System Informer.

The reasons: a Windows driver needs an EV certificate and Microsoft attestation signing; it is a
serious attack surface, and a bug in it is a bugcheck rather than a stack trace; and a signed driver
that can read and write arbitrary process memory is a privilege-escalation primitive that gets
abused by other software the moment it is on disk. Several tools in this class have been used exactly
that way.

What we therefore cannot do, and must say so in the UI rather than failing quietly:

- Read from or act on protected processes (PPL) — anti-malware services, LSASS on a hardened machine
- Walk kernel-mode stack frames (§30); ours stop at the user/kernel boundary
- Some handle-object details that require kernel-mode object access
- Close a handle in another process on Linux (no supported mechanism exists at all)

Everything else in this document is reachable from documented user-mode APIs.

## 4.2 Licence boundary

System Informer is GPL-3.0. ProcessManager is LGPL-3.0-or-later. Its **features may be reimplemented
from documented APIs and from observed behaviour; its source code must not be copied, adapted or
translated into this repository.** Where a technique is subtle — the handle-name deadlock in §32 is
the obvious one — the implementation must be derived from the underlying API documentation and
reasoning, and the reasoning recorded in a comment.

---

# 5. Product principles

## 5.1 One canonical data model

There must not be separate "GUI fields" and "TUI fields".

- [x] Every field is registered in a central field catalogue — `FieldRegistry` in
      `ProcessManager.Core/Query`. There were three lists before it (a sort-key enum in Core, the
      window's own `ColumnSet`, and a third in the terminal), and three places to add a field is
      three places to forget one.

`FieldAccessor` reads a field three ways from that one declaration: as text to display, as a number
to compare and filter, and as an ordering. The window and the terminal both render through it, and
the view sorts through it — so sorting by a column cannot disagree with what the column shows, and a
value reads identically in both front-ends because it is the same code producing it.

Each registry entry declares:

- [x] Stable field ID — the `Key`, which is what `--sort`, a saved layout and a search term all use
- [x] Human-readable name
- [x] Short TUI label
- [x] Description
- [x] Data type
- [x] Units
- [ ] Precision
- [x] Whether it is instantaneous, cumulative, delta, rate, state, enumeration or derived
- [x] Supported platforms
- [ ] Required privilege
- [x] Collection cost
- [x] Default visibility
- [x] Sort semantics
- [x] Filter semantics — `Number` for comparisons, `RawText` for substring and regex
- [x] Formatting function
- [x] Null/unavailable semantics
- [ ] Export serialisation
- [ ] 🟡 Historical-storage eligibility — the graph fields declare their series

Worked example — `process.cpu.usage`: display "CPU", TUI "CPU%", percentage, normalised
instantaneous utilisation, 0–100 in default mode and 0–N×100 in raw logical-CPU mode, all platforms,
normal privilege, low collection cost, eligible for history.

## 5.2 Progressive disclosure

- [x] Common information is visible immediately
- [ ] 🟡 Advanced information is one click, key or pane away rather than in the default view

## 5.3 Native semantics over false equivalence

Windows handles are not Unix file descriptors. Windows services are not systemd units. launchd jobs
are not Windows services. Job objects are not cgroups.

- [x] The UI may group concepts under shared categories but retains native terminology and data

## 5.4 Expensive collection is opt-in

- [x] Stack walking, symbol resolution, signature validation, reputation lookups, full handle
      enumeration, string scans and continuous tracing are never required to display the process table

This is enforced by measurement, not intention: §71's budget is a build gate, and the two collectors
that broke it (per-process fd counting at 85 µs, `smaps_rollup` at 0.8–4 ms) are both opt-in because
of it.

## 5.5 Destructive actions are unmistakable

- [x] "Inspect" and "terminate" are never adjacent unlabelled icons
- [ ] 🟡 Every destructive action names its target (§90)

---

# 6. Supported platforms

## 6.1 Windows — **primary**

- [x] Windows 10 22H2 and Windows 11 — verified on NT 10.0.26100
- [x] x64
- [ ] ARM64 — should work; never built or run
- [ ] Server equivalents
- ∅ x86 — no plan

Windows receives the deepest feature set because several target applications expose Windows-specific
concepts.

**Implementation.** Native API only — no WMI anywhere, at any point, for any field. One
`NtQuerySystemInformation(SystemProcessInformation)` call returns every process with its threads and
counters in a single buffer; a second pass adds only what the bulk query omits.

- [x] Bulk process + thread enumeration in one call
- [x] Command line via `ProcessCommandLineInformation`, PEB read as fallback
- [x] Environment block via PEB walk
- [x] Owner SID → `LookupAccountSidW`
- [x] Module list via Toolhelp32
- [x] Handle table via `SystemExtendedHandleInformation` + `DuplicateHandle` + `NtQueryObject`
- [x] Connections via `GetExtendedTcpTable` / `GetExtendedUdpTable`

**The handle-name deadlock.** `NtQueryObject` on a synchronous named pipe whose peer never replies
blocks forever, and the thread cannot be killed safely. Ours runs the query on a worker that is
*abandoned* on timeout, never aborted — and each worker owns its handshake semaphore, because a
shared one is released by the abandoned worker into the next worker's wait and throws
`SemaphoreFullException`. That bug shipped and was caught by the Wine leg in CI.

## 6.2 Linux — **primary**

- [x] Kernel 5.10+
- [x] x86-64
- [ ] ARM64 — should work; never built or run
- [x] `/proc` and `/sys` native collectors
- [x] cgroup v2 first-class
- [ ] 🟡 systemd as the primary service-management target — no service view yet (§41)

**Implementation.** Raw `open`/`read`/`close`/`getdents64` syscalls against UTF-8 byte paths, parsed
from `ReadOnlySpan<byte>` into pooled buffers. Managed file APIs were the original implementation and
cost 3000 ms and 200 KB per sample; this costs 38 ms and 86 bytes.

- [x] `stat` — including the `comm` backscan past the *last* `)`, because a process may be named `)`
- [x] `status`, `io`, `cgroup`, `maps`, `fd/`, `net/{tcp,tcp6,udp,udp6}`
- [x] `smaps_rollup` for PSS/USS — opt-in, 0.8–4 ms per process
- [x] `sysconf(_SC_CLK_TCK)` and `_SC_PAGESIZE` rather than assuming 100 and 4096
- [x] Permission failures surface at `read(2)`, not `open(2)` — handled by errno, not by exception

## 6.3 macOS — **stub**

- [ ] macOS 13+
- [ ] Apple Silicon
- [ ] Intel

The probe exists and throws. This is deliberate and honest: a macOS build that silently reported
nothing would be worse than one that says it is not implemented. Every macOS row in this document is
therefore unticked, and the `∅` marks in the field tables mean "impossible on macOS", not "not yet".

- [x] Unavailable and protected data is displayed explicitly rather than failing silently

---

# 7. Capability levels

Every feature declares one of four states per platform:

| State | Meaning |
|---|---|
| **Full** | All required fields and actions supported |
| **Partial** | Feature exists; one or more platform-specific fields or actions unavailable |
| **Read-only** | Data visible, modification unavailable |
| **Unavailable** | The OS exposes no safe or reliable equivalent |

- [x] Unavailable values render as `—` (or `n/a`, `n/i`, `…`, `×` per §72.3)
- [ ] 🟡 …plus an explanation reachable through tooltip, details or help — the explanation strings
      exist (`Humanize.Explain`); only the detail pane shows them

---

# 8. Architecture

## 8.1 Components

```
Platform backends → Core collector → Snapshot engine → Field registry → Query engine → renderers
                                            ↓                                              ↑
                                     Action broker ← ← ← ← ← ← ← ← ← ← ← ← ← ← ← ← ← ← ← ←
                                            ↓
                                    Privileged helper
```

| Component | Project | Status |
|---|---|:---:|
| Core collector | `ProcessManager.Core` | ✅ |
| Platform backends | `ProcessManager.Platform.{Windows,Linux,MacOS}` | 🟡 macOS stub |
| Snapshot engine | `ProcessManager.Core/Sampling` | ✅ |
| Field registry | `ProcessManager.Core/Query` | ✅ |
| Query engine | `ProcessManager.Core/Query` | 🟡 |
| Action broker | `ProcessManager.Core/Actions` | 🟡 |
| Privileged helper | `ProcessManager.Elevated` | ✅ |
| GUI renderer | `ProcessManager.Ui.Desktop` (NativeForms) | 🟡 |
| TUI renderer | `ProcessManager.Ui.Terminal` | ✅ |
| CLI / API | `ProcessManager.App` | 🟡 |

- [x] Platform code exposes canonical DTOs through a stable internal API
- [x] The snapshot engine maintains current, previous, delta, rates, monotonic timestamp,
      creation/termination detection and history rings
- [x] The query engine is shared by GUI, TUI and CLI
- [x] **The UI never calls a platform API directly** — every mutation goes through the action broker
- [x] The main GUI and TUI remain non-elevated; only the helper is privileged

## 8.2 The sampling model

**Snapshot.** One pass produces absolute readings only. A probe that pre-divides anything has a bug:
every rate, percentage and delta in the program is computed by `SnapshotDelta` from two snapshots.

- [x] Absolute counters only, never rates, from a probe
- [x] Processes live in a pooled `ProcessRecord[]`, a mutable struct — a class would be a thousand
      allocations per sample against a budget of zero
- [x] Monotonic `Stopwatch` timestamps; the wall clock is never used for an interval

**Identity.** `ProcessKey(Pid, StartTicks)`.

- [x] PID reuse cannot attach an exited process's history to a new one — every map in the program is
      keyed by the pair, and the test suite covers the reuse case explicitly

**Delta and rate.** One `RateCalculator`, deliberately the only place a division happens: every
interesting bug in a tool of this kind is a division that should not have been performed.

- [x] A counter that moved backwards yields `CounterInvalid`, not a negative rate
- [x] …except where the fall is the meaning (private-bytes delta), which uses a signed variant
- [x] A zero or non-finite interval yields `CounterInvalid`, never a division
- [x] CPU% is **not** clamped to 100 — above 100 is legitimate per-core and diagnostic when normalised
- [x] Both CPU conventions are computed every sample so both columns can be shown at once

**History.** Per-visible-row ring buffers with shared, decayed, floored scales.

- [x] 60 s at sample resolution, visible rows only
- [x] Floors (CPU 5 %, memory 32 MB, I/O 64 KB) so an idle process is a flat line, not noise
      amplified to full scale
- [x] Decay ×0.92 so a spike does not permanently flatten everything after it
- [ ] Longer windows (5 min, 15 min, 1 h) need a decimating ring — 3600 points per series per
      process is not affordable (§85)

**Cadence.** See §12.

## 8.3 AOT and interop rules — enforced, not aspirational

- [x] `PublishAot` and `PublishTrimmed`; `IsAotCompatible` on every library
- [x] **`[LibraryImport]` only.** `[DllImport]` is banned; the source generator produces the marshalling
      so it is visible, trimmable and AOT-safe
- [x] `AllowUnsafeBlocks` — required by the generated marshalling
- [x] No reflection, no dynamic codegen, no `BinaryFormatter`, no runtime `Type.GetType`
- [x] Native structs are blittable; `StringBuilder` is never a parameter (SYSLIB1051 — use `ref char`)
- [x] Pointers from a native buffer are treated as **offsets into that buffer**, bounds-checked, never
      dereferenced as absolute addresses — this is what makes a captured buffer replayable in a test
- [x] Single-file publish produces exactly one binary with no loose native assets

Verified: `dotnet publish -r linux-x64 --self-contained -p:PublishAot=true` → one 3.2 MB ELF, which
runs, passes the self-test 21/21 and opens the window.

## 8.4 Engineering rule — see §103

No field or action may be introduced inside a front-end. §103 lists the thirteen steps.

---

# 9. Main GUI information architecture

Primary navigation:

- [x] Processes
- [ ] 🟡 Performance — a system overview exists; the resource selector does not (§45)
- [ ] Applications / usage history (§44)
- [ ] 🟡 Startup — `--startup` lists them; there is no view (§42)
- [ ] 🟡 Users / sessions — `--users` lists them; there is no view (§43)
- [ ] 🟡 Services — `--services` lists them; there is no view and no control verbs (§41)
- [ ] 🟡 Network — connections are collected and shown per process, not as a view (§40)
- [ ] System activity (§51)
- [ ] Search / find resources (§33)
- [ ] Logs / history (§63)
- [ ] Settings (§67)

Optional advanced views: drivers/kernel modules, file activity, disk activity, GPU activity,
containers/cgroups, jobs, security, devices — all unbuilt.

---

# 10. Global window layout

- [ ] Left navigation rail with the persistent primary views
- [ ] 🟡 Top command bar with context-sensitive actions — a menu bar exists instead
- [x] Primary content pane: table, tree table, graphs or dashboard
- [ ] Optional lower pane, resizable, toggled by shortcut, toolbar button and menu

Lower-pane modes (all unbuilt as a *pane*; the engine behind the ticked ones works and is reachable
from the detail pane):

- [ ] Summary
- [ ] Threads
- [ ] Modules
- [ ] Handles / descriptors
- [ ] Network
- [ ] Memory mappings
- [ ] Environment
- [ ] Windows
- [ ] Services
- [ ] Security
- [ ] Timeline

The lower pane is the defining Process Explorer interaction and is the highest-value single item in
this document.

---

# 11. Global table requirements

Every table:

- [x] Column show/hide
- [ ] Column reorder
- [ ] 🟡 Column resize
- [ ] Column reset
- [x] Ascending sort
- [x] Descending sort
- [ ] Multi-column sort
- [ ] 🟡 Keyboard sort — TUI only
- [ ] Freeze / pin columns
- [ ] Auto-size column / all columns
- [ ] Copy cell
- [ ] 🟡 Copy row
- [ ] Copy selected rows / columns
- [ ] Export table
- [x] Text filter
- [x] Advanced filter
- [x] Regular-expression filter
- [x] Numeric comparison filters
- [x] Unit-aware comparison
- [ ] Case-sensitive toggle — everything matches case-insensitively today
- [ ] Highlight matched text
- [ ] Multi-selection
- [ ] Select all / invert selection
- [ ] 🟡 Context menu
- [x] Persist layout — columns, sort and interval survive a restart

Named **column sets**. Each stores its visible fields and their ordering; widths come from the
registry, and sorting, grouping and pinned columns are not stored yet.

- [x] Basic
- [x] Performance — as `cpu`
- [x] Memory debugging — as `memory`
- [x] Security
- [x] I/O debugging — as `io`
- [ ] Network — blocked on §18
- [ ] 🟡 Full forensic — `expert` is close, but has none of the security or I/O detail
- [x] Minimal recovery — as `minimal`

Reachable as `--columns @security`. A set saved in the file replaces a built-in preset of the same
name; the presets themselves are never written into the file, because a preset copied into
everybody's settings could never be improved again.

§94 defines the presets' contents.

---

# 12. Update / refresh system

- [ ] 🟡 Intervals 250 ms · 500 ms · **1 s** · 2 s · 5 s · 10 s · paused · manual — settable from
      the CLI and persisted; the in-app picker and pause are TUI-only
- [x] Default 1 second
- [ ] 🟡 Pause while preserving selection

Refresh preserves:

- [x] Scroll position
- [x] Expanded process nodes
- [x] Selected entities
- [x] Sort order
- [x] Open property page
- [ ] Lower-pane mode (none exists yet)

A row index is not a place. The window held its scroll position by keeping `TopIndex` across a
rebuild, which is exactly wrong: the number survives and the rows underneath it do not, so twenty
processes exiting above the viewport slide twenty rows of different content under the reader's eyes,
once a second. What is held now is the *node* at the top — noted before the rebuild, found again
afterwards, and put back — and a node that has exited leaves the view where the rebuild put it,
because there is nowhere better for it to be.

- [ ] Dead processes remain visible for one cycle with terminated styling, if enabled
- [x] New processes are optionally highlighted

Node reuse across samples is what makes expansion and selection survive — and it is why sorting
silently stopped working for one release: a reused node kept its insertion order forever. The binder
now reorders each sibling group to match the view, skipping the work when it already matches.

---

# 13. Process presentation modes

- [ ] **Friendly / grouped** — applications, background processes, system processes, categorised
      per platform
- [x] **Process tree** — parent/child hierarchy, expand/collapse per row
- [x] **Flat expert** — one row per process, optimised for sorting and density

- [x] When a parent disappears, surviving children remain visible and become orphaned roots
- [ ] …or attach to the closest surviving ancestor, per configured behaviour

---

# 14. Process table — identity fields

The canonical field registry. The ID is stable: a saved layout, a `--columns` argument and a search
term all use it, and it never changes even when the display name differs per platform.

- [x] `name` — friendly process/executable name
- [ ] 🟡 `exe.name` — actual executable filename; derived from `image.path`, differs from `name` when
      a process renames itself, not yet its own field
- [ ] `app.name` — human-readable product/application identity
- [x] `pid` — process identifier
- [x] `ppid` — parent process identifier
- [x] `instance.id` — PID plus creation-time-safe unique identity (`ProcessKey`)
- [ ] 🟡 `parent.name` — parent executable/application; resolvable from the tree, not a field
- [x] `tree.depth` — hierarchical depth
- [x] `status` — running, sleeping, suspended, zombie, terminated
- [ ] `responding` — GUI responsiveness; `IsHungAppWindow` on Windows, `n/a` on Linux
- [x] `start.time` — process creation timestamp
- [x] `running.time` — elapsed lifetime
- [ ] `exit.time` — requires retaining dead rows; ties to §87
- [ ] `exit.code` — only for children we spawned or via job/wait; honest `—` otherwise
- [x] `user` — account owning the process
- [x] `user.id` — SID / UID
- [x] `session` — login/terminal session
- [x] `session.id` — native session identifier
- [ ] `arch` — x86, x64, ARM64; `IsWow64Process2` (W), ELF header (L)
- [ ] `emulation` — WOW64, Rosetta, translation state
- [x] `image.path` — full image path
- [x] `cmdline` — complete command invocation
- [ ] 🟡 `cwd` — current working directory; Linux readable, Windows needs a PEB read we do not do
- [ ] `description` — binary description (version resource)
- [ ] `company` — publisher metadata
- [ ] `product` — product metadata
- [ ] `product.version`
- [ ] `file.version`
- [ ] `package` — MSIX / Flatpak / Snap / `.app`
- [ ] `app.id` — platform application ID
- [ ] `bundle.id` — macOS bundle identifier
- [ ] 🟡 `container.id` — the cgroup path is read; the container ID is not parsed out of it
- [ ] `namespace` — `/proc/pid/ns/*` readlink
- [ ] 🟡 `job.cgroup` — Linux cgroup path done; Windows job object not
- [ ] 🟡 `terminal` — controlling TTY; already parsed from `stat` field 7, not surfaced
- [ ] `exe.size`
- [ ] `exe.modified`
- [ ] `exe.created`
- [ ] `subsystem` — GUI/console/native; PE only, `n/a` for ELF
- [ ] `interpreter` — shebang / `PT_INTERP`
- [ ] `runtime` — native/.NET/JVM/Python, from the module list

# 15. Process table — CPU fields

- [x] `cpu.percent` — normalised 0–100 % utilisation
- [x] `cpu.percent.raw` — multi-core cumulative
- [ ] `cpu.delta` — change since prior sample
- [x] `cpu.time` — total processor time
- [x] `cpu.time.user`
- [x] `cpu.time.kernel`
- [x] `cpu.cycles` — Windows only; `n/a` on Linux
- [x] `cpu.cycles.delta`
- [x] `ctx.switches`
- [x] `ctx.switches.delta`
- [x] `ctx.switches.rate`
- [x] `threads` — current thread count
- [ ] `threads.peak`
- [x] `priority.base`
- [ ] 🟡 `priority.dynamic` — Linux only
- [ ] `priority.class` — idle/below normal/normal/… (`GetPriorityClass`)
- [x] `nice`
- [ ] `cpu.affinity` — `sched_getaffinity` / `GetProcessAffinityMask`
- [ ] `cpu.set` — Windows CPU sets
- [ ] `numa.node`
- [x] `cpu.last` — field 39, which sits behind fourteen fields nothing else reads
- [ ] `sched.class` — `sched_getscheduler`
- [ ] `qos` — OS energy/performance state
- [ ] `throttled` — cgroup `cpu.stat` `nr_throttled`

Required of the CPU percentage:

- [x] Normalised 0–100 view
- [x] Logical-CPU cumulative view
- [ ] Configurable decimal precision

# 16. Process table — memory fields

- [ ] `mem.percent` — share of usable physical memory (derivable now)
- [x] `ws` — working set / RSS
- [x] `ws.peak`
- [x] `ws.private` — `WorkingSetPrivateSize` (W), `RssAnon` or PSS (L)
- [ ] 🟡 `ws.shared` — `RssFile + RssShmem` available on Linux, not surfaced
- [ ] `ws.shareable`
- [x] `private.bytes` — private committed virtual memory; `PrivatePageCount` (W), `VmData` (L).
      Both mean commit charge — this was `RssAnon` on Linux until it was corrected, which made the
      same column mean two different things on two platforms
- [ ] `private.bytes.peak`
- [x] `virtual.size`
- [x] `virtual.size.peak`
- [x] `commit.size` — same counter as `private.bytes`
- [x] `pss` — Linux, opt-in; `smaps_rollup` costs 0.8–4 ms per process
- [ ] 🟡 `uss` — reported by `smaps_rollup`, not surfaced
- [x] `swap`
- [ ] `mem.compressed`
- [x] `page.faults`
- [x] `page.faults.delta`
- [x] `page.faults.hard` — Linux `majflt`
- [x] `page.faults.hard.rate`
- [ ] `page.priority` — Windows
- [x] `pool.paged` — Windows; `n/a` on Linux
- [x] `pool.nonpaged` — Windows; `n/a` on Linux
- [ ] `heap.count`
- [ ] `stack.commit`
- [ ] `mapped.file.bytes`
- [x] `anon.bytes` — `RssAnon`
- [ ] `shared.mem`

- [x] Terminology adapts per platform while the ID stays canonical — the Linux build labels
      `private.bytes` "Commit" and the Windows build "Private bytes", and a saved layout moves
      between them unchanged

# 17. Process table — I/O fields

- [ ] `disk.percent` — process contribution where derivable
- [x] `io.read.rate`
- [x] `io.write.rate`
- [x] `io.total.rate` — read + write + other
- [ ] 🟡 `io.read.ops` / `io.read.ops.delta` — `syscr` (L) and `ReadOperationCount` (W) both
      available, not surfaced
- [x] `io.read.bytes` / `io.read.bytes.delta`
- [ ] 🟡 `io.write.ops` / `io.write.ops.delta` — as above
- [x] `io.write.bytes` / `io.write.bytes.delta`
- [ ] 🟡 `io.other.ops` — Windows only
- [x] `io.other.bytes` — Windows only
- [x] `io.rate` — aggregate bytes/sec
- [ ] `io.priority` — `ioprio_get` / `NtQueryInformationProcess`
- [ ] `io.wait` — `/proc/pid/schedstat`, needs delayacct
- ∅ `disk.latency` — requires eBPF/ETW tracing; see §52

# 18. Process table — network fields

Per-process byte counters have no portable source: Linux needs packet accounting or eBPF, Windows
needs ETW. Both are opt-in subsystems and **off by default** — §5.4 forbids making the ordinary
process table depend on them.

- [ ] `net.percent`
- [ ] `net.send.rate`
- [ ] `net.recv.rate`
- [ ] `net.rate`
- [ ] `net.sent.bytes`
- [ ] `net.recv.bytes`
- [ ] `net.errors`
- [ ] `net.packets.sent`
- [ ] `net.packets.recv`
- [ ] 🟡 `tcp.count` — endpoints are enumerated and attributed; not aggregated into a column
- [ ] 🟡 `udp.count` — as above
- [ ] 🟡 `net.listening` — as above
- [ ] 🟡 `net.remote.count` — as above

# 19. Process table — GPU fields

- [ ] `gpu.percent`
- [ ] `gpu.engine`
- [ ] `gpu.engine.percent`
- [ ] `gpu.adapter`
- [ ] `gpu.mem.dedicated`
- [ ] `gpu.mem.shared`
- [ ] `gpu.mem.total`
- [ ] `gpu.mem.dedicated.delta`
- [ ] `gpu.encode`
- [ ] `gpu.decode`
- [ ] `gpu.compute`
- [ ] `gpu.copy`
- [ ] `gpu.graphics`
- [ ] `gpu.power`

Sources: Windows GPU performance counters (what Task Manager reads); Linux DRM
`/sys/class/drm/*/device` plus per-vendor `fdinfo` (`drm-engine-*`), covering amdgpu and i915 and
leaving proprietary NVIDIA to a vendor plugin.

- [ ] **Unsupported driver stacks render capability state, never a zero** (§72.3 restated — this is
      why the fields exist in the registry before they have values)
- [ ] OS-provided data is separated from vendor-specific sensor extensions; vendor plugins cannot
      stall a sample or crash the sampler

# 20. Process table — object and resource fields

- [x] `handles` — handle count (W) / fd count (L)
- [ ] `handles.peak`
- [x] `fd.count`
- [ ] 🟡 `socket.count` — derivable from the fd scan
- [ ] 🟡 `file.count` — as above
- [ ] 🟡 `pipe.count` — as above
- [ ] `event.count` — Windows handle-type tally
- [ ] `semaphore.count`
- [ ] `mutex.count`
- [ ] `section.count`
- [ ] `regkey.count`
- [ ] `user.objects` — `GetGuiResources(GR_USEROBJECTS)`
- [ ] `gdi.objects` — `GetGuiResources(GR_GDIOBJECTS)`
- [ ] `mach.ports` — macOS
- [ ] `ipc.count`

The per-type tallies are one pass over a handle table that already exists — but they must **not**
move into the sample loop before that cost is measured against §71. Handle enumeration is currently
on-demand precisely because it is expensive.

# 21. Process table — security fields

- [x] `elevated` — the effective uid on Linux, `TokenElevation` on Windows
- [x] `integrity` — the last sub-authority of the token's mandatory label: untrusted, low, medium,
      medium+, high or system, and the raw number for anything Microsoft adds later
- [ ] `protected` — protected-process status
- [ ] `protection.level`
- [ ] `signature.status` — see §70's vocabulary
- [ ] `signer` — verified publisher
- [ ] `cert.subject`
- [ ] `cert.issuer`
- [ ] `signature.timestamp`
- [ ] `hash.sha256` — on demand only
- [ ] `hash.sha1`
- [ ] `reputation` — opt-in, see §70
- [ ] `dep`
- [ ] `aslr`
- [ ] `cfg`
- [ ] `cet`
- [ ] `acg`
- [ ] `cig`
- [ ] `sandbox`
- [ ] `appcontainer`
- [ ] `capabilities`
- [x] `selinux.context` — `/proc/pid/attr/current`, opt-in
- [x] `apparmor.profile` — same file, same field: the LSM label is one value whichever module wrote it
- [x] `seccomp` — off, strict or filter
- [x] `caps.linux` — the effective mask (`CapEff`); the permitted and inheritable sets are not shown yet
- [ ] macOS: code-sign identity, entitlements, hardened runtime, sandbox

- [ ] **Online reputation checking is opt-in, and the program states exactly what is transmitted
      before the first time it happens** — at the point of use, not buried in a settings page

# 22. Process table — energy fields

Every one of these is an **estimate** wherever the OS does not measure it directly, and is labelled
as such. Windows models energy impact from weighted CPU/disk/network; Linux has RAPL for the package
and nothing per-process. A model presented as a measurement is exactly the dishonesty §72.3 exists to
prevent.

- [ ] `power.usage`
- [ ] `power.trend`
- [ ] `energy.impact`
- [ ] `energy.cpu`
- [ ] `energy.gpu`
- [ ] `qos.background`
- [ ] `eco.state`
- [ ] `thermal`
- [ ] `battery.impact`

---

# 23. Process highlighting

- [x] Highlight colours are configurable
- [ ] Highlighting is disabled in high-contrast modes where inappropriate

Categories:

- [x] Newly started
- [x] Terminating
- [x] Suspended
- [x] Service-hosting
- [x] System process
- [x] Current user's process
- [x] Another user's process
- [x] Elevated — and deliberately not the same colour as System: one is a process root started, the
      other is a process a user started that is root now, which is the more interesting of the two
- [ ] Packaged application — needs `package` (§14)
- [ ] Managed runtime — needs `runtime` (§14)
- [ ] Unsigned executable — needs `signature.status` (§21)
- [ ] Invalid signature
- [ ] Suspicious reputation — needs opt-in reputation
- [ ] High CPU
- [ ] High memory
- [ ] High disk
- [ ] High network
- [ ] High GPU
- [ ] Process with an active UI window — needs §39
- [ ] Process with a changed executable — needs image mtime + hash watch
- [ ] Process containing the selected search match — needs §56

The seven that are ticked are the ones the program can *prove*. The rest stay off rather than
guessing: a colour claiming "unsigned" without having checked a signature is worse than no colour.

---

# 24. Process tooltip / quick inspector

- [ ] Hover or keyboard inspection shows: name, PID, parent, user, path, command line, start time,
      CPU, memory, I/O, network, signature, service names, package/container, detected runtime,
      window title, current state
- [ ] **Tooltips perform no expensive synchronous collection** — everything is either already in the
      snapshot or fetched asynchronously with `…` shown until it arrives

---

# 25. Process actions

## 25.1 Lifecycle

- [ ] 🟡 End task gracefully — `SIGTERM` on Unix; Windows `WM_CLOSE` to the main window is missing
- [x] Terminate
- [ ] Terminate process tree
- [ ] Restart
- [x] Suspend
- [x] Resume

- [x] "End task" and "Terminate" remain semantically distinct — the first asks, the second does not.
      A UI that blurs them loses somebody's unsaved work.

## 25.2 Scheduling

- [x] Set priority
- [x] Set nice value
- [ ] Set scheduling class
- [x] Set processor affinity — through the helper where it needs privilege
- [ ] Set CPU set
- [ ] Set I/O priority
- [ ] Set page priority
- [ ] Enable/disable efficiency mode or platform QoS

## 25.3 Navigation

- [x] Open properties (§26)
- [x] Expand / collapse tree
- [ ] Go to parent
- [ ] Go to children
- [ ] Go to owning service
- [ ] Go to package
- [ ] Go to executable
- [ ] Reveal in file manager
- [ ] Open file properties
- [x] Copy path
- [x] Copy command line
- [ ] Search Internet
- [ ] Inspect binary (§53)

## 25.4 Diagnostics

- [ ] Create memory dump — **baseline Windows parity requirement**; Task Manager has it
- [ ] Analyse wait chain — **baseline Windows parity requirement**
- [x] Inspect threads
- [ ] Inspect stacks (§30)
- [x] Inspect modules
- [x] Inspect handles / descriptors
- [ ] 🟡 Inspect memory mappings — `maps` is parsed, nothing displays it
- [x] Inspect environment
- [ ] Inspect token / security context
- [x] Inspect network connections
- [ ] Inspect windows
- [ ] Inspect services

## 25.5 Memory — expert

- [ ] Trim working set
- [ ] Read memory
- [ ] Save memory range
- [ ] Search readable memory
- [ ] Inspect mapped region

- [ ] Direct modification of another process's memory is classified expert/debugging and **disabled
      by default** — the only feature here requiring a deliberate per-session opt-in

## 25.6 Modules

- [ ] View module
- [ ] Reveal module file
- [ ] Verify signature
- [ ] Hash file
- [ ] Open binary inspector
- [ ] Search reputation
- [ ] Copy path
- [ ] Inspect mapped memory
- [ ] Unload module — expert-only, with an explicit instability warning

---

# 26. Process properties window

- [x] Double-click or Properties opens a **persistent** inspector — one per process, and several at
      once, which is what makes comparing two of them possible
- [ ] Tabs whose capability is unavailable are hidden **or** disabled by user preference — the
      preference matters, because hidden and disabled answer different questions ("can this machine
      do it" versus "get out of my way")

Tabs:

- [ ] 🟡 General (§27) — the overview tab carries identity, ownership, timing and the command line;
      the version, signature and hash fields need §21
- [ ] Performance (§28)
- [ ] CPU
- [ ] Memory
- [ ] I/O
- [x] Threads (§29)
- [x] Modules (§31)
- [x] Handles / resources (§32)
- [ ] Memory map (§34)
- [x] Network (§40)
- [ ] GPU (§19)
- [ ] Security (§36)
- [x] Environment (§37)
- [ ] Jobs / cgroups / containers (§38)
- [ ] Windows (§39)
- [ ] Services (§41)
- [ ] Runtime (§80)
- [ ] Strings (§35)
- [ ] Timeline (§63)

The window exists now, hosting the same pane the main window docks at its foot — pinned to one
process rather than following the selection, which is what makes two of them comparable.

When the process ends the window stays open, says so in its title and stops asking about the pid:
a window that followed the number would quietly start describing whoever the kernel gave it to next
(§72.2, §86).

---

# 27. General process properties

- [ ] Icon, name, PID, PPID, start time, running duration, state, session, user, architecture,
      executable path, command line, current directory, parent process, application identity,
      package/bundle, version, company, description, signer, signature status, file hashes, file
      size, creation/modification timestamps, runtime, service associations, container/cgroup/job
      associations
- [ ] Buttons: Copy · Reveal executable · File properties · Verify · Inspect binary

# 28. Performance process properties

- [ ] Historical graphs for CPU, private/commit, resident/working set, I/O, disk, network, GPU,
      handles/descriptors and thread count
- [ ] Time windows: 60 s · 5 min · 15 min · 1 h · retained-history limit
- [ ] Hover values
- [ ] Keyboard-accessible point inspection

The 60-second ring exists (§8.2). The longer windows need the decimating ring from §85.

---

# 29. Threads view

The engine enumerates threads on both platforms; the table shows a subset.

- [x] Thread ID
- [x] State
- [ ] CPU %
- [x] CPU time
- [x] User CPU time
- [x] Kernel CPU time
- [ ] Cycles
- [ ] Cycles delta
- [x] Context switches
- [ ] Context-switch rate
- [x] Start time
- [ ] 🟡 Start address
- [ ] Resolved start module
- [ ] Resolved start symbol
- [ ] Current instruction / address
- [x] Priority
- [ ] Base priority
- [ ] Scheduling policy
- [ ] Ideal processor
- [x] Current / last CPU
- [ ] Affinity
- [x] Wait reason
- [ ] Wait duration
- [ ] Kernel/user indicator
- [ ] Stack usage
- [ ] TEB / TLS information
- [x] Name
- [ ] Description
- [ ] Service association
- [ ] AppDomain / runtime context

The wait reason earns its place above the rest: it answers "why is this hanging" — §2's first
question — without a stack walk. On Linux it is the kernel symbol from `wchan`
(`futex_wait_queue_me`, `poll_schedule_timeout`); on Windows it is the thread's `KWAIT_REASON`, which
the bulk query already carried and nothing read. The names come free from the same `stat` line the
rest is parsed from, because Linux gives every thread its own `comm`.

Actions:

- [ ] Suspend thread
- [ ] Resume thread
- [ ] Terminate thread — **labelled dangerous** (§69 class 3)
- [ ] Set priority
- [ ] Set affinity
- [ ] View stack
- [ ] Copy
- [ ] Go to start module
- [ ] Resolve symbols
- [ ] Save stack

# 30. Stack viewer

- [ ] Native stacks
- [ ] Symbols when available
- [ ] Module + offset fallback
- [ ] Source filename and line when symbols provide it
- [ ] Kernel frames where permissions permit — **see §4.1: without a driver, ours stop at the
      user/kernel boundary and say so**
- [ ] Managed runtime frames
- [ ] Mixed native/managed stacks
- [ ] Columns: frame · address · symbol · module · source · source line · displacement · frame type
- [ ] Actions: refresh · copy frame · copy stack · resolve symbols · open module · save stack
- [ ] **Symbol loading is asynchronous** — a symbol-server round trip is never on the UI thread

# 31. Modules / loaded images view

Enumeration works on both platforms (Toolhelp32; `/proc/pid/maps`).

- [x] Name
- [x] Path
- [x] Base address
- [x] Size
- [ ] End address
- [ ] Entry point
- [ ] Architecture
- [ ] Module type
- [ ] Load count
- [ ] Load time
- [ ] Load reason
- [ ] File size
- [ ] File modification time
- [ ] Version
- [ ] Description
- [ ] Company
- [ ] Product
- [ ] Signature status
- [ ] Signer
- [ ] SHA-256
- [ ] ASLR
- [ ] CFG
- [ ] 🟡 Executable flag — in `maps`, not surfaced
- [ ] 🟡 Writable flag — as above
- [ ] 🟡 Mapped / shared — as above
- [x] Backing file
- [ ] Runtime classification

- [x] Windows enumerates DLLs and mapped images; Unix maps the concept to shared objects and
      executable mappings

# 32. Handles / descriptors / resources view

Enumeration works on both platforms, including the name resolution that deadlocks on synchronous
named pipes (§6.1).

- [x] Resource type
- [x] Native type
- [x] Handle / FD identifier
- [x] Name / path
- [ ] Access rights
- [ ] Flags
- [ ] Object address
- [ ] Reference count
- [ ] File offset
- [ ] File type
- [ ] Device
- [ ] 🟡 Inode
- [ ] 🟡 Socket endpoint
- [ ] Creation / open time
- [ ] Target process

Categories — Windows: files, directories, registry keys, processes, threads, events, mutexes,
sections, jobs, tokens, desktops, window stations, pipes, ports, transactions, devices. Linux/macOS:
files, directories, sockets, pipes, event descriptors, devices, shared memory, process descriptors,
kernel/event interfaces.

- [ ] 🟡 Resource categories as above — the types are read; the grouping is not built

Actions:

- [ ] Copy
- [ ] Reveal / open path
- [ ] Resource properties
- [ ] Go to owning process
- [ ] Close resource — **strong warning** (§69 class 3)

- ∅ Closing a descriptor in another process on Linux — no supported mechanism exists; offering it
  would be a lie dressed as a feature

# 33. Find handles / files / modules / resources

The question this answers — "which process is using this file?" — is one of the two or three reasons
people install Process Explorer at all.

- [x] Search targets: resource/handle names · file descriptors · executable paths · loaded modules ·
      memory mappings · sockets · service names · process names · command lines
- [ ] 🟡 Modes — substring ✔ (case-insensitive) and regex ✔ as `/pattern/`; wildcard, exact and a
      case-sensitive toggle are not
- [ ] 🟡 Results — process ✔ · PID ✔ · resource type ✔ · name/path ✔ · user ✔; the access mask is
      not reported
- [ ] Double-click navigates to the process **and** the resource — there is no view to click in;
      `procman --find` is the whole interface

Reported one reason per process for the three that are really one thing. A pattern matching a name
usually matches the command line and the path too, so the most specific one that answered wins:
three rows saying the same thing is noise, not information.

The expensive half — every descriptor, every mapping and every socket of every process — runs only
for the processes the cheap fields did not already answer for (§5.4), which a test asserts by
counting the reads.

---

# 34. Memory map

`/proc/pid/maps` is parsed; nothing displays it.

- [ ] Columns: start · end · size · state · type · protection · allocation protection ·
      private/shared · committed · resident · dirty · executable · writable · copy-on-write ·
      backing file · module · region classification · NUMA node · huge-page state · guard-page
      state · stack owner · heap association
- [ ] Actions: inspect bytes · save region · search strings · go to mapped file/module ·
      copy address · copy range

# 35. Strings view — expert

- [ ] Scan accessible memory or executable files for ASCII, UTF-8 and UTF-16
- [ ] Configurable minimum length
- [ ] Filters: executable image only · private memory · mapped memory · specific region · regex ·
      substring
- [ ] **The UI warns that a full process scan is expensive before starting one, not after**

# 36. Security / token view

Windows:

- [ ] 🟡 User SID ✔ · groups · restricted SIDs · privileges and their state · integrity level ✔ ·
      elevation ✔ · virtualisation · AppContainer · capabilities · claims/security attributes ·
      token type · impersonation · session ✔ · protection level · process mitigations

Linux — most of this is already in `/proc/pid/status`, which the sampler already reads:

- [ ] 🟡 UID / eUID / sUID / fsUID and GID equivalents — the real and effective uids are read; the
      saved and filesystem ids and the group ids are not
- [ ] Supplementary groups
- [x] Capabilities
- [x] SELinux context
- [x] AppArmor profile
- [x] seccomp state
- [ ] Namespaces
- [x] no-new-privileges

macOS:

- [ ] UID/GID · code-sign identity · entitlements · hardened runtime · sandbox

- ∅ Modifying tokens or capabilities — explicitly not a baseline requirement, and not planned

# 37. Environment view

- [x] Variables read on both platforms — PEB walk (W), `/proc/pid/environ` (L)
- [x] Displayed as name / value
- [ ] Search
- [ ] Copy name / copy value / copy row
- [ ] Export
- [ ] Reveal full value

- [x] **Values are collected only when requested.** An environment block routinely holds
      credentials, and §71 would not pay for reading one per process per second even if it were
      harmless.
- [x] Potential secrets are never uploaded as telemetry — trivially guaranteed: there is no telemetry

# 38. Jobs / cgroups / containers

Windows job objects:

- [ ] Job name · process membership · CPU limits · memory limits · process limits · UI restrictions ·
      affinity · scheduling · accounting

Linux cgroups:

- [x] cgroup path
- [ ] Hierarchy
- [ ] Controllers
- [ ] CPU limits
- [x] Memory limit
- [ ] Current memory usage
- [ ] I/O limits
- [ ] Process membership
- [ ] Pressure metrics (PSI)

Containers:

- [ ] Runtime · container ID · locally resolvable name · namespaces · resource limits

# 39. Windows / UI objects

- [ ] Window title · native window ID/handle · process · thread · class · state · visible ·
      minimized · maximized · responding · bounds · desktop/workspace · monitor · parent/owner
- [ ] Actions: bring to foreground · minimize · maximize · restore · close · inspect properties

Windows has `EnumWindows`. X11 has `_NET_CLIENT_LIST`. **Wayland has nothing** — by design, a Wayland
client cannot enumerate other clients' surfaces.

- [ ] This page reports `n/a` with an explanation on a Wayland session rather than appearing
      mysteriously empty for half of all Linux users

# 40. Network view

Endpoints are enumerated on both platforms and attributed to processes.

- [x] Process
- [x] PID
- [ ] User
- [x] Protocol
- [x] Address family
- [x] State
- [x] Local address
- [x] Local port
- [ ] Local hostname
- [x] Remote address
- [x] Remote port
- [ ] Remote hostname
- [ ] Service name
- [ ] Interface
- [ ] Connection creation time
- [ ] Connection age
- [ ] Bytes sent / received
- [ ] Send rate / receive rate
- [ ] Packets sent / received
- [ ] Retransmissions
- [ ] Latency / RTT
- [ ] Owning service
- [ ] Container / cgroup
- [ ] Firewall / security context

Protocols:

- [x] TCP
- [x] UDP
- [x] IPv4
- [x] IPv6
- [ ] Unix / local sockets as a separate native category

Actions:

- [ ] Go to process
- [ ] Process properties
- [ ] Copy endpoint
- [ ] Resolve hostname
- [ ] Disable hostname resolution
- [ ] Close connection where natively supported
- [ ] Terminate owner
- [ ] Search remote endpoint

- [ ] **Hostname resolution is asynchronous and globally disableable** — a blocking DNS lookup in a
      table that refreshes every second is a hang waiting to happen, and on some networks it is also
      a disclosure

# 41. Services view

Read on Linux; unbuilt on Windows and macOS. Control — start, stop, enable — is unbuilt everywhere.

Shared columns:

- [ ] 🟡 Name ✔ · description ✔ (which is systemd's display name) · state ✔ · enabled ✔ · PID ✔ ·
      binary/command ✔. User/account, service type, arguments, dependencies, dependents, failure
      state, start time and last state change are not read

Windows-specific:

- [ ] Service type · service group · accepted controls · error control · start account ·
      delayed start · trigger information · required privileges · preshutdown timeout ·
      key modification time · driver service indicator

systemd-specific:

- [ ] 🟡 Unit name ✔ · main PID ✔ · fragment path ✔ · description ✔ · restart policy ✔ · masked ✔.
      Load state, sub-state, control PID and the activation timestamp are not — those need systemd
      itself to answer

launchd-specific:

- [ ] Label · domain · PID · status · executable/program · arguments · keep-alive · run-at-load

Actions:

- [ ] Start · stop · restart · pause/continue where native · enable · disable · reload
- [ ] Open configuration · reveal executable · go to process · properties · copy ·
      inspect dependencies
- [ ] Creating and editing services — deferred to a later release

**Read without D-Bus and without spawning `systemctl`.** Everything the columns need is on disk: the
unit files say what a service is, the `*.wants` symlinks say whether it starts at boot, and the
cgroup tree says what is running and with which main process. A D-Bus client is a substantial piece
of machinery, and shelling out to read state is the thing that stops working on the machine you most
need it on.

Three shapes of running service had to be handled, and each was found by comparing against
`systemctl` rather than by reading the documentation:

- a service **nested in a slice of its own** — cups lives at `system.slice/system-cups.slice/`
- a service whose **own cgroup is empty because its processes are in a child** — systemd-udevd
- an **instance of a template**, whose file on disk is named for the template — `user@1000.service`
  from `user@.service`, and under `user.slice` rather than the system one

With all three handled, the list matches `systemctl list-units --state=running` exactly on this
machine: 22 against 22, with no difference in either direction.

The control verbs still need the privileged helper (§68), and none of them is written.

# 42. Startup applications

- [ ] Columns: name · publisher · enabled · status · startup impact · startup CPU · startup disk I/O ·
      command · executable · location/source · user scope · last launch · file path · signature ·
      architecture

Sources:

- [ ] Windows: registered startup applications · Startup folders · Run registry mechanisms ·
      supported startup tasks
- [ ] Linux: XDG autostart · systemd user services categorised as login startup
- [ ] macOS: login items · user launch agents

Actions:

- [ ] Enable · disable · reveal executable · reveal configuration · properties · run now
- [ ] Delete entry — only where safe and explicit

- [ ] Impact categories are computed only where a **reliable measurement** exists; where it does not,
      the column says so rather than inventing a "Medium"

# 43. Users and sessions

- [ ] 🟡 Columns — user ✔ · login time ✔ · terminal ✔ · remote host ✔ · CPU ✔ · memory ✔ ·
      process count ✔. Full name, session id and type, state, idle time, last input, and the disk,
      network and GPU totals are not read
- [ ] Rows expand to the processes owned by that user — the totals are summed, but there is no
      tree to open
- [ ] Actions: disconnect session · log off · send notification where native · view processes ·
      copy session information

Reachable as `procman --users`, which answers two questions in one table: the sessions come from the
login records and the totals from the process list. A user with a session and no processes is a stale
login, and processes with no session belong to services.

An empty remote host means a login at the machine itself, which is a different answer from not
knowing where it came from — so it reads "local" rather than leaving the column blank.
- [ ] Destructive session actions require confirmation (§90)

# 44. Application / usage history

- [ ] Metrics: application · executable identity · publisher · CPU time · foreground CPU time ·
      background CPU time · disk read · disk write · network sent · network received ·
      metered-network usage · GPU time · average memory · peak memory · launch count ·
      cumulative runtime · last launch · history start date
- [ ] Controls: enable/disable history · reset · export · retention period
- [ ] **Off by default.** A file recording which applications a person ran and for how long is
      surveillance if it appears without being asked for, however useful it is when it is asked for.

---

# 45. Performance page

The page answers three questions, in this order, and its whole layout follows from them:

1. **What part of this machine is busy right now?** — answered by the rail, before anything is clicked
2. **How has it behaved over the last minute?** — answered by the graph
3. **What is the hardware underneath it?** — answered by the statistics below

It has to feel like a diagnostic instrument, not a dashboard: dense, quiet, and readable by somebody
who is not an expert but is looking for the one thing that is unusual.

## 45.1 Shape

- [x] Vertical resource rail down the left, one entry per resource, 210–240 px wide
- [ ] Each rail row carries a **sparkline** over the same history the main graph uses
- [ ] Each rail row carries a primary value and an optional secondary — `13 %  4.17 GHz`,
      `8.4 / 16.0 GB`, `↓ 11.6 KB/s ↑ 2.9 KB/s`, `42 %  57 °C`
- [ ] The selected row takes a pale accent background and a 2–3 px accent stripe down its left edge
- [x] One resource selected at a time
- [ ] The rail scrolls on its own when a machine has many disks, adapters or GPUs
- [ ] Header: resource name large and left, full hardware model smaller and right —
      `CPU                    Intel Core i9-14900K`
- [x] Large detailed graph, whose series follows the selection. Every resource's history is recorded
      whether or not it is on screen, so selecting a disk that has been idle for a minute shows that
      minute rather than starting blank
- [ ] Statistics in **two columns**: live measurements on the left, hardware specifications on the
      right — the two answer different questions and reading them as one list is what makes a
      performance page look like a data dump
- [ ] Engineering diagnostics collapsed below both, so the default state is not overwhelming
- [ ] Reference size 1280×780, minimum 900×600, graphs growing horizontally rather than leaving
      whitespace

## 45.2 Visual hierarchy

Four levels, and nothing may jump a level:

1. **Immediate status**, readable without reading — sparklines, percentages, the main graph
2. **Current measurements** — utilisation, clock, usage, throughput, temperature, fan
3. **Hardware specifications**, smaller — model, cores, capacity, interface, caches, driver
4. **Engineering diagnostics**, collapsed — context switches, page faults, kernel pools, I/O counters

## 45.3 Resources

- [x] CPU (§46)
- [x] Memory (§47)
- [x] Each disk (§48) — one section per device
- [x] Each network adapter (§49) — one section per interface
- [x] Each GPU (§50)
- [ ] Battery
- [ ] Optional sensors and devices

- [ ] The page opens on whatever is under the greatest meaningful load rather than always on the
      processor, with a setting to turn that off

## 45.4 Graphs

- [x] 60 seconds by default, newest on the right, moving right to left
- [x] Updated once a second
- [ ] Selectable history: 30 s · 60 s · 2 min · 5 min · 15 min
- [ ] Optional 500 ms mode
- [x] Engineering graticule — major and minor rules, graph paper rather than an analytics chart
- [x] Thin resource-coloured stroke over a translucent fill; no data-point markers; no animation
      that gets in the way of reading the current value
- [x] Scale label in the corner — `100%`, `16 GB`
- [ ] Axis labels: `60 seconds ago` at the left, `Now` at the right
- [ ] Hover tooltip carrying the timestamp and that instant's readings
- [ ] Hovering a graph reveals Pause · history · mode · expand in its top-right corner
- [ ] Pause freezes the drawing without clearing history or stopping collection, and says `Paused`
- [ ] Double-click or Expand opens an inspection view with current, minimum, maximum and average

### Scales

- [x] Fixed 0–100 % for CPU, GPU utilisation, disk active time and GPU power percentage
- [x] Dynamic for network throughput and disk transfer rate
- [ ] Scale hysteresis, so a dynamic scale does not rescale every second and make the shape unreadable
- [ ] Temperature on a stable hardware-appropriate scale rather than a dynamic one

## 45.5 Colour

Each resource owns one accent, used for its sparkline, its main graph, its selection stripe and its
numeric highlights — so the eye can follow one resource across the whole window.

| Resource | Accent | | Resource | Accent |
|---|---|---|---|---|
| CPU | blue/cyan | | Temperature | red/coral |
| Memory | purple | | Fan | magenta |
| Disk | green | | Power | lime |
| Network | orange | | I/O | yellow |
| GPU | teal | | | |

- [ ] The palette above, which is not the one in use — the plots are currently an instrument's
      green-on-black throughout (§7.2), and the two ideas have to be reconciled rather than layered
- [x] Every accent is overridable from the settings file (§67)

## 45.6 Missing readings

- [x] A sensor that cannot be read never shows a zero
- [x] It carries a tooltip saying why, from `Humanize.Explain`
- [ ] A category the hardware does not have at all has its graph hidden rather than emptied — a GPU
      with no fan sensor shows no fan graph

**Deliberately not a single `—`.** Five reasons a value is missing are five different situations, and
collapsing them loses the one thing a reader needs: `n/a` means this OS cannot report it, `n/i` means
it can and we do not read it yet, `—` means this user may not, `?` means the counter contradicted
itself, `…` means one more sample is needed. "Not reported by this device" is only ever one of those
five, and telling somebody to run as root reads very differently from telling them to wait a second
(§5.3).

## 45.7 Density

- [ ] **Comfortable** — more spacing, larger graphs, advanced statistics collapsed
- [ ] **Compact** — tighter rows, more graphs at once, advanced statistics left open

## 45.8 Commands

- [ ] Right-click a resource: copy current values · copy full diagnostics · pause · change graph ·
      show kernel times · show logical processors · open hardware details
- [ ] Copy diagnostics — a plain-text snapshot of the machine, which is what makes a support
      conversation possible
- [ ] `Ctrl+1`…`Ctrl+6` select overview, CPU, memory, disk, network and GPU
- [ ] `Space` pauses, `F5` resumes, `Ctrl+C` copies the selected statistics

## 45.9 Accessibility

- [ ] Nothing is identified by colour alone — every graph carries a visible text heading
- [ ] Keyboard navigation reaches every graph control
- [ ] Screen-reader labels
- [ ] High-contrast support, and lines that stay distinguishable in it
- [ ] Colour-blind-safe differentiation
- [ ] 100–200 % UI scaling

## 45.10 Cost

The monitor must not distort what it is monitoring (§71):

- [x] Under 1 % CPU while idle
- [ ] Under 150 MB resident — not measured
- [x] No kernel driver required for anything here

The page is modeless and refreshed from the main window's sample tick. It was modal and painted
once — a performance page whose numbers never moved, which no screenshot of it would have shown as
wrong, and which the tests now catch.

- [x] Reachable by clicking any of the plots along the top — which is where somebody looking at a
      total goes for the detail behind it — and from View ▸ Performance for people who would rather
      not find that by accident

# 46. CPU performance

- [x] Processor name / model
- [x] Architecture
- [x] Utilisation
- [x] Current speed / frequency — averaged across the logical processors, and separate from the base
      speed because it moves constantly on any modern part
- [x] Base speed — `base_frequency` where the kernel has it, parsed out of the model name otherwise
- [x] Sockets / packages
- [x] Physical cores — counted per socket, because both sockets have a core 0 and counting the bare
      ids reports two cores on an eight-thread machine
- [x] Logical processors
- [x] NUMA nodes
- [ ] 🟡 Virtualisation support / state — a hypervisor is detected and named from the DMI product
      name; whether the CPU *supports* virtualisation is not read
- [x] L1 / L2 / L3 cache — data and instruction separately at L1
- [x] Process count
- [x] Thread count
- [ ] Handle / resource count
- [x] Uptime
- [ ] Context switches per second
- [ ] Interrupts per second
- [ ] System calls per second
- [ ] DPC-like kernel activity

The three rate counters, plus their cumulative totals, belong in the collapsed **system counters**
section of §45.2 level 4 rather than beside the utilisation — they are diagnostics, not status.

Layout (§45.1): utilisation and speed are the two largest figures on the page, with processes,
threads, handles and uptime beside them in the live column, and everything from base speed down to
the cache sizes in the hardware column.
- [x] Load averages

Read once and cached: none of it changes between samples, and walking the cache directories every
second would be indefensible against §71.

Windows answers the same questions by different means: `GetLogicalProcessorInformationEx` for cores,
packages, NUMA nodes and caches, and `CPUID` leaves 0x80000002–4 for the brand string. Asking the
processor rather than the registry avoids a dependency the trimmer would have to be told about, and
returns the same string, because that is where Windows got it. ARM64 has no CPUID and reports that it
does not know rather than inventing a name. The live clock speed is still `n/i` there — Windows
exposes it only through a performance counter or a power interface, neither worth opening to describe
a machine.

Graph modes:

- [x] Overall
- [x] Logical processors — a checkbox on the processor page, not a rail entry per core: twenty cores
      would put twenty entries in the rail and bury the disks under them, and "overall or per core"
      is one switch rather than twenty destinations. Ticked, the plot becomes a grid of one small
      plot per core; the terminal has no checkbox and prints them all
- [ ] Physical cores
- [ ] NUMA nodes
- [x] User vs kernel / system — plotted together, total in green with kernel over it in red, on the
      processor page and on every core

Kernel time is system time **plus hard IRQ plus soft IRQ**, not system time alone: a saturated
network adapter is almost entirely soft IRQ, and counting only system time would show a nearly idle
kernel beside a fully busy core. User and kernel deliberately do not add up to utilisation — steal
time is busy from this machine's point of view and belongs to neither, so a guest losing a third of
its core to the hypervisor shows it as the gap rather than hidden in one of the two. I/O wait is in
none of the three, because nothing is running during it.

`/proc/cpuinfo` and `CPUID`/registry supply make, model and speed; DMI (`/sys/class/dmi`) supplies
socket and cache topology.

# 47. Memory performance

- [x] Total physical memory
- [ ] Usable memory
- [x] Used / in use
- [x] Available
- [ ] Free (as distinct from available)
- [x] Cached
- [ ] 🟡 Buffers
- [x] Committed
- [ ] Commit limit
- [x] Swap / pagefile used
- [x] Swap / pagefile total
- [ ] Compressed
- [ ] Kernel memory
- [ ] Paged pool total
- [ ] Nonpaged pool total
- [ ] Hardware reserved
- [ ] Memory pressure
- [ ] **Memory speed** — refused rather than guessed: `—`, with the reason
- [ ] **Channels**
- [ ] **Form factor** — as above
- [ ] **Slots used / available** — as above
- [ ] NUMA distribution

The four in bold are the Task-Manager-style hardware facts. They come from DMI/SMBIOS type-17
records: `/sys/firmware/dmi/tables` on Linux, which is root-readable and therefore a helper call, and
`GetSystemFirmwareTable` on Windows.

The page renders from `PerformanceReport`, which is data rather than a window — so `--host` on a
terminal and the desktop view show the same figures by construction, and the content is unit-tested
in a way a window is not (§58).

Graphs:

- [x] Physical-memory usage — 60 seconds, scaled to installed memory rather than to 100 %
- [ ] Committed memory
- [ ] **Composition bar** — a single horizontal bar under the graph, split into in-use, modified,
      cached and free, each segment in its own shade of the memory accent, each naming its exact
      value on hover. It is the one picture that explains why a machine with "no free memory" is
      fine, and the reason people open a memory page at all
- [ ] Memory pressure
- [ ] Swap
- [ ] Cache

Advanced counters, collapsed (§45.2 level 4): commit current/peak/limit · file cache
current/peak/minimum/maximum · page faults by kind — total, copy-on-write, transition, demand-zero,
cache · kernel pools, paged and non-paged, with their allocation and free counts.

# 48. Disk performance

For each physical disk or device:

- [ ] 🟡 Friendly name ✔ · model ✔ · media type ✔ (rotational or solid state, and *unknown* when
      the kernel does not say) · capacity ✔ · serial, bus/interface, volumes and the system-disk and
      page-file indicators are not read ·
      capacity · formatted capacity · mounted volumes · system-disk indicator · page/swap indicator
- [ ] 🟡 Active time ✔ · read rate ✔ · write rate ✔ · read IOPS ✔ · write IOPS ✔ · cumulative reads
      and writes ✔ · average response time, queue length and per-direction latency are not ·
      queue length · read latency · write latency · cumulative reads · cumulative writes

Sources: `/proc/diskstats` — one file for the whole machine, which is what makes this affordable on
the sampling path where the per-process figures of §18 are not. Whole devices only: a partition is
charged the same I/O as the disk holding it, so counting both reports twice the traffic. `/sys/block`
decides which is which, because the name cannot — `nvme0n1` ends in a digit and is a whole disk.

Windows needs `IOCTL_STORAGE_QUERY_PROPERTY` and the disk performance counters, and has neither.

Graphs — two, not one:

- [x] **Active time**, fixed 0–100 %
- [ ] **Transfer rate**, reads and writes as separate lines on a dynamic scale whose unit follows the
      traffic (KB/s → MB/s → GB/s). Active time says a disk is busy; only the transfer rate says
      whether that is a hundred large reads or a hundred thousand small ones

- [ ] Optional hardware-health plugin: temperature · wear · SMART/NVMe health · remaining life
- [ ] Hardware health is **separately permissioned**, because platform coverage varies too much for
      it to be a baseline promise

# 49. Network adapter performance

- [ ] 🟡 Name ✔ · state ✔ · description, type and interface index are not
- [ ] 🟡 Link speed ✔ where the kernel reports one — absent on Wi-Fi and on anything virtual, and
      reported as unknown rather than as a dead link. Utilisation needs the link speed, so it is not
- [x] Send rate · receive rate · errors · drops — packets are counted and their rate is computed
- [ ] 🟡 MTU ✔ · MAC address ✔ · the addresses and the gateway are not
- [ ] DNS servers where readable
- [ ] Wi-Fi SSID and signal strength where permitted
- [ ] Graph modes: total · send vs receive — receive and send drawn as distinguishable lines within
      the one network accent, on a dynamic scale that reads Kbps, Mbps or Gbps as the traffic requires
- [ ] Wi-Fi pages additionally carry SSID · protocol · signal strength · channel · band

Sources: `/sys/class/net/*` and `/proc/net/dev`; `GetIfTable2` and `GetAdaptersAddresses`.

# 50. GPU performance

- [x] Adapter name · vendor · device · driver
- [ ] 🟡 Memory totals · dedicated memory · shared memory · current dedicated usage · current shared
      usage — dedicated total and in-use, plus how busy the memory bus is; shared memory is not read
- [x] Overall utilisation
- [ ] Per-engine utilisation: compute · graphics · copy · encode · decode
- [x] Temperature, power and clock where available — draw against the ceiling, the momentary cap
      beside it, core and memory clocks, and the fan
- [x] **OS-provided data is separated from vendor-specific sensor extensions**, and vendor plugins
      cannot stall a sample or crash the sampler

`/sys/class/drm` holds one entry per card and one per connector — `card0` beside `card0-HDMI-A-1` —
and only the cards are adapters. Each becomes its own section, read on demand by the page that shows
it and never from the sample loop, whose allocation budget is a build gate (§5.4).

**sysfs alone answers almost nothing, and concluding from that that the numbers do not exist was a
mistake.** AMD publishes `gpu_busy_percent`, the VRAM figures and a hwmon node; Intel's `i915`
publishes its render clock and nothing else, its engine busyness living behind a perf counter that
needs a privileged open; NVIDIA's driver publishes nothing there at all. It is the **vendors' own
libraries** that hold the readings, which is why every tool with a real GPU page loads one — a card
that rendered here as a column of `n/i` was at the time sitting at 100 % with 15.9 of its 16 GB in
use, 54 °C and 27 W.

`libnvidia-ml.so.1` is therefore loaded the way GTK is: optional, tried once, and remembered as
absent when the first call throws. Devices are matched by **PCI address** and not by index — NVML's
enumeration order is its own and need not match the kernel's `cardN` numbering, so matching by
position would confidently attribute one card's readings to another on any machine with two.

**Power is shown as draw against the ceiling**, because thirty watts means something entirely
different at a forty-watt cap than at a four-hundred-watt one. The ceiling is the card's maximum and
not the enforced limit: a laptop's dynamic boost moves the enforced figure around constantly and the
instantaneous draw routinely exceeds it, so using it as the denominator renders "28.5 W of 20.0 W"
and reads as a bug. The enforced cap gets its own row, because a card clamped to 20 W of a possible
130 W is the entire explanation for a 210 MHz clock at full utilisation.

Where a reading still cannot be had it carries `NotImplementedHere` and renders `n/i`. None of the
readings on `GpuInfo` has a default value, deliberately: `default(Counter)` is a *confident zero*, so
a caller allowed to leave one out would claim a card draws no power and has a nought-watt ceiling
(§5.3).

The vendor is named from the PCI id and the device is not, for cards no library recognises. The full
PCI id database is megabytes of names that go stale the month after a build, and a program that ships
one tells people their new card is unknown hardware.

ROCm SMI for AMD and the i915 perf counter for Intel are the two remaining gaps; Windows and macOS
report no adapters yet, which is a gap and not a claim that the machine has none.

## 50.1 Layout

The GPU page is deliberately the most graph-heavy in the program, because a GPU is the one component
whose six readings genuinely move independently — a card can be at full utilisation and cold, or idle
and hot, and only seeing both at once explains either. Stacked, in this order, each with its own
60-second history:

- [ ] **Utilisation**, 0–100 %, with an optional selector for 3D · compute · copy · decode · encode
- [ ] **Dedicated memory**, scaled to the card's VRAM — `7.2 GB / 16.0 GB`
- [ ] **Shared memory**, in a lighter shade of the GPU accent — `134 MB / 31.9 GB`
- [ ] **Power**, 0–100 % of the ceiling, labelled with the absolute figure too — `28 % · 89 W`
- [ ] **Temperature**, on the red accent so it never reads as another utilisation figure
- [ ] **Fan**, in RPM, one line per fan where the card exposes several. Zero RPM is a flat line at
      the bottom and not an error: it is what a modern card does when it is cool

A card that exposes no fan sensor shows **no fan graph** (§45.6) rather than an empty one.

Statistics below, in the two columns of §45.1 — live: utilisation, dedicated and shared memory,
temperature, fan, power. Hardware: model, driver version and date, PCI location, resizable BAR, and
where supported the core and memory clocks, voltage, power limit and temperature limit.

# 51. System activity view — expert

- [ ] Top CPU processes
- [ ] Top memory processes
- [ ] Top disk readers
- [ ] Top disk writers
- [ ] Top network senders
- [ ] Top network receivers
- [ ] Top GPU processes
- [ ] Process creation rate
- [ ] Process termination rate
- [ ] Context-switch rate
- [ ] Thread creation
- [ ] Disk activity
- [ ] Network activity
- [ ] Clicking any top-process entry navigates to that process

# 52. Disk activity view

- [ ] Columns: process · PID · operation · file/path · read/write · offset · size · latency ·
      throughput · timestamp
- [ ] Continuous high-volume tracing is explicitly enabled by the user
- [ ] …and **exposes an overhead warning**

eBPF on Linux (a privileged helper call); an ETW session on Windows.

# 53. Binary inspector

Read-only. PE, ELF and Mach-O.

- [ ] Summary · headers · sections/segments · imports · exports · dependencies · symbols · strings ·
      resources · signatures · hashes · debug information · security properties
- [ ] PE: DOS/NT headers · optional header · data directories · load configuration · delay imports ·
      CLR metadata presence · manifest · Authenticode · ASLR/DEP/CFG/CET flags
- [ ] ELF: program headers · section headers · dynamic section · interpreter · symbols ·
      relocations · build ID
- [ ] Mach-O: load commands · segments · dylib dependencies · code signature · entitlements
- [ ] **Read-only in baseline releases** — this is a viewer, not a patcher

# 54. Run / launch new process

- [ ] Executable / command · arguments · working directory · environment overrides
- [ ] Selected user / account where supported
- [ ] Elevation
- [ ] Launch suspended — expert mode
- [ ] Priority · affinity
- [ ] Terminal / console behaviour · shell execution · environment inheritance
- [ ] Recent commands are optional and clearable
- [ ] **Passwords and secrets are never retained** — a "run as" dialog that remembers a credential is
      a credential store nobody audited

# 55. System power and session actions

- [ ] Lock · log out · sleep · hibernate · restart · shutdown
- [ ] **Isolated from ordinary process actions** — meaning not in the same menu as "End task": very
      different consequences, very similar mouse positions
- [ ] Confirmation according to user settings

---

# 56. Search

Substring matching over name, PID, user and command line works in both front-ends. The query
language does not exist.

- [x] Plain substring search
- [x] Every field is addressable by a stable key
- [x] `field:value`
- [x] `field=value`
- [x] Comparison operators — `>` `>=` `<` `<=` `!=`, with or without the colon, spaced or not
- [x] Quoted strings — always literal, so a search for `name:chrome` as *text* is possible
- [x] Boolean AND / OR / NOT — words or `&&` `||` `!`, with `(` `)`, and AND binding tighter than OR
- [x] Regex form — `/pattern/` over the usual fields, `field:/pattern/` over one
- [x] Unit-aware quantities — `1GiB` `500MB` `1K` `50%` `500ms` `1.5s`
- [x] Search over hidden as well as visible fields — every registered field, shown or not

Examples that must parse:

```
chrome            pid:1234         user:alice        cpu:>50
memory:>1GiB      port:443         remote:10.0.0.5   unsigned:true
path:/opt/myapp   service:sshd     state:suspended   runtime:dotnet
```

- [x] **The same query syntax works in GUI, TUI and CLI.** `ProcessQuery` lives in
      `ProcessManager.Core` and every front-end filters through `ProcessView.TextFilter`, so no
      front-end has its own dialect. Every field added to the registry becomes filterable for free.

Two decisions worth recording, because both could reasonably have gone the other way:

- **A half-typed query degrades to a substring search rather than matching nothing.** Somebody typing
  `chrome:` is midway through a working query, and blanking the list at every keystroke makes an
  interactive box unusable. `--filter` is the opposite: it refuses the query and names the problem,
  because a script that silently matched nothing would be worse than one that stopped.
- **An unknown value matches no comparison at all** — not `> 0`, not `== 0`, and not `!= 5`. Saying
  a process's memory is "not equal to 5" is a claim about a number we do not have (§72.3).

The unit is taken from the field, which is why `1G` is 1073741824 against a byte field and
1000000000 against a count. Spelling it `GiB` or `GB` overrides the guess.

---

# 57. TUI requirements

The TUI is a first-class product, not a simplified dashboard. It exposes the same field registry and
all applicable actions.

**Implementation.** A double-buffered diff renderer: each frame is compared against the last and only
the changed cells are written, with ANSI attributes coalesced per run. This is what makes it usable
over SSH.

## 57.1 Layout

- [x] Top status bar
- [ ] 🟡 Left view selector or compact tab row
- [x] Primary table / tree
- [ ] Optional lower pane
- [x] Bottom command / help bar

Responsive breakpoints:

- [x] Full desktop terminal
- [x] Medium SSH terminal
- [ ] 🟡 Narrow terminal

## 57.2 TUI process view

- [x] Columns adapt automatically to width
- [ ] Horizontal scroll
- [ ] Column sets
- [x] Hide / show columns
- [ ] Resize columns
- [ ] Pin first column
- [ ] 🟡 Switch friendly / tree / flat mode — tree and flat only

## 57.3 Keyboard model

- [x] `↑/↓` or `j/k` move
- [x] `←/→` or `h/l` collapse/expand
- [x] `Enter` properties / open
- [ ] `Space` select
- [x] `/` search
- [ ] `f` advanced filter
- [x] `s` sort
- [x] `c` columns
- [x] `p` pause
- [x] `r` refresh
- [ ] `x` action menu
- [x] `t` terminate / end-task menu
- [x] `S` suspend / resume
- [ ] `n` network
- [ ] `m` modules
- [ ] `h` handles (contextual)
- [ ] `T` threads
- [ ] `g` graphs / performance
- [x] `?` help
- [x] `q` close / back / quit according to depth
- [ ] Bindings are customisable

## 57.4 TUI graphs

- [x] Block graphs — the U+2581–U+2588 eighth-block ramp
- [ ] Braille graphs where supported
- [x] Sparklines
- [ ] Textual min/avg/max/current fallback
- [x] **No information exists only as graphical colour**
- [x] ASCII fallback when the locale or terminal cannot show blocks

The block ramp is locale-dependent, which broke CI once: the golden frame was generated under a UTF-8
locale and compared under `LANG=C`. There is now an explicit `UseBlockCharacters` switch, pinned in
the capture and asserted by a test that checks both ramps differ *and* each is stable.

## 57.5 Mouse

- [ ] Select
- [ ] Scroll
- [ ] Pane resizing
- [ ] Tab selection
- [ ] Context / action menu
- [x] Keyboard functionality remains complete without a mouse

---

# 58. GUI / TUI parity contract

- [ ] 🟡 Every view declares its GUI representation, TUI representation, CLI equivalent, canonical
      fields and canonical actions
- [ ] A feature is not complete until GUI and TUI parity exists, unless explicitly marked
      "GUI-only interaction"

Permitted GUI-only interactions: locating a native desktop window by dragging a crosshair; native
drag and drop.

- [ ] The information such a feature retrieves is still accessible elsewhere

---

# 59. CLI

- [x] `procman ps`
- [x] `procman ps --tree`
- [x] `procman ps --columns pid,name,cpu,memory` — with `--format` for the six formats
- [x] `procman ps --filter 'cpu > 50'` — as `--filter`, plus `--help-fields` listing every
      field, its aliases and the filter grammar, generated from the registry so it cannot drift
- [ ] `procman process 1234`
- [ ] `procman process 1234 threads`
- [ ] `procman process 1234 modules`
- [ ] `procman process 1234 handles`
- [ ] `procman process 1234 network`
- [x] `procman kill 1234`
- [x] `procman suspend 1234`
- [x] `procman resume 1234`
- [x] `procman service list` — as `--services`
- [ ] `procman net`
- [x] `procman --host` — the §96 summary, which is `perf cpu` without the graph
- [x] `procman --startup` — what will run at login
- [x] `procman --users` — who is logged in, and what their processes cost
- [x] `procman --services` — which services exist and which are running
- [ ] `procman perf cpu`

Output formats:

- [x] human
- [x] table
- [x] JSON
- [x] JSON Lines
- [x] CSV
- [x] TSV
- [x] Stable, versioned JSON schemas — every document carries `"schema": 1`, bumped when a key is
      renamed or removed. The keys are the registry keys now rather than a second camel-cased set
      kept alongside them, which is the rename this version records.

# 60. Local API

- [ ] Snapshot queries · entity details · historical metrics · actions · event subscription
- [ ] Transport: local IPC — Unix domain socket or named pipe
- [ ] Optional localhost HTTP/gRPC layer
- [x] **The privileged helper exposes no network listener** — it speaks length-prefixed binary frames
      over redirected stdio and nothing else

# 61. Exports

- [x] CSV · TSV · JSON · JSON Lines · Markdown table · plain text
- [ ] 🟡 Scopes — the visible table and all matching rows are done, through `--columns` and
      `--filter`; selected cells and rows need a selection model, and the process and system reports
      and the historical metrics need §26 and §85
- [ ] Process-report export includes timestamp and host metadata

**Machine formats carry raw exact values; human formats carry what the screen shows.** CSV, TSV,
JSON and JSON Lines write bytes as bytes and nanoseconds as nanoseconds — a spreadsheet cannot sum
"1.5K", and a number rounded to one decimal is not the measurement any more. Text and Markdown write
"1.5K", because a person is reading them. That split is what makes §76 true rather than aspirational.

An unknown value is an empty cell and a JSON `null`, never a zero and never the placeholder glyph —
which would turn "we could not read this" into the literal string "—" in a column of numbers.

# 62. Snapshot / diagnostic bundle

- [ ] Bundle contains: system summary · process list · process tree · service list · network list ·
      startup list · selected process details · performance counters · version · collection timestamp
- [ ] **Before export the UI warns about sensitive fields** — usernames, command lines, environment
      variables, paths, IP addresses
- [ ] The user can redact categories

# 63. Event timeline

- [ ] Records: process start · process exit · service state change · connection appeared/disappeared ·
      high CPU alert · high memory alert · executable/signature change · user action ·
      privilege escalation of ProcessManager itself
- [ ] Columns: time · event · process · PID · details · severity/category
- [ ] Configurable retention

# 64. Notifications

- [ ] Process started · process terminated · named process started
- [ ] CPU / memory / disk / network above threshold
- [ ] Service stopped · process became unresponsive
- [ ] Unsigned process started · reputation warning
- [ ] Rules are explicit and stored locally

# 65. Tray / menu bar

- [ ] Indicators: CPU · memory · disk · network · GPU
- [ ] Click opens a compact popover with current values and top CPU/memory/disk/network processes
- [ ] Double-click opens the full application
- [ ] Individual indicators can be enabled and disabled

# 66. Persistent process notes and rules

- [ ] Attach: note/comment · colour · category · expected publisher · preferred priority ·
      preferred affinity · preferred I/O priority
- [ ] Matching by: executable path · executable hash · process name · command-line pattern · signer
- [ ] **Automatic application of scheduling settings is opt-in**

# 67. Settings

The file is `key=value` lines at the platform's own config location — `$XDG_CONFIG_HOME/procman/`
or `~/.config/procman/` on Unix, `%APPDATA%\procman\` on Windows — and it is meant to be edited by
hand: every value in it is a field key, a plain number or a `#rrggbb`, and `--help-fields` lists the
field keys. Two rules make that safe — a line that cannot be parsed leaves its setting at the default
rather than failing the file, and a key this build does not understand is written back out untouched
so an older build cannot eat a newer one's settings.

**It saves itself.** The window used to require `--save-settings` from a terminal, which meant every
preference set through the window was gone by the next start. The saver runs from the sample tick
rather than from each change, so a window being dragged is one write and not a hundred, and it
renders the settings and compares them with what was last written, so a tick that changed nothing
touches no disk at all. A write that fails is retried on the next tick and never reported: somebody
diagnosing a machine whose disk is full is exactly the person who must not be interrupted by a dialog
about a preferences file (§81).

- [ ] **General** — launch behaviour · start minimised · start at login · default page ·
      confirm destructive actions · auto-elevation behaviour
- [ ] 🟡 **Appearance** — every colour of §7.1's categories and of the plots is a `color.<name>` line,
      and the window's size and splitter are remembered; theme, density, font, icon size and row
      height are not
- [x] **Refresh** — the interval persists, and the window writes it back when it is changed
- [x] **Processes** — tree-or-flat, the sort column and its direction all persist
- [x] **Columns** — saved column sets, and the columns each front-end opens with
- [ ] **Symbols** — enable resolution · search paths · cache directory
- [ ] **Reputation** — disabled by default · provider configuration · privacy disclosure
- [ ] **History** — enable persistence · retention · storage size
- [ ] **Privacy** — telemetry · crash reporting · recent commands · saved searches
- [ ] **TUI** — key bindings · mouse · colours · Unicode/Braille graphs
- [ ] **Advanced** — expensive collectors · debugging functionality · plugins · experimental APIs

---

# 68. Privilege model

- [x] Starts with ordinary user rights
- [x] Views display as much information as is accessible
- [x] The flow is: user requests action → the program explains why elevation is required →
      the privileged helper performs one narrowly scoped operation → the result returns
- [x] **Users are never encouraged to run the whole GUI as administrator or root**
- [x] Privileged communication authenticates the requesting local user
- [x] …and is structurally immune to command injection

## 68.1 Shape

A separate executable (`ProcessManager.Elevated`), launched on demand through `pkexec` or a UAC
prompt, speaking length-prefixed binary frames over redirected stdio. It is not a daemon, it holds no
socket, and it exits when the client does.

- [x] **Opcode allowlist.** The helper implements a fixed, small set of operations. There is no
      "run this command" opcode, so there is nothing to inject into: the wire format carries an
      opcode and typed arguments, never a string that becomes a command line.
- [x] **Identity revalidation.** Every request revalidates the target against `ProcessKey`, so a PID
      that was recycled between the request and the action cannot be acted on by mistake.
- [x] A polkit policy file ships in `packaging/` so the prompt names the action rather than saying
      "an application wants to run as root"

## 68.2 Degradation

- [x] Every capability the helper would provide has a defined unprivileged fallback, and the UI shows
      `—` with an explanation rather than an error

---

# 69. Action safety classification

Every mutation has a class.

- [ ] 🟡 **Class 0 — read-only.** Copy, search, properties, reveal path. No confirmation.
- [ ] 🟡 **Class 1 — reversible or low impact.** Change priority, change affinity, suspend.
      Optional confirmation.
- [ ] 🟡 **Class 2 — potential data loss.** Terminate process, stop service, log out user, disable
      startup item. Confirmation configurable, default enabled for high-value and system targets.
- [ ] **Class 3 — expert / unsafe.** Close a foreign process's resource, terminate a thread, unload a
      module, modify process memory. Always an explicit warning.
- [ ] System-critical process actions display additional warnings and may be blocked where the OS
      itself prohibits them

The classes exist in the action broker; the confirmation policy that reads them does not.

# 70. Reputation and signature verification

- [ ] The program distinguishes, and never conflates: hash calculation · local signature verification ·
      trust-chain verification · online reputation query · file submission

Status vocabulary — exactly these, no synonyms:

- [ ] Verified
- [ ] Valid but untrusted chain
- [ ] Unsigned
- [ ] Invalid signature
- [ ] Revoked
- [ ] Expired
- [ ] Verification error
- [ ] Not checked

- [ ] Online providers are plugins or integrations with explicit privacy controls

---

# 71. Performance requirements

With deep tracing, reputation checking and symbol resolution disabled.

## 71.1 Startup

- [ ] 🟡 TUI first useful process list < 300 ms
- [ ] 🟡 GUI first useful process list < 750 ms
- [x] Expensive metadata fills asynchronously

Neither startup figure is measured yet; both are believed met on the strength of the sampling numbers
below, which is not the same thing as knowing.

## 71.2 Sampling overhead

- [x] Median collector CPU below 0.5 % of one modern core at 1 s refresh
- [x] Sustained below 1 % at ordinary process counts

Measured, per 1000 processes: **33 ms of CPU locally, 51 ms on a 4-core CI runner.** The build gate
is set at 90 ms so it fails on a regression rather than on runner variance; the *target* remains
25 ms and is honestly recorded as unmet.

The road here is worth keeping, because every step was a measurement rather than a guess:

| Change | ms / sample | bytes / sample |
|---|---:|---:|
| Managed file APIs | 3000 | 199 598 |
| Raw syscalls, pooled buffers, UTF-8 byte paths | 120 | 1 100 |
| fd counting moved out of the sample loop (85 µs/process) | 62 | 320 |
| `smaps_rollup` made opt-in (0.8–4 ms/process) | 38 | 86 |

- [x] **Allocation budget: 0–86 bytes per sample.** A `ProcessRecord` is a mutable struct in a pooled
      array for exactly this reason.

## 71.3 Memory

- [x] TUI below 60 MB baseline
- [x] GUI below 150 MB baseline

## 71.4 UI responsiveness

- [x] No synchronous operation over 100 ms on the UI thread
- [ ] 🟡 Sort/filter of 10 000 rows under 100 ms — measured at 1000, not 10 000
- [x] Scrolling stays interactive while background metadata resolves

## 71.5 Large resource sets

- [ ] Virtualised tables
- [ ] 10 000 process rows
- [ ] 100 000 thread / module / result rows
- [ ] 1 000 000 handle / resource-search results via streaming or pagination

---

# 72. Data correctness requirements

## 72.1 Counter semantics

- [ ] 🟡 Every numeric metric documents its source, sampling interval, unit, cumulative-vs-instantaneous
      nature, normalisation algorithm, overflow behaviour and unavailable behaviour — this is the
      registry metadata of §5.1, and it is incomplete

## 72.2 Identity

- [x] PID reuse cannot attach an exited process's history to a new process
- [x] Canonical identity is PID **plus** process start time (`ProcessKey`)

## 72.3 Unknown is a value

The rule the rest of the document depends on. A field is never zero because we failed to read it.

- [x] `UnknownReason` is carried by every counter and rate: `NotPermitted`, `NotSupportedOnPlatform`,
      `NotImplementedHere`, `ProcessExited`, `NotSampledYet`, `CounterInvalid`

`NotImplementedHere` is the newest and the one this document most needed. "Windows has no cgroups"
and "we have not written the Windows token code yet" are different statements, and rendering the
second as the first tells the reader their machine cannot do something it can. One is a fact about
the operating system; the other is a fact about us. It shows as `n/i` against `n/a`, and it is what
lets a field be listed as available on a platform before it is built there.
- [x] Each renders distinctly — `—`, `n/a`, `×`, `…`, `?` — and each has a one-line explanation
- [x] Both front-ends render them through one shared formatter, so a value reads the same in the
      window and in the terminal
- [ ] 🟡 The explanation is reachable everywhere it is shown — currently only in the detail pane

# 73. Sampling and race conditions

Processes terminate between enumeration and inspection. This is normal, not an error.

- [x] A process exiting mid-sample is an ordinary outcome, never an exception path
- [x] Field states: available · permission denied · process exited · unsupported · collection
      failed · pending
- [x] Normal tables show an unobtrusive placeholder
- [ ] 🟡 Diagnostics expose the distinctions

Two race bugs found and fixed, both worth remembering: `/proc` permission failures surface at
`read(2)` and not at `open(2)`, so an `open` that succeeded proves nothing; and a `UNICODE_STRING`
buffer pointer from a bulk Windows query must be treated as an offset into the query buffer, not an
absolute address, or it dangles the moment the buffer moves.

---

# 74. Accessibility

GUI:

- [ ] 🟡 Full keyboard operation
- [ ] Logical tab order
- [ ] Screen-reader labels
- [ ] Scalable text
- [ ] High contrast
- [ ] System text scaling
- [x] Non-colour status indicators
- [ ] Graphs expose textual summaries

TUI:

- [x] Works without colour
- [x] Never conveys state solely by colour
- [x] ASCII fallback
- [ ] Conventional terminal screen readers

# 75. Localisation

- [ ] All user-visible labels are translatable
- [ ] Dates, times and decimal separators respect locale
- [x] Technical units use standardised forms consistently
- [x] Field IDs and CLI flags stay language-neutral and stable

Formatting is currently `InvariantCulture` everywhere, which is a deliberate placeholder: it is
wrong for display and right for tests, and it must not stay once §75 is implemented.

# 76. Units

- [ ] Memory / storage: automatic · binary KiB/MiB/GiB · decimal kB/MB/GB
- [ ] Rates: bytes/sec · optional bits/sec for network
- [x] Time adapts across ns · µs · ms · s · min · h · d
- [x] Raw exact values remain available through export

Today: binary units with single-letter suffixes, no setting. Counts scale in thousands rather than
1024s, because a cycles-per-second figure in kibicycles would be nobody's idea of a reading.

# 77. Logging

- [ ] 🟡 Records startup · backend initialisation · collector failures · privilege-helper requests ·
      user-requested mutations · plugin failures
- [ ] **Logs never include full environment blocks, passwords, memory content or secret command
      arguments** unless debug logging is explicitly enabled

# 78. Audit trail

- [ ] Optional: timestamp · user · action · target · result · privilege used
- [ ] Enabled for enterprise packaging later

# 79. Plugin system

- [ ] Plugins may contribute fields · collectors · detail tabs · reputation providers ·
      hardware sensors · exporters · actions · process classifiers · runtime inspectors
- [ ] Plugins declare supported OSes · privileges · network usage · collection cost · permissions
- [ ] **Untrusted plugins never inherit privileged-helper access**

A plugin system is in tension with §8.3's AOT rules: dynamic assembly loading is exactly what
NativeAOT forbids. The likely resolution is out-of-process plugins over the same framed protocol the
helper uses, which also makes the privilege boundary trivially enforceable.

# 80. Runtime inspection

.NET:

- [ ] Runtime version · managed/native classification · managed threads ·
      AppDomains/AssemblyLoadContexts · assemblies · managed stack frames · GC metrics where
      attach-safe

JVM, later:

- [ ] JVM version · Java threads · heap summary · loaded JAR/module information

- [ ] Runtime inspection stays modular, because attaching to a runtime can alter its behaviour

# 81. Recovery / degraded-system mode

A major reason to replace an ordinary task manager is to remain usable when the system is unhealthy.

- [ ] `procman --minimal` disables icons · signatures · symbols · module enumeration ·
      handle enumeration · history · GPU · hostname resolution · reputation · plugins
- [ ] Minimal mode prioritises PID · process name · CPU · memory · user · state · terminate
- [x] The TUI is suitable for recovery shells and SSH sessions

The single-file AOT binary is most of the way to this already: 3.2 MB, no runtime to load, no
dependencies to be missing on a broken machine.

---

# 82. Process aggregation

- [ ] Collapsed tree nodes may show aggregated CPU · resident memory · private memory · disk I/O ·
      network I/O · GPU · process count
- [ ] **Aggregated values are visibly marked as aggregates**, never presented as the parent's own usage

# 83. Process grouping

- [ ] none
- [x] parent tree
- [ ] application
- [ ] executable
- [ ] user
- [ ] session
- [ ] service
- [ ] container
- [ ] cgroup
- [ ] package
- [ ] publisher
- [ ] Aggregations follow canonical query rules

# 84. User-defined alerts

- [ ] Rule form: `process.name == "myservice" AND process.cpu.usage > 80% for 30s`
- [ ] Conditions: greater/less than · equals · contains · regex · appears · disappears · state changes
- [ ] Actions: visual notification · OS notification · log event
- [ ] Automatic process termination from alert rules is **not** enabled in baseline releases

# 85. Historical charts

- [x] Every eligible metric has an in-memory ring buffer
- [x] 60 seconds at high resolution by default
- [ ] Configurable 5 min · 15 min · 1 h
- [ ] Persistent history, separate and optional
- [x] **Graphs distinguish a missing sample from a zero value**

# 86. Selection persistence

- [x] Selection uses stable instance identity, not PID alone
- [ ] A selected process that exits stays selected during fade-out
- [ ] Its properties view changes status to terminated
- [ ] Retained historical graphs remain viewable until the window closes or retention expires

# 87. Process creation / termination visual behaviour

- [ ] Configurable new-process highlight duration
- [ ] Configurable exited-process highlight duration
- [ ] Scroll to new process
- [x] Do not move the selected process during transient sorting
- [ ] 🟡 "Stable sort while interacting" for fast-changing lists — the scroll anchor and the
      selection are held across every rebuild (§12); the sort itself still runs each sample

A subtree somebody collapsed now stays collapsed. It used to be reopened whenever a child appeared
under it — Process Explorer's gesture for "this process just forked" — which on a machine that forks
steadily meant the tree reopening itself every second and everything below it sliding down the
screen. The behaviour is still available as `ProcessTreeBinder.ExpandOnNewChild`.

# 88. Error handling

- [x] Access denied, process exited, protected process, unsupported field and stale connection
      **never** produce a modal dialog during normal refresh
- [x] Modal errors are reserved for explicit failed user actions, and name the target:
      "Could not terminate PID 412 because access was denied."

# 89. Context-aware actions

- [ ] 🟡 Only valid actions are enabled — Resume disabled unless suspended; Restart disabled where
      launch parameters cannot be reconstructed; Start service disabled while running; Stop service
      disabled where unsupported; Efficiency hidden where the OS lacks an equivalent; Close
      connection disabled where closure is unavailable

# 90. Confirmation dialog requirements

- [ ] 🟡 A confirmation states the action · target name · PID or service identifier ·
      likely consequence · whether child processes are included · whether unsaved data may be lost
- [ ] **No vague "Are you sure?" dialogs**

Model: *"Terminate `editor` (PID 4832)? This forcibly stops the process and may cause unsaved data to
be lost."*

---

# 91. Source-tool parity matrix

## Windows Task Manager

- [x] Processes
- [ ] 🟡 Performance
- [ ] App / usage history
- [ ] 🟡 Startup
- [ ] 🟡 Users
- [x] Details
- [ ] Services
- [ ] Run new task
- [x] End task
- [ ] Restart supported applications
- [x] Priority
- [ ] Affinity
- [ ] Efficiency / QoS
- [ ] Dumps
- [ ] Wait chains
- [x] Process search / filter
- [ ] Startup enable / disable
- [ ] User session control
- [ ] Service controls
- [ ] Kernel vs user CPU graph

## Process Explorer

- [x] Hierarchical process tree
- [x] Process ownership / account
- [x] Highly configurable columns
- [ ] Process properties window
- [ ] Lower pane
- [ ] 🟡 Handles
- [ ] 🟡 DLLs / mapped files
- [ ] Search for resource owners
- [ ] Signature verification
- [ ] Image metadata
- [x] CPU / memory / I/O histories
- [ ] 🟡 Per-process inspection

## System Informer / Process Hacker lineage

- [x] Detailed process tree
- [x] Resource highlighting
- [ ] 🟡 System graphs
- [ ] 🟡 Handles
- [ ] 🟡 Modules
- [ ] 🟡 Threads
- [ ] Stack traces
- [ ] Services
- [ ] 🟡 Network connections
- [ ] Disk activity
- [ ] GPU data
- [ ] Memory maps
- [x] Environment
- [ ] Security / token information
- [ ] File / resource ownership search
- [ ] Advanced process scheduling
- [ ] Detailed service control
- [ ] Binary inspection
- [ ] Runtime inspection
- [ ] Process notes / rules
- [x] Configurable columns

## DBC Task Manager

- [x] Clear simple process page
- [ ] Attractive resource graphs
- [ ] Resource selector
- [ ] 🟡 CPU · memory · disks · networks pages
- [ ] Services
- [ ] 🟡 Startup
- [ ] Users
- [ ] Minimal cognitive overhead for ordinary users

---

# 92. Cross-platform mapping

| Concept | Windows | Linux | macOS |
|---|---|---|---|
| Process | Process | Process | Process |
| Thread | Thread | Task/thread | Thread |
| Handle/resource | HANDLE | FD / kernel resource | FD / Mach resource |
| Loaded module | DLL / image | ELF / shared object | Mach-O / dylib |
| Service | SCM service | systemd unit | launchd job |
| Process group control | Job object | cgroup / process group | process group / launchd |
| Security identity | Token / SID | UID/GID/capabilities | UID/GID/audit credentials |
| Sandbox | AppContainer | namespace / seccomp / LSM | App Sandbox |
| Startup | Startup mechanisms | XDG / systemd user | Login Items / launchd |
| Resource ownership | Handles | FDs / maps | FDs / maps / ports |
| Performance source | Native counters | procfs / sysfs | Mach / BSD APIs |
| Affinity | Yes | Yes | Limited, different semantics |
| Process priority | Priority class | nice / scheduler | nice / QoS |
| Process dump | Yes | core dump | core / sample |
| Wait chain | Native | derived | partial / derived |
| Code signature | Authenticode | varies by package system | codesign |

- [ ] Platform differences are documented directly in UI help

# 93. Design requirements

- [ ] 🟡 Left navigation ✔ (on the performance page) · resource summary cards ✔ · clear table
      headers ✔ · restrained toolbar ✔ · large readable performance graphs ✔ · compact expert
      density ✔ · the lower pane is still a tabbed detail area rather than a switchable pane

The process list is dense the way the tools it imitates are: seventeen-pixel rows, faint rules
between them and between columns, the tree in the first column with the pid in its own, and Process
Hacker's own default column set — process, PID, CPU, I/O total rate, private bytes, user. The three
drawn histories are one click away in the column chooser rather than in the default set: they are
three of the widest columns in the catalogue and they push the numbers people read off the edge.

The rules are shaded from each row's own colour rather than being a fixed grey, so a rule over a
green "just started" row is a darker green and the category colour survives the grid.

Every secondary window — system information, a process's properties, the colour legend, the column
chooser — is a window and not the program. `Form.QuitsOnClose` defaults to true because the first
window shown owns the message loop, so each of these has to say otherwise; until it did, closing a
properties window closed the whole program. The plots and the core meters open the system information
window when clicked, which is where a reader who wants the detail behind a graph looks first — wired
to `MouseUp`, because the toolkit raises `Click` only from `PerformClick` and nothing a mouse does
ever reaches it. The column chooser follows its own frame when resized, and both owner-drawn lists
now draw a scrollbar when they have more rows than they can show.
- [x] Original branding and original icons
- [x] No Microsoft, Sysinternals or System Informer trademarks as navigation labels
- [x] No pixel-perfect clone of a copyrighted UI

The goal is **interaction familiarity and information density, not impersonation.** This also settles
the "match Process Explorer 1:1" question: 1:1 in *layout, column vocabulary and interaction* — the
tree, the lower pane, the column chooser, the colour legend, the double-click properties window — and
deliberately not in chrome, icons or artwork.

# 94. Default views

**Processes — Basic:** name · PID · status · CPU · memory · disk · network · GPU

- [ ] 🟡 Available except disk, network and GPU

**Processes — Expert:** process · PID · PPID · CPU · private memory · working set · I/O rate · user ·
start time · command line · signature

- [ ] 🟡 Available except signature

**Security:** name · PID · user · path · signer · signature · integrity/security context · elevated ·
protection · hash · reputation

- [ ] Blocked on §21

**I/O:** name · PID · read rate · write rate · read bytes · write bytes · other rate · I/O priority

- [ ] 🟡 Available except I/O priority

**Network:** name · PID · send · receive · connections · listening ports

- [ ] Blocked on §18

# 95. Copy behaviour

- [ ] `Ctrl+C` copies selected cells in cell-selection mode, otherwise selected rows using the
      visible columns
- [ ] Copy cell · copy row · copy field name/value · copy all properties · copy as JSON
- [ ] The TUI uses the same serialisation logic

# 96. Host summary

- [x] Hostname
- [x] OS and version
- [x] Architecture
- [x] Uptime
- [x] CPU — model, vendor, base and current speed, sockets, cores, threads, cache
- [x] Total memory
- [ ] Total disk
- [x] Active user
- [ ] Privilege state of ProcessManager itself
- [x] Virtualisation / container state
- [ ] Device model

All of it is reachable as `procman --host`. Written as a command before a page because a command can
be tested, scripted and pasted into a bug report, and a page cannot — the Performance page of §45
will render the same values.

# 97. Privacy requirements

- [x] **Default operation involves no external communication whatsoever.** Not update checks, not
      symbol servers, not reputation — the program contains no network client at all today, and any
      future one is opt-in per §70.
- [x] Crash reporting and usage telemetry are opt-in — there are none
- [x] Command lines, environment variables, memory, file contents, usernames and paths are treated as
      potentially sensitive

# 98. Security requirements

The privileged helper:

- [x] Exposes a minimal, fixed set of operations
- [x] Validates every identifier
- [x] **Rejects arbitrary command execution** — there is no opcode for it
- [x] Authenticates the client
- [x] Uses local protected IPC
- [x] Loads no plugins
- [x] Terminates when unused

Hostile process metadata — the program must not be attackable by what it reads:

- [x] Malformed binary metadata
- [x] Huge command lines
- [ ] Malicious icons (no icons yet)
- [x] Unusual Unicode
- [x] Invalid paths
- [ ] Corrupted symbols (no symbols yet)

The `comm` backscan in §6.2 is a small instance of this: a process may legitimately be named `)`, and
a naive parser hands the attacker the parse.

---

# 99. Testing strategy

**140 tests pass on every leg, under both a UTF-8 and a `C` locale.**

## Unit tests

- [x] Counter calculations
- [x] Delta handling
- [x] Unit formatting
- [x] PID reuse
- [x] Field registry — 14 tests, including the one that enforces §103
- [x] Filters
- [x] Sorting
- [ ] Export schemas

## Fixture replay

The technique that makes cross-platform testing possible: a captured `/proc` tree and a captured
Windows query buffer are replayed on **every** platform, so Linux parsing is tested on Windows and
Windows parsing on Linux.

- [x] Recorded `/proc` fixtures with golden expected output
- [x] Recorded `SystemProcessInformation` buffers, parsed by a type that is not platform-gated
- [x] Both the raw-syscall and portable-file-access paths run on every leg
- [x] Fixtures marked `-text` in `.gitattributes` — a Windows checkout once rewrote the line endings
      and gave a cgroup path a trailing `\r`

## Backend integration tests

- [x] Process lifecycle
- [x] Suspension
- [x] Termination
- [x] Priority
- [ ] Affinity
- [ ] 🟡 Services
- [ ] 🟡 Startup
- [x] Network
- [x] Modules
- [x] Descriptors
- [x] Privilege errors

## Performance tests

- [x] Sampling budget enforced as a build gate (§71.2)
- [ ] 10 000 processes
- [ ] 100 000 threads
- [ ] 1 000 000 resource rows
- [ ] Rapid process churn
- [ ] 100 % CPU load
- [ ] Low-memory state
- [ ] High-I/O machine

## UI tests

- [x] Sorting while data changes — three tests, added after the reordering bug in §12
- [x] Tree expansion
- [ ] Selected-process termination
- [ ] Lower pane
- [ ] 🟡 Column sets
- [ ] Accessibility
- [ ] 🟡 Keyboard operation

## TUI tests

- [x] Golden-frame comparison at fixed dimensions
- [ ] 🟡 80×24 · 120×30 · 160×50
- [ ] 256-colour
- [x] Monochrome
- [x] UTF-8
- [x] ASCII fallback

## Self-test

- [x] `procman --self-test` — 21 checks against live data, cross-validated against the .NET runtime's
      own view of the current process. Green on Linux and on a real Windows runner (NT 10.0.26100:
      145 processes, 8 threads, 47 modules, 159 handles of which 42 named, 149 environment variables).

## CI

- [x] 11 jobs, all green: build and test on Linux, Windows and macOS; the AOT single-file publish;
      the benchmark gate; the self-test on Windows
- [x] A Wine leg, advisory (`continue-on-error`) — it found the `SemaphoreFullException` in §6.1
- [x] Screenshots regenerate on manual dispatch and at release, not per push

---

# 100. Release plan

## Phase 1 — core replacement · **in progress**

- [x] Windows and Linux process enumeration
- [ ] macOS process enumeration
- [x] Common CPU and memory fields
- [x] Process tree
- [ ] 🟡 Flat and grouped modes — flat yes, grouped no
- [x] Terminate
- [x] Suspend / resume
- [x] Priority
- [ ] Affinity
- [ ] 🟡 Basic Performance page
- [ ] Network view
- [ ] Services read and control
- [ ] 🟡 Startup
- [ ] 🟡 Users / sessions — `--users`
- [x] GUI
- [x] TUI
- [ ] 🟡 CLI
- [ ] 🟡 Column registry
- [ ] 🟡 Filters and search
- [ ] Exports

## Phase 2 — Process Explorer parity

Process properties window · modules · handles/descriptors · lower pane · resource search ·
signatures · hashes · memory maps · threads · stacks · advanced column sets · dumps · wait analysis.

## Phase 3 — System Informer depth

Detailed security · token and capabilities · advanced service properties · runtime plugins ·
binary inspector · detailed GPU · disk activity · resource timeline · process annotations and
rules · advanced memory inspection.

## Phase 4 — observability

Persistent history · alert rules · diagnostic bundles · plugin SDK · local API · richer automation.

---

# 101. Definition of "replacement complete"

ProcessManager may claim to replace the named applications only when all ten are true:

- [ ] Every common Task Manager workflow can be completed without Task Manager
- [ ] Process trees, handles, modules and resource-owner search remove routine need for
      Process Explorer
- [ ] Advanced process, thread, security, network, service and memory inspection removes routine
      need for System Informer
- [ ] Performance dashboards are at least as approachable as DBC's or modern Task Manager's
- [x] GUI and TUI expose the same canonical information
- [ ] 🟡 Every unsupported platform-specific feature explicitly communicates why
- [x] Common actions work without running the whole program elevated
- [ ] 🟡 Recovery/minimal mode remains functional under significant load
- [ ] Data can be copied, exported and scripted
- [ ] The product is stable enough that administrators trust it while diagnosing an already
      unstable machine

# 102. Acceptance criteria for v1

v1 does not ship unless every one of these is true:

- [x] The process list updates continuously without losing selection
- [x] PID reuse cannot corrupt process identity
- [x] GUI and TUI report matching canonical counters within the sampling tolerance
- [ ] 🟡 CPU and memory metrics have documented semantics
- [ ] 🟡 The user can kill a process from GUI, TUI **and** CLI
- [x] The user can suspend and resume where supported
- [x] The user can inspect process path and command line
- [x] The user can inspect the process tree
- [ ] 🟡 The user can inspect active network endpoints
- [ ] 🟡 The user can inspect services — starting and stopping them is not written
- [ ] The user can manage common startup items
- [ ] 🟡 The user can inspect logged-in sessions — from the CLI; neither front-end has the view
- [ ] 🟡 The user can view CPU, memory, disk and network performance
- [ ] 🟡 The user can create and restore column presets — restoring works from the file and from
      `--columns @name`; creating one means editing the file, not a dialog
- [x] The user can search and filter by any registered field, visible or not
- [ ] 🟡 Tables remain usable with thousands of changing rows
- [x] Privileged actions work through the privilege broker
- [x] Lack of privileges does not crash or freeze views
- [ ] 🟡 The GUI exposes no data unobtainable from TUI or CLI
- [x] No external metadata is transmitted without opt-in
- [x] Unavailable platform fields are distinguishable from zero-valued fields
- [ ] 🟡 Destructive actions identify their exact target
- [ ] Minimal recovery mode operates independently of expensive collectors and plugins

---

# 103. Engineering rule — feature registration

**No field or action may be introduced inside a front-end.** This rule is what prevents feature drift,
and it is the reason §5.1's registry exists.

To add a **field**:

1. Define the canonical field ID
2. Define its semantics
3. Define the supported OS backends
4. Define unit and type
5. Define privilege and collection cost
6. Implement the collector
7. Register the formatter
8. Register sort and filter behaviour
9. Expose it in the GUI
10. Expose it in the TUI
11. Expose it in the CLI/API
12. Add the export schema
13. Add tests

To add an **action**:

1. Define the canonical action ID
2. Define its targets
3. Define platform support
4. Define required privileges
5. Define the safety class (§69)
6. Implement the backend
7. Implement it in the action broker
8. Implement the GUI control
9. Implement the TUI command
10. Implement the CLI command where appropriate
11. Add the audit event
12. Add tests

- [ ] 🟡 A CI check enforces this. Half of it is real: `EveryFieldInTheEnumIsRegistered` fails the
      build when a field is added to the enum without a descriptor, so steps 1–8 cannot be skipped.
      Steps 9–13 — GUI, TUI, CLI, export schema, tests — are still on the author to remember.

# 104. Internal object model

Entities: `Host` · `Cpu` · `Memory` · `Disk` · `Volume` · `NetworkInterface` · `Gpu` · `Process` ·
`Thread` · `Module` · `Resource` · `MemoryRegion` · `NetworkEndpoint` · `Service` · `StartupItem` ·
`User` · `Session` · `Window` · `Container` · `Job` · `SecurityContext` · `Binary` · `Event`

Every entity exposes:

- [ ] 🟡 Stable internal ID
- [x] Native ID
- [ ] 🟡 Creation timestamp where meaningful
- [ ] Source backend
- [ ] Capability mask
- [x] Collection status

Implemented today: `Host`, `Cpu`, `Memory`, `Process`, `Thread`, `Module`, `Resource`,
`NetworkEndpoint`, `MemoryRegion`. The rest do not exist.

# 105. API semantics

- [x] Collectors publish immutable snapshots
- [x] Reading: `OS backend → snapshot → derived metrics → query → renderer`
- [ ] 🟡 Mutating: `renderer/CLI → action request → validation → privilege broker → OS backend →
      result → audit event → snapshot refresh` — every stage exists except the audit event

This separation is what stops front-ends developing platform-specific behaviour.

---

# 106. Final product requirement

ProcessManager must feel **simple when the user wants a task manager and deep when the user wants a
system inspector**.

One application, and a natural progression from:

> "What is using my CPU?"

to *"Which thread is responsible?"* to *"What stack is that thread executing?"* to *"Which module
contains that frame?"* to *"Who signed that module?"* to:

> "Which files, sockets, services and memory regions does this process own?"

without changing tools.

And the same investigation, from a terminal session, using the same terminology, fields, identifiers,
filters and actions.

**That shared model — not visual cloning — is the defining requirement of the product.**
