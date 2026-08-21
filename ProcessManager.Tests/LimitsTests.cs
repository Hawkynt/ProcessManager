using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The catalogue every front-end offers signals from (PRD §25.1).
/// </summary>
[TestFixture]
public sealed class SignalCatalogueTests {

  [Test]
  public void TheNumbersAreTheOnesKillPrints() {
    // Checked against `kill -l` on the architectures this catalogue claims. The whole risk here is
    // an off-by-one in a table nobody re-reads, and an off-by-one sends SIGKILL where SIGUSR1 was
    // meant.
    Assert.Multiple(() => {
      Assert.That(Signals.ByName("SIGHUP")?.Number, Is.EqualTo(1));
      Assert.That(Signals.ByName("SIGKILL")?.Number, Is.EqualTo(9));
      Assert.That(Signals.ByName("SIGUSR1")?.Number, Is.EqualTo(10));
      Assert.That(Signals.ByName("SIGTERM")?.Number, Is.EqualTo(15));
      Assert.That(Signals.ByName("SIGCONT")?.Number, Is.EqualTo(18));
      Assert.That(Signals.ByName("SIGSTOP")?.Number, Is.EqualTo(19));
      Assert.That(Signals.ByName("SIGSYS")?.Number, Is.EqualTo(31));
    });
  }

  [Test]
  public void TheThirtyOneStandardSignalsAreAllThereAndNoneTwice() {
    var numbers = Signals.All.Select(s => s.Number).ToList();

    Assert.That(numbers, Is.EqualTo(Enumerable.Range(1, 31)));
    Assert.That(Signals.All.Select(s => s.Name).Distinct().Count(), Is.EqualTo(31));
  }

  [Test]
  public void BothSpellingsAndTheHistoricalAliasesResolve() {
    // kill -TERM and kill -SIGTERM are the same request, and every kill(1) still accepts SIGIOT.
    Assert.Multiple(() => {
      Assert.That(Signals.ByName("TERM")?.Number, Is.EqualTo(15));
      Assert.That(Signals.ByName("sigterm")?.Number, Is.EqualTo(15));
      Assert.That(Signals.ByName("SIGIOT")?.Number, Is.EqualTo(6), "an alias of SIGABRT");
      Assert.That(Signals.ByName("CLD")?.Number, Is.EqualTo(17), "an alias of SIGCHLD");
      Assert.That(Signals.ByName("SIGNOTASIGNAL"), Is.Null);
    });
  }

  /// <summary>
  /// Only the two that cannot be handled are marked as such, because that is what a confirmation
  /// promises and the promise has to be true.
  /// </summary>
  [Test]
  public void OnlyKillAndStopCannotBeDeclined() {
    var uncatchable = Signals.All.Where(s => !s.Catchable).Select(s => s.Name);
    Assert.That(uncatchable, Is.EquivalentTo(new[] { "SIGKILL", "SIGSTOP" }));
  }

  /// <summary>
  /// The sentence that matters most: the default action of most signals is to end the process, so
  /// poking a program with SIGUSR1 kills it unless it asked for the signal.
  /// </summary>
  [Test]
  public void TheConsequenceOfAnUnhandledUserSignalSaysItEndsTheProcess() {
    Assert.That(Signals.Consequence(10), Does.Contain("ends it"));
    Assert.That(Signals.Consequence(9), Does.Contain("cannot decline"));
    Assert.That(Signals.Consequence(18), Does.Contain("runs again"));
    Assert.That(Signals.Consequence(17), Does.Contain("ignore"));
  }

  [Test]
  public void RealTimeSignalsAreSendableByNumberAndHaveNoName() {
    // Deliberate: SIGRTMIN is whatever C library the target was linked against reserved for itself,
    // and a sender cannot see which. The number is the unambiguous half.
    Assert.Multiple(() => {
      Assert.That(Signals.IsSendable(34), Is.True);
      Assert.That(Signals.ByNumber(34), Is.Null);
      Assert.That(Signals.Describe(34), Does.Contain("real-time"));
      Assert.That(Signals.IsSendable(0), Is.False, "kill with nought is the existence test, not a signal");
      Assert.That(Signals.IsSendable(65), Is.False);
    });
  }

  [Test]
  public void ANumberIsAcceptedWhereverANameIs() {
    Assert.That(Signals.TryParse("15", out var byNumber), Is.True);
    Assert.That(byNumber, Is.EqualTo(15));
    Assert.That(Signals.TryParse("HUP", out var byName), Is.True);
    Assert.That(byName, Is.EqualTo(1));
    Assert.That(Signals.TryParse("0", out _), Is.False);
    Assert.That(Signals.TryParse("nonsense", out _), Is.False);
  }

}

/// <summary>
/// The catalogue of the kernel's per-process ceilings (PRD §25.2).
/// </summary>
[TestFixture]
public sealed class ResourceLimitCatalogueTests {

  [Test]
  public void TheNumbersAreTheAbisAndAreAllDistinct() {
    // RLIMIT_NOFILE being 7 is the whole reason prlimit lands on the right ceiling. Checked against
    // asm-generic/resource.h, which every architecture this catalogue claims shares.
    Assert.Multiple(() => {
      Assert.That(ResourceLimits.Of(ResourceLimitKind.CpuTime)?.Number, Is.EqualTo(0));
      Assert.That(ResourceLimits.Of(ResourceLimitKind.OpenFiles)?.Number, Is.EqualTo(7));
      Assert.That(ResourceLimits.Of(ResourceLimitKind.AddressSpace)?.Number, Is.EqualTo(9));
      Assert.That(ResourceLimits.Of(ResourceLimitKind.RealTimeTimeout)?.Number, Is.EqualTo(15));
      Assert.That(ResourceLimits.All.Select(d => d.Number), Is.EqualTo(Enumerable.Range(0, 16)));
      Assert.That(ResourceLimits.All.Select(d => d.Kind).Distinct().Count(), Is.EqualTo(16));
    });
  }

  [Test]
  public void EveryKindInTheEnumHasADefinition() {
    // The same rule the field registry enforces for columns, one layer down: a limit added to the
    // enum without a definition is one a front-end would offer and nothing could set.
    foreach (var kind in Enum.GetValues<ResourceLimitKind>())
      Assert.That(ResourceLimits.Of(kind), Is.Not.Null, kind.ToString());
  }

  [Test]
  public void BothSpellingsParse() {
    Assert.That(ResourceLimits.TryParse("nofile", out var shortForm), Is.True);
    Assert.That(shortForm, Is.EqualTo(ResourceLimitKind.OpenFiles));
    Assert.That(ResourceLimits.TryParse("RLIMIT_STACK", out var longForm), Is.True);
    Assert.That(longForm, Is.EqualTo(ResourceLimitKind.StackSize));
    Assert.That(ResourceLimits.TryParse("nonsense", out _), Is.False);
  }

  [Test]
  public void UnlimitedIsAWordAndNotANumber() {
    Assert.Multiple(() => {
      Assert.That(ResourceLimits.TryParseValue("unlimited", out var unlimited), Is.True);
      Assert.That(unlimited, Is.Null);
      Assert.That(ResourceLimits.TryParseValue("1024", out var plain), Is.True);
      Assert.That(plain, Is.EqualTo(1024ul));
      Assert.That(ResourceLimits.TryParseValue("8MiB", out var scaled), Is.True);
      Assert.That(scaled, Is.EqualTo(8ul << 20));
      Assert.That(ResourceLimits.TryParseValue("16G", out var shortScaled), Is.True);
      Assert.That(shortScaled, Is.EqualTo(16ul << 30));
      Assert.That(ResourceLimits.Format(ResourceLimitUnit.Bytes, null), Is.EqualTo("unlimited"));
    });
  }

  [Test]
  public void AValueThatWouldOverflowIsRefusedRatherThanWrapped() {
    // A limit that wrapped would be a very small one rather than a very large one, which is the
    // wrong way round for anything anybody meant by it.
    Assert.That(ResourceLimits.TryParseValue("99999999999999999999T", out _), Is.False);
    Assert.That(ResourceLimits.TryParseValue("-1", out _), Is.False);
  }

}

/// <summary>
/// Reading <c>/proc/[pid]/limits</c>, which is how the ceilings are read on a live machine and on a
/// recording alike (PRD §9.1).
/// </summary>
[TestFixture]
public sealed class ProcLimitsParserTests {

  private const string _Sample = """
    Limit                     Soft Limit           Hard Limit           Units
    Max cpu time              unlimited            unlimited            seconds
    Max file size             unlimited            unlimited            bytes
    Max data size             unlimited            unlimited            bytes
    Max stack size            8388608              unlimited            bytes
    Max core file size        0                    unlimited            bytes
    Max resident set          unlimited            unlimited            bytes
    Max processes             62733                62733                processes
    Max open files            1024                 524288               files
    Max locked memory         8388608              8388608              bytes
    Max address space         unlimited            unlimited            bytes
    Max file locks            unlimited            unlimited            locks
    Max pending signals       62733                62733                signals
    Max msgqueue size         819200               819200               bytes
    Max nice priority         0                    0
    Max realtime priority     0                    0
    Max realtime timeout      unlimited            unlimited            us
    """;

  [Test]
  public void EverySixteenLimitsComeBack() {
    Assert.That(ProcLimitsParser.Parse(_Sample), Has.Count.EqualTo(16));
  }

  [Test]
  public void ANameWithSpacesInItSurvives() {
    // "Max locked memory" is why the columns are taken from the header rather than by splitting on
    // whitespace: a split loses the name and lines everything up one column to the left.
    var limits = ProcLimitsParser.Parse(_Sample);
    var locked = limits.Single(l => l.Kind == ResourceLimitKind.LockedMemory);

    Assert.That(locked.Soft, Is.EqualTo(8388608ul));
    Assert.That(locked.Hard, Is.EqualTo(8388608ul));
  }

  [Test]
  public void UnlimitedIsNoLimitRatherThanAVeryLargeNumber() {
    var limits = ProcLimitsParser.Parse(_Sample);

    Assert.Multiple(() => {
      Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.CpuTime).Soft, Is.Null);
      Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.StackSize).Soft, Is.EqualTo(8388608ul));
      Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.StackSize).Hard, Is.Null);
    });
  }

  /// <summary>
  /// The two rows with no unit column at all, which a fixed-width slice would read past the end of.
  /// </summary>
  [Test]
  public void TheRowsWithoutAUnitAreReadLikeTheRest() {
    var limits = ProcLimitsParser.Parse(_Sample);

    Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.NiceCeiling).Soft, Is.EqualTo(0ul));
    Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.RealTimePriority).Hard, Is.EqualTo(0ul));
  }

  [Test]
  public void ASoftLimitAtItsCeilingSaysSo() {
    var limits = ProcLimitsParser.Parse(_Sample);

    Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.Processes).IsAtItsHardLimit, Is.True);
    Assert.That(limits.Single(l => l.Kind == ResourceLimitKind.OpenFiles).IsAtItsHardLimit, Is.False);
  }

  [Test]
  public void SomethingThatIsNotThatFileYieldsNothingRatherThanRubbish() {
    // No header means no column positions, and guessing them would work until a value grew wide
    // enough to touch its neighbour — and then be silently wrong.
    Assert.That(ProcLimitsParser.Parse("hello\nworld"), Is.Empty);
    Assert.That(ProcLimitsParser.Parse(string.Empty), Is.Empty);
    Assert.That(ProcLimitsParser.Parse(null), Is.Empty);
  }

  /// <summary>
  /// A value that is neither a number nor the word is left out, because reporting it as unlimited
  /// would be the opposite of the truth.
  /// </summary>
  [Test]
  public void ARowThatCannotBeReadIsOmittedRatherThanCalledUnlimited() {
    var damaged = _Sample.Replace("Max open files            1024", "Max open files            ????", StringComparison.Ordinal);
    var limits = ProcLimitsParser.Parse(damaged);

    Assert.That(limits.Any(l => l.Kind == ResourceLimitKind.OpenFiles), Is.False);
    Assert.That(limits, Has.Count.EqualTo(15), "and the rest are unaffected");
  }

}
