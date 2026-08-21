using Hawkynt.ProcessManager.Abstractions;
using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Checks a probe against an independent source of truth: the BCL's own view of this process.
/// </summary>
/// <remarks>
/// <para>
/// The fixture tests (PRD §9.1) prove the parsers turn recorded bytes into the right numbers. They
/// cannot prove the numbers describe the machine — a struct field read at the wrong offset, a unit
/// converted the wrong way, or an OS that simply does not fill a field all look identical to a
/// parser test. This asks a different question: does what the probe says about *this* process agree
/// with what <see cref="System.Diagnostics.Process"/> and <see cref="Environment"/> say about it?
/// </para>
/// <para>
/// It runs on every platform with a probe, which is what makes it the verification step §9.4 was
/// missing for Windows: on a Windows runner — or under Wine, whose ntdll implements the same
/// structure — it is the first thing that has ever executed the Windows probe against a kernel.
/// </para>
/// <para>
/// Checks are ratios and windows, not equalities. The process keeps running between the probe's read
/// and the BCL's, so memory and CPU move; an exact-match assertion here would be a flaky test, and a
/// flaky test gets deleted.
/// </para>
/// </remarks>
internal static class SelfTest {

  public static int Run(Sampler sampler, string probeDescription)
    => Run(sampler, probeDescription, null);

  public static int Run(Sampler sampler, string probeDescription, ISystemProbe? probe) {
    var failures = new List<string>();
    var notes = new List<string>();

    Console.WriteLine($"probe:    {probeDescription}");
    Console.WriteLine($"platform: {Environment.OSVersion.VersionString}");
    Console.WriteLine($"pid:      {Environment.ProcessId}");
    Console.WriteLine();

    // Two samples an interval apart, so the rate columns have something to say as well.
    sampler.Sample();
    Thread.Sleep(600);
    sampler.Sample();

    var snapshot = sampler.Current;
    var delta = sampler.Delta;

    if (snapshot.ProcessCount == 0) {
      Console.Error.WriteLine("FAIL the probe returned no processes at all");
      return 1;
    }

    var index = -1;
    for (var i = 0; i < snapshot.ProcessCount; ++i)
      if (snapshot.Processes[i].Pid == Environment.ProcessId) {
        index = i;
        break;
      }

    if (index < 0) {
      Console.Error.WriteLine($"FAIL the probe listed {snapshot.ProcessCount} processes and this one was not among them");
      return 1;
    }

    ref readonly var self = ref snapshot.Processes[index];
    using var expected = System.Diagnostics.Process.GetCurrentProcess();

    Check(failures, notes, "process count", $"{snapshot.ProcessCount}", snapshot.ProcessCount > 1,
      "a machine with one process is a machine that is not running this program");

    Check(failures, notes, "name", $"{self.Name}  (BCL: {expected.ProcessName})",
      self.Name.StartsWith(expected.ProcessName, StringComparison.OrdinalIgnoreCase)
      || expected.ProcessName.StartsWith(self.Name, StringComparison.OrdinalIgnoreCase),
      // Linux truncates comm to 15 characters and Windows keeps the extension, so one is a prefix of
      // the other rather than equal to it.
      "the probe's name and the runtime's must be prefixes of each other");

    // Not "parent pid > 0": pid 1 on Linux, the idle process on Windows, and the first process
    // inside a Wine prefix all legitimately have no visible parent. The invariant that is actually
    // worth asserting is that the links form a tree — no process is its own parent, and the tree
    // walk returns every process exactly once rather than losing one to a broken link or looping.
    Check(failures, notes, "parent pid", $"{self.ParentPid}", self.ParentPid != self.Pid,
      "a process cannot be its own parent");

    Check(failures, notes, "tree is well formed", TreeShape(snapshot, delta), TreeIsComplete(snapshot, delta),
      "nesting every process under its parent must not lose or duplicate any of them");

    Check(failures, notes, "thread count", $"{self.ThreadCount}  (BCL: {expected.Threads.Count})",
      self.ThreadCount > 0 && WithinFactor(self.ThreadCount, expected.Threads.Count, 3),
      "thread counts should agree within a factor of three");

    CheckCounter(failures, notes, "working set", self.WorkingSetBytes, (ulong)expected.WorkingSet64, 3);
    CheckPrivateBytes(failures, notes, in self, expected);
    CheckCounter(failures, notes, "virtual bytes", self.VirtualBytes, (ulong)expected.VirtualMemorySize64, 8);

    // The peaks are not comparable against anything the runtime exposes, so they are checked for the
    // relationship that must hold: a peak is never smaller than the current value.
    CheckPeak(failures, notes, "peak working set", self.PeakWorkingSetBytes, self.WorkingSetBytes);
    CheckPeak(failures, notes, "peak virtual", self.PeakVirtualBytes, self.VirtualBytes);
    Check(failures, notes, "page faults", Humanize.Count(self.PageFaults),
      !self.PageFaults.HasValue || self.PageFaults.Value > 0,
      "a process that has run has faulted at least once");

    CheckSecurity(failures, notes, in self);
    CheckScheduling(failures, notes, in self);
    CheckWindowsOnly(failures, notes, snapshot, in self, expected);

    // CPU time only grows, and the probe read it first, so it must not exceed the later reading by
    // more than the interval could account for.
    var expectedCpuNs = (ulong)(expected.TotalProcessorTime.TotalSeconds * 1_000_000_000);
    CheckCounter(failures, notes, "cpu time", self.CpuTimeNs, expectedCpuNs, 4);

    var startTime = new DateTime(self.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime();
    var startDrift = Math.Abs((startTime - expected.StartTime).TotalSeconds);
    Check(failures, notes, "start time", $"{startTime:HH:mm:ss}  (BCL: {expected.StartTime:HH:mm:ss}, drift {startDrift:0.0}s)",
      startDrift < 5,
      "a start time more than five seconds out means the boot-time or tick conversion is wrong");

    Check(failures, notes, "identity is unique", "no duplicate (pid, start) pairs", NoDuplicateKeys(snapshot),
      "two processes sharing an identity would make every delta wrong");

    Check(failures, notes, "cpu percent", Humanize.Percent(delta.CpuPercent(index)) + " %",
      delta.CpuPercent(index).HasValue,
      "a second sample must produce a rate");

    Check(failures, notes, "system cpu percent", Humanize.Percent(delta.SystemCpuPercent) + " %",
      delta.SystemCpuPercent.HasValue && delta.SystemCpuPercent.Value is >= 0 and <= 101,
      "the machine's busy percentage has to be a percentage");

    Check(failures, notes, "total memory", Humanize.Bytes(snapshot.System.TotalMemoryBytes),
      snapshot.System.TotalMemoryBytes.HasValue && snapshot.System.TotalMemoryBytes.Value > 64ul * 1024 * 1024,
      "a machine with under 64 MB of RAM is not running .NET");

    Check(failures, notes, "per-core times", $"{snapshot.PerCoreCount} cores (Environment: {Environment.ProcessorCount})",
      snapshot.PerCoreCount > 0,
      "the per-core meters need per-core counters");

    Check(failures, notes, "owner", self.UserName ?? $"uid {self.UserId}",
      self.UserName is not null || self.UserId >= 0,
      "every process has an owner, and a row that cannot name it says so");

    if (probe is not null)
      CheckDetailQueries(failures, notes, probe, self.Key, expected);

    Console.WriteLine();
    foreach (var note in notes)
      Console.WriteLine($"note {note}");

    foreach (var failure in failures)
      Console.WriteLine($"FAIL {failure}");

    Console.WriteLine();
    Console.WriteLine(failures.Count == 0
      ? $"OK: the probe agrees with the runtime on all {notes.Count + failures.Count} checks."
      : $"{failures.Count} check(s) disagree with the runtime.");

    return failures.Count == 0 ? 0 : 1;
  }

  /// <summary>
  /// The on-demand queries behind the detail views (PRD §6.2).
  /// </summary>
  /// <remarks>
  /// These are the parts of a probe that the process list never exercises, and on Windows they are
  /// the newest code in the product — the Toolhelp module walk, the handle table filtered by owner
  /// and duplicated into this process, and the thread list read back out of the sampling buffer.
  /// Asking them about the process we are running in is the cheapest way to find out they work at
  /// all, and it is the only check that ever has.
  /// </remarks>
  private static void CheckDetailQueries(
    List<string> failures,
    List<string> notes,
    ISystemProbe probe,
    ProcessKey key,
    System.Diagnostics.Process expected
  ) {
    var threads = probe.GetThreads(key);
    Check(failures, notes, "threads listed", $"{threads.Count}  (BCL: {expected.Threads.Count})",
      threads.Count > 0 && WithinFactor(threads.Count, expected.Threads.Count, 3),
      "a running process has threads, and the detail view has to be able to name them");

    var modules = probe.GetModules(key);
    Check(failures, notes, "modules listed", $"{modules.Count}",
      modules.Count > 0,
      "a .NET process has loaded libraries; an empty list means the walk found nothing it should have");

    // Handles and the environment are permission-dependent even for one's own process on some
    // platforms, so an empty list is reported rather than failed — but a *throw* is not acceptable,
    // and neither is a handle with no type at all.
    var handles = probe.GetHandles(key);
    var named = 0;
    foreach (var handle in handles)
      if (handle.Name is not null)
        ++named;

    Check(failures, notes, "handles listed", $"{handles.Count} ({named} named)",
      true,
      "reported rather than asserted: a platform may refuse this even for one's own process");

    var environment = probe.GetEnvironment(key);
    Check(failures, notes, "environment listed", $"{environment.Count} variables",
      true,
      "reported rather than asserted");

    var connections = probe.GetConnections(key);
    Check(failures, notes, "sockets listed", $"{connections.Count}",
      true,
      "reported rather than asserted; a process with no sockets is normal");
  }

  /// <summary>
  /// The two private-memory columns, checked against what the runtime reports.
  /// </summary>
  /// <remarks>
  /// This check used to be platform-special-cased, because the probe reported <c>RssAnon</c> on Linux
  /// against a runtime figure of <c>VmData</c> — resident against virtual, a factor of twenty-three
  /// apart on this machine and neither of them wrong. The columns were split instead: private bytes
  /// is the committed figure on both platforms and compares directly, and the resident part became
  /// <see cref="ProcessRecord.PrivateWorkingSetBytes"/>, which must be a subset of it.
  /// </remarks>
  private static void CheckPrivateBytes(
    List<string> failures,
    List<string> notes,
    in ProcessRecord self,
    System.Diagnostics.Process expected
  ) {
    CheckCounter(failures, notes, "private bytes", self.PrivateBytes, (ulong)expected.PrivateMemorySize64, 4);

    if (!self.PrivateWorkingSetBytes.HasValue) {
      Console.WriteLine($"  ok   {"private WS",-20} {Humanize.Placeholder(self.PrivateWorkingSetBytes.Reason)}");
      notes.Add($"private WS: not available ({self.PrivateWorkingSetBytes.Reason})");
      return;
    }

    // Resident private cannot exceed either committed private or the whole working set. Two
    // relationships that must hold on any platform, and that a field read at the wrong offset or a
    // kB-versus-byte slip breaks immediately.
    var resident = self.PrivateWorkingSetBytes.Value;
    var committed = self.PrivateBytes.GetValueOrDefault(ulong.MaxValue);
    var workingSet = self.WorkingSetBytes.GetValueOrDefault(ulong.MaxValue);
    var ok = resident > 0 && resident <= committed && resident <= workingSet;

    Console.WriteLine(
      $"  {(ok ? "ok  " : "FAIL")} {"private WS",-20} {Humanize.Bytes(self.PrivateWorkingSetBytes)}"
      + $"  (of {Humanize.Bytes(self.PrivateBytes)} committed, {Humanize.Bytes(self.WorkingSetBytes)} working set)"
    );

    if (ok)
      notes.Add($"private WS: {Humanize.Bytes(self.PrivateWorkingSetBytes)}");
    else
      failures.Add(
        $"private WS {Humanize.Bytes(self.PrivateWorkingSetBytes)} is not a subset of the committed "
        + $"private bytes ({Humanize.Bytes(self.PrivateBytes)}) and the working set ({Humanize.Bytes(self.WorkingSetBytes)})"
      );
  }

  private static string TreeShape(SystemSnapshot snapshot, SnapshotDelta delta) {
    var view = new ProcessView { TreeMode = true };
    view.Rebuild(snapshot, delta);
    var roots = 0;
    foreach (var row in view.Rows)
      if (row.Depth == 0)
        ++roots;

    return $"{view.RowCount} rows from {snapshot.ProcessCount} processes, {roots} root(s)";
  }

  private static bool TreeIsComplete(SystemSnapshot snapshot, SnapshotDelta delta) {
    var view = new ProcessView { TreeMode = true };
    view.Rebuild(snapshot, delta);
    if (view.RowCount != snapshot.ProcessCount)
      return false;

    var seen = new HashSet<int>();
    foreach (var row in view.Rows)
      if (!seen.Add(row.Index))
        return false;

    return true;
  }

  /// <summary>A peak that is below the current reading is a peak read from the wrong offset.</summary>
  private static void CheckPeak(List<string> failures, List<string> notes, string name, Counter peak, Counter current) {
    if (!peak.HasValue) {
      Console.WriteLine($"  ok   {name,-20} {Humanize.Placeholder(peak.Reason)}");
      notes.Add($"{name}: not available ({peak.Reason})");
      return;
    }

    var ok = !current.HasValue || peak.Value >= current.Value;
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-20} {Humanize.Bytes(peak)}  (current {Humanize.Bytes(current)})");
    if (ok)
      notes.Add($"{name}: {Humanize.Bytes(peak)}");
    else
      failures.Add($"{name} {Humanize.Bytes(peak)} is below the current {Humanize.Bytes(current)}");
  }

  /// <summary>
  /// Whether this is Wine rather than Windows.
  /// </summary>
  /// <remarks>
  /// Told, rather than detected. Sniffing for an export in ntdll would mean interop in a file that
  /// is compiled for every platform, to answer a question the thing running the test already knows —
  /// so the leg that runs under wine says so, and nothing else has to guess.
  /// <para>
  /// Wine implements a great deal of Win32 and stubs the rest, and the stubs answer honestly: an
  /// unimplemented call returns "not supported" and this program reports that faithfully. So a check
  /// that asserts what a real Windows would say fails there for a reason that is neither a defect in
  /// the program nor a defect in the reading — the machine genuinely cannot answer.
  /// <para>
  /// The leg is still worth running: it catches a call that crashes, one that returns nonsense, and
  /// every reading Wine does implement. Only the assertions Wine cannot honour step aside, and they
  /// say so rather than passing quietly.
  /// </para>
  /// </remarks>
  private static bool OnWine { get; } =
    Environment.GetEnvironmentVariable("PROCMAN_EMULATED") is "wine";

  /// <summary>
  /// A check that only a real Windows can answer.
  /// </summary>
  /// <remarks>
  /// Reported as skipped under Wine rather than passed: a check that quietly succeeds where it was
  /// never run is worse than one that fails, because it is counted.
  /// </remarks>
  private static void CheckOnWindowsOnly(
    List<string> failures,
    List<string> notes,
    string name,
    string value,
    bool ok,
    string why
  ) {
    if (!OnWine) {
      Check(failures, notes, name, value, ok, why);
      return;
    }

    Console.WriteLine($"  skip {name,-20} {value}  (wine does not implement this)");
    notes.Add($"{name}: skipped, wine does not implement it");
  }

  private static void Check(List<string> failures, List<string> notes, string name, string value, bool ok, string why) {
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-20} {value}");
    if (ok)
      notes.Add($"{name}: {value}");
    else
      failures.Add($"{name}: {value} — {why}");
  }

  private static void CheckCounter(
    List<string> failures,
    List<string> notes,
    string name,
    Counter actual,
    ulong expected,
    double factor
  ) {
    if (!actual.HasValue) {
      // Not a failure by itself: §3.4 says a value the platform will not give us is reported as a
      // reason, and the reason is the correct answer. It is recorded so the run says which columns
      // this platform cannot fill.
      Console.WriteLine($"  ok   {name,-20} {Humanize.Placeholder(actual.Reason)} ({actual.Reason})");
      notes.Add($"{name}: not available on this platform ({actual.Reason})");
      return;
    }

    // Zero where the runtime reports megabytes is the interesting case: it is what a field read at
    // the wrong offset looks like, and also what an OS that does not fill the field looks like.
    // Either way it is not a number anyone should show, so it fails and the run says which.
    var ok = actual.Value > 0
      ? WithinFactor(actual.Value, expected, factor)
      : expected == 0;

    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-20} {Humanize.Bytes(actual)}  (runtime: {Humanize.Bytes(Counter.Of(expected))})");
    if (ok)
      notes.Add($"{name}: {Humanize.Bytes(actual)}");
    else
      failures.Add($"{name}: probe says {Humanize.Bytes(actual)}, runtime says {Humanize.Bytes(Counter.Of(expected))}");
  }

  private static bool WithinFactor(double actual, double expected, double factor) {
    if (expected <= 0)
      return actual >= 0;

    var ratio = actual / expected;
    return ratio >= 1 / factor && ratio <= factor;
  }

  private static bool NoDuplicateKeys(SystemSnapshot snapshot) {
    var seen = new HashSet<ProcessKey>();
    foreach (var process in snapshot.Processes)
      if (!seen.Add(process.Key))
        return false;

    return true;
  }


  /// <summary>
  /// The security fields, against whatever the runtime will say about this same process.
  /// </summary>
  /// <remarks>
  /// The arithmetic behind these is unit-tested against hand-built structures on every platform;
  /// this is the other half — that the real tokens and the real /proc files on a real machine feed
  /// that arithmetic the bytes it expects (PRD §9.4).
  /// </remarks>
  /// <summary>
  /// The scheduler class, read a second time and plainly (PRD §15).
  /// </summary>
  /// <remarks>
  /// Field 41 of <c>stat</c> sits behind a command name that may contain spaces and brackets and
  /// behind fourteen fields nothing else reads, which makes it exactly the kind of positional value
  /// that a parser can miscount without anything looking wrong — the number beside it is a plausible
  /// small integer too. So it is counted again here, by a completely different route, and the two
  /// have to agree.
  /// </remarks>
  private static void CheckScheduling(List<string> failures, List<string> notes, in ProcessRecord self) {
    if (!OperatingSystem.IsLinux())
      return;

    string line;
    try {
      line = File.ReadAllText("/proc/self/stat");
    } catch (IOException) {
      return;
    }

    var close = line.LastIndexOf(')');
    if (close < 0)
      return;

    var fields = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    // State is field 3, so field 41 — the policy — is the thirty-ninth of what follows the name.
    if (fields.Length < 39)
      return;

    var expected = fields[38];
    var shown = FieldAccessor.Text(ProcessField.SchedulingClass, in self, null, 0);
    Check(
      failures,
      notes,
      "scheduler class",
      $"{shown}  (stat field 41: {expected})",
      shown == expected switch {
        "0" => "SCHED_OTHER",
        "1" => "SCHED_FIFO",
        "2" => "SCHED_RR",
        "3" => "SCHED_BATCH",
        "5" => "SCHED_IDLE",
        "6" => "SCHED_DEADLINE",
        "7" => "SCHED_EXT",
        // A class nobody has named yet must read as "no answer", never as the ordinary one.
        _ => Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
      },
      $"stat says policy {expected}"
    );
  }

  /// <summary>
  /// The §14, §20 and §21 readings that only Windows can take, against what the runtime says about
  /// the same process.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the only place any of the interop behind those columns is ever executed. The parsing
  /// halves — the PE walk, the mitigation bit decoding, the handle tally — are held against real
  /// files and hand-built tables on every CI leg, and none of that can reach a
  /// <c>GetProcessMitigationPolicy</c> or a <c>GetGuiResources</c>. A wrong structure size or a
  /// wrong policy ordinal makes the call fail rather than crash, which is invisible on a screen and
  /// is exactly what the <c>HasValue</c> checks below are for.
  /// </para>
  /// <para>
  /// The strongest check here is the version resource. <c>FileVersionInfo</c> reads the same bytes
  /// out of the same file through an entirely separate implementation, so agreeing with it is real
  /// corroboration rather than this program agreeing with itself.
  /// </para>
  /// </remarks>
  private static void CheckWindowsOnly(
    List<string> failures,
    List<string> notes,
    SystemSnapshot snapshot,
    in ProcessRecord self,
    System.Diagnostics.Process expected
  ) {
    if (!OperatingSystem.IsWindows())
      return;

    // The bulk query carries the image's file name and not its path, so until this was written the
    // Windows probe filled no path at all. The runtime's answer comes from a different call.
    string? runtimePath = null;
    try {
      runtimePath = expected.MainModule?.FileName;
    } catch (Exception) {
      // A process may refuse its own module list under some hosts; the check is then skipped rather
      // than failed, because nothing was disproved.
    }

    if (runtimePath is { Length: > 0 })
      Check(
        failures,
        notes,
        "image path",
        $"{self.ImagePath}  (BCL: {runtimePath})",
        string.Equals(self.ImagePath, runtimePath, StringComparison.OrdinalIgnoreCase),
        "the probe's path and the runtime's must be the same file"
      );

    // Not protected, and not an AppContainer. Both are true of anything a person runs from a shell,
    // and both would be false if the reading were coming back as a confident nought — PROTECTION
    // LEVEL nought is WinTCB-light, which is the most protected thing on the machine.
    CheckOnWindowsOnly(
      failures,
      notes,
      "protection level",
      Said(FieldAccessor.Text(ProcessField.ProtectionLevel, in self, null, 0), self.ProtectionLevel),
      self.ProtectionLevel.HasValue && self.ProtectionLevel.Value == 0xFFFF_FFFE,
      "an ordinary process is PROTECTION_LEVEL_NONE, which is 0xFFFFFFFE"
    );

    // The two fields that used to come back as a confident answer nobody had given: app.name read
    // "none", which on Linux means the machine has no desktop entry for the program, and runtime
    // rendered an empty placeholder. Neither was a statement this platform had made. Checked here
    // rather than only in a unit test because the defect was in the probe rather than in the
    // rendering, and this is the leg that runs the probe (PRD §72.3, §14).
    CheckOnWindowsOnly(
      failures,
      notes,
      "app.name says it does not apply",
      FieldAccessor.Text(ProcessField.ApplicationName, in self, null, 0),
      self.ApplicationNameReason == UnknownReason.NotSupportedOnPlatform,
      "Windows has no desktop entry to name a program by; the version resource is its own column"
    );

    CheckOnWindowsOnly(
      failures,
      notes,
      "runtime says nobody looked",
      FieldAccessor.Text(ProcessField.Runtime, in self, null, 0),
      self.RuntimeReason == UnknownReason.NotImplementedHere,
      "the module list is readable here and this program has not read it — which is not the same as cannot"
    );

    Check(
      failures,
      notes,
      "appcontainer",
      FieldAccessor.Text(ProcessField.AppContainer, in self, null, 0),
      self.IsAppContainer.HasValue && self.IsAppContainer.Value == 0,
      "a process started from a shell is not in an AppContainer"
    );

    // An independent answer to the same question: when the process architecture and the machine's
    // are the same nothing is being translated, and when they differ something is.
    var translated = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
      != System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
    var emulation = FieldAccessor.Text(ProcessField.Emulation, in self, null, 0);
    Check(
      failures,
      notes,
      "emulation",
      $"{emulation}  (BCL: process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, "
        + $"machine {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
      self.Emulation.HasValue && (emulation == "native") != translated,
      "IsWow64Process2 must agree with the runtime about whether this process is being translated"
    );

    CheckImageVersion(failures, notes, in self, runtimePath);
    CheckMitigations(failures, notes, in self);
    CheckObjectCounts(failures, notes, in self);
    CheckPowerThrottling(failures, notes, in self);
    CheckSignatures(failures, notes, snapshot, in self);
  }

  /// <summary>
  /// The one energy reading §22 accepts, on the only kernel that has it.
  /// </summary>
  /// <remarks>
  /// The value is not asserted — what a runtime asks for is the runtime's business — but the call
  /// succeeding is, and that is the whole of what a wrong information class or a wrong structure
  /// size would break. Both make <c>GetProcessInformation</c> return FALSE and leave the column
  /// looking like a process nobody has set a policy on, which is what most of the table looks like
  /// anyway: the failure would be invisible on a screen (PRD §72.3).
  /// </remarks>
  private static void CheckPowerThrottling(List<string> failures, List<string> notes, in ProcessRecord self) {
    // ProcessPowerThrottling arrived in Windows 10 1809, so an older Windows legitimately refuses
    // this one and is recorded rather than failed.
    if (!self.PowerThrottling.HasValue) {
      notes.Add(
        $"eco.state: not reported ({self.PowerThrottling.Reason}) — ProcessPowerThrottling needs Windows 10 1809 or newer"
      );
      return;
    }

    Check(
      failures,
      notes,
      "eco.state",
      FieldAccessor.Text(ProcessField.EcoMode, in self, null, 0),
      true,
      "a process can open itself, so this state should have been read"
    );

    notes.Add($"qos.background: {FieldAccessor.Text(ProcessField.BackgroundQualityOfService, in self, null, 0)}");
  }

  /// <summary>
  /// The Authenticode reader, against images Microsoft itself signed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The strongest check available for this one, and the reason it looks at other processes rather
  /// than at this one: the program under test is not signed, so its own row proves only that the
  /// reader ran. Windows' own system directory is full of images that are signed, timestamped and
  /// still valid, and every one of them is a file this walk did not produce — so agreeing with them
  /// is corroboration rather than this program agreeing with itself.
  /// </para>
  /// <para>
  /// A digest computed over the wrong bytes fails against all of them at once. That is the mistake
  /// this exists to catch, and it is one the unit tests cannot: they sign an image with the same
  /// arithmetic they check it with, which proves the arithmetic is self-consistent and not that it
  /// is Microsoft's.
  /// </para>
  /// </remarks>
  private static void CheckSignatures(
    List<string> failures,
    List<string> notes,
    SystemSnapshot snapshot,
    in ProcessRecord self
  ) {
    Check(
      failures,
      notes,
      "signature.status",
      FieldAccessor.Text(ProcessField.ImageSignature, in self, null, 0),
      self.ImageSignature != SignatureStatus.NotChecked,
      "the run asked for every Windows column, so this image should have been checked"
    );

    var system = Environment.SystemDirectory;
    var verified = 0;
    var signed = 0;
    string? example = null;
    string? disagreed = null;
    for (var i = 0; i < snapshot.ProcessCount; ++i) {
      ref readonly var record = ref snapshot.Processes[i];
      if (record.ImagePath is not { Length: > 0 } path
          || !path.StartsWith(system, StringComparison.OrdinalIgnoreCase)
          || record.ImageSignature is SignatureStatus.NotChecked or SignatureStatus.Unsigned)
        continue;

      ++signed;
      if (record.ImageSignature == SignatureStatus.Verified) {
        ++verified;
        example ??= $"{Path.GetFileName(path)} signed by {record.ImageSigner}";
      } else
        disagreed ??= $"{Path.GetFileName(path)}: {record.ImageSignature.Text()}";
    }

    if (signed == 0) {
      // Nothing was disproved, and there are two innocent ways to get here: an account that may read
      // no system process's path, and a Windows whose system files are not signed at all — which is
      // what the Wine leg is. Failing either would be failing a run for describing its machine
      // correctly.
      notes.Add("signature: no signed image under the system directory was readable, so nothing corroborated the digest");
      return;
    }

    Check(
      failures,
      notes,
      "signed system image",
      $"{verified} of {signed} verified" + (example is null ? "" : $"  ({example})") + (disagreed is null ? "" : $"  first other: {disagreed}"),
      verified > 0,
      "Windows' own system images are signed and timestamped, so the Authenticode digest must reproduce at least one of them"
    );
  }

  /// <summary>
  /// The version resource, against <c>FileVersionInfo</c>'s reading of the very same file.
  /// </summary>
  /// <remarks>
  /// A separate implementation of the same format, shipped by the same people who define it. Where
  /// it and the PE walk disagree about a string, one of them is wrong about where that string is —
  /// and it is not going to be the runtime's.
  /// </remarks>
  private static void CheckImageVersion(
    List<string> failures,
    List<string> notes,
    in ProcessRecord self,
    string? runtimePath
  ) {
    Check(
      failures,
      notes,
      "subsystem",
      FieldAccessor.Text(ProcessField.Subsystem, in self, null, 0),
      self.Subsystem.HasValue,
      "the image's optional header should have been read"
    );

    if (runtimePath is not { Length: > 0 })
      return;

    System.Diagnostics.FileVersionInfo version;
    try {
      version = System.Diagnostics.FileVersionInfo.GetVersionInfo(runtimePath);
    } catch (Exception) {
      return;
    }

    // Only the fields the runtime actually found. A host that ships no version resource leaves them
    // all null, and there is then nothing to corroborate rather than something to fail.
    foreach (var (name, field, other) in (ReadOnlySpan<(string, ProcessField, string?)>)[
      ("description", ProcessField.ImageDescription, version.FileDescription),
      ("company", ProcessField.ImageCompany, version.CompanyName),
      ("product", ProcessField.ImageProduct, version.ProductName),
      ("file version", ProcessField.ImageFileVersion, version.FileVersion),
      ("product version", ProcessField.ImageProductVersion, version.ProductVersion),
    ]) {
      if (other is not { Length: > 0 })
        continue;

      var mine = FieldAccessor.RawText(field, in self);
      Check(
        failures,
        notes,
        name,
        $"{mine}  (BCL: {other})",
        string.Equals(mine, other, StringComparison.Ordinal),
        "the PE walk and FileVersionInfo must read the same string out of the same file"
      );
    }
  }

  /// <summary>
  /// The six mitigation policies. A process can always open <em>itself</em> with
  /// <c>PROCESS_QUERY_INFORMATION</c>, so every one of these calls must succeed here.
  /// </summary>
  /// <remarks>
  /// The value is not asserted, because what a runtime asks for is the runtime's business and
  /// changes between versions. Whether the call succeeded is asserted, and that is the whole of what
  /// a wrong structure size or a wrong policy ordinal would break — both make the call return FALSE
  /// and leave the column looking like a mitigation that is simply switched off.
  /// </remarks>
  private static void CheckMitigations(List<string> failures, List<string> notes, in ProcessRecord self) {
    foreach (var (name, field, counter) in (ReadOnlySpan<(string, ProcessField, Counter)>)[
      ("dep", ProcessField.DataExecutionPrevention, self.DepPolicy),
      ("aslr", ProcessField.AddressSpaceRandomisation, self.AslrPolicy),
      ("cfg", ProcessField.ControlFlowGuard, self.ControlFlowGuardPolicy),
      ("acg", ProcessField.ArbitraryCodeGuard, self.DynamicCodePolicy),
      ("cig", ProcessField.CodeIntegrityGuard, self.BinarySignaturePolicy),
    ])
      Check(
        failures,
        notes,
        name,
        FieldAccessor.Text(field, in self, null, 0),
        counter.HasValue,
        "a process can open itself, so this policy should have been read"
      );

    // The shadow-stack policy arrived in Windows 10 2004 and is the one of the six that an older
    // Windows legitimately refuses, so it is recorded rather than required.
    if (self.ShadowStackPolicy.HasValue)
      notes.Add($"cet: {FieldAccessor.Text(ProcessField.ShadowStackPolicy, in self, null, 0)}");
    else
      notes.Add("cet: not reported — ProcessUserShadowStackPolicy needs Windows 10 2004 or newer");

    // On x64 data execution prevention is always on and cannot be turned off, so this one value is
    // knowable independently of what any runtime asked for.
    if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        == System.Runtime.InteropServices.Architecture.X64)
      CheckOnWindowsOnly(
        failures,
        notes,
        "dep is on",
        FieldAccessor.Text(ProcessField.DataExecutionPrevention, in self, null, 0),
        self.DepPolicy.HasValue && (self.DepPolicy.Value & 1) != 0,
        "a 64-bit process always has DEP enabled"
      );
  }

  /// <summary>
  /// The per-type handle tallies and the two desktop quotas.
  /// </summary>
  /// <remarks>
  /// The tally proves more than it looks: the object type indices are the running kernel's, worked
  /// out by duplicating one handle of each index and asking what it is called, so a count above
  /// nought here means that whole discovery pass worked. A build that hard-coded the indices would
  /// still produce numbers, which is why the check is that the count is plausible rather than that
  /// it exists.
  /// </remarks>
  private static void CheckObjectCounts(List<string> failures, List<string> notes, in ProcessRecord self) {
    Check(
      failures,
      notes,
      "event handles",
      Humanize.Count(self.EventObjectCount),
      self.EventObjectCount.HasValue && self.EventObjectCount.Value > 0,
      "a .NET process holds events, so the type index for them was found and counted"
    );

    Check(
      failures,
      notes,
      "section handles",
      Humanize.Count(self.SectionObjectCount),
      self.SectionObjectCount.HasValue && self.SectionObjectCount.Value > 0,
      "a process running from a mapped image holds at least one section"
    );

    // Recorded rather than required. An object type is only discoverable while some process on the
    // machine holds a handle of it — the index is learnt by duplicating one and asking what it is —
    // so a machine where nothing currently holds a semaphore honestly cannot fill that column, and
    // demanding a number would be demanding one that does not exist (PRD §72.3).
    foreach (var (name, counter) in (ReadOnlySpan<(string, Counter)>)[
      ("semaphore handles", self.SemaphoreObjectCount),
      ("mutex handles", self.MutexObjectCount),
      ("registry keys", self.RegistryKeyCount),
    ])
      notes.Add($"{name}: {Said(Humanize.Count(counter), counter)}");

    // These two are different: they are a call per process rather than a tally, so they have no
    // discovery step to fail. Nought is the right answer for a console program, which is why the
    // check is that the call answered rather than that the number is large.
    foreach (var (name, counter) in (ReadOnlySpan<(string, Counter)>)[
      ("user objects", self.UserObjectCount),
      ("gdi objects", self.GdiObjectCount),
    ])
      CheckOnWindowsOnly(failures, notes, name, Said(Humanize.Count(counter), counter), counter.HasValue, "this count should have been read");
  }

  /// <summary>
  /// What a column shows, and — when it shows no value — which reason it is carrying.
  /// </summary>
  /// <remarks>
  /// The placeholders are a dash, an ellipsis and "n/a", which is right on a screen and useless in a
  /// build log: a failing check has to say whether the call was refused or does not exist on this
  /// Windows, because those two ask the reader to do completely different things.
  /// </remarks>
  private static string Said(string shown, Counter counter)
    => counter.HasValue ? shown : $"{shown} ({counter.Reason})";

  private static void CheckSecurity(List<string> failures, List<string> notes, in ProcessRecord self) {
    if (OperatingSystem.IsWindows()) {
      // The BCL's own answer to "am I running elevated": the administrators group is enabled in the
      // token rather than present as deny-only, which is what TokenElevation reports too.
      var principal = new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent()
      );

      var expected = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
      Check(
        failures,
        notes,
        "elevated",
        FieldAccessor.Text(ProcessField.Elevated, in self, null, 0),
        self.IsElevated.HasValue && (self.IsElevated.Value != 0) == expected,
        $"the runtime says elevated={expected}"
      );

      // Not compared to a fixed level: a test runner may be medium or high depending on how it was
      // started. What must hold is that it is one Windows actually defines.
      var integrity = FieldAccessor.Text(ProcessField.Integrity, in self, null, 0);
      Check(
        failures,
        notes,
        "integrity",
        integrity,
        self.IntegrityLevel.HasValue && !integrity.StartsWith("0x", StringComparison.Ordinal),
        "the level should be one of the documented ones"
      );

      return;
    }

    if (!OperatingSystem.IsLinux())
      return;

    // Read again, plainly, with the managed file APIs: the probe reaches the same line through raw
    // syscalls and a span parser, and this is the check that the two agree. The capability mask was
    // wrong for every process on the machine because of a tab the span parser did not trim, and a
    // second reading of the same file is what catches that shape of bug.
    var uids = Array.Empty<int>();
    var gids = Array.Empty<int>();
    var masks = new Dictionary<string, ulong>(StringComparer.Ordinal);
    try {
      foreach (var line in File.ReadAllLines("/proc/self/status")) {
        if (line.StartsWith("Uid:", StringComparison.Ordinal))
          uids = Quartet(line[4..]);
        else if (line.StartsWith("Gid:", StringComparison.Ordinal))
          gids = Quartet(line[4..]);
        else if (line.Length > 7 && line.StartsWith("Cap", StringComparison.Ordinal)) {
          var colon = line.IndexOf(':');
          if (colon > 0 && ulong.TryParse(
                line[(colon + 1)..].Trim(),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMask
              ))
            masks[line[..colon]] = parsedMask;
        }
      }
    } catch (IOException) {
      return;
    }

    if (uids.Length < 4)
      return;

    var euid = uids[1];

    Check(
      failures,
      notes,
      "elevated",
      FieldAccessor.Text(ProcessField.Elevated, in self, null, 0),
      self.IsElevated.HasValue && (self.IsElevated.Value != 0) == (euid == 0),
      $"status says euid {euid}"
    );

    Check(
      failures,
      notes,
      "effective uid",
      self.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
      self.EffectiveUserId == euid,
      $"status says {euid}"
    );

    Check(
      failures,
      notes,
      "saved uid",
      self.SavedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
      self.SavedUserId == uids[2],
      $"status says {uids[2]}"
    );

    Check(
      failures,
      notes,
      "filesystem uid",
      self.FilesystemUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
      self.FilesystemUserId == uids[3],
      $"status says {uids[3]}"
    );

    if (gids.Length >= 4)
      Check(
        failures,
        notes,
        "effective gid",
        self.EffectiveGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        self.EffectiveGroupId == gids[1],
        $"status says {gids[1]}"
      );

    // Each of the five against its own line, because the five labels differ only in their last
    // three characters and one of them being filled from another's line is exactly the mistake that
    // would go unnoticed — every mask on the machine would still look plausible.
    foreach (var (label, counter) in (ReadOnlySpan<(string, Counter)>)[
      ("CapEff", self.EffectiveCapabilities),
      ("CapPrm", self.PermittedCapabilities),
      ("CapInh", self.InheritableCapabilities),
      ("CapBnd", self.BoundingCapabilities),
      ("CapAmb", self.AmbientCapabilities),
    ]) {
      if (!masks.TryGetValue(label, out var expected))
        continue;

      Check(
        failures,
        notes,
        label.ToLowerInvariant(),
        LinuxCapabilities.Hex(counter.GetValueOrDefault()),
        counter.HasValue && counter.Value == expected,
        $"status says {LinuxCapabilities.Hex(expected)}"
      );
    }

    // And that nothing was dropped on the way to the column: the names add back up to the mask they
    // came from. This cannot catch a wrong name — the tests hold the table against the kernel's own
    // header for that — but it does catch a bit falling out of the decoder, which is the failure
    // that reports less privilege than a process holds.
    if (self.EffectiveCapabilities.TryGetValue(out var effective))
      Check(
        failures,
        notes,
        "capability names",
        FieldAccessor.Text(ProcessField.Capabilities, in self, null, 0),
        Recompose(effective) == effective,
        "the names must re-encode to the mask they came from"
      );
  }

  /// <summary>The four ids of a <c>Uid:</c> or <c>Gid:</c> line, or empty if it is not four.</summary>
  private static int[] Quartet(string rest) {
    var fields = rest.Split(
      ['\t', ' '],
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
    if (fields.Length < 4)
      return [];

    var ids = new int[4];
    for (var i = 0; i < 4; ++i)
      if (!int.TryParse(fields[i], System.Globalization.CultureInfo.InvariantCulture, out ids[i]))
        return [];

    return ids;
  }

  /// <summary>The mask the decoded names add back up to.</summary>
  private static ulong Recompose(ulong mask) {
    var rebuilt = 0ul;
    foreach (var name in LinuxCapabilities.Decode(mask))
      for (var bit = 0; bit < 64; ++bit)
        if (name == (LinuxCapabilities.Name(bit) ?? bit.ToString(System.Globalization.CultureInfo.InvariantCulture)))
          rebuilt |= 1ul << bit;

    return rebuilt;
  }

}
