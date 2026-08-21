using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The memory map (§34) and the security context (§36) against the machine running the test rather
/// than against a recording (PRD §99).
/// </summary>
/// <remarks>
/// The fixture tests prove the parsers; these prove the parsers are handed the right files, and they
/// hold the result against the kernel's own answer rather than against a number somebody once saw.
/// Every process acted on here is one this test started and stops again — never one it found.
/// </remarks>
[TestFixture]
[Platform("Linux", Reason = "Reads the live machine.")]
public sealed class LinuxInspectionTests {

  private static LinuxProbe Probe() => new(new LinuxProbeOptions());

  private static LinuxProcessActions Actions() => new(new());

  /// <summary>
  /// A process of this test's own, running the program it was asked for and no longer moving.
  /// </summary>
  /// <remarks>
  /// Both halves are needed, and the second was learnt the hard way. A launch returns as soon as the
  /// child exists, and the child is a shell until it reaches its <c>exec</c> (PRD §25.1) — so the
  /// first wait is for the program to be the right program. But <c>comm</c> changes <em>at</em> the
  /// exec, before the dynamic loader has mapped anything, so waiting only for that leaves the address
  /// space still growing: the test read the kernel's file and the probe's answer either side of libc
  /// being mapped, and reported "21 lines, 25 regions" as though the parser were dropping mappings.
  /// </remarks>
  private static LaunchResult StartSleep() {
    var launched = Actions().Launch(new("/bin/sleep", ["30"]));
    Assert.That(launched.Outcome.Succeeded, Is.True, launched.Outcome.Detail);
    Settle(launched.Pid, "sleep");
    SettleMap(launched.Pid);
    return launched;
  }

  /// <summary>Waits until the launched process is the program that was asked for.</summary>
  private static void Settle(int pid, string program) {
    for (var attempt = 0; attempt < 500; ++attempt) {
      try {
        if (File.ReadAllText($"/proc/{pid}/comm").Trim() == program)
          return;
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }

      Thread.Sleep(10);
    }

    Assert.Fail($"process {pid} never became {program}");
  }

  /// <summary>
  /// Waits until two readings of the process's map a moment apart are the same file.
  /// </summary>
  /// <remarks>
  /// Which is as close to "this process has finished starting" as anything outside it can get. A
  /// <c>sleep</c> reaches it in a few milliseconds and then never moves again, which is the whole
  /// reason it is what these tests launch.
  /// </remarks>
  private static void SettleMap(int pid) {
    var previous = string.Empty;
    for (var attempt = 0; attempt < 500; ++attempt) {
      string current;
      try {
        current = File.ReadAllText($"/proc/{pid}/maps");
      } catch (IOException) {
        current = string.Empty;
      }

      if (current.Length > 0 && current == previous)
        return;

      previous = current;
      Thread.Sleep(10);
    }

    Assert.Fail($"the address space of {pid} never stopped changing");
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

  #region the memory map

  /// <summary>
  /// The map of a process this test started, held against the file the kernel wrote for it.
  /// </summary>
  /// <remarks>
  /// Against the count of header lines rather than against a number: how many mappings a
  /// <c>sleep</c> has depends on the libc it was linked against and on whether the loader used a gap.
  /// The contract is that every line becomes a row and none is folded away.
  /// </remarks>
  [Test]
  public void EveryLineOfTheKernelsMapBecomesARow() {
    var launched = StartSleep();

    try {
      var lines = 0;
      foreach (var line in File.ReadAllLines($"/proc/{launched.Pid}/maps"))
        if (line.Length > 0)
          ++lines;

      var reading = Probe().GetMemoryRegions(launched.Key);
      Assert.Multiple(() => {
        Assert.That(reading.State, Is.EqualTo(MemoryMapState.Available));
        Assert.That(reading.Regions, Has.Count.EqualTo(lines));
        // Our own process, so the page-table walk is permitted and the counters are real.
        Assert.That(reading.Detailed, Is.True);
      });
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// The two things every process has, wherever the kernel put them.
  /// </summary>
  /// <remarks>
  /// Asserting the addresses would be asserting this machine's layout; asserting that the classifier
  /// recognises what the kernel labelled is asserting the contract.
  /// </remarks>
  [Test]
  public void TheKernelsOwnLabelsAreRecognised() {
    var launched = StartSleep();

    try {
      var kinds = new Dictionary<string, MemoryRegionKind>(StringComparer.Ordinal);
      foreach (var region in Probe().GetMemoryRegions(launched.Key).Regions)
        if (region.Path is { Length: > 0 } path && path[0] == '[')
          kinds[path] = region.Kind;

      Assert.Multiple(() => {
        Assert.That(kinds, Does.ContainKey("[stack]"));
        Assert.That(kinds["[stack]"], Is.EqualTo(MemoryRegionKind.Stack));
        Assert.That(kinds, Does.ContainKey("[vdso]"));
        Assert.That(kinds["[vdso]"], Is.EqualTo(MemoryRegionKind.KernelProvided));
        // Whatever else this kernel puts in a process — [vvar], [vvar_vclock], [uprobes] — must not
        // have been classified as a file on disk.
        foreach (var (path, kind) in kinds)
          Assert.That(kind, Is.Not.EqualTo(MemoryRegionKind.FileBacked), path);
      });
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// The resident total, against what the kernel says the same process's resident set is.
  /// </summary>
  /// <remarks>
  /// Loose on purpose, and the looseness is the finding rather than a hedge: the two figures are read
  /// at two moments and <c>smaps</c> is not atomic — the kernel walks the page table one mapping at a
  /// time and the process may fault a page in between two of them. What must hold is that summing the
  /// per-mapping figures gives the same order of answer as the one-line total, because that is what
  /// says the counters were charged to the mapping they belong to rather than to the one below.
  /// </remarks>
  [Test]
  public void TheMappingsAddUpToTheProcessesResidentSet() {
    var launched = StartSleep();

    try {
      var summed = 0ul;
      foreach (var region in Probe().GetMemoryRegions(launched.Key).Regions)
        summed += region.ResidentBytes.GetValueOrDefault();

      var reported = 0ul;
      foreach (var line in File.ReadAllLines($"/proc/{launched.Pid}/status"))
        if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
          reported = ulong.Parse(line["VmRSS:".Length..].Replace("kB", string.Empty).Trim(), System.Globalization.CultureInfo.InvariantCulture) * 1024;

      Assert.That(reported, Is.GreaterThan(0ul), "the kernel reports a resident set for a live process");
      Assert.That(summed, Is.EqualTo(reported).Within(20).Percent);
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// A process that is not there.
  /// </summary>
  /// <remarks>
  /// The one case where an empty list would be actively wrong: it is indistinguishable from a kernel
  /// thread, and a page that showed one as the other would be describing a process that has ended as
  /// one with nothing in it.
  /// </remarks>
  [Test]
  public void AProcessThatHasEndedSaysSoRatherThanComingBackEmpty() {
    var launched = StartSleep();
    Stop(launched.Pid);

    // The kernel reaps it asynchronously, so this is not immediate on a loaded machine.
    var reading = MemoryMapReading.NotImplemented;
    for (var attempt = 0; attempt < 50; ++attempt) {
      reading = Probe().GetMemoryRegions(launched.Key);
      if (reading.State != MemoryMapState.Available)
        break;

      Thread.Sleep(20);
    }

    Assert.That(reading.State, Is.EqualTo(MemoryMapState.Gone));
    Assert.That(reading.Explain(), Is.Not.Empty);
  }

  #endregion

  #region the security context

  /// <summary>
  /// The supplementary groups of a process this test started, against the kernel's own line.
  /// </summary>
  /// <remarks>
  /// The numbers rather than the names: which groups this account is in is a fact about whoever runs
  /// the test, and asserting any particular one would be encoding one machine's configuration as the
  /// contract. What is asserted is that the list is the kernel's list.
  /// </remarks>
  [Test]
  public void TheGroupListIsTheKernelsGroupList() {
    var launched = StartSleep();

    try {
      var expected = new List<int>();
      foreach (var line in File.ReadAllLines($"/proc/{launched.Pid}/status"))
        if (line.StartsWith("Groups:", StringComparison.Ordinal))
          foreach (var field in line["Groups:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            expected.Add(int.Parse(field, System.Globalization.CultureInfo.InvariantCulture));

      if (Probe().DescribeSecurity(launched.Key) is not { } security) {
        Assert.Fail("a live process has a security context");
        return;
      }


      var read = new List<int>();
      foreach (var group in security.SupplementaryGroups)
        read.Add(group.Id);

      Assert.Multiple(() => {
        Assert.That(read, Is.EqualTo(expected));
        // The kernel wrote the line, so the reason is that it was read — even when the line is empty,
        // which is what a process in no supplementary group looks like.
        Assert.That(security.GroupsReason, Is.EqualTo(UnknownReason.None));
      });
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// Group numbers resolved to names, where this machine's own file has them.
  /// </summary>
  /// <remarks>
  /// Against <c>/etc/group</c> rather than against a name: a machine whose groups come from LDAP has
  /// none of them in the file, and the honest answer there is a number. What must hold is that a name
  /// appears exactly when the file carries one, and that it is the right name.
  /// </remarks>
  [Test]
  public void AGroupIsNamedWhenTheMachinesOwnFileNamesIt() {
    var names = new Dictionary<int, string>();
    foreach (var line in File.ReadAllLines("/etc/group")) {
      var fields = line.Split(':');
      if (fields.Length >= 3 && int.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out var gid))
        names[gid] = fields[0];
    }

    var launched = StartSleep();

    try {
      if (Probe().DescribeSecurity(launched.Key) is not { } security) {
        Assert.Fail("a live process has a security context");
        return;
      }

      if (security.SupplementaryGroups.Count == 0)
        Assert.Ignore("this account is in no supplementary group, so there is nothing to resolve");

      Assert.Multiple(() => {
        foreach (var group in security.SupplementaryGroups)
          Assert.That(group.Name, Is.EqualTo(names.TryGetValue(group.Id, out var name) ? name : null), $"gid {group.Id}");
      });
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>
  /// The LSM label, or the reason there is none.
  /// </summary>
  /// <remarks>
  /// A machine with no security module loaded is the ordinary case on a plain distribution, and it
  /// fails the read with <c>EINVAL</c> rather than producing an empty file. So the contract is that
  /// the two are never confused: a label with a reason, or a reason with no label, and never a
  /// missing label reported as an unconfined process.
  /// </remarks>
  [Test]
  public void TheLabelAndTheReasonThereIsNoneAreNeverBothAbsent() {
    var launched = StartSleep();

    try {
      if (Probe().DescribeSecurity(launched.Key) is not { } security) {
        Assert.Fail("a live process has a security context");
        return;
      }


      if (security.Label is { Length: > 0 }) {
        Assert.That(security.LabelReason, Is.EqualTo(UnknownReason.None));
        // Whatever this machine's module writes, it is what the file says.
        Assert.That(File.ReadAllText($"/proc/{launched.Pid}/attr/current").TrimEnd('\0', '\n'), Is.EqualTo(security.Label));
      } else
        Assert.That(security.LabelReason, Is.Not.EqualTo(UnknownReason.None), "no label has to come with a reason");
    } finally {
      Stop(launched.Pid);
    }
  }

  /// <summary>A process that is not there has no security context, rather than an empty one.</summary>
  [Test]
  public void AProcessThatHasEndedHasNoSecurityContext() {
    var launched = StartSleep();
    Stop(launched.Pid);

    ProcessSecurity? security = null;
    for (var attempt = 0; attempt < 50; ++attempt) {
      security = Probe().DescribeSecurity(launched.Key);
      if (security is null)
        break;

      Thread.Sleep(20);
    }

    Assert.That(security, Is.Null);
  }

  #endregion

}
