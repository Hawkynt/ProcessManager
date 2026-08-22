using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a row's colour means (PRD §7.1).
/// </summary>
/// <remarks>
/// The categories are deliberately fewer than Process Hacker's: several of its colours need
/// information no probe here collects, and a colour that is sometimes right is worse than none.
/// These tests pin which distinctions are actually claimed.
/// </remarks>
[TestFixture]
public sealed class ProcessCategoryTests {

  private static ProcessRecord Make(
    int uid = 1000,
    ProcessState state = ProcessState.Sleeping,
    string name = "thing",
    string? cgroup = null,
    bool suspended = false,
    int pid = 100,
    string? imagePath = null,
    PackageSource package = PackageSource.Unknown,
    ProcessRuntime runtime = ProcessRuntime.Unknown
  ) => new() {
    Key = new(pid, 1),
    UserId = uid,
    State = state,
    Name = name,
    ContainerPath = cgroup,
    IsSuspended = suspended,
    ImagePath = imagePath,
    Package = new(package, null, null, null, UnknownReason.None),
    Runtime = runtime,
  };

  [Test]
  public void AProcessThatJustStartedWinsOverEverythingElse() {
    // It is the thing most worth seeing, whatever else it also is.
    var record = Make(uid: 0);
    Assert.That(ProcessCategories.Classify(in record, 1000, isNew: true), Is.EqualTo(ProcessCategory.New));
  }

  [Test]
  public void RootIsASystemProcess() {
    var record = Make(uid: 0);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.System));
  }

  [Test]
  public void OneOfMyOwnIsMine() {
    var record = Make(uid: 1000);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [Test]
  public void AnotherUsersIsNeitherMineNorTheSystems() {
    var record = Make(uid: 1001);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Other));
  }

  [Test]
  public void ASystemdUnitIsAService() {
    var record = Make(uid: 1000, cgroup: "/system.slice/sshd.service");
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Service));
  }

  [Test]
  public void AUserSessionScopeIsNotAService() {
    var record = Make(uid: 1000, cgroup: "/user.slice/user-1000.slice/session-3.scope");
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [Test]
  public void AZombieIsAZombieEvenWhenItIsMine() {
    var record = Make(uid: 1000, state: ProcessState.Zombie);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Zombie));
  }

  [Test]
  public void ASuspendedProcessSaysSo() {
    var stopped = Make(uid: 1000, state: ProcessState.Stopped);
    var flagged = Make(uid: 1000, suspended: true);

    Assert.That(ProcessCategories.Classify(in stopped, 1000, false), Is.EqualTo(ProcessCategory.Suspended));
    Assert.That(ProcessCategories.Classify(in flagged, 1000, false), Is.EqualTo(ProcessCategory.Suspended));
  }

  [Test]
  public void WithNoKnownUserNothingIsColouredAsMine() {
    // -1 means the platform would not say who we are; colouring rows "mine" then would be a guess.
    var record = Make(uid: 1000);
    Assert.That(ProcessCategories.Classify(in record, -1, false), Is.EqualTo(ProcessCategory.Other));
  }

  [Test]
  public void AnImageTheKernelCallsDeletedIsMarked() {
    // The whole of the evidence is the suffix readlink(2) hands back for an unlinked inode. Nothing
    // is inferred from a timestamp or a hash, which is why this category exists and "unsigned" does
    // not (PRD §23).
    var record = Make(uid: 1000, imagePath: "/usr/bin/thing (deleted)");
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.ImageReplaced));
  }

  [Test]
  public void ARootDaemonRunningAReplacedImageIsNotJustAnotherSystemProcess() {
    // The point of the colour. After an upgrade, the rows that need restarting are almost all root
    // daemons, and painting them blue with every other daemon is exactly how they are missed.
    var record = Make(uid: 0, cgroup: "/system.slice/sshd.service", imagePath: "/usr/bin/sshd (deleted)");
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.ImageReplaced));
  }

  [Test]
  public void AProcessThatJustStartedStillWinsOverAReplacedImage() {
    var record = Make(uid: 1000, imagePath: "/usr/bin/thing (deleted)");
    Assert.That(ProcessCategories.Classify(in record, 1000, isNew: true), Is.EqualTo(ProcessCategory.New));
  }

  [Test]
  public void AnOrdinaryImagePathIsNotMarked() {
    var record = Make(uid: 1000, imagePath: "/usr/bin/thing");
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [Test]
  public void AKernelThreadWithNoImageAtAllIsNotMarked() {
    // Null is no path rather than a path that ended in something. It read as "not deleted" either
    // way; the test is here so a future rewrite of the check cannot throw on it instead.
    var record = Make(uid: 0, imagePath: null);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.System));
  }

  [TestCase(PackageSource.Flatpak)]
  [TestCase(PackageSource.Snap)]
  [TestCase(PackageSource.AppImage)]
  public void ASandboxedApplicationIsPackaged(PackageSource source) {
    var record = Make(uid: 1000, package: source);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Packaged));
  }

  [TestCase(PackageSource.Pacman)]
  [TestCase(PackageSource.Dpkg)]
  public void TheMachinesOwnPackagesAreNotAColour(PackageSource source) {
    // pacman and dpkg own nearly every binary on a machine. A colour that painted nine rows in ten
    // would distinguish nothing, which is why the category is the sandboxed application and not the
    // packaged file (PRD §23).
    var record = Make(uid: 1000, package: source);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [Test]
  public void NobodyHavingAskedAboutPackagingIsNotAFinding() {
    // The identity is opt-in, so an unfilled record must not paint a row. Unknown is "nobody looked"
    // and None is "looked, and nothing claims it" — neither is a packaged application (PRD §72.3).
    var unasked = Make(uid: 1000, package: PackageSource.Unknown);
    var asked = Make(uid: 1000, package: PackageSource.None);

    Assert.That(ProcessCategories.Classify(in unasked, 1000, false), Is.EqualTo(ProcessCategory.Own));
    Assert.That(ProcessCategories.Classify(in asked, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [TestCase(ProcessRuntime.DotNet)]
  [TestCase(ProcessRuntime.Java)]
  [TestCase(ProcessRuntime.Python)]
  [TestCase(ProcessRuntime.Wine)]
  public void ARuntimeMappedIntoTheProcessIsAColour(ProcessRuntime runtime) {
    var record = Make(uid: 1000, runtime: runtime);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.ManagedRuntime));
  }

  [Test]
  public void NativeAndUnknownAreBothLeftAlone() {
    // Native is a finding — every module was looked at and none was a runtime — and Unknown is the
    // absence of one. Collapsing them is the defect the enum's two values exist to prevent.
    var native = Make(uid: 1000, runtime: ProcessRuntime.Native);
    var unknown = Make(uid: 1000, runtime: ProcessRuntime.Unknown);

    Assert.That(ProcessCategories.Classify(in native, 1000, false), Is.EqualTo(ProcessCategory.Own));
    Assert.That(ProcessCategories.Classify(in unknown, 1000, false), Is.EqualTo(ProcessCategory.Own));
  }

  [Test]
  public void TheTwoIdentityColoursOnlyEverReplaceNothingDistinguishing() {
    // A .NET service stays a service and a snap running as root stays a system process. These two
    // are tested last on purpose: no colour that already meant something loses its row to them.
    var service = Make(uid: 1000, cgroup: "/system.slice/thing.service", runtime: ProcessRuntime.DotNet);
    var root = Make(uid: 0, package: PackageSource.Snap);

    Assert.That(ProcessCategories.Classify(in service, 1000, false), Is.EqualTo(ProcessCategory.Service));
    Assert.That(ProcessCategories.Classify(in root, 1000, false), Is.EqualTo(ProcessCategory.System));
  }

  [Test]
  public void PackagingBeatsARuntimeWhenBothAnswer() {
    // Almost every Flatpak has a runtime inside it, so the two would otherwise fight on most desktop
    // rows. Where the application came from is the more specific fact and wins.
    var record = Make(uid: 1000, package: PackageSource.Flatpak, runtime: ProcessRuntime.DotNet);
    Assert.That(ProcessCategories.Classify(in record, 1000, false), Is.EqualTo(ProcessCategory.Packaged));
  }

  [Test]
  public void EveryCategoryHasASentenceForTheLegend() {
    foreach (var category in Enum.GetValues<ProcessCategory>())
      Assert.That(ProcessCategories.Describe(category), Is.Not.Empty, $"{category} has no description");
  }


  #region what counts as a service (PRD §13, §23)

  /// <summary>
  /// A process the desktop launched is yours, not a service — even though the path to it runs
  /// through the user's own systemd manager, which is called <c>user@1000.service</c>.
  /// </summary>
  /// <remarks>
  /// This was wrong, and wrong in the case it mattered most. The test was whether the cgroup path
  /// <em>contained</em> ".service" anywhere, and every process in a desktop session has
  /// <c>user@1000.service</c> as an ancestor — so on the machine this was written on, 207 of the
  /// user's own programs classified as services and exactly one as theirs. The row colours and §13's
  /// friendly grouping were both useless for the person they exist for.
  /// </remarks>
  [TestCase("/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope")]
  [TestCase("/user.slice/user-1000.slice/user@1000.service/app.slice/vte-spawn-63a2e373.scope")]
  [TestCase("/user.slice/user-1000.slice/session-3.scope")]
  public void SomethingTheDesktopLaunchedIsYoursAndNotAService(string cgroup) {
    var record = Make(uid: 1000, cgroup: cgroup);

    Assert.That(ProcessCategories.Classify(in record, 1000, isNew: false), Is.EqualTo(ProcessCategory.Own));
  }

  /// <summary>
  /// And something that really is a unit still is one, whether it is the system's or the user's own.
  /// </summary>
  [TestCase("/system.slice/sshd.service")]
  [TestCase("/system.slice/system-cups.slice/cups.service")]
  [TestCase("/user.slice/user-1000.slice/user@1000.service/background.slice/kde-baloo.service")]
  [TestCase("/system.slice/docker.socket")]
  public void SomethingThatReallyIsAUnitStillIsOne(string cgroup) {
    var record = Make(uid: 1000, cgroup: cgroup);

    Assert.That(ProcessCategories.Classify(in record, 1000, isNew: false), Is.EqualTo(ProcessCategory.Service));
  }

  /// <summary>
  /// The innermost unit decides, which is the same rule the owning-service column follows — so a row
  /// coloured as a service and the unit named beside it can never disagree.
  /// </summary>
  [Test]
  public void TheColourAgreesWithTheOwningServiceColumn() {
    const string Nested = "/user.slice/user-1000.slice/user@1000.service/app.slice/thing.scope";
    var record = Make(uid: 1000, cgroup: Nested);

    Assert.That(CgroupUnit.Of(Nested), Is.EqualTo("thing.scope"), "the column names the scope");
    Assert.That(
      ProcessCategories.Classify(in record, 1000, isNew: false),
      Is.Not.EqualTo(ProcessCategory.Service),
      "so the colour must not call it a service"
    );
  }

  /// <summary>
  /// A process in no cgroup at all is not a service either. That is a kernel thread or a machine
  /// with no systemd, and neither is a reason to guess.
  /// </summary>
  [Test]
  public void NoCgroupIsNotAService() {
    var record = Make(uid: 1000, cgroup: null);

    Assert.That(ProcessCategories.Classify(in record, 1000, isNew: false), Is.EqualTo(ProcessCategory.Own));
  }

  #endregion

}
