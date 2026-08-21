using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// File descriptors, against a recorded <c>fd</c>/<c>fdinfo</c> pair (PRD §9.1, §32).
/// </summary>
/// <remarks>
/// The fixture is a real process that was made to hold one of everything — a file, a directory, both
/// ends of a pipe, an epoll set, an eventfd, a timerfd, an inotify watch, a TCP socket, a unix
/// socket, a memfd and a pidfd — and then recorded itself. <c>targets</c> is what the symlinks
/// resolved to and <c>fdinfo/[n]</c> is what the kernel wrote about each; nothing here is invented.
/// </remarks>
[TestFixture]
public sealed class DescriptorTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-fdzoo");

  private static Dictionary<int, string> Targets() {
    var result = new Dictionary<int, string>();
    foreach (var line in File.ReadAllLines(Path.Combine(FixtureRoot, "targets"))) {
      var tab = line.IndexOf('\t', StringComparison.Ordinal);
      if (tab > 0)
        result[int.Parse(line[..tab])] = line[(tab + 1)..];
    }

    return result;
  }

  private static DescriptorParser.DescriptorInfo Info(int fd)
    => DescriptorParser.ParseFdInfo(File.ReadAllBytes(Path.Combine(FixtureRoot, "fdinfo", fd.ToString())));

  private static HandleKind Kind(int fd) => DescriptorParser.Classify(Targets()[fd], Info(fd).OpenFlags);

  [Test]
  public void EveryKindTheKernelNamesIsRecognised() {
    Assert.Multiple(() => {
      Assert.That(Kind(0), Is.EqualTo(HandleKind.Device), "/dev/null");
      Assert.That(Kind(5), Is.EqualTo(HandleKind.File), "/etc/hostname");
      Assert.That(Kind(6), Is.EqualTo(HandleKind.Directory), "/usr/share, opened with O_DIRECTORY");
      Assert.That(Kind(7), Is.EqualTo(HandleKind.Pipe));
      Assert.That(Kind(9), Is.EqualTo(HandleKind.EventPoll));
      Assert.That(Kind(10), Is.EqualTo(HandleKind.Event));
      Assert.That(Kind(11), Is.EqualTo(HandleKind.Timer));
      // inotify is the one subsystem that writes its name without brackets. That is a kernel
      // inconsistency, not a different kind of object.
      Assert.That(Kind(12), Is.EqualTo(HandleKind.Notify));
      Assert.That(Kind(13), Is.EqualTo(HandleKind.Socket));
      Assert.That(Kind(15), Is.EqualTo(HandleKind.SharedMemory), "a memfd");
      Assert.That(Kind(17), Is.EqualTo(HandleKind.Process), "a pidfd");
    });
  }

  [Test]
  public void ADirectoryIsToldFromAFileByItsOpenFlags() {
    // Both are plain absolute paths; only O_DIRECTORY separates them, and a fixture replay has no
    // file system to stat the target on.
    Assert.That(DescriptorParser.Classify("/usr/share", Counter.NotSampledYet), Is.EqualTo(HandleKind.File));
    Assert.That(DescriptorParser.Classify("/usr/share", Info(6).OpenFlags), Is.EqualTo(HandleKind.Directory));
  }

  [Test]
  public void TheCommonFdInfoLinesAreRead() {
    var hostname = Info(5);

    Assert.Multiple(() => {
      // The recording opened /etc/hostname and read four bytes of a nine-byte file.
      Assert.That(hostname.Position.Value, Is.EqualTo(9ul));
      Assert.That(hostname.Inode.Value, Is.EqualTo(46902ul));
      Assert.That(hostname.MountId.Value, Is.EqualTo(33ul));
      // 02100000 octal: O_LARGEFILE | O_CLOEXEC, read-only.
      Assert.That(hostname.OpenFlags.Value, Is.EqualTo(0x88000ul));
      Assert.That(DescriptorParser.DescribeAccess(hostname.OpenFlags), Is.EqualTo("r"));
    });
  }

  [Test]
  public void FlagsAreReadAsOctalBecauseThatIsHowTheKernelWritesThem() {
    // 0100002 is O_RDWR | O_LARGEFILE. Read as decimal it is 100002, which shares not one bit with
    // the right answer and still looks like a plausible number.
    var memfd = Info(15);

    Assert.That(memfd.OpenFlags.Value, Is.EqualTo(0x8002ul));
    Assert.That(DescriptorParser.DescribeAccess(memfd.OpenFlags), Is.EqualTo("rw"));
    Assert.That(DescriptorParser.DescribeFlags(memfd.OpenFlags), Is.EqualTo("O_LARGEFILE"));
  }

  [Test]
  public void AnUnnamedBitIsShownRatherThanDropped() {
    // The named list is x86-64's. On an architecture that moves a bit, or on a kernel that adds one,
    // the flag is still set and the reader is still entitled to know.
    Assert.That(DescriptorParser.DescribeFlags(Counter.Of(0x80000ul | 0x8000000ul)), Is.EqualTo("O_CLOEXEC|0x8000000"));
  }

  [Test]
  public void APidfdNamesTheProcessItHolds() {
    var pidfd = Info(17);

    Assert.That(pidfd.TargetPid.Value, Is.EqualTo(3738694ul));
    // Every other kind of descriptor refers to something that is not a process, and must say that
    // rather than report pid 0 (PRD §72.3).
    Assert.That(Info(5).TargetPid.HasValue, Is.False);
    Assert.That(Info(5).TargetPid.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  [Test]
  public void TheSocketInodeIsReadableFromBothPlacesTheKernelWritesIt() {
    var targets = Targets();

    // fdinfo's ino: line and the number in the link target are the same inode, which is what the
    // fallback for older kernels relies on.
    Assert.That(DescriptorParser.TryParsePseudoInode(targets[13], out var fromName), Is.True);
    Assert.That(fromName, Is.EqualTo(Info(13).Inode.Value));
    Assert.That(fromName, Is.EqualTo(11134442ul));
  }

  [Test]
  public void AnAnonymousInodeIsNotAPseudoInodeToJoinOn() {
    // "anon_inode:[eventfd]" has brackets and no number in them. Reading it as an inode would put a
    // zero into the socket join and match whatever happens to be there.
    Assert.That(DescriptorParser.TryParsePseudoInode("anon_inode:[eventfd]", out _), Is.False);
  }

  [Test]
  public void AnUnreadableFdInfoSaysWhyRatherThanReportingZero() {
    Assert.Multiple(() => {
      Assert.That(DescriptorParser.Refused.Position.Reason, Is.EqualTo(UnknownReason.NotPermitted));
      Assert.That(DescriptorParser.Unread.Inode.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
      // The helper relays the name and nothing else. That is a fact about this program, not about
      // the machine, and the two placeholders differ on purpose (PRD §7).
      Assert.That(DescriptorParser.NotRelayed.OpenFlags.Reason, Is.EqualTo(UnknownReason.NotImplementedHere));
    });
  }

  [Test]
  public void AnUnnamedDescriptorIsUnknownRatherThanAFile() {
    Assert.That(DescriptorParser.Classify(null, Counter.NotSampledYet), Is.EqualTo(HandleKind.Unknown));
  }

  [Test]
  public void TheTallyGroupsTheKindsTheWayTheViewDoes() {
    var handles = new List<HandleRecord>();
    foreach (var (fd, target) in Targets()) {
      var info = Info(fd);
      handles.Add(new(
        (ulong)fd,
        DescriptorParser.Classify(target, info.OpenFlags),
        target,
        DescriptorParser.DescribeAccess(info.OpenFlags),
        info.Position,
        info.OpenFlags,
        info.Inode,
        info.TargetPid,
        info.MountId,
        // The recording carries no mount table, so nothing resolves the mount id — which is a
        // different statement from "this descriptor is on no file system" and is why the id itself
        // travels beside the two fields it would have filled.
        null,
        null,
        info.Detail
      ));
    }

    var tally = HandleTally.From(handles);
    Assert.Multiple(() => {
      Assert.That(tally.Total, Is.EqualTo(18));
      // /sys/kernel/mm/transparent_hugepage/enabled and /etc/hostname.
      Assert.That(tally.Files, Is.EqualTo(2));
      Assert.That(tally.Directories, Is.EqualTo(1));
      Assert.That(tally.Sockets, Is.EqualTo(2));
      Assert.That(tally.Pipes, Is.EqualTo(2));
      // /dev/null four times — stdin, stdout, stderr and one opened by hand — and /dev/urandom once.
      Assert.That(tally.Devices, Is.EqualTo(5));
      Assert.That(tally.SharedMemory, Is.EqualTo(1));
      // epoll, eventfd, timerfd and inotify.
      Assert.That(tally.EventInterfaces, Is.EqualTo(4));
      Assert.That(tally.Total, Is.EqualTo(
        tally.Files + tally.Directories + tally.Sockets + tally.Pipes
        + tally.Devices + tally.SharedMemory + tally.EventInterfaces + tally.Other
      ));
    });
  }

  [Test]
  public void TheTallyNamesOnlyWhatIsThere() {
    var tally = new HandleTally(3, 2, 0, 1, 0, 0, 0, 0, 0);

    Assert.That(tally.Describe(), Is.EqualTo("3 descriptors — 2 files, 1 socket"));
  }

}
