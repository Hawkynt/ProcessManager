using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// How much of a process's address space is backed by a file (PRD §16, <c>mapped.file</c>).
/// </summary>
/// <remarks>
/// The mapped size and not the resident one, which is the whole reason it is its own column: a
/// process that has mapped a large file and touched little of it is large here and small under the
/// file-backed working set, and neither number is the other's approximation.
/// <para>
/// The parser carries no platform attribute and reads bytes, so every one of these runs on every CI
/// leg (PRD §9.2).
/// </para>
/// </remarks>
[TestFixture]
public sealed class MappedFileTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static ReadOnlySpan<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

  /// <summary>
  /// The sum is over the mappings that name a file, and the arithmetic is the address range rather
  /// than anything the kernel adds up for us.
  /// </summary>
  /// <remarks>
  /// Three pages of the program plus 0x201000 of libc: 4096 × 3 + 0x200000 + 4096. Checked against
  /// the same sum taken with awk over the same file, which is what caught the bracketed pseudo-
  /// mappings being counted.
  /// </remarks>
  [Test]
  public void TheTotalIsEveryMappingThatNamesAFile() {
    var maps = File.ReadAllBytes(Path.Combine(Fixtures, "proc-desktop", "1001", "maps"));

    Assert.That(MapsParser.MappedFileBytes(maps), Is.EqualTo(2_113_536ul));
  }

  /// <summary>
  /// A larger, real capture, with a heap, a vdso, two kinds of vvar and several anonymous mappings in
  /// it — the shapes that a naive "everything after the inode" would have counted.
  /// </summary>
  [Test]
  public void ARealCaptureAgreesWithTheSameSumTakenByHand() {
    var maps = File.ReadAllBytes(Path.Combine(Fixtures, "proc-maps", "maps"));

    Assert.That(MapsParser.MappedFileBytes(maps), Is.EqualTo(5_767_168ul));
  }

  /// <summary>
  /// The kernel's own pseudo-mappings are not files and are excluded by their bracket rather than by
  /// a list of their names — the kernel keeps adding them, and <c>[vvar_vclock]</c> is one this
  /// machine has that no list written a year ago would have had.
  /// </summary>
  [Test]
  public void ThePseudoMappingsAreNotFiles() {
    var maps = Utf8("""
      7ffd00000000-7ffd00001000 rw-p 00000000 00:00 0                          [stack]
      7ffd00001000-7ffd00002000 r-xp 00000000 00:00 0                          [vdso]
      7ffd00002000-7ffd00003000 r--p 00000000 00:00 0                          [vvar_vclock]
      7ffd00003000-7ffd00004000 rw-p 00000000 00:00 0                          [heap]
      7ffd00004000-7ffd00005000 rw-p 00000000 00:00 0
      """);

    Assert.That(MapsParser.MappedFileBytes(maps), Is.EqualTo(0ul));
  }

  /// <summary>
  /// A file that has been deleted or replaced under a running process is still backing the mapping,
  /// and so is a <c>memfd</c>, which never had a name in the file system at all. Both are counted:
  /// what the column asks is what the pages come from, not whether the path still resolves.
  /// </summary>
  [Test]
  public void DeletedFilesAndMemoryDescriptorsStillCount() {
    var maps = Utf8("""
      7f0000000000-7f0000001000 r-xp 00000000 08:02 111    /usr/lib/libold.so.6 (deleted)
      7f0000001000-7f0000003000 rw-s 00000000 00:01 222    /memfd:pulseaudio (deleted)
      """);

    Assert.That(MapsParser.MappedFileBytes(maps), Is.EqualTo(3 * 4096ul));
  }

  /// <summary>
  /// A path with a space in it is one path. Taking it as a whitespace-delimited field is the bug that
  /// once reported a module nobody has, and here it would have counted the mapping anyway — so the
  /// assertion is that the total is right, which it would also be if the path were mangled. The point
  /// is that a line of this shape is not skipped.
  /// </summary>
  [Test]
  public void APathWithASpaceInItIsStillAFile() {
    var maps = Utf8("7f0000000000-7f0000002000 r-xp 00000000 08:02 111    /opt/My App/libfoo.so\n");

    Assert.That(MapsParser.MappedFileBytes(maps), Is.EqualTo(2 * 4096ul));
  }

  #region through the probe (PRD §5.4, §72.3)

  private static ProcessRecord Sample(int pid, bool readMappedFiles, int asUser) {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = asUser,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      ReadMappedFileBytes = readMappedFiles,
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

  [Test]
  public void TheProbeFillsItFromTheProcessesOwnMap() {
    var process = Sample(1001, readMappedFiles: true, asUser: 0);

    Assert.That(process.MappedFileBytes.Value, Is.EqualTo(2_113_536ul));
  }

  /// <summary>
  /// And it is not the resident file-backed figure, which comes from a different file and answers a
  /// different question. The fixture's <c>status</c> says 2048 kB of <c>RssFile</c> against 2064 kB
  /// mapped; a column that quietly reported one for the other would look entirely plausible.
  /// </summary>
  [Test]
  public void ItIsNotTheResidentFileBackedFigure() {
    var process = Sample(1001, readMappedFiles: true, asUser: 0);

    Assert.That(process.FileBackedBytes.HasValue, Is.True, "the fixture carries RssFile");
    Assert.That(process.MappedFileBytes.Value, Is.Not.EqualTo(process.FileBackedBytes.Value));
    Assert.That(process.MappedFileBytes.Value, Is.GreaterThan(process.FileBackedBytes.Value));
  }

  /// <summary>
  /// A run that did not ask says so rather than reporting nought. This is the defect this project
  /// keeps meeting: <c>default(Counter)</c> claims the value is present, and a table-wide column of
  /// noughts here would read as "nothing on this machine has a file mapped", which is true of no
  /// process that runs a program (PRD §72.3).
  /// </summary>
  [Test]
  public void ARunThatDidNotAskReportsNoValueRatherThanNought() {
    var process = Sample(1001, readMappedFiles: false, asUser: 0);

    Assert.That(process.MappedFileBytes.HasValue, Is.False);
    Assert.That(process.MappedFileBytes.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// And somebody else's map is not a nought either — <c>/proc/[pid]/maps</c> is not readable across
  /// users, and that is a refusal a privileged helper could lift rather than an absence of mappings.
  /// </summary>
  [Test]
  public void AnotherUsersMapIsRefusedRatherThanEmpty() {
    // 4242 owns nothing in the fixture, so every process in it is somebody else's.
    var process = Sample(1001, readMappedFiles: true, asUser: 4242);

    Assert.That(process.MappedFileBytes.HasValue, Is.False);
    Assert.That(process.MappedFileBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  /// <summary>
  /// A process whose map is not in the capture at all — most of the fixture — is not reported as
  /// having nothing mapped either.
  /// </summary>
  [Test]
  public void AProcessWithNoMapInTheCaptureReportsWhyRatherThanNought() {
    var process = Sample(1, readMappedFiles: true, asUser: 0);

    Assert.That(process.MappedFileBytes.HasValue, Is.False);
  }

  /// <summary>
  /// The column reads through the accessor the way both front-ends do, and sorts by the same number
  /// it shows.
  /// </summary>
  [Test]
  public void TheColumnShowsAndSortsByTheSameNumber() {
    var process = Sample(1001, readMappedFiles: true, asUser: 0);

    Assert.Multiple(() => {
      Assert.That(
        FieldAccessor.Number(ProcessField.MappedFileBytes, in process, null, 0),
        Is.EqualTo(2_113_536d)
      );
      Assert.That(
        FieldAccessor.Text(ProcessField.MappedFileBytes, in process, null, 0),
        Is.EqualTo(Humanize.Bytes(2_113_536ul))
      );
    });
  }

  #endregion

}
