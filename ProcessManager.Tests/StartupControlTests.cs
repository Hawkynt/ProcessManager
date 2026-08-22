using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Turning a login-time entry off, and on again (PRD §42).
/// </summary>
/// <remarks>
/// The rule is text and lives in Core, so most of this replays on every CI leg with no files at all.
/// The two that do touch the disk write into a directory the test made and deletes, and never into
/// the machine's own autostart — a suite that switched off somebody's session manager and then
/// passed would be a green run and a broken login.
/// </remarks>
[TestFixture]
public sealed class StartupControlTests {

  private const string _Entry = """
    [Desktop Entry]
    Type=Application
    Name=Something
    Name[de]=Irgendwas
    Exec=/usr/bin/something --daemon
    # a comment the distribution left
    X-KDE-autostart-phase=2

    [Desktop Action Configure]
    Name=Configure
    Hidden=true
    Exec=/usr/bin/something --configure
    """;

  #region the rule, as text

  [Test]
  public void DisablingAddsTheKeyTheSpecificationDefines() {
    var written = DesktopEntryEdit.Apply(_Entry, enabled: false);

    Assert.That(written, Does.Contain("Hidden=true"));
  }

  /// <summary>
  /// And it goes in the main group, not in an action group that happens to have one. Setting the
  /// action's key would turn off a right-click item and leave the autostart running.
  /// </summary>
  [Test]
  public void TheKeyGoesInTheMainGroupAndNotInAnActionGroup() {
    var written = DesktopEntryEdit.Apply(_Entry, enabled: false);
    var main = written.IndexOf("[Desktop Entry]", StringComparison.Ordinal);
    var action = written.IndexOf("[Desktop Action Configure]", StringComparison.Ordinal);
    var hidden = written.IndexOf("Hidden=true", StringComparison.Ordinal);

    Assert.That(hidden, Is.GreaterThan(main).And.LessThan(action));
  }

  /// <summary>
  /// The action group's own <c>Hidden</c> is left exactly where it was. It was not ours to touch.
  /// </summary>
  [Test]
  public void TheActionGroupKeepsItsOwnKey() {
    var written = DesktopEntryEdit.Apply(_Entry, enabled: true);
    var action = written.IndexOf("[Desktop Action Configure]", StringComparison.Ordinal);

    Assert.That(written[action..], Does.Contain("Hidden=true"));
  }

  /// <summary>
  /// Everything else survives: comments, blank lines, translations and keys this program never
  /// reads. A desktop file is somebody's, and rewriting it from a parsed model would drop most of it.
  /// </summary>
  [Test]
  public void EverythingElseInTheFileSurvives() {
    var written = DesktopEntryEdit.Apply(_Entry, enabled: false);

    Assert.Multiple(() => {
      Assert.That(written, Does.Contain("# a comment the distribution left"));
      Assert.That(written, Does.Contain("Name[de]=Irgendwas"));
      Assert.That(written, Does.Contain("X-KDE-autostart-phase=2"));
      Assert.That(written, Does.Contain("Exec=/usr/bin/something --daemon"));
    });
  }

  /// <summary>
  /// Off and on again is the file it started as, but for the trailing newline. Anything else means
  /// the switch is not a switch — it is a one-way edit somebody has to undo by hand.
  /// </summary>
  [Test]
  public void OffAndOnAgainLeavesTheEntryAsItWas() {
    var round = DesktopEntryEdit.Apply(DesktopEntryEdit.Apply(_Entry, enabled: false), enabled: true);

    Assert.That(round.TrimEnd('\r', '\n'), Is.EqualTo(_Entry.TrimEnd('\r', '\n')));
  }

  /// <summary>
  /// A file that used one line ending still uses it afterwards. Preserving every other line has to
  /// include the ends of them — this is how the source file arrives on a Windows checkout, and it is
  /// how somebody's own file may genuinely be.
  /// </summary>
  [TestCase("\n")]
  [TestCase("\r\n")]
  public void TheFilesOwnLineEndingsSurvive(string newline) {
    var original = string.Join(newline, ["[Desktop Entry]", "Type=Application", "Exec=/usr/bin/thing", ""]);
    var written = DesktopEntryEdit.Apply(original, enabled: false);

    Assert.That(written, Does.Contain("Exec=/usr/bin/thing" + newline));
    if (newline == "\n")
      Assert.That(written, Does.Not.Contain("\r"));
  }

  /// <summary>
  /// Disabling twice writes one key, not two. A file with two <c>Hidden</c> lines is one whose
  /// meaning depends on which the reader took.
  /// </summary>
  [Test]
  public void DisablingTwiceStillLeavesOneKey() {
    var twice = DesktopEntryEdit.Apply(DesktopEntryEdit.Apply(_Entry, enabled: false), enabled: false);

    Assert.That(twice.Split("Hidden=true").Length - 1, Is.EqualTo(2), "the entry's and the action's");
  }

  /// <summary>
  /// GNOME's own key is cleared when enabling. A file left carrying both would be off for a reason
  /// its Hidden line no longer gives, and nothing on screen would explain it.
  /// </summary>
  [Test]
  public void TheOtherDesktopsKeyIsClearedWhenEnabling() {
    const string GnomeStyle = """
      [Desktop Entry]
      Type=Application
      Exec=/usr/bin/thing
      X-GNOME-Autostart-enabled=false
      """;

    Assert.That(DesktopEntryEdit.Apply(GnomeStyle, enabled: true), Does.Not.Contain("X-GNOME-Autostart-enabled"));
  }

  /// <summary>
  /// A file with no main group is not a desktop entry, and one is not invented for it.
  /// </summary>
  [Test]
  public void AFileThatIsNotADesktopEntryIsNotMadeIntoOne() {
    const string Nonsense = "just some text\nand more of it\n";

    Assert.That(DesktopEntryEdit.Apply(Nonsense, enabled: false), Does.Not.Contain("Hidden"));
  }

  #endregion

  #region and what is written where

  private static string Scratch() {
    var path = Path.Combine(
      TestContext.CurrentContext.WorkDirectory,
      "autostart-" + TestContext.CurrentContext.Test.ID.Replace('-', '_')
    );

    Directory.CreateDirectory(path);
    return path;
  }

  /// <summary>
  /// A user's own entry is edited where it is. It is theirs, and there is nothing to override.
  /// </summary>
  [Test]
  public void AUsersOwnEntryIsEditedInPlace() {
    var directory = Scratch();
    try {
      var file = Path.Combine(directory, "mine.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.User, null);
      var result = new LinuxStartupControl(directory).SetEnabled(in entry, enabled: false);

      Assert.That(result.Succeeded, Is.True, result.Detail);
      Assert.That(File.ReadAllText(file), Does.Contain("Hidden=true"));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>
  /// A system entry is never written to. That file belongs to a package: the next update would
  /// overwrite whatever we did, and on most machines it cannot be written at all. What is written is
  /// a file of the same name in the user's own directory, which is the specification's override.
  /// </summary>
  [Test]
  public void ASystemEntryIsOverriddenRatherThanEdited() {
    var directory = Scratch();
    var system = Scratch() + "-system";
    Directory.CreateDirectory(system);
    try {
      var file = Path.Combine(system, "theirs.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.System, null);
      var result = new LinuxStartupControl(directory).SetEnabled(in entry, enabled: false);

      Assert.Multiple(() => {
        Assert.That(result.Succeeded, Is.True, result.Detail);
        // Byte for byte, which is stronger than looking for a key the fixture's action group has
        // anyway — and it is the actual claim: that file was never opened for writing.
        Assert.That(File.ReadAllText(file), Is.EqualTo(_Entry), "the package's file is untouched");
        Assert.That(File.Exists(Path.Combine(directory, "theirs.desktop")), Is.True, "the override was written");
      });

      // And the override is a whole copy: the specification's rule is that a user file replaces the
      // system one rather than merging with it, so a stub would lose the name and the command.
      var written = File.ReadAllText(Path.Combine(directory, "theirs.desktop"));
      Assert.That(written, Does.Contain("Exec=/usr/bin/something --daemon"));
    } finally {
      Directory.Delete(directory, recursive: true);
      Directory.Delete(system, recursive: true);
    }
  }

  /// <summary>
  /// Switching a system entry back on removes the override rather than writing "not hidden" into it.
  /// A copy left behind would freeze the entry as it was on the day it was switched off, and a new
  /// command in the package's own file would never be seen again.
  /// </summary>
  [Test]
  public void SwitchingASystemEntryBackOnRemovesTheOverride() {
    var directory = Scratch();
    var system = Scratch() + "-system2";
    Directory.CreateDirectory(system);
    try {
      var file = Path.Combine(system, "theirs.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.System, null);
      var control = new LinuxStartupControl(directory);
      control.SetEnabled(in entry, enabled: false);

      Assert.That(control.SetEnabled(in entry, enabled: true).Succeeded, Is.True);
      Assert.That(File.Exists(Path.Combine(directory, "theirs.desktop")), Is.False);
    } finally {
      Directory.Delete(directory, recursive: true);
      Directory.Delete(system, recursive: true);
    }
  }

  /// <summary>
  /// Nothing is left lying about. The file is written whole and moved into place, so a reader that
  /// arrives mid-write sees one version or the other and never half of either.
  /// </summary>
  [Test]
  public void NoHalfWrittenFileIsLeftBehind() {
    var directory = Scratch();
    try {
      var file = Path.Combine(directory, "mine.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.User, null);
      new LinuxStartupControl(directory).SetEnabled(in entry, enabled: false);

      Assert.That(Directory.GetFiles(directory, "*.procman-new"), Is.Empty);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>
  /// And what was written reads back as switched off, through the reader rather than through a
  /// second opinion about what the file means.
  /// </summary>
  [Test]
  public void TheReaderAgreesThatItIsOff() {
    var directory = Scratch();
    try {
      var file = Path.Combine(directory, "mine.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.User, null);
      new LinuxStartupControl(directory).SetEnabled(in entry, enabled: false);

      var read = XdgAutostartReader.Read(directory, [], null);
      Assert.That(read, Has.Count.EqualTo(1));
      Assert.That(read[0].Enabled, Is.False);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  #endregion

  #region removing an entry (PRD §42)

  /// <summary>
  /// The user's own file is theirs to delete, and this is the only case that deletes anything.
  /// </summary>
  [Test]
  public void AUsersOwnEntryCanBeDeleted() {
    var directory = Scratch();
    try {
      var file = Path.Combine(directory, "mine.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.User, null);
      var result = new LinuxStartupControl(directory).Delete(in entry);

      Assert.That(result.Succeeded, Is.True, result.Detail);
      Assert.That(File.Exists(file), Is.False);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>
  /// A package's file is refused, and the refusal names the thing to do instead. Deleting it looks
  /// like it worked and does not: the next update of that package puts it straight back.
  /// </summary>
  [Test]
  public void APackagesEntryIsRefusedRatherThanDeleted() {
    var system = Scratch() + "-package";
    Directory.CreateDirectory(system);
    try {
      var file = Path.Combine(system, "theirs.desktop");
      File.WriteAllText(file, _Entry);

      var entry = new StartupEntry("Something", "x", file, true, null, StartupScope.System, null);
      var result = new LinuxStartupControl(Path.Combine(system, "user")).Delete(in entry);

      Assert.Multiple(() => {
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.Refused));
        Assert.That(result.Detail, Does.Contain("switching it off"));
        Assert.That(File.Exists(file), Is.True, "and it is still there");
      });
    } finally {
      Directory.Delete(system, recursive: true);
    }
  }

  /// <summary>
  /// A unit file is refused too, for a different reason: the enablement that lists it is a symlink,
  /// and deleting the file behind it leaves the manager complaining at every login afterwards.
  /// </summary>
  [Test]
  public void AUserUnitIsRefusedRatherThanDeleted() {
    var directory = Scratch();
    try {
      var entry = new StartupEntry("agent.service", "x", "/nowhere/agent.service", true, null, StartupScope.User, null) {
        Mechanism = StartupMechanism.SystemdUserUnit,
      };

      var result = new LinuxStartupControl(directory).Delete(in entry);

      Assert.That(result.Succeeded, Is.False);
      Assert.That(result.Detail, Does.Contain("Switching it off"));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  #endregion

  #region a unit is handed to the manager that owns it (PRD §42)

  /// <summary>
  /// Nothing here starts, stops or enables anything on the machine: the control is handed a stand-in
  /// that records what it was asked and does nothing at all. What is being tested is the routing —
  /// that a unit goes to the manager and not into a desktop file the manager would never read.
  /// </summary>
  private sealed class RecordingUnits : IServiceControl {

    public bool IsAvailable { get; init; } = true;

    public string Asked { get; private set; } = string.Empty;

    public ActionResult Apply(ServiceCommand command, string unit, bool userScope = false) {
      this.Asked = $"{IServiceControl.Verb(command)} {unit} {(userScope ? "user" : "system")}";
      return ActionResult.Ok;
    }

  }

  [Test]
  public void SwitchingAUnitOffAsksTheUsersOwnManager() {
    var units = new RecordingUnits();
    var entry = new StartupEntry("agent.service", "x", "/nowhere/agent.service", true, null, StartupScope.User, null) {
      Mechanism = StartupMechanism.SystemdUserUnit,
    };

    var result = new LinuxStartupControl("/nowhere", units).SetEnabled(in entry, enabled: false);

    Assert.That(result.Succeeded, Is.True, result.Detail);
    Assert.That(units.Asked, Is.EqualTo("disable agent.service user"), "the user's manager, not the system's");
  }

  /// <summary>
  /// And with no manager to ask, it refuses and says why rather than writing a <c>Hidden=</c> key into
  /// a unit file, which nothing on the machine reads.
  /// </summary>
  [Test]
  public void WithNoManagerAUnitRefusesRatherThanBeingEdited() {
    var entry = new StartupEntry("agent.service", "x", "/nowhere/agent.service", true, null, StartupScope.User, null) {
      Mechanism = StartupMechanism.SystemdUserUnit,
    };

    var result = new LinuxStartupControl("/nowhere", new RecordingUnits { IsAvailable = false })
      .SetEnabled(in entry, enabled: false);

    Assert.That(result.Succeeded, Is.False);
    Assert.That(result.Outcome, Is.EqualTo(ActionOutcome.NotSupportedOnPlatform));
  }

  #endregion

}
