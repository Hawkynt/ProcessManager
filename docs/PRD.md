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

**Counting, as of the last update:** **1012 of 1330 boxes are ticked** — 168 of 189 in the field
registry (§14–22), 844 of 1141 across the capabilities. A further 148 are marked 🟡, meaning some of
the work behind them is already done. §100 tracks the phases; §101 defines when this may be called
finished.

This paragraph is counted by hand and goes stale the moment two branches are in flight against this
file, which is exactly when somebody reads it. Recount before quoting it:

```sh
grep -c '\[x\]' docs/PRD.md && grep -c '\[ \]' docs/PRD.md
```

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

- [ ] 🟡 Replace everyday Task Manager workflows — process management, and views of services, startup
      and users; services can now be commanded from every front-end, and a startup entry can be
      turned on and off from the window. Neither is reachable from the terminal's own view, because
      the terminal has no services or startup view — only the process's own unit
- [x] Replace Process Explorer tree and lower-pane workflows — the pane is docked under the tree with
      overview, threads, modules, handles, environment and network, and pins to one process in a window
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
- [x] Remain usable with thousands of processes, threads, mappings, connections or handles — ten
      thousand processes flat and nested, a chain twenty thousand deep, and a million descriptors
      searched in 186 ms; the quadratic that used to be in the tree builder is gone (§99)
      — measured to 1000 processes (§71); 10 000 is untested
- [x] Permit customisation of columns, layouts, refresh intervals, highlighting, shortcuts and
      defaults — every one of the six, and all of it persists. The file understands forty-six keys
      plus two prefixes: columns for each front-end with their widths and pinned run (`columns.*`,
      `columnset.*`), the window's size, splitter and lower pane (`window.*`), the refresh interval,
      eight highlighting thresholds and the whole row palette (`heat.*`, `color.*`), the terminal's
      keys in a `keys.conf` beside it, and the defaults — grouping, sort, CPU convention, whether a
      single action asks first, how the performance page opens. Checked by round-tripping each kind
      through the file rather than by reading the list: a key this build does not understand comes
      back untouched too, so an older build cannot eat a newer one's settings
- [ ] 🟡 Support keyboard-driven operation throughout GUI and TUI — the terminal completely; the
      window nearly, and what is missing is named in §74 rather than left as "partial". Every menu
      item, the sort, the columns, the filter and the settings box have keys; the row tick and the
      splitter are reachable through the toolkit's own bindings, which three assertions now hold
      against the two ways they silently stop arriving (a control out of the tab order, and a menu
      accelerator claiming the key before the focused control sees it)
- [x] Permit information to be copied or exported without screenshots

## 3.2 Secondary — SHOULD

- [ ] Serve as a lightweight incident-response triage utility
- [ ] Serve as a developer debugging companion
- [x] Provide long-running metric history when requested — §44's usage record: what each program has
      cost this machine across every time it has been run, kept between sessions and **only when
      requested**, which is the whole of its design rather than a preference about it. Not a series
      per metric per process, which would be a database; totals per program, in a file somebody can
      read and delete a line out of
- [ ] Support plugins and extensions
- [ ] 🟡 Support scripting and automation through CLI/API — six formats over every field the registry
      holds, a filter language shared by all three front-ends, per-process detail and per-resource
      plots from the command line, a mode that turns the expensive collectors off, and exit codes a
      script can branch on: nought for a match, two for none, one for a query it could not parse.
      **There is still no library API**, and that is what keeps this partial — everything above is a
      program somebody shells out to, which is not the same as something they can reference
- [x] Provide a portable, no-install distribution — single-file NativeAOT binary, 6.7 MB, no runtime
- [x] Support dark, light and system themes
- [x] 🟡 Provide an optional tray / menu-bar mode — indicators for the processor and memory, named
      one at a time in the settings file and absent unless somebody asks. Disk, network and graphics
      have no reading behind them yet and are not drawn rather than drawn flat (§65)

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
- [x] Precision — read off the unit rather than written on every entry: every percentage on the
      machine is written to the same number of decimals, a byte count scales the same way wherever it
      appears, and a count is whole. A hundred and fifty copies of that rule would be a hundred and
      fifty chances for one of them to say something else, which is the drift one catalogue exists to
      stop. It follows the percentage setting rather than the default it ships with, so it answers
      with what somebody will actually see (§67)
- [x] Whether it is instantaneous, cumulative, delta, rate, state, enumeration or derived
- [x] Supported platforms
- [x] Required privilege — ordinary, or the owner's. Two levels because two is what a reader can act
      on: there is nothing to do, or there is the elevated helper. `--fields` prints it, so the
      column of em dashes over somebody else's processes is answered before it is met. A field
      declares the most it can need on a platform that supports it: the I/O counters are free on
      Windows, where one system-wide query fills them, and behind the owner's authority on Linux,
      where the file has been mode 0400 since 5.12. There is no third level, and that is a finding —
      no field of this table needs elevation to be read about a process of your own; the things that
      do are a thread's kernel stack and its current system call, which are not fields (§29, §30)
- [x] Collection cost
- [ ] 🟡 Default visibility — what the catalogue declares is the *constraint*, and tests enforce it:
      an opening set costs no syscall per process, the descriptor count excepted because it is
      counted on a schedule of its own (§5.4). Which of the cheap ones each front-end opens with is
      still three lists — the window's, the terminal's three widths and the CLI's — and deliberately
      so, since eighty columns of terminal cannot open with what a 1920-pixel window does. What is
      not true is the sentence above this list: no *entry* says whether it is default-visible
- [x] Sort semantics
- [x] Filter semantics — `Number` for comparisons, `RawText` for substring and regex
- [x] Formatting function
- [x] Null/unavailable semantics
- [x] Export serialisation — text, number, ISO 8601 or nothing at all, derived from the kind and the
      unit so that a field is serialised correctly on the day it is added. The exporter had that rule
      copied into it with one field named by hand, so every timestamp but the start time wrote `null`
      into every row while the column beside it showed a date — an image's creation time and a
      signature's countersigning date, both added to the catalogue after the exporter was written.
      That is exactly the second definition of a field this section exists to prevent
- [x] Historical-storage eligibility — every field whose readings are kept says which rings keep
      them: the shared sixty-sample rings behind the table's sparklines, an hour of them per process
      for a properties window, or both. The drawn column and the column it plots name the same
      series, so a plot cannot be drawn from a reading no column shows — and the two front-ends draw
      a plot where the *kind* says graph rather than wherever a series is declared, which is what
      makes it safe for the CPU percentage to say that its history is kept. Both halves are held to
      the code by tests: one reads the declaration and checks what the sampler actually puts in each
      ring, and one checks the properties window's plots against the fields declared kept per process

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
- [x] systemd as the primary service-management target — a view of every unit and its state, read
      from the unit files and the cgroup tree without D-Bus and without spawning anything, and all
      six verbs systemd offers from the window, the terminal and the command line (§41)

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
- [x] Longer windows (5 min, 15 min, 1 h) — **and no decimating ring was needed, because the
      affordability problem this box names is avoided rather than solved.** It is real: 3600 points
      per series per process, over four hundred processes, is not something to keep. Nothing keeps
      it. The in-row sparklines are sixty-four samples per process and are sixty seconds by design;
      the per-process page keeps 3600 for the one process somebody is looking at; and the machine's
      own pages keep one series per resource rather than one per process. A long window is offered
      exactly where there is one series to keep it for (§85)

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

Verified: `dotnet publish -r linux-x64 --self-contained -p:PublishAot=true` → one 6.7 MB ELF, which
runs, passes the self-test 37/37 and opens the window. It was 3.2 MB when this was first written and
has roughly doubled since, which is what a year of columns, readers and views costs; the figure is
re-measured rather than left at the flattering one.

## 8.4 Engineering rule — see §103

No field or action may be introduced inside a front-end. §103 lists the thirteen steps.

---

# 9. Main GUI information architecture

Primary navigation:

- [x] Processes
- [ ] 🟡 Performance — reachable from the rail, but as a window of its own rather than a page in the
      content region: it is modeless, has its own timer and its own lifetime, and a second copy of it
      in here would mean two of everything it samples (§45)
- [ ] 🟡 Applications / usage history (§44) — the record is kept and is a documented file anybody
      can read, and there is no view of it behind the rail yet
- [x] Startup (§42)
- [x] Users / sessions (§43)
- [x] Services — a view, and all six verbs on a right-click (§41)
- [x] Network — every socket on the machine, with the process holding it where this account may see
      one; opening a row goes to that process (§40)
- [ ] System activity (§51)
- [x] Search / find resources — from the rail, which opens §33's dialog
- [ ] 🟡 Logs / history (§63) — the terminal has a timeline; the window does not yet
- [ ] 🟡 Settings (§67) — reachable and complete, as a dialog rather than a page in the content
      region. The same argument as Performance above: the box hands back a record and closes, where
      a page would be a fifth place the same record is edited and would have to agree with the other
      four about when a change takes effect

The four table views are collected when they are chosen and when Refresh is asked for, and never on
the sample tick: enumerating every unit on the machine once a second would cost more than the thing
being measured (§5.4). Each says above its rows how many of what came back and when — a table that
has silently stopped being true is worse than one that admits its age — and each says which of the
two things "none" means (§72.3).

Optional advanced views: drivers/kernel modules, file activity, disk activity, GPU activity,
containers/cgroups, jobs, security, devices — all unbuilt.

---

# 10. Global window layout

- [x] Left navigation rail with the persistent primary views — and it collapses to icons on its own
      hamburger, so the content region gets the width back on a small screen
- [x] Top command bar with context-sensitive actions — what a view cannot do is disabled rather than
      offered and silently inert, which is the failure mode this program has already shipped once
- [x] Primary content pane: table, tree table, graphs or dashboard
- [x] Optional lower pane, resizable, toggled by shortcut, toolbar button and menu — Ctrl+D,
      the strip and View ▸ Lower pane, all three, and whether it was showing survives a restart.
      Collapsed rather than removed, so the splitter comes back where it was left

Lower-pane modes:

- [x] Summary
- [x] Threads
- [x] Modules
- [x] Handles / descriptors
- [x] Network
- [x] Memory mappings
- [x] Environment
- [x] Windows
- [x] Services
- [x] Security
- [ ] Timeline — it needs the event history of §63, and nothing in this program records one. A tab
      named for a feature nobody wrote is worse than a missing one

The lower pane is the defining Process Explorer interaction and is the highest-value single item in
this document.

**The four that arrived last are pages rather than lists, and they were the properties window's
until they moved.** §26 asks that window for one row of tabs and not two, and it gets that by hosting
this pane and adding its own pages to the pane's strip — so a memory map owned by the window and a
memory map owned by the pane would have been two tabs of the same name side by side. Owning them here
gave the lower pane four of §10's modes and cost the window nothing: its own tab list is unchanged,
and a test now counts every caption on the strip so that a page somebody forgets to stop adding fails
a build rather than shipping as a duplicate.

**A page at the foot of the main window follows the selection, and one in a properties window never
does.** That difference is the whole of what had to be written rather than moved: the memory map and
the window list each remember that they have been filled, and a page that kept that flag across a
change of process showed one process's mappings under another's name. Pointing either at a new
process throws away what it read for the last one (§72.2, §86).

**The window stacks menu, command bar, plots, then the rail beside the content, with the status line
along the foot.** The adds run the other way round, because the toolkit's layout pass walks its
children backwards and the child added last claims its edge first. That is not a style note: the
strip used to be added after the menu on the assumption that earlier meant outer, and the window
shipped with its plots above its menu bar for exactly as long as nobody looked at a picture of it.

---

# 11. Global table requirements

Every table:

- [x] Column show/hide
- [x] Column reorder — `{` and `}` in the terminal; in the window, drag the header or
      `Ctrl+Shift+←`/`→`
- [x] Column resize — `,` and `.` in the terminal; in the window, drag the boundary between two
      headers or `Ctrl+-`/`Ctrl++`
- [x] Column reset — `0` in the terminal, `Ctrl+0` in the window
- [x] Ascending sort
- [x] Descending sort
- [x] Multi-column sort — the engine takes any number of tie-breaking keys; shift-clicking a header
      adds one in either front-end, and the header shows a digit rather than a second arrow, because
      two arrows say "sorted twice" and nothing about which one wins
- [x] Keyboard sort — `F6`/`Shift+F6` step the sort through the columns that are showing and `F7`
      reverses it, in both front-ends
- [x] Freeze / pin columns — the first column is pinned in both front-ends, and the boundary moves
      with `#` in the terminal and Ctrl+Shift+P in the window. A count of leading columns rather than
      a set of ticked ones: a pinned third column with two scrolling ones in front of it leaves a
      hole beside it that nothing can fill
- [x] Auto-size column / all columns — measured against the rows on screen rather than every
      process, because a column fitted to the widest value in the whole table is usually fitted to
      something scrolled out of sight
- [x] Copy cell — `y` in the terminal, over OSC 52; `Ctrl+Shift+C` in the window
- [x] Copy row — `Y` in the terminal, `Ctrl+C` in the window
- [x] Copy selected rows / columns — one key copies every ticked row with a header line; `Ctrl+Y` in
      the terminal and Ctrl+Shift+D in the window take one column down every row, the ticked ones if
      any are ticked and everything on screen otherwise. A column copy wants a column and not a cell
      selection, and both front-ends have had a column cursor since their headers grew gestures (§95)
- [x] Export table — `X` in the terminal and Ctrl+E in the window, in whichever of the six formats
      the file name asks for
- [x] Text filter
- [x] Advanced filter
- [x] Regular-expression filter
- [x] Numeric comparison filters
- [x] Unit-aware comparison
- [x] Case-sensitive toggle — `!` in the terminal, the "Match case" box beside the window's filter
- [x] Highlight matched text — the run that matched, inside the cell, measured with the renderer
      that draws the string rather than by counting characters
- [x] Multi-selection — `Space` in the terminal, a tick box on the row in the window; a bulk
      terminate names the count before it acts
- [x] Select all / invert selection — `Ctrl+A` and `v` in the terminal, the Edit menu in the window
- [x] Context menu
- [x] Persist layout — columns, their order, their widths, the sort, the grouping and the interval
      survive a restart

**The window's filter box.** §56's query language was reachable from the terminal and from
`--filter`, and from nowhere in the GUI; the three rows above it depend on there being somewhere to
type. A query that will not parse falls back to a plain substring search rather than blanking the
list, because somebody halfway through typing `chrome:` has not written a broken query.

**Copying, over SSH.** The clipboard a terminal front-end can reach is the *terminal emulator's*,
not the machine's: a process on the far end of an SSH session that wrote to an X selection would put
the text on the server's clipboard, where nobody can paste it. So a copy is an OSC 52 sequence handed
to the emulator. Nothing answers it — a terminal with the feature switched off looks exactly like one
that took the text — which is why the status line says the text was *offered* and why every copy is
also reachable through the export key, which writes a file no terminal setting can veto.

Named **column sets**. Each stores its visible fields and their ordering; widths come from the
registry, and sorting and grouping are not stored in a set. How many columns are pinned is kept per
front-end rather than per set — the window and the terminal keep their own column orders, and five
pinned columns in a wide list mean nothing at all in an eighty-column terminal. Applying a set
therefore keeps whatever was pinned and clamps it to the new set's width rather than dropping it: a
reader who pinned the name column pinned it because they want it there whatever else the table shows.

Reachable from both front-ends — the terminal's column chooser and the window's **Column sets**
submenu — and from `--columns @name`. A set the settings file names shadows a preset of the same
name, which is what keeps a preset improvable instead of something to be worked around.

- [x] Basic
- [x] Performance — as `cpu`
- [x] Memory debugging — as `memory`
- [x] Security
- [x] I/O debugging — as `io`
- [ ] Network — blocked on §18
- [x] Full forensic — everything `expert` has, plus who the process really is and what it is doing
      to the disk. Deliberately the dearest set there is: the package, the digest and the descriptor
      count each cost a reading nothing else takes, and asking for a forensic table is asking to pay
      for them (§5.4). It is also wider than any terminal, which is what the pinned run and the
      sideways scroll are for
- [x] Minimal recovery — as `minimal`

Reachable as `--columns @security`. A set saved in the file replaces a built-in preset of the same
name; the presets themselves are never written into the file, because a preset copied into
everybody's settings could never be improved again.

§94 defines the presets' contents.

**Known and reproducible:** a right-aligned caption sits flush against the next column's caption, so
"CPU %" and "Working set" touch with no gap between them. Three separate changes have reported it and
none has fixed it: the header cell clips and pads within its own bounds, so the captions do not
overlap — they simply abut, and the eye reads the join as a clipped letter. It wants measuring in the
shared header painter rather than guessing, and widening a column to hide it would be the wrong fix.

---

# 12. Update / refresh system

- [x] Intervals 250 ms · 500 ms · **1 s** · 2 s · 5 s · 10 s · paused · manual — settable from the
      CLI, persisted, and pickable in both front-ends: View ▸ Refresh in the window, the settings box
      of §67, and `d` in the terminal, over one list of rates all three share. A file naming a rate
      that is not on the list keeps it: the settings box shows the nearest offered one and a file
      saying three seconds does not come back as one
- [x] Default 1 second
- [x] Pause while preserving selection

**Paused and by hand are two entries.** Both stop the tick, and only the second is remembered: a
pause is flipped for a few seconds to read a row that will not hold still, and a monitor that opened
paused because it was paused when it was last closed is a monitor showing a table of nothing at all.
The rate underneath is kept either way, so switching the tick back on returns to the rate somebody
chose rather than to the default. Neither disturbs the list — nothing is rebuilt while the tick is
off, so the selection, the expanded nodes and the scroll position are where they were left, and both
front-ends say on screen which of the two states they are in.

Refresh preserves:

- [x] Scroll position
- [x] Expanded process nodes
- [x] Selected entities
- [x] Sort order
- [x] Open property page
- [x] Lower-pane mode — there is a lower pane now, and which of its tabs is showing survives a
      refresh. It holds by construction, because the pane is built once and filled again rather than
      rebuilt; it is asserted anyway, since the scroll position in this same list was preserved by
      accident once and then stopped being

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

- [x] **Friendly / grouped** — group by Kind, in both front-ends and settable from the file, which
      arranges the table as "these are your programs, these are the machine's": yours, the system's,
      elevated, services, suspended, zombies, newly started, packaged, running a managed runtime. It
      is §23's own classifier rather than a second opinion beside it, so a heading and the colour of
      a row underneath it cannot come to disagree.

      **Building it found the classification broken in exactly the case it exists for.** Whether a
      process was a service was decided by looking for `.service` anywhere in its cgroup path — and
      every process in a desktop session has `user@1000.service` as an ancestor, because that is the
      name of the user's own systemd manager. So on the machine this was written on, 207 of the
      user's own programs were classified as services and one as theirs. The innermost unit decides
      now, which is the rule the owning-service column already followed: a `.scope` is a group
      systemd adopted — a terminal, an application the desktop launched — and is not a service in the
      sense anybody means. The same walk of `/proc` afterwards: 115 yours, 94 services
- [x] **Process tree** — parent/child hierarchy, expand/collapse per row
- [x] **Flat expert** — one row per process, optimised for sorting and density

- [x] When a parent disappears, surviving children remain visible and become orphaned roots
- [ ] …or attach to the closest surviving ancestor, per configured behaviour — deliberately still
      open. On Linux the kernel reparents an orphan to `init` or to the nearest subreaper and writes
      that in `ppid`, so "the closest surviving ancestor" is a chain this program never saw: the
      parent it would climb to has already gone, and remembering the tree across samples to guess at
      it would be inventing a relationship the kernel has forgotten

---

# 14. Process table — identity fields

The canonical field registry. The ID is stable: a saved layout, a `--columns` argument and a search
term all use it, and it never changes even when the display name differs per platform.

- [x] `name` — friendly process/executable name
- [x] `exe.name` — the file that is running, from `image.path`. Differs from `name` when
      a process renames itself, not yet its own field
- [x] `app.name` — from the desktop entry that starts the program; "several" where more than one
      does, because naming one of eight would be wrong most of the time. **Windows says the question
      does not apply**, which it had not been saying: the field was left at its default there and a
      default reason means "the value is present", so the column read "none" — a real Linux answer,
      meaning the machine has no entry for the program. The nearest Windows equivalent is the version
      resource, which is its own column
- [x] `pid` — process identifier
- [x] `ppid` — parent process identifier
- [x] `instance.id` — PID plus creation-time-safe unique identity (`ProcessKey`)
- [x] `parent.name` — the parent's name, resolved once per sample over the whole table
- [x] `tree.depth` — hierarchical depth
- [x] `status` — running, sleeping, suspended, zombie, terminated
- [ ] `responding` — GUI responsiveness; `IsHungAppWindow` on Windows, `n/a` on Linux. **Windows
      only, and unwritten**: it needs the window list joined to the process table, which is §39's
      work and is not done on Windows either
- [x] `start.time` — process creation timestamp
- [x] `running.time` — elapsed lifetime
- [ ] `exit.time` — requires retaining dead rows; ties to §87. **Every platform, and unwritten on all
      of them**: nothing here keeps a row after the process behind it has gone, so there is nowhere
      for the answer to live yet
- [ ] `exit.code` — only for children we spawned or via job/wait; honest `—` otherwise. **Every
      platform, and unwritten on all of them**, for the same reason and with a second one on top:
      neither kernel will tell a bystander what a process it did not start exited with
- [x] `user` — account owning the process
- [x] `user.id` — SID / UID
- [x] `session` — login/terminal session
- [x] `session.id` — native session identifier
- [x] `arch` — from the ELF header, which is the program's answer rather than the machine's: an
      x86-64 kernel runs 32-bit binaries every day, and reporting the machine's architecture for
      every row describes the machine instead of the program. Byte order is in the header, so a
      big-endian binary decodes on a little-endian machine — the case no laptop here can produce
      and a test covers
- [x] `emulation` — WOW64, Rosetta, translation state. **Windows**, from `IsWow64Process2`, which
      names the machine the process is being translated *from* and reports
      `IMAGE_FILE_MACHINE_UNKNOWN` for one that is not. So nought is an answer here and the ordinary
      one — it renders "native" rather than leaving a cell that reads like a hole (§72.3). The guest
      machine rather than the host's, because every row on a machine shares the host and what differs
      between rows is the half worth a column: an x86 program on an x64 machine, an x64 program on an
      ARM64 one. A Windows older than 1709 has no such call, which is a fact about the machine and is
      reported once as unsupported rather than as every process running natively. Held against the
      runtime's own two architectures, which say whether a process is being translated without asking
      the same question of the same API. Rosetta is **macOS only** and the macOS probe is a stub
      (§6.3), so that half is not answered
- [x] `image.path` — full image path. On Windows this was empty until the six lines below needed it:
      the bulk query carries the image's file *name* and nothing else, so the path comes from
      `QueryFullProcessImageName` on the handle the owner lookup already opens — one more call, on a
      right it already holds, cached for the process's life because a running program does not move.
      `exe.name` fills in with it
- [x] `cmdline` — complete command invocation
- [x] 🟡 `cwd` — Linux; Windows needs a PEB read we do not do
- [x] `description` — binary description (version resource). **Windows only**: a PE keeps these five
      strings inside the file and an ELF has no such section and never did, so the nearest Linux
      facts are the package's and are `package` and `app.name` below — a different question with a
      different answer, and a column showing one under the other's name would be stating something
      false (§5.3). Routinely the only cell on the row that says what a service actually *is*, the
      name being whatever the program decided to call itself.

      Read once per **image** rather than once per process — three hundred processes of one runtime
      share one binary — and again when that file is replaced underneath them. Opt-in, because the
      cost is opening and reading a file (§5.4).

      **How this is verified without a Windows machine.** The walk takes bytes and carries no
      platform attribute, so it runs on every leg — and not against a buffer synthesised from its own
      struct definitions, which is the weakness the §9.4 replay tests admit to. Every assembly this
      repository builds *is* a PE image with a version resource in it, written by a compiler nobody
      here controls, and the tests read those. A PE writes its file version twice, in two encodings
      reached by two different paths through the resource tree, and a walk that lands in the wrong
      place cannot make the two agree: held against **1617 real PE images**, of which 478 carry a
      version resource and 472 agree exactly — the six that do not are publishers who wrote `8.00`
      where the binary field says `8.0`. The header fields were separately checked against
      `objdump -x`. On a Windows kernel the strings are compared against `FileVersionInfo`, which is
      Microsoft's own reader of the same bytes out of the same file
- [x] `company` — publisher metadata. As above, and a **claim rather than a verification**: anybody
      may write anything in a version resource, and whether somebody this machine trusts signed for
      the image is the signature fields of §21 and is not this
- [x] `product` — product metadata. As above. Several files of one suite share it, which is what
      makes it worth a column beside the file's own description
- [x] `product.version` — as above, and kept as the **string the publisher wrote** rather than as a
      number, because `10.0.19041.1 (WinBuild.160101.0800)` is a real value of this field
- [x] `file.version` — as above. Routinely neither the product version nor the four numbers
      `VS_FIXEDFILEINFO` carries, which is why it is its own column and stays a string
- [x] `package` — the distribution's own database, Flatpak, snap and AppImage on Linux; MSIX on
      Windows, from `GetPackageFullName` on the handle the owner lookup already holds. Its own
      source rather than "the machine's package manager", because it is not one: nearly every
      program on a Windows machine comes from no package at all, and lumping an MSIX in with a
      distribution's database would claim a symmetry that is not there (§5.3).
      `APPMODEL_ERROR_NO_PACKAGE` is the answer for most of a Windows process table and is a
      *finding* — this program came from no package — so it reads "not packaged", the same word the
      Linux half uses; anything else that goes wrong does not borrow it (§72.3).

      **No switch in front of the Windows half, unlike the Linux one.** There the column costs
      thirty megabytes of file lists to build an index and §5.4 applies; here the package manager is
      asked about one process, answers immediately, and the answer is fixed for that process's life
      and cached with the rest of its identity. A switch would buy nothing and would leave the column
      empty for anybody who forgot it. **macOS is a stub (§6.3)**, so `.app` bundles are not read
- [x] 🟡 `app.id` — read for Flatpak and snap; neither could be verified live on the machine this
      was written on, which has neither installed, and that is what the amber is for. **Windows is
      the package family name**, from the same handle as the line above and asked for separately
      rather than cut out of the full name: a family is the name and the publisher hash, and the
      full name has the version and the architecture in between them, so taking one out of the other
      would be reassembling a format rather than reading it. **macOS is a stub**
- [ ] `bundle.id` — macOS bundle identifier. **macOS only**, and the macOS probe is a stub (§6.3).
      Windows and Linux have no such notion — the nearest are the two lines above, and both are
      already their own field
- [x] `container.id` — every runtime writes its own cgroup shape and they all bury a long
      hexadecimal id somewhere, so the id is looked for rather than the layout: there is always
      another layout. A run of hex has to be long enough to *be* an id, or a systemd slice and a
      terminal's UUID scope would both report as containers on an ordinary desktop
- [x] `namespace` — kind and inode. The inode is the identity: two processes sharing one share that
      namespace, which is how a container's members are actually told apart, rather than by a cgroup
      path anybody can write anything into
- [ ] 🟡 `job.cgroup` — Linux cgroup path done; Windows job object not. **The Windows half is
      unwritten**, and only half of it is even reachable: `IsProcessInJob` says whether a process is
      in one and is a single call on the handle the identity read already holds, but
      `QueryInformationJobObject` needs a handle to the *job*, and Windows offers no way to get one
      from a process — a job is anonymous unless somebody named it, and a bystander cannot open what
      has no name. So the Windows column could say "in a job" and could never say what that job
      limits, which is the half worth reading; a yes with no answer behind it was not judged worth a
      column, and that is a decision rather than an oversight. Not the same fact wearing one name,
      which is why the line carries both — a cgroup is a path in a hierarchy and a job is an unnamed
      kernel object a process cannot leave (§5.3)
- [x] `terminal` — controlling TTY, decoded from `stat` field 7. The packing is the awkward part:
      minor is split across the low eight bits and bits 20–31 with major in between, so the obvious
      shift is right for small numbers and wrong for large ones. Zero is *no terminal* — the answer
      for every daemon, and so for most of a machine — rather than device 0:0
- [x] `exe.size` — of the resolved target, not of the `/proc` link. Asking the link its length gives
      nought, which is how this first reported every program as being no bytes long
- [x] `exe.modified`
- [x] `exe.created` — `statx` where the filesystem carries a birth time, and honestly unknown where
      it does not, which is most of them. **Windows now answers too**, and it is the one line here
      where it answers more reliably than Linux: NTFS has recorded a creation time for every file
      since it was written. Which is exactly why the two keep their reasons apart rather than
      sharing an empty cell — a filesystem with no birth time to give and a file this user may not
      stat are different findings (§72.3) — and why a filesystem that has none is tested for by the
      instant it hands back rather than by nought: `FILETIME` zero is 1601, and a column of 1601
      would be the same lie a column of 1970 is on Linux. Opt-in on both, being a `stat` per process
      on a path nothing else reads. **macOS is a stub**
- [x] `subsystem` — GUI/console/native; PE only, `n/a` for ELF. **Windows only**, out of the optional
      header of the same file the five strings above come from, so naming any of the six buys the one
      read that answers all of them. What the loader is expected to give the program rather than what
      it happens to have, so a console program started without a console still reads as a console
      program. `IMAGE_SUBSYSTEM_UNKNOWN` is a value a PE header can actually carry, so it keeps its
      own word and a file that is not a PE at all is *not* given it — those are different statements
      (§72.3). A subsystem Microsoft adds later shows as its number rather than being flattened into
      the nearest name this build knows
- [x] `interpreter` — `PT_INTERP`, or the shebang's program for a script. A shebang is as real a way
      to start a program on Linux as an ELF header, and reporting "not an executable" for every shell
      script would be wrong about a large part of any machine.

      **No interpreter and no permission to look are different answers.** The first means statically
      linked; the second means nobody could check. Collapsing them made the report call every other
      user's process statically linked — a confident claim made out of an absence (§5.3)
- [x] `runtime` — native, .NET, JVM or Python, from the module list rather than guessed from a name.
      **Windows says nobody has looked**, rather than rendering an empty placeholder that read as an
      answer. Deliberately not "not supported": the module list is perfectly readable there through
      Toolhelp32 and this program has not read it, and that is a different sentence to somebody
      deciding whether to go and find out elsewhere

# 15. Process table — CPU fields

- [x] `cpu.percent` — normalised 0–100 % utilisation
- [x] `cpu.percent.raw` — multi-core cumulative
- [x] `cpu.delta` — change since prior sample, in percentage points and signed. The difference
      between two intervals rather than between two samples, so it needs three of them: a process
      one interval old has a share and nothing to compare it against, and says so rather than
      reading as steady. `--list` takes the third sample only when the column is asked for
- [x] `cpu.time` — total processor time
- [x] `cpu.time.user`
- [x] `cpu.time.kernel`
- [x] `cpu.cycles` — Windows only; `n/a` on Linux
- [x] `cpu.cycles.delta`
- [x] `ctx.switches`
- [x] `ctx.switches.delta`
- [x] `ctx.switches.rate`
- [x] `threads` — current thread count
- [ ] `threads.peak` — **no source on either platform.** Linux keeps the current thread count in
      `status` and no high-water mark of it anywhere; Windows carries `NumberOfThreads` in the same
      bulk query every other column here comes out of and no peak beside it. So this is not the
      usual "one platform can and we have not written it": neither kernel records the number. The
      only figure this program could offer is the largest it happened to see while it was running,
      which is a fact about the observer rather than about the process (§9.2)
- [x] `priority.base`
- [x] 🟡 `priority.dynamic` — Linux only; `stat` field 18, the kernel's own number rather than the
      nice value
- [x] `nice` — the politeness a process was started with, backwards on purpose
- [x] `priority.class` — idle/below normal/normal/above normal/high/real time. **Windows only.**
      Linux has no such band: nice orders tasks inside `SCHED_OTHER` and the class is `sched.class`,
      and folding either into a Windows priority class would be the false equivalence §5.3 forbids.

      **Read out of `priority` rather than through `GetPriorityClass`, and not to save the call.**
      A handle per process per sample is the `OpenProcess` in the sampling loop §5.2 forbids; a
      handle once per process, cached the way the identity is, would be wrong the instant somebody
      changed the class — and this program has a menu item that changes it (§25.2). The base
      priority arrives with every sample and the kernel derives it from the class by a table that is
      one-to-one — 4, 6, 8, 10, 13, 24 — so inverting that table is both free and current. It is
      also what makes the column sort: the six numbers are ordered the way the bands are, while
      `PROCESS_PRIORITY_CLASS_*` numbers "below normal" above "high" and would sort into nonsense.
      A base priority the table does not contain is left unknown rather than rounded into the
      nearest band it is not (§72.3)
- [x] `nice`
- [x] 🟡 `cpu.affinity` — Linux only, and opt-in. `Cpus_allowed_list` from the `status` the sampler
      already has open, in the kernel's own notation (`0-15`, `2,3`), which is what `taskset -pc`
      prints; the list rather than the mask, because on a 128-way machine the mask is unreadable.
      Not `sched_getaffinity`: that is a syscall per process for a line already in front of us.
      Windows' `GetProcessAffinityMask` is not written
- [x] `cpu.set` — Windows CPU sets, from `GetProcessDefaultCpuSets`, and opt-in. **Windows only**;
      the Linux near-relative is the cgroup `cpuset`, which is already reflected in `cpu.affinity` —
      the kernel narrows `Cpus_allowed` to it. Its own column beside the affinity mask rather than
      folded into it, because the two are different promises: a mask is a wall a process cannot run
      outside, a set is a preference the scheduler honours when it can, and that is the whole reason
      Windows grew a second mechanism (§5.3). No set assigned is a real answer and the ordinary one —
      the process gets the system's default set, which is every processor — so it reads "default"
      rather than leaving a cell that looks like a hole (§72.3). Asked for with a buffer that is
      already large enough rather than with a length probe, because a probe that reports nought and a
      process that has nothing assigned would otherwise be the same reply
- [ ] `numa.node` — **not answerable honestly here.** `Mems_allowed_list` says which nodes a process
      may allocate from, which is a different question from which node it is running on, and this
      machine has one node — an implementation could not be told from a broken one (§9.2)
- [x] `cpu.last` — field 39, which sits behind fourteen fields nothing else reads
- [x] `sched.class` — `SCHED_OTHER`, `_FIFO`, `_RR`, `_BATCH`, `_IDLE`, `_DEADLINE`, `_EXT`, under
      the kernel's own names. From `stat` field 41 rather than `sched_getscheduler`, which would be a
      syscall per process for a number already in the line being parsed. Verified against `chrt -p`.
      A class the kernel adds later is left unknown rather than folded into the ordinary one, and a
      `stat` that stops short says nothing rather than claiming `SCHED_OTHER`
- [x] `qos` — OS energy/performance state, from `GetProcessInformation(ProcessPowerThrottling)`.
      **Windows and macOS only**, and macOS is a stub (§6.3). Linux has no per-process
      quality-of-service class; `uclamp` is a utilisation hint on a task, not a state the OS assigns,
      and reporting one as the other would invent a concept the kernel does not have.

      **The columns are §22's `qos.background` and `eco.state`, and there is no third one here.**
      This line and those are one reading — what Windows has been asked to do about a process's
      energy — and §22 is where an energy reading belongs; a `qos` column of its own in §15 would
      have been a third spelling of one counter, which is exactly the drift the one field catalogue
      exists to stop (§5.1). Ticked here because the question this box asks is answered, not because
      it is answered twice
- [x] `throttled` — cgroup `cpu.stat` `nr_throttled`, opt-in. The group's counter and not the
      process's, which the column says: everything in one cgroup shows the same figure. Read once per
      cgroup per sample rather than once per process. A group whose CPU controller is off has no such
      line and reports unknown — a real nought there means "has a quota and never reached it"

Required of the CPU percentage:

- [x] Normalised 0–100 view
- [x] Logical-CPU cumulative view
- [x] Configurable decimal precision — `percent.decimals` in the settings file, `--decimals` on the
      command line, and a picker in the settings window; nought to three, one by default, and out of
      range leaves the setting alone rather than zeroing it. It governs **every** percentage and not
      only the processor's, deliberately: a window writing CPU to two decimals and memory to one
      would be stating a precision about the first that it does not have about the second. One digit
      is dropped once a value reaches a hundred so the column keeps its width where it is widest,
      which is what the program has always done at the default — "100" beside "99.9" — and the rule
      now follows the setting instead of staying fixed at the tenth it was written as. The threshold
      under which `cpu.delta` writes a plain nought follows it too, or two decimals would round away
      changes the column had room to show

# 16. Process table — memory fields

- [x] `mem.percent` — share of the machine's memory. Computed in the delta rather than where the
      columns are rendered, because that is the only place the machine's total is in scope beside
      the process; a percentage of an unknown total is not a percentage. Answered on the first
      sample, unlike every rate beside it
- [x] `ws` — working set / RSS
- [x] `ws.peak`
- [x] `ws.private` — `WorkingSetPrivateSize` (W), `RssAnon` (L). **No longer "or PSS"**: the
      proportional set used to overwrite this field when the option was on, so one column headed
      "Private WS" showed the anonymous resident set on one machine and a share of every mapping on
      another — two different questions under one label, with nothing to tell a reader which they
      were looking at
- [x] `ws.file` — `RssFile`. Split out because the halves behave completely differently under
      pressure: file-backed pages can be dropped and read back, anonymous ones can only go to swap.
      A process whose resident set is nearly all file-backed costs the machine far less than one of
      the same size that is nearly all anonymous
- [x] `ws.shared` — `RssShmem`: tmpfs, shared anonymous mappings, System V shared memory
- [x] `ws.shareable` — the resident memory somebody else could also be holding, read from the same
      lines as the private half so the two cannot disagree about the file they came from

The three add up to the working set exactly, which is what makes them a breakdown rather than three
more numbers. All free: the lines are already in the `status` this program reads anyway.
- [x] `private.bytes` — private committed virtual memory; `PrivatePageCount` (W), `VmData` (L).
      Both mean commit charge — this was `RssAnon` on Linux until it was corrected, which made the
      same column mean two different things on two platforms
- [x] `private.bytes.peak` — **Windows only**, and free: `PeakPagefileUsage` sits beside the commit
      charge in the structure the sampler already reads, and is the peak of the *same* charge
      `private.bytes` reports. That is what makes the pair worth having — a process sitting at fifty
      megabytes with a peak of four gigabytes has been somewhere the current row cannot show. Linux
      keeps no high-water mark of `VmData` anywhere: `VmPeak` is the peak of the address space and
      `VmHWM` the peak of the resident set, both are already their own columns, and reporting either
      under this heading would be a different number wearing this one's name (§5.3). Guarded by the
      same rule the other Windows counters are: a peak that reads nought for every process on the
      machine is a stub rather than a measurement, and says so (§72.3)
- [x] `virtual.size`
- [x] `virtual.size.peak`
- [x] `commit.size` — same counter as `private.bytes`
- [x] `pss` — Linux, opt-in; `smaps_rollup` costs 0.8–4 ms per process
- [x] `swap.pss` — swapped-out memory divided the same way, from the same file, so asking for either
      buys both
- [x] `uss` — private clean plus private dirty, free once `smaps_rollup` is open. PSS says what a
      process costs the machine; USS says what killing it would recover, and the two differ by
      exactly the shared pages somebody else is also using. The key used to be an alias for the
      anonymous resident set, which is *close to* the unique set and is not it

**PSS is the only per-process memory figure that adds up.** Working set counts every shared page in
full for every process mapping it, so summing it over a machine reports several times the memory that
exists; summing PSS gives back roughly what is really in use. Which is exactly why it is worth a file
read, and why the read is asked for rather than always taken — the kernel walks the page tables to
answer, and doing that for four hundred processes a second is indefensible against §71.

Asked for by naming the column or filtering on it, the same rule the security context follows: a
separate switch would only be a way to get an empty column by forgetting it (§5.4).

Another user's `smaps_rollup` is 0400, so their proportional set reports *not permitted* rather than
a zero — and a process nobody asked about reports *not sampled*. Neither is nought (§5.3).
- [x] `swap`
- [ ] `mem.compressed` — **no per-process source on either platform.** Windows compresses pages into
      a store owned by the memory manager and publishes its size for the *machine*, which is the
      memory page's `system.CompressedBytes` and is already read there; nothing attributes a share of
      it to the process whose pages were compressed. Linux is the same shape one layer down — zram
      and zswap account per swap device, not per task. macOS is the one platform that does keep a
      per-task compressed figure, and its probe is a stub (§6.3). So this is not a column somebody
      has not written: on the two platforms that work there is nothing to read
- [x] `page.faults`
- [x] `page.faults.delta`
- [x] `page.faults.hard` — Linux `majflt`
- [x] `page.faults.hard.rate`
- [x] `page.priority` — **Windows**, opt-in, from `GetProcessInformation(ProcessMemoryPriority)` —
      the documented call rather than `NtQueryInformationProcess(ProcessPagePriority)`, whose
      structure Microsoft has never published and which §8.3 forbids reading. Which of a process's
      pages the memory manager takes back first when the machine runs short: a backup or an indexer
      that has set itself low is the case the column exists for, and no other column on the row says
      it. Nought is `MEMORY_PRIORITY_LOWEST` and a real reading, so a run that did not ask must say
      so rather than showing a machine full of processes whose pages go first (§72.3). Opt-in
      because it needs a handle per process per *sample* — the value is settable while a process
      runs, so it cannot be cached for its lifetime the way the identity is. Linux has no
      per-process equivalent: reclaim there is driven by the LRU lists and by the cgroup's knobs,
      and neither belongs to a process
- [x] `pool.paged` — Windows; `n/a` on Linux
- [x] `pool.nonpaged` — Windows; `n/a` on Linux
- [ ] `heap.count` — **not a kernel fact on either platform.** A Linux process has no heaps the
      kernel knows of: glibc's arenas are an allocator's bookkeeping inside the process's own memory,
      and counting them would mean reading another process's allocator state. Windows does keep a
      list, and `Heap32ListFirst` walks it — behind a Toolhelp snapshot per process, which is the
      one thing §5.2 says the sampler may not do per row. So the honest Windows form of this is a
      figure for the process being looked at rather than a column, and it belongs to §34's memory map
      rather than here
- [x] `stack.commit` — how much stack the kernel has committed to the process
- [x] 🟡 `mapped.file.bytes` — **Linux**, opt-in, summed from `maps` over every mapping that names a
      file. The *mapped* size and not the resident one, which is why it is not `ws.file` under
      another name: a process that has mapped a four-gigabyte database and touched a megabyte of it
      is four gigabytes here and a megabyte there, and neither figure is the other's approximation.
      What counts is the presence of a name rather than the existence of a path — a library replaced
      under a running process reads as `… (deleted)` and a `memfd` never had a name in the file
      system at all, and both are backing pages all the same. The kernel's own pseudo-mappings are
      excluded by their bracket rather than by a list of their names, because the kernel keeps adding
      them: `[vvar_vclock]` is one this machine has that a list written a year ago would not have.
      Checked against the same sum taken with `awk` over `/proc/[pid]/maps`, exactly, on two live
      processes.

      Opt-in, and it is the one reading here that cannot be worked out once and kept: a process maps
      and unmaps files for as long as it runs, so it is a read of `maps` per process per *sample*
      (§5.4). **The Windows half is unwritten** — it is reachable by walking the address space with
      `VirtualQueryEx` and adding up the `MEM_MAPPED` and `MEM_IMAGE` regions, which is a call loop
      per process that nobody has written, so the cell there says so rather than claiming Windows has
      no such notion
- [x] `anon.bytes` — `RssAnon`
- ∅ `shared.mem` — the same question as `ws.shared` and `ws.shareable`, which are two columns above
      and are read from the same two lines of the same file. A third counter for it would be a number
      that can disagree with the two it is made of, which is exactly the reason `ws.shareable` is
      computed from the halves rather than read separately. Nothing here is missing: what a process
      has resident in shared segments is `ws.shared`, and what somebody else could also be holding is
      `ws.shareable`

- [x] Terminology adapts per platform while the ID stays canonical — the Linux build labels
      `private.bytes` "Commit" and the Windows build "Private bytes", and a saved layout moves
      between them unchanged

# 17. Process table — I/O fields

- [ ] `disk.percent` — process contribution where derivable
- [x] `io.read.rate`
- [x] `io.write.rate`
- [x] `io.total.rate` — read + write + other
- [x] `io.read.ops` / `io.read.ops.delta` — `syscr`, which counts calls where the byte columns count
      bytes: a gigabyte in one call and a gigabyte a byte at a time cost very different amounts
- [x] `io.read.bytes` — what the process has caused to be read from storage since it started, from
      `read_bytes`, which is actual device traffic and not `rchar`: a gigabyte served out of the page
      cache cost the disk nothing and reading it as I/O would make every warm process look busy. The
      per-interval figure is `io.read` beside it, which is the same measurement as a rate — this box
      named a `.delta` spelling that never existed, and **was ticked while neither column did**
- [x] `io.write.ops` / `io.write.ops.delta` — `syscw`, as above
- [x] `io.write.bytes` — the same for writes, from `write_bytes`. Checked against
      `/proc/[pid]/io` byte for byte on three live processes
- [ ] 🟡 `io.other.ops` — Windows only
- [x] `io.other.bytes` — Windows only, and on Linux the cell says the platform has no such figure
      rather than nought, which would claim the process made no such calls
- [x] `io.rate` — aggregate bytes/sec
- [x] `io.priority` — the class the scheduler holds the process at, read as well as set
- [x] `cpu.time.user` / `cpu.time.kernel` — the per-process split, free from `stat`'s `utime` and
      `stime`. A process that is mostly kernel time is usually waiting on something rather than
      computing, and one number covering both cannot say so
- [ ] 🟡 `io.wait` — read where the kernel accounts it; `kernel.task_delayacct` is nought on the
      machine this was written on, so it reports unknown rather than nought there
- ∅ `disk.latency` — requires eBPF/ETW tracing; see §52

# 18. Process table — network fields

Per-process byte counters have no portable source: Linux needs packet accounting or eBPF, Windows
needs ETW. Both are opt-in subsystems and **off by default** — §5.4 forbids making the ordinary
process table depend on them.

The four counts are read. The nine traffic fields are **not**, and the reason is written out below
rather than being worked around, because the obvious workaround produces a number that is wrong in a
way nothing on screen would betray.

- ∅ `net.percent`
- ∅ `net.send.rate`
- ∅ `net.recv.rate`
- ∅ `net.rate`
- ∅ `net.sent.bytes`
- ∅ `net.recv.bytes`
- ∅ `net.errors`
- ∅ `net.packets.sent`
- ∅ `net.packets.recv`
- [x] `tcp.count` — how many TCP sockets the process holds a descriptor on, listeners included
- [x] `udp.count` — the same for datagram sockets, which have no connection to count and so are
      counted themselves
- [x] `net.listening` — TCP only: a UDP socket bound to a port is not listening in any sense the
      kernel records, and counting one would invent a distinction the protocol does not make (§5.3)
- [x] `net.remote.count` — distinct peers rather than connections, because two connections to one
      machine are one correspondent, which is what the column is read for

All four are `High`: the join from a socket to a process is a `readlink` for every open descriptor on
the machine, so nothing is collected unless a column or a filter names one of them (§5.4).

**The traffic fields are refused, not deferred.** The socket diagnostics of §40 give per-socket byte
counters, and summing the ones a process currently holds is arithmetic anybody could do. It would not
be the quantity the column claims:

- A connection that closes takes its counters with it. A server handling short requests would report
  near nothing while saturating its link, and a process that finished a large download an hour ago
  would report having received nothing at all.
- UDP has no byte accounting anywhere in the kernel — not in `/proc/net/udp`, not in `udp_diag`. A
  process whose traffic is datagrams would read as idle.
- The counters are payload. Headers, and every packet the process caused that was not carried on one
  of its own sockets, are outside them.

None of those three shortfalls announces itself in the cell, and a reader has no way to tell a quiet
process from an unmeasurable one. That is a model presented as a measurement, which is what §72.3
exists to prevent. Honest per-process traffic needs the packet accounting or eBPF named above; until
one of them is built, the columns do not exist rather than existing and being wrong.

The refusal was gone over again rather than inherited, and it stands. One further source gets
proposed every time and is worth writing down so it is not proposed again: `/proc/[pid]/net/dev`
does carry byte counters, and they are real and complete. They are the counters of the **network
namespace** the process is in, not of the process — on an ordinary machine every process shares one
namespace, so the column would show the whole machine's traffic on every row. Where it is not the
whole machine it is a container, and the traffic of a container is a fact about the container: it
belongs to §38 beside the other cgroup and namespace figures, under a header that says namespace,
and it is not one of these nine fields under any header at all.

# 19. Process table — GPU fields

Linux, so far. Windows reads its own performance counters and that is §100's work; a field claiming
to work there would be worse than one that says it does not.

- [x] `gpu.percent` — the busiest of the process's engines, never their sum. A card's engines run at
      once and each share is of the whole interval, so adding them reports a transcode at 200 %
- [x] `gpu.engine` — which engine that was: 3D, compute, copy, encode or decode
- [x] `gpu.engine.percent`
- [x] `gpu.adapter` — which `cardN`. A laptop has two, and a GPU figure that does not say which one
      is unreadable on exactly the machines where it matters
- [x] `gpu.mem.dedicated`
- [x] `gpu.mem.shared`
- [x] `gpu.mem.total`
- [x] `gpu.mem.dedicated.delta`
- [x] `gpu.encode`
- [x] `gpu.decode`
- [x] `gpu.compute` — where the driver counts it apart from graphics. i915 does not, and NVML gives
      one figure covering both; each says so rather than reporting a nought
- [x] `gpu.copy`
- [x] `gpu.graphics`
- [ ] `gpu.power` — neither NVML nor the DRM interface attributes power to a process, and a card's
      draw split by utilisation share is a model rather than a reading. §22 is where a labelled
      estimate would belong

Sources: Windows GPU performance counters (what Task Manager reads); Linux DRM
`/sys/class/drm/*/device` plus per-vendor `fdinfo` (`drm-engine-*`, `drm-memory-*`), covering amdgpu
and i915; NVIDIA through NVML, which is the only place its per-process figures exist at all — a
process rendering on this machine's RTX A5000 has no `drm-` line in any of its descriptors.

The two shapes are kept apart because they are different measurements. DRM publishes a monotonic
counter per engine, which the delta divides by the interval exactly as it does CPU time. NVML
publishes a percentage it sampled over its own window and no counter of any kind, so integrating one
into the other would produce a "GPU time" that drifts and that nobody could reconcile against
`nvidia-smi`.

Off unless asked for, by `--gpu` or by naming one of the fields (§5.4): reading every process's
descriptors costs 590 µs per process unthrottled, so each process is listed in full once and then
every eighth sample, staggered by pid, with its known graphics descriptors read directly in between.

- [x] **Unsupported driver stacks render capability state, never a zero** (§72.3 restated — this is
      why the fields exist in the registry before they have values). A machine whose cards answer
      neither NVML nor the kernel's own client accounting renders `n/i` for every process rather
      than a nought, and the difference between "we looked and it uses none" and "nobody could
      look" is asserted in the tests
- [x] OS-provided data is separated from vendor-specific sensor extensions; vendor plugins cannot
      stall a sample or crash the sampler — NVML is loaded optionally, every call into it is guarded,
      a missing entry point is remembered rather than retried, and the DRM half runs whatever it does

# 20. Process table — object and resource fields

- [x] `handles` — handle count (W) / fd count (L)
- [ ] `handles.peak` — **no Linux source.** The kernel publishes the current descriptor count and no
      high-water mark of it; `RLIMIT_NOFILE` is the ceiling it was allowed, which is a different
      number, and `FDSize` is the table it was given, which is a third. On the machine this was
      written on one shell held **four** descriptors, with a table of **256** and a limit of
      **524288** — three numbers, none of them the other two, and none of them the peak. Same
      reasoning as `threads.peak` in §15. `fd.table` below is the nearest honest thing and is
      labelled as the different quantity it is rather than filed under this name
- [x] `fd.count`
- [x] `fd.table` — how many descriptor slots the kernel has allocated, from `FDSize`. A capacity: the
      table is grown when a descriptor will not fit and is never shrunk while the process lives, so
      it is an upper bound on how many were once held at once — which is the question somebody
      opening a peak column is usually asking, without pretending to be the answer. Free to read,
      and opt-in with the §21 lines because it is recognised in the same pass
- [x] `socket.count` — Linux, opt-in. One pass over the descriptor table, classified by the same
      Core function the handles view of §32 uses, so the column and the view cannot disagree about
      what a socket is. §5.4 is what resolved the objection below: naming the column is the request,
      and nothing else turns the scan on
- [x] `file.count` — as above. Descriptors on a name in the file system, directories included:
      separating those two needs the open flags out of `fdinfo`, which is a second file per
      descriptor. A device, a `memfd` and an anonymous inode are each their own kind and none of
      them is a file
- [x] `pipe.count` — as above. Both ends of one pipe are a descriptor each
- [x] `event.count` — Windows handle-type tally. **Windows only.** Linux's nearest equivalent is an
      `eventfd`, which is a descriptor and is already counted as one.

      One pass over the machine's whole handle table rather than a query per process, because Windows
      has no per-process handle query at all — the table arrives whole and the owner is a field in
      each row. That makes this cheaper here than the equivalent scan is on Linux and nowhere near
      free, so §20's rule below stands: naming any of the five buys all five, and nothing else turns
      the pass on (§5.4).

      **The part that would have been silently wrong.** A handle's type is a sixteen-bit index into
      the kernel's own table of object types, handed out in the order those types were created during
      boot — so it depends on which drivers loaded and differs between machines and between boots of
      one machine. A build that hard-coded the indices would report a plausible tally of entirely the
      wrong objects, and no test on a Windows machine would catch it unless that machine happened to
      boot differently. So the indices are discovered: one handle of each distinct index is duplicated
      and asked what its type is called, which is a few dozen duplications against a table with a
      million rows in it. A type is only discoverable while something on the machine holds a handle of
      it, so the pass repeats across samples until all five are known — running it once left any type
      absent from the first sample unknown for the life of the program, which is a thing that was
      observed rather than imagined. An index nothing could name leaves that column **missing rather
      than nought**, because a nought there would be a finding made out of an absence (§5.3).

      The walk itself takes a span and carries no platform attribute, so it replays on every leg
      against hand-built tables; the naming pass is the half that only runs on Windows
- [x] `semaphore.count` — **Windows only**; a POSIX semaphore is a mapped file and a System V one
      belongs to no process at all, so neither is countable per process. Out of the same pass
- [x] `mutex.count` — **Windows only**; a futex has no kernel object to count. Out of the same pass,
      and matched on the kernel's own word for the type: the object manager calls a mutex a
      **Mutant**, and matching on "Mutex" would leave this column empty on every Windows there is
- [x] `section.count` — **Windows only**; the Linux equivalent is a mapping and lives in §34. Out of
      the same pass
- [x] `regkey.count` — **Windows only**; there is no registry. Out of the same pass. Keys left open
      are the classic reason a configuration change does not take effect until something is restarted
- [x] `user.objects` — `GetGuiResources(GR_USEROBJECTS)`. **Windows only.** Not a handle and not in
      that table: the desktop's own quota, ten thousand per process by default, which a program can
      exhaust while every other counter on its row still looks healthy. Its own switch rather than
      the tally's, because the cost has a different shape — a call per process per *sample*, and
      uncacheable, the number moving being the whole point of the column.

      The call returns nought both for a process holding no such objects and for a call that failed,
      and says so in its own documentation. A console service really does hold none, and that is a
      measurement; so the last error is cleared before the call and asked afterwards, and the two
      cases are told apart rather than both reported as nought (§72.3)
- [x] `gdi.objects` — `GetGuiResources(GR_GDIOBJECTS)`. **Windows only**, and the same quota, the
      same switch and the same nought-is-not-a-failure reasoning as the line above
- [ ] `mach.ports` — **macOS only**, and the macOS probe is a stub (§6.3). Neither Windows nor Linux
      has anything of the kind to count
- [ ] `ipc.count` — **not answerable per process on Linux.** System V queues, semaphores and shared
      memory belong to the kernel rather than to a process: `ipcs` lists them by creator, and a
      segment stays after everything that attached it has exited. Attached shared memory is visible
      in `maps` and belongs in §34, not in a count of things a process holds

Everything still unticked here names the platform it belongs to, and between them they cover the
whole of the remainder. The seven **Windows-only** object types are now answered — `event.count`,
`semaphore.count`, `mutex.count`, `section.count` and `regkey.count` out of one pass over the
machine's handle table, `user.objects` and `gdi.objects` out of the desktop's own quotas — and what
is left is three lines that no amount of writing would fill: one is **macOS-only** (`mach.ports`) and
the macOS probe is a stub, one is a high-water mark **no Linux interface publishes**
(`handles.peak`), and one is **not a per-process quantity on any of them** — a System V object
outlives every process that touched it, so `ipc.count` is a question about the kernel rather than
about a row in this table. Nothing is left here that Windows or Linux could answer and does not.

The per-type tallies are one pass over a handle table that already exists — and they do **not** sit
in the sample loop for a run that did not ask. That rule is what the ticks above are built on rather
than an exception to it. On Linux the pass costs a `readlink` per descriptor on top of the listing
that was already the most expensive read in the sampler; on Windows it is one query for the whole
machine, which is cheaper and is still megabytes of table per sample. Either way the whole table is
never scanned for a column nobody opened, and the two desktop quotas — which are a call per process
per sample and cannot be cached — have a switch of their own (§5.4).

# 21. Process table — security fields

- [x] `elevated` — the effective uid on Linux, `TokenElevation` on Windows
- [x] `integrity` — the last sub-authority of the token's mandatory label: untrusted, low, medium,
      medium+, high or system, and the raw number for anything Microsoft adds later
- [x] `protected` — protected-process status. **Windows only.** Whether the kernel is keeping other
      processes out of this one: a protected process cannot be opened for reading, injected into or
      debugged even by an administrator, which is why a debugger that will not attach to it is not a
      fault. Derived from the level below rather than read separately — there is one reading and two
      questions
- [x] `protection.level` — **Windows only.** Which protection the process holds, by the signer class
      that granted it: the Windows trusted computing base, an antimalware service, the store, or an
      Authenticode publisher. "none" is a real answer and the answer for nearly every process on a
      machine.

      Read through `GetProcessInformation(ProcessProtectionLevelInfo)`, which is documented and needs
      only `PROCESS_QUERY_LIMITED_INFORMATION` — the same right the owner lookup already holds, so it
      is one more call on a handle that is already open and cached for the process's lifetime. The
      undocumented `NtQueryInformationProcess(ProcessProtectionInformation)` answers the same question
      through a structure Microsoft has never published, and this program does not read structures
      nobody can check (§8.3).

      **The two constants that would have been silently wrong.** `PROTECTION_LEVEL_NONE` is
      `0xFFFFFFFE`, not the `-1` a sentinel usually is — `0xFFFFFFFF` is `PROTECTION_LEVEL_SAME`,
      which is a *set*-side value that never comes back from this call. And **nought is a real
      level**, `PROTECTION_LEVEL_WINTCB_LIGHT`, so a record nobody filled must not read as the most
      protected process on the machine. Neither number appears on any Microsoft reference page: the
      page for the structure prints the constant names against an empty value column, so they were
      taken from `winbase.h`, and that is stated here because it is the weakest-sourced constant in
      this section
- [x] `signature.status` — see §70's vocabulary. **Windows**: Linux binaries carry no embedded
      signature to verify, and what signs a Linux program is its package — a different question with
      a different answer, which is `package.status` rather than this column. **macOS is a stub
      (§6.3)**.

      This section drew a line for a long time and drew it in the wrong place, which is the mistake
      the mitigation policies made too. `WinVerifyTrust` is a verdict about *trust*, settled by a
      machine's root store, its revocation lists and its policy providers, and a verifier nobody can
      run against the OS it describes is still worse than an empty one (§9.2). But that was never the
      whole of what this column needed. The other half is a documented on-disk layout and a
      documented digest, which is precisely what made the version resource above buildable from here:
      the certificate table out of the image's fifth data directory, the PKCS#7 signed data inside
      it, the Authenticode digest recomputed over the bytes the specification names, and the signer's
      signature over that digest checked against the certificate's own key.

      So the column answers §70's **second** question and says which one it answered in a sentence on
      every row: the running bytes still match the digest the signature covers. That is the same
      question `package.status` asks on Linux, put to the other kind of evidence. The chain is
      **not** checked and `trust.chain` stays empty beside it, because a good signature by an unknown
      key is a different finding from a good signature by a known one — which is §70's first
      requirement and the entire reason these are two columns.

      **Held against images this program did not produce**, which is the only corroboration worth
      having: the recomputed digest reproduces the recorded one byte for byte on six binaries signed
      by Microsoft and by the .NET Foundation, and one of those recorded digests and one of those
      countersignature times were read out independently with `openssl asn1parse`. MD5 is refused
      rather than computed — a 1998 VeriSign-signed font installer on the machine this was written on
      carries one, and its certificate and countersignature read correctly while its digest is
      declined — and it reads as "verification error"
      naming the algorithm, because putting this program's word behind a digest forgeable since 2004
      would be worse than saying nothing. Only the primary signature of a dual-signed image is read,
      and this says so rather than quietly reporting the stronger of the two. Nothing in it is
      platform-specific, so the whole of it runs on every CI leg; and on the `windows-latest` leg
      `--self-test` walks the table for images under the system directory and fails unless at least
      one that carries a signature verifies — a real Microsoft signature on a real kernel rather than
      this program agreeing with itself. Where no signed system image was readable at all the run
      records that instead of failing, which is both an account that may read none and the Wine leg,
      whose system files nobody signed: neither disproves anything
- [x] `signer` — the signing certificate's common name, out of the same verification. **Windows**;
      **macOS is a stub (§6.3)**. Emphatically not the `company` column below, which is a string a
      publisher typed into a version resource and which anybody at all may type: this one is bound to
      the key the signature was made with, and the column above says whether that binding still holds
- [x] `cert.subject` — that certificate's whole subject, for where a common name does not tell two
      publishers apart. **Windows**; **macOS is a stub (§6.3)**
- [x] `cert.issuer` — who issued the signing certificate: who put their name to the signer, and
      **not** who this machine trusts. Nothing behind this column has looked at a root store, which
      is what `trust.chain` is for. **Windows**; **macOS is a stub (§6.3)**
- [x] `signature.timestamp` — **Windows**; **macOS is a stub (§6.3)**. Its own field because a
      countersigned timestamp is what keeps a signature valid after the certificate behind it has
      expired, which is the ordinary state of most signed software — and it is what makes `Expired`
      in the column above readable rather than alarming. Both forms are read, because both are in the
      wild: the RFC 3161 token modern signers attach, and the PKCS#9 countersignature older ones do.
      Read structurally rather than through `Rfc3161TimestampToken`, which refuses tokens real files
      on this machine carry — a Microsoft-signed compiler from 2021 among them — and a date reported
      as absent because a stricter decoder declined it would be a false "never countersigned"
      (§72.3). "none" is a real answer, is the answer for a great deal of signed software, and reads
      as one
- [x] `hash.sha256` — on demand only, and on every platform that has a file to hash: the digest of
      the running image, which is neither a signature nor a verdict (§70). Hashed once per image
      rather than once per process — three hundred processes of one runtime share one binary — and
      again when that file is replaced underneath them, which is the case somebody watching this
      column is watching for. Linux fills it; the Windows probe does not call it yet and says so.
      Verified against `sha256sum`
- [x] `hash.sha1` — the same bytes under the older digest, from the same single read of them. Kept
      because so many package manifests and threat feeds are still keyed by it, and collidable since
      2017: on its own it is evidence of nothing. Verified against `sha1sum`
- [ ] 🟡 `reputation` — opt-in, see §70. **Not implemented on any platform**; it is a network
      service rather than an OS reading, so there is no OS to write it against and no platform it is
      missing from. The one line in this section that is unbuilt everywhere for a reason that is not
      about any operating system. Partial rather than open because the half that can exist without a
      provider does: the column, the vocabulary and the slot are there and say `n/i` out loud, which
      is what stops a digest computed on this machine from ever being read as a file sent from it.
      What is missing is a provider, and that box belongs to §70
- [x] `dep` — **Windows only.** Linux has NX on every mapping and no per-process policy to report.

      All six mitigation lines are read the same way and share one switch, and the six are worth
      keeping apart from what Linux publishes for a reason the `cet` line below spells out: these are
      **requests**, not states. What was asked for on behalf of a process and what the hardware
      underneath it is doing are two questions, and Windows is the platform that records the first.

      Each is held as the raw flags word its `PROCESS_MITIGATION_*` structure is a union over, and
      the bits are decoded where the column is rendered — which puts the part most likely to be
      wrong, a bit position off by one, into portable code with a test case per bit rather than into
      interop nobody here can run. A bit Microsoft adds later stays visible instead of being rounded
      into whichever of this build's words is nearest.

      Permanent is the interesting half of this one: DEP that cannot be turned off again is a
      stronger statement than DEP that happens to be on at the moment somebody looked, and it is the
      one field of the six structures that lives outside the flags word.

      **Opt-in, unlike the token fields above.** `GetProcessMitigationPolicy` documents
      `PROCESS_QUERY_INFORMATION` rather than the limited form, so this is a second open with a
      stronger right and six calls on it. For another user's process it will usually fail, and each
      policy says so on its own rather than six mitigations being reported as absent — a mitigation
      reported off when nobody was allowed to look is the worst cell on this row (§72.3)
- [x] `aslr` — **Windows only** as a per-process mitigation policy. Linux's is the machine-wide
      `kernel.randomize_va_space` plus whether the image is a PIE, which is §53's business. Four
      bits, named separately rather than folded into a yes: bottom-up allocations, forcing relocation
      on images that would rather not move, high entropy, and refusing stripped images are four
      different things to have asked for
- [x] `cfg` — **Windows only.** Whether indirect calls are checked against the set of functions the
      compiler said were callable. Everything after the enable bit qualifies it — export suppression
      without CFG enabled is not weaker CFG, it is no CFG — so the first bit decides whether the cell
      says anything at all
- [x] `cet` — **Windows only** as a policy field, and the policy is the part Linux has no answer to:
      there is no per-process record of what was *asked* for. What Linux does publish is what is
      switched **on**, which is `shadow.stack` below — a reading rather than a policy, and a better
      question. The CPU's own support for it is a machine capability and is already in §46. Strict
      mode upgrades the word rather than being listed beside it: "on, strict" would read as two
      policies where there is one at two strengths. `ProcessUserShadowStackPolicy` arrived in Windows
      10 2004, so an older Windows refuses this one of the six and says so
- [x] `acg` — **Windows only.** Whether the process is forbidden to generate code at runtime or make
      existing code writable — a just-in-time compiler cannot run under it, which is why a browser
      enables it in the processes that have no need to. Audit is **its own state and not a weaker
      "on"**: under it nothing is prevented, only watched, and reporting that as on would claim a
      protection is in force while nothing is being stopped (§5.3)
- [x] `cig` — **Windows only.** Which signatures an image must carry before this process will load
      it. It restricts what may be *loaded into* the process and says nothing about whether what is
      already loaded was signed, which is the signature fields above and is a different question
      wearing similar words
- [x] `sandbox` — **Windows (AppContainer) and macOS (Seatbelt).** Linux has no single sandbox flag:
      what confines a process here is the seccomp mode, the LSM label and the namespace set, and all
      three are already their own fields. One "sandboxed: yes" over them would answer less than any
      of them does (§5.3). On Windows the sandbox *is* the AppContainer, so this and the line below
      are one question with one answer and `sandbox` is the other spelling of it rather than a second
      column saying the same thing twice. **macOS is a stub (§6.3)**, so the Seatbelt half is
      unanswered
- [x] `appcontainer` — **Windows only.** `TokenIsAppContainer`, off the same token the owner and the
      integrity level come from, so it costs nothing beyond them. The sandbox a packaged application
      and a browser renderer are put in, which decides what a process may reach rather than who it
      runs as
- ∅ `capabilities` — the AppContainer capability list. **Windows only**; Linux capabilities are
      `caps.linux` below and are a different thing wearing the same word. **Refused as a column, not
      deferred.** The flag above says a process is in an AppContainer and this would say what that
      container is allowed to reach, which is `TokenCapabilities` — a variable-length list of
      capability SIDs, each needing a name lookup, for a list that is empty on every process that is
      not a packaged application. That is a per-row allocation of unbounded size on a path with a
      budget of zero (§4). It belongs in §36's on-demand view with the other expensive security
      readings, and putting it there is a decision about where the reading lives rather than a gap in
      this table — which is why it is no longer a box here
- [x] `selinux.context` — `/proc/pid/attr/current`, opt-in
- [x] `apparmor.profile` — same file, same field: the LSM label is one value whichever module wrote it
- [x] `lsm.mode` — the part of that label which is not the label: AppArmor writes how hard it is
      applying the profile in brackets after it, and `(complain)` against `(enforce)` is the whole
      difference between a rule that is written down and a rule that happens. Out of the string the
      label column already read, so it costs nothing beyond it and is bought by the same switch.
      An SELinux context states no such thing — four fields, none of them an enforcement setting,
      and the machine-wide `enforcing` flag is not a property of any one process — so it renders
      `n/a` there rather than claiming a mode nobody set (§5.3). Held against the documented label
      forms rather than against a live module: neither AppArmor nor SELinux was loaded on the
      machine this was written on, which is stated here because it is the weakest verification in
      this section
- [x] `spec.ssb` — whether speculative store bypass is mitigated for this process, and whether the
      process chose that or had it chosen for it: the seven words `fs/proc/array.c` writes for the
      value `prctl(PR_GET_SPECULATION_CTRL)` would return. This is what Linux has in place of the
      Windows mitigation policies above, and it is a better thing to have — a state rather than a
      request — but it is emphatically not the same field, so it is not one of them. Verified
      against the kernel by driving `PR_SET_SPECULATION_CTRL` on a child and reading its `status`
      back: `PR_SPEC_DISABLE` produced "thread mitigated", `PR_SPEC_FORCE_DISABLE` produced "thread
      force mitigated". Ordered by exposure so that sorting brings the unmitigated rows to the top,
      and a word this build cannot name is reported as unrecognised rather than rounded towards safe
- [x] `spec.ib` — the same for indirect branch speculation, and a separate field because it is a
      separate control: a process may have asked for one and not the other, which processes on this
      machine do. Eight words rather than seven, and not the same eight — the kernel writes
      "unsupported" here where the line above writes "unknown". Arrived in 5.11, four kernel
      versions after the seccomp filter count and seven after the line above, so a machine can
      honestly have one and not the other
- [x] `shadow.stack` — the hardware protections the kernel has switched on for the process, from
      `x86_Thread_features` (6.6 and newer). The honest Linux answer to what `cet` asks: a binary
      built `-fcf-protection=full` still runs without a shadow stack unless the loader turned one
      on, and this is the field that tells those two apart. Verified by building exactly such a
      binary and running it under the loader tunable that enables the feature — the column read
      `shstk` where pid 1 beside it read `none`. Empty is a real answer and the ordinary one; the
      line being absent, on a kernel before 6.6 or on any machine that is not x86, is not that
      answer and does not read like it
- [x] `umask` — which permissions are withheld from every file the process creates. Free, in the
      status already open, and a finding no other column on the row would show: a daemon running
      with a mask of nothing makes world-writable files. Parsed base eight, because reading `0022`
      as twenty-two names a mask nobody holds, and verified against the shell builtin
- [x] `tracer` — which process is attached to this one as a debugger, by pid rather than as a yes or
      no, because "something is reading this process's memory" is only half the question. Nought is
      a real answer and the usual one. Verified across a real `PTRACE_ATTACH`: nought, then the
      tracer's pid, then nought again on detach
- [x] `seccomp` — off, strict or filter
- [x] `seccomp.filters` — how many filter programs are attached, where the kernel says (5.9 and
      newer). Several of them is a process something has sandboxed more than once, which the mode
      alone cannot express; an older kernel leaves it unknown rather than reporting none
- [x] `caps.linux` — all five sets, by capability name rather than as sixteen hex digits: `caps`
      (effective), `caps.permitted`, `caps.inheritable`, `caps.bounding` and `caps.ambient`, with
      `caps.hex` carrying the raw effective mask in the form `capsh --decode` accepts. The bit→name
      table is held against the kernel's own `uapi/linux/capability.h`, vendored beside the tests
- [x] `setuid` — whether the process is running as somebody other than whoever started it: real and
      effective ids that disagree, for the group as well as for the user
- [x] `uid` / `uid.effective` / `uid.saved` / `uid.fs` and the four `gid` equivalents — the whole
      quartet, because a process whose real and effective ids are an ordinary user while the saved
      one is root has given up nothing
- [x] `user.effective` — the account whose authority the process is using, which for anything
      set-user-ID is not the account in the `user` column
- [x] 🟡 `groups` — the supplementary groups as the kernel numbers them; opt-in, because the line is
      free to read and costs one string per process per sample to keep (§5.4). Not resolved to
      names: that would need a second name service beside the passwd one
- [ ] macOS: code-sign identity, entitlements, hardened runtime, sandbox. **macOS only**, and the
      macOS probe is a stub (§6.3) — so this is not four unwritten readings but a whole unwritten
      platform, and nothing here will move until §6.3 does

- [ ] **Online reputation checking is opt-in, and the program states exactly what is transmitted
      before the first time it happens** — at the point of use, not buried in a settings page.
      **Blocked on there being anything to disclose.** Nothing is transmitted about an executable by
      any code path in this program, and a sentence describing a transmission that cannot happen
      would be a worse thing to ship than no sentence: it would teach a reader that the program does
      send something. This box opens when §70's provider box does, and not before

The five lines added above are **opt-in**, and for a cost that is neither a read nor an allocation,
which is why it had to be measured rather than argued about. Every one of them is in the `status`
the sampler already has open, so they buy no syscall — but recognising five more labels in a loop
that runs about fifty times for each of six hundred processes every second cost seven to eight
milliseconds of CPU per thousand processes when every run paid it, against a whole-sample budget of
twenty-five (§71.2). Interleaved against `main`, alternating which leg ran first, the branch was
slower in eleven pairs out of eleven. Moving the work out of the loop recovered most of it and the
switch buys the rest, so a run that names none of these columns measures level with `main` — which
is what §5.4 asks for and, on this evidence, is not a rule that only applies to expensive reads.

Two boxes are left in this section, and neither is waiting on anything that could be written here.
The macOS line is a whole unwritten platform rather than four unwritten readings, and nothing on it
moves until §6.3 does. The reputation disclosure is blocked on there being a transmission to
disclose, which there is not.

The protected-process status, the six mitigation policies and now the five signature fields were all
once refused on the grounds that they could not be written honestly from a Linux machine. They were
refused on a line drawn in the wrong place. In every one of these cases the part that cannot be done
here is the *call* — or, for the signatures, a *verdict about trust* that depends on a machine's root
store — and never the reading of a documented structure. So the calls are gated on Windows and the
decoding lives in portable code with a test case per bit and per state, which every CI leg runs, and
the calls themselves are executed by `--self-test` on the `windows-latest` leg, which names every one
of these columns because §5.4's opt-in rule would otherwise mean the interop never ran anywhere.

The signature fields are the clearest case of that line being moved rather than crossed. What is
built is arithmetic over a documented on-disk layout and a signature check against a key that is in
the file — every step of which runs on a Linux machine and was held there against real signed
binaries. What is still not built, and is not promised anywhere, is the chain: `trust.chain` sits
beside these five and stays empty on Windows, which is the honest shape of "the signature is the
signer's, and whether anybody trusts the signer is a question nothing here asked".

The hashes were on the wrong side of the same line, until they were read properly: hashing a file is
the same operation on every operating system, is verifiable here against `sha256sum`, and says
nothing about signatures or trust — which is precisely why it could be built while the fields around
it could not.

# 22. Process table — energy fields

Every one of these is an **estimate** wherever the OS does not measure it directly, and is labelled
as such. Windows models energy impact from weighted CPU/disk/network; Linux has RAPL for the package
and nothing per-process. A model presented as a measurement is exactly the dishonesty §72.3 exists to
prevent.

**All nine are refused on Linux, not deferred.** This was gone looking for rather than assumed, and
what the looking found was that the two interfaces which could attribute energy to a process both
refuse to, by construction rather than by omission:

- `/sys/class/powercap/intel-rapl*/energy_uj` is the package counter, and it is **root-only**: mode
  0400 since 5.10, changed by `powercap: restrict energy meter to root access` in response to
  CVE-2020-8694, because the counter leaks what other processes are computing. Verified on the
  machine this was written on — `-r-------- root root`. So even the machine figure is `—` for an
  ordinary user, and must say "not permitted" rather than nought (§72.3).
- The perf RAPL PMU cannot be opened per task **at all**. `arch/x86/events/rapl.c` sets
  `task_ctx_nr = perf_invalid_context`, and `perf_event_alloc` refuses any event on such a PMU that
  names a task or a cgroup with `EINVAL`. The commit that introduced it says so in as many words:
  "the RAPL PMU is uncore by nature and is implemented such that it only works in system-wide mode".
  There is no per-process energy to read, not one that is hard to reach.
- NVML publishes no per-process power either. Its per-process query answers pid, process name and
  used GPU memory, and nothing else — checked against `nvidia-smi --help-query-compute-apps` on a
  card that has processes on it. §19 already refused `gpu.power` on these grounds and this is the
  same refusal seen from the other side.

That leaves splitting a machine-level reading by each process's share of something else, which is a
model, and §72.3 is the rule that a model may not be shown where a measurement is claimed. Nine
columns of it would be worse than nine empty ones, because a number on screen is read as a reading.

**One of the nine turned out not to be an energy figure at all**, and that is what got it built. Two
of the lines below ask what quality of service a process has been given rather than what it is
spending, and Windows answers exactly that through `GetProcessInformation(ProcessPowerThrottling)` —
a documented information class, a documented pair of bitmasks, and only
`PROCESS_QUERY_LIMITED_INFORMATION` to read it. Nothing about it is modelled: it reports what was
*asked for* on a process's behalf, which is a state, and it is the same kind of reading the six
Windows mitigation policies of §21 are. The masks are decoded in portable code with a test per state
and the call is gated on Windows, which is §21's arrangement for the same reason. So the count below
is not nine refusals; it is seven, one blocked platform, and two readings.

The seven are marked ∅ rather than left open. They are not work waiting to be done: each is a figure
that either does not exist per process anywhere, or exists on Windows only inside a structure
Microsoft has never published — and §8.3 forbids reading those as firmly as §72.3 forbids modelling
them.

- ∅ `power.usage` — **no per-process source this program will read, on any platform.** Linux has
      RAPL for the package, which the two paragraphs above show cannot be attributed to a process.
      Windows does have a number and Task Manager shows it, out of an undocumented extension to
      `SYSTEM_PROCESS_INFORMATION` that Microsoft has never published — and §8.3 refuses that for the
      same reason `protection.level` is read through the documented call rather than the undocumented
      one. Refused, not deferred
- ∅ `power.trend` — the derivative of a figure that does not exist
- [ ] `energy.impact` — **Windows and macOS**, and a weighted model on both. It is the one field here
      that is honest *as* a model, because both platforms define it as one and label it so; on Linux
      there is no vendor definition to be faithful to, and inventing weights would make this
      program's arithmetic look like the operating system's measurement. **Open on macOS only, and
      blocked on §6.3**: the Windows half comes out of the same undocumented structure as
      `power.usage` and is refused with it, so what is left here is a whole unwritten platform rather
      than an unwritten reading
- ∅ `energy.cpu` — would need RAPL charged to a task. See above: the PMU refuses the scope
- ∅ `energy.gpu` — **not published per process by any driver.** §19 refused this already
- [x] `qos.background` — **Windows (EcoQoS).** Linux has no energy quality of service per process:
      what it has is the scheduler class and the cgroup's `cpu.idle`, which are scheduling decisions
      rather than energy ones and are already `sched.class` in §15, and the processor's
      energy-performance preference is per core rather than per process. **macOS is a stub (§6.3)**,
      so its QoS classes are unanswered.

      Three states and not two, which is the whole reason both of the structure's masks are carried.
      The control mask says which behaviours the process has an opinion about and the state mask says
      what that opinion is, so a bit absent from the control mask is one nobody has set — the system
      decides, which is what nearly every row on a machine is. A decoder that read only the state
      mask would report an untouched process as "not throttled", which claims somebody asked for full
      speed when nobody asked for anything (§72.3). The second bit in the same word is named rather
      than dropped: whether the process's requests for a finer system timer are ignored is a real
      saving and a separate decision.

      Read per **sample** rather than cached for a process's lifetime, unlike everything else that
      comes off a process handle here — an application may set its own throttling at any moment and a
      person may tick "efficiency mode" against any row of Task Manager, and a column watching for
      exactly that change must not remember the answer. That is what makes it opt-in (§5.4)
- [x] `eco.state` — **Windows only**, and the same reading as the line above wearing the name the
      operating system's own window uses for it, the way `protected` and `protection.level` are one
      call and two questions. Kept apart from it because "off" and "system managed" are different
      findings and a yes-or-no column would have to round one into the other
- ∅ `thermal` — **not a per-process quantity anywhere.** A processor has a temperature and a
      process does not; the machine's sensors are read and are on the performance page (§46)
- [ ] `battery.impact` — **macOS only**, modelled there too, and blocked on §6.3 like the rest of
      that platform

Machine-level RAPL is a different matter and a legitimate one: it is a real measurement of a real
thing, it is simply not a property of a process. It belongs beside the other machine readings on the
performance page (§45, §46) rather than in this table, and when it is added it must render "not
permitted" for an unprivileged reader rather than a nought, for the reason in the first bullet.

---

# 23. Process highlighting

- [x] Highlight colours are configurable — the settings file names every one of them, and the
      thresholds behind the two washes are settable from the window as well, under
      View ▸ Highlighting thresholds and from the legend itself
- [x] Every colour the table paints is explained by a dialog — the row categories and both cell
      marks, with the numbers the marks are currently being judged by (§7.1)
- [x] 🟡 Highlighting is never the only signal — the number is in the cell it washes, so the table
      reads without colour at all; an explicit high-contrast mode is not detected yet (§45.9)

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
- [x] Packaged application — a Flatpak, a snap or an AppImage, from `package` (§14). Deliberately not
      "some package owns this file": `pacman` and `dpkg` own nearly every binary on a machine, and a
      colour that paints nine rows in ten distinguishes nothing. What is worth a colour is the
      application that brought its own filesystem with it, which is what Windows means by the word
- [x] Managed runtime — from `runtime` (§14), which reads the module list rather than the name.
      `native` is a finding and is not coloured; `unknown` is nobody having looked and is not either
- ∅ Unsigned executable — **refused, not deferred, and the refusal does not rest on there being no
      signature.** Since §21 was written, Windows images really are verified: the digest is
      recomputed and the signer's signature checked over it. The argument that survives is the one
      below, which never depended on the platform. On Linux the two columns that come nearest each
      answer something else: `package.status` is whether the bytes still match what the
      package recorded, and `trust.chain` is whether anybody this machine trusts signed for that
      package. `Unsigned` in the trust chain is the ordinary reading for a package built on the
      machine it runs on, so a colour taken from it would paint every developer's own build as
      suspect.

      §70's whole design is that the word is meaningless without the column it is in — the same
      vocabulary answers five questions, and which one was asked is the heading. **A row colour has
      no heading.** That is the argument, and it does not depend on which of the five is read
- ∅ Invalid signature — as above. On Windows a signature can now genuinely be invalid, and it is
      still not a row colour, for the same reason: the word is meaningless without its heading
- [ ] Suspicious reputation — needs opt-in reputation, and §97 promises nothing about an executable
      leaves this machine unasked. There is no provider to ask
- [x] High CPU
- [x] High memory
- [x] High disk
- ∅ High network — no per-process byte counters exist to threshold, and §18 refuses them rather
      than deferring them: every workaround produces a number wrong in a way nothing on screen
      would betray

**The mark goes on the cell, not the row.** A row's colour already answers a different question —
what kind of process it is (§7.1) — and that is a one-of-many answer, while how much CPU something
is using is a separate axis: a system process can be busy without stopping being a system process.
Colouring the row for both would mean one of the two facts quietly winning.

Two bands rather than one, warm and hot, because "above a line" throws away the difference between a
process using half a core and one using six.

**CPU is judged per core, not normalised.** "It is eating a core" is what somebody is looking for,
and a normalised percentage turns a fully held core on a thirty-two thread machine into 3 % — which
is nothing. A cell reading 10 % normalised can therefore be marked, and correctly: on sixteen threads
that is 1.6 cores.

Thresholds are settable (`heat.cpu.warm` and its five siblings, §67), because the right answer
depends on the machine: a whole core is a lot on a laptop and nothing on a build server, and a
hundred megabytes a second is saturation for a spinning disk and idle for an NVMe. A threshold of
nought turns its band off rather than marking everything, and a line that will not parse leaves the
setting alone — a threshold of nought is the most annoying possible response to a typo.

A reading that does not exist is never marked, in either direction. `default(Rate)` is a confident
zero, so an unread counter compares as cold; a counter that came back *not permitted* is not a
measurement at all (§5.3).
- [x] High GPU — `heat.gpu.warm` and `heat.gpu.hot`, a third of the adapter and three quarters of it.
      Deliberately not the CPU's numbers: a GPU percentage is already a share of the whole device
      where a CPU percentage here is a share of one core out of many, so a band set at a hundred
      could only ever fire on a benchmark. Each engine column is marked from its own reading and the
      summary column from the busiest engine; the graphics-memory columns are bytes and are left
      alone, because a percentage threshold has nothing to say about them.
- ∅ Process with an active UI window — **refused on Linux.** §39's own finding is that a Wayland
      client cannot enumerate other clients' surfaces by design, so the colour would appear on
      XWayland rows and on nothing else. A mark that is present for a third of the windows on a
      modern desktop and absent for the rest describes which toolkit a program was built with
- [x] Process with a changed executable — the kernel appends `" (deleted)"` to the `exe` link of a
      process whose image has been unlinked, which is an upgrade having replaced the file underneath
      it. No mtime watch and no hash: the kernel states this rather than the program inferring it,
      which is the difference between this and the two signature lines above.

      **It under-reports and never over-reports.** The link is read once per process, so an image
      replaced while this program is already watching shows on the next full build of the table
      rather than at the moment it happens. The reverse is impossible — a path carrying the marker
      was never a live file. An under-reported colour costs a reader one discovery; an over-reported
      one costs them their trust in every other colour.

      It is the one row colour deliberately louder than the rest, and it outranks System: after an
      upgrade the rows that need restarting are almost all root daemons, and painting them the same
      blue as every other daemon is exactly how they are missed
- ∅ Process containing the selected search match — the match is already marked, on the run of
      characters it matched, which is §11's wash and points at the word rather than at the row. A
      second mark on the row would say the same thing less precisely. The one case the cell mark does
      not cover is a match in a column that is not showing, and that is a reason to show the column

The ones that are ticked are the ones the program can *prove*. The rest stay off rather than
guessing: a colour claiming "unsigned" without having checked a signature is worse than no colour.

**A row has one colour and most processes qualify for several, so the order is part of the design.**
Two rules settle it. *The transient beats the permanent*: started, ended, stopped and
running-a-deleted-image were all untrue an hour ago and will be untrue again, while being root, or
yours, or a service is true for the process's whole life and is in a column besides. And *the two
identity colours only ever replace "nothing distinguishing"*: packaged and managed-runtime are tested
after privilege and service membership, so a .NET service stays a service and a snap running as root
stays a system process. They take the place of "yours" and "somebody else's", which is where the
palette had nothing to say.

Neither of those two can crowd the default table, because neither is painted unless the `package` or
`runtime` field is switched on — both cost a read, and an unread field is never marked in either
direction. Somebody who switched one on is looking for exactly this.

---

# 24. Process tooltip / quick inspector

- [x] 🟡 Hover inspection shows: name ✔ (as the heading, because it is what the pointer is on) ·
      PID ✔ · parent ✔ and its name ✔ · user ✔ · state ✔ · kind ✔ · start time ✔ · CPU ✔ · memory ✔
      (private and working set) · I/O ✔ · threads ✔ · detected runtime ✔ · package ✔ · container ✔ ·
      path ✔ · command line ✔.

      **Network and window title are missing on purpose.** Per-process network bytes have no portable
      source at all, which §18 refuses rather than approximates, and a window belongs to a process
      only on a compositor that will say so — §39's finding is that most of them will not. Signature
      and service names are readable and are not in the list yet.

      It answers "what is this?" without changing what is selected, which is the one thing the lower
      pane cannot do: the pane describes the row somebody chose and this describes the row they are
      looking at, and on a table of four hundred rows those are usually different questions
- [x] **Tooltips perform no expensive synchronous collection** — and this holds by where the text
      comes from rather than by a rule somebody has to keep remembering. Every line is the same
      `FieldAccessor` the columns use, against the record already in the snapshot, and that accessor
      reads the record and nothing else: there is no path from a mouse-move message to a file. A
      field this run never asked for renders as the mark for "nobody looked", which is what a tooltip
      should say about it. Asserted by describing a record nobody ever filled in — anything that went
      and looked would find something, and this cannot.

      It is also rebuilt only when the pointer crosses into a different row, because composing
      seventeen fields on every mouse-move message is work done hundreds of times a second to produce
      the same string

---

# 25. Process actions

## 25.1 Lifecycle

- [ ] 🟡 End task gracefully — on a Linux desktop this is now `WM_DELETE_WINDOW` to every window the
      process owns, falling back to `SIGTERM` for a process that has none. Windows `WM_CLOSE` to the
      main window is still missing
- [x] Terminate
- [x] Terminate process tree
- [x] Restart
- [x] Suspend
- [x] Resume
- [x] **Send any signal** — the whole of `kill -l` and not the three that have menu items, offered
      from a list that says what each one *does* rather than only what it is called
- [x] **Freeze / thaw the whole cgroup** — what stopping a unit means on Linux, and deliberately not
      described as a suspend

- [x] "End task" and "Terminate" remain semantically distinct — the first asks, the second does not.
      A UI that blurs them loses somebody's unsaved work.

**Ending a task politely means asking the window, not signalling the process.** `WM_DELETE_WINDOW`
is the message a close button sends and the one a toolkit's own handler is written for: it is what
makes an editor offer to save rather than vanish, and it can be declined, which is the whole
distinction from terminate. It is sent to the window rather than routed through the window manager's
`_NET_CLOSE_WINDOW` because it then arrives whether or not there is a window manager running to
forward it. A window that does not list the protocol has said it does not handle being asked and is
not counted as asked; the only thing left for one of those is `XKillClient`, which severs a
connection rather than requesting anything and is not a polite close by any reading.

The two outcomes are reported distinctly — "its window was asked to close" against "it has no window,
so SIGTERM was sent instead" — because only one of them can still be refused by the program (§72.3).

**A restart reads everything it needs before it sends anything**, because none of it survives the
process: the `exe` link, the `cwd` link and the argument vector all vanish with it. If the process
has not gone five seconds later, no replacement is started — two copies of a program guarding a
socket or a lock file is a worse outcome than a restart that says it did not happen, and escalating
to `SIGKILL` is a decision for whoever is watching rather than for a menu item.

The replacement inherits *this* program's environment and not the old process's, deliberately.
`/proc/[pid]/environ` is the block the kernel laid down at `exec`, so for anything that has since
called `setenv` it is stale, and nothing outside can tell a stale block from a current one. Copying
it would produce a restart that quietly differs from the process it replaced (§5.3).

**The default action of most signals is to end the process, and that is the sentence the chooser
leads with.** `SIGUSR1` sent to a program that never installed a handler for it kills the program;
so does `SIGALRM`, `SIGPIPE` and `SIGHUP`. A menu offering thirty-one names and no consequences
would be offering to kill something under the label of poking it, so each row says what the kernel
does with it when nobody handled it, and the confirmation repeats it for the one that was chosen
(§5.5, §90). Only `SIGKILL` and `SIGSTOP` cannot be declined, and only those two say so.

**The numbers are not universal, so they are not assumed.** Linux uses one layout on x86, ARM,
RISC-V and LoongArch and another on Alpha, SPARC, MIPS and PA-RISC, where `SIGUSR1` is 30 and 10 is
`SIGBUS`. An architecture whose layout is not known refuses to offer any name at all rather than
sending a number that means something else there — the same rule the I/O priority syscalls follow,
and for a worse failure: a signal sent by the wrong number is not a failed action but a successful
one performed on the wrong thing (§5.3).

**Real-time signals are reachable by number and not by name.** There is no true number for
`SIGRTMIN`: it is whatever the C library the *target* was linked against reserved for itself — 34
with glibc, 35 with musl — and a sender cannot see which. Computing `SIGRTMIN+3` from this program's
own library and sending it to a process linked against another would deliver a different signal from
the one that process's own header names, so the number, which is unambiguous, is the half that is
offered.

**Freezing is an action on the cgroup and is never called a suspend.** `SIGSTOP` stops one process
and leaves everything it started running; the freezer stops the cgroup, every cgroup below it, and
anything either of them starts while it is frozen. That is what pausing a container or a service
actually means here, and the confirmation therefore names the cgroup and counts what is in it rather
than naming the row that happened to be selected — which is one member of it and usually not the
interesting one.

Two things about a frozen process were checked against a running kernel rather than written from
memory, and both were the opposite of the obvious guess. **A frozen task still reports itself as
sleeping**: there is no process state for frozen, so a task shows `S` in `/proc/[pid]/stat` whether
it was running or sleeping when the freeze landed, and nothing in a process table distinguishes a
frozen program from an idle one — only the cgroup's own `cgroup.events` will say. And **a fatal
signal still reaches it**: unlike the cgroup v1 freezer, v2 breaks a frozen task out for anything
that would end it, so a frozen process is not one that has to be thawed before it can be stopped.

**Freezing the cgroup this program is in is refused**, which is the one place the honest answer is
to decline what was asked: it would stop the window that is asking and leave nothing able to thaw
it. The root cgroup is refused for the same reason at machine scale.

A tree is ended deepest first, and every key in it is re-validated on its own before it is signalled:
a pid recycled halfway through a large tree is refused rather than acted on (§8.2). A member that has
already gone counts as ended, because killing a parent routinely takes its children with it and
reporting that race as a failure would make the ordinary case look broken. The refusal names how much
of the tree went.

- ∅ **Sending a signal to one thread.** `tgkill` will address a tid, and almost nothing useful
  follows from it: every fatal signal is acted on by the whole thread group whichever thread it was
  delivered to, and `SIGSTOP` and `SIGCONT` are group-wide by definition. What is left is delivering
  a handled signal to a chosen thread, which means something only to a program that has arranged its
  signal masks for it — and such a program has already made that choice itself. An item whose only
  honest description is "this may do what the whole-process one does" is a worse offer than no item,
  and it is the same ground on which per-thread suspend, resume and terminate were refused (§29).

- ∅ **Waiting for a process to exit.** Nothing here is the process's parent, so there is no `wait`
  to make: the only way to answer from outside is to poll `/proc` until the directory goes, which is
  what a shell loop already does without needing a menu item. Where this program needs it internally
  — a restart, which must not start a second copy beside a first that is still running — it polls,
  says so, and refuses rather than escalating when the time is up.

## 25.2 Scheduling

- [x] Set priority
- [x] Set nice value
- [x] **Set scheduling class** — the control nice cannot express: a task at `SCHED_IDLE` does not
      merely queue last, it runs only when the machine has nothing else to run at all, which is what
      makes a re-index invisible rather than merely quieter
- [x] Set processor affinity — through the helper where it needs privilege, and from a dialog whose
      boxes name what each core *is*: on a hybrid part "CPU 14" says nothing and "CPU 14 (E)" says
      which half of the machine is being pinned to (§46)
- [ ] Set CPU set — a cgroup here and an unrelated Win32 API there, sharing only a name. §5.3
      forbids mapping one onto the other
- [x] **Set I/O priority** — the control that makes a backup or an indexer stop making a machine
      unusable without slowing it down much: left at normal CPU priority but moved to idle I/O, it
      keeps running at full speed and yields the disk whenever anything else wants it
- [ ] Set page priority — Linux has no equivalent to set
- [ ] Enable/disable efficiency mode or platform QoS — the nearest Linux relatives are the
      scheduling class and the I/O class, both already settable; calling either of them a QoS class
      is the false equivalence §5.3 forbids
- [x] **Set resource limits** — all sixteen of the kernel's per-process ceilings, read and set, each
      shown beside what running into it actually does
- [x] **Per-thread priority and affinity** — from the Threads tab

All of it was unreachable until now: priority and affinity had been implemented in the actions layer
for some time and appeared in no menu and no flag, which is the same as not existing.

`ioprio_set` has no glibc wrapper and must be called as a raw syscall, so this is the one place in
the program that hard-codes syscall numbers — and they are architecture-specific, so an architecture
whose numbers are not known says so rather than calling something else by accident.

**Raising into the real-time I/O class is deliberately not routed through the privileged helper.**
It starves every other reader on the machine until the process finishes, which is a decision somebody
should take at a root prompt rather than by picking a menu item (§68).

A thread is checked to belong to the process the key names, not merely to exist. A tid is a number in
the same space as a pid, so a stale one may name a live thread of an unrelated process — checking
only the process would let the syscall land there (§8.2).

**The scheduler class is not nice on a different scale.** Nice orders processes *within*
`SCHED_OTHER`; a class change is a change of the rules they are ordered by, and the two controls are
offered separately for that reason. The classes are named as the kernel names them — `SCHED_IDLE`,
not "Low" — so that what a menu did can be checked against what `chrt` reports (§5.3).

`SCHED_DEADLINE` and `SCHED_EXT` are named as unreachable rather than attempted: a deadline task is
described by a runtime, a period and a deadline that `sched_setscheduler` has nowhere to put, and the
extensible class belongs to whichever BPF scheduler is loaded, if any. **Raising into a real-time
class is deliberately not routed through the privileged helper**, for the reason the real-time I/O
class is not — a `SCHED_FIFO` task that spins never yields the processor it is on (§68).

**Leaving `SCHED_IDLE` needs privilege and going into it does not**, which is surprising enough that
the refusal says so. The kernel scores `SCHED_IDLE` as nice 20, so moving out of it is a promotion
and is permitted only where `RLIMIT_NICE` reaches that far — at the default limit of 0 it never does.
Reported first as a plain "not permitted", which sends somebody looking for a permission that is not
the one in the way; found by asking `chrt` rather than by asking the code.

**A resource limit is read from one place and written to another, on purpose.** The reading side
parses `/proc/[pid]/limits`, which answers for a recorded tree as well as for a live process and is
therefore testable without a machine (§9.1); the writing side has no such choice, because that file
is read-only and `prlimit64` is the only way in. Always `prlimit64` rather than `prlimit`: the
latter's `rlim_t` is 32-bit on a 32-bit build unless the caller was compiled with
`_FILE_OFFSET_BITS=64`, which a P/Invoke has no way of having been, and a file-size limit above
4 GiB would then come back truncated on 32-bit ARM and nowhere else.

**Each ceiling is shown with what running into it does**, because that is never the same thing
twice: `RLIMIT_CPU` sends a signal, `RLIMIT_NOFILE` fails an `open`, `RLIMIT_AS` fails an allocation
the program probably does not check, and `RLIMIT_NPROC` is a limit on the *user* rather than on the
process it is being read from. Two of the sixteen do nothing at all — Linux has ignored `RLIMIT_RSS`
since 2.6 and `RLIMIT_LOCKS` since 2.4.25 — and they are shown saying so rather than omitted,
because the kernel still reports them and a sheet that quietly dropped them would look like one that
had failed to read them.

**Lowering a hard limit cannot be undone.** The kernel lets anybody lower one and lets nobody raise
it again without `CAP_SYS_RESOURCE`, which makes it the only irreversible thing on the sheet and the
one the confirmation names outright (§5.5). Unlimited stays a word and never becomes a number, the
same rule the cgroup limits follow (§38).

Still unticked and why: a **CPU set** is a cgroup on Linux and an unrelated Win32 API on Windows, and
the two share a name and nothing else; **page priority** has no Linux equivalent at all; and
**efficiency mode / platform QoS** is a Windows and macOS concept whose nearest Linux relative is a
combination of two controls already here, which is not the same thing and would be exactly the false
equivalence §5.3 forbids.

## 25.3 Navigation

- [x] Open properties (§26)
- [x] Expand / collapse tree
- [x] Go to parent
- [x] Go to children
- [x] Go to owning service — the innermost unit in the cgroup path, with the cursor put on that
      unit's row in the services list
- [ ] Go to package — the package is known and there is no package view to go to. Grouping by
      package gathers a process with its siblings, which is a different question
- [ ] 🟡 Go to executable — it can be revealed in the file manager and its properties opened; there
      is no destination inside this program to go to
- [x] Reveal in file manager
- [x] Open file properties — path, size, modification time, permissions, architecture and interpreter,
      with a SHA-256 on request
- [x] Copy path
- [x] Copy command line
- [x] Search Internet — confirmed, and the confirmation names the engine before it goes
- [ ] Inspect binary (§53) — needs a disassembler, which §53 has not been written

Navigation is grouped under one menu and none of it changes anything, which is what keeps it away
from the items that do (§5.5).

**A parent that is not in the list is the ordinary case rather than an error**: a process whose
parent has exited was reparented to init, and one belonging to another user is filtered out of the
view. Both are said out loud, because "nothing happened" is what a broken menu item looks like too.
Going to the children opens the tree rather than offering a list of them: they are already on screen
a row below, and a dialog listing what the window shows better would be answering the wrong question.

**Searching the web is the only item here that leaves the machine, so it is the only one that asks
first.** It puts the name of something running on this machine onto somebody else's server, and a
menu item that did that quietly would be a disclosure dressed as a convenience (§70). The engine is
named in the confirmation rather than taken from the session's default, because the session's default
is not discoverable without opening a browser to ask it.

Still unticked and why: **go to owning service** and **go to executable** are navigations to views
that do not exist yet — the services list is CLI-only and there is no binary inspector to land in
(§41, §53). **Go to package** would mean asking `dpkg`, `rpm` or `pacman` which package owns a file,
which is a subprocess per query against a database whose format is the distribution's to change.

## 25.4 Diagnostics

- [ ] Create memory dump — **baseline Windows parity requirement**; Task Manager has it
- [ ] 🟡 Analyse wait chain — **baseline Windows parity requirement.** The one chain a kernel states
      outright is followed: a process queued behind a file lock names the process holding it, on the
      properties window's General page, from `/proc/locks` where the kernel writes every waiter beside
      its holder. The rest cannot be followed from outside. Nothing publishes who holds a futex, and
      reconstructing it needs the debugger interface §4 rules out — so what is here is one real case
      rather than a general "wait chain" that is that case wearing a general name (§33, §91)
- [x] Inspect threads
- [x] Inspect stacks (§30) — Inspect ▸ Stacks…, in a window that resolves symbols and saves the
      frames it found
- [x] Inspect modules
- [x] Inspect handles / descriptors
- [x] **Inspect memory mappings** — every mapping of the address space, unfolded, with what each one
      is costing (§34)
- [x] Inspect environment
- [x] **Inspect token / security context** — the four uids, the four gids, the five capability sets,
      seccomp, no-new-privileges, the LSM label and the namespaces (§36)
- [x] Inspect network connections
- [x] Inspect windows — Inspect ▸ Windows…
- [x] Inspect services — Inspect ▸ Service unit…, which stays on the process and shows the unit
      it is in, where Go to ▸ Owning service leaves for the machine's list

**All of it is under one menu now, and for most of it that is the whole change.** Threads, modules,
descriptors, environment and connections were all implemented and reachable only by opening a
properties window and hunting along a tab strip for the right caption. The last two needed building:
the memory map had a parser and no display at all, and the security context had every field the
sampler already reads and no page to put them on, plus two more — the LSM label and the group list —
that had to be read on demand for the process being looked at. A menu that names the question is the
difference between a feature and a feature somebody can find (§5.2).

The three pages the menu needed are the ones §26 was missing, and each is described where it belongs:
the memory map in §34, the security context in §36, the cgroup's ceilings in §38. They are grouped
with "Go to" rather than with the items above it, because none of them changes anything (§5.5).

**The memory map is read when somebody asks for it and not on the tick.** Its counters come from
`smaps`, which makes the kernel walk the whole page table of the process — a browser has ten thousand
mappings to walk — so the page carries the time it was collected and a button, the same bargain every
other on-demand list here makes (§5.4). The cgroup page is refreshed every tick instead: a dozen small
files for one process is affordable, and half of what is on it moves while somebody watches.

**Both files look world-readable and are not.** `/proc/[pid]/maps` is mode 0444 and reading another
user's still fails with `EPERM`, because the permission the kernel checks is `ptrace_may_access` when
it generates the content rather than the mode bits when the file is opened. So the refusal is reported
as a refusal and says which permission is really in the way — a reader who looked at the mode and
concluded the program was at fault would be looking in the wrong place (§72.3).

Still unticked and why: **windows** needs the actions half of §39 as well as the list, and a page that
could show a window and do nothing to it is half a feature; **services** is a navigation to a view that
does not exist outside the CLI (§41). **Dumps** and **wait chains** are unchanged — both need an engine
this program has not got, and §30's reasoning for stacks applies to the first of them wholesale.

## 25.5 Memory — expert

- [x] **Set the out-of-memory priority** — `oom_score_adj`, which decides who the kernel kills when
      the machine runs out, shown beside the badness score that says who it would actually pick
- ∅ Trim working set — `process_madvise` needs CAP_SYS_NICE to reach another process, which
      nothing on a desktop holds, so the item could only ever refuse
- ∅ Read memory — needs PTRACE_MODE_ATTACH; `kernel.yama.ptrace_scope` is 1 on this machine, so
      even a process of the same user that is not a descendant is refused. §4 does not ship a
      debugger or a memory reverse-engineering suite, and this is the same line seen from here
- ∅ Save memory range — as above
- ∅ Search readable memory — as above
- [x] **Inspect mapped region** — Inspect ▸ Memory map ▸ a row, which opens everything a mapping is
      and a row one line high cannot hold: the whole path, the kernel's `VmFlags` line, the eight
      counters spelled out, and the other mappings of the same file. The half that would read its
      bytes is refused with the four above

- ∅ Direct modification of another process's memory is classified expert/debugging and **disabled
      by default** — there is nothing to gate, because nothing here writes to another process's
      address space at all

**Trimming another process's working set on Linux would be an item that could only ever refuse.**
`process_madvise` with `MADV_PAGEOUT` is the closest thing there is, and its own manual page says
what it needs: a `PTRACE_MODE_READ_FSCREDS` check *and* `CAP_SYS_NICE`, the second explicitly because
of the performance implications of letting one program page out another's memory. Nothing on a
desktop has `CAP_SYS_NICE`, so the menu item would refuse on every machine anybody is likely to run
this on — and §25.6 already refused unloading a module on exactly that ground, that an item which can
only ever refuse is a lie dressed as a feature.

**The four that read memory need an engine that does not exist here, and a permission that usually
does not either.** `process_vm_readv` and `/proc/[pid]/mem` are both governed by
`PTRACE_MODE_ATTACH`, and every current distribution ships Yama's `ptrace_scope` at 1, where a
process may read only its own descendants — so on an ordinary desktop reading the memory of something
this program did not start is refused for the same user, never mind another. Building a hex viewer, a
range exporter and a scanner on top of a call that is refused by default, for a feature that is
supposed to be off by default anyway, is work whose first honest screen would be a permission error.
The map of §34 is the piece of this that does not need the permission and it is built, down to the
box that spells one mapping out in full; the rest is §4's debugger, which this program does not
ship.

**And the opt-in that was to guard the writing half has nothing to guard.** It was written down as
the one feature here needing a deliberate per-session switch, on the assumption that the four above
would exist to be switched on. None of them does, and nothing else in this program writes a byte to
another process's address space — so the honest state of that line is that there is no such mode
rather than that there is one and it is off. A switch that gates nothing is a claim that something
dangerous is being held back, which would be the opposite of true.

**The out-of-memory adjustment is not a memory limit and the dialogs say so.** It reserves nothing
and caps nothing: it decides *which* process dies when something has to, so lowering one process's
score does not save memory — it points the killer at whatever is next on the list, which is
somebody else's process. The two numbers are shown separately for the same reason the throttle count
is shown beside the quota: the adjustment is what somebody asked for and `oom_score` is what the
kernel would actually do about it, and only the second answers "which process is at risk here".

**Raising it is free and lowering it needs `CAP_SYS_RESOURCE`**, which is the opposite way round
from most permissions and surprising enough that the refusal says so rather than reporting a bare
"not permitted": a process may always volunteer itself for the killer and may never excuse itself
again. It is **deliberately not routed through the privileged helper**, for the reason the real-time
I/O class is not — making one process harder to kill makes every other process on the machine
likelier to be chosen, and that is a decision to take at a root prompt rather than from a menu
(§68).

## 25.6 Modules

- [x] View module — Inspect ▸ Modules…
- [x] Reveal module file
- [ ] Verify signature — there is nothing to run: the verdict is a column, read for every row from
      the package database, rather than an action somebody starts (§70)
- [x] **Hash file** — SHA-256, computed on request and never as a side effect
- [ ] Open binary inspector — as above
- [ ] Search reputation — a network service that does not exist, and §3 promises no executable
      information leaves the machine unasked
- [x] Copy path
- [x] Inspect mapped memory — Inspect ▸ Memory map…
- [ ] Unload module — expert-only, with an explicit instability warning

**A hash is not a verdict.** It says what the bytes are and nothing about whether they are signed,
trusted or known; the four stay separate operations and this program never conflates them (§70).

It is asked for rather than computed, because hashing is the one operation here whose cost is the
size of the file: doing it for every module a process has loaded would read a gigabyte to fill a
column nobody looked at (§5.4).

- ∅ **Unloading a module on Linux** — there is no supported way to make another process drop a shared
  object, and an item that could only ever refuse is a lie dressed as a feature. The same reasoning
  §32 records for closing a foreign descriptor.

---

# 26. Process properties window

- [x] Double-click or Properties opens a **persistent** inspector — one per process, and several at
      once, which is what makes comparing two of them possible
- [x] Tabs whose capability is unavailable are hidden **or** disabled by user preference — the
      preference matters, because hidden and disabled answer different questions ("can this machine
      do it" versus "get out of my way"). `tabs.unavailable=disabled` leaves the tab in place saying
      which of the two reasons applies; `hidden` takes it off the strip. Disabled is the default,
      because a missing tab is indistinguishable from a feature nobody wrote

Tabs:

- [ ] 🟡 General (§27) — a page of its own now: identity, ownership, timing, how long it has been
      running, what the ELF header and the directory entry say about the image, and the command
      line, with Copy, Open folder and File properties under it. The three questions it does not
      answer — the signature, the package, the digest — are rows saying so and naming the button that
      answers them, rather than blanks that read like a clean bill of health. What is still missing
      is what a PE says about itself on Windows — version, company, description and signer, all four
      read there and none of them on this page
- [x] Performance (§28) — six graphs over the four time windows, hover readings and a keyboard
      cursor. There is no per-process network graph because there are no per-process byte counters
      to draw one from (§18)
- [x] CPU
- [x] Memory
- [x] I/O
- [x] Threads (§29)
- [x] Modules (§31)
- [x] Handles / resources (§32)
- [x] **Memory map (§34)** — every mapping, unfolded, read when the page is asked for
- [x] Network (§40)
- [x] GPU (§19) — and the tab that proves the preference above: a machine whose driver publishes no
      per-process accounting has nothing to put on it, which is not the same as this build not
      having one
- [x] **Security (§36)** — who the process is, what it may do, and what is confining it
- [x] Environment (§37)
- [x] **cgroup (§38)** — named for the thing rather than for the three things it is called on three
      platforms. A Windows job object and a container are not this and would not be described by it
- [ ] 🟡 Windows (§39) — the list is there and so are the actions: the page's own menu brings a
      window to the front, minimises, maximises, restores it and asks it to close, through X11
      itself rather than by shelling out to `wmctrl`. Three things keep it from a tick, and none of
      them is the one this line used to claim. Six attributes per window are printed as *not read*
      rather than read — the thread, minimised and maximised, whether it is responding, the
      workspace, the monitor and the owner. There is no *inspect properties* action. And no window
      on Windows or macOS is listed at all, which on a tab named for windows is the gap that matters
      most
- [x] **Services (§41)** — which unit this process belongs to and what that unit's file says: its
      description, whether it starts at boot, whether it is masked, its restart policy, its command
      and the file itself. The unit comes from the cgroup, because a systemd unit *is* a cgroup — the
      same join §40's owning-service column makes, through the same code, so the two cannot disagree.
      The row worth opening it for is whether this process is the unit's **main** process: that is the
      one systemd watches and restarts, and everything else in the cgroup is a child it will take down
      with it
- [ ] Runtime (§80) — blocked on §80, of which one sentence exists: which runtime is mapped into the
      process, read from its module list and shown as a column. A version, an assembly list, a
      managed stack and a heap summary are all unwritten, and a tab holding one row would be a tab
      promising the other four
- [ ] Strings (§35) — blocked on §35, of which nothing exists. The two scanners in Core are `/proc`
      tokenisers and not extractors of strings from a binary; nothing scans an image on disk, and
      scanning the process's memory is refused on §25.5's ground
- [ ] Timeline (§63) — blocked on §63: nothing records an event. The delta knows which processes
      have exited since the previous tick and forgets at the next one, and the usage history keeps a
      running total per application rather than a start with a time on it

The window hosts the same pane the main window docks at its foot — pinned to one process rather than
following the selection, which is what makes two of them comparable — and adds the pages the pane
has no room for: what the process *is*, an hour of what it has been *doing*, and the three resource
sheets that would each be twenty columns in the table.

Those pages go onto the pane's own tab strip, so the window has one row of tabs and not two. They
land after the pane's because the toolkit's page collection has `Add` and `Remove` and no `Insert`;
the window opens on General instead, and a test asserts every page is present so that an upstream
change fails a build rather than shipping a properties window with no properties on it.

When the process ends the window stays open, says so in its title and stops asking about the pid:
a window that followed the number would quietly start describing whoever the kernel gave it to next
(§72.2, §86).

**Most pages are refilled from a sample already taken; the three that cost a read of their own are
filled only while they are the page showing.** That is the discipline the pane's own tabs follow, and
the three are not the same kind of expensive. The memory map is a walk of the process's page tables,
so it is read once when the page is opened and re-read on a button. The security context is two small
files and the cgroup a dozen, so both are re-read on every tick while their page is up — a cgroup's
memory, throttle count and pressure figures all move while somebody is watching them, and a sheet
that froze at the moment it was opened would be answering a question about the past.

The `hidden` preference needs an answer earlier than that, so the cgroup is asked about once on the
first sample whether or not its page is showing: a dozen small files once per window is a fair price
for not moving every tab on the strip the moment somebody clicks the last one. The memory map cannot
buy its answer that cheaply — walking a browser's page table to decide whether to draw a tab nobody
asked for is exactly what §5.4 forbids — so its tab settles the first time it is opened instead. That
is the trade showing through the preference, and it is the honest way round.

**The Services page is the fourth that costs a read, and the only one read exactly once.** Its
reading is a walk of every unit file on the machine — 372 of them here — which is far too much for a
tick and does not need spending twice: a process cannot move between units while it runs, and a unit
that stops takes its processes with it, so this window would say *ended* rather than show a stale
service. What can change underneath it is somebody running `systemctl disable` in another window,
and that is a fair price for not walking a thousand files a second (§5.4).

Its tab may be hidden only when nothing on the machine publishes services at all. A process in no
unit is a finding about the *process* — most of a desktop is like that — so it keeps its tab and says
so, naming the cgroup it looked in. Collapsing the two would make "you are in no service" and "this
build cannot tell you" the same answer, which is the distinction §5.3 exists for.

The four still unticked, one line each. **Windows** had "the list and none of the actions" written
against it, and that was wrong when it was written: the actions are there and go through X11
directly. What it has instead is six attributes per window it prints *not read* beside, no
inspect-properties action, and no implementation at all on Windows or macOS — a tab named for
windows that lists none of them on the platform the word comes from (§39). **Runtime** needs the
managed and interpreted introspection of §80, of which one sentence exists — which runtime is
mapped in — and a tab showing that one row would be promising the other four. **Strings** is two
features under one heading: scanning the image on disk needs no permission and is honest work nobody
has done, while scanning the process's memory is one of §25.5's readers wearing a different hat and
is refused on the same ground, and shipping the first under a tab named for both would be promising
the second. **Timeline** needs the event history of §63, which nothing records: the delta knows what
exited since the last tick and forgets at the next.

---

# 27. General process properties

- [ ] 🟡 name ✔ · PID ✔ · PPID ✔ · parent process ✔ · start time ✔ · running duration ✔ · state ✔ ·
      session ✔ · user ✔ · effective user ✔ · architecture ✔ · executable path ✔ · command line ✔ ·
      current directory ✔ · file size ✔ · modification timestamp ✔ · creation timestamp ✔ · file
      permissions ✔ · runtime ✔ · container/cgroup association ✔ (and a page of its own — §38) ·
      service associations ✔ (and a page of its own — §41) · **package/bundle**, **signature
      status** and **file hashes** ✔ *as far as this page goes* — each is a row saying the question
      has not been asked and naming the button that asks it, which is what §5.4 leaves a page room
      to do — against **icon** (refused, below), **application identity**, **version**, **company**,
      **description** and **signer**
- [ ] 🟡 Buttons: Copy ✔ · Reveal executable ✔ · File properties ✔ · Verify ✔ — one click deeper
      rather than a fourth button on this strip, because verifying is the same read of the same file
      as the hash beside it and two buttons over one read would be two answers to look at (§5.2) —
      against **Inspect binary**

**Everything unticked here is unticked for one of three reasons, and none of them is this page.**

The first is what a file costs to read. The hash is the size of the image, the package is a walk of
every installed package's file list, and the signature is both — so none of the three is paid for on
opening, and each is a row saying it has not been asked and naming the button that asks it. Silence
would have been the wrong answer twice over: a properties window with no signature row reads as one
that checked and was happy (§70), and a missing package row reads as a program that belongs to no
package rather than one nobody looked up (§72.3). "**Not read — this build verifies no signatures**"
was the wrong answer too, and had been for some time: the button behind it verifies an Authenticode
signature on Windows, and on Linux reports what the packaging system recorded — the digest the
package kept and who validated the package — which is the whole of what an ELF admits of. The row
says *not checked* now, and names the way to the answer.

The second is the file format. Version, company and description are three of the five strings a PE
keeps in its version resource, the signer is bound to the key its signature was made with, and an
ELF has neither and never did (§5.3). All four are read on Windows and the signature is verified
there — that half of §21 is written — and this page does not carry them, which is the one thing
still owed here and is owed only on Windows. The Linux equivalents are a different question with a
different answer and are the package's, above.

The third is a view that does not exist. **Inspect binary** is a button onto §53, and there is no
§53. **Verify** is not in that class any more: the File properties dialog computes the digest and
checks the image in the same read, which is the button §27 was asking for, one click deeper because
two buttons over one read would be two answers to look at (§5.2).

**The runtime and the creation timestamp were being read and thrown away**, which is the cheapest
kind of gap there is: `DescribeImage` already makes the one `statx` for the birth time and already
walks the module list for what is executing inside the process, and neither reached the page. Both
are rows now. The runtime is worth its own line because it comes from the modules rather than from
the name — a .NET application and a shell script that launches one are called the same thing and are
not the same thing (§14, §80).

**Service associations were the one left that could be answered and was not, and now are.** The row
names the unit the process belongs to, or says outright that it belongs to none. It costs nothing:
the cgroup is already in the sample and a systemd unit *is* a cgroup, so it is the same join §40's
owning-service column makes, through the same code. What the unit itself says — its description, its
restart policy, whether this process is the one systemd watches — is a page of its own (§26, §41),
because those are facts about the service and several processes share one.

**Icon** is not a Linux fact about a process. It is a desktop-entry lookup by executable path against
`/usr/share/applications`, which answers for the third of processes that have a launcher and for
nothing else — a page that showed a picture for some rows and a blank for most would be describing
which programs ship a `.desktop` file rather than describing the process.

**Application identity** is the same lookup wearing a name, and is unticked here for the cost rather
than for the argument: `app.name` and `app.id` are columns of the table already, and filling either
means reading the machine's three hundred desktop files or its package database. That is the same
price as the package row above it and belongs behind the same button, which does not name it yet.

# 28. Performance process properties

- [x] 🟡 Historical graphs for CPU, private/commit, resident/working set, I/O, disk, GPU,
      handles/descriptors and thread count — six plots tiled two across, each on a scale that
      follows its readings up quickly and comes back down slowly, with the ceiling named in the
      corner. Private and working set share one plot deliberately: a resident set far below the
      commit charge is a process that has been paged out, and that is only visible side by side.
      **Network is not among them**, and is refused rather than deferred for the reasons §18 gives
      at length — a graph is a worse place than a column to put an unmeasurable quantity, because a
      flat line along the floor reads as a quiet process rather than as an unmeasured one (§72.3)
- [x] Time windows: 60 s · 5 min · 15 min · 1 h · retained-history limit — four buttons above the
      graphs, and the limit in words under them. An hour of axis over ten minutes of history draws
      as a graph that is mostly empty, and nothing on it would otherwise say whether that means
      "idle" or "this program has not been running that long"
- [x] Hover values — a rule down the sample under the pointer and its readings beside it, drawn on
      the plot rather than in a floating tip, which on a page of six stacked graphs would cover the
      neighbour a reader is comparing against. The same readings are echoed into the strip under the
      graphs, so the gesture is discoverable at all
- [x] Keyboard-accessible point inspection — Tab reaches every graph and ←/→ walk the cursor along
      the axis. The strip follows the arrow keys as well as the pointer, which it did not: it hung
      off the mouse alone, so the keyboard moved the cursor and drew it while the strip went on
      reporting wherever the pointer had last been — a reading beside the wrong moment, which is
      worse than no reading (§45.9)

Rings of its own rather than the table's. §8.2's are sized for a forty-pixel sparkline and are kept
only for the rows a front-end says are on screen; this window is pinned to one process, so an hour at
a one-second interval costs one process's worth of numbers and needs no decimation to hold it.

Every series is appended on every tick, including the ones with nothing to report. A ring that only
grew when a reading existed would put the samples out of step with each other and with the axis, and
the plot would draw a gap as though it had never happened. Past the history the window actually
holds, the cursor reports an absence and not a nought: the part of the axis this program has not been
running long enough to fill is not a quiet minute (§72.3).

---

# 29. Threads view

The engine enumerates threads on both platforms; the table shows a subset.

- [x] Thread ID
- [x] State
- [x] CPU %
- [x] CPU time
- [x] User CPU time
- [x] Kernel CPU time
- [ ] Cycles
- [ ] Cycles delta
- [x] Context switches
- [x] Context-switch rate
- [x] Start time
- [x] 🟡 Start address
- [x] 🟡 Resolved start module
- [x] 🟡 Resolved start symbol
- [x] 🟡 Current instruction / address
- [x] Priority
- [x] Base priority — on both platforms, though Windows only since the reader stopped throwing it
      away. `SYSTEM_THREAD_INFORMATION` carries a `BasePriority` field, the whole structure was being
      read into a buffer, and the record it built passed null for it under a comment saying "the bulk
      query carries neither an affinity nor a base priority". Half of that was true: there is no
      affinity in it. The base priority was arriving and being discarded one line before it was used,
      so the column read as unavailable on every Windows thread. Base is what the scheduler was told
      the thread is worth and `Priority` is where it sits now, after the boosts a waiting thread
      collects and loses — a view showing one and not the other cannot show that a thread has been
      boosted, which is the reason both are columns
- [x] Scheduling policy
- [ ] Ideal processor
- [x] Current / last CPU
- [x] Affinity
- [x] Wait reason
- [ ] Wait duration
- [x] 🟡 Kernel/user indicator
- [x] 🟡 Stack usage
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

The context-switch count is shown split as well as summed, because the halves mean opposite things: a
thread with millions of voluntary switches is waiting on something, while the same number of
involuntary ones is a thread losing a contended processor, and the total cannot tell those apart.
Linux writes the halves in the thread's `status`; Windows counts switches but does not split them, so
that column reads `n/a` there rather than zero (§72.3).

Base priority, scheduling policy and affinity are native readings, not translations (§5.3). On Linux
base priority is the nice value — the priority the thread was *given*, against the effective priority
in `Priority` that moves with the load — the policy is the `SCHED_*` class under the kernel's own
name, and the affinity is `Cpus_allowed_list` kept in the kernel's list notation, which is what
`taskset` both prints and accepts.

**CPU %** and the **context-switch rate** are differences between two readings, so the thread tab is
re-read on every tick while it is open rather than once per selection — a percentage of an interval
that ended when the process was selected gets less true the longer somebody looks at it. The first
reading of a thread shows `…` and not `0`. The percentage is expressed per processor, because a
thread cannot use more than one, and it is not clamped: `stat` accounts CPU time in ten-millisecond
ticks, so over a one-second window a saturated thread reads 100 % give or take a tick, and hiding
that would hide the disturbed samples with it (§3.2).

That repetition is the one expensive thing this view does, and it is bounded the way §5.4 asks: five
files per thread, for one process, only while somebody has that tab in front of them. Measured on a
loaded machine at 78 µs a thread — six milliseconds for a 77-thread compiler service, and a tenth of
a second would need a process with well over a thousand threads. The other tabs stay collected on
demand, because enumerating a process's descriptors costs 85 µs and its modules a walk of the page
table, and neither answer moves while it is being read.

Four of these columns are marked 🟡 because Linux answers them only for a thread the reader could
have attached a debugger to. **Current instruction** and **stack usage** come from
`/proc/[pid]/task/[tid]/syscall`, which is gated on `PTRACE_MODE_ATTACH` — owning the process is not
enough under the default `yama/ptrace_scope` — so on an ordinary desktop they read as refusals and
for the program's own threads they are real. They are emphatically not read from `stat`: its
`kstkeip` has been zero for every task that is not core-dumping since Linux 4.9, and parsing it would
put `0x0` in that column for the whole machine. Stack usage is the distance from the stack pointer to
the top of the mapping the stack pointer is in, which is why it needs `maps` as well as the register.

**Start address** is 🟡 for a different reason: Linux records no entry point for a thread. `clone` is
handed a stack and a register set, and the start routine is gone by the time the thread exists. The
one thread whose start is knowable is the first, which began at the executable's own ELF entry point
— biased by the load address for a position-independent image, exactly as §31 biases a module's. The
resolved module comes from `maps` and the resolved symbol from the image's `.symtab` or `.dynsym`,
which most binaries on a distribution no longer carry: an unresolved symbol beside a resolved module
is the normal outcome, and is why the module column sits beside the symbol column rather than behind
it. Every other thread reports "this platform has no such thing" rather than the zero that used to
sit there and render as `0x0` (§72.3).

The **kernel/user indicator** comes from the same `syscall` file, which also says which call a thread
is in — the two are shown in one cell, because "kernel" and "kernel, in call 202" are the same answer
at two levels of detail. Where the file is refused, a wait channel is still an answer: a thread parked
in a named kernel function is in the kernel whatever the permissions say. A thread with neither is
left blank rather than assumed to be in user code — a runnable thread may be on either side, and a
coin toss rendered as a reading is worse than an empty cell (§5.3).

Linux addition — what the kernel reports and Windows has no equivalent for:

- [x] **Queued** — cumulative time on a run queue wanting a processor, from `schedstat`. The only
      scheduling delay readable for a thread whose registers are refused. A kernel booted with
      `schedstats=disable` keeps the file and writes a literal `0 0 0` into it; all three zeroes
      together are read as the switch being off rather than as a thread that has never been given a
      processor, which is not something that can be true of a thread there is a file to read (§72.3)

Still unticked and why — each re-checked against a live `/proc/[pid]/task/[tid]/` rather than taken
on trust, since a kernel that grew a file would turn a refusal into a gap without anything saying so:

- **Cycles** and **cycles delta** — no file under `/proc` carries a per-thread cycle count. The only
  route is `perf_event_open`, which needs a descriptor held open per thread for the life of the view
  and the same `PTRACE_MODE_ATTACH` `syscall` needs, and returns nothing at all on a machine with no
  hardware PMU. Two hundred lines of native plumbing to render "not permitted" on every row of an
  ordinary desktop is not a column, and the process-wide `Cycles` counter has said `n/a` on Linux for
  the same reason since §8.
- **Ideal processor** and **TEB / TLS information** — no Linux equivalent. The scheduler has no
  notion of a thread's preferred processor that it will name — the closest thing in
  `/proc/[pid]/task/[tid]/sched` is `numa_preferred_nid`, which is a memory node rather than a
  processor and reads `-1` on a machine with one node — and the thread pointer is readable only
  through `ptrace(ARCH_GET_FS)`, which means stopping the thread to ask.
- **Wait duration** — deliberately left. `schedstat` reports cumulative time queued for a processor,
  which is not how long the current wait has lasted, and labelling it as such would be exactly the
  false equivalence §5.3 forbids. It is reported above under the name of what it actually is.
- **Description** — Linux gives a thread one name, `comm`, and it is already the Name column. A
  Description column would repeat it.
- **Service association** — a thread may be moved into its own cgroup under v2's threaded mode, so
  `/proc/[pid]/task/[tid]/cgroup` is a real per-thread reading. Checked again rather than assumed:
  the file is there for every task, and for an ordinary process it is byte for byte the process's
  own. Reading it would be a *sixth* file per thread in the one view this section re-reads on every
  tick — the five it already reads are the 78 µs a thread measured above — spent on a column that
  says the same thing on every row of almost every process. That is the §5.4 trade this view is
  built around, so it stays unread and the reason stays written down.
- **AppDomain / runtime context** — needs the runtime's own introspection, not the kernel's (§80).

Actions:

- ∅ **Suspend thread** / **Resume thread** — not possible on Linux, and not for want of trying.
  `SIGSTOP` and `SIGCONT` act on a thread group however they are directed, so `tgkill` to one thread
  stops all of them; the only per-thread stop is `PTRACE_ATTACH`, which makes this program the
  thread's debugger and kills it if we exit. An item that could only ever do the wrong thing is worse
  than one that is not there (§25.6 records the same reasoning for unloading a module).
- ∅ **Terminate thread** — the same fact from the other side: a fatal signal is group-wide, so there
  is no way to end one thread of a process and leave the others running. Offering it would end the
  process under a reader who asked for a thread.
- [x] Set priority
- [x] Set affinity
- [x] View stack
- [x] Copy — the selected row, or the whole list, with the visible columns and their headers
- [x] Go to start module — only the first thread of a process has one, and it says so for the rest
      rather than selecting nothing and leaving the reader wondering whether the click registered
- [x] Resolve symbols
- [x] Save stack

The list scrolls up and down and not sideways, so a column past the right-hand edge is unreachable
rather than merely off-screen. That is why the columns are ordered by what a reader came for — the
wait reason seventh rather than last, where it used to be cut off — and why pairs that are always
read together share a cell: user and kernel time, effective and base priority. The twenty-one of them
come to about 1800 pixels, and the properties window opens wide enough to show most of them.

# 30. Stack viewer

The window exists, opened from a thread row, one per thread and several at once.

Linux keeps two stacks per thread and hands over neither freely. The kernel stack behind
`/proc/[pid]/task/[tid]/stack` needs `CAP_SYS_ADMIN` — not the owning user, not the same uid,
`CAP_SYS_ADMIN` — and the thread's own stack is not exposed at all: walking it means reading another
process's memory and unwinding it with its debug information, which is the driver §4.1 rules out
wearing different clothes. So what this window shows is usually a short list and a paragraph about
what is missing, and **the paragraph is the honest part**. A viewer that showed the same short list
without it would read as the whole stack.

- ∅ **Native stacks** — meaning the thread's own. Nothing here unwinds one, and nothing here will
  without the unwinder §4.1 rules out. What is shown instead is the single user-space frame Linux
  does give up — the instruction the thread will resume at, from `syscall` — marked as one frame and
  not as a walk.
- [x] 🟡 Symbols when available — kernel frames arrive already named by `kallsyms`; a user frame is
      looked up in the image's `.symtab` or `.dynsym`. Most binaries on a distribution carry neither
      any more, so an unresolved name is the ordinary outcome and not a failure
- [x] Module + offset fallback — which is why the module column sits beside the symbol column rather
      than behind it, and costs no file access at all
- ∅ **Source filename and line** — needs DWARF, which this build does not read. The columns are kept
  and say `n/i`: a column that is absent is a requirement quietly dropped, and a column that says it
  has nothing is a fact about this build
- [x] Kernel frames where permissions permit — **see §4.1: without a driver, ours stop at the
      user/kernel boundary and say so**. `kernel.kptr_restrict` is set nearly everywhere, and the
      kernel then writes `[<0>]` for every frame; zero is an address, so the address column carries
      the reason rather than a stack that appears to live entirely at the null page (§3.4)
- ∅ **Managed runtime frames** and **mixed native/managed stacks** — both need the runtime's own
  stack walker, which is §80's, and neither is faked from a native frame that happens to be in a
  runtime library
- [x] 🟡 Columns: frame · address · symbol · module · source · source line · displacement · frame
      type. Source and source line are there and empty, as above. A displacement is unknown rather
      than zero when there is no symbol to be inside of — zero says the address is a function's first
      instruction, which is a claim
- [x] Actions: refresh · copy frame · copy stack · resolve symbols · open module · save stack
- ∅ **Symbol loading is asynchronous** — there is no symbol server to round-trip to: the tables are
  read from local files in bounded ranges, a few dozen kilobytes for a stack of forty frames. Moving
  that off the UI thread would mean calling the probe from a second thread, and the probe reuses one
  read buffer across every file it opens — trading a millisecond of latency for a data race is not an
  improvement. It goes back on the list the day a symbol server does.

# 31. Modules / loaded images view

Enumeration works on both platforms (Toolhelp32; `/proc/pid/maps`).

One row per *load*, not per mapping: a library's four or five consecutive lines are folded, and
`Mappings` says how many. A file mapped twice at unrelated addresses — which is what .NET does to
every assembly — stays two rows, because one row spanning the gap would report an image occupying
most of the address space.

- [x] Name
- [x] Path
- [x] Base address
- [x] Size — the total of the folded mappings, not the span from the first to the last
- [x] End address
- [x] 🟡 Entry point — from the ELF header, biased by the load address for a position-independent
      image so it is an address in *this* process. Windows does not read it yet
- [x] 🟡 Architecture — the ELF `e_machine`; a machine with no name is reported as its number
- [x] 🟡 Module type — executable · shared object · relocatable · core dump, and `data` for a mapped
      file that is not an image at all
- [x] 🟡 Load count — how many separate loads of the file are in this process, which is the number
      the address space actually shows. Not the loader's reference count: `link_map`'s
      `l_direct_opencount` is the count of `dlopen` calls not yet undone and no file under `/proc`
      publishes it. One is the answer for nearly every row and that is what makes a two worth the
      column — two copies of a library is two sets of its global state, and a live `dotnet` has
      forty-eight of its ninety-four mapped files loaded more than once. Windows has both counts in
      the Toolhelp entry and does not read them yet
- ∅ **Load time** — and on Windows too, which is why this is the one here with no box at all.
      Nothing records when a mapping was made. The loader knows and
      publishes nothing; `/proc/[pid]/maps` has no timestamp column; and the one date near a mapping
      is the file's own modification time, which is a different fact and already a column. Windows'
      Toolhelp has no such field either, so this is unanswered on both, for two different reasons
- [x] 🟡 Load reason — derived rather than read, because Linux publishes none: the program names its
      interpreter in `PT_INTERP` and its libraries in `DT_NEEDED`, and every library does the same.
      Sound in one direction only, and the value says which — an image nothing names reports "nothing
      that could be read names this" and never "somebody called `dlopen`", because `LD_PRELOAD` and
      an unreadable dependency produce the same silence. Windows keeps the reason per module and it
      is not read yet
- [x] 🟡 File size — Linux only; Windows reports `n/i`
- [x] 🟡 File modification time — as above

The four a Windows file keeps in a version resource, which **ELF has no counterpart for**. There is
no such section in the format and never has been, so what a Linux machine publishes about a file is
what the database that installed it publishes about its package — read from `pacman`'s `desc` and
`dpkg`'s `status`, named as the package's in every cell so that nobody reads a package version as a
file version (§5.3), and asked for when the properties box is opened rather than while the list is
filled, because the lookup indexes every path every installed package owns (§5.4):

- [x] 🟡 Version — the package's, as the packaging system spells it. Linux only
- [x] 🟡 Description — `%DESC%`, or the synopsis line of `dpkg`'s `Description:`. Linux only
- [x] 🟡 Company — `%PACKAGER%`, or `dpkg`'s `Maintainer:`: who assembled the package. Linux only
- [x] 🟡 Product — the package name. Linux only
- [x] 🟡 Signature status — the same verdict §70 gives a process's own image, asked of a module.
      Nothing signs an ELF, so this is the packaging system's answer: the file against the digest its
      package recorded, and whether that package's own signature was checked when it was installed.
      The two are separate readings and only both together are worth a verdict — a file that matches
      a package nobody signed is exactly as unsigned as one from nowhere. Behind the same button as
      the hash, because it is the same read of the file. Windows has Authenticode and does not read
      it yet
- [ ] **Signer** — ∅ on Linux, unwritten on Windows. Nothing on Linux names one. `pacman` records *that* a signature was verified
      at install time, not whose; `dpkg` records no signature over an installed file at all; and the
      signature itself went with the downloaded archive. Naming the packager here would answer a
      question about identity with a name from the changelog. Windows' certificate subject is a real
      signer and **is now read — for a process's own image, not for each module it has mapped.**
      `ImageSigner` is a field of the process record and there is no equivalent on the module record,
      so the machinery exists and this row is the work of applying it per row rather than a question
      nobody has answered
- [x] 🟡 SHA-256 — a button in the file properties box and never a side effect, because it is the one
      reading whose cost is the size of the file: hashing every module a process has mapped would
      read a gigabyte to fill a column nobody looked at. **A hash is not a verdict** (§70)
- [x] 🟡 ASLR — `ET_DYN`, which is what lets the kernel place the image where it likes; an `ET_EXEC`
      image names its own addresses and goes where it says. What the *file* asks for, which is not
      what the kernel granted — randomisation additionally needs the process not to have turned it
      off — and only the first is readable from a mapped file. Windows' `DllCharacteristics` is not
      read yet
- [x] 🟡 CFG — Linux's control-flow protection, from `NT_GNU_PROPERTY_TYPE_0`: `IBT` and `SHSTK` on
      x86, `BTI` and `PAC` on AArch64. The two properties have different type numbers and the same
      bit values, so the type is checked rather than the bits alone — reading the bits off an AArch64
      note would report CET on a machine that has never had it. Windows' own CFG bit is not read yet
- [x] Executable flag — the `x` of the folded mappings' permission union
- [x] Writable flag — the `w` of it
- [x] Mapped / shared — the `s`/`p` of it
- [x] Backing file
- [x] 🟡 Runtime classification — which engine reads the file, from its own header and never from its
      name: an ELF is native, a PE with a CLI header is a managed assembly, one without is a Windows
      binary under Wine, and a ZIP container is a class path. The rows this exists for are the ones
      the view used to call `data`, which is also the word for a font: a .NET process maps every
      assembly it loads and not one of them is an ELF. "Not code" is a finding and the dash beside it
      is a hole, and they render differently (§72.3). What *runs* in a process — the version of the
      runtime, its managed threads — is §80's and is not this

Linux additions — what `maps` and `smaps` report and Windows has no equivalent for:

- [x] Resident size per module, summed over its mappings — from `smaps`, and the reason it is not
      there when only `maps` could be read
- [x] File offset · device · inode
- [x] Deleted — the image is still mapped and still running, and the file on disk is gone
- [x] Mapping count — how many `maps` lines the row folded
- [x] `SONAME` — the name other binaries link against, which is not always the file's
- [x] Program interpreter — the dynamic loader an executable asks for

- [x] Windows enumerates DLLs and mapped images; Unix maps the concept to shared objects and
      executable mappings

# 32. Handles / descriptors / resources view

Enumeration works on both platforms, including the name resolution that deadlocks on synchronous
named pipes (§6.1).

- [x] Resource type
- [x] Native type
- [x] Handle / FD identifier
- [x] Name / path
- [x] 🟡 Access rights — on Unix this is the access mode in the open flags, `r`, `w` or `rw`, and not
      a mask of independent bits. Windows has the mask in the table entry and does not decode it yet
- [x] 🟡 Flags — the `O_*` word, spelled out; a bit the list does not name is shown as a hex
      remainder rather than dropped. Windows reports `n/a`: its equivalent is the access mask
- [ ] **Object address** — ∅ on Linux, unwritten on Windows. The five `/proc/net` tables do print a pointer beside every
      socket, and it is not an address: the kernel writes it with `%pK`, which since 4.15 hands an
      unprivileged reader a *hash* of the pointer and hands one a `kptr_restrict` of 1 sixteen zeros.
      Measured on the machine this was written on with `kptr_restrict` at nought — the value has eight
      leading zeros and no x86-64 kernel address does. Putting it in a column labelled "address" would
      be a number that is stable, plausible and not the address of anything. Nothing outside a socket
      publishes even that: a file, a pipe and an event descriptor have a `struct file` whose address
      no file under `/proc` prints. Windows hands the object pointer over in the handle table and does
      not read it yet
- [x] 🟡 Reference count — `sk_refcnt`, out of the network table's own column, joined to the
      descriptor by inode. Decimal in the four internet tables and hex in `/proc/net/unix`, which is a
      fact about the kernel's format strings and not about sockets. Sockets only, and everything else
      says there is no such number rather than showing one: note that a socket held by one descriptor
      commonly reads two or three, because this counts holders of the socket and the protocol's own
      hash tables are among them. Windows' object reference count is not read yet
- [x] 🟡 File offset — Linux, from `fdinfo`. A socket and an event descriptor have none and say so
- [x] 🟡 File type — the kernel's own `st_mode`, from one `statx` on the descriptor rather than on the
      path it names, so that it still answers for an unlinked file or one in another mount namespace.
      A second axis and not a finer resource type, and the answer wherever the name above was only a
      guess: a FIFO or a Unix socket bound under `/run` is a path like any other and all of them read
      as ordinary files before this. **An anonymous inode has no file type** — an eventfd's `st_mode`
      is `0600` with the type bits *clear* — and it says "no type", because a table that maps that
      nought onto one of the seven POSIX types files every event descriptor on the machine under
      something it is not (§72.3). It replaces a `Directory.Exists` on the same path, so it costs the
      list nothing it was not already paying. Windows' `GetFileType` is not called yet
- [x] 🟡 Device — two of them, and they are different questions. The device the descriptor's inode is
      *on* comes from `fdinfo`'s `mnt_id` joined to `mountinfo`, with the mount point and the file
      system beside it; the device a node *is* comes from `statx`'s `stx_rdev`, and `/dev/null` is
      character device 1:3 living on the `devtmpfs` at 0:7. Reporting either as the other would give
      every device node on the machine the same number. A socket, a pipe and an anonymous inode are
      on file systems the kernel mounts nowhere and say so. Windows has no per-handle mount to join
      against — a handle's volume is in the object name — so this is `n/a` there
- [x] 🟡 Inode — Linux, from `fdinfo`'s `ino:` with the bracketed number in `socket:[n]`/`pipe:[n]`
      as the fallback for a kernel too old to write it
- [x] Socket endpoint — the descriptor's inode joins it to a row of the five `/proc/net` tables, and
      the handles view shows the endpoint and state beside the descriptor
- [ ] **Creation / open time** — ∅ on Linux, unwritten on Windows. The kernel records no time at which a descriptor was
      opened. The timestamps on `/proc/[pid]/fd/[n]` belong to the `procfs` directory entry, and a
      test proves it by reusing a descriptor number: a file is opened and its link looked at, the
      descriptor is closed, a different file is opened over a second later on the same number, and
      the timestamp has not moved by a nanosecond. Reporting it would say the second file had been
      open since before it was. Windows keeps a creation time on the object and it is not read yet
- [x] 🟡 Target process — a pidfd names the process it holds, from `fdinfo`'s `Pid:`. Nothing else
      refers to a process, and says so rather than reporting pid 0

Categories — Windows: files, directories, registry keys, processes, threads, events, mutexes,
sections, jobs, tokens, desktops, window stations, pipes, ports, transactions, devices. Linux/macOS:
files, directories, sockets, pipes, event descriptors, devices, shared memory, process descriptors,
kernel/event interfaces.

- [x] 🟡 Resource categories as above — Linux reads and names all of them: file, directory, socket,
      pipe, eventfd, epoll, timerfd, signalfd, inotify/fanotify, shared memory (`memfd` and
      `/dev/shm`), device, pidfd, and "kernel object" for an anonymous inode nobody has named yet.
      The handles view tallies them per process (§20). Windows' own list is still the nine types
      `NtQueryObject` returns

Actions:

- [x] Copy — the row with its headers, or every row of the list, because the interesting cells are
      the wide ones a table shows eight characters of
- [x] Reveal / open path — for a descriptor that has a path. A socket, a pipe and an anonymous inode
      are named by the kernel and not by the file system, and this says which of the two it is rather
      than opening a file manager on a name that is not a path
- [x] Resource properties — everything one descriptor is, in a box: the per-kind detail `fdinfo`
      carries that a row one line high cannot show, and a button that finds every other descriptor on
      the machine pointing at the same inode. That scan is how the far end of a pipe is found, and it
      travels with the count of processes that answered — "nothing else holds this" and "nothing we
      were allowed to ask holds this" are different statements (§72.3)
- [x] 🟡 Go to owning process — in the only form that means anything here. Every row belongs to the
      process the pane is showing, so the navigation that is worth having is to the process a
      descriptor *names*: the target of a pidfd. Nothing else names one, and this says so rather than
      opening the window the reader is already in
- [ ] **Close resource** — ∅ on Linux, unwritten on Windows. No supported mechanism exists for closing a descriptor in
      another process, and an item that could only ever refuse is a lie dressed as a feature. Windows
      has `DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE` and it is not wired up, which is why this
      is a refusal on one platform and unwritten work on the other

# 33. Find handles / files / modules / resources

The question this answers — "which process is using this file?" — is one of the two or three reasons
people install Process Explorer at all.

- [x] Search targets: resource/handle names · file descriptors · executable paths · loaded modules ·
      memory mappings · sockets · service names · process names · command lines
- [ ] 🟡 Modes — substring ✔ (case-insensitive) and regex ✔ as `/pattern/`; wildcard, exact and a
      case-sensitive toggle are not
- [ ] 🟡 Results — process ✔ · PID ✔ · resource type ✔ · name/path ✔ · user ✔; the access mask is
      not reported
- [x] 🟡 Double-click navigates to the process — from **View ▸ Find handles or files…**, which is the
      window's half of what `--find` always did from a terminal. Navigating to the *resource* still
      has nowhere to go: there is no handle view to land in
- [x] The tree expands every ancestor of the process it selects, because a process nested under a
      collapsed parent cannot be brought into view and looking at it was the entire point
- [x] **Who is holding the lock somebody else is waiting for** — `/proc/locks` lists every waiter
      beside the holder it is queued behind, both by pid, and the properties window's General page
      says which. This is the answer to "why is this hanging" for the case a kernel answers it for.
      Read when the row refreshes rather than remembered, because the whole reason to look is that a
      process is stuck *now*. A waiter whose holder has gone between the kernel writing the two lines
      is left out rather than reported as blocked by pid nought, and nobody is ever reported as
      waiting for themselves. The wording is "nothing is holding a file lock this process wants"
      rather than "not blocked": a process waiting on a futex, a pipe or a socket is blocked and is
      not in this table at all (§5.3, §72.3)

Reported one reason per process for the three that are really one thing. A pattern matching a name
usually matches the command line and the path too, so the most specific one that answered wins:
three rows saying the same thing is noise, not information.

The expensive half — every descriptor, every mapping and every socket of every process — runs only
for the processes the cheap fields did not already answer for (§5.4), which a test asserts by
counting the reads.

---

# 34. Memory map

- [x] **Columns**: start ✔ · end ✔ · size ✔ · protection ✔ · private/shared ✔ · resident ✔ ·
      proportional ✔ · dirty ✔ · swapped ✔ · locked ✔ · anonymous ✔ · huge-page state ✔ ·
      backing file ✔ · region classification ✔ · file offset ✔ · device ✔ · inode ✔ · the kernel's
      `VmFlags` verbatim ✔ — and *state*, *allocation protection*, *committed*, *copy-on-write*,
      *NUMA node* and *stack owner* are the six Linux does not answer, below
- [ ] 🟡 Actions: copy address ✔ · copy range ✔ · copy path ✔ · go to the mapped file's
      properties ✔ — inspect bytes, save region and search strings are §25.5's, and refused there

**One row per mapping, and deliberately not folded.** §31 turns the same file into one row per loaded
image, because it is answering "which code is in this process" and a library's five consecutive
mappings are one library. This is answering "what is at this address and what may be done to it", and
the fold would destroy the answer: an image's read-only segment and the writable one above it become
a single row claiming to be both readable and writable, which is the one fact somebody opens this
view to check. The recording of a `cat` is twenty-six rows here and five in §31, and a test asserts
both numbers over the same file so that neither view can quietly become the other.

**The kernel's order is kept.** A memory map has one property a list of modules does not — the row
above is the memory below — and sorting it by anything else here would take that away from every
caller at once.

**Classification is by prefix and never by a table of known names.** `[vvar_vclock]` arrived in 6.13
and this build has never been taught it; a name-by-name table would have classified it as a file that
does not exist, and it lands as a kernel-provided region under its own name instead. Named memory
that is not a file on any disk — `/memfd:`, `/dev/shm`, System V segments — is its own kind for the
same reason: a reader adding those to "what this process has open" would be counting shared memory as
file cache.

**Only the initial thread's stack is labelled a stack, and the page does not pretend otherwise.**
Linux stopped labelling other threads' stacks in 4.5 — working out which anonymous region a given
thread's stack pointer was in cost a walk of every thread per line of the file — so a threaded
process shows one `[stack]` and its other stacks appear as ordinary anonymous regions. That is what
the kernel says; saying more would be inventing it.

**The two-letter `VmFlags` codes are kept as the kernel writes them**, and that is where the answers
this section asks for that have no counter of their own actually live: `gd` for a region that grows
down, `ht` for hugetlb pages, `nr` for memory with no swap reserved, `dd` for a region left out of a
core dump. Inventing English for a set the kernel extends every few releases would go stale silently
(§5.3).

Still unticked and why: **state** and **committed** are Windows notions — Linux has no
reserve-versus-commit distinction per region, and describing a mapping as "committed" because it is
present in `maps` would be inventing a state the kernel does not have. **Allocation protection** is
the protection a region was *created* with, which Linux does not record. **Copy-on-write** has no
per-region flag: every private file mapping is copy-on-write by definition, and how much of one has
actually been copied is the `Anonymous` counter, which is a column. **NUMA node** is in
`numa_maps`, a third file and a second walk of the same page tables, and it does not name a node
anyway — it reports how many of a mapping's pages are on each of them, so a mapping spread across two
has no single answer to put in a column. **Stack owner** is the 4.5 change above.
**Heap association** is `[heap]`, which is a classification and is one.

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

- [x] UID / eUID / sUID / fsUID and GID equivalents — all eight, plus the effective account by name
      as well as by number, and a field that says outright when the two disagree
- [x] **Supplementary groups** — on the page, resolved to names where this machine's own file has
      them and left as numbers where it does not. The *column* of the same name still shows the raw
      line and is still opt-in, deliberately: resolving a name per process per sample is a lookup the
      sample loop has no budget for, and the page resolves two numbers for one process (§5.4)
- [x] Capabilities — all five sets, by name (§21)
- [x] SELinux context
- [x] AppArmor profile
- [x] seccomp state — the mode and the filter count
- [x] **Namespaces** — by kind and inode, from the same read §14 already makes
- [x] no-new-privileges

**There is a page now, and it is where the eight identities earn their place.** Every field above was
readable and several were columns nobody turns on; a sheet that puts the real uid beside the effective
one answers "is this running as somebody it was not started by" at a glance, which no single column
does. The page costs nothing to draw — uids, gids, capabilities, seccomp and no-new-privileges are
all lines of the `status` the sampler already reads for every process every second.

**The two that are not in the sample are read for the one process being looked at.** The LSM label is
a file per process and the group list is a string per process, and both were measured at the same
order of cost as the descriptor scan that had to leave the sample loop — so a column that names them
switches them on for the whole machine, and opening a properties window switches them on for one
(§5.4). They fail independently and carry a reason each: `status` is readable for anybody's process
and `attr/current` is not necessarily, so a machine can perfectly well answer the group list and
refuse the label.

**A kernel with no security module fails the label read outright rather than leaving the file empty.**
It comes back `EINVAL`, not an empty string — so "nothing is confining this process" and "we were not
allowed to look" arrive by different routes and are reported as different sentences. Neither is a
blank, which would read as a clean bill of health (§70). AppArmor's `unconfined` is kept verbatim
because it is an answer.

**Group names come from `/etc/group` and not from `getgrgid`**, which is the trade the user-name
resolver already makes and has the same stated cost: a group that comes from LDAP, SSSD or a
container's own file is not in this machine's file and stays a number. The gain is that the resolver
is a pure function of a file path and replays against a fixture, and that a lookup can never block on
a network directory. The number is shown either way, so a resolved name is extra information and
never the only information.

macOS:

- [ ] UID/GID · code-sign identity · entitlements · hardened runtime · sandbox

- ∅ Modifying tokens or capabilities — explicitly not a baseline requirement, and not planned

# 37. Environment view

- [x] Variables read on both platforms — PEB walk (W), `/proc/pid/environ` (L)
- [x] Displayed as name / value
- [ ] Search
- [ ] Copy name / copy value / copy row
- [x] Export — `--environment PID`, and `--format json` for a machine. The output is byte for byte
      what `/proc/[pid]/environ` holds, checked against it. Deliberately *not* quoted for a shell:
      dressing another process's block up as something to paste would invite somebody to paste it
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
- [x] Controllers — which are *enabled* here, which is not the same as which limit files exist: a
      delegated cgroup may have `memory` and not `cpu`, and then the CPU limit is inherited from an
      ancestor rather than absent
- [x] CPU limits — as a number of cores, plus how many times the cgroup has actually been throttled
- [x] Memory limit — the hard cap and the soft one, which are different limits
- [x] Current memory usage
- [ ] I/O limits
- [x] 🟡 **Task membership** — the count against the limit, named for what it counts; the list of
      members is not read
- [x] Pressure metrics (PSI) — per cgroup, through the same parser the machine-wide ones use
- [x] **Frozen or not, and freeze / thaw** — the state from `cgroup.events` rather than from
      `cgroup.freeze`, because the first is what the cgroup *is* and the second is only what it was
      *asked* to be; they differ while a freeze is still catching processes that were inside a
      syscall when it began (§25.1)

This is the answer to "why is this slow when the machine is idle". A container or a systemd unit can
be throttled to a fraction of a core or capped well below the machine's memory, and nothing in a
process table shows it — the process simply appears to be doing less than it should. `--limits PID`
prints it, together with the process's own ceilings and its out-of-memory standing (§25.2, §25.5).

There is a page as well as a flag now, and it is the answer to that question in the window rather than
only at a prompt. Refreshed every tick, unlike the memory map: a dozen small files for one process is
affordable, and the memory in use, the throttle count and the three pressure figures all move while
somebody is watching them (§5.4).

**The two kinds of ceiling are printed under separate headings and never added together**, because
different parts of the kernel enforce them against different things: `RLIMIT_NPROC` is a limit on the
*user*, `pids.max` is a limit on the *cgroup*, and a single combined number would be the false
equivalence §5.3 forbids.

**`pids.current` counts tasks and not processes, and both the page and `--limits` now say so.** They
said "processes", which is wrong by whatever factor the group's programs happen to be threaded: the
cgroup this program's own window was open in reported 892 against 58 entries in `cgroup.procs`, so
the row was out by a factor of fifteen with nothing to indicate it. It is the figure systemd calls
`TasksMax` for the same reason, and it is what the freeze confirmation counts as well — a destructive
action naming a consequence an order of magnitude out is the one thing §5.5 exists to prevent.

**A controller that is switched off is not a limit that is absent**, and the page distinguishes them
where the reader cannot. `memory.max` missing and `memory.max` containing the literal word `max` both
arrive as "no value", so where the controller is not enabled here, "no limit" would be an outright
false statement — an ancestor's quota still applies, and that is precisely the case somebody opens
this page to find. The first real machine it was run against had `memory` and `pids` enabled and not
`cpu`, so the distinction was doing work immediately rather than guarding a hypothetical.

**Unlimited is not a quantity.** `max` is reported as *no limit* rather than as the very large number
the file literally contains on some kernels, because "no limit" and "a limit of nine million
terabytes" must not look alike. Likewise a quota is divided into a number of cores: "half a core" is
a sentence and "50000 100000" is not.

**A nought throttle count and a missing one are different answers.** A cgroup with a quota it never
reaches has genuinely never been throttled; one whose CPU controller is not enabled has no such file
at all (§5.3).

Unified hierarchy only. cgroup v1 puts each controller in its own hierarchy with its own path, so a
process has several cgroups at once and no single one answers this — a v1 machine says so rather than
giving half an answer.

Containers:

- [ ] 🟡 Runtime · container ID ✔ (§14) · locally resolvable name · namespaces ✔ (on the security
      page, where the boundary they draw belongs — §36) · resource limits ✔

# 39. Windows / UI objects

- [ ] 🟡 Window title ✔ · native window ID ✔ · process ✔ · class ✔ · visible ✔ · bounds ✔ ·
      thread, minimised/maximised, responding, workspace, monitor and parent/owner are not read
- [ ] Actions: bring to foreground · minimize · maximize · restore · close · inspect properties
- [x] **Find window** — point at a window and the process behind it is selected in the list

Windows has `EnumWindows`. X11 has `_NET_CLIENT_LIST`, and where a session has no window manager to
maintain it — a bare X server, a minimal WM — the root's children are walked with `XQueryTree`
instead, which is what `xwininfo` does and answers in both cases. **Wayland has nothing**: by design,
a Wayland client cannot enumerate other clients' surfaces.

- [x] A Wayland session says so rather than appearing mysteriously empty for half of all Linux users.
      The distinction is carried in the data as `WindowSourceState`, not left for a caller to infer
      from an empty list: "no windows" and "this session will not tell you about windows" are
      different answers, and on Wayland the second is the true one (§5.3)
- [x] XWayland's windows still appear there, so the sentence says that too — an unexplained short
      list reads as a broken program

**The picker does not grab the pointer.** Process Explorer drags a crosshair, which needs a grab: the
pointer belongs to the picking program until the button comes up, and a grab left dangling by a crash
takes the desktop with it. A countdown reads the pointer where it already is, gets the same answer,
holds nothing hostage — and works when the target is modal, dragging or in a menu, none of which
survive a grab.

# 40. Network view

Endpoints are enumerated on both platforms and attributed to processes.

- [x] Process
- [x] PID
- [x] 🟡 User — Linux reads the uid the kernel charges the socket to, which is the socket's own owner
      rather than the owning process's; they differ for a descriptor passed between processes. A
      socket in `TIME_WAIT` has no structure left to charge, so it reads unknown and not `root`. The
      Windows owner table carries no uid at all, which is why the tick is amber
- [x] Protocol
- [x] Address family
- [x] State
- [x] Local address
- [x] Local port
- [x] Local hostname — `--connections --resolve` on the command line, and the network tab's own
      **Resolve hostnames** in the window. Off in both until asked for, and asynchronous in the
      window: an address with no name yet shows the address and the name appears in a later fill
- [x] Remote address
- [x] Remote port
- [x] Remote hostname — as above
- [x] 🟡 Service name — from the machine's own `/etc/services`, in all three front-ends; `-n` turns it
      off on the command line, as it does for `ss`. The window and the terminal used to show numbers
      where `--connections` showed names, because reading the file is the platform project's job
      (§8.1) and the front-ends reference only `ProcessManager.Core`. The seam is
      `ISystemProbe.DescribePortNames`, which hands back the parsed table read once and held: the
      Linux probe reads `/etc/services`, the Windows probe
      `%SystemRoot%\System32\drivers\etc\services` — the same file in the same format, which is why
      only the path is platform-specific and the parser is shared and tested on every leg. A probe
      that has not learnt to look answers with an empty table, so a port keeps its number rather
      than acquiring an invented name. Amber because macOS, whose probe is a stub, is on that
      default. Four surfaces show an endpoint and all four were found by grep rather than by memory:
      the lower pane's network tab, the machine-wide network view behind the rail, the descriptor
      list's socket summary — a socket described as `:631` on one tab and `:ipp` on the next is one
      window disagreeing with itself — and the terminal's own tab. One test renders the same socket
      through two front-ends and holds their cells together, which is what §58 asks for rather than a
      promise in prose
- [x] 🟡 Interface — Linux, from the address the socket is bound to. `/proc/net/if_inet6` names it
      outright for IPv6; an IPv4 address is on the interface whose on-link subnet contains it, longest
      prefix first. A socket on the wildcard address is on all of them and shows `*`; an address no
      route claims — a multicast group, a point-to-point peer — is left unknown rather than guessed at.
      Amber because Windows has no equivalent reading yet
- [ ] Connection creation time
- [ ] Connection age
- [x] Bytes sent / received — payload each way over the connection's life, retransmissions included.
      Not what the interface carried: headers are not in it
- [x] Send rate / receive rate — those totals against the previous reading of the same socket, over
      the measured interval between them. The window's network tab has both readings; a one-shot
      listing has one, and says "not sampled yet" rather than dividing by an interval it invented
- [x] Packets sent / received — segments each way, which is the honest packet count for TCP: the
      kernel counts segments and the wire carries one packet per segment except where something
      fragments
- [x] **Retransmissions** — both halves. How often the segment currently awaiting acknowledgement has
      been sent again, which says this connection is losing packets *now*; and the cumulative count
      over its life, which says whether the path has ever been bad
- [x] Latency / RTT — the connection's own smoothed round-trip time, which is the latency of the path
      it is actually using rather than of a ping that may take a different route. A figure of zero is
      the kernel's initial value showing through and is reported as unmeasured, not as no latency
- [x] **Send / receive queue depth** — bytes written and not yet acknowledged by the peer, and bytes
      received and not yet read by the process. The pair is what says which end of a stalled
      connection is the slow one, and it is what `ss` puts in Send-Q and Recv-Q
- [x] Owning service — the systemd unit of whoever holds the descriptor, read off their cgroup,
      because a unit *is* a cgroup. The innermost one: a desktop application sits inside its user's
      session manager, which is itself a unit, and naming the outer one would report every program a
      user starts as belonging to the manager that started it. A slice is not an owner — it holds no
      processes of its own — so a cgroup with no unit in it answers nothing rather than the nearest
      thing that looks like one
- [x] Container / cgroup — the holder's cgroup path, which is what says whether a listening port
      belongs to the host or to a container
- [ ] Firewall / security context — no per-socket answer exists on Linux. A firewall rule matches
      packets by address, port and mark, and the kernel records no link from a rule back to a socket;
      answering it would mean reading the nftables ruleset and re-evaluating it against each row,
      which is a simulation of the firewall rather than a reading of it. `conntrack` knows about
      flows and not about which socket owns one

**Where the numbers come from.** Addresses, ports, states, queues and the current retransmit count are
read from `/proc/net/{tcp,tcp6,udp,udp6,unix}`. Everything else on a TCP row — bytes, segments,
round-trip time, the lifetime retransmission count — is not in `/proc` at all and comes from
`NETLINK_INET_DIAG`, which is the source `ss -i` reads. UDP is deliberately not asked: `udp_diag`
answers, but Linux keeps no byte total, no segment count and no round-trip time for a datagram
socket, so those columns say "there is no such thing" rather than "we have not looked".

**The kernel writes zeros where it has nothing to say, and they are not readings.** A listening TCP
socket's two queue columns hold the Fast Open queue length and the accept backlog rather than byte
counts; a socket in `TIME_WAIT` has no socket structure left to ask, so its owner, its queues and its
retransmit count are all literal zeros; a UDP row has a retransmission column that is always zero
because the protocol has no such concept. Each of those is reported as unknown, because passing them
on would say "owned by root, nothing queued, never retransmitted" about something nobody measured
(§72.3).

**A listening socket's `tcp_info` is the same trap one level down.** `tcp_get_info` clears the whole
structure, fills in the four fields that mean something for a listener — the pacing rate, the receive
threshold, and the accept backlog pair it aliases onto `tcpi_unacked` and `tcpi_sacked` — and
returns. Every counter this program reads out of it is therefore the `memset` and not a measurement,
so a listening row reports none of them. And `tcp_info` grows with every release: it was 240 bytes
not long ago and is 280 now, so a field past the end of what a kernel sent is that kernel not having
it, never a nought.

**Connection creation time is not available on Linux and the box stays empty.** The obvious place to
look is the descriptor — `/proc/[pid]/fd/[n]` has timestamps — and they are the time the `/proc`
inode was materialised, which is when something last looked, not when the socket was opened. A
process fourteen seconds old and a descriptor seven seconds old both stamp as *now* the moment they
are read. Reporting either as the connection's age would produce a number that moves when you look
at it. The socket diagnostics do not change this: `tcp_info` carries no creation time either, and its
closest fields are how long ago the connection last sent or received, which is not when it started.

Protocols:

- [x] TCP
- [x] UDP
- [x] IPv4
- [x] IPv6
- [x] **Unix / local sockets as a separate native category** — read from `/proc/net/unix`, which
      describes a different thing and is carried as one: a Unix socket is named by a filesystem path
      instead of an address and a port, is a stream, datagram or seqpacket endpoint, and has a peer
      the kernel keeps but does not publish. The listening ones are found in the flags column and not
      the state column, where a server and an unbound socket both read "unconnected" (§5.3)

Actions:

- [x] Go to process — it became a real command the moment there was a machine-wide connection view to
      invoke it from. In the lower pane's network tab there is still nowhere to go — every socket
      there belongs to the selected process by construction — so it lives on the view behind the
      rail, which shows the whole machine's sockets and the pid of each. Double-clicking a row did
      this already and from nowhere else, and a gesture with no menu item is, for somebody who works
      from a menu, the same as a command that is not there (§25.3). A socket whose owner this account
      may not see says so rather than navigating nowhere: the kernel gives an unprivileged reader the
      socket and withholds the inode's owner, and the difference between "nobody owns it" and "you
      may not ask" is the whole of §72.3
- [x] Process properties — the same row, through the same navigation: the owner is selected in the
      process list and the existing command opened on it, so one code path decides which process a
      properties window is about rather than two that have to agree about identity (§8.2)
- [x] Copy endpoint — both ends of the selected row, as one line worth pasting into a search. Taken
      from the drawn cells rather than re-read: a connection can close between a right-click and a
      menu choice, and what the reader asked to copy is what they were looking at
- [x] Resolve hostname — the network tab's own **Resolve hostnames**, checkable rather than per-row.
      The disclosure is the same either way, so it is one deliberate act with a visible state
- [x] Disable hostname resolution — off unless asked for, which is the stronger version of disableable
- [ ] Close connection where natively supported — Linux has `SOCK_DESTROY` on the same netlink family
      the counters come from, and it is what `ss -K` uses. It needs `CAP_NET_ADMIN`, so for the user
      a process manager is normally run as it could only ever refuse, and an item that can only
      refuse is a lie dressed as a feature (§32). It belongs with the elevated helper of §8
- [x] Terminate owner — with the confirmation §5.5 requires. The process is the pane's own rather
      than one read off a row: every row here belongs to the same process by construction
- [x] Search remote endpoint — the far end's address, without its port, handed to the session's
      browser. The engine is named in the item's own label rather than only in the code: this is the
      one thing in the program that reaches the network, §97's promise is that nothing goes out
      unasked, and an item reading only "search online" would be collecting consent without saying to
      what. The port is left off because it is noise in a search — the question is who the address
      belongs to, and `:443` only narrows it to pages that mention the port too.

      The term comes off the drawn cell rather than out of the record, for the same reason Copy
      endpoint's does, so `Humanize` reads back what `Humanize` wrote and one test holds the pair
      together. On a row with no far end — a listener, a Unix socket — the item is greyed rather than
      shown and then apologising (§32)

- [x] **Hostname resolution is asynchronous and globally disableable** — a blocking DNS lookup in a
      table that refreshes every second is a hang waiting to happen, and on some networks it is also
      a disclosure

# 41. Services view

Read on Linux; unbuilt on Windows and macOS. Control is written for systemd and reachable from all
three front-ends. One process's own unit is also a page of the properties window (§26).

Shared columns:

- [ ] 🟡 Name ✔ · description ✔ (which is systemd's display name) · state ✔ · enabled ✔ · PID ✔ ·
      binary/command ✔ · user/account ✔ · service type ✔ · arguments ✔ · dependencies ✔ ·
      dependents ✔ · start time ✔. **Failure state and last state change are not**, and cannot be:
      the manager keeps both in its own memory and writes neither to any file. Everything else came
      off disk — the account and the type out of `[Service]`, the arguments out of the `ExecStart`
      line, the dependencies out of the `[Unit]` keys *and* the `.wants` and `.requires` directories,
      and the start time off the manager's own runtime directory (below)

Windows-specific:

- [ ] Service type · service group · accepted controls · error control · start account ·
      delayed start · trigger information · required privileges · preshutdown timeout ·
      key modification time · driver service indicator — **blocked on the reading half**: nothing
      opens the service control manager, so `WindowsProbe.GetServices` answers with an empty list and
      there are no rows for any of these to be columns of

systemd-specific:

- [ ] 🟡 Unit name ✔ · main PID ✔ · fragment path ✔ · description ✔ · restart policy ✔ · masked ✔ ·
      load state ✔ · activation timestamp ✔ · sub-state 🟡. **The control PID is not**, and neither
      are the sub-states that go with it: `failed`, `auto-restart` and `start-pre` live in the
      manager's memory, and a unit in any of them looks like `dead` from out here. Three sub-states
      are on disk and honestly named — `running` when the cgroup has processes, `exited` when there
      is a current invocation and no processes, `dead` when there is no invocation — which is why the
      enum has three members and not systemd's dozen

launchd-specific:

- [ ] Label · domain · PID · status · executable/program · arguments · keep-alive · run-at-load —
      **blocked on the platform**: macOS is a stub (§6.3) and `MacOsProbe.GetServices` throws rather
      than answering, so there is nothing here to put a column beside

Actions:

- [x] Start · stop · restart · enable · disable · reload — through systemctl and whatever polkit
      decides, from `--service`, from a right-click on the window's Services page, and from the
      terminal's action menu on any process that belongs to a unit. Pause and continue are absent
      because systemd does not offer them, not because they are unwritten. The two that stop
      something are confirmed whatever the confirmation setting says: one of these units is what
      keeps the machine on the network. Enable and disable are worded as being about the next boot,
      so that nobody reaches for "disable" meaning "stop"
- [x] Open configuration · reveal executable · go to process · properties · copy ·
      inspect dependencies — all six from a right-click on the window's Services page, and all six
      offered on a machine with no manager to command. That is the point of them: opening a unit
      file, following a unit to the process systemd watches and reading what pulls it in are what
      somebody diagnosing a machine actually needs, and none of them asks a manager for anything. The
      six verbs above are dropped where there is nothing to ask; these six are not (§7). "Go to
      process" goes to the **main** process specifically, because everything else in the cgroup is a
      child systemd will take down with it. "Inspect dependencies" names each edge in systemd's own
      vocabulary rather than one word for all of them — `Wants` and `Requires` differ in what happens
      when the other unit fails — and says where the edge was found, because a setting in a unit file
      and a symlink in a `.wants` directory are changed in completely different ways

**A template instance shows its template's description**, `%i` and all: `user@1000.service` reads
"User Manager for UID %i". The specifiers are systemd's own and expanding them needs the same
substitution table `systemd.unit(5)` documents — a small piece of work that nobody has done, and the
raw string is at least visibly a template rather than a wrong name.
- [ ] Creating and editing services — deferred to a later release

**Read without D-Bus and without spawning `systemctl`.** Everything the columns need is on disk: the
unit files say what a service is, the `*.wants` symlinks say whether it starts at boot, the cgroup
tree says what is running and with which main process, and `/run/systemd/units` — which is a
directory of files the manager writes, not an interface to it — says when the current invocation of a
unit began. A D-Bus client is a substantial piece of machinery, and shelling out to read state is the
thing that stops working on the machine you most need it on.

**That runtime directory settled two things at once.** It holds one symlink per current invocation,
named `invocation:`*unit*, and the symlink's own modification time is the moment the manager created
it — checked against `systemctl show -p ActiveEnterTimestamp` on three units of three different types
and agreeing to the second. Its *presence* is worth as much as its time: a `Type=oneshot` unit with
`RemainAfterExit=yes` is active with nothing in a cgroup, and this reader used to call every one of
them inactive. There were 34 of them on this machine when this was written. The state column says `running`,
`active · exited` and `inactive`, which are three answers and not two, and `stopped` is no longer
said about a unit that is doing its job.

**Checked against `systemctl list-units --state=active`: 56 against 56, with no difference in either
direction.** Six of those were found only by that check. They are all instances of templates —
`systemd-pcrlogin@1000.service`, `user-runtime-dir@1000.service` — which finished and stayed active,
so they have an invocation, no processes and no file of their own; a scan of the cgroup tree alone
cannot see them at all. The running count is unchanged at 22 against 22.

**The dependency list is what is written down, and `systemctl show -p Wants` is not the same set.**
The manager adds a handful of its own to every unit — `system.slice`, `sysinit.target`, the mount its
files live on — so its answer for `NetworkManager.service` is three units longer than the file's. That
is a difference between two questions rather than a disagreement: this column says what somebody
configured, which is what somebody about to change it needs, and the implicit ones are the same for
every unit on the machine. Where a count here differs from `systemctl`'s, work out which set each
side is describing before changing either.

**Unit files are read with their drop-ins.** A `foo.service.d/*.conf` is the supported way to change
a packaged unit, and a reader of the main file alone reports the packaged answer for a unit the
administrator has already altered. The awkward half of the rule is that a drop-in *adds* to a
list-valued key and *replaces* a scalar one, and that a key written with nothing after the equals
sign clears whatever came before it — which is the only way a drop-in can take a setting away. All
three are in the engine rather than behind the platform seam, so the rule is tested without files.

Three shapes of running service had to be handled, and each was found by comparing against
`systemctl` rather than by reading the documentation:

- a service **nested in a slice of its own** — cups lives at `system.slice/system-cups.slice/`
- a service whose **own cgroup is empty because its processes are in a child** — systemd-udevd
- an **instance of a template**, whose file on disk is named for the template — `user@1000.service`
  from `user@.service`, and under `user.slice` rather than the system one

With all three handled, the list matches `systemctl list-units --state=running` exactly on this
machine: 22 against 22, with no difference in either direction.

**The control verbs go through polkit rather than through the privileged helper (§68).** systemctl
already carries the machine's own policy, so an ordinary user gets the prompt their desktop is
configured to give and a session that cannot prompt gets a refusal instead of a hang — which is not
a hypothetical: run `systemctl reload` on a unit from a terminal with no agent and it waits for the
prompt until the connection times out, then reports the timeout as though the manager were broken.
`--no-ask-password` turns that into an immediate refusal carrying the real reason. Routing this
through the helper instead would mean reimplementing the policy decision that polkit exists to make.

The refusal is classified by matching systemctl's message, and **nothing depends on that match**:
systemd's messages are translated, and this machine answers in German. A refusal on a non-English
desktop is filed as an ordinary failure and still shows the reader exactly what the manager said,
which is the part that matters.

# 42. Startup applications

- [ ] 🟡 Columns: name ✔ · enabled ✔ · status ✔ · startup impact ✔ (as a refusal — see the last box)
      · command ✔ · executable ✔ · location/source ✔ · user scope ✔ · file path ✔. **Publisher,
      signature, architecture, startup CPU, startup disk I/O and last launch are not read**, and each
      for its own reason rather than one: a publisher means asking the package database which package
      owns the program, which builds an index of every path on the machine and is not a price a view
      should pay for a column (§5.4); a signature means §70, and this build verifies none; the two
      cost columns need the boot measurement the impact box below refuses to invent; and nothing on a
      Linux desktop records when an entry last ran, so there is no file to read it out of. Each of
      them is a row in the entry's properties box saying so, because four missing rows read as four
      fields nobody wrote (§72.3)

Sources:

- [ ] Windows: registered startup applications · Startup folders · Run registry mechanisms ·
      supported startup tasks — **blocked on the reading half**: `WindowsProbe.GetStartupEntries`
      answers with an empty list, so there is nothing here to show or to switch
- [x] Linux: XDG autostart ✔ and systemd user units ✔. A user unit that `default.target` wants is a
      login-time entry by any reasonable reading — it is started when the session starts, by the
      manager the session starts with — and for as long as this program looked only in the autostart
      directories it reported a desktop that has moved its session to systemd as having nothing at
      login. Read from the same kind of files as everything else here: the `default.target.wants`
      directories say what is wanted, and the unit each symlink points at says what will run. Only
      services: those directories also hold timers and path units, and a timer is a schedule rather
      than something that starts at login. One case is an entry and is reported as one that will
      never run — an enablement whose unit was removed with its package, which is still somebody's
      symlink to delete, and leaving it out would hide the thing that needs doing about it
- [ ] macOS: login items · user launch agents — **blocked on the platform**: macOS is a stub (§6.3)
      and `MacOsProbe.GetStartupEntries` throws rather than answering

Actions:

- [x] Enable ✔ · disable ✔ · reveal executable ✔ · reveal configuration ✔ · properties ✔ · run
      now ✔ — all six from a right-click on the window's Startup page. The switch goes through the
      specification's own `Hidden=true`: a user's own entry is edited where it is; a system-wide one
      is never written to, because that file belongs to a package and the next update would overwrite
      whatever we did — it is switched off by writing a file of the same name into the user's own
      directory, which is the specification's override and the mechanism every desktop's own switch
      uses. Switching it back on removes that override rather than writing "not hidden" into it, so
      the package's file speaks again and a new command in it is not frozen out for ever.
      **A systemd user unit is neither of those and is handed to the manager that owns it**: its
      enablement is a symlink in a `.wants` directory, and writing one behind the manager's back
      would be ignored until the next login and wrong afterwards. Both mechanisms sit behind one
      switch, because a front-end has a row under the pointer and no business knowing which kind it
      is — the moment it has to know is the moment one of them quietly stops being switchable.
      "Run it now" strips the field codes a `.desktop` file's `Exec` carries, because `%U` is the
      launcher's business and several programs treat a literal one as a file name and fail on it
- [x] Delete entry — only where safe and explicit, which on this platform means the user's own
      desktop file and nothing else. A package's file is refused, and the refusal names the thing to
      do instead: deleting it looks like it worked and does not, because the next update of that
      package puts it straight back. A unit file is refused too, for a different reason — the
      enablement that lists it is a symlink, and removing the file behind it leaves the manager
      complaining at every login afterwards. Deliberately below a rule and last rather than beside
      the switch: turning an entry off is undone by the item at the top of the menu, and this is
      undone by nothing, which is the test §69 uses rather than a preference. Confirmed whatever the
      confirmation setting says, for the same reason

- [x] Impact categories are computed only where a **reliable measurement** exists; where it does not,
      the column says so rather than inventing a "Medium". There is no such measurement here:
      working out what an entry costs at login means timing a login, and nothing in this program
      does. So the column says `not measured` on every row, the heading says it once in words, and
      the entry's properties box gives the reason and says the same about the processor and disk
      columns beside it. A category derived from the size of the binary would be a guess wearing a
      measurement's clothes, and it is the one a reader would act on

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

- [ ] 🟡 Metrics: application ✔ · executable identity ✔ · CPU time ✔ · disk read ✔ · disk write ✔ ·
      average memory ✔ · peak memory ✔ · launch count ✔ · cumulative runtime ✔ · last launch ✔ ·
      history start date ✔.

      The application is the image path. Not the name, which is what a program calls itself and which
      two programs may share; not a digest either, which would make an upgraded program a different
      application and lose everything the old one had done — the question this answers is "what has
      this program cost me", and the answer has to survive its updates.

      The average is the working set integrated over time rather than a mean of means. A run lasting
      a second and one lasting a day would otherwise count equally, and the day is what the machine
      actually spent.

      **Foreground and background CPU time are not separable on Linux**: nothing in the kernel knows
      which window has focus, and the desktop that does knows nothing about processor time. **Network
      sent and received are refused** for the reason §18 gives at length — there is no per-process
      byte counter with a portable source, and every workaround is wrong in a way nothing on screen
      would betray. **Metered-network usage** needs a notion of a metered connection that no Linux
      desktop publishes. **Publisher** and **GPU time** are readable and are not written yet
- [ ] 🟡 Controls: enable ✔ (`history.usage`) · retention period ✔ (`history.usage.days`, counted
      from when a program was last seen rather than from when its record began — a program run every
      day since January is not old, and dropping it because its record is old would delete exactly
      the rows worth keeping) · export ✔, in the sense that the record *is* a documented
      tab-separated file with a header, which anybody can read, filter or delete a line out of
      without a tool. **Reset has no control of its own yet** — deleting the file is reset, and that
      is a thing somebody has to know rather than something the program offers
- [x] **Off by default.** A file recording which applications a person ran and for how long is
      surveillance if it appears without being asked for, however useful it is when it is asked for.
      Nothing is accumulated and the file is not created until `history.usage` says otherwise —
      verified by running with the setting absent and checking that no file appeared. It lives beside
      whichever settings file is in use rather than beside the default one, because `--settings`,
      `PROCMAN_SETTINGS` and a portable marker each move that: a portable install on a stick that
      wrote its record into the profile directory would leave behind exactly the file it exists to
      keep off the machine. That was wrong when first written and only a run against a real path
      showed it

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
- [x] Each rail row carries a **sparkline** over the same history the main graph uses — the same
      span, not merely the same ring: rail and graph share one time axis, so changing the graph to
      five minutes changes the rail with it
- [x] Each rail row carries a primary value and an optional secondary — `13 %  4.17 GHz`,
      `8.4 / 16.0 GB`, `↓ 11.6 KB/s ↑ 2.9 KB/s`, `42 %  57 °C`
- [x] The selected row takes a pale accent background and a 2–3 px accent stripe down its left edge
- [x] One resource selected at a time
- [x] The rail scrolls on its own when a machine has many disks, adapters or GPUs
- [x] Header: resource name large and left, full hardware model smaller and right —
      `CPU                    Intel Core i9-14900K`
- [x] Large detailed graph, whose series follows the selection. Every resource's history is recorded
      whether or not it is on screen, so selecting a disk that has been idle for a minute shows that
      minute rather than starting blank
- [x] Statistics in **two columns**: live measurements on the left, hardware specifications on the
      right — the two answer different questions and reading them as one list is what makes a
      performance page look like a data dump. The columns are sized before the graphs rather than
      after them: they held twelve rows and dropped the rest off the bottom of the window, which is
      how a memory page showed twelve of its fifteen live figures and looked complete doing it
- [x] Engineering diagnostics collapsed below both, so the default state is not overwhelming —
      nineteen figures on the memory page, five on the processor's, none where a resource has none
- [x] 🟡 Reference size 1280×780, minimum 900×600 — both windows now name a real minimum and relay
      out as they are dragged; graphs grow horizontally, the statistics do not reflow into the space

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
- [x] Battery — charge, draw, time remaining, health against design capacity, chemistry and
      cycle count; both the µAh and the µWh families of battery are read
- [x] Optional sensors and devices — every hwmon chip, grouped by chip, with temperatures and
      fans first and the electrical channels in the collapsed block

- [x] The page opens on whatever is under the greatest meaningful load rather than always on the
      processor, with a setting to turn that off — `performance.busiest=false`. **Meaningful** is
      the load-bearing word: only the resources whose primary measures how hard they are being
      worked are compared, because a battery at 100 % charge and a sensor chip at 65 °C on a
      hundred-degree scale are percentages of exactly the right shape that measure no load at all,
      and both beat a processor at 60 %. Without that test the page opened on a full battery

## 45.4 Graphs

- [x] 60 seconds by default, newest on the right, moving right to left
- [x] Updated once a second
- [x] Selectable history: 30 s · 60 s · 2 min · 5 min · 15 min — the axis is the span rather than
      the sample count, so a page open for sixteen seconds fills the right quarter of a minute-wide
      graph instead of stretching sixteen samples across it
- [ ] 🟡 Optional 500 ms mode — the page already draws whatever interval it is told, half a second
      included: the axis is a span in seconds divided by the sample interval, and `interval=0.5`
      gives the whole program a two-hertz tick that it renders correctly. What is missing is a
      control on the page itself, and that is not the page's to offer — the tick belongs to the main
      window, and a graph page that changed the rate under it would leave that window's own plots
      labelled with an axis they no longer have
- [x] Engineering graticule — major and minor rules, graph paper rather than an analytics chart
- [x] Thin resource-coloured stroke over a translucent fill; no data-point markers; no animation
      that gets in the way of reading the current value
- [x] Scale label in the corner — `100%`, `16 GB`
- [x] Axis labels: `60 seconds ago` at the left, `Now` at the right
- [x] Hover tooltip carrying the timestamp and that instant's readings — a rule down the sample and
      its readings drawn on the plot rather than in a popup, which on a stack of six graphs would
      cover the neighbour being compared against. The arrow keys walk it too (§45.9)
- [x] 🟡 Hovering a graph reveals Pause · history · mode · expand in its top-right corner — the four
      are there, above the graphs and to the right, but permanently rather than on hover: a control
      that appears only under a pointer is one nobody finds and one no screenshot proves
- [x] Pause freezes the drawing without clearing history or stopping collection, and says `Paused`.
      Counted in samples rather than remembered as an index, so a plot paused on a spike still shows
      that spike after the ring behind it has wrapped
- [x] Double-click or Expand opens an inspection view with current, minimum, maximum and average

### Scales

- [x] Fixed 0–100 % for CPU, GPU utilisation, disk active time and GPU power percentage
- [x] Dynamic for network throughput and disk transfer rate
- [x] Scale hysteresis, so a dynamic scale does not rescale every second and make the shape
      unreadable — a dynamic ceiling only ever doubles, so noise leaves it where it is and a real
      change moves it once
- [x] Temperature on a stable hardware-appropriate scale rather than a dynamic one — a fixed
      hundred degrees, so a card idling between 40 and 42 °C does not fill its graph

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

- [x] 🟡 One accent per resource, used by its sparkline and its graph alike — the two were worked
      out in different places and disagreed, a GPU's sparkline being orange while its graphs were
      teal. The hues are not yet the table's exactly: the plots keep the instrument's black ground
      with a green graticule (§7.2), and a resource's colour is the trace on it rather than the
      window's chrome
- [x] Every accent is overridable from the settings file (§67)

## 45.6 Missing readings

- [x] A sensor that cannot be read never shows a zero
- [x] It carries a tooltip saying why, from `Humanize.Explain`
- [x] A category the hardware does not have at all has its graph hidden rather than emptied — a GPU
      with no fan sensor shows no fan graph. Utilisation is the exception and is always plotted:
      its absence is itself the finding, and "this card has no fan" is a different sentence from
      "nobody can tell you what this card is doing"

**Deliberately not a single `—`.** Five reasons a value is missing are five different situations, and
collapsing them loses the one thing a reader needs: `n/a` means this OS cannot report it, `n/i` means
it can and we do not read it yet, `—` means this user may not, `?` means the counter contradicted
itself, `…` means one more sample is needed. "Not reported by this device" is only ever one of those
five, and telling somebody to run as root reads very differently from telling them to wait a second
(§5.3).

## 45.7 Density

- [x] **Comfortable** — more spacing, larger graphs, advanced statistics collapsed
- [x] **Compact** — tighter rows, more graphs at once, advanced statistics left open. Not merely
      smaller: somebody who asks for density has asked to see more at once, so the fourth level opens
      with it

## 45.8 Commands

- [x] 🟡 Right-click a resource: copy current values · copy full diagnostics · pause · change graph ·
      show kernel times · show logical processors · open hardware details — all but two of them.
      Kernel time is not a mode: it is always the second series under the total, because the reader
      is asking what fraction of a busy core is kernel and that is a comparison rather than a choice.
      A hardware-details window has nothing to say beyond what the right-hand column already does
- [x] Copy diagnostics — a plain-text snapshot of the machine, which is what makes a support
      conversation possible. Every level, including the one the window keeps collapsed: what is worth
      hiding from a reader looking at the machine is exactly what is worth sending to one who is not
- [x] `Ctrl+1`…`Ctrl+6` select overview, CPU, memory, disk, network and GPU — by name rather than by
      index, so the shortcut for a resource this machine does not have does nothing instead of
      landing on whatever took its place
- [x] `Space` pauses, `F5` resumes, `Ctrl+C` copies the selected statistics

## 45.9 Accessibility

- [x] Nothing is identified by colour alone — every graph carries a visible text heading, every band
      of the composition bar names itself wherever it is wide enough to, and the expander says "show"
      and "hide" rather than only turning a triangle
- [x] Keyboard navigation reaches every graph control — the plots take focus, the arrow keys walk
      the cursor along the axis, and the span, the pause and the inspection view all have shortcuts
- [x] 🟡 Screen-reader labels — every graph and every control in the strip names itself; the
      statistic rows are plain text and announce as what they say. The main window's plots, meter
      strip, list, rail and toolbar do too now (§74); what still announces as nothing is a table row
- [x] High-contrast support, and lines that stay distinguishable in it — the plot keeps its black
      ground under that scheme, and that is a decision rather than an oversight: an instrument has a
      ground of its own, black under bright inks is the highest contrast there is, and a flat black
      is not the blend of two colours the scheme exists to stop. What changed is what is drawn on
      it — the graticule comes up to a readable green, the caption, axis and cursor inks all come up
      to white, and the axis labels lose the drop shadow that was a second colour a pixel away
- [ ] Colour-blind-safe differentiation — and the core heat map is where this bites hardest. It is
      a green-through-amber-to-red ramp, which is the one ramp red-green colour blindness flattens
      completely, and there is not a digit on it to fall back to. Its textual summary (§74) is the
      first half of an answer and not the whole of one: what this box wants is a ramp that survives,
      or a number in the cell
- [ ] 100–200 % UI scaling — see §74's "scalable text". The blocker is not here: every painter,
      including the toolkit's own header and cell painters, draws with `ITheme.DefaultFont`, and
      `GtkBackend` pins `gtk-xft-dpi` to a 96-DPI baseline so the desktop's own factor never arrives

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
- [x] Virtualisation support **and** state — VMX or SVM from CPUID says the processor supports it;
      the hypervisor bit says this machine is itself a guest. Two different bits, constantly confused
- [x] **Feature sets** — instruction sets, cryptography, hardening and virtualisation, decoded from
      CPUID and grouped into one line each. Sixty rows of one word is a data dump; five sentences is
      a specification
- [x] **Signature** — family, model and stepping. The name is marketing and two very different parts
      can share one; the signature is what an erratum or a mitigation is written against
- [x] L1 / L2 / L3 cache — data and instruction separately at L1
- [x] Process count
- [x] Thread count
- [x] Handle / resource count — open descriptors across the whole machine, from
      `/proc/sys/fs/file-nr`, which is what Linux hands out instead of handles. One small file
      rather than a sum over four hundred processes, which is what makes it affordable at all
      (§3.5); its ceiling is `fs.file-max`, routinely nine quintillion, and is said in words
- [x] Uptime
- [x] **Pressure stall information** — how much of the last ten, sixty and three hundred seconds
      something was stalled waiting for the processor
- [x] Context switches per second
- [x] Interrupts per second
- ∅ System calls per second — refused. Linux keeps no machine-wide counter for them: the figure
      can be had per process with `ptrace` or a BPF probe, and both are far more intrusive than a
      performance page has any business being. The context-switch count moves at a similar speed
      and is a different number, which is exactly why it is not shown under this name (§5.3)

**Pressure is a different question from utilisation, and usually the better one.** A processor at
100 % is not in trouble if nothing is waiting for it; a processor at 60 % with things queued behind
it is. Pressure measures the second, which is what a person means when they say the machine feels
slow — and it is the one figure none of the tools this replaces shows well.

`some` is any task stalled; `full` is every task stalled at once, so nothing ran. Full is the serious
one: a machine above a few percent of full memory pressure is thrashing rather than busy.

All three windows are shown together because the shape between them is the information — ten above
sixty is a spike starting, ten below sixty is one ending, and all three alike is a machine that has
been like this for a while.

A kernel built without `CONFIG_PSI` or booted with `psi=0` has no such files, which leaves the
readings **unknown rather than zero**: a machine under no pressure and a machine that cannot say look
identical otherwise, and one of them may be thrashing (§5.3).

Three small files for the whole machine, so this costs three reads a sample however many processes
there are — unlike the per-process figures of §5.4.
- [x] DPC-like kernel activity — soft interrupts, which are what a DPC is on this machine: the work
      a hard interrupt handler deferred so it could return. Counted per second beside the hard
      interrupts, and their share of the interval broken out of the kernel time that already
      contains it, because kernel time cannot say whether the machine is in there for a process or
      for a device. A machine at 30 % kernel of which 25 is soft IRQ is an adapter drowning it
- [x] Frequency scaling — the range the clock is allowed, taken across every processor rather than
      from cpu0, because a part with favoured cores does not have one ceiling: this laptop's
      favoured cores reach 5.0 GHz and the rest 4.9, so cpu0's answer disagrees with `lscpu`. The
      governor comes with
      the driver enforcing it, because `powersave` under `intel_pstate` is a full-range governor and
      `powersave` under the generic driver is not — the name alone is routinely misread as a
      throttled machine
- [x] Where each logical processor sits — socket, physical core, its SMT sibling, whether it is a
      performance or an efficiency core, and which NUMA node its memory is on. Two logical
      processors sharing a core do not do twice the work of one, and a saturated efficiency core
      beside an idle performance core is a scheduling problem rather than a busy machine. A machine
      that publishes no topology gets none of those rows rather than a column of unknowns

The three rate counters, plus their cumulative totals, belong in the collapsed **system counters**
section of §45.2 level 4 rather than beside the utilisation — they are diagnostics, not status.

Layout (§45.1): utilisation and speed are the two largest figures on the page, with processes,
threads, handles and uptime beside them in the live column, and everything from base speed down to
the cache sizes in the hardware column.
- [x] Load averages

Read once and cached: none of it changes between samples, and walking the cache directories every
second would be indefensible against §71.

**ARM has no `CPUID`.** The identification registers exist but are privileged — `MRS` on
`ID_AA64ISAR0_EL1` traps from user code — so the feature list comes from `AT_HWCAP` and `AT_HWCAP2`
in the auxiliary vector instead, two words of bits that are an ABI between the kernel and userspace.
The signature comes from `MIDR_EL1`, which the kernel publishes for cpu0 under
`/sys/devices/system/cpu/cpu0/regs/identification/`. Both decode into the same shape as the x86
table, so the page rendering them does not know which architecture it is describing.

Nobody working on this has ARM hardware, which is precisely why the bit table is **held against the
kernel's own `hwcap.h`** in a test rather than against anybody's memory — a wrong index produces a
plausible feature list on a machine none of us can look at. Doing that caught two bits that were
wrong on the first pass, `MTE3` and `SME`.

- [x] arm64 instruction sets — NEON/ASIMD, SVE, SVE2, SVE2p1, SME, SME2, dot product, FP16, BF16, I8MM, MOPS
- [x] arm64 cryptography — AES, PMULL, SHA1/2/3/512, SM3, SM4, CRC32, the SVE variants, RNG
- [x] arm64 hardening — pointer authentication, BTI, MTE, MTE3, SSBS, SB, POE
- [x] arm64 implementer and part, from `MIDR_EL1`
- [x] 32-bit arm — `AT_HWCAP` and `AT_HWCAP2` as *that* architecture assigns them, which is a
      different word entirely and shares not one bit position with the table above: NEON is bit 12
      on 32-bit ARM and bit 1 on arm64, VFPv3 is bit 13 where arm64 has JSCVT. Decoding one
      architecture's words with the other's table produces a full, plausible and entirely wrong
      feature list, and because every bit is assigned in both there is nothing for a check to fail
      on — so the table is chosen by the process's own architecture and never inferred. Held against
      a vendored `arch/arm/include/uapi/asm/hwcap.h` in the same test the arm64 table gets. The
      *signature* is still arm64's alone: `MIDR_EL1` does not exist there and 32-bit ARM publishes
      the same fields as four lines of `/proc/cpuinfo`, which nothing reads yet — so that row is
      blank on such a machine rather than wrong
- [x] Windows on ARM — `IsProcessorFeaturePresent`, which is what Windows has instead of an
      auxiliary vector: a call per feature answering yes or no to a numbered question, asked once at
      startup rather than decoded from two words of bits. The ordinals are Windows' own and are an
      ABI, so they are held against a vendored copy of the `PF_*` list in a test for the same reason
      the arm64 bits are held against the kernel's header — nobody here has such a machine, and a
      wrong ordinal produces a plausible list on one none of us can look at. The crypto extension is
      one question covering AES, PMULL, SHA1 and SHA2, so the row names all four rather than
      claiming four readings from one bit

`CPUID` is read through `X86Base.CpuId`, which the runtime emits as the instruction itself: no native
library, no `[LibraryImport]`, one implementation answering on Linux and Windows alike, and
`IsSupported` false on ARM rather than an error there. The decoding is a pure function of the
register values, so the whole table is exercised on every CI leg — including the ARM one, which has
no such instruction to run.

**It is read only when the files beside it are this machine's.** CPUID answers about the processor
executing it and about no other, so a `--probe-root` replay must not consult it: mixing a fixture's
core count with this laptop's feature list describes two machines in one table (§9.4).

Windows answers the same questions by different means: `GetLogicalProcessorInformationEx` for cores,
packages, NUMA nodes and caches, and `CPUID` leaves 0x80000002–4 for the brand string. Asking the
processor rather than the registry avoids a dependency the trimmer would have to be told about, and
returns the same string, because that is where Windows got it. ARM64 has no CPUID and reports that it
does not know rather than inventing a name — the *feature* list there comes from the numbered
questions above, and on x86 Windows it comes from the same `X86Base.CpuId` the Linux side uses, which
is what "one implementation answering on Linux and Windows alike" was supposed to mean and did not:
the Windows host record carried no feature list at all, so every Windows machine reported an empty
one. The live clock speed is still `n/i` there — Windows
exposes it only through a performance counter or a power interface, neither worth opening to describe
a machine.

Graph modes:

- [x] Overall
- [x] Logical processors — a checkbox on the processor page, not a rail entry per core: twenty cores
      would put twenty entries in the rail and bury the disks under them, and "overall or per core"
      is one switch rather than twenty destinations. Ticked, the plot becomes a grid of one small
      plot per core; the terminal has no checkbox and prints them all
- [x] 🟡 Physical cores — SMT siblings sit adjacent in the heat map, so a physical core reads as a
      pair; they are not yet summed into one cell
- [x] NUMA nodes — one plot per node, each the mean of that node's own processors, since the kernel
      publishes no per-node CPU line. A checkbox beside the per-core one and exclusive with it: the
      two are different divisions of the same processor, and showing both at once would put a node's
      plot beside the plots of the very cores it is the mean of. Offered only where there is more
      than one node, because "node 0 is the whole machine" is not a distribution and a per-node view
      of it would be a second copy of the processor's own graph

The main window shows the cores as a **heat map** rather than a meter each. A bar per core is
readable at eight and useless at sixty-four — four pixels of width is a texture, not a reading —
whereas colour survives being small in a way a height does not. Cells wrap toward square, because a
colour is judged by area and cells of wildly different aspect get compared wrongly.

**Grouped, not merely listed** (§46 hybrid parts): performance cores first, efficiency cores after,
each socket its own block, SMT siblings adjacent. A grid in the kernel's enumeration order
interleaves the two kinds on some machines and separates them on others, so the same silicon would
not always look the same — and "the fast half is idle while the slow half is saturated" has to be
visible as a shape rather than worked out from sixteen numbers.

- [x] Performance and efficiency cores told apart — from the kernel's own hybrid PMUs,
      `/sys/devices/cpu_core/cpus` and `/sys/devices/cpu_atom/cpus`, which exist only on a hybrid
      part and name exactly which processors are which
- [x] Grouped by socket where the machine says which is which
- [x] ARM big.LITTLE — from `cpu_capacity`, which is not the guess from differing maximum clocks
      that was refused here before: it is the number the scheduler itself uses to decide that one
      core does more work per second than another, normalised so the fastest is 1024, and it is the
      kernel's own answer to exactly the question the map is asking. Read only where the hybrid PMU
      directories said nothing, and only believed where the capacities actually differ — this
      laptop publishes the file too and reports 1024 for all sixteen, which is a machine that is not
      hybrid rather than a machine of sixteen performance cores (§5.3)
- [x] Windows: the topology is read out of the buffer `GetLogicalProcessorInformationEx` was
      already being called for and was simply never read from — which socket each processor is in,
      which processors share a core, which NUMA node each is on, and the efficiency class that tells
      a hybrid part's two kinds apart. The class is a rank rather than a kind and higher is faster,
      so a machine reporting one class throughout is not hybrid; and past sixty-four processors a
      processor's number is its position in its group plus sixty-four per group before it, which is
      the arithmetic that decides whether the map draws a second socket or a second copy of the
      first
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
- [x] Usable memory — `MemTotal`, which is what the kernel can allocate rather than what is in the
      machine; the difference between the two is the hardware-reserved figure below
- [x] Used / in use
- [x] Available
- [x] Free (as distinct from available) — and the distinction matters more than any other figure
      here: a healthy machine keeps almost nothing free because it caches with the rest, and reading
      free as "how much you can use" is the most misread number in memory
- [x] Cached
- [x] Buffers
- [x] Committed
- [x] Commit limit — shown against the committed figure, because neither means much alone
- [x] Swap / pagefile used
- [x] Swap / pagefile total
- [x] Compressed — `Zswap` against `Zswapped`, and only ever as the pair: a gigabyte holding two and
      a half is a machine that has saved itself the difference in swapping, and either figure alone
      says nothing about the ratio
- [x] Kernel memory — reclaimable and fixed, which are what Linux has instead of paged and non-paged
      pools, plus page tables and kernel stacks
- [x] 🟡 Paged pool total — `SReclaimable`, which is the nearest true equivalent
- [x] 🟡 Nonpaged pool total — `SUnreclaim`, likewise. Both are named for what they are on this
      machine rather than for what Windows calls them (§5.3), and both are in the collapsed block
      with the slab total beside them — where they are now actually visible, which they were not
      while a column held twelve rows and these were the thirteenth and fourteenth
- [x] Hardware reserved — installed less usable, where the installed figure was read. Where it was
      not, the row is refused with its reason rather than computed as a zero: subtracting a number
      nobody read gives a plausible nought, and "the firmware kept nothing for itself" is a claim
      about the machine
- [x] Memory pressure — both halves, `some` and `full`
- [x] **Memory speed** — the *configured* rate rather than the rated one wherever the record is long
      enough to carry it: a DDR5-5600 module the board would only train to 4800 is doing 4800, and
      the rated figure would describe a machine nobody has
- ∅ **Channels** — how many channels the modules are interleaved over is in no type-17 record and in
      no file the kernel publishes for an ordinary machine. Type 17 describes a device and the slot
      it sits in, never the controller's interleave; the locator strings that look like channel
      names — `ChannelA-DIMM0` — are vendor-formatted text, and a parser reading channels out of
      them would be guessing at a string the firmware author was free to write any way at all.
      Linux exposes the real figure only through EDAC, and only where a driver for that particular
      controller is bound, which on this laptop is nowhere. Refused with its reason (§5.3)
- [x] **Form factor** — DIMM, SODIMM, CAMM and the rest of the enumeration. The two values that mean
      the firmware declined to say — `Other` and `Unknown` — report not knowing, rather than
      printing the word "Unknown" as though it were a shape
- [x] **Slots used / available** — every type-17 record is a slot, populated or not, so an empty one
      is counted as a slot and not as a module. Four slots with nothing in any of them is a readable
      answer; nought bytes installed is not, and is refused
- [x] NUMA distribution — how much memory each node has, from `/sys/devices/system/node/node*/meminfo`,
      and only where there is more than one node: "node 0 has all of it" is not a distribution

The four in bold are the Task-Manager-style hardware facts, and three of them come from DMI/SMBIOS
type-17 records — one record per memory device on the board. The same bytes on both operating
systems, so the same parser answers on both and is tested on machines that have neither: every field
is bounds-checked against the record's own declared length rather than against the version in the
anchor, because firmware in the field is routinely a revision behind its own version number and a
record that stops before the configured speed is ordinary. Reading past its end would report the next
record's handle as a clock rate.

On Windows the table is `GetSystemFirmwareTable`'s `RSMB` provider and needs no elevation at all. On
Linux the same bytes are `/sys/firmware/dmi/tables/DMI`, mode 0400 root, and there are three ways to
end up looking at them:

- **The program is root**, or is replaying a recorded tree that carries a copy, and reads the file
  directly. That is what makes the whole path testable rather than just the parser.
- **The helper is already running**, because somebody elevated for something else, and answers one
  frame with the table. The helper reads it from a constant path of its own and this is the one
  opcode that names no process — a firmware table is a fact about the machine, the same bytes
  whoever asks, so the recycled-pid check has nothing to check. Every opcode that *does* name a
  process still goes through it, and the split is by opcode, so nothing can talk its way past the
  check by leaving a key out (§8.2).
- **Neither**, which is the ordinary case, and the rows say `—` and mean "you may not look". The
  helper is deliberately *not* started for this: a machine description is not something the user
  asked for, and a password prompt raised by drawing a page is precisely what §8 exists to prevent.

A machine with no `CONFIG_DMI` at all — most ARM boards, most virtual machines — has no such file,
and that is `n/a` rather than `—`. The difference is the whole reason both reasons exist: one of them
is a machine where starting the helper would answer the question and the other is not (§5.3).

The page renders from `PerformanceReport`, which is data rather than a window — so `--host` on a
terminal and the desktop view show the same figures by construction, and the content is unit-tested
in a way a window is not (§58).

Graphs:

- [x] Physical-memory usage — 60 seconds, scaled to installed memory rather than to 100 %, because
      the useful question is how much is gone and not what fraction: 60 % means nothing until you
      know whether the machine has 8 GB or 128
- [x] Committed memory — its own series and not a second line on the first, because it counts what
      has been asked for rather than what has been taken, routinely exceeds physical memory, and on
      one axis would either be clipped or squash the physical line into the floor
- [x] **Composition bar** — one horizontal bar under the graphs, split into in use, modified, cached
      and free, each in its own shade of the memory accent, each naming its value in place and
      explaining itself on hover
- [x] Memory pressure — both halves; `full` is the one that says the machine is thrashing
- [x] Swap — on the swap device's own scale, and absent altogether on a machine with no swap: a flat
      line along the floor of a scale of zero draws the absence of a device as an idle one
- [x] Cache — cached plus buffers, on the machine's scale, directly under the physical series. Cache
      falling while physical memory stays put is the kernel giving its cache away; swap rising after
      that is the point where it has run out of cache to give, and neither is visible in the physical
      series, which stays pinned near the top through all of it

**The bands are a partition and sum to the total exactly.** That is what makes it a bar rather than
four numbers, and every definition here is bent to fit it:

- **In use** is total less available — the same figure the statistics beside it show, so the bar and
  the numbers cannot disagree
- **Modified** is dirty plus in-writeback: cache whose contents differ from the disk, so it cannot
  simply be dropped. Carved *out of* the cache rather than added beside it, which is what it is on
  both operating systems
- **Free** is physically unallocated, which on a healthy machine is nearly nothing
- **Cached** is the remainder — deliberately the remainder and not `Cached + Buffers + SReclaimable`,
  because that sum counts pages the kernel does not consider reclaimable and the four bands would
  overrun the total by a few percent, so the bar would lie about its own scale

Every band is clamped, because `meminfo`'s lines are read one after another without a lock: a machine
that allocated a gigabyte between two of them can report a set that does not add up, which is
ordinary. A band of negative width is not (§5.3).

Advanced counters, collapsed (§45.2 level 4): commit current/peak/limit · file cache
current/peak/minimum/maximum · page faults by kind — total, copy-on-write, transition, demand-zero,
cache · kernel pools, paged and non-paged, with their allocation and free counts.

# 48. Disk performance

For each physical disk or device:

- [ ] 🟡 Friendly name ✔ · model ✔ · media type ✔ (rotational or solid state, and *unknown* when
      the kernel does not say) · capacity ✔ · serial ✔ · bus/interface ✔ · mounted volumes ✔ ·
      system-disk indicator ✔ · page/swap indicator ✔ · formatted capacity is not read — it is the
      sum of the file systems' own sizes rather than anything the block layer knows, and a `statvfs`
      per mount is a different kind of read from the rest of this
- [x] Active time ✔ · read rate ✔ · write rate ✔ · read IOPS ✔ · write IOPS ✔ · cumulative reads
      and writes ✔ · average response time ✔ · queue length ✔ · per-direction latency ✔

**Active time says a disk is busy and nothing else.** The three figures that say whether it is
*keeping up* are the milliseconds each direction waited and the time-weighted queue depth, all three
already in `diskstats` and previously skipped over. A disk at 100 % active time with a queue of one
is saturated by a single client; the same disk at the same active time with a queue of thirty is
being asked for far more than it can do, and nothing else on the page tells those apart. The
arithmetic is `iostat`'s, and was checked against it on a disk under load.

A disk with no requests in the interval says **idle** rather than the placeholder meaning "wait one
interval" — on a disk that stays idle that is a wait with no end (§45.6).

**Which disk a mount is on is followed down the stack, not looked up by device number.** btrfs and
ZFS report a synthetic `major:minor` of their own — the root of the machine this was written on is
`0:30` — so a lookup by device number finds nothing for exactly the file systems most likely to be
the root one. The source path is resolved through `/sys/block` instead: a mapper name to its `dm-N`,
a device-mapper target to its slaves, a partition to the disk holding it. Every layer is credited,
because the root really is on the volume, on the container under it and on the disk under both. A
swap *file* is charged to the disk under the file system holding its path, found by the longest
mount point that path begins with.

Sources: `/proc/diskstats` — one file for the whole machine, which is what makes this affordable on
the sampling path where the per-process figures of §18 are not. Whole devices only: a partition is
charged the same I/O as the disk holding it, so counting both reports twice the traffic. `/sys/block`
decides which is which, because the name cannot — `nvme0n1` ends in a digit and is a whole disk.

Windows needs `IOCTL_STORAGE_QUERY_PROPERTY` and the disk performance counters, and has neither.

Graphs — two, not one:

- [x] **Active time**, fixed 0–100 %
- [x] **Transfer rate** on a dynamic scale whose unit follows the traffic, as two lines: reads as
      the filled area and writes as a line over it, both in the disk's own accent. Their sum was one
      line until now, and a disk reading at 500 MB/s and one writing at 500 MB/s draw the same
      combined line and are not the same machine. Active time says a disk is busy; only the transfer
      rate says whether that is a hundred large reads or a hundred thousand small ones

- [ ] Optional hardware-health plugin: temperature · wear · SMART/NVMe health · remaining life —
      the temperature is already on the page as its own hwmon chip (§45.3), and the rest needs a
      privileged `ioctl` per device: `NVME_IOCTL_ADMIN_CMD` for a health log, `SG_IO` for a SMART
      one. Both are a different kind of access from every read in this program, which is the reason
      the box below exists
- [ ] Hardware health is **separately permissioned**, because platform coverage varies too much for
      it to be a baseline promise

# 49. Network adapter performance

- [x] Name ✔ · state ✔ · description ✔ · type ✔ · interface index ✔ — the type from what the kernel
      publishes about the interface rather than from its name: `phy80211` exists only on a wireless
      one, a `device` symlink is what a real piece of hardware has, and its absence is what makes an
      interface virtual. Names are a convention that predictable naming already broke once, and
      `enp0s31f6` begins with none of the old ones
- [x] Link speed where the kernel reports one — absent on Wi-Fi and on anything virtual, and
      reported as unknown rather than as a dead link — and utilisation where there is a speed to
      divide by, and only there: a percentage of a guessed denominator is worse than no percentage
- [x] Send rate · receive rate · errors · drops — packets are counted and their rate is computed
- [x] MTU ✔ · MAC address ✔ · addresses ✔ with their prefix lengths · gateway ✔. The addresses come
      from `getifaddrs`, because no `/proc` file lists an interface's IPv4 ones; the gateway from the
      routing tables, whose two files disagree about byte order in a way that produces a plausible
      address belonging to somebody else
- [x] DNS servers where readable — and where `resolv.conf` holds nothing but systemd-resolved's stub
      listener, the resolver's own upstream list instead: 127.0.0.53 is this machine talking to
      itself and describes nothing about the network
- [x] Wi-Fi SSID and signal strength where permitted — the SSID through the wireless extensions,
      which every driver still answers and a kernel built without `CONFIG_CFG80211_WEXT` refuses,
      reported then as unknown rather than as an adapter associated with nothing; the signal from
      `/proc/net/wireless` in dBm, with a positive "dBm" refused because it is the old relative scale
      wearing this one's name
- [x] Graph modes: total · send vs receive — receive as the filled area and send as a line over it,
      both in the one network accent, on a dynamic scale that follows the traffic. Two lines in two
      shades of one colour are a pair nobody can tell apart at a pixel wide, which is why it is one
      of each rather than two of the same
- [ ] 🟡 Wi-Fi pages additionally carry SSID ✔ · signal strength ✔ · channel ✔ · band ✔ · protocol
      is not: the negotiated 802.11 generation lives in the association's rate table behind nl80211,
      and an adapter's own capability is not what it negotiated (§5.3)

Sources: `/sys/class/net/*` and `/proc/net/dev` for the counters and the description; `getifaddrs`
for the addresses, `/proc/net/route` and `/proc/net/ipv6_route` for the gateway, `resolv.conf` for
the nameservers, and the `SIOCGIW*` ioctls for what a wireless adapter is associated with.
`GetIfTable2` and `GetAdaptersAddresses` on Windows.

# 50. GPU performance

- [x] Adapter name · vendor · device · driver
- [ ] 🟡 Memory totals ✔ · dedicated memory ✔ · current dedicated usage ✔ · how busy the memory bus
      is ✔ · shared memory is not read — what a card borrows from the machine's own memory is what
      Task Manager's second figure is, and nothing on Linux publishes it: NVML's BAR1 is an aperture
      rather than an allocation, and the DRM accounting is per client. The dedicated figure is
      `nvmlDeviceGetMemoryInfo_v2`, because the original call counts the driver's own reservation as
      in use and put the page four hundred megabytes above what every other tool reports
- [x] Overall utilisation
- [ ] 🟡 Per-engine utilisation: encode ✔ · decode ✔ · compute, graphics and copy are not read —
      `nvmlDeviceGetUtilizationRates` reports one figure for the shaders with graphics and compute
      already summed, and no interface takes them apart. The two video engines are shown as one plot
      with two lines, because a card that is transcoding is using both and either alone reads as an
      idle video block
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

- [x] **Utilisation**, 0–100 % — the per-engine selector (3D · compute · copy · decode · encode) is not
- [x] **Dedicated memory**, scaled to the card's VRAM — `14.7G / 16.0G`
- [x] **Memory bus**, which sysfs and NVML both offer and Task Manager does not
- [ ] **Shared memory** — not read, and refused with its reason rather than drawn as nought: see §50
- [x] **Video engines**, encode against decode on one plot
- [x] **Power**, scaled to the ceiling and labelled with both figures — `25.5 W of 130.0 W`
- [x] **Temperature**, on the red accent so it never reads as another utilisation figure, and on a
      fixed 0–100 scale so a card idling between 40 and 42 °C does not fill its graph
- [ ] 🟡 **Fan** — the duty cycle ✔, the tachometer ✔ and the number of fans ✔; a card with more
      than one reports only the first. The percentage was previously taken from `fan1_input` where
      `pwm1` was missing, and that is revolutions a minute — a fan at 1800 rpm was drawn as being
      1800 % of the way up its range. They are two readings of the same fan and neither can be
      computed from the other, because nothing publishes the speed a fan turns at flat out

Every scale label is in its series' own unit. They were all rendered as quantities of bytes, so a
card's power graph was labelled `130 B` two inches from a caption reading `15.6 W of 130.0 W`
(§76).

A card that exposes no fan sensor shows **no fan graph** (§45.6) rather than an empty one.

Statistics below, in the two columns of §45.1 — live: utilisation, dedicated and shared memory,
temperature, fan, power. Hardware: model, driver version and date, PCI location, resizable BAR, and
where supported the core and memory clocks, voltage, power limit and temperature limit.

# 51. System activity view — expert

- [x] Top CPU processes
- [x] Top memory processes
- [x] Top disk readers
- [x] Top disk writers
- [ ] Top network senders — no per-process byte counters exist (§18)
- [ ] Top network receivers — likewise
- [ ] Top GPU processes — needs per-process GPU attribution (§19)
- [x] Process creation rate
- [x] Process termination rate
- [x] Context-switch rate — machine-wide, from the kernel's own counter
- [x] 🟡 Thread creation — the live thread count; the rate of creation is not tracked
- [x] Disk activity
- [x] Network activity — what the machine is sending and receiving, summed across its interfaces
      with loopback left out: traffic a host sends to itself crosses no wire and is counted twice,
      so a database and its client on one box would otherwise read as heavy network users while
      nothing had left it. This is the machine's figure and not any process's — §18 refuses
      per-process byte counters because no portable source exists, and that refusal says nothing
      about the interfaces, which have counted every byte since boot. A reader seeing a busy link
      over an idle process table has learnt something: whatever is using it is not here
- [x] Clicking any top-process entry navigates to that process — the row carries the identity pair
      through to the window, which re-checks it before moving. The page is modeless and outlives any
      one sample, so a row clicked a second after it was drawn must not be able to take somebody to
      whatever has since been given that number (§8.2); if the process has gone it says so. A pooled
      label that is emptied forgets whose row it was, or a click on blank space would navigate

This is the question a sorted table answers only if you already sorted it by the right column.
Somebody opening a system page wants three answers at once — what is using the processor, the memory
and the disk — and getting them from a table means re-sorting it three times and losing their place
each time.

**Selection, not sorting.** Finding the top five of four hundred by sorting is four hundred log four
hundred comparisons, three times over, every second; a single pass keeping the best five is four
hundred. On a page refreshing at one hertz that difference is the whole cost of the feature. The
selection is hand-written, so a test holds it against the answer a sort would have given, for several
different arrival orders — a top-five list that is subtly wrong is worse than none, because nothing
about it looks wrong.

A process using none of a resource is left out rather than padding the list with zeros, and one whose
counter cannot be read is left out too: an unreadable reading is not a small one, and must not sit at
the bottom of the list as though the process were idle (§5.3).

Started and ended come from comparing two snapshots rather than from the kernel's cumulative
`processes` counter, which only ever counts *forks* — it cannot say how many went away, so a machine
churning a thousand short-lived processes a second would look identical to one that started a
thousand and kept them. Both are divided by the real elapsed interval rather than assumed to be a
second, or a page refreshing every five seconds would quintuple everything.

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

- [x] Executable / command · arguments · working directory · environment overrides
- [ ] Selected user / account where supported
- [ ] Elevation
- [x] Launch suspended — expert mode
- [x] 🟡 Priority · affinity ✔, and I/O priority; a CPU set is not offered
- [x] 🟡 Environment inheritance ✔ and shell execution deliberately *not* offered; terminal and
      console behaviour are not controlled
- [ ] Recent commands are optional and clearable — nothing is remembered at all yet, which satisfies
      the clearable half by accident rather than by design
- [x] **Passwords and secrets are never retained** — there is no field for one on the request, and a
      test enumerates its surface so the day somebody adds one in good faith it fails

`procman --run PROGRAM [ARG...]`. Everything after `--run` belongs to the program being started,
including anything that looks like one of this program's own switches: a launcher that ate its
child's `--help` would be useless for exactly the programs somebody most wants to start.

**Arguments are already split and the shell is not involved.** A single string would have to be
re-split by somebody, and every program that tries gets quoting wrong for at least one shell.

**The environment is added to, not replaced.** A process started with an emptied environment loses
its locale, its display and its path, which is never what somebody setting one variable meant.

**The scheduling is applied after the process exists**, because there is no portable way to start one
that is already niced. So a launch can succeed while its priority does not, and the result says the
process started and names what could not be applied rather than reporting a failure for a program
that is now running.

The pid and the identity pair are reported separately, because they become known at different moments
and one can exist without the other: a program that exits before it can be read back — `echo`, or
anything that fails on its own terms — has a pid and no readable start time. **That is a successful
launch of a short-lived program, not a failure to start one** (§8.2), and it is only worth mentioning
when there were settings that now cannot be applied.

Running as another user stays the platform's own job — `sudo`, `pkexec`, `runas` — which is why there
is no credential on the request to begin with.

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
- [x] Left view selector or compact tab row — a tab row: the process list and the performance page,
      clickable, with the paused, case, ticked and filter state on the right of it
- [x] Primary table / tree
- [x] Optional lower pane — `Tab`, and it carries the figures a plot cannot be read for
- [x] Bottom command / help bar

Responsive breakpoints:

- [x] Full desktop terminal — 140 columns and wider: every column
- [x] Medium SSH terminal — 100 to 139: one history, and the numbers that pay for their width
- [x] Narrow terminal — under 100: five columns, and one aggregate processor meter instead of
      sixty-four bars four characters wide

The boundaries are measurements, not preferences. Thirteen columns and their separators are 126
characters before the process name has one, so the full set needs 140; the medium set is 70, which
leaves an eighty-column terminal ten characters of name — enough to render every row as `kthread`.
A layout somebody has changed by hand is never re-picked by a resize.

## 57.2 TUI process view

- [x] Columns adapt automatically to width
- [x] Horizontal scroll — `Ctrl+←`/`Ctrl+→`, or `(` and `)` where the terminal will not send those
- [x] Column sets — in the column chooser, `c`, and from `--columns @name`
- [x] Hide / show columns
- [x] Resize columns
- [x] Pin first column — and any number of them: `#` pins up to the column cursor
- [ ] 🟡 Switch friendly / tree / flat mode — tree and flat only

A column takes what it asks for, what is left, or what is left once the columns behind it have their
share — whichever is smallest. The process name asks for 120 characters because the window has them,
and a terminal that gave it all of them drew nothing at all of whatever was ordered after it.

## 57.3 Keyboard model

- [x] `↑/↓` or `j/k` move
- [x] `←/→` or `h/l` collapse/expand
- [x] `Enter` properties / open
- [x] `Space` select
- [x] `/` search — finds the row and leaves the rest on screen
- [x] `f` advanced filter — the query language of §56, with the parser's complaint on the status line
- [ ] 🟡 `s` sort — sorting is `F6`, `<` and `>`; `s` is the scheduler class, and `keys.conf` can
      swap them round
- [x] `c` columns
- [x] `p` pause
- [x] `r` refresh
- [x] `d` how often it samples — the picker of §12, on the letter every other terminal monitor binds
      a sampling delay to
- [x] `x` action menu
- [x] `t` terminate / end-task menu
- [x] `S` suspend / resume
- [x] `n` network
- [x] `m` modules
- [x] `h` handles (contextual)
- [x] `T` threads
- [x] `g` graphs / performance
- [x] `?` help — generated from the binding table, so it lists the keys as they are bound now
- [x] `q` close / back / quit according to depth
- [x] Bindings are customisable — `keys.conf` beside the settings file, one `action = key, key` a
      line; a typo is reported on start-up and leaves the built-in binding alone

## 57.4 TUI graphs

- [x] Block graphs — the U+2581–U+2588 eighth-block ramp
- [x] Braille graphs where supported — `--graph-style braille`: two samples a cell at four levels,
      against one at eight
- [x] Sparklines
- [x] Textual min/avg/max/current fallback — the lower pane always, and `--graph-style numbers` puts
      the figures in the columns themselves
- [x] **No information exists only as graphical colour**
- [x] ASCII fallback when the locale or terminal cannot show blocks

The block ramp is locale-dependent, which broke CI once: the golden frame was generated under a UTF-8
locale and compared under `LANG=C`. There is now an explicit `UseBlockCharacters` switch, pinned in
the capture and asserted by a test that checks both ramps differ *and* each is stable.

## 57.5 Mouse

- [x] Select — a click picks the row *and* the column under it, which is what the copy and resize
      keys then act on; the gutter ticks the row, as does Ctrl+click
- [x] Scroll — the wheel, three rows at a time
- [x] Pane resizing — dragging the divider above the lower pane
- [x] Tab selection
- [x] Context / action menu — the right button
- [x] Keyboard functionality remains complete without a mouse

Both report forms are read: SGR (`ESC [ < b ; x ; y M`), which is the one asked for and the only one
that can name a column past 223, and the original X10 form for terminals that ignored the request. A
click resolves through the same placement the drawing used, so a header that moved because a column
was pinned is still the header that gets clicked. `--no-mouse` turns the whole thing off.

---

# 58. GUI / TUI parity contract

- [ ] 🟡 Every view declares its GUI representation, TUI representation, CLI equivalent, canonical
      fields and canonical actions
- [ ] A feature is not complete until GUI and TUI parity exists, unless explicitly marked
      "GUI-only interaction"

Permitted GUI-only interactions: locating a native desktop window by dragging a crosshair; native
drag and drop.

Owed and named rather than assumed: **send a signal, set a resource limit, set the out-of-memory
priority and freeze a cgroup** reached the window and the command line together and have no terminal
command yet (§25).

One smaller disagreement of the same kind, now closed: the window read `confirm.destructive` and the
terminal did not, so one preference produced two programs — the terminal asked about a terminate
whatever the file said. Stricter, so never unsafe, and exactly the sort of gap nobody sees until
somebody sets the preference and finds half of it took. Both front-ends read the same table now, and
the classes still overrule it in the two places that matter: an unsafe action, and anything aimed at
a process the machine depends on (§69). The information each of them acts on is reachable from the TUI — `--limits` prints
every ceiling and the out-of-memory standing — so the second clause above holds; the actions
themselves do not, and this is where that is written down instead of being discovered later.

- [x] The information such a feature retrieves is still accessible elsewhere — the second clause
      above, held in both directions and checked over the registry rather than by inspection: every
      field the window can show is exportable from the command line except the three drawn histories,
      which refuse by name rather than exporting a picture as text, and every view behind the rail
      has a verb. The audit that established it found exactly one gap — the environment block, which
      the window had a page for and neither of the others could reach at all — and `--environment`
      closed it (§102)

---

# 59. CLI

- [x] `procman ps`
- [x] `procman ps --tree`
- [x] `procman ps --columns pid,name,cpu,memory` — with `--format` for the six formats
- [x] `procman ps --filter 'cpu > 50'` — as `--filter`, plus `--help-fields` listing every
      field, its aliases and the filter grammar, generated from the registry so it cannot drift
- [x] `procman process 1234` — as `--process 1234`, the summary page the window and the terminal
      both open with
- [x] `procman process 1234 threads` — as `--process 1234 threads`
- [x] `procman process 1234 modules` — as `--process 1234 modules`
- [x] `procman process 1234 handles` — as `--process 1234 handles`, or `fds`
- [x] `procman process 1234 network` — as `--process 1234 network`
- [x] `procman kill 1234`
- [x] `procman suspend 1234`
- [x] `procman resume 1234`
- [x] `procman --signal 1234 TERM` — any signal, by name or by number
- [x] `procman --rlimit 1234 nofile 512:4096` — one ceiling, in `prlimit`'s own `soft:hard` spelling
      so an answer can be checked against the tool
- [x] `procman --oom 1234 500` — how likely the out-of-memory killer is to choose it
- [x] `procman --freeze 1234` / `--thaw` — the whole cgroup, which is what it says
- [x] `procman service list` — as `--services`
- [x] `procman net` — as `--connections`, with `=unix` and `=all` for the local sockets, which a
      desktop has seventeen hundred of against a dozen internet ones
- [x] `procman --host` — the §96 summary, which is `perf cpu` without the graph
- [x] `procman --startup` — what will run at login
- [x] `procman --users` — who is logged in, and what their processes cost
- [x] `procman --services` — which services exist and which are running
- [x] `procman perf cpu` — as `--perf cpu`: forty samples a tenth of a second apart, plotted beside
      the figures `--host` prints without them. Also `memory`, `disk`, `net`, `gpu`, or a device by
      name — `--perf nvme0n1`, `--perf wlp148s0`

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

**The five process pages are the terminal's detail view, printed rather than drawn.** Both read the
same table builder, so the columns a script asks for and the columns somebody sees over ssh are the
same columns by construction rather than by two lists agreeing for as long as anybody keeps them in
step — the argument that put the fields in one registry (§5.1, §58). `environment` is a sixth page
for the same reason, and `--environment` remains the older spelling of it.

**They are text and take no `--format`.** Every cell on them has already been through the humaniser,
and §76 requires that a machine format carry the raw measurement rather than the rounded string a
screen shows: a CSV of `1.5G` cannot be summed. Offering one here would be a promise the page cannot
keep, so it is not offered — `--list --format` is where an exportable table lives.

**`--perf` is the graph `--host` has no room for.** The sections are the same ones, from the same
builder the window's performance page draws, so the three cannot disagree about the machine (§58);
what this adds is time. A tenth of a second between samples rather than the `--interval` default of
one, because forty samples at a second each is forty seconds of waiting for a plot — an `--interval`
given on the command line is honoured, since then the wait is what was asked for. A processor brings
its cores with it: a terminal has no checkbox to reveal them behind, so it prints them (§46).

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

- [ ] 🟡 Records: process start ✔ · process exit ✔ · high CPU alert ✔ · high memory alert ✔ ·
      user action ✔ (its own category, because somebody reading a timeline after an incident has to
      be able to tell what the machine did from what they did to it, and a line that does not
      distinguish them will be misread under exactly the pressure it exists for) · service state
      change ✔ where a rule watches one.

      Connection appeared and disappeared, executable and signature change, and this program's own
      privilege escalation are **not recorded yet**. The first would need the socket table walked
      every tick, which §5.4 keeps out of the sampling loop; the other two are readings that exist
      and are simply not wired to the log.

      **In memory and bounded, and nothing is written to disk.** That is the difference between this
      and §44's record, which is off unless asked for because it outlives the session: a ring that
      dies with the program records nothing about anybody after they close it. Fed from what the
      sampler already computed, so a timeline costs no reading of its own — a monitor watching itself
      is the one thing it must not become. The first sample records nothing, or the first line would
      be four hundred processes starting and would hide everything after it
- [ ] 🟡 Columns: time ✔ · event ✔ · PID ✔ · category ✔, in the terminal's timeline overlay,
      newest first because somebody opening it has just noticed something and wants the most recent
      thing rather than to scroll past an hour to reach it. A heading each time the category changes.
      An entry about a process still running goes to it; one about a process that has ended says so
      rather than moving the cursor somewhere arbitrary — which is most of what a timeline holds, and
      the reason it exists. **There is no window view yet**, and process name and details are the one
      sentence rather than separate columns
- [ ] 🟡 Configurable retention — the ring's size is a constructor argument and nothing in the
      settings file names it yet. A count rather than a duration, deliberately: a machine that starts
      a thousand processes a minute and one that starts none both have to stay inside the same
      memory. The heading says how many are shown against how many there have been, because a ring
      that dropped the older ones and says only "five hundred" reads as though five hundred was all
      there was

# 64. Notifications

- [x] Process started · process terminated · named process started
- [ ] 🟡 CPU / memory / disk above threshold. **Network is §18's refusal and not a gap**: nothing here
      attributes bytes to a process, so there is no per-process traffic figure to compare against a
      number, and one summed from the sockets a process happens to hold open would be a rule that
      fired on the wrong processes and stayed quiet about the right ones
- [ ] 🟡 Service stopped. **"Process became unresponsive" has no source on Linux** — there is no
      kernel notion of a process failing to answer, the Windows one is a message pump that stopped
      pumping, and the nearest thing here is `_NET_WM_PING` to each window of each process, which is
      a round trip per window per sample and answers only for processes that have windows (§39)
- ∅ Unsigned process started · reputation warning. **Both are refused rather than deferred**, and on
      the same ground §23 refuses the colours for them: nothing here establishes either. An ELF
      carries no signature, so "unsigned" on Linux would have to mean the package check — which needs
      the image read and digested for every process that starts, and answers "not packaged" for most
      of what a developer runs (§70). There is no reputation provider and none ships, so a warning
      from one could never fire; a rule that can never fire is a claim that something is being
      watched
- [x] Rules are explicit and stored locally

**Nothing fires that nobody asked for.** Every rule is off until a `notify.` line is written, an
unconfigured program interrupts nobody, and the file grows no `notify.` lines until one is set — a
monitor that decided for itself what was worth interrupting somebody about would be interrupting them
during the one hour they were using it to diagnose something. The rules are seven lines in the
settings file beside everything else §67 keeps: `notify.started`, `notify.ended`, `notify.name`,
`notify.cpu`, `notify.memory`, `notify.disk` and `notify.service`. The thresholds are absent-or-set
rather than defaulting to nought, because nought is a threshold somebody could mean, and a mistyped
number therefore leaves the rule unset instead of arming it to fire on every process on the machine.

**Edge-triggered, never level-triggered.** A process sitting above a threshold for a minute is one
thing that happened and not sixty, so the crossing fires and the process is remembered until it drops
back. Getting this wrong does not produce a slightly noisy program; it produces one that scrolls its
own status line faster than anything else on it can be read.

**A reading with no value is not a reading below the threshold.** An unpermitted or not-yet-sampled
rate neither fires a rule nor clears it, and the process keeps whatever state it had. Treating
`default(Rate)` as nought here would announce "back below" for every process on the first sample and
again for every process the sampler could not read, which is the confident-zero defect of §72.3
arriving as an interruption instead of as a cell. The same rule governs a unit whose state could not
be determined: passing through unknown is not a service stopping.

**It is the status line, in both front-ends, and deliberately not a tray or a desktop toast.** §65's
tray is unbuilt, and a program that put a system-wide notification on screen because of a rule
somebody wrote in a text file would be interrupting the whole session rather than the window they
were looking at. The window holds a notice for five samples and then drops it — one sample is a
second and nobody reads a sentence in a second, while leaving it up until something replaced it
would mean a status line still announcing a process that started twenty minutes ago, which stops
being a notification and becomes furniture.

**The service rule is the one that costs anything, and it is the one that is polled.** Reading the
service list is a walk of two unit directories and the cgroup tree, far too dear to do at the sample
rate — so it happens every eighth sample and only when a rule names a unit at all. Naming one is the
opt-in that pays for the walk (§5.4). Everything else here is free: it reads the snapshot and the
delta that were taken anyway.

The timeline of §63 is a different feature and is still unbuilt. These are transient and are not
recorded; a notification nobody was looking at is gone, which is what the timeline is for.

# 65. Tray / menu bar

- [x] 🟡 Indicators: CPU ✔ · memory ✔ · disk · network · GPU — each is the recent history of its
      resource rather than a number, because at twenty-two pixels square a number is unreadable and a
      shape is not, which is why every tray monitor ever written draws the same little graph. The
      arithmetic is in Core and returns pixels, so it is tested without a panel: an icon that is
      wrong is wrong in a way nobody reports — a smudge in the corner that somebody quietly stops
      trusting. A sample nobody could read leaves its column clear rather than drawing a bar of no
      height, and the unfilled part is transparent so the panel's own background shows through.

      **Disk, network and graphics have no reading here yet** and say so rather than drawing a flat
      line, which would read as an idle device
- [ ] 🟡 Click opens a compact popover — a click opens that resource's own page, which has the
      current values and the top processes on it. The popover itself is not written: it would be a
      third place the same figures are laid out, and the argument for it is that it is quicker than
      a window, which is worth measuring before it is worth building
- [x] Double-click opens the full application — and puts the keyboard in it, because raising a
      window and leaving the focus elsewhere is half of the gesture
- [x] Individual indicators can be enabled and disabled — `tray=` names them one at a time, in the
      order they appear in the panel, so turning one off does not mean turning the tray off. The
      default is none at all: a program that puts icons in somebody's panel without being asked has
      taken a decision about their screen that was theirs to take

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

**Where it is, and how it moves.**

- [x] **Somewhere else, on purpose** — `PROCMAN_SETTINGS` names the file outright and a
      `procman.portable` marker beside the executable keeps the file beside the executable, in that
      order under `--settings`, which beats both. The order is how deliberate each answer is, so a
      variable in a shell profile never overrules a flag somebody just typed. A portable install is
      also one that simply *has* a `settings.conf` next to the binary — the folder copied onto a
      stick with the marker since deleted must not stop being read
- [x] **`--settings-path`** — which file is in use and what put it there, which is the question
      people actually ask, because a preference that did not take is nearly always a preference set
      in the wrong file
- [x] **`--export-settings` · `--import-settings` · `--reset-settings`** — a copy out, a copy in,
      and a fresh start. The first two carry a key this build does not understand through untouched,
      for the same reason every other path here does. The third does not, and says so in its own
      output: it removes the file, which is what starting again means, and exporting first is one
      argument away
- [x] **Settable without a text editor** — View ▸ Settings…, or Ctrl+comma. The box hands back the
      record it was given with the changes written over it, so a column set somebody wrote by hand
      and a key a newer build wrote both come out of it exactly as they went in, and it writes the
      moment it is accepted rather than waiting for the tick to notice

**Nothing is in that box that does not do something.** It shows the groups below that have behaviour
behind them and deliberately not the ones that have not. A control writing a key nothing reads is
worse than no control: it tells the person who set it that they have changed something. Symbols,
reputation, history retention, privacy and the advanced group are absent from it for exactly that
reason, and each is marked below for what it actually is — refused, or waiting on the feature it
would configure — rather than left looking half-built.

**It saves itself.** The window used to require `--save-settings` from a terminal, which meant every
preference set through the window was gone by the next start. The saver runs from the sample tick
rather than from each change, so a window being dragged is one write and not a hundred, and it
renders the settings and compares them with what was last written, so a tick that changed nothing
touches no disk at all. A write that fails is retried on the next tick and never reported: somebody
diagnosing a machine whose disk is full is exactly the person who must not be interrupted by a dialog
about a preferences file (§81).

- [ ] 🟡 **General** — `confirm.destructive` decides whether a single-process action asks first, and
      the lower pane and the unavailable-tab behaviour are remembered. Launch behaviour, start
      minimised, start at login, the default page and auto-elevation are not here, because none of
      them is a thing this program does yet. **The bulk terminate asks whatever the setting says**:
      "End 14 processes?" and "End Firefox?" are the same gesture and very different requests, and
      the count is the whole of what that confirmation is for (§90)
- [ ] 🟡 **Appearance** — every colour of §7.1's categories and of the plots is a `color.<name>` line,
      the window's size and splitter are remembered, and the performance page's density now is too;
      theme, font, icon size and row height are not. Theme is not ours to keep — the window follows
      the desktop's, which is what `ITheme` is for — and the other three wait on §74's scaling
- [x] **Refresh** — the interval persists, and the window writes it back when it is changed
- [x] **Processes** — tree-or-flat, the sort column and its direction all persist
- [x] **Columns** — saved column sets, and the columns each front-end opens with
- ∅ **Symbols** — enable resolution · search paths · cache directory. **Refused, and not for want
      of symbols**: §30 resolves them, a kernel frame arriving already named by `kallsyms` and a user
      frame looked up in the image's own `.symtab` or `.dynsym`. All three of these controls belong
      to a symbol *server* — a switch to turn the lookup on, the paths to search, somewhere to cache
      what came back — and there is no server to ask. §97 promises that nothing about an executable
      leaves this machine unasked, so there will not be one here by default. Resolution is already
      asked for per stack rather than once for all of them, because it is the expensive half and §5.4
      says the expensive half is asked for: "View stack…" and "View stack with symbols…" are two menu
      items, which is a finer control than a tickbox in a preferences page could be
- [ ] **Reputation** — disabled by default · provider configuration · privacy disclosure. Deferred
      rather than refused, and waiting on what §70's last box waits on: there is no provider to
      configure. A switch reading "reputation: disabled" over nothing at all is the one control that
      would lie twice — once about the feature existing, and once about having turned it off
- [ ] **History** — enable persistence · retention · storage size. Waiting on §44 and on §85's
      "persistent history, separate and optional", neither of which exists: the ring buffers are in
      memory and go when the program does, so there is nothing yet to retain, to size, or to switch
      off. §44's own rule is that a file recording what somebody ran must not appear without being
      asked for, and these three controls are that asking — so they arrive with it, not before it
- ∅ **Privacy** — telemetry · crash reporting · recent commands · saved searches. **Refused, because
      there is nothing here to control.** §97 does not say collection is off by default; it says the
      program contains no network client at all, keeps no command history and saves no searches. A
      page of switches reading "telemetry: off" tells whoever found it that there is telemetry, which
      is worse than saying nothing and is the reverse of what they opened that page to find out
- [x] **TUI** — key bindings are a `keys.conf` beside the settings file (§57.3), the mouse is
      `tui.mouse`, how a history column is drawn is `tui.graphs` — blocks, braille, punctuation, the
      figures, or `auto` to let the terminal decide from what it can draw — and the colours are
      `tui.color.<name>`. `GraphStyle` moved into Core to make the first of those possible and the
      list of appearance names lives there for the same reason: the file has to tell a name it does
      not know from a name a newer build added, and it cannot do that from inside a front-end Core
      does not reference. Saying nothing and saying `auto` are the same thing, and the older
      `blocks=false` still means punctuation. The picker in the settings box replaced a tickbox that
      could only ever say "these two or those two", which left braille — twice the samples in the
      same width — reachable from a flag and from nowhere a person would look.
      **The colours name appearances rather than meanings**, and there are ten of them because ten is
      what a cell's attribute byte can say. The window's `color.` lines name kinds of process and
      each gets an ink of its own; in the terminal half a dozen meanings share every one of these, so
      a colour per meaning would be a promise the renderer cannot keep. `<name>` is the ink and
      `<name>.bg` the ground, and naming either replaces that appearance outright rather than tinting
      the built-in — a header that kept a cyan bar nobody asked for is not what choosing a colour
      means, and tinting would leave no way to say "no bar" at all. A terminal that cannot show the
      figure written is given the nearest it has rather than the built-in it was told to replace,
      which is the one outcome that would read as the line having been ignored; a terminal with no
      colour keeps its reverse video and its bold, because there is no escape to put a colour in and
      everything it draws already carries its meaning in a glyph as well (§57.4)
- ∅ **Advanced** — expensive collectors · debugging functionality · plugins · experimental APIs. The
      first is refused and the other three have nothing behind them. Expensive collection is already
      opt-in and the opt-in is naming the column (§5.4); a switch beside that would only be a way to
      get an empty column by forgetting it, which is the failure the inference was written to stop.
      Debugging functionality and experimental APIs are not things this program has. Plugins are
      §79's box, and a second one here would count one unbuilt thing twice

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

Every mutation has a class, and the class decides what it takes to do it. `ActionClass` and
`ActionSafety.MustAsk` are in Core beside `IProcessActions`, so the window and the terminal cannot
come to different answers about the same request (§5.1, §58). The class is a property of the
*request* rather than of the method carrying it out — `SIGCONT` and `SIGKILL` are one method and two
very different things to have done — and `ActionClass.Unclassified` is nought and is confirmed like
class 3, because the request nobody sorted is the one nobody thought about (§72.3).

- [x] **Class 0 — read-only.** Copy, search, properties, reveal path, export. No confirmation, and
      none for a system target either: copying a row out of `init` is still copying a row
- [x] **Class 1 — reversible or low impact.** Change priority, change affinity, change I/O priority,
      move between the ordinary scheduler classes, suspend, resume, start a service, reload one's
      configuration. `confirm.destructive` decides for all of these, and switching it off switches
      them off — which is the whole point of a class undone by the item beside it. §69 makes the
      confirmation optional, and three class 1 actions decline even that: **end task**, because the
      thing being asked is the program itself and an editor with unsaved work does its own asking
      (§25.1); **thaw a cgroup**, because it is the reversal of the freeze and costs nothing; and
      **putting a login entry back**, because the entry beside it takes it out again. All three are
      the reversal half of a pair, and a prompt on the way back teaches people to dismiss prompts
- [x] **Class 2 — potential data loss.** Terminate, restart, stop or restart a service, take a login
      entry out of the next boot. The setting decides — **except against a system target, where it
      asks whatever the setting says.** That switch is turned off by people who end their own editors
      all day, and not by people who meant to stop `systemd`. A system
      target is root's, or one of the four lowest pids, read off the snapshot rather than guessed; it
      under-reports rather than over-reports; and the confirmation says *which kind* of process it
      is, because a reader can check "this belongs to root" against the user column and cannot check
      "this is critical" against anything. **A bulk action is class 2 and never skips**, whatever the
      setting says and whoever owns the rows: ending a tree and ending the ticked rows both ask,
      because there the count is the whole of what the confirmation is for (§67, §90)
- [x] **Class 3 — expert / unsafe.** Any signal from the chooser, either real-time scheduling class,
      freezing a whole cgroup. **Always warned, and no preference switches it off.** Two of these
      were wrong until this section was checked against the code rather than against itself: the
      window sent an arbitrary signal under the class 2 rule, so `confirm.destructive=false` sent
      `SIGKILL` to a daemon with nothing said, and the terminal moved a process to `SCHED_FIFO` on
      one keystroke while ending it took a confirmation
- [x] **System-critical process actions display additional warnings, and are blocked where the
      operating system itself prohibits them.** The warning is the sentence named under class 2. The
      blocking is `SIGKILL` and `SIGSTOP` to pid 1 and to kernel threads, which are refused rather
      than sent

**The four things §69 names for class 3 are all refused elsewhere, and the class is not empty because
of it.** Closing a foreign process's descriptor (§32), terminating one thread (§29), unloading a
module from another process (§25.6) and writing to another process's memory (§25.5) are each ∅ with
their own argument, and none of them exists here to be classified. What is left in the class is what
this program *can* do that belongs in it — an arbitrary signal, a real-time class, a cgroup freeze —
and until now two of those three were going unwarned.

**Refusing what the kernel would discard is not caution; it is the rule `kill -0` already follows
here.** `kill(1, SIGKILL)` returns nought and does nothing: the kernel delivers pid 1 only the
signals init installed a handler for, and `SIGKILL` and `SIGSTOP` are the two no process may ever
have one for. A kernel thread never returns to user space, so it never looks at a pending signal at
all, and even `SIGKILL` to one is a successful call with no effect. In both cases the person who
asked would be told the action worked — which is exactly why signal nought is already refused rather
than sent as an existence test (§25.1). Only those two signals and only those two kinds of target are
refused, because a refusal is this program declining what was asked and it may only decline where the
kernel's behaviour is certain rather than likely: `SIGTERM` to pid 1 *is* delivered, `systemd` handles
it, and what it does with it is between the sender and the manual page.

The wording of a confirmation is §90's business. The class decides only whether one appears.

# 70. Reputation and signature verification

- [x] The program distinguishes, and never conflates: hash calculation · local signature verification ·
      trust-chain verification · online reputation query · file submission

Status vocabulary — exactly these, no synonyms:

- [x] Verified — the image still matches the digest its package recorded
- [ ] Valid but untrusted chain — a package database records that something signed, not who, so
      this needs a real chain and Linux packaging does not offer one
- [x] Unsigned — nothing signed for the package, which is most of a machine that builds its own
- [x] Invalid signature — the image no longer matches the digest its package recorded
- [ ] Revoked — needs a revocation list, which no package database keeps
- [ ] Expired — needs a certificate with a validity period, which a package signature is not
- [x] Verification error — the databases could not be read
- [x] Not checked — nobody asked; verification is opt-in and costs a read of the file

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

- [x] Full keyboard operation — every menu item, the sort, the columns, the filter and the
      settings box have chords, and the two parts of the window that are *not* menu items — the row
      tick and the splitter — are worked from the control that owns them: Space over the row under
      the cursor, and the arrow keys on a splitter that has the focus. Both mappings were already in
      the toolkit, and neither was reachable here for want of the same three things — the check boxes
      switched on, the control left in the tab order, and no accelerator claiming the key. The last
      is the one that would have been invisible: the form runs its menu shortcuts *before* the
      focused control sees a keystroke, so a chord on Space would have taken the tick away again
      while everything else still looked right. A test holds all three. The tick also says how many
      rows are ticked now — the menu's three bulk verbs each wrote that line and the check box itself
      wrote none, which made the gesture somebody tries first the one that looked like it had failed
- [x] Logical tab order — the reading order, which is *not* the order the children were added in:
      the toolkit docks by walking its children backwards, so the strip added last is the one at the
      top of the window and insertion order is very nearly the reverse of reading order. A test holds
      the numbers to the reading
- [ ] 🟡 Screen-reader labels — every container, graph, strip and field in the main window names
      itself and says what it is, and so now does every textless control of the lower pane, the
      properties window and the performance window — thirteen lists, nine plots, a second rail and a
      composition bar that the main window's own naming never reached. Three of those fixes are
      structural rather than one-by-one, because a list of controls written out by hand goes stale
      the next time somebody adds one: a plot takes its name from the caption it paints, a tab's
      content takes the tab's title, and a record table is told what it is a list of when it is
      built. A sweep of the real control tree of all four windows fails on a new textless control, and
      a second test refuses a name that merely repeats the role. **The rows do not name themselves.**
      The toolkit has no per-item accessibility — no object per row, no way to say "firefox, 4 %
      CPU" as the selection moves — so a reader lands
      on the process list, is told it is a tree, and finds nothing inside it. That is a gap in
      NativeForms rather than here, and this box stays open until it is closed there
- ∅ Scalable text — **a refusal rather than an omission, and it is still one.** Scaling only this
      program's layout constants gives tall rows with small text: every painter in the window, and
      every one inside the toolkit that draws a header or a cell, takes its font from
      `ITheme.DefaultFont`, and that font is the desktop's rather than anything this program picks.
      `Control.LogicalToDevice` exists and is called in two places in the whole toolkit — a tool tip
      and a tool strip — so the geometry everywhere else is device pixels by construction. Half of
      this therefore belongs in NativeForms, and a `ui.scale` here that moved the boxes and not the
      letters would look like progress and photograph as a defect
- [x] High contrast — `ITheme.IsHighContrast` is populated by all three backends and was read by
      nothing here. Under that scheme no row wash and no cell mark is painted: each is a third colour
      laid between a foreground and background the theme promised would be readable, and every state
      they name is also a column. The match highlight is the exception, because a matched run has no
      other carrier in the cell, and it comes from the theme's own selection colour. The graticule
      comes up from a faint green to a visible one and the axis labels lose their drop shadow — a
      second colour a pixel from the first is the same thing the scheme exists to stop
- [ ] System text scaling — blocked upstream, and worth naming precisely: `GtkBackend` writes
      `gtk-xft-dpi` back to a 96-DPI baseline on start-up, deliberately, so that native widgets match
      owner-drawn text. That is the right call for a toolkit whose geometry is device pixels, and its
      effect is that the desktop's text-scaling factor never reaches this program at all. Nothing
      here can honour a number it is never told
- [ ] 🟡 Non-colour status indicators — **downgraded from a tick, on the evidence.** Every *row*
      colour is also a column, and since this was written, one column in particular: `category` calls
      the very classifier the palette calls, so the words and the wash cannot disagree — yours, the
      system's, elevated, a service, suspended, a zombie, newly started, packaged, running a managed
      runtime. It costs nothing to read, so it may sit in a default layout. A marked cell keeps its
      number. The core heat map is not: sixty-four cells on a green-amber-red
      ramp with not a digit anywhere on them, which is a reading available to nobody who cannot
      separate those hues. It now has a textual summary a screen reader is given, and that is not the
      same as a visible one — see §45.9's colour-blind row, which is the box this actually belongs to
- [x] Graphs expose textual summaries — current, minimum, maximum and average over the drawn span,
      refreshed on the sample tick. Visible on the performance page's inspection view; announced,
      rather than drawn, for the two plots and the meter strip in the main window

TUI:

- [x] Works without colour
- [x] Never conveys state solely by colour
- [x] ASCII fallback — and the way to check it is `--ascii`, not a hostile locale. `--capture-frame`
      pins the block ramp on purpose, because a golden frame is compared byte for byte and may not
      depend on the capturing machine's `LANG`; the interactive path is the one that reads the locale
- ∅ Conventional terminal screen readers — refused in this shape, and not something an accessible
      name could reach anyway: a full-screen alternate-buffer program that repaints a grid once a
      second is exactly what a terminal screen reader has the most trouble with, and the honest
      answer is a mode that gives up the grid rather than a label stuck on one. That mode is a
      different program from the one §57 describes, so it is a thing to build beside this rather than
      a box this can ever tick

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

- [x] `procman --minimal` disables icons · signatures · symbols · module enumeration ·
      handle enumeration · history · GPU · hostname resolution · reputation · plugins — every
      opt-in switch is forced off, and which readings are expensive is read from the registry's own
      `FieldCost.High` rather than from a second list that would drift from it. A named column it
      cannot then fill is reported on standard error by name — "collects nothing that costs a read,
      so handles, pss will be empty" — rather than silently printing a column of placeholders
- [x] Minimal mode prioritises PID · process name · CPU · memory · user · state · terminate — those
      are what it opens on when no columns are named
- [x] The TUI is suitable for recovery shells and SSH sessions

The single-file AOT binary is most of the way to this already: 6.7 MB, no runtime to load, no
dependencies to be missing on a broken machine.

**The `minimal` column preset was never this, and measuring is what proved it.** `--columns @minimal`
takes 1.52–1.65 s against 1.54–1.74 s for the default listing — no difference worth the name.
Choosing fewer columns changes what is *printed*; the collectors that cost the time are chosen by
§5.4's opt-in switches, and the preset touches none of them.

**The flag does.** Asking for four expensive columns over 1,206 processes: 1.93 s without it, 1.23 s
with — and a second of each of those is the deliberate wait between the two samples a rate needs, so
the work itself went from about 930 ms to about 230 ms. Measured twice, on two different machines'
worth of load, by two people who wrote it and checked it separately.

---

# 82. Process aggregation

- [ ] Collapsed tree nodes may show aggregated CPU · resident memory · private memory · disk I/O ·
      network I/O · GPU · process count
- [ ] **Aggregated values are visibly marked as aggregates**, never presented as the parent's own usage

# 83. Process grouping

Grouping is one setting rather than a flag beside the tree: a list is nested by parentage, or headed
by user, or neither, and it can never be two of those at once. Picking one is `G` in the terminal and
View ▸ Group by in the window, and `--group` takes the same words the settings file does.

**A heading is not a process.** It is not counted in "N of M processes", it cannot be selected, and
no action can be aimed at it — its row carries no process at all, so ending one is impossible rather
than discouraged. Folding a heading hides its members and keeps its count, because the count is a
fact about the machine and not about what is on screen.

The headings come out in the order the current sort put their first row in, so a table sorted by
memory puts the heaviest group at the top. Ordering them alphabetically would bury the group somebody
sorted the table to find.

- [x] none
- [x] parent tree
- [ ] application — there is no notion of an application to read off a process. Task Manager gets
      one from shell activation; nothing here does, and a heading that guessed would not be true
- [x] executable
- [x] user
- [x] session
- [x] service — the innermost systemd unit in the cgroup path, for the reason `CgroupUnit` gives:
      naming the slice above it would report every program a user started as their session manager's
- [x] container
- [x] cgroup
- [ ] 🟡 package — off the same reader §14's package column uses, so a heading and that column can
      never disagree. `--group package` and a saved `grouping=package` collect it and work; switching
      to it in a running session heads every row "package not looked up" instead, because reading a
      package costs a database lookup per image and the probe's expensive readings are chosen when it
      is built (§5.4). Honest, and half a feature
- [ ] publisher — needs the signature verification of §70, which is not built
- [ ] Aggregations follow canonical query rules — a heading's key is read through the same accessor
      the columns and the filters use, so that half holds; but the aggregates themselves are §82's
      CPU, memory and I/O sums, and none of those exists yet. A heading counts its members and
      claims nothing else

# 84. User-defined alerts

- [ ] Rule form: `process.name == "myservice" AND process.cpu.usage > 80% for 30s`
- [ ] Conditions: greater/less than · equals · contains · regex · appears · disappears · state changes
- [ ] Actions: visual notification · OS notification · log event
- [ ] Automatic process termination from alert rules is **not** enabled in baseline releases

# 85. Historical charts

- [x] Every eligible metric has an in-memory ring buffer
- [x] 60 seconds at high resolution by default
- [x] 🟡 Configurable 5 min · 15 min · 1 h — five and fifteen minutes on both the machine's pages
      and a process's own; an hour on a process's own page only. The machine's pages stop at fifteen
      because their rings are sized to the longest span the drop-down offers, and a span that can be
      selected with no history behind it is a menu entry that draws an empty graph. Going to an hour
      there means four times the machine-wide history for every resource on the rail, which is a
      trade worth making deliberately rather than because a box wanted ticking
- [x] Persistent history, separate and optional — §44's usage record: a separate file, off unless
      asked for, holding totals per program rather than a series per metric. Deliberately not the
      ring buffers above written to disk: sixty seconds at high resolution is a thing to look at
      while it happens, and keeping every sample of every metric for every process would be a
      database rather than a file somebody can read
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

What the tools this one is meant to replace can do, and whether it can. Ticked means a person can
reach it and it gives a true answer; 🟡 means part of it is there and the rest is named after it.
Several capabilities exist only on the command line so far — that is written down here rather than
hidden behind a tick, because a feature nobody can reach from the window is not parity with a tool
that has a tab for it. That drift is what made an earlier version of this matrix wrong.

## Windows Task Manager

- [x] Processes
- [x] Performance — twenty-two resources on the rail, each with its own graphs and figures
- [x] App / usage history — a per-application total kept across sessions: processor time, bytes read
      and written, peak and time-weighted average memory, launch count, cumulative runtime, last
      launch and the date the record began. Off unless asked for, which is §44's design rather than a
      preference about it. Foreground and background time are not separated, because nothing in the
      Linux kernel knows which window has focus and the desktop that does knows nothing about
      processor time; network bytes are refused for §18's reasons
- [x] Startup — a window view listing every entry and which will run, and a right-click that turns
      one on or off
- [x] Users — a window view of who is logged in and what their processes cost
- [x] Details
- [x] Services — a window view of every unit and its state, and a right-click that starts, stops,
      restarts, reloads, enables or disables one
- [ ] 🟡 Run new task — `--run` starts a program with a directory, environment and priority; no dialog
- [x] End task
- [ ] Restart supported applications
- [x] Priority
- [x] Affinity
- ∅ Efficiency / QoS — refused rather than unwritten: the nearest Linux relatives are the scheduling class and the I/O class, both already settable, and mapping a QoS class onto them is the false equivalence §5.3 forbids
- [ ] Dumps
- [ ] 🟡 Wait chains — each thread says what it is blocked in and which syscall it is in, and the
      chain is followed for the one case a kernel states outright: a process queued behind a file
      lock names the process holding it, on the properties window's General page. The other cases
      are not followed and cannot be. Nothing publishes who holds a futex, and reconstructing it from
      outside needs the debugger interface §4 rules out — so a general "wait chain" here would be
      this one special case wearing a general name, which is what §5.3 forbids
- [x] Process search / filter
- [x] Startup enable / disable
- [ ] User session control
- [x] Service controls
- [x] Kernel vs user CPU graph

## Process Explorer

- [x] Hierarchical process tree
- [x] Process ownership / account
- [x] Highly configurable columns
- [x] Process properties window
- [x] Lower pane
- [x] Handles
- [x] DLLs / mapped files
- [x] Search for resource owners
- [x] Signature verification — an ELF carries no Authenticode, so the honest equivalent is what the
      package manager knows: whether the image still matches the digest recorded for it, and
      whether anything signed for the package. Both are read, and both agree with pacman
- [x] Image metadata — architecture, word size, position independence, interpreter, build id,
      mitigations, size and mtime on both. On Windows the version resource itself: description,
      company, product, product version and file version, matching what the shell's own property
      sheet shows. On Linux the version comes from the package instead, because an ELF carries no
      version resource to read — two different sources for the same question, each named as itself
- [x] CPU / memory / I/O histories
- [x] Per-process inspection

## System Informer / Process Hacker lineage

- [x] Detailed process tree
- [x] Resource highlighting
- [x] System graphs — one page per processor, disk, adapter, battery and sensor chip
- [x] Handles
- [x] Modules
- [x] Threads
- [ ] 🟡 Stack traces — the kernel stack where the machine permits it, with symbols where the image still carries them; a user-space walk needs the driver §4 rules out
- [x] Services — listed in a view, and started, stopped, restarted, reloaded, enabled or disabled
      from the window's Services page, from the terminal's action menu, and from `--service`
- [x] Network connections
- [x] Disk activity — per-process I/O columns, and a page per disk with its own throughput and queue
- [x] GPU data — the adapter's own figures, and per process the memory and the time on each engine
- [x] Memory maps
- [x] Environment
- [x] Security / token information — all five capability sets by name, both id quartets, seccomp mode and filter count, no-new-privs and the LSM label
- [x] File / resource ownership search
- [x] Advanced process scheduling
- [x] Detailed service control — all six verbs systemd offers, in all three front-ends, each
      carrying the manager's own answer rather than a word of ours. Pause and continue are not
      offered because systemd has no such thing
- [ ] 🟡 Binary inspection — the ELF header and the mapped images; nothing disassembles
- [ ] 🟡 Runtime inspection — each process and each loaded image says whether it is native, .NET,
      a JVM or Python, read from the module list rather than guessed from a name; nothing
      inspects a running runtime's own structures
- [ ] Process notes / rules
- [x] Configurable columns

## DBC Task Manager

- [x] Clear simple process page
- [x] Attractive resource graphs — a graticule, a filled area, a selectable time axis and a cursor that reads a sample
- [x] Resource selector
- [x] CPU · memory · disks · networks pages
- [x] Services — listed in a view of its own, and controlled from it
- [x] Startup — listed in a view of its own, and enabled or disabled from it
- [x] Users
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

Every one of these is a named set reachable from all three front-ends — `--columns @basic` on the
command line, Columns ▸ Column sets in the window, `c` in the terminal — and none of them is written
into anybody's settings file, so a preset that gets improved improves everywhere. Naming a set is
also what pays for the expensive readings in it: a column nobody named collects nothing (§5.4).

**Processes — Basic:** name · PID · status · CPU · memory · disk · network · GPU

- [ ] 🟡 Available except network, which is §18's refusal rather than a gap. Nothing here attributes
      bytes to a process, and the four endpoint counts that *are* readable are not throughput and are
      not offered under a header that would say they were (§5.3). The graphics share is in the set:
      §19 reads it, it costs a descriptor read per process, and naming this set is the asking

**Processes — Expert:** process · PID · PPID · CPU · private memory · working set · I/O rate · user ·
start time · command line · signature

- [ ] 🟡 Available except signature, and that is a decision rather than a gap. "Signature" is two
      columns and not one — a PE carries one the publisher put inside the file and an ELF does not,
      so what signs a Linux program is the package database that recorded its digest (§5.3, §21,
      §70). The pair is fifty characters wide, one half of it reads `n/a` on whichever platform this
      is, and filling either means reading and digesting every image on the machine. This is the set
      somebody names to get a broad everyday table, and eleven columns of it fit a terminal eighty
      wide; the verdict columns are in the two sets below, which exist to pay for them

**Security:** name · PID · user · path · signer · signature · integrity/security context · elevated ·
protection · hash · reputation

- [x] Available. The claim that the missing half "belongs to §70's code signing, which has no Linux
      counterpart" was wrong about four of the five: `hash.sha256` is read on Linux, `reputation` is
      a column on every platform precisely so that a digest computed here can never be mistaken for a
      file submitted from here, `package.status` is what signs a Linux program, and `trust.chain` is
      what the packaging system recorded about who validated it. So §70's five questions are five
      columns here and none is read off another — a package built on this machine matches its own
      record exactly and carries nobody's signature, which is Verified in one column and Unsigned in
      the next. `signer`, `signature`, `protected` and `protection.level` are Windows' and say `n/a`
      here; `confinement` is the Linux side of the same question and says `n/a` there

**I/O:** name · PID · read rate · write rate · read bytes · write bytes · other rate · I/O priority

- [ ] 🟡 Available except the two cumulative byte totals and the other-operations rate. I/O priority
      *is* in the set — it was missing rather than unavailable, and costs one `ioprio_get` per
      process per sample, which naming this set pays for. The three that are absent are absent from
      the field catalogue rather than from this set: `ProcessRecord` carries `ReadBytes`,
      `WriteBytes` and `OtherBytes` and the sampler fills them, but the only columns built on them
      are the rates. **§17 ticks `io.read.bytes` and `io.write.bytes` and there are no such
      columns** — that is §17's box to correct, not this one's

**Network:** name · PID · send · receive · connections · listening ports

- [ ] 🟡 Available except send and receive, which §18 refuses. There is a `network` set now — it was
      not that the columns did not exist, it was that nobody had named a set to put them in: TCP
      connections, UDP sockets, listening sockets and distinct peers, all four `High` because the
      join from a socket to a process is a `readlink` per open descriptor on the machine. The set
      counts endpoints and never invents traffic, which is asserted rather than left to review

**Full forensic:** the expert set, plus effective user · privilege change · capabilities ·
security context · seccomp · no-new-privileges · tracer · read rate · write rate · descriptors ·
package · SHA-256

- [x] Available. The dearest set in the file by a wide margin, and the only one where that is the
      point: the package, the digest and the descriptor count each cost a reading the sampler does
      not otherwise take. It is twenty-five columns, which is wider than any terminal — the pinned
      run holds the name still and the rest is reached by scrolling sideways

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

**2156 tests pass on every leg, under both a UTF-8 and a `C` locale.**

## Unit tests

- [x] Counter calculations
- [x] Delta handling
- [x] Unit formatting
- [x] PID reuse
- [x] Field registry — 14 tests, including the one that enforces §103
- [x] Filters
- [x] Sorting
- [x] Export schemas

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
- [x] Affinity — set on a real process and held against the kernel's Cpus_allowed_list
- [x] Services — against systemctl's own loaded and installed lists
- [x] Startup — every entry traced back to a desktop file that is really there
- [x] Network
- [x] Modules
- [x] Descriptors
- [x] Privilege errors

## Performance tests

- [x] Sampling budget enforced as a build gate (§71.2)
- [x] 10 000 processes
- [x] 100 000 threads — as records and not as a recorded tree. The probe reads five files per thread,
      so a fixture at this size would be half a million files in the repository and would measure the
      filesystem the tests happen to run on. The layer that has to survive the size is the one above
      it: §29 re-reads the thread tab on *every* tick while it is open, so `ThreadDelta` does a
      hundred thousand keyed lookups a second on such a machine. Measured at 22 ms against 2.5 ms for
      ten thousand — linear — allocating nothing per tick once its buffers are grown, and holding
      exactly one generation of history after twelve rounds of a hundred thousand entirely new
      threads. That last one is counted rather than weighed: asking the garbage collector reads the
      large-object heap's refusal to compact discarded records as a leak
- [x] 1 000 000 resource rows
- [x] Rapid process churn
- [x] 100 % CPU load — a sample taken while every core is saturated is still a whole sample
- [x] Low-memory state — free, available and total stay consistent and stay distinct
- [x] High-I/O machine — the byte counters only ever go forwards, checked across real disk work

## UI tests

- [x] Sorting while data changes — three tests, added after the reordering bug in §12
- [x] Tree expansion
- [x] Selected-process termination — the menu item worked over the recorded machine, not the action
      layer under it. What was untested was everything between: that the prompt names the process,
      its pid and what is lost rather than asking "are you sure"; that answering **no** ends nothing,
      a branch that had been written and never run; that **End task** asks the program and not the
      user, because it is the reversible one; that ending a tree counts what goes with it; that a
      refusal from the kernel is put in front of somebody rather than swallowed; and that with no row
      selected nothing is asked and nothing happens. It needed a seam: every prompt went straight to
      a static dialog that throws without a display, so `MainWindow.Confirm` and `Announce` are now
      properties whose defaults are that dialog
- [x] Lower pane — that it opens showing, that hiding and showing it again is a no-op, that whether
      it was showing survives a save and a load, and that every one of its six tabs fills for the
      selected process with every cell of every row drawn. The last is the one that matters: the
      tabs read their cells out of a row's tag array by index, so a row shorter than the list has
      columns throws while painting rather than while testing
- [x] 🟡 Column sets — the sets were parsed, saved, carried through untouched and reachable from the
      terminal alone, so the window had no way to apply one. It has a **Column sets** submenu now,
      offering §94's presets and anything the file names, with a saved set shadowing a preset of the
      same name. The pinned run is kept and clamped rather than dropped: somebody who pinned the name
      column wants it pinned whatever else the table shows. Amber because there is still no way to
      *save* the current columns as a set from either front-end — a set is written by hand into the
      file
- [x] Accessibility — a sweep of the real control tree of all four windows rather than a checklist
      written out by hand, so a control added tomorrow and left unnamed fails it. It found every
      table, every plot, both rails, the filter box, the composition bar and every strip unnamed (§74)
- [x] 🟡 Keyboard operation — the window's twenty-two accelerators, read for the first time. The
      terminal's bindings have been under test since they were written; the window's were lines in a
      builder that compiled whatever it was given, so two items claiming one chord — of which one
      silently never fires, and which one depends on the order they were added in — was a thing that
      could be written, reviewed and shipped. Now: no chord claimed twice, every chord on an item
      that does something rather than on a submenu header, no chord a bare letter or digit (the
      filter box is where every keystroke is meant to be text), nothing destructive reachable by a
      keystroke, and the nine §74 names by chord rather than by label, so an item renamed keeps
      passing and an item whose chord was dropped does not. Amber because this is the inventory and
      its rules: working an item needs a confirmation dialog and there is no display to open one on,
      and tab order — §74's other open box — is not asserted at all

## TUI tests

- [x] Golden-frame comparison at fixed dimensions
- [x] 80×24 · 120×30 · 160×50 — three goldens, because §57.1's breakpoints make these three different
      layouts rather than one layout scaled. The unit tests compared all three and the CI job
      compared only 120×30, so a change that took the narrow and the wide frames with it would have
      been caught by a developer's own `dotnet test` and not by the gate; the job now walks all three
- [x] 256-colour — every slot of the palette rather than one of them. The old test wrote a single
      accented cell and looked for `38;5;`, which would pass on a table whose other nine slots had
      been left identical. Now: every slot defined and *distinct* at each depth, so two meanings
      cannot paint the same; no palette speaking another's language, because the 256-colour and
      24-bit tables sit one under the other and are edited by copying a line across; every one of the
      256 attribute bytes painting something, because the flags are a bitfield and the palettes are
      arrays; a whole frame at 256 colours carrying the escapes; and the same frame's characters
      identical to the monochrome one, since colour is a plane beside the text and must not move a
      cell. `DetectColorDepth` is read too — the only place the choice is actually made, and until
      now nothing tested it, so a 256-colour terminal getting sixteen was invisible
- [x] Monochrome
- [x] UTF-8
- [x] ASCII fallback

## Self-test

- [x] `procman --self-test` — 37 checks against live data, cross-validated against the .NET runtime's
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
- [x] Flat and grouped modes — by user, session, service, executable, container, cgroup or package
- [x] Terminate
- [x] Suspend / resume
- [x] Priority
- [x] Affinity — from the window; there is no command-line switch for it
- [x] Basic Performance page
- [x] Network view — every socket with its owner, queues, round-trip time and retransmits
- [ ] 🟡 Services read and control — read in a window view, controlled from `--service`; no menu item
- [ ] 🟡 Startup
- [x] Users / sessions — a window view and `--users`
- [x] GUI
- [x] TUI
- [x] CLI — list, find, kill, run, host, limits, startup, users, services, service control and
      connections, with exit codes a script can branch on
- [x] Column registry — 118 fields, each sortable, filterable and exportable by one key
- [x] Filters and search — comparisons, booleans, regular expressions and unit-aware sizes over
      every field, plus the search for what holds a file
- [x] Exports — text, csv, tsv, json, jsonl and markdown, all six exercised

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
- [x] Process trees, handles, modules and resource-owner search remove routine need for
      Process Explorer — §91's Process Explorer row is twelve of twelve, including the two that took
      the longest to answer honestly: signature verification, where an ELF carries no Authenticode
      and the local equivalent is what the package manager recorded, and image metadata, where the
      version resource is read on Windows and the package is asked on Linux because an ELF has no
      version resource — two different sources for one question, each named as itself rather than
      pretending to be the other
- [ ] Advanced process, thread, security, network, service and memory inspection removes routine
      need for System Informer
- [ ] Performance dashboards are at least as approachable as DBC's or modern Task Manager's
- [x] GUI and TUI expose the same canonical information
- [x] Every unsupported platform-specific feature explicitly communicates why — **checked over the
      registry rather than field by field**, which is how it kept going wrong: `app.name` read "none"
      on Windows, which is a Linux finding meaning the machine has no desktop entry for the program;
      `runtime` rendered an empty placeholder; two counters read as a confident nought. Every one of
      those sat inside a box that was already ticked. Now every field the registry declares as
      belonging to another platform is sampled here and has to come back as one of the eight marks
      that mean "no answer" — never a number, never a name, and never an empty cell, because a reader
      cannot tell "nothing to report" from "nobody asked" and one of those is a finding. Forty-one
      Windows-only fields are covered, and the assertion was mutated to confirm it sees all forty-one
      rather than passing over an empty loop. The same fields export as nothing rather than as a mark
      meant for a person, and no mark parses as a number or is wide enough to be mistaken for one
- [x] Common actions work without running the whole program elevated
- [x] Recovery/minimal mode remains functional under significant load — **measured, and the
      measurement is what produced the mode.** On sixteen cores at load 12.5 with 1,144 processes a
      full listing returns every row in 1.54–1.74 s, of which a second is the deliberate wait between
      the two samples a rate needs: 540–740 ms of work against 150 ms on an idle machine with a third
      of the processes. The forensic preset — every expensive reading at once — costs 2.46 s, which
      is the §5.4 opt-in trade with a number against it. Peak resident for that heaviest case was
      117 MiB, and that is the framework build rather than the single-file one that ships.

      What the measurement found was that the `minimal` column *preset* saved nothing, because
      choosing fewer columns does not turn a collector off. §81's `procman --minimal` now does: four
      expensive columns over 1,206 processes take 1.93 s without it and 1.23 s with, and once the
      inter-sample second is taken off both, the work went from about 930 ms to about 230 ms.

- [ ] The product is stable enough that administrators trust it while diagnosing an already
      unstable machine — no soak has been run, and one run of anything is not the evidence this asks
      for. What is known: under the load above it returned all 1,144 rows on every attempt with no
      dropped or duplicated process, and the row count tracked the machine's own `/proc` exactly.
      Trust is a longer-run claim than that and this stays open until something has run for days

# 102. Acceptance criteria for v1

v1 does not ship unless every one of these is true:

- [x] The process list updates continuously without losing selection
- [x] PID reuse cannot corrupt process identity
- [x] GUI and TUI report matching canonical counters within the sampling tolerance
- [x] CPU and memory metrics have documented semantics — and the documentation is held true by
      assertion rather than by good intentions. Every field's meaning lives in the registry, which is
      what `--help-fields` prints, what the column chooser shows and what the terminal's help screen
      carries, so a sentence nobody kept true would be worse than none. Four invariants: every field
      is described; no two share a description, since two columns described identically are two
      columns one of which is mislabelled and nothing says which; a description says more than its
      own header did; and **every percentage names its denominator**, either as a number ("100% is
      one core") or in the ordinary English form ("how much of its adapter"). That last one found the
      CPU history plot saying "the last sixty seconds of processor use" and no scale at all — the one
      column where a reader cannot work the scale out from the number, because there is no number.
      Every byte-valued memory figure has to name which kind of memory it counts, which is the whole
      difference between figures several of which are legitimately larger than the machine's RAM
- [x] The user can kill a process from GUI, TUI **and** CLI
- [x] The user can suspend and resume where supported
- [x] The user can inspect process path and command line
- [x] The user can inspect the process tree
- [x] The user can inspect active network endpoints — the pane's network tab, and `--connections`
- [x] The user can inspect services — and start, stop, restart, reload, enable and disable them
      from the window, the terminal and the command line
- [x] The user can manage common startup items — every XDG autostart entry, turned on and off from
      the window's Startup page
- [x] The user can inspect logged-in sessions — a window view and `--users`
- [x] The user can view CPU, memory, disk and network performance — thirteen resource pages, each
      with its own graphs
- [ ] 🟡 The user can create and restore column presets — restoring works from the file and from
      `--columns @name`; creating one means editing the file, not a dialog
- [x] The user can search and filter by any registered field, visible or not
- [x] Tables remain usable with thousands of changing rows — ten thousand nested and flat, half the
      table replaced every sample, with the rebuild allocating nothing
- [x] Privileged actions work through the privilege broker
- [x] Lack of privileges does not crash or freeze views
- [x] The GUI exposes no data unobtainable from TUI or CLI — audited rather than asserted. Every
      view behind the rail has a verb: `--startup`, `--users`, `--services`, `--connections`,
      `--find`. Every properties page has a field, a detail tab or a verb. The one gap the audit
      found was the environment block, which the window had a page for and neither of the others
      could reach at all; `--environment PID` closes it, and its output is byte-for-byte what
      `/proc/[pid]/environ` holds
- [x] No external metadata is transmitted without opt-in
- [x] Unavailable platform fields are distinguishable from zero-valued fields — including in the
      terminal's plots, which were the one place they were not. Level nought of both ramps is a space
      and so was a gap, under a comment that said "a gap is a gap: a space, not a zero-height block
      that reads as idle" — describing an intention rather than the code beneath it. An idle process
      and one whose counter was refused drew the same plot. A gap is a middle dot now, or a question
      mark where the terminal has no block characters, and five of them appear in the golden frames
      where the fixture's plots had been quietly claiming idle
- [x] Destructive actions identify their exact target — the action, the name, the pid and the count
      of what runs underneath it
- [x] Minimal recovery mode operates independently of expensive collectors and plugins —
      `--minimal` forces every §5.4 opt-in switch off, including the two that used to bypass the
      general gate, and reads which readings count as expensive from the registry's own cost rather
      than from a list beside it. Four expensive columns over 1,206 processes: 1.93 s without it,
      1.23 s with. A named column it cannot fill is reported by name rather than printed as a column
      of placeholders

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
