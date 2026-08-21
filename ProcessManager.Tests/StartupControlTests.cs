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

    Assert.That(round.TrimEnd('\n'), Is.EqualTo(_Entry.TrimEnd('\n')));
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
      var result = new XdgAutostartControl(directory).SetEnabled(in entry, enabled: false);

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
      var result = new XdgAutostartControl(directory).SetEnabled(in entry, enabled: false);

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
      var control = new XdgAutostartControl(directory);
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
      new XdgAutostartControl(directory).SetEnabled(in entry, enabled: false);

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
      new XdgAutostartControl(directory).SetEnabled(in entry, enabled: false);

      var read = XdgAutostartReader.Read(directory, [], null);
      Assert.That(read, Has.Count.EqualTo(1));
      Assert.That(read[0].Enabled, Is.False);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  #endregion

}
