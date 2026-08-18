using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// XDG autostart entries (PRD §42), read from recorded directories so the rules are checked on every
/// CI leg rather than against whatever desktop the runner happens to have.
/// </summary>
[TestFixture]
public sealed class StartupEntryTests {

  private static string Fixtures
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static IReadOnlyList<StartupEntry> Read(string? desktop = "KDE") {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      AutostartUserDirectory = Path.Combine(Fixtures, "autostart-user"),
      AutostartSystemDirectories = [Path.Combine(Fixtures, "autostart-system")],
      CurrentDesktop = desktop,
      EffectiveUserId = 0,
    });

    return probe.GetStartupEntries();
  }

  private static StartupEntry One(IReadOnlyList<StartupEntry> entries, string name) {
    foreach (var entry in entries)
      if (entry.Name == name)
        return entry;

    Assert.Fail($"no entry called '{name}'");
    return default;
  }

  [Test]
  public void BothDirectoriesAreRead() {
    var entries = Read();

    Assert.That(One(entries, "Plain Thing").Scope, Is.EqualTo(StartupScope.System));
    Assert.That(One(entries, "Only Mine").Scope, Is.EqualTo(StartupScope.User));
  }

  /// <summary>
  /// The rule that makes this more than a directory listing: a user file with the same <em>file
  /// name</em> replaces the system one. Listing both would report the entry twice and, worse, report
  /// a disabled one as enabled.
  /// </summary>
  [Test]
  public void AUserFileReplacesTheSystemFileOfTheSameName() {
    var entries = Read();

    var matches = 0;
    foreach (var entry in entries)
      if (entry.Name == "Overridden Thing")
        ++matches;

    Assert.That(matches, Is.EqualTo(1), "listed once, not twice");

    var overridden = One(entries, "Overridden Thing");
    Assert.That(overridden.Scope, Is.EqualTo(StartupScope.User), "the user's file won");
    Assert.That(overridden.Enabled, Is.False, "and it turns the entry off");
    Assert.That(overridden.DisabledReason, Is.EqualTo("hidden"));
  }

  [Test]
  public void AnEntrySwitchedOffIsReportedAsSuch() {
    var entry = One(Read(), "Switched Off");

    Assert.That(entry.Enabled, Is.False);
    // Distinct from "hidden": the two are different keys and mean different things to a desktop.
    Assert.That(entry.DisabledReason, Is.EqualTo("turned off"));
  }

  #region which desktop

  [Test]
  public void AnEntryForAnotherDesktopWillNotRunHere() {
    var entry = One(Read("KDE"), "Gnome Only");

    Assert.That(entry.Enabled, Is.False);
    Assert.That(entry.DisabledReason, Does.Contain("GNOME"));
  }

  [Test]
  public void TheSameEntryRunsOnTheDesktopItIsFor() =>
    Assert.That(One(Read("GNOME"), "Gnome Only").Enabled, Is.True);

  [Test]
  public void NotShowInExcludesTheDesktopItNames() {
    Assert.That(One(Read("KDE"), "Not For KDE").Enabled, Is.False);
    Assert.That(One(Read("GNOME"), "Not For KDE").Enabled, Is.True);
  }

  /// <summary>
  /// XDG_CURRENT_DESKTOP is itself a colon-separated list — "ubuntu:GNOME" is normal — so either
  /// side of the comparison may name several.
  /// </summary>
  [Test]
  public void ADesktopThatNamesSeveralMatchesAnyOfThem() =>
    Assert.That(One(Read("ubuntu:GNOME"), "Gnome Only").Enabled, Is.True);

  /// <summary>
  /// With no desktop set we cannot tell, and guessing that a KDE entry will not run is worse than
  /// admitting it (PRD §72.3).
  /// </summary>
  [Test]
  public void WithNoDesktopKnownNothingIsExcludedForIt() {
    // An empty string is "we looked and there is nothing set", which is different from passing null
    // — null means "read XDG_CURRENT_DESKTOP", and a test that did that would assert about whatever
    // desktop the CI runner happens to have.
    Assert.That(One(Read(""), "Gnome Only").Enabled, Is.True);
    Assert.That(One(Read(""), "Not For KDE").Enabled, Is.True);
  }

  #endregion

  #region reading the file

  [Test]
  public void AFileWithNoExecIsNotAStartupEntry() {
    foreach (var entry in Read())
      Assert.That(entry.Name, Is.Not.EqualTo("Has No Exec"), "there is nothing for it to run");
  }

  [Test]
  public void AFileWithNoNameFallsBackToItsFileName() =>
    Assert.That(One(Read(), "nameless").Command, Is.EqualTo("/usr/bin/nameless"));

  /// <summary>
  /// "Name[de]" is a translation. Taking whichever came first would show a German name to an English
  /// reader, which is the kind of bug that only appears on somebody else's machine.
  /// </summary>
  [Test]
  public void ATranslatedNameIsNotMistakenForTheName() {
    var entries = Read();

    Assert.That(One(entries, "English Name").Command, Is.EqualTo("/usr/bin/translated"));
    foreach (var entry in entries)
      Assert.That(entry.Name, Is.Not.EqualTo("Deutscher Name"));
  }

  /// <summary>
  /// A .desktop file may carry action groups after the main one, with their own Name and Exec. They
  /// belong to a context-menu action, not to the entry.
  /// </summary>
  [Test]
  public void KeysAfterTheDesktopEntryGroupAreNotRead() {
    var entry = One(Read(), "Has Actions");

    Assert.That(entry.Command, Is.EqualTo("/usr/bin/real"));
    Assert.That(entry.Command, Is.Not.EqualTo("/usr/bin/not-this"));
  }

  [Test]
  public void EntriesCarryThePathOfTheFileThatDefinesThem() {
    var entry = One(Read(), "Plain Thing");

    // What "reveal configuration" opens, and what somebody has to edit to change the entry.
    Assert.That(entry.Path, Does.EndWith("plain.desktop"));
    Assert.That(File.Exists(entry.Path), Is.True);
  }

  [Test]
  public void EntriesAreSortedByName() {
    var names = new List<string>();
    foreach (var entry in Read())
      names.Add(entry.Name);

    var sorted = new List<string>(names);
    sorted.Sort(StringComparer.OrdinalIgnoreCase);
    Assert.That(names, Is.EqualTo(sorted));
  }

  [Test]
  public void AMissingDirectoryIsNotAnError() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      AutostartUserDirectory = Path.Combine(Fixtures, "does-not-exist"),
      AutostartSystemDirectories = [Path.Combine(Fixtures, "also-not-there")],
      EffectiveUserId = 0,
    });

    Assert.That(probe.GetStartupEntries(), Is.Empty);
  }

  #endregion

}
