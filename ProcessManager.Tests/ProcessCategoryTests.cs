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
    int pid = 100
  ) => new() {
    Key = new(pid, 1),
    UserId = uid,
    State = state,
    Name = name,
    ContainerPath = cgroup,
    IsSuspended = suspended,
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
  public void EveryCategoryHasASentenceForTheLegend() {
    foreach (var category in Enum.GetValues<ProcessCategory>())
      Assert.That(ProcessCategories.Describe(category), Is.Not.Empty, $"{category} has no description");
  }

}
