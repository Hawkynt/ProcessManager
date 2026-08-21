using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The counts of I/O operations and the shareable half of a working set (PRD §16, §17).
/// </summary>
/// <remarks>
/// Bytes moved and calls made are different questions and a process can be heavy in one and light in
/// the other: a program reading a gigabyte in one call and one reading it a byte at a time cost the
/// machine very different amounts, and only the call count tells them apart.
/// </remarks>
[TestFixture]
public sealed class OperationCountTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static ProcessRecord Sample(int pid) {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();

    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"no process {pid} in the fixture");
    return default;
  }

  /// <summary>
  /// The call counts come from <c>syscr</c> and <c>syscw</c>, which are the two lines in that file
  /// that count calls rather than bytes.
  /// </summary>
  [Test]
  public void TheOperationCountsAreTheCallsAndNotTheBytes() {
    var process = Sample(1);

    Assert.Multiple(() => {
      Assert.That(process.ReadOperations.Value, Is.EqualTo(100ul), "syscr");
      Assert.That(process.WriteOperations.Value, Is.EqualTo(50ul), "syscw");
      // The bytes are in the same file and are much larger. Reading one into the other would give a
      // process credit for eight million calls it never made.
      Assert.That(process.ReadBytes.Value, Is.GreaterThan(process.ReadOperations.Value));
    });
  }

  /// <summary>
  /// The shareable working set is the resident memory somebody else could also be holding: what a
  /// file backs, plus what a shared segment does. It is the part that does not come back when the
  /// process exits, which is the whole reason for separating it from the private half.
  /// </summary>
  [Test]
  public void TheShareableWorkingSetIsTheFileBackedAndSharedHalves() {
    var process = Sample(1);

    // 2048 kB file-backed, no shared segments in this fixture. Computed by the accessor rather than
    // stored on the record, so it is asked for the way every front-end asks for it.
    Assert.That(
      FieldAccessor.Number(ProcessField.ShareableWorkingSet, in process, null, 0),
      Is.EqualTo(2048d * 1024)
    );
  }

  /// <summary>
  /// And it agrees with the two figures it is made of, which are read from the same lines of the same
  /// file — the thing the read was rearranged to guarantee.
  /// </summary>
  [Test]
  public void ItAgreesWithTheHalvesItIsMadeOf() {
    var process = Sample(1);

    Assert.That(
      FieldAccessor.Number(ProcessField.ShareableWorkingSet, in process, null, 0),
      Is.EqualTo((double)(process.FileBackedBytes.Value + process.SharedResidentBytes.Value))
    );
  }

  /// <summary>
  /// Private and shareable together are the whole resident set. If they did not add up, one of them
  /// would be counting something the other already had.
  /// </summary>
  [Test]
  public void ThePrivateAndShareableHalvesAreTheWholeResidentSet() {
    var process = Sample(1);

    Assert.That(
      process.PrivateWorkingSetBytes.Value + (ulong)FieldAccessor.Number(ProcessField.ShareableWorkingSet, in process, null, 0)!.Value,
      Is.EqualTo(process.WorkingSetBytes.Value)
    );
  }

  /// <summary>
  /// A counter nobody read says so. The trap this project keeps meeting is that an unread
  /// <see cref="Counter"/> defaults to a present nought, and a process that had made no calls and one
  /// whose file could not be opened would then look identical (PRD §72.3).
  /// </summary>
  [Test]
  public void AProcessWhoseCountsCouldNotBeReadDoesNotReportNought() {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      // Somebody who owns none of the fixture's processes, so io is refused.
      EffectiveUserId = 4242,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();

    foreach (var process in snapshot.Processes) {
      if (process.Pid != 1)
        continue;

      Assert.That(process.ReadOperations.HasValue, Is.False, "unread, not nought");
      return;
    }

    Assert.Fail("no process 1 in the fixture");
  }

}
