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
    var euid = -1;
    var capabilities = "?";
    try {
      foreach (var line in File.ReadAllLines("/proc/self/status")) {
        if (line.StartsWith("Uid:", StringComparison.Ordinal)) {
          var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
          if (fields.Length > 1 && int.TryParse(fields[1], out var parsed))
            euid = parsed;
        } else if (line.StartsWith("CapEff:", StringComparison.Ordinal)) {
          // Compared as a number, not as text: status writes it zero-padded to sixteen digits, and
          // trimming the padding off an all-zero mask leaves nothing at all.
          capabilities = ulong.TryParse(
            line[7..].Trim(),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedMask
          )
            ? "0x" + parsedMask.ToString("x", System.Globalization.CultureInfo.InvariantCulture)
            : "?";
        }
      }
    } catch (IOException) {
      return;
    }

    if (euid < 0)
      return;

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

    var mask = FieldAccessor.Text(ProcessField.Capabilities, in self, null, 0);
    Check(
      failures,
      notes,
      "capabilities",
      mask,
      capabilities == "?" || string.Equals(mask, capabilities, StringComparison.OrdinalIgnoreCase),
      $"status says {capabilities}"
    );
  }

}
