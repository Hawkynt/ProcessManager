using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// systemd services (PRD §41), read from a unit tree and a cgroup tree built for the test.
/// </summary>
/// <remarks>
/// Built rather than committed because one of the cases is a symlink to <c>/dev/null</c>, and a
/// symlink is not a thing to check into a repository that gets cloned on Windows. Everything else is
/// plain files, so the whole tree is made in a temporary directory and torn down after.
/// </remarks>
[TestFixture]
public sealed class ServiceTests {

  private string _root = string.Empty;
  private string _vendor = string.Empty;
  private string _admin = string.Empty;
  private string _wants = string.Empty;
  private string _cgroup = string.Empty;
  private bool _symlinksWork;

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"procman-services-{Guid.NewGuid():N}");
    this._vendor = Path.Combine(this._root, "usr", "lib", "systemd", "system");
    this._admin = Path.Combine(this._root, "etc", "systemd", "system");
    this._wants = Path.Combine(this._admin, "multi-user.target.wants");
    this._cgroup = Path.Combine(this._root, "cgroup");

    Directory.CreateDirectory(this._vendor);
    Directory.CreateDirectory(this._admin);
    Directory.CreateDirectory(this._wants);

    Unit(this._vendor, "sshd.service", """
      [Unit]
      Description=OpenSSH Daemon

      [Service]
      Type=notify
      ExecStart=/usr/bin/sshd -D
      Restart=always

      [Install]
      WantedBy=multi-user.target
      """);

    // The administrator's copy replaces the vendor's entirely, which is how a packaged unit is
    // overridden. Both files exist; only one may be reported.
    Unit(this._vendor, "overridden.service", """
      [Unit]
      Description=The vendor's version

      [Service]
      ExecStart=/usr/bin/vendor
      """);

    Unit(this._admin, "overridden.service", """
      [Unit]
      Description=The administrator's version

      [Service]
      ExecStart=/usr/bin/administrator
      """);

    // Description belongs to [Unit] and ExecStart to [Service]. A parser that ignores sections picks
    // up whichever came first, and [Install] sits after both.
    Unit(this._vendor, "sections.service", """
      [Service]
      Description=Wrong section, must be ignored

      [Unit]
      Description=The real description

      [Service]
      ExecStart=/usr/bin/real
      ExecStart=/usr/bin/second-command-of-a-oneshot

      [Install]
      ExecStart=/usr/bin/also-wrong
      """);

    Unit(this._vendor, "socket-activated.service", """
      [Unit]
      Description=Started on demand

      [Service]
      ExecStart=/usr/bin/on-demand
      """);

    Unit(this._vendor, "user@.service", """
      [Unit]
      Description=User Manager

      [Service]
      ExecStart=/usr/lib/systemd/systemd --user
      """);

    Unit(this._vendor, "masked.service", "[Unit]\nDescription=Should never be read\n");

    // sshd is wanted at boot; the others are not, which is a different thing from being disabled.
    File.WriteAllText(Path.Combine(this._wants, "sshd.service"), "symlink stand-in");

    // A masked unit is a symlink to /dev/null. Windows may refuse to make one without developer
    // mode, in which case the test that asserts on it says so rather than failing.
    try {
      File.CreateSymbolicLink(Path.Combine(this._admin, "masked.service"), "/dev/null");
      this._symlinksWork = true;
    } catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
      this._symlinksWork = false;
    }

    // The cgroup tree, with each of the three shapes a running service takes.
    Cgroup(Path.Combine(this._cgroup, "system.slice", "sshd.service"), "4242\n4243\n");
    // Nested in a slice of its own, the way cups is.
    Cgroup(Path.Combine(this._cgroup, "system.slice", "system-cups.slice", "cups.service"), "1090\n");
    // Its own group is empty and the processes are in a child, the way systemd-udevd's are.
    Cgroup(Path.Combine(this._cgroup, "system.slice", "udev.service"), string.Empty);
    Cgroup(Path.Combine(this._cgroup, "system.slice", "udev.service", "workers"), "777\n");
    // An instance of a template, under the user's own slice rather than the system one.
    Cgroup(Path.Combine(this._cgroup, "user.slice", "user-1000.slice", "user@1000.service"), "5555\n");
    // Started once and finished: the directory survives with nothing in it.
    Cgroup(Path.Combine(this._cgroup, "system.slice", "overridden.service"), string.Empty);
  }

  [OneTimeTearDown]
  public void RemoveTree() {
    if (Directory.Exists(this._root))
      Directory.Delete(this._root, recursive: true);
  }

  private static void Unit(string directory, string name, string content)
    => File.WriteAllText(Path.Combine(directory, name), content);

  private static void Cgroup(string directory, string procs) {
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "cgroup.procs"), procs);
  }

  private IReadOnlyList<ServiceRecord> Services() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      UnitDirectories = [this._vendor, this._admin],
      WantsDirectories = [this._wants],
      ServiceCgroupRoot = this._cgroup,
      EffectiveUserId = 0,
    });

    return probe.GetServices();
  }

  private ServiceRecord One(string name) {
    foreach (var service in this.Services())
      if (service.Name == name)
        return service;

    Assert.Fail($"no service called '{name}'");
    return default;
  }

  #region reading a unit

  [Test]
  public void EveryFieldOfAUnitLands() {
    var sshd = this.One("sshd.service");

    Assert.That(sshd.Description, Is.EqualTo("OpenSSH Daemon"));
    Assert.That(sshd.Command, Is.EqualTo("/usr/bin/sshd -D"));
    Assert.That(sshd.RestartPolicy, Is.EqualTo("always"));
    Assert.That(sshd.Path, Does.EndWith("sshd.service"));
  }

  /// <summary>
  /// Description is a [Unit] key and ExecStart a [Service] one. Reading keys without regard to
  /// section takes whichever came first — and the fixture puts a decoy Description in [Service]
  /// before the real one.
  /// </summary>
  [Test]
  public void KeysAreReadFromTheSectionTheyBelongTo() {
    var sections = this.One("sections.service");

    Assert.That(sections.Description, Is.EqualTo("The real description"));
    Assert.That(sections.Command, Is.EqualTo("/usr/bin/real"), "the first ExecStart, not the last");
  }

  [Test]
  public void TheAdministratorsUnitFileReplacesTheVendorsEntirely() {
    var matches = 0;
    foreach (var service in this.Services())
      if (service.Name == "overridden.service")
        ++matches;

    Assert.That(matches, Is.EqualTo(1), "listed once, not twice");
    Assert.That(this.One("overridden.service").Description, Is.EqualTo("The administrator's version"));
  }

  [Test]
  public void ServicesAreSortedByName() {
    var names = new List<string>();
    foreach (var service in this.Services())
      names.Add(service.Name);

    var sorted = new List<string>(names);
    sorted.Sort(StringComparer.Ordinal);
    Assert.That(names, Is.EqualTo(sorted));
  }

  #endregion

  #region enabled, disabled, and neither

  [Test]
  public void AUnitSomethingWantsStartsAtBoot() =>
    Assert.That(this.One("sshd.service").Enabled, Is.True);

  /// <summary>
  /// A unit nothing wants is not necessarily disabled — socket- and timer-activated units appear in
  /// no wants directory at all. Reporting "no" would be a claim about configuration nobody made
  /// (PRD §72.3).
  /// </summary>
  [Test]
  public void AUnitNothingWantsIsUnknownRatherThanDisabled() =>
    Assert.That(this.One("socket-activated.service").Enabled, Is.Null);

  [Test]
  public void AMaskedUnitCanNeverRunAndSaysSo() {
    if (!this._symlinksWork)
      Assert.Ignore("this platform would not let the test create a symlink");

    var masked = this.One("masked.service");
    Assert.That(masked.Masked, Is.True);
    Assert.That(masked.Enabled, Is.False, "masked is a stronger statement than disabled");
    Assert.That(masked.Description, Is.Null, "there is nothing to read through a link to /dev/null");
  }

  #endregion

  #region what is running

  [Test]
  public void AServiceWithProcessesIsRunningAndCarriesItsMainPid() {
    var sshd = this.One("sshd.service");

    Assert.That(sshd.State, Is.EqualTo(ServiceState.Running));
    // The first entry: the oldest process in the cgroup, which is the one the service started with.
    Assert.That(sshd.MainPid, Is.EqualTo(4242));
  }

  /// <summary>
  /// systemd puts some services in a sub-slice of their own — cups lives at
  /// system.slice/system-cups.slice/cups.service — and a flat listing calls those stopped while they
  /// are plainly running.
  /// </summary>
  [Test]
  public void AServiceNestedInItsOwnSliceIsFound() {
    var cups = this.One("cups.service");

    Assert.That(cups.State, Is.EqualTo(ServiceState.Running));
    Assert.That(cups.MainPid, Is.EqualTo(1090));
  }

  /// <summary>
  /// systemd-udevd's own cgroup is empty and its workers live in a child, so reading only the
  /// service's own cgroup.procs reports a service with a hundred processes as stopped.
  /// </summary>
  [Test]
  public void AServiceWhoseProcessesAreInAChildCgroupIsStillRunning() {
    var udev = this.One("udev.service");

    Assert.That(udev.State, Is.EqualTo(ServiceState.Running));
    Assert.That(udev.MainPid, Is.EqualTo(777));
  }

  /// <summary>
  /// A running instance has no unit file of its own — user@1000.service is started from
  /// user@.service. Listing it under the template's name merges every user's session into one row;
  /// leaving it out loses a service that is genuinely running.
  /// </summary>
  [Test]
  public void ARunningInstanceOfATemplateIsListedUnderItsOwnName() {
    var instance = this.One("user@1000.service");

    Assert.That(instance.State, Is.EqualTo(ServiceState.Running));
    Assert.That(instance.MainPid, Is.EqualTo(5555));
    // It borrows the template's description, because that is where the text lives.
    Assert.That(instance.Description, Is.EqualTo("User Manager"));
    Assert.That(instance.Path, Does.EndWith("user@.service"));
  }

  [Test]
  public void AUnitWithAnEmptyCgroupHasFinishedRatherThanStarted() {
    var finished = this.One("overridden.service");

    Assert.That(finished.State, Is.EqualTo(ServiceState.Inactive));
    Assert.That(finished.MainPid, Is.Zero);
  }

  [Test]
  public void AUnitWithNoCgroupAtAllIsInactive() =>
    Assert.That(this.One("socket-activated.service").State, Is.EqualTo(ServiceState.Inactive));

  #endregion

  [Test]
  public void MissingDirectoriesAreNotAnError() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      UnitDirectories = [Path.Combine(this._root, "nowhere")],
      WantsDirectories = [Path.Combine(this._root, "also-nowhere")],
      ServiceCgroupRoot = Path.Combine(this._root, "no-cgroups"),
      EffectiveUserId = 0,
    });

    Assert.That(probe.GetServices(), Is.Empty);
  }

}
