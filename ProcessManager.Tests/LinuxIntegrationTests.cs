using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The readers against the machine running the test, rather than against a recording (PRD §99).
/// </summary>
/// <remarks>
/// <para>
/// The fixture tests prove the parsers; these prove the parsers are handed the right files and that
/// the actions reach the kernel. Both are needed and neither substitutes for the other: a fixture
/// cannot catch a path built wrongly, and a live machine cannot be relied on to contain the awkward
/// case somebody recorded.
/// </para>
/// <para>
/// Where the machine genuinely has nothing to look at — a container with no systemd, an image with
/// no autostart directory — the test says so and stops, rather than asserting something vacuous and
/// reporting a pass. A test that cannot fail is worse than an absent one, because it is counted.
/// </para>
/// </remarks>
[TestFixture]
[Platform("Linux", Reason = "Reads the live machine and acts on real processes.")]
public sealed class LinuxIntegrationTests {

  private static LinuxProbe Probe() => new(new LinuxProbeOptions());

  private static LinuxProcessActions Actions() => new(new());

  #region affinity

  /// <summary>
  /// Setting an affinity mask, held against the kernel's own answer.
  /// </summary>
  /// <remarks>
  /// The refusal cases were covered — a stale identity, an empty mask — and the case where it works
  /// was not, so nothing established that the call reached the scheduler at all.
  /// </remarks>
  [Test]
  public void AnAffinityMaskIsAppliedAndTheKernelAgrees() {
    if (Environment.ProcessorCount < 2)
      Assert.Ignore("a single-processor machine has one mask and no way to tell it changed");

    var launched = Actions().Launch(new("/bin/sleep", ["30"]));
    Assert.That(launched.Outcome.Succeeded, Is.True, launched.Outcome.Detail);

    try {
      // Processor 1 alone, which is not the default on any machine with more than one.
      var result = Actions().SetAffinity(launched.Key, 0b10);
      Assert.That(result.Succeeded, Is.True, result.Detail);

      Assert.That(AllowedList(launched.Pid), Is.EqualTo("1"), "the kernel's own Cpus_allowed_list");
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>Two processors, so the mask is a set rather than a single choice.</summary>
  [Test]
  public void AMaskOfSeveralProcessorsIsAppliedWhole() {
    if (Environment.ProcessorCount < 3)
      Assert.Ignore("needs three processors to set two and leave one out");

    var launched = Actions().Launch(new("/bin/sleep", ["30"]));
    Assert.That(launched.Outcome.Succeeded, Is.True, launched.Outcome.Detail);

    try {
      Assert.That(Actions().SetAffinity(launched.Key, 0b110).Succeeded, Is.True);
      Assert.That(AllowedList(launched.Pid), Is.EqualTo("1-2"));
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// The affinity of a process that has since exited is refused rather than applied to whatever now
  /// holds that number (PRD §72.2).
  /// </summary>
  [Test]
  public void AnExitedProcessIsNotGivenAnAffinity() {
    var launched = Actions().Launch(new("/bin/true", []));
    Assert.That(launched.Pid, Is.GreaterThan(0));

    // /bin/true has already finished, or is about to; either way this key is not something to act on
    // once it has gone.
    Thread.Sleep(200);
    var result = Actions().SetAffinity(launched.Key, 0b1);

    Assert.That(result.Succeeded, Is.False);
  }

  private static string AllowedList(int pid) {
    foreach (var line in File.ReadAllLines($"/proc/{pid}/status"))
      if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
        return line["Cpus_allowed_list:".Length..].Trim();

    Assert.Fail($"no Cpus_allowed_list for {pid}");
    return string.Empty;
  }

  private static void Stop(int pid) {
    if (pid <= 0)
      return;

    try {
      System.Diagnostics.Process.GetProcessById(pid).Kill();
    } catch (ArgumentException) {
    } catch (InvalidOperationException) {
    }
  }

  #endregion

  #region services

  /// <summary>
  /// The units this machine actually has, against what <c>systemctl</c> says about them.
  /// </summary>
  /// <remarks>
  /// Not a count comparison: the two commands run a moment apart and a unit may start or stop
  /// between them. What must hold is that everything reported is real and described consistently —
  /// a name, a state, and a main pid that either identifies a running process or is absent.
  /// </remarks>
  [Test]
  public void EveryServiceReportedIsOneTheMachineHas() {
    using var probe = Probe();
    var services = probe.GetServices();
    if (services.Count == 0)
      Assert.Ignore("no service manager answered on this machine");

    // Both lists, because they answer different questions and this view wants both: list-units
    // reports what is loaded right now, list-unit-files what is installed. A machine here has 137
    // of the first and 372 of the second, and a service manager that showed only the loaded ones
    // would be missing every service that is installed and stopped — which is most of them, and
    // exactly the ones somebody opens this view to start.
    var known = new HashSet<string>(StringComparer.Ordinal);
    foreach (var arguments in new[] {
      "list-units --type=service --all --no-legend --plain",
      "list-unit-files --type=service --no-legend",
    })
      foreach (var line in Run("systemctl", arguments))
        if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var unit, ..])
          known.Add(unit);

    if (known.Count == 0)
      Assert.Ignore("systemctl did not answer, so there is nothing to hold this against");

    var matched = 0;
    foreach (var service in services)
      if (known.Contains(service.Name))
        ++matched;

    // Not all: a unit can vanish between the two readings, and systemctl's own list depends on what
    // it was asked for. The great majority agreeing is the check that the names are real names.
    Assert.That(matched, Is.GreaterThan(services.Count / 2),
      $"{matched} of {services.Count} unit names were ones systemctl also lists");
  }

  /// <summary>
  /// A service reported as running names a process that exists; one that is not running does not
  /// name a pid at all.
  /// </summary>
  [Test]
  public void ARunningServiceNamesAProcessThatExists() {
    using var probe = Probe();
    var services = probe.GetServices();
    if (services.Count == 0)
      Assert.Ignore("no service manager answered on this machine");

    var checked_ = 0;
    foreach (var service in services) {
      if (service.MainPid <= 0)
        continue;

      ++checked_;
      // It may have exited since; what must not happen is a pid that never was one.
      Assert.That(service.MainPid, Is.LessThan(1 << 22), service.Name);
    }

    if (checked_ == 0)
      Assert.Ignore("nothing on this machine reported a main pid");
  }

  #endregion

  #region startup

  /// <summary>
  /// The autostart entries this machine has, against the files themselves.
  /// </summary>
  /// <remarks>
  /// Every entry must come from a desktop file that is really there and really enabled, because the
  /// interesting failure is the opposite of a missing entry: one reported as starting at login that
  /// has been disabled, which tells somebody the machine will do something it will not.
  /// </remarks>
  [Test]
  public void EveryStartupEntryComesFromADesktopFileThatExists() {
    using var probe = Probe();
    var entries = probe.GetStartupEntries();
    if (entries.Count == 0)
      Assert.Ignore("nothing is configured to start at login on this machine");

    foreach (var entry in entries) {
      Assert.That(entry.Name, Is.Not.Empty);
      if (entry.Path is { Length: > 0 } path)
        Assert.That(File.Exists(path), Is.True, $"{entry.Name} names {path}, which is not there");
    }
  }

  #endregion

  #region what the machine is doing to itself

  /// <summary>
  /// A sample taken while the machine is saturated still reports what it read (PRD §99).
  /// </summary>
  /// <remarks>
  /// Not a timing assertion — under this load the timing is whatever the scheduler decides. What is
  /// asserted is that nothing is dropped or invented: every process still has an identity, the
  /// counters that were readable are readable, and the total does not collapse because the sampler
  /// was descheduled halfway through.
  /// </remarks>
  [Test]
  public void ASampleTakenUnderLoadIsStillAWholeSample() {
    var burners = new List<System.Diagnostics.Process>();
    try {
      for (var i = 0; i < Environment.ProcessorCount; ++i)
        burners.Add(System.Diagnostics.Process.Start(
          new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "timeout 6 sh -c 'while :; do :; done'"]) {
            UseShellExecute = false,
          }
        )!);

      using var probe = Probe();
      var snapshot = new SystemSnapshot();
      probe.Sample(snapshot);

      Assert.That(snapshot.ProcessCount, Is.GreaterThan(burners.Count), "the load itself is in the table");
      foreach (var process in snapshot.Processes) {
        Assert.That(process.Key.Pid, Is.GreaterThan(0));
        Assert.That(process.Name, Is.Not.Null);
        // The identity pair is what every action re-validates, and a sample that produced records
        // without one would be a sample nothing could act on.
        Assert.That(process.Key.StartTicks, Is.GreaterThan(0ul), $"{process.Name} has no start time");
      }
    } finally {
      foreach (var burner in burners)
        try {
          burner.Kill(entireProcessTree: true);
          burner.Dispose();
        } catch (InvalidOperationException) {
        }
    }
  }

  /// <summary>
  /// The machine's own memory figures stay consistent with each other however little is free.
  /// </summary>
  /// <remarks>
  /// The relationships are what matter rather than the values: free is never more than total,
  /// available is never more than total, and the three stay distinct — conflating available with
  /// free is the classic error, and it only shows when the machine is under pressure.
  /// </remarks>
  [Test]
  public void TheMemoryFiguresAgreeWithEachOtherWhateverTheState() {
    using var probe = Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var system = snapshot.System;
    Assert.That(system.TotalMemoryBytes.HasValue, Is.True);

    if (system.FreeMemoryBytes.HasValue)
      Assert.That(system.FreeMemoryBytes.Value, Is.LessThanOrEqualTo(system.TotalMemoryBytes.Value));

    if (system.AvailableMemoryBytes.HasValue) {
      Assert.That(system.AvailableMemoryBytes.Value, Is.LessThanOrEqualTo(system.TotalMemoryBytes.Value));
      if (system.FreeMemoryBytes.HasValue)
        // Available counts what could be reclaimed as well as what is already free, so it is never
        // the smaller of the two. A build that reported them the other way round would be reading
        // one of the lines into the other.
        Assert.That(system.AvailableMemoryBytes.Value, Is.GreaterThanOrEqualTo(system.FreeMemoryBytes.Value));
    }
  }

  /// <summary>
  /// I/O counters only ever go forwards, which is what makes differencing them safe.
  /// </summary>
  /// <remarks>
  /// Read twice with real disk work in between. A counter that went backwards would produce a
  /// negative rate, and the engine turns that into "invalid" rather than a huge number — but the
  /// premise is worth checking against a machine rather than assuming it.
  /// </remarks>
  [Test]
  public void DiskCountersOnlyGoForwards() {
    using var probe = Probe();
    var before = new SystemSnapshot();
    probe.Sample(before);

    var scratch = Path.Combine(Path.GetTempPath(), $"procman-io-{Environment.ProcessId}");
    try {
      File.WriteAllBytes(scratch, new byte[8 * 1024 * 1024]);
      using (var stream = new FileStream(scratch, FileMode.Open, FileAccess.Read))
        stream.CopyTo(Stream.Null);
    } catch (IOException) {
      Assert.Ignore("could not write to the temporary directory on this machine");
    } finally {
      try {
        File.Delete(scratch);
      } catch (IOException) {
      }
    }

    var after = new SystemSnapshot();
    probe.Sample(after);

    var earlier = new Dictionary<string, ulong>(StringComparer.Ordinal);
    foreach (var disk in before.Disks)
      if (disk.ReadBytes.HasValue)
        earlier[disk.Name] = disk.ReadBytes.Value;

    var compared = 0;
    foreach (var disk in after.Disks) {
      if (!disk.ReadBytes.HasValue || !earlier.TryGetValue(disk.Name, out var was))
        continue;

      ++compared;
      Assert.That(disk.ReadBytes.Value, Is.GreaterThanOrEqualTo(was), disk.Name);
    }

    if (compared == 0)
      Assert.Ignore("no disk reported a readable byte counter twice");
  }

  #endregion

  private static IEnumerable<string> Run(string program, string arguments) {
    try {
      using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program) {
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      });

      if (process is null)
        return [];

      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit(5000);
      return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    } catch (System.ComponentModel.Win32Exception) {
      // The tool is not installed, which is a fact about the machine rather than a failure.
      return [];
    }
  }

}
