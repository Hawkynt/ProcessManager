using Hawkynt.ProcessManager.App;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// <c>--minimal</c>: the mode that actually collects less (PRD §81).
/// </summary>
/// <remarks>
/// The preset it replaces did not. <c>--columns @minimal</c> names six columns and changes nothing
/// about what is read, and measured within noise of the default listing on a loaded machine; what
/// costs the time is the opt-in collectors of §5.4, and those are chosen by the switches these tests
/// exercise. A mode that claims to be cheaper and is not is worse than no mode at all, so every one
/// of them is checked rather than the flag being trusted to have reached them.
/// </remarks>
[TestFixture]
public sealed class MinimalModeTests {

  private static CommandLineOptions Parse(params string[] arguments)
    => CommandLineOptions.Parse(arguments, null);

  /// <summary>
  /// Naming an expensive column normally turns its collector on. That is the behaviour this mode has
  /// to override, so it is asserted first — a test that only checked the minimal side would pass just
  /// as well if the switches had stopped working altogether.
  /// </summary>
  [Test]
  public void WithoutItANamedColumnStillTurnsItsCollectorOn() {
    var options = Parse("--list", "--columns", "pid,handles,pss,security,hash.sha256,package");

    Assert.Multiple(() => {
      Assert.That(options.WantsHandleCount, Is.True);
      Assert.That(options.WantsProportionalSetSize, Is.True);
      Assert.That(options.WantsSecurityContext, Is.True);
      Assert.That(options.WantsImageHashes, Is.True);
      Assert.That(options.WantsPackageIdentity, Is.True);
    });
  }

  [Test]
  public void ItTurnsEveryCollectorOffHoweverTheColumnWasAskedFor() {
    var options = Parse("--list", "--minimal", "--columns", "pid,handles,pss,security,hash.sha256,package");

    Assert.Multiple(() => {
      Assert.That(options.WantsHandleCount, Is.False);
      Assert.That(options.WantsProportionalSetSize, Is.False);
      Assert.That(options.WantsSecurityContext, Is.False);
      Assert.That(options.WantsImageHashes, Is.False);
      Assert.That(options.WantsPackageIdentity, Is.False);
      Assert.That(options.WantsDescriptorKinds, Is.False);
      Assert.That(options.WantsSocketCounts, Is.False);
      Assert.That(options.WantsRuntime, Is.False);
      Assert.That(options.WantsCpuAffinity, Is.False);
      Assert.That(options.WantsIoPriority, Is.False);
      Assert.That(options.WantsCpuThrottling, Is.False);
      Assert.That(options.WantsSupplementaryGroups, Is.False);
      Assert.That(options.WantsSecurityStatus, Is.False);
      Assert.That(options.WantsMappedFileBytes, Is.False);
      Assert.That(options.WantsApplicationName, Is.False);
      Assert.That(options.WantsImageCreationTime, Is.False);
      Assert.That(options.WantsCpuPercentDelta, Is.False);
    });
  }

  /// <summary>A filter is a way of naming a column, and it is overridden the same way.</summary>
  [Test]
  public void AFilterDoesNotTurnOneOnEither()
    => Assert.That(Parse("--list", "--minimal", "--filter", "handles:>100").WantsHandleCount, Is.False);

  /// <summary>
  /// The two switches that do not go through the column test need their own gate, and are the two a
  /// refactor would silently miss.
  /// </summary>
  [Test]
  public void TheSwitchesThatAreNotColumnsAreOverriddenToo() {
    Assert.That(Parse("--list", "--gpu").WantsGpuUsage, Is.True);
    Assert.That(Parse("--list", "--minimal", "--gpu").WantsGpuUsage, Is.False);
    Assert.That(Parse("--list", "--group", "package").WantsPackageIdentity, Is.True);
    Assert.That(Parse("--list", "--minimal", "--group", "package").WantsPackageIdentity, Is.False);
  }

  /// <summary>
  /// With no columns named it opens with the six §81 asks for: what a process is, what it is doing,
  /// and whose it is.
  /// </summary>
  [Test]
  public void ItOpensWithTheSixColumns() {
    var options = Parse("--list", "--minimal");

    Assert.That(options.Fields, Is.EqualTo(CommandLineOptions.MinimalColumns));
    Assert.That(options.TerminalColumns, Is.EqualTo(CommandLineOptions.MinimalColumns));
  }

  /// <summary>
  /// And not one of them can cost a read, which is what stops the mode contradicting itself the
  /// moment somebody adds a field to the list.
  /// </summary>
  [Test]
  public void NoneOfThemCostsARead() {
    foreach (var candidate in CommandLineOptions.MinimalColumns)
      Assert.That(FieldRegistry.Get(candidate).Cost, Is.Not.EqualTo(FieldCost.High), FieldRegistry.Get(candidate).Key);
  }

  /// <summary>
  /// Columns that were named are kept. Replacing them would answer a question nobody asked, and the
  /// notice below is what makes keeping them honest.
  /// </summary>
  [Test]
  public void NamedColumnsAreKept() {
    var options = Parse("--list", "--minimal", "--columns", "pid,name");

    Assert.That(options.Fields, Is.EqualTo(new[] { ProcessField.Pid, ProcessField.Name }));
  }

  /// <summary>Whichever way round they were typed.</summary>
  [Test]
  public void TheOrderOfTheTwoSwitchesDoesNotMatter() {
    Assert.That(Parse("--list", "--minimal", "--columns", "pid,name").Fields,
      Is.EqualTo(Parse("--list", "--columns", "pid,name", "--minimal").Fields));

    Assert.That(Parse("--list", "--columns", "pid,handles", "--minimal").WantsHandleCount, Is.False);
  }

  /// <summary>
  /// A column this run will leave empty is said once, up front. Silence there would be a screen of
  /// placeholders whose cause is a switch on the same command line.
  /// </summary>
  [Test]
  public void ItSaysWhichNamedColumnsItWillLeaveEmpty() {
    var notice = Parse("--list", "--minimal", "--columns", "pid,name,handles,pss").MinimalNotice;

    Assert.That(notice, Does.Contain("handles"));
    Assert.That(notice, Does.Contain("pss"));
    Assert.That(notice, Does.Not.Contain("name"));
  }

  [Test]
  public void ThereIsNothingToSayWhenEveryNamedColumnIsFree()
    => Assert.That(Parse("--list", "--minimal", "--columns", "pid,name,cpu").MinimalNotice, Is.Null);

  [Test]
  public void AndNothingAtAllWhenTheModeWasNotAskedFor()
    => Assert.That(Parse("--list", "--columns", "pid,handles").MinimalNotice, Is.Null);

  [Test]
  public void ItIsInTheHelp()
    => Assert.That(CommandLineOptions.HelpText, Does.Contain("--minimal"));

}
