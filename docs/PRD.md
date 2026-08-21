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

**Counting, as of the last update:** **799 of 1337 boxes are ticked** — 113 of 198 in the field
registry (§14–22), 686 of 1139 across the capabilities. A further 116 are marked 🟡, meaning some of
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

- [ ] 🟡 Replace everyday Task Manager workflows — process management yes; services, startup, users no
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
- [ ] 🟡 Performance — reachable from the rail, but as a window of its own rather than a page in the
      content region: it is modeless, has its own timer and its own lifetime, and a second copy of it
      in here would mean two of everything it samples (§45)
- [ ] Applications / usage history (§44)
- [x] Startup (§42)
- [x] Users / sessions (§43)
- [ ] 🟡 Services — there is a view; there are still no start or stop verbs, and the view says so
      rather than offering buttons that would only ever refuse (§41)
- [x] Network — every socket on the machine, with the process holding it where this account may see
      one; opening a row goes to that process (§40)
- [ ] System activity (§51)
- [x] Search / find resources — from the rail, which opens §33's dialog
- [ ] Logs / history (§63)
- [ ] Settings (§67)

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
- [ ] Memory mappings
- [x] Environment
- [ ] Windows
- [ ] Services
- [ ] Security
- [ ] Timeline

The lower pane is the defining Process Explorer interaction and is the highest-value single item in
this document.

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
- [ ] 🟡 Freeze / pin columns — terminal only; the first column is pinned by default and `#` moves
      the boundary. The window's list paints every column from one horizontal offset and has no
      pinned region to paint into, so this needs a seam in the toolkit rather than work here
- [x] Auto-size column / all columns — measured against the rows on screen rather than every
      process, because a column fitted to the widest value in the whole table is usually fitted to
      something scrolled out of sight
- [x] Copy cell — `y` in the terminal, over OSC 52; `Ctrl+Shift+C` in the window
- [x] Copy row — `Y` in the terminal, `Ctrl+C` in the window
- [ ] 🟡 Copy selected rows / columns — the rows are done in both front-ends: one key copies every
      ticked row with a header line. A column is not, in either — there is no cell selection to copy
      one from (§95)
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
- [x] `exe.name` — the file that is running, from `image.path`. Differs from `name` when
      a process renames itself, not yet its own field
- [ ] `app.name` — human-readable product/application identity
- [x] `pid` — process identifier
- [x] `ppid` — parent process identifier
- [x] `instance.id` — PID plus creation-time-safe unique identity (`ProcessKey`)
- [x] `parent.name` — the parent's name, resolved once per sample over the whole table
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
- [x] `arch` — from the ELF header, which is the program's answer rather than the machine's: an
      x86-64 kernel runs 32-bit binaries every day, and reporting the machine's architecture for
      every row describes the machine instead of the program. Byte order is in the header, so a
      big-endian binary decodes on a little-endian machine — the case no laptop here can produce
      and a test covers
- [ ] `emulation` — WOW64, Rosetta, translation state
- [x] `image.path` — full image path
- [x] `cmdline` — complete command invocation
- [x] 🟡 `cwd` — Linux; Windows needs a PEB read we do not do
- [ ] `description` — binary description (version resource)
- [ ] `company` — publisher metadata
- [ ] `product` — product metadata
- [ ] `product.version`
- [ ] `file.version`
- [ ] `package` — MSIX / Flatpak / Snap / `.app`
- [ ] `app.id` — platform application ID
- [ ] `bundle.id` — macOS bundle identifier
- [x] `container.id` — every runtime writes its own cgroup shape and they all bury a long
      hexadecimal id somewhere, so the id is looked for rather than the layout: there is always
      another layout. A run of hex has to be long enough to *be* an id, or a systemd slice and a
      terminal's UUID scope would both report as containers on an ordinary desktop
- [x] `namespace` — kind and inode. The inode is the identity: two processes sharing one share that
      namespace, which is how a container's members are actually told apart, rather than by a cgroup
      path anybody can write anything into
- [ ] 🟡 `job.cgroup` — Linux cgroup path done; Windows job object not
- [x] `terminal` — controlling TTY, decoded from `stat` field 7. The packing is the awkward part:
      minor is split across the low eight bits and bits 20–31 with major in between, so the obvious
      shift is right for small numbers and wrong for large ones. Zero is *no terminal* — the answer
      for every daemon, and so for most of a machine — rather than device 0:0
- [x] `exe.size` — of the resolved target, not of the `/proc` link. Asking the link its length gives
      nought, which is how this first reported every program as being no bytes long
- [x] `exe.modified`
- [ ] `exe.created`
- [ ] `subsystem` — GUI/console/native; PE only, `n/a` for ELF
- [x] `interpreter` — `PT_INTERP`, or the shebang's program for a script. A shebang is as real a way
      to start a program on Linux as an ELF header, and reporting "not an executable" for every shell
      script would be wrong about a large part of any machine.

      **No interpreter and no permission to look are different answers.** The first means statically
      linked; the second means nobody could check. Collapsing them made the report call every other
      user's process statically linked — a confident claim made out of an absence (§5.3)
- [ ] `runtime` — native/.NET/JVM/Python, from the module list

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
- [ ] `threads.peak` — **no Linux source.** The kernel keeps the current thread count in `status`
      and no high-water mark of it anywhere. The only figure this program could offer is the largest
      it happened to see while it was running, which is a fact about the observer rather than about
      the process (§9.2)
- [x] `priority.base`
- [x] 🟡 `priority.dynamic` — Linux only; `stat` field 18, the kernel's own number rather than the
      nice value
- [x] `nice` — the politeness a process was started with, backwards on purpose
- [ ] `priority.class` — idle/below normal/normal/… (`GetPriorityClass`). **Windows only.** Linux
      has no such band: nice orders tasks inside `SCHED_OTHER` and the class is `sched.class`, and
      folding either into a Windows priority class would be the false equivalence §5.3 forbids
- [x] `nice`
- [x] 🟡 `cpu.affinity` — Linux only, and opt-in. `Cpus_allowed_list` from the `status` the sampler
      already has open, in the kernel's own notation (`0-15`, `2,3`), which is what `taskset -pc`
      prints; the list rather than the mask, because on a 128-way machine the mask is unreadable.
      Not `sched_getaffinity`: that is a syscall per process for a line already in front of us.
      Windows' `GetProcessAffinityMask` is not written
- [ ] `cpu.set` — Windows CPU sets. **Windows only**; the Linux near-relative is the cgroup `cpuset`,
      which is already reflected in `cpu.affinity` — the kernel narrows `Cpus_allowed` to it
- [ ] `numa.node` — **not answerable honestly here.** `Mems_allowed_list` says which nodes a process
      may allocate from, which is a different question from which node it is running on, and this
      machine has one node — an implementation could not be told from a broken one (§9.2)
- [x] `cpu.last` — field 39, which sits behind fourteen fields nothing else reads
- [x] `sched.class` — `SCHED_OTHER`, `_FIFO`, `_RR`, `_BATCH`, `_IDLE`, `_DEADLINE`, `_EXT`, under
      the kernel's own names. From `stat` field 41 rather than `sched_getscheduler`, which would be a
      syscall per process for a number already in the line being parsed. Verified against `chrt -p`.
      A class the kernel adds later is left unknown rather than folded into the ordinary one, and a
      `stat` that stops short says nothing rather than claiming `SCHED_OTHER`
- [ ] `qos` — OS energy/performance state. **Windows and macOS only.** Linux has no per-process
      quality-of-service class; `uclamp` is a utilisation hint on a task, not a state the OS assigns,
      and reporting one as the other would invent a concept the kernel does not have
- [x] `throttled` — cgroup `cpu.stat` `nr_throttled`, opt-in. The group's counter and not the
      process's, which the column says: everything in one cgroup shows the same figure. Read once per
      cgroup per sample rather than once per process. A group whose CPU controller is off has no such
      line and reports unknown — a real nought there means "has a quota and never reached it"

Required of the CPU percentage:

- [x] Normalised 0–100 view
- [x] Logical-CPU cumulative view
- [ ] Configurable decimal precision

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
- [ ] `ws.shareable`

The three add up to the working set exactly, which is what makes them a breakdown rather than three
more numbers. All free: the lines are already in the `status` this program reads anyway.
- [x] `private.bytes` — private committed virtual memory; `PrivatePageCount` (W), `VmData` (L).
      Both mean commit charge — this was `RssAnon` on Linux until it was corrected, which made the
      same column mean two different things on two platforms
- [ ] `private.bytes.peak`
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
- [ ] 🟡 `io.priority` — `ioprio_set` is implemented and `ioprio_get` is not read into the table yet
- [x] `cpu.time.user` / `cpu.time.kernel` — the per-process split, free from `stat`'s `utime` and
      `stime`. A process that is mostly kernel time is usually waiting on something rather than
      computing, and one number covering both cannot say so
- [ ] `io.wait` — `/proc/pid/schedstat`, needs delayacct
- ∅ `disk.latency` — requires eBPF/ETW tracing; see §52

# 18. Process table — network fields

Per-process byte counters have no portable source: Linux needs packet accounting or eBPF, Windows
needs ETW. Both are opt-in subsystems and **off by default** — §5.4 forbids making the ordinary
process table depend on them.

The four counts are read. The nine traffic fields are **not**, and the reason is written out below
rather than being worked around, because the obvious workaround produces a number that is wrong in a
way nothing on screen would betray.

- [ ] `net.percent`
- [ ] `net.send.rate`
- [ ] `net.recv.rate`
- [ ] `net.rate`
- [ ] `net.sent.bytes`
- [ ] `net.recv.bytes`
- [ ] `net.errors`
- [ ] `net.packets.sent`
- [ ] `net.packets.recv`
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
      number. Same reasoning as `threads.peak` in §15
- [x] `fd.count`
- [x] `socket.count` — Linux, opt-in. One pass over the descriptor table, classified by the same
      Core function the handles view of §32 uses, so the column and the view cannot disagree about
      what a socket is. §5.4 is what resolved the objection below: naming the column is the request,
      and nothing else turns the scan on
- [x] `file.count` — as above. Descriptors on a name in the file system, directories included:
      separating those two needs the open flags out of `fdinfo`, which is a second file per
      descriptor. A device, a `memfd` and an anonymous inode are each their own kind and none of
      them is a file
- [x] `pipe.count` — as above. Both ends of one pipe are a descriptor each
- [ ] `event.count` — Windows handle-type tally. **Windows only.** Linux's nearest equivalent is an
      `eventfd`, which is a descriptor and is already counted as one
- [ ] `semaphore.count` — **Windows only**; a POSIX semaphore is a mapped file and a System V one
      belongs to no process at all, so neither is countable per process
- [ ] `mutex.count` — **Windows only**; a futex has no kernel object to count
- [ ] `section.count` — **Windows only**; the Linux equivalent is a mapping and lives in §34
- [ ] `regkey.count` — **Windows only**; there is no registry
- [ ] `user.objects` — `GetGuiResources(GR_USEROBJECTS)`. **Windows only**
- [ ] `gdi.objects` — `GetGuiResources(GR_GDIOBJECTS)`. **Windows only**
- [ ] `mach.ports` — **macOS only**
- [ ] `ipc.count` — **not answerable per process on Linux.** System V queues, semaphores and shared
      memory belong to the kernel rather than to a process: `ipcs` lists them by creator, and a
      segment stays after everything that attached it has exited. Attached shared memory is visible
      in `maps` and belongs in §34, not in a count of things a process holds

The remaining per-type tallies are one pass over a handle table that already exists — but they must
**not** move into the sample loop before that cost is measured against §71. The three that are
ticked are how that is done rather than an exception to it: the pass costs a `readlink` per
descriptor on top of the listing that was already the most expensive read in the sampler, so it
happens only for a run that named one of the three columns, and the whole table is never scanned for
a column nobody opened (§5.4).

# 21. Process table — security fields

- [x] `elevated` — the effective uid on Linux, `TokenElevation` on Windows
- [x] `integrity` — the last sub-authority of the token's mandatory label: untrusted, low, medium,
      medium+, high or system, and the raw number for anything Microsoft adds later
- [ ] `protected` — protected-process status. **Windows only**
- [ ] `protection.level` — **Windows only**
- [ ] `signature.status` — see §70's vocabulary. **Windows and macOS**: Linux binaries carry no
      embedded signature to verify. What signs a Linux program is its package, which is a different
      question with a different answer and belongs to §42's provenance rather than to this column
- [ ] `signer` — verified publisher. **Windows and macOS**
- [ ] `cert.subject` — **Windows and macOS**
- [ ] `cert.issuer` — **Windows and macOS**
- [ ] `signature.timestamp` — **Windows and macOS**
- [x] `hash.sha256` — on demand only, and on every platform that has a file to hash: the digest of
      the running image, which is neither a signature nor a verdict (§70). Hashed once per image
      rather than once per process — three hundred processes of one runtime share one binary — and
      again when that file is replaced underneath them, which is the case somebody watching this
      column is watching for. Linux fills it; the Windows probe does not call it yet and says so.
      Verified against `sha256sum`
- [x] `hash.sha1` — the same bytes under the older digest, from the same single read of them. Kept
      because so many package manifests and threat feeds are still keyed by it, and collidable since
      2017: on its own it is evidence of nothing. Verified against `sha1sum`
- [ ] `reputation` — opt-in, see §70. **Not implemented on any platform**; it is a network service
      rather than an OS reading
- [ ] `dep` — **Windows only.** Linux has NX on every mapping and no per-process policy to report
- [ ] `aslr` — **Windows only** as a per-process mitigation policy. Linux's is the machine-wide
      `kernel.randomize_va_space` plus whether the image is a PIE, which is §53's business
- [ ] `cfg` — **Windows only**
- [ ] `cet` — **Windows only** as a policy field. The CPU's own shadow-stack support is a machine
      capability and is already in §46
- [ ] `acg` — **Windows only**
- [ ] `cig` — **Windows only**
- [ ] `sandbox` — **Windows (AppContainer) and macOS (Seatbelt).** Linux has no single sandbox flag:
      what confines a process here is the seccomp mode, the LSM label and the namespace set, and all
      three are already their own fields. One "sandboxed: yes" over them would answer less than any
      of them does (§5.3)
- [ ] `appcontainer` — **Windows only**
- [ ] `capabilities` — the AppContainer capability list. **Windows only**; Linux capabilities are
      `caps.linux` below and are a different thing wearing the same word
- [x] `selinux.context` — `/proc/pid/attr/current`, opt-in
- [x] `apparmor.profile` — same file, same field: the LSM label is one value whichever module wrote it
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
      macOS probe is a stub (§6.3)

- [ ] **Online reputation checking is opt-in, and the program states exactly what is transmitted
      before the first time it happens** — at the point of use, not buried in a settings page

Everything still unticked here is **Windows-only or macOS-only**, and each line above now says
which. Protected-process status, the certificate and signature fields, the mitigation policies
(`dep`, `aslr`, `cfg`, `cet`, `acg`, `cig`), AppContainer with its capability list, and the macOS
line are not work that can be written honestly from a Linux machine: a signature verifier or a
mitigation-policy reader that nobody can run against the OS it describes is a plausible
implementation, which is worse than an empty one (§9.2). Reputation is the one exception to the
pattern — it is a network service rather than an OS reading, and it is unbuilt everywhere.

The hashes were in that list until they were read properly: hashing a file is the same operation on
every operating system, is verifiable here against `sha256sum`, and says nothing about signatures or
trust — which is precisely why it could be built while the fields around it could not.

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
- [ ] Packaged application — needs `package` (§14)
- [ ] Managed runtime — needs `runtime` (§14)
- [ ] Unsigned executable — needs `signature.status` (§21)
- [ ] Invalid signature
- [ ] Suspicious reputation — needs opt-in reputation
- [x] High CPU
- [x] High memory
- [x] High disk
- [ ] High network — no per-process byte counters exist to threshold (§18)

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
- [ ] Process with an active UI window — needs §39
- [ ] Process with a changed executable — needs image mtime + hash watch
- [ ] Process containing the selected search match — needs §56

The ones that are ticked are the ones the program can *prove*. The rest stay off rather than
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
- [ ] Set CPU set
- [x] **Set I/O priority** — the control that makes a backup or an indexer stop making a machine
      unusable without slowing it down much: left at normal CPU priority but moved to idle I/O, it
      keeps running at full speed and yields the disk whenever anything else wants it
- [ ] Set page priority
- [ ] Enable/disable efficiency mode or platform QoS
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
- [ ] Go to owning service
- [ ] Go to package
- [ ] Go to executable
- [x] Reveal in file manager
- [x] Open file properties — path, size, modification time, permissions, architecture and interpreter,
      with a SHA-256 on request
- [x] Copy path
- [x] Copy command line
- [x] Search Internet — confirmed, and the confirmation names the engine before it goes
- [ ] Inspect binary (§53)

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

- [x] **Set the out-of-memory priority** — `oom_score_adj`, which decides who the kernel kills when
      the machine runs out, shown beside the badness score that says who it would actually pick
- [ ] Trim working set
- [ ] Read memory
- [ ] Save memory range
- [ ] Search readable memory
- [ ] Inspect mapped region

- [ ] Direct modification of another process's memory is classified expert/debugging and **disabled
      by default** — the only feature here requiring a deliberate per-session opt-in

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

- [ ] View module
- [x] Reveal module file
- [ ] Verify signature
- [x] **Hash file** — SHA-256, computed on request and never as a side effect
- [ ] Open binary inspector
- [ ] Search reputation
- [x] Copy path
- [ ] Inspect mapped memory
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
      line, with Copy, Open folder and File properties under it. Version, publisher, signer and
      hashes need §21, and the page says outright that no signature was checked rather than leaving
      a blank that reads like a clean bill of health
- [x] Performance (§28) — six graphs over the four time windows, hover readings and a keyboard
      cursor. There is no per-process network graph because there are no per-process byte counters
      to draw one from (§18)
- [x] CPU
- [x] Memory
- [x] I/O
- [x] Threads (§29)
- [x] Modules (§31)
- [x] Handles / resources (§32)
- [ ] Memory map (§34)
- [x] Network (§40)
- [x] GPU (§19) — and the tab that proves the preference above: a machine whose driver publishes no
      per-process accounting has nothing to put on it, which is not the same as this build not
      having one
- [ ] Security (§36)
- [x] Environment (§37)
- [ ] Jobs / cgroups / containers (§38)
- [ ] Windows (§39)
- [ ] Services (§41)
- [ ] Runtime (§80)
- [ ] Strings (§35)
- [ ] Timeline (§63)

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
- [x] Base priority
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

Still unticked and why:

- **Cycles** and **cycles delta** — no file under `/proc` carries a per-thread cycle count. The only
  route is `perf_event_open`, which needs a descriptor held open per thread for the life of the view
  and the same `PTRACE_MODE_ATTACH` `syscall` needs, and returns nothing at all on a machine with no
  hardware PMU. Two hundred lines of native plumbing to render "not permitted" on every row of an
  ordinary desktop is not a column, and the process-wide `Cycles` counter has said `n/a` on Linux for
  the same reason since §8.
- **Ideal processor** and **TEB / TLS information** — no Linux equivalent. The scheduler has no
  notion of a thread's preferred processor that it will name, and the thread pointer is readable only
  through `ptrace(ARCH_GET_FS)`, which means stopping the thread to ask.
- **Wait duration** — deliberately left. `schedstat` reports cumulative time queued for a processor,
  which is not how long the current wait has lasted, and labelling it as such would be exactly the
  false equivalence §5.3 forbids. It is reported above under the name of what it actually is.
- **Description** — Linux gives a thread one name, `comm`, and it is already the Name column. A
  Description column would repeat it.
- **Service association** — a thread may be moved into its own cgroup under v2's threaded mode, so
  `/proc/[pid]/task/[tid]/cgroup` is a real per-thread reading; it is one more file per thread for a
  column that is the same value on every row of almost every process, and is not read yet.
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
- [ ] Load count
- [ ] Load time
- [ ] Load reason
- [x] 🟡 File size — Linux only; Windows reports `n/i`
- [x] 🟡 File modification time — as above
- [ ] Version
- [ ] Description
- [ ] Company
- [ ] Product
- [ ] Signature status
- [ ] Signer
- [ ] SHA-256
- [ ] ASLR
- [ ] CFG
- [x] Executable flag — the `x` of the folded mappings' permission union
- [x] Writable flag — the `w` of it
- [x] Mapped / shared — the `s`/`p` of it
- [x] Backing file
- [ ] Runtime classification

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
- [ ] Object address
- [ ] Reference count
- [x] 🟡 File offset — Linux, from `fdinfo`. A socket and an event descriptor have none and say so
- [ ] File type
- [ ] Device
- [x] 🟡 Inode — Linux, from `fdinfo`'s `ino:` with the bracketed number in `socket:[n]`/`pipe:[n]`
      as the fallback for a kernel too old to write it
- [x] Socket endpoint — the descriptor's inode joins it to a row of the five `/proc/net` tables, and
      the handles view shows the endpoint and state beside the descriptor
- [ ] Creation / open time
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
- [x] 🟡 Double-click navigates to the process — from **View ▸ Find handles or files…**, which is the
      window's half of what `--find` always did from a terminal. Navigating to the *resource* still
      has nowhere to go: there is no handle view to land in
- [x] The tree expands every ancestor of the process it selects, because a process nested under a
      collapsed parent cannot be brought into view and looking at it was the entire point

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

- [x] UID / eUID / sUID / fsUID and GID equivalents — all eight, plus the effective account by name
      as well as by number, and a field that says outright when the two disagree
- [x] 🟡 Supplementary groups — the numbers, opt-in; not resolved to group names
- [x] Capabilities — all five sets, by name (§21)
- [x] SELinux context
- [x] AppArmor profile
- [x] seccomp state — the mode and the filter count
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
- [x] Controllers — which are *enabled* here, which is not the same as which limit files exist: a
      delegated cgroup may have `memory` and not `cpu`, and then the CPU limit is inherited from an
      ancestor rather than absent
- [x] CPU limits — as a number of cores, plus how many times the cgroup has actually been throttled
- [x] Memory limit — the hard cap and the soft one, which are different limits
- [x] Current memory usage
- [ ] I/O limits
- [x] 🟡 Process membership — the count against the limit; the list of members is not read
- [x] Pressure metrics (PSI) — per cgroup, through the same parser the machine-wide ones use
- [x] **Frozen or not, and freeze / thaw** — the state from `cgroup.events` rather than from
      `cgroup.freeze`, because the first is what the cgroup *is* and the second is only what it was
      *asked* to be; they differ while a freeze is still catching processes that were inside a
      syscall when it began (§25.1)

This is the answer to "why is this slow when the machine is idle". A container or a systemd unit can
be throttled to a fraction of a core or capped well below the machine's memory, and nothing in a
process table shows it — the process simply appears to be doing less than it should. `--limits PID`
prints it, together with the process's own ceilings and its out-of-memory standing (§25.2, §25.5).

**The two kinds of ceiling are printed under separate headings and never added together**, because
different parts of the kernel enforce them against different things: `RLIMIT_NPROC` is a limit on the
*user*, `pids.max` is a limit on the *cgroup*, and a single combined number would be the false
equivalence §5.3 forbids.

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

- [ ] 🟡 Runtime · container ID ✔ (§14) · locally resolvable name · namespaces · resource limits ✔

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
- [ ] 🟡 User — Linux reads the uid the kernel charges the socket to, which is the socket's own owner
      rather than the owning process's; they differ for a descriptor passed between processes. The
      Windows owner table carries no uid at all
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
- [x] Service name — from the machine's own `/etc/services`, in `--connections`; `-n` turns it off, as it does for `ss`
- [ ] 🟡 Interface — Linux, from the address the socket is bound to. `/proc/net/if_inet6` names it
      outright for IPv6; an IPv4 address is on the interface whose on-link subnet contains it, longest
      prefix first. A socket on the wildcard address is on all of them and shows `*`; an address no
      route claims — a multicast group, a point-to-point peer — is left unknown rather than guessed at
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

- [ ] 🟡 Go to process — the window's network tab shows one process's own sockets, so the owner is
      already the selected row of the tree and there is nowhere to go. It becomes a real command when
      there is a machine-wide connection view to invoke it from
- [ ] 🟡 Process properties — as above
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
- [ ] Search remote endpoint

- [x] **Hostname resolution is asynchronous and globally disableable** — a blocking DNS lookup in a
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

- [ ] The page opens on whatever is under the greatest meaningful load rather than always on the
      processor, with a setting to turn that off

## 45.4 Graphs

- [x] 60 seconds by default, newest on the right, moving right to left
- [x] Updated once a second
- [x] Selectable history: 30 s · 60 s · 2 min · 5 min · 15 min — the axis is the span rather than
      the sample count, so a page open for sixteen seconds fills the right quarter of a minute-wide
      graph instead of stretching sixteen samples across it
- [ ] Optional 500 ms mode
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
      statistic rows are plain text and announce as what they say
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
- [ ] Handle / resource count
- [x] Uptime
- [x] **Pressure stall information** — how much of the last ten, sixty and three hundred seconds
      something was stalled waiting for the processor
- [ ] Context switches per second
- [ ] Interrupts per second
- [ ] System calls per second

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
- [ ] DPC-like kernel activity

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
- [ ] 32-bit arm — the capability words differ and are not read
- [ ] Windows on ARM — needs `IsProcessorFeaturePresent`, which is not called

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
does not know rather than inventing a name. The live clock speed is still `n/i` there — Windows
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
- [ ] NUMA nodes

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
- [ ] ARM big.LITTLE — the PMU directories are Intel's; nothing here reads the equivalent, and a
      guess from differing maximum clocks would be a guess. Unknown rather than wrong (§5.3)
- [ ] Windows: no topology is read, so the map falls back to one flat row
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
- [ ] Hardware reserved — the row is there and is refused with its reason rather than computed as a
      zero, because the installed total it subtracts from is one of the four firmware facts below
- [x] Memory pressure — both halves, `some` and `full`
- [ ] **Memory speed** — refused rather than guessed: `—`, with the reason
- [ ] **Channels**
- [ ] **Form factor** — as above
- [ ] **Slots used / available** — as above
- [x] NUMA distribution — how much memory each node has, from `/sys/devices/system/node/node*/meminfo`,
      and only where there is more than one node: "node 0 has all of it" is not a distribution

The four in bold are the Task-Manager-style hardware facts. They come from DMI/SMBIOS type-17
records: `/sys/firmware/dmi/tables` on Linux, which is root-readable and therefore a helper call, and
`GetSystemFirmwareTable` on Windows.

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
- [x] 🟡 **Transfer rate** on a dynamic scale whose unit follows the traffic — one combined line,
      with reads and writes named separately in its label; two lines are not drawn yet. Active time
      says a disk is busy; only the transfer rate says whether that is a hundred large reads or a
      hundred thousand small ones

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

- [x] **Utilisation**, 0–100 % — the per-engine selector (3D · compute · copy · decode · encode) is not
- [x] **Dedicated memory**, scaled to the card's VRAM — `14.7G / 16.0G`
- [x] **Memory bus**, which sysfs and NVML both offer and Task Manager does not
- [ ] **Shared memory** — not read
- [x] **Power**, scaled to the ceiling and labelled with both figures — `25.5 W of 130.0 W`
- [x] **Temperature**, on the red accent so it never reads as another utilisation figure, and on a
      fixed 0–100 scale so a card idling between 40 and 42 °C does not fill its graph
- [ ] 🟡 **Fan** — plotted where the card reports one; RPM and multiple fans are not read

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
- [ ] Network activity
- [ ] Clicking any top-process entry navigates to that process — the entry carries the identity pair
      ready for it; the page has no click handling yet

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
command yet (§25). The information each of them acts on is reachable from the TUI — `--limits` prints
every ceiling and the out-of-memory standing — so the second clause above holds; the actions
themselves do not, and this is where that is written down instead of being discovered later.

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

What the tools this one is meant to replace can do, and whether it can. Ticked means a person can
reach it and it gives a true answer; 🟡 means part of it is there and the rest is named after it.
Several capabilities exist only on the command line so far — that is written down here rather than
hidden behind a tick, because a feature nobody can reach from the window is not parity with a tool
that has a tab for it. That drift is what made an earlier version of this matrix wrong.

## Windows Task Manager

- [x] Processes
- [x] Performance — twenty-two resources on the rail, each with its own graphs and figures
- [ ] App / usage history — nothing keeps a per-application total across sessions
- [ ] 🟡 Startup — a window view listing every entry and which will run; nothing can turn one off yet
- [x] Users — a window view of who is logged in and what their processes cost
- [x] Details
- [ ] 🟡 Services — a window view of every unit and its state; starting and stopping are not written
- [ ] 🟡 Run new task — `--run` starts a program with a directory, environment and priority; no dialog
- [x] End task
- [ ] Restart supported applications
- [x] Priority
- [x] Affinity
- [ ] Efficiency / QoS — refused rather than unwritten: the nearest Linux relatives are the scheduling class and the I/O class, both already settable, and mapping a QoS class onto them is the false equivalence §5.3 forbids
- [ ] Dumps
- [ ] 🟡 Wait chains — each thread says what it is blocked in and which syscall it is in; nothing follows the chain from one process to the next
- [x] Process search / filter
- [ ] Startup enable / disable
- [ ] User session control
- [ ] Service controls
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
- [ ] Signature verification — an ELF carries no Authenticode; the honest equivalent is asking the package manager whether the file still matches what it shipped, which is not written yet
- [ ] 🟡 Image metadata — architecture, word size, position independence, interpreter, size and mtime are read; there is no version or vendor string to read on Linux
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
- [ ] 🟡 Services — listed, not controlled
- [x] Network connections
- [x] Disk activity — per-process I/O columns, and a page per disk with its own throughput and queue
- [x] GPU data — the adapter's own figures, and per process the memory and the time on each engine
- [x] Memory maps
- [x] Environment
- [x] Security / token information — all five capability sets by name, both id quartets, seccomp mode and filter count, no-new-privs and the LSM label
- [x] File / resource ownership search
- [x] Advanced process scheduling
- [ ] Detailed service control
- [ ] 🟡 Binary inspection — the ELF header and the mapped images; nothing disassembles
- [ ] Runtime inspection
- [ ] Process notes / rules
- [x] Configurable columns

## DBC Task Manager

- [x] Clear simple process page
- [x] Attractive resource graphs — a graticule, a filled area, a selectable time axis and a cursor that reads a sample
- [x] Resource selector
- [x] CPU · memory · disks · networks pages
- [ ] 🟡 Services — listed in a view of its own; not controlled
- [ ] 🟡 Startup — listed in a view of its own; not enabled or disabled
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

**Processes — Basic:** name · PID · status · CPU · memory · disk · network · GPU

- [ ] 🟡 Available except disk, network and GPU

**Processes — Expert:** process · PID · PPID · CPU · private memory · working set · I/O rate · user ·
start time · command line · signature

- [ ] 🟡 Available except signature

**Security:** name · PID · user · path · signer · signature · integrity/security context · elevated ·
protection · hash · reputation

- [ ] 🟡 Available except signer, signature, protection, hash and reputation — the identity and
      confinement half of the set is there (§21), and everything still missing belongs to §70's code
      signing, which has no Linux counterpart to build against

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

**802 tests pass on every leg, under both a UTF-8 and a `C` locale.**

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
- [ ] 100 000 threads
- [x] 1 000 000 resource rows
- [x] Rapid process churn
- [x] 100 % CPU load — a sample taken while every core is saturated is still a whole sample
- [x] Low-memory state — free, available and total stay consistent and stay distinct
- [x] High-I/O machine — the byte counters only ever go forwards, checked across real disk work

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
- [x] Affinity — from the window; there is no command-line switch for it
- [x] Basic Performance page
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
- [x] The user can kill a process from GUI, TUI **and** CLI
- [x] The user can suspend and resume where supported
- [x] The user can inspect process path and command line
- [x] The user can inspect the process tree
- [x] The user can inspect active network endpoints — the pane's network tab, and `--connections`
- [ ] 🟡 The user can inspect services — starting and stopping them is not written
- [ ] The user can manage common startup items
- [ ] 🟡 The user can inspect logged-in sessions — from the CLI; neither front-end has the view
- [x] The user can view CPU, memory, disk and network performance — thirteen resource pages, each
      with its own graphs
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
