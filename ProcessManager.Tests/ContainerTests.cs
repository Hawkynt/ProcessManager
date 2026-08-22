using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Which runtime a process is under, and what its container is called (PRD §38).
/// </summary>
/// <remarks>
/// The paths here are the layouts these runtimes are documented to write, and the systemd ones match
/// what this machine's own <c>machine.slice</c> and <c>user.slice</c> look like. They are not paths
/// of the detector author's own design, which is the failure this fixture would otherwise have: a
/// detector tested against invented paths agrees with itself and with nothing else. No container
/// runtime was installed on the machine this was written on, so the id-bearing layouts are checked
/// against documentation rather than against a running container — which is what the shortening
/// assertion below is for, since it holds this against the process table's own reading of the same
/// path.
/// </remarks>
[TestFixture]
public sealed class ContainerTests {

  private const string _Id = "3f2a91c4e07b8d5f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f7081";

  [Test]
  public void DockersOwnLayoutIsRead() {
    var container = ContainerDetector.Of("/docker/" + _Id);

    Assert.That(container.Runtime, Is.EqualTo(ContainerRuntime.Docker));
    Assert.That(container.Id, Is.EqualTo("3f2a91c4e07b"));
  }

  [Test]
  public void DockerUnderSystemdIsRead() {
    var container = ContainerDetector.Of($"/system.slice/docker-{_Id}.scope");

    Assert.That(container.Runtime, Is.EqualTo(ContainerRuntime.Docker));
    Assert.That(container.Id, Is.EqualTo("3f2a91c4e07b"));
  }

  [Test]
  public void PodmanIsRead() =>
    Assert.That(
      ContainerDetector.Of($"/machine.slice/libpod-{_Id}.scope").Runtime,
      Is.EqualTo(ContainerRuntime.Podman)
    );

  [Test]
  public void CriOAndContainerdAreToldApart() {
    Assert.That(
      ContainerDetector.Of($"/kubepods.slice/kubepods-burstable.slice/crio-{_Id}.scope").Runtime,
      Is.EqualTo(ContainerRuntime.CriO)
    );

    Assert.That(
      ContainerDetector.Of($"/kubepods.slice/kubepods-besteffort.slice/cri-containerd-{_Id}.scope").Runtime,
      Is.EqualTo(ContainerRuntime.Containerd)
    );
  }

  /// <summary>
  /// The id is shortened exactly the way the process table shortens it, or one window says a process
  /// is in <c>3f2a91c4e07b</c> and the next says it is in something else (PRD §5.1).
  /// </summary>
  [Test]
  public void TheShortIdIsTheOneTheProcessTableShows() {
    var path = $"/system.slice/docker-{_Id}.scope";

    Assert.That(ContainerDetector.Of(path).Id, Is.EqualTo(Humanize.ContainerId(path)));
  }

  /// <summary>
  /// A unit somebody wrote whose name begins the same way is not a container. Half of
  /// <c>system.slice</c> would grow a container column otherwise.
  /// </summary>
  [Test]
  public void AUnitThatMerelyStartsLikeOneIsNotAContainer() {
    Assert.That(ContainerDetector.Of("/system.slice/docker-cleanup.scope").IsContained, Is.False);
    Assert.That(ContainerDetector.Of("/system.slice/containerd.service").IsContained, Is.False);
  }

  /// <summary>
  /// LXC and <c>machined</c> put the name in the path, so the name is what is reported and there is
  /// no id to report at all.
  /// </summary>
  [Test]
  public void WhereTheMachineKnowsTheNameTheNameIsGiven() {
    var lxc = ContainerDetector.Of("/lxc/webserver");
    Assert.That(lxc.Runtime, Is.EqualTo(ContainerRuntime.Lxc));
    Assert.That(lxc.Name, Is.EqualTo("webserver"));
    Assert.That(lxc.Id, Is.Null);

    var payload = ContainerDetector.Of("/lxc.payload.build01/init.scope");
    Assert.That(payload.Runtime, Is.EqualTo(ContainerRuntime.Lxc));
    Assert.That(payload.Name, Is.EqualTo("build01"));
  }

  /// <summary>
  /// systemd escapes a hyphen because a hyphen already means a path separator in a unit name.
  /// Printing the escape verbatim shows somebody a name nothing on their machine is called.
  /// </summary>
  [Test]
  public void AnEscapedMachineNameIsUnescaped() {
    var machine = ContainerDetector.Of(@"/machine.slice/machine-web\x2d1.scope");

    Assert.That(machine.Runtime, Is.EqualTo(ContainerRuntime.SystemdNspawn));
    Assert.That(machine.Name, Is.EqualTo("web-1"));
  }

  /// <summary>
  /// A libvirt guest shares <c>machined</c>'s naming and is not a container: none of its processes
  /// are on this machine's list, and calling it one would say the opposite (PRD §5.3).
  /// </summary>
  [Test]
  public void AVirtualMachineIsNotReportedAsAContainer() {
    var guest = ContainerDetector.Of(@"/machine.slice/machine-qemu\x2d1\x2ddebian.scope");

    Assert.That(guest.Runtime, Is.EqualTo(ContainerRuntime.VirtualMachine));
    Assert.That(guest.Name, Is.EqualTo("qemu-1-debian"));
    Assert.That(guest.IsContained, Is.False, "it is a guest operating system rather than a container");
  }

  /// <summary>
  /// An incomplete escape is left as it was rather than dropped. A name is not improved by silently
  /// losing characters out of it.
  /// </summary>
  [Test]
  public void AnIncompleteEscapeSurvivesUnchanged() {
    Assert.That(ContainerDetector.Unescape(@"a\x2"), Is.EqualTo(@"a\x2"));
    Assert.That(ContainerDetector.Unescape(@"a\zz"), Is.EqualTo(@"a\zz"));
    Assert.That(ContainerDetector.Unescape("plain"), Is.EqualTo("plain"));
  }

  /// <summary>
  /// An ordinary desktop cgroup names no container — and that is a different statement from "nobody
  /// looked", which is what a default-constructed identity says (PRD §72.3).
  /// </summary>
  [Test]
  public void AnOrdinaryCgroupIsNotAContainerAndAnUnreadOneIsNotEither() {
    var ordinary = ContainerDetector.Of("/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope");
    Assert.That(ordinary.Runtime, Is.EqualTo(ContainerRuntime.None));
    Assert.That(ordinary.IsContained, Is.False);

    Assert.That(ContainerDetector.Of(null).Runtime, Is.EqualTo(ContainerRuntime.Unknown));
    Assert.That(default(ContainerIdentity).Runtime, Is.EqualTo(ContainerRuntime.Unknown));
    Assert.That(default(ContainerIdentity).IsContained, Is.False);
  }

  /// <summary>
  /// The id is all the kernel has. The name lives in the runtime's daemon, which is a socket whose
  /// membership is root-equivalent — so the reason is carried rather than the cell left blank.
  /// </summary>
  [Test]
  public void ANameThatOnlyADaemonKnowsSaysSoRatherThanReadingAsAbsent() {
    var container = ContainerDetector.Of($"/system.slice/docker-{_Id}.scope");

    Assert.That(container.Name, Is.Null);
    Assert.That(container.NameReason, Is.EqualTo(UnknownReason.NotImplementedHere));
    Assert.That(Humanize.Explain(container.NameReason), Is.Not.Empty);
  }

  #region io.max, parsed (PRD §38)

  [Test]
  public void EveryKeyOnALineIsReadAsItsOwnDirection() {
    var limits = CgroupIoMaxParser.Parse("259:0 rbps=1048576 wbps=2097152 riops=100 wiops=200\n");

    Assert.That(limits, Has.Count.EqualTo(1));
    Assert.That(limits[0].ReadBytesPerSecond.Value, Is.EqualTo(1048576ul));
    Assert.That(limits[0].WriteBytesPerSecond.Value, Is.EqualTo(2097152ul));
    Assert.That(limits[0].ReadOperationsPerSecond.Value, Is.EqualTo(100ul));
    Assert.That(limits[0].WriteOperationsPerSecond.Value, Is.EqualTo(200ul));
  }

  /// <summary>
  /// <c>rbps</c> is a suffix of nothing but <c>iops</c> is a suffix of both <c>riops</c> and
  /// <c>wiops</c>, so a substring search answers the read limit when asked for the write one.
  /// </summary>
  [Test]
  public void TheTwoOperationKeysAreNotConfusedWithEachOther() {
    var limits = CgroupIoMaxParser.Parse("8:0 riops=11 wiops=22");

    Assert.That(limits[0].ReadOperationsPerSecond.Value, Is.EqualTo(11ul));
    Assert.That(limits[0].WriteOperationsPerSecond.Value, Is.EqualTo(22ul));
  }

  /// <summary>
  /// A key that is not on the line is no ceiling in that direction. A nought would say the device
  /// was closed to the group entirely, which is the opposite (PRD §72.3).
  /// </summary>
  [Test]
  public void AnAbsentKeyIsNoLimitAndNeverANought() {
    var limits = CgroupIoMaxParser.Parse("8:0 rbps=4096");

    Assert.That(limits[0].WriteBytesPerSecond.HasValue, Is.False);
    Assert.That(limits[0].WriteBytesPerSecond.Reason, Is.EqualTo(UnknownReason.NoLimit));
  }

  /// <summary>
  /// A line whose device is not two numbers is dropped rather than guessed at: reporting it against
  /// device nought would put somebody else's limit on a device that exists on every machine.
  /// </summary>
  [Test]
  public void ALineThatIsNotADeviceIsDroppedRatherThanReadAsDeviceNought() {
    Assert.That(CgroupIoMaxParser.Parse("garbage rbps=1\n\n  \n"), Is.Empty);
    Assert.That(CgroupIoMaxParser.Parse(string.Empty), Is.Empty);
  }

  /// <summary>The kernel writes no carriage returns, but a fixture edited on Windows might.</summary>
  [Test]
  public void CarriageReturnsDoNotBreakTheLastKey() {
    var limits = CgroupIoMaxParser.Parse("8:0 rbps=max wbps=8192\r\n");

    Assert.That(limits[0].WriteBytesPerSecond.Value, Is.EqualTo(8192ul));
  }

  #endregion

}
