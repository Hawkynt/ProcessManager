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
  private string _runtime = string.Empty;
  private bool _symlinksWork;

  /// <summary>
  /// Whether this filesystem will hold a name with a colon in it.
  /// </summary>
  /// <remarks>
  /// systemd names every invocation entry <c>invocation:the-unit.service</c>, and on Windows a colon
  /// in a path opens an alternate data stream instead of making a file — so the fixture silently
  /// becomes a stream on a file called "invocation" and the entry the reader looks for is not there.
  /// The reader is right and the fixture cannot exist, which is a reason to say so rather than to
  /// fail: these are Linux runtime files and the parse of them is what is being tested.
  /// </remarks>
  private bool _colonNamesWork;

  /// <summary>
  /// When the fixture claims <c>lingering.service</c>'s invocation began.
  /// </summary>
  /// <remarks>
  /// A time in the past and not "now", so that a reader which quietly answered with the moment of the
  /// read — which is what a missing file gives on some platforms — cannot pass.
  /// </remarks>
  private static readonly DateTime _Activated = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"procman-services-{Guid.NewGuid():N}");
    this._vendor = Path.Combine(this._root, "usr", "lib", "systemd", "system");
    this._admin = Path.Combine(this._root, "etc", "systemd", "system");
    this._wants = Path.Combine(this._admin, "multi-user.target.wants");
    this._cgroup = Path.Combine(this._root, "cgroup");
    this._runtime = Path.Combine(this._root, "run", "systemd", "units");

    Directory.CreateDirectory(this._vendor);
    Directory.CreateDirectory(this._admin);
    Directory.CreateDirectory(this._wants);
    Directory.CreateDirectory(this._runtime);

    Unit(this._vendor, "sshd.service", """
      [Unit]
      Description=OpenSSH Daemon
      Requires=network.target sshd-keygen.target
      After=network.target
      Wants=nothing-in-particular.service

      [Service]
      Type=notify
      User=sshd
      ExecStart=-/usr/bin/sshd -D --with "a quoted thing"
      Restart=always

      [Install]
      WantedBy=multi-user.target
      """);

    // A unit whose type is not stated and which has an ExecStart: systemd assumes simple. The one
    // below it has no ExecStart at all, where systemd assumes oneshot instead — the pair is the whole
    // of the rule, and a hard-coded "simple" gets the second one wrong.
    Unit(this._vendor, "implied-simple.service", """
      [Unit]
      Description=No type stated

      [Service]
      ExecStart=/usr/bin/implied
      """);

    Unit(this._vendor, "implied-oneshot.service", """
      [Unit]
      Description=No type and nothing to start

      [Service]
      ExecStop=/usr/bin/tidy-up
      """);

    // A long command written across several physical lines, which is how anything with more than
    // three arguments is written in practice.
    Unit(this._vendor, "continued.service", """
      [Unit]
      Description=Written over several lines

      [Service]
      ExecStart=/usr/bin/long \
        --first \
        --second
      """);

    // A drop-in changes the type and the account without the packaged file being touched, which is
    // the supported way to alter a unit and the way a reader of the main file alone gets wrong.
    Unit(this._vendor, "dropped-in.service", """
      [Unit]
      Description=The packaged version

      [Service]
      Type=simple
      ExecStart=/usr/bin/packaged
      """);

    Directory.CreateDirectory(Path.Combine(this._admin, "dropped-in.service.d"));
    Unit(Path.Combine(this._admin, "dropped-in.service.d"), "10-first.conf", """
      [Service]
      User=nobody
      """);

    Unit(Path.Combine(this._admin, "dropped-in.service.d"), "20-second.conf", """
      [Unit]
      Description=The administrator's version

      [Service]
      Type=notify
      """);

    // Active with nothing in a cgroup: a oneshot that finished and was told to stay active. Without
    // the manager's runtime directory this is indistinguishable from a unit that never ran.
    Unit(this._vendor, "lingering.service", """
      [Unit]
      Description=Set something up and stayed

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      ExecStart=/usr/bin/set-up
      """);

    var invocation = Path.Combine(this._runtime, "invocation:lingering.service");
    try {
      File.WriteAllText(invocation, "an invocation id");
      File.SetLastWriteTimeUtc(invocation, _Activated);
      // Written, and actually there under that name. Not File.Exists: on Windows that answers true
      // for an alternate data stream, which is exactly what the write above made instead of a file.
      // A directory listing is the honest test, because a stream never appears in one.
      this._colonNamesWork = false;
      foreach (var found in Directory.EnumerateFiles(this._runtime))
        if (Path.GetFileName(found) == "invocation:lingering.service") {
          this._colonNamesWork = true;
          break;
        }
    } catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or NotSupportedException) {
      this._colonNamesWork = false;
    }

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

    // The same enablement seen as a dependency: multi-user.target wants sshd.service, which is what
    // `systemctl enable` writes and is how nearly every service on a machine is actually pulled in.
    // A dependency list built only from Wants= lines would show sshd as wanted by nothing.
    Directory.CreateDirectory(Path.Combine(this._admin, "multi-user.target.requires"));
    File.WriteAllText(Path.Combine(this._admin, "multi-user.target.requires", "sshd.service"), "symlink stand-in");

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
      // Named, and never left to the default: without this the fixture would be answered from the
      // machine the tests are running on, and a unit called sshd.service exists there too.
      ServiceRuntimeDirectory = this._runtime,
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
    // The whole ExecStart line, prefix characters and all: the split into program and arguments is
    // beside it rather than instead of it, because what the file says is what "copy" has to give.
    Assert.That(sshd.Command, Is.EqualTo("-/usr/bin/sshd -D --with \"a quoted thing\""));
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

  #region what kind of service, and whose (PRD §41)

  /// <summary>
  /// The type systemd would assume where the file states none, which is not one value but two:
  /// <c>simple</c> with an <c>ExecStart=</c> and <c>oneshot</c> without. A hard-coded "simple"
  /// describes the second unit as a long-running service, which is the opposite of what it is.
  /// </summary>
  [Test]
  public void TheServiceTypeIsTheStatedOneOrTheDefaultSystemdWouldApply() {
    Assert.Multiple(() => {
      Assert.That(this.One("sshd.service").Type, Is.EqualTo("notify"));
      Assert.That(this.One("implied-simple.service").Type, Is.EqualTo("simple"));
      Assert.That(this.One("implied-oneshot.service").Type, Is.EqualTo("oneshot"));
    });
  }

  /// <summary>
  /// The account, left null where the file names none rather than filled in with "root". "It says
  /// root" and "it says nothing and the default is root" are different statements about a unit, and
  /// the record must not collapse them (PRD §5.3).
  /// </summary>
  [Test]
  public void TheAccountIsWhatTheFileSaysAndNullWhereItSaysNothing() {
    Assert.Multiple(() => {
      Assert.That(this.One("sshd.service").Account, Is.EqualTo("sshd"));
      Assert.That(this.One("implied-simple.service").Account, Is.Null);
    });
  }

  /// <summary>
  /// The program, its arguments and the prefix characters, each in their own field. The prefix is not
  /// decoration: a leading <c>-</c> is the difference between a unit that reports a failure and one
  /// that quietly does not.
  /// </summary>
  [Test]
  public void TheCommandIsSplitIntoProgramArgumentsAndPrefixes() {
    var sshd = this.One("sshd.service");

    Assert.Multiple(() => {
      Assert.That(sshd.CommandPrefixes, Is.EqualTo("-"));
      Assert.That(sshd.Executable, Is.EqualTo("/usr/bin/sshd"));
      Assert.That(sshd.Arguments, Is.EqualTo("-D --with \"a quoted thing\""));
    });
  }

  /// <summary>
  /// A command written across several physical lines is one command. Reading them separately gives a
  /// program with no arguments and two settings that are not settings.
  /// </summary>
  [Test]
  public void ACommandContinuedOverSeveralLinesIsOneCommand() {
    var continued = this.One("continued.service");

    Assert.That(continued.Executable, Is.EqualTo("/usr/bin/long"));
    Assert.That(continued.Arguments, Is.EqualTo("--first --second"));
  }

  /// <summary>
  /// A drop-in is the supported way to change a packaged unit, and a reader of the main file alone
  /// reports the packaged answer for a unit the administrator has already altered.
  /// </summary>
  [Test]
  public void DropInsAreAppliedOverTheUnitFile() {
    var unit = this.One("dropped-in.service");

    Assert.Multiple(() => {
      Assert.That(unit.Type, Is.EqualTo("notify"), "the drop-in's, not the packaged file's");
      Assert.That(unit.Account, Is.EqualTo("nobody"));
      Assert.That(unit.Description, Is.EqualTo("The administrator's version"));
      Assert.That(unit.Command, Is.EqualTo("/usr/bin/packaged"), "which no drop-in touched");
    });
  }

  #endregion

  #region what a unit is tied to (PRD §41)

  [Test]
  public void TheUnitFilesOwnDependenciesAreRead() {
    var sshd = this.One("sshd.service");
    var edges = new List<string>();
    foreach (var edge in sshd.Dependencies)
      edges.Add($"{edge.Relation} {edge.Unit}");

    Assert.That(edges, Is.SupersetOf(new[] {
      // One line, two units: a dependency key carries a list.
      "Requires network.target",
      "Requires sshd-keygen.target",
      "After network.target",
      "Wants nothing-in-particular.service",
    }));
  }

  /// <summary>
  /// The symlink half. <c>systemctl enable</c> writes one of these and nothing else, so a dependency
  /// list built only from <c>Wants=</c> lines shows nearly every service on a machine as wanted by
  /// nothing at all.
  /// </summary>
  [Test]
  public void TheTargetThatPullsAUnitInIsOneOfItsDependents() {
    var dependents = new List<string>();
    foreach (var edge in this.One("sshd.service").Dependents)
      dependents.Add($"{edge.Relation} {edge.Unit}");

    Assert.That(dependents, Does.Contain("Wants multi-user.target"));
    Assert.That(dependents, Does.Contain("Requires multi-user.target"), "from the .requires directory");
  }

  /// <summary>
  /// And the reverse of a plain <c>Wants=</c> line, whose owner is a service and does have a record.
  /// </summary>
  [Test]
  public void AUnitNamedByAnothersWantsLineKnowsAboutIt() {
    var dependents = new List<string>();
    foreach (var service in this.Services())
      if (service.Name == "sshd.service")
        foreach (var edge in service.Dependencies)
          if (edge.Unit == "nothing-in-particular.service")
            dependents.Add(edge.Source);

    Assert.That(dependents, Does.Contain("the unit file"));
  }

  #endregion

  #region the manager's own runtime directory (PRD §41)

  /// <summary>
  /// A oneshot that finished and stayed is active with nothing in a cgroup. Without the invocation
  /// file it is indistinguishable from a unit that never ran, and this reader called both inactive.
  /// </summary>
  [Test]
  public void AUnitWithAnInvocationAndNoProcessesIsActiveRatherThanInactive() {
    if (!this._colonNamesWork)
      Assert.Ignore("this filesystem will not hold a name with a colon in it, so there is no invocation entry to read");

    var lingering = this.One("lingering.service");

    Assert.Multiple(() => {
      Assert.That(lingering.State, Is.EqualTo(ServiceState.Active));
      Assert.That(lingering.SubState, Is.EqualTo(ServiceSubState.Exited));
      Assert.That(lingering.MainPid, Is.Zero);
    });
  }

  [Test]
  public void TheActivationTimeIsTheInvocationFilesOwn() {
    if (!this._colonNamesWork)
      Assert.Ignore("this filesystem will not hold a name with a colon in it, so there is no invocation entry to read");

    Assert.That(this.One("lingering.service").ActivatedUtcTicks.TryGetValue(out var ticks), Is.True);
    Assert.That(new DateTime((long)ticks, DateTimeKind.Utc), Is.EqualTo(_Activated));
  }

  /// <summary>
  /// systemd writes those entries as dangling symlinks — the target is an invocation id and not a
  /// path — so the time has to come off the link and not off whatever following it lands on.
  /// </summary>
  [Test]
  public void ADanglingInvocationSymlinkStillCarriesItsTime() {
    if (!this._symlinksWork || !this._colonNamesWork)
      Assert.Ignore("this platform will not hold a symlink named with a colon");

    var link = Path.Combine(this._runtime, "invocation:linked.service");
    File.CreateSymbolicLink(link, "0123456789abcdef0123456789abcdef");
    try {
      File.SetLastWriteTimeUtc(link, _Activated);
      Unit(this._vendor, "linked.service", "[Unit]\nDescription=Linked\n\n[Service]\nExecStart=/usr/bin/linked\n");

      var service = this.One("linked.service");
      Assert.That(service.ActivatedUtcTicks.TryGetValue(out var ticks), Is.True, "the link's own time was not read");
      Assert.That(new DateTime((long)ticks, DateTimeKind.Utc), Is.EqualTo(_Activated));
    } finally {
      File.Delete(link);
      File.Delete(Path.Combine(this._vendor, "linked.service"));
    }
  }

  /// <summary>
  /// A unit the manager holds no invocation of has no activation time, and that is an answer rather
  /// than a gap — which is why it carries a reason and never a zero (PRD §3.4).
  /// </summary>
  [Test]
  public void AUnitWithNoInvocationHasNoActivationTimeAndSaysWhy() {
    var counter = this.One("socket-activated.service").ActivatedUtcTicks;

    Assert.That(counter.HasValue, Is.False);
    Assert.That(counter.Reason, Is.EqualTo(UnknownReason.SourceGone));
  }

  /// <summary>
  /// And a machine whose manager writes no runtime directory at all says something different again:
  /// nobody here can tell, rather than nothing is running.
  /// </summary>
  [Test]
  public void WithNoRuntimeDirectoryTheActivationTimeIsUnsupportedRatherThanAbsent() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      UnitDirectories = [this._vendor, this._admin],
      WantsDirectories = [this._wants],
      ServiceCgroupRoot = this._cgroup,
      ServiceRuntimeDirectory = Path.Combine(this._root, "no-manager-here"),
      EffectiveUserId = 0,
    });

    foreach (var service in probe.GetServices())
      if (service.Name == "lingering.service") {
        Assert.That(service.ActivatedUtcTicks.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
        Assert.That(service.State, Is.EqualTo(ServiceState.Inactive), "nothing says otherwise");
        return;
      }

    Assert.Fail("no lingering.service");
  }

  [Test]
  public void TheLoadStateSaysWhetherTheManagerCanMakeSenseOfTheUnit() {
    Assert.That(this.One("sshd.service").LoadState, Is.EqualTo(ServiceLoadState.Loaded));
    if (this._symlinksWork)
      Assert.That(this.One("masked.service").LoadState, Is.EqualTo(ServiceLoadState.Masked));
  }

  /// <summary>
  /// A running unit with no file on disk is one systemd made at runtime, and saying "loaded" about it
  /// would name a file that does not exist.
  /// </summary>
  [Test]
  public void AUnitRunningWithNoFileOnDiskIsTransient() {
    var transient = this.One("cups.service");

    Assert.That(transient.LoadState, Is.EqualTo(ServiceLoadState.Transient));
    Assert.That(transient.SubState, Is.EqualTo(ServiceSubState.Running));
  }

  #endregion

  /// <summary>
  /// A record built from its constructor alone carries a refusal, not a zero.
  /// </summary>
  /// <remarks>
  /// The defect this project keeps meeting: <c>default(Counter)</c> is a <em>confident</em> zero —
  /// its reason is <see cref="UnknownReason.None"/>, which means "the value is present" — so an
  /// activation time nobody read would render as a timestamp in the year 1 rather than as a hole.
  /// The initialiser on the property is what stops that, and this is the assertion that it runs: a
  /// probe that knows less than the Linux one must not hand a front-end a blank to render as an
  /// answer (PRD §3.4, §72.3).
  /// </remarks>
  [Test]
  public void AServiceRecordThatNobodyFilledInClaimsNothing() {
    var bare = new ServiceRecord("x.service", null, ServiceState.Unknown, null, false, 0, null, "/x", null);

    Assert.Multiple(() => {
      Assert.That(bare.ActivatedUtcTicks.HasValue, Is.False, "an unread activation time is not the year 1");
      Assert.That(bare.ActivatedUtcTicks.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
      Assert.That(bare.Dependencies, Is.Not.Null.And.Empty);
      Assert.That(bare.Dependents, Is.Not.Null.And.Empty);
      Assert.That(bare.LoadState, Is.EqualTo(ServiceLoadState.Unknown));
      Assert.That(bare.SubState, Is.EqualTo(ServiceSubState.Unknown));
    });
  }

  [Test]
  public void MissingDirectoriesAreNotAnError() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop"),
      UnitDirectories = [Path.Combine(this._root, "nowhere")],
      WantsDirectories = [Path.Combine(this._root, "also-nowhere")],
      ServiceCgroupRoot = Path.Combine(this._root, "no-cgroups"),
      ServiceRuntimeDirectory = Path.Combine(this._root, "no-manager"),
      EffectiveUserId = 0,
    });

    Assert.That(probe.GetServices(), Is.Empty);
  }

}
