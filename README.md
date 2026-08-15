# ProcessManager

[![License](https://img.shields.io/github/license/Hawkynt/ProcessManager)](LICENSE)
[![Language](https://img.shields.io/github/languages/top/Hawkynt/ProcessManager?color=8957D5)](https://github.com/Hawkynt/ProcessManager)

[![CI](https://img.shields.io/github/actions/workflow/status/Hawkynt/ProcessManager/ci.yml?branch=main&label=CI)](https://github.com/Hawkynt/ProcessManager/actions/workflows/ci.yml)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager/commits/main)
[![Activity](https://img.shields.io/github/commit-activity/m/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager/pulse)

[![Stars](https://img.shields.io/github/stars/Hawkynt/ProcessManager?color=FFD700)](https://github.com/Hawkynt/ProcessManager/stargazers)
[![Forks](https://img.shields.io/github/forks/Hawkynt/ProcessManager?color=008080)](https://github.com/Hawkynt/ProcessManager/network/members)
[![Issues](https://img.shields.io/github/issues/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager/issues)
[![Code Size](https://img.shields.io/github/languages/code-size/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager)
[![Repo Size](https://img.shields.io/github/repo-size/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager)

[![Release](https://img.shields.io/github/v/release/Hawkynt/ProcessManager)](https://github.com/Hawkynt/ProcessManager/releases/latest)
[![Nightly](https://img.shields.io/github/v/release/Hawkynt/ProcessManager?include_prereleases&sort=date&filter=nightly-*&label=nightly&color=FF9800)](https://github.com/Hawkynt/ProcessManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/ProcessManager/total)](https://github.com/Hawkynt/ProcessManager/releases)

> A cross-platform process manager in C#: a Process-Explorer-shaped desktop UI and an htop-shaped
> terminal UI over one sampling engine. The desktop UI is built on
> [NativeForms](https://github.com/Hawkynt/NativeForms), so the same binary puts real Win32 windows on
> Windows and real GTK windows on Linux without a second UI codebase.

> [!IMPORTANT]
> **Early, and honest about it.** The engine, the Linux probe and the terminal UI work and are
> tested; the desktop window comes up and shows the tree and the plots but is not finished; the
> Windows probe is written and **has never been run on Windows**; macOS throws; the privileged helper
> refuses everything. [`docs/PRD.md`](docs/PRD.md) tracks all of it box by box — when a box is
> unticked there, the feature does not exist. §12 is the per-feature coverage matrix and §10 the
> milestone state.

## ✨ What it is

Three things that are usually three separate programs:

- **A process explorer** — the process tree with per-process CPU, memory, I/O, handles and threads;
  the detail views (threads, modules, open handles, environment, network endpoints); and the search
  that answers "which process has this file open?".
- **A task manager** — end a process or its tree, suspend and resume it, change priority and CPU
  affinity, and see at a glance what is eating the machine.
- **A terminal monitor** — the same data as a full-screen console UI over SSH, where no display exists
  and installing a desktop toolkit is not an option.

All three read from **one** sampling engine (`ProcessManager.Core`) behind one platform probe
interface. A metric is implemented once, and both front-ends get it; a front-end has no privilege to
read anything the other cannot.

## 🧩 Architecture

```
ProcessManager.Core                Sampling engine: snapshots, deltas, rates, history, tree building,
                                   sort/filter. No platform code, no UI, no I/O beyond the probe.
ProcessManager.Platform.Linux      /proc, /sys, cgroup v2, netlink                       — shipping
ProcessManager.Platform.Windows    NtQuerySystemInformation, ToolHelp32, PDH             — shipping
ProcessManager.Platform.MacOS      libproc / sysctl                                      — stub, throws
ProcessManager.Ui.Terminal         Terminal renderer, no toolkit dependency
ProcessManager.Ui.Desktop          Desktop UI on NativeForms
ProcessManager.App                 The one binary: CLI plus both front-ends           — procman
ProcessManager.Elevated            procman-helper: the only component that ever runs as root/admin
```

One executable carries both front-ends, so `procman` and `procman --tui` are the same program. A
headless machine still never loads GTK: a NativeForms backend that is not registered is never asked
for its native library.

Core never calls a native API; it asks an `ISystemProbe` for a `SystemSnapshot` and does the
arithmetic. That is what makes the whole engine testable against recorded `/proc` trees and captured
Windows structures rather than against the machine running the tests — see
[PRD §9](docs/PRD.md#9-testing-strategy).

**Platform support: Windows and Linux.** macOS is a
[stated future direction](docs/PRD.md#10-milestones), not a shipped feature — the macOS probe is a
stub whose every member throws `PlatformNotSupportedException` with an actionable message.

## 🚀 Usage

```sh
procman                       # desktop UI (Win32 on Windows, GTK on Linux)
procman --tui                 # full-screen terminal UI
procman --tui --sort=cpu      # start sorted by CPU, tree mode off
procman --list --json         # one snapshot to stdout as JSON, then exit
procman --find "libssl"       # which processes have a handle/mapping matching this?
procman --kill 1234 --tree    # end a process and its descendants
```

The terminal UI keeps the keys htop users already have in their fingers — `F5` tree, `F6` sort,
`F9` kill, `F10` quit, `/` search, `\` filter, `u` filter by user, `H` show/hide threads — and the
desktop UI keeps the layout Process Explorer users already have in their eyes: a tree on top, a
detail pane below, a system graph in the toolbar, double-click for process properties.

## 📊 What it shows

| Area                    | Contents                                                                                                                                                                                                                  |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Process tree**        | Name, PID, PPID, user, state, CPU %, private bytes, working set / RSS, virtual size, I/O read + write rates, handle / file-descriptor count, thread count, start time, session, priority, command line, working directory |
| **Per-process details** | Threads (TID, state, CPU, start address) · modules and mappings (path, base, size, permissions) · handles and open files (type, name, access) · environment block · TCP/UDP endpoints · memory regions                    |
| **System overview**     | Per-core CPU history, load average, memory and swap with cache breakdown, I/O throughput, network per-interface throughput, disk per-device throughput, uptime, context switches, interrupts                              |
| **Search**              | One query across process names, command lines, open files, mapped modules and listening ports — the "who is holding this file" question, answered in one place                                                            |

Rows are coloured the way Process Explorer colours them — new green, exited red — in the terminal UI.
The window does not colour them yet; see the limitations below for why.

## 🔐 Privileged operations

Most of what this program shows needs no privileges at all. A few things do: reading another user's
command line and open files, ending another user's process, and per-process network capture.

ProcessManager stays **unprivileged** and starts a small separate helper (`procman-helper`) only when
an action needs one — polkit on Linux, a UAC-elevated child on Windows. The helper accepts a fixed
set of typed requests over a private pipe, checks each one against an allowlist, and exits with the
program. It does not evaluate anything it receives, and it never runs the UI. When the helper is not
available the affected columns and actions are disabled with the reason shown, rather than the whole
program refusing to start.

See [PRD §8](docs/PRD.md#8-privilege-model) for the protocol and its threat model.

## 🛠️ Build

```sh
dotnet build ProcessManager.slnx -c Release
dotnet test  ProcessManager.slnx -c Release
dotnet run --project ProcessManager.App -- --tui              # terminal UI
dotnet run --project ProcessManager.App                       # desktop UI; needs GTK 3 on Linux
dotnet run --project ProcessManager.Benchmarks                # the PRD §4 budget harness

# Everything replays against a recorded /proc tree, on any OS:
dotnet run --project ProcessManager.App -- --list --tree \
  --probe-root ProcessManager.Tests/Fixtures/proc-desktop
```

Publishing produces a single self-contained binary per platform, NativeAOT where the platform allows
it. Trim and AOT warnings are build errors — see [PRD §4](docs/PRD.md#4-performance--footprint-budget)
for the footprint budget the CI enforces.

## CI

GitHub Actions, same four-workflow layout as the other repos here:

| Workflow      | Trigger                  | Does                                                                                                                                                            |
| ------------- | ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ci.yml`      | push / PR                | Build + test on Linux, Windows and macOS; a NativeAOT publish per RID with trim warnings as errors; a headless run of both front-ends against recorded fixtures |
| `_build.yml`  | called                   | The shared publish block — NativeAOT self-contained binaries, one runner per RID (AOT cannot cross-compile), so release and nightly can never diverge           |
| `nightly.yml` | after green CI on `main` | Nightly prerelease + the sampling benchmark harness, GFS-pruned to 7 daily / 4 weekly / 3 monthly                                                               |
| `release.yml` | manual dispatch          | CI, build, changelog, and a dated `vyyyyMMdd` GitHub Release                                                                                                    |

Versions are never taken from a tag: `.github/workflows/scripts/version.pl --stamp` rewrites each
project's own `<Version>X.Y.Z</Version>` to `X.Y.Z.<commit count of that folder>` at build time.

## Inspiration

- [Process Explorer](https://learn.microsoft.com/sysinternals/downloads/process-explorer) — the tree, the handle search, the color legend
- [System Informer / Process Hacker](https://systeminformer.sourceforge.io/) — the privilege split and the depth of the detail views
- [htop](https://htop.dev/) — the terminal layout and its keybindings
- [btop](https://github.com/aristocratos/btop) — the system graphs
- Windows Task Manager — the "what is wrong right now" first screen

## Known limitations

These are consequences of the design, not a to-do list; the to-do list is the PRD.

- **macOS does not work.** The probe is a stub whose every member throws. Nothing samples, nothing
  renders.
- **The Windows probe is unverified.** It is written — one `NtQuerySystemInformation` call for the
  whole process and thread list, per-core times, memory, I/O, and the actions — and it has never
  executed on Windows. Its structure-replay test (PRD §9.4) is the next thing to write.
- **Sampling costs more than it should.** 44 µs of CPU per process on Linux, where the budget says
  25 — three files are read per process and syscalls are the whole cost. Measured, tracked, and the
  trade is written down in PRD §4 rather than left as a number nobody intends to meet.
- **The desktop rows are not coloured.** The green-for-new, red-for-exited convention is in the
  terminal UI and not in the window, because NativeForms' `TreeListView` has no per-row colour hook.
  The engine already reports what the colours would say; the fix belongs in the toolkit.
- **Per-process network capture needs the helper.** Linux attributes sockets to processes through
  `/proc/net` plus inode matching, which is unprivileged but coarse; anything finer needs root.
- **No kernel driver, ever.** Everything Process Explorer does through its driver — real thread
  stacks with symbols, kernel object inspection, protected-process access — is out of reach here and
  stated as a non-goal in the PRD, rather than promised and quietly missing.
- **Sampled, not traced.** Rates come from differencing counters at an interval. A process that lives
  and dies inside one interval is a gap in the data, and the UI says so instead of drawing a zero.

## ❤️ Support

If ProcessManager is useful to you, consider supporting development:

[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-Hawkynt-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see [LICENSE](LICENSE).
