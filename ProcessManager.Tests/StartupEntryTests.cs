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
      // Named empty, and never left to the default: the other half of what starts at login is read
      // from the machine's own user-unit directories, and a fixture that quietly picked those up
      // would pass or fail according to what the runner happens to have installed.
      UserUnitDirectories = [],
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
      UserUnitDirectories = [Path.Combine(Fixtures, "no-units-here")],
      EffectiveUserId = 0,
    });

    Assert.That(probe.GetStartupEntries(), Is.Empty);
  }

  /// <summary>
  /// The command is split once, where the format is known, rather than by every reader of the row.
  /// A field code is not an argument and a quoted path may contain a space, so the first word of the
  /// command is not reliably the program (PRD §42).
  /// </summary>
  [Test]
  public void TheProgramAndItsArgumentsAreSeparated() {
    var entry = One(Read(), "Plain Thing");

    Assert.That(entry.Mechanism, Is.EqualTo(StartupMechanism.XdgAutostart));
    Assert.That(entry.Executable, Is.Not.Null.And.Not.Empty);
    Assert.That(entry.Command, Does.StartWith(entry.Executable!));
  }

  #endregion

  #region the other half: systemd user units (PRD §42)

  /// <summary>
  /// A user unit that <c>default.target</c> wants is a login-time entry by any reasonable reading —
  /// it is started when the session starts — and for as long as this program looked only in the
  /// autostart directories it reported a machine with a dozen of them as having nothing at login.
  /// </summary>
  private static string UnitTree() {
    var root = Path.Combine(Path.GetTempPath(), $"procman-user-units-{Guid.NewGuid():N}");
    var vendor = Path.Combine(root, "usr");
    var mine = Path.Combine(root, "mine");
    Directory.CreateDirectory(Path.Combine(vendor, "default.target.wants"));
    Directory.CreateDirectory(Path.Combine(mine, "default.target.wants"));

    File.WriteAllText(Path.Combine(vendor, "agent.service"), """
      [Unit]
      Description=Something the session needs

      [Service]
      ExecStart=/usr/lib/agent --session
      """);

    File.WriteAllText(Path.Combine(vendor, "default.target.wants", "agent.service"), "symlink stand-in");

    // A timer in the same directory. It is a schedule rather than a thing that starts at login, and
    // listing it here would be answering a different question (PRD §5.3).
    File.WriteAllText(Path.Combine(vendor, "cleanup.timer"), "[Unit]\nDescription=Weekly tidy\n");
    File.WriteAllText(Path.Combine(vendor, "default.target.wants", "cleanup.timer"), "symlink stand-in");

    // An enablement whose unit was removed with the package. It will never run, and saying so is
    // more use than leaving it out: the symlink is still there and still somebody's to delete.
    File.WriteAllText(Path.Combine(mine, "default.target.wants", "gone.service"), "symlink stand-in");
    return root;
  }

  private static IReadOnlyList<StartupEntry> ReadUnits(string root) {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      AutostartUserDirectory = Path.Combine(Fixtures, "does-not-exist"),
      AutostartSystemDirectories = [],
      UserUnitDirectories = [Path.Combine(root, "usr"), Path.Combine(root, "mine")],
      EffectiveUserId = 0,
    });

    return probe.GetStartupEntries();
  }

  [Test]
  public void AUserUnitThatDefaultTargetWantsIsALoginEntry() {
    var root = UnitTree();
    try {
      var entry = One(ReadUnits(root), "agent.service");

      Assert.Multiple(() => {
        Assert.That(entry.Mechanism, Is.EqualTo(StartupMechanism.SystemdUserUnit));
        Assert.That(entry.Enabled, Is.True, "the symlink is the enablement");
        Assert.That(entry.Executable, Is.EqualTo("/usr/lib/agent"));
        Assert.That(entry.Arguments, Is.EqualTo("--session"));
        Assert.That(entry.Description, Is.EqualTo("Something the session needs"));
      });
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void ATimerInTheSameDirectoryIsNotALoginEntry() {
    var root = UnitTree();
    try {
      foreach (var entry in ReadUnits(root))
        Assert.That(entry.Name, Is.Not.EqualTo("cleanup.timer"));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>
  /// An enablement pointing at a unit that is not installed. Reported, and reported as one that will
  /// not run — leaving it out would hide a symlink somebody has to remove by hand.
  /// </summary>
  [Test]
  public void AnEnablementWithNoUnitFileIsListedAndSaysWhyItWillNotRun() {
    var root = UnitTree();
    try {
      var entry = One(ReadUnits(root), "gone.service");

      Assert.That(entry.Enabled, Is.False);
      Assert.That(entry.DisabledReason, Does.Contain("no unit file"));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  #endregion

}
