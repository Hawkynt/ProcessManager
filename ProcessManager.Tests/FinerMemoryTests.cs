using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The finer-grained memory figures the kernel offers (PRD §17, §5.4).
/// </summary>
/// <remarks>
/// Working set is one number covering three very different things. Splitting it is free — the lines
/// are already in <c>status</c> — and the proportional set costs a file read, which is why it is
/// asked for rather than always taken.
/// </remarks>
[TestFixture]
public sealed class FinerMemoryTests {

  private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static SystemSnapshot Sample(bool proportional) {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      EffectiveUserId = 0,
      UseProportionalSetSize = proportional,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();
    return snapshot;
  }

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Key.Pid == pid)
        return process;

    Assert.Fail($"no process {pid} in the fixture");
    return default;
  }

  #region the free split

  /// <summary>
  /// Anonymous, file-backed and shared behave completely differently under pressure: file-backed
  /// pages can be dropped and read back, anonymous ones can only go to swap. One number covering
  /// all three cannot say which kind of trouble a process is.
  /// </summary>
  [Test]
  public void TheResidentSetIsSplitByWhatBacksIt() {
    var process = Find(Sample(proportional: false), 1);

    Assert.That(process.PrivateWorkingSetBytes.Value, Is.EqualTo(8000ul * 1024), "RssAnon");
    Assert.That(process.FileBackedBytes.Value, Is.EqualTo(2048ul * 1024), "RssFile");
    Assert.That(process.SharedResidentBytes.HasValue, Is.True);
    Assert.That(process.SharedResidentBytes.Value, Is.Zero, "RssShmem, genuinely zero here");
  }

  /// <summary>The three parts are the whole, which is the check that they were read as a set.</summary>
  [Test]
  public void ThePartsAddUpToTheWorkingSet() {
    var process = Find(Sample(proportional: false), 1);
    var parts = process.PrivateWorkingSetBytes.Value
      + process.FileBackedBytes.Value
      + process.SharedResidentBytes.Value;

    Assert.That(parts, Is.EqualTo(process.WorkingSetBytes.Value));
  }

  [Test]
  public void UserAndKernelTimeAreReportedSeparately() {
    var process = Find(Sample(proportional: false), 1);

    Assert.That(process.UserTimeNs.HasValue, Is.True);
    Assert.That(process.KernelTimeNs.HasValue, Is.True);
    Assert.That(process.UserTimeNs.Value + process.KernelTimeNs.Value, Is.EqualTo(process.CpuTimeNs.Value));
  }

  #endregion

  #region the proportional set

  /// <summary>
  /// The only per-process memory figure that adds up. Working set counts each shared page in full
  /// for every process mapping it, so summing it over a machine reports several times the memory
  /// that exists.
  /// </summary>
  [Test]
  public void TheProportionalSetIsReadWhenItIsAskedFor() {
    var process = Find(Sample(proportional: true), 1);

    Assert.That(process.ProportionalBytes.Value, Is.EqualTo(3072ul * 1024));
    Assert.That(process.ProportionalSwapBytes.Value, Is.EqualTo(1024ul * 1024));
    Assert.That(process.ProportionalBytes.Value, Is.LessThan(process.WorkingSetBytes.Value),
      "a process sharing anything has a smaller proportional set than resident set");
  }

  /// <summary>
  /// <c>Pss_Anon</c>, <c>Pss_File</c> and <c>Pss_Shmem</c> all begin with "Pss" and would each match
  /// a prefix test on three characters. The colon is what makes "Pss:" mean the total — and matching
  /// one of the others would report a fraction of the memory as the whole of it.
  /// </summary>
  [Test]
  public void TheTotalIsNotConfusedWithItsOwnBreakdown() {
    var process = Find(Sample(proportional: true), 1);

    Assert.That(process.ProportionalBytes.Value, Is.Not.EqualTo(2048ul * 1024), "that is Pss_Anon");
    Assert.That(process.ProportionalBytes.Value, Is.Not.EqualTo(1024ul * 1024), "that is Pss_File");
  }

  /// <summary>
  /// The trap this project keeps meeting: <c>default(Counter)</c> is a confident zero. A process
  /// nobody read the rollup for must say nobody asked, never that it uses no memory.
  /// </summary>
  [Test]
  public void NotAskingForItIsNotTheSameAsItBeingZero() {
    var process = Find(Sample(proportional: false), 1);

    Assert.That(process.ProportionalBytes.HasValue, Is.False);
    Assert.That(process.ProportionalBytes.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(process.ProportionalSwapBytes.HasValue, Is.False);
  }

  /// <summary>
  /// <c>smaps_rollup</c> is 0400, so another user's proportional set is the ordinary answer without
  /// the elevated helper rather than a failure — and certainly not a zero.
  /// </summary>
  [Test]
  public void AnotherUsersProportionalSetIsRefusedRatherThanZero() {
    var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      PasswdPath = Path.Combine(Fixtures, "proc-desktop", "passwd"),
      // Somebody who owns none of the fixture's processes.
      EffectiveUserId = 4242,
      UseProportionalSetSize = true,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Dispose();

    var process = Find(snapshot, 1);
    Assert.That(process.ProportionalBytes.HasValue, Is.False);
    Assert.That(process.ProportionalBytes.Reason, Is.EqualTo(UnknownReason.NotPermitted));
  }

  /// <summary>
  /// It used to overwrite the private working set, so one column headed "Private WS" showed the
  /// anonymous resident set on a machine without the option and a share of every mapping on one
  /// with it — two different questions under one label.
  /// </summary>
  [Test]
  public void TheProportionalSetDoesNotOverwriteThePrivateWorkingSet() {
    var withOut = Find(Sample(proportional: false), 1);
    var with = Find(Sample(proportional: true), 1);

    Assert.That(with.PrivateWorkingSetBytes.Value, Is.EqualTo(withOut.PrivateWorkingSetBytes.Value));
    Assert.That(with.PrivateWorkingSetBytes.Value, Is.Not.EqualTo(with.ProportionalBytes.Value));
  }

  #endregion

  #region asking for it

  /// <summary>
  /// Naming the column is the request. A separate switch would only be a way to get an empty column
  /// by forgetting it (PRD §5.4).
  /// </summary>
  [Test]
  public void NamingTheColumnIsWhatTurnsTheExpensiveReadOn() {
    Assert.That(Parse("--columns=name,pss").WantsProportionalSetSize, Is.True);
    Assert.That(Parse("--columns=name,swap.pss").WantsProportionalSetSize, Is.True);
    Assert.That(Parse("--columns=name,ws").WantsProportionalSetSize, Is.False);
  }

  [Test]
  public void FilteringOnItCountsAsAskingToo() =>
    Assert.That(Parse("--filter=pss>10M").WantsProportionalSetSize, Is.True);

  /// <summary>
  /// The same rule for the descriptor count, which had no rule at all: nothing in the program ever
  /// turned the read on, so the column came back empty however it was asked for (PRD §5.4).
  /// </summary>
  [Test]
  public void NamingTheDescriptorCountTurnsItOnToo() {
    Assert.That(Parse("--columns=name,handles").WantsHandleCount, Is.True);
    Assert.That(Parse("--filter=handles>100").WantsHandleCount, Is.True);
    // And it stays off otherwise: a getdents loop over every process every sample is the most
    // expensive thing the sampler can be asked to do.
    Assert.That(Parse("--columns=name,ws").WantsHandleCount, Is.False);
  }

  private static Hawkynt.ProcessManager.App.CommandLineOptions Parse(string argument)
    => Hawkynt.ProcessManager.App.CommandLineOptions.Parse([argument], null);

  #endregion

  [Test]
  public void EveryNewFieldIsInTheRegistryWithAKeyAndAHeader() {
    foreach (var id in new[] {
      ProcessField.ProportionalSet, ProcessField.ProportionalSwap,
      ProcessField.FileBackedSet, ProcessField.SharedSet,
      ProcessField.UserTime, ProcessField.KernelTime,
    }) {
      var descriptor = FieldRegistry.Get(id);
      Assert.That(descriptor.Key, Is.Not.Empty, id.ToString());
      Assert.That(descriptor.Header, Is.Not.Empty, id.ToString());
      Assert.That(FieldRegistry.TryParse(descriptor.Key, out var parsed), Is.True, descriptor.Key);
      Assert.That(parsed, Is.EqualTo(id));
    }
  }

}
