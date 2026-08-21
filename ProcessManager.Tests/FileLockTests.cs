using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Who is waiting for a file lock, and who is holding it (PRD §33, §91).
/// </summary>
/// <remarks>
/// The lines below are what this machine's kernel actually wrote while one <c>flock</c> waited for
/// another — the pair with id 56 was copied out of <c>/proc/locks</c> rather than invented, because
/// a fixture written from the documentation tests the documentation.
/// </remarks>
[TestFixture]
public sealed class FileLockTests {

  private const string _Table = """
    1: POSIX  ADVISORY  READ 725565 00:1e:834279 1073741826 1073742335
    2: POSIX  ADVISORY  WRITE 725565 00:1e:221482 1073741824 1073742335
    56: FLOCK  ADVISORY  WRITE 3391156 00:34:3771978 0 EOF
    56: -> FLOCK  ADVISORY  WRITE 3391178 00:34:3771978 0 EOF
    57: OFDLCK ADVISORY  READ -1 00:34:99 0 EOF
    """;

  [Test]
  public void EveryLineIsRead()
    => Assert.That(FileLockParser.Parse(_Table), Has.Count.EqualTo(5));

  /// <summary>
  /// The arrow is the whole feature: it marks an entry as somebody waiting rather than holding, and
  /// gives it the id of the lock it is queued behind.
  /// </summary>
  [Test]
  public void TheArrowMarksAWaiterAndNotAHolder() {
    var locks = FileLockParser.Parse(_Table);

    Assert.That(locks[2].Blocked, Is.False, "the holder");
    Assert.That(locks[3].Blocked, Is.True, "the waiter");
    Assert.That(locks[3].Id, Is.EqualTo(locks[2].Id), "same lock");
  }

  [Test]
  public void TheFieldsAreWhatTheKernelWrote() {
    var waiter = FileLockParser.Parse(_Table)[3];

    Assert.Multiple(() => {
      Assert.That(waiter.Kind, Is.EqualTo("FLOCK"));
      Assert.That(waiter.Exclusive, Is.True);
      Assert.That(waiter.Pid, Is.EqualTo(3391178));
      Assert.That(waiter.Device, Is.EqualTo("00:34"));
      Assert.That(waiter.Inode, Is.EqualTo(3771978ul));
    });
  }

  /// <summary>
  /// And the chain reads out: the waiting process, and the one it is queued behind.
  /// </summary>
  [Test]
  public void TheChainNamesWhoIsBlockingWhom() {
    var blocked = FileLockParser.BlockedBy(FileLockParser.Parse(_Table));

    Assert.That(blocked, Has.Count.EqualTo(1));
    Assert.That(blocked[3391178], Is.EqualTo(3391156));
  }

  /// <summary>
  /// A holder with nobody queued behind it is not in the answer. Most locks on a machine are
  /// uncontended and reporting them would bury the one that matters.
  /// </summary>
  [Test]
  public void AnUncontendedLockIsNotAWaitChain() {
    var blocked = FileLockParser.BlockedBy(FileLockParser.Parse(_Table));

    Assert.That(blocked.ContainsKey(725565), Is.False);
  }

  /// <summary>
  /// A waiter whose holder is not in the table is left out rather than reported as blocked by
  /// nobody. That happens when the holder exits between the kernel writing the two lines, and
  /// "blocked by pid 0" is a statement about a process that does not exist (PRD §72.3).
  /// </summary>
  [Test]
  public void AWaiterWithNoHolderIsNotReported() {
    var orphan = "99: -> FLOCK  ADVISORY  WRITE 4242 00:34:1 0 EOF\n";

    Assert.That(FileLockParser.BlockedBy(FileLockParser.Parse(orphan)), Is.Empty);
  }

  /// <summary>
  /// And a process is never reported as waiting for itself, which a lock it already holds would
  /// otherwise produce — sending a reader to look at the process they were already looking at.
  /// </summary>
  [Test]
  public void NobodyIsBlockedByThemselves() {
    var itself = """
      7: FLOCK  ADVISORY  WRITE 500 00:34:1 0 EOF
      7: -> FLOCK  ADVISORY  WRITE 500 00:34:1 0 EOF
      """;

    Assert.That(FileLockParser.BlockedBy(FileLockParser.Parse(itself)), Is.Empty);
  }

  /// <summary>
  /// A line in a shape this does not know is skipped rather than failing the file. The format has
  /// gained columns before — open-file-description locks arrived in 3.15 — and a parser that gives
  /// up on the whole table when one line surprises it turns a newer kernel into a missing feature.
  /// </summary>
  [Test]
  public void AnUnfamiliarLineDoesNotCostTheRestOfTheTable() {
    var mixed = """
      1: SOMETHINGNEW
      56: FLOCK  ADVISORY  WRITE 100 00:34:5 0 EOF
      56: -> FLOCK  ADVISORY  WRITE 200 00:34:5 0 EOF
      not a lock line at all
      """;

    Assert.That(FileLockParser.BlockedBy(FileLockParser.Parse(mixed))[200], Is.EqualTo(100));
  }

  /// <summary>
  /// The kernel's own word for the kind is kept rather than mapped onto a common vocabulary. A POSIX
  /// lock belongs to a process and an open-file-description lock to a descriptor, and they behave
  /// differently across <c>fork</c> and across a second <c>open</c> of the same file (PRD §5.3).
  /// </summary>
  [Test]
  public void TheKernelsOwnWordForTheKindSurvives() {
    var kinds = new List<string>();
    foreach (var entry in FileLockParser.Parse(_Table))
      kinds.Add(entry.Kind);

    Assert.That(kinds, Does.Contain("POSIX").And.Contain("FLOCK").And.Contain("OFDLCK"));
  }

  [Test]
  public void AnEmptyTableIsNotAFailure()
    => Assert.That(FileLockParser.Parse(string.Empty), Is.Empty);


  #region and the reader that opens the file

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  /// <summary>
  /// The probe reads the table and answers the same thing the parser does, through the file rather
  /// than through a string. The parse and the read are separate mistakes to make.
  /// </summary>
  [Test]
  public void TheProbeReadsTheTableFromTheFile() {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    try {
      var waits = probe.DescribeLockWaits();

      Assert.That(waits, Has.Count.EqualTo(1));
      Assert.That(waits[1001], Is.EqualTo(1000));
    } finally {
      probe.Dispose();
    }
  }

  /// <summary>
  /// A machine whose kernel has no such file answers "nothing that we know of" rather than throwing.
  /// A missing file is an ordinary state — a container with a trimmed <c>/proc</c>, an older kernel —
  /// and it must not take the properties window down with it.
  /// </summary>
  [Test]
  public void AProcWithNoSuchFileIsNotAFailure() {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-minimal-nonexistent"),
      EffectiveUserId = 0,
      ClockTicksPerSecond = 100,
      PageSize = 4096,
    });

    try {
      Assert.That(probe.DescribeLockWaits(), Is.Empty);
    } finally {
      probe.Dispose();
    }
  }

  #endregion

}
