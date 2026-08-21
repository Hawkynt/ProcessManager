using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a process holds open, split by kind: sockets, files, pipes (PRD §20).
/// </summary>
/// <remarks>
/// The tally goes through the same classification as the handle view of §32 and is tested against
/// the same recorded descriptor zoo — a real process that was made to hold one of everything and
/// then recorded itself, so nothing here is invented (PRD §9.1).
/// </remarks>
[TestFixture]
public sealed class ObjectCountTests {

  private static string ZooRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-fdzoo");

  /// <summary>The recorded link targets, in descriptor order.</summary>
  private static IEnumerable<string> Targets() {
    var lines = File.ReadAllLines(Path.Combine(ZooRoot, "targets"));
    var result = new List<string>();
    foreach (var line in lines) {
      var tab = line.IndexOf('\t', StringComparison.Ordinal);
      if (tab > 0)
        result.Add(line[(tab + 1)..]);
    }

    return result;
  }

  private static DescriptorTally Zoo() {
    var tally = default(DescriptorTally);
    foreach (var target in Targets())
      // Without the open flags, which is how the sampler counts: reading them is a second file per
      // descriptor for a distinction this tally does not draw.
      tally.Add(target, Counter.NotSampledYet);

    return tally;
  }

  /// <summary>
  /// The three counts §20 asks for, over a process holding one of everything the kernel can name.
  /// </summary>
  [Test]
  public void EachKindIsCountedApartFromTheOthers() {
    var zoo = Zoo();

    Assert.Multiple(() => {
      Assert.That(zoo.Sockets.Value, Is.EqualTo(2ul), "one TCP and one unix socket");
      Assert.That(zoo.Pipes.Value, Is.EqualTo(2ul), "both ends of one pipe");
      Assert.That(zoo.Files.Value, Is.EqualTo(3ul), "two files and a directory");
    });
  }

  /// <summary>
  /// A device is not a file, a memfd is not a file, and an eventfd is not any of the three. Most of
  /// what a process holds open is none of them, which is why the handle count answers a different
  /// question from all three of these put together (PRD §5.3).
  /// </summary>
  [Test]
  public void TheThreeCountsDoNotAddUpToTheHandleCount() {
    var zoo = Zoo();
    var counted = zoo.Sockets.Value + zoo.Pipes.Value + zoo.Files.Value;
    var held = 0;
    foreach (var _ in Targets())
      ++held;

    Assert.That(held, Is.EqualTo(18));
    Assert.That(counted, Is.LessThan((ulong)held), "five devices, a memfd and five anonymous inodes");
  }

  /// <summary>A process holding nothing holds nought sockets, and that is a real nought.</summary>
  [Test]
  public void NoDescriptorsAtAllIsARealNought() {
    var empty = default(DescriptorTally);

    Assert.That(empty.Sockets.Value, Is.Zero);
    Assert.That(empty.Files.Value, Is.Zero);
    Assert.That(empty.Pipes.Value, Is.Zero);
  }

  /// <summary>
  /// Descriptors nobody could name are the other case entirely. A live <c>/proc</c> names every
  /// one of them, so a scan that saw entries and classified none of them was not reading one —
  /// and "no sockets" would be a confident zero about a process that may hold hundreds (§72.3).
  /// </summary>
  [Test]
  public void DescriptorsNobodyCouldNameAreNotNoughtOfEachKind() {
    var unreadable = default(DescriptorTally);
    for (var i = 0; i < 3; ++i)
      unreadable.Add(null, Counter.NotSampledYet);

    Assert.That(unreadable.Sockets.HasValue, Is.False);
    Assert.That(unreadable.Sockets.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    Assert.That(unreadable.Files.HasValue, Is.False);
    Assert.That(unreadable.Pipes.HasValue, Is.False);
  }

  /// <summary>
  /// One descriptor closing between the listing and the link read does not blank the column: the
  /// process really does hold the ones that were named, and a racing scan is the ordinary case for
  /// anything busy.
  /// </summary>
  [Test]
  public void OneDescriptorThatVanishedDoesNotBlankTheRest() {
    var tally = default(DescriptorTally);
    tally.Add("socket:[1]", Counter.NotSampledYet);
    tally.Add(null, Counter.NotSampledYet);

    Assert.That(tally.Sockets.Value, Is.EqualTo(1ul));
  }

  #region through the probe (PRD §5.4)

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProbeOptions Options => new() {
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  };

  private static ProcessRecord Find(SystemSnapshot snapshot, int pid) {
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        return process;

    Assert.Fail($"pid {pid} is not in the snapshot");
    return default;
  }

  private static SystemSnapshot Sample(LinuxProbeOptions options) {
    using var probe = new LinuxProbe(options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    return snapshot;
  }

  /// <summary>
  /// Nobody asking is what keeps the scan off. It is the descriptor listing plus a link resolved
  /// per descriptor — the most expensive thing the sampler can be told to do (PRD §5.4, §20).
  /// </summary>
  [Test]
  public void TheScanIsOffUntilAColumnAsksForIt() {
    var unasked = Find(Sample(Options), 1000);

    Assert.That(unasked.SocketCount.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(unasked.FileCount.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
    Assert.That(unasked.PipeCount.Reason, Is.EqualTo(UnknownReason.NotSampledYet));
  }

  /// <summary>
  /// Asking for the split counts the descriptors as well, from the one listing rather than two.
  /// A recorded tree has no link targets in it, so the kinds are unknown there while the count is
  /// perfectly real — which is exactly the pair of answers a fixture ought to produce.
  /// </summary>
  [Test]
  public void AskingForTheSplitAlsoAnswersTheCount() {
    var counted = Find(Sample(Options with { CountDescriptorKinds = true }), 1000);

    Assert.That(counted.HandleCount.Value, Is.EqualTo(3ul), "the recorded fd directory has three entries");
    Assert.That(counted.SocketCount.HasValue, Is.False, "a recorded tree carries no link targets");
    Assert.That(counted.SocketCount.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  /// <summary>
  /// Naming any of the three is the request, and it drags the descriptor count along with it —
  /// otherwise the scan would run and the column beside it would still say "not sampled".
  /// </summary>
  [Test]
  public void NamingAnyOfTheThreeTurnsTheScanOn() {
    Assert.Multiple(() => {
      Assert.That(Parse("--columns=name,socket.count").WantsDescriptorKinds, Is.True);
      Assert.That(Parse("--columns=name,file.count").WantsDescriptorKinds, Is.True);
      Assert.That(Parse("--filter=pipe.count>2").WantsDescriptorKinds, Is.True);
      Assert.That(Parse("--columns=name,ws").WantsDescriptorKinds, Is.False);

      Assert.That(Parse("--columns=name,socket.count").WantsHandleCount, Is.True);
    });
  }

  private static Hawkynt.ProcessManager.App.CommandLineOptions Parse(string argument)
    => Hawkynt.ProcessManager.App.CommandLineOptions.Parse([argument], null);

  #endregion

  #region the catalogue (PRD §5.1)

  [Test]
  public void TheThreeCountsAreSpelledTheWayThePrdNamesThem() {
    Assert.Multiple(() => {
      Assert.That(FieldRegistry.TryParse("socket.count", out var sockets), Is.True);
      Assert.That(sockets, Is.EqualTo(ProcessField.SocketCount));

      Assert.That(FieldRegistry.TryParse("file.count", out var files), Is.True);
      Assert.That(files, Is.EqualTo(ProcessField.FileCount));

      Assert.That(FieldRegistry.TryParse("pipe.count", out var pipes), Is.True);
      Assert.That(pipes, Is.EqualTo(ProcessField.PipeCount));

      // And all three are declared expensive, which is what keeps them out of a default column set.
      Assert.That(FieldRegistry.Get(ProcessField.SocketCount).Cost, Is.EqualTo(FieldCost.High));
      Assert.That(FieldRegistry.Get(ProcessField.FileCount).Cost, Is.EqualTo(FieldCost.High));
      Assert.That(FieldRegistry.Get(ProcessField.PipeCount).Cost, Is.EqualTo(FieldCost.High));
    });
  }

  #endregion

}
