using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Grouping the rows under headings (PRD §83).
/// </summary>
/// <remarks>
/// The rule the whole section turns on is that a heading is not a process: it must not be counted,
/// must not be selectable, and must not be something an action can be aimed at. Most of what is
/// asserted here is that negative — which is exactly the kind of requirement a front-end satisfies
/// by accident and stops satisfying the moment somebody adds a loop over the rows.
/// </remarks>
[TestFixture]
public sealed class GroupingTests {

  [Test]
  public void GroupingByUserPutsEachAccountUnderItsOwnHeading() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User, SortColumn = ProcessField.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    Assert.That(Labels(view), Is.EqualTo(new[] { "root", "alice" }), "in the order their first row appears");
    Assert.That(view.Groups[0].Count, Is.EqualTo(2));
    Assert.That(view.Groups[1].Count, Is.EqualTo(2));
    Assert.That(view.RowCount, Is.EqualTo(6), "four processes and two headings");
  }

  /// <summary>
  /// The count in the status bar is processes, not rows. A grouped list that counted its headings
  /// would claim more processes than the machine is running (PRD §83).
  /// </summary>
  [Test]
  public void AHeadingIsNotCounted() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User };
    view.Rebuild(snapshot, delta);

    Assert.That(view.MatchCount, Is.EqualTo(4));
    Assert.That(view.RowCount, Is.EqualTo(6));
    Assert.That(view.TotalCount, Is.EqualTo(4));
  }

  /// <summary>
  /// A heading's index into the snapshot is deliberately invalid, so a caller that forgets to ask
  /// gets an exception rather than the wrong process. Silently rendering process zero as a heading
  /// is the failure this guards.
  /// </summary>
  [Test]
  public void AHeadingHasNoProcessBehindIt() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User };
    view.Rebuild(snapshot, delta);

    var headings = 0;
    foreach (var row in view.Rows) {
      if (!row.IsGroupHeader)
        continue;

      ++headings;
      Assert.That(row.Index, Is.LessThan(0));
      Assert.That(row.Group, Is.InRange(0, view.Groups.Count - 1));
    }

    Assert.That(headings, Is.EqualTo(2));
  }

  [Test]
  public void EveryProcessRowKnowsWhichGroupItIsIn() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User, SortColumn = ProcessField.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    var current = -1;
    foreach (var row in view.Rows) {
      if (row.IsGroupHeader) {
        current = row.Group;
        continue;
      }

      Assert.That(row.Group, Is.EqualTo(current), "a row sits under the heading it belongs to");
      Assert.That(row.Depth, Is.EqualTo(1));
    }
  }

  /// <summary>
  /// A folded heading hides its rows and keeps its count: the count is a fact about the machine, not
  /// about what is on screen.
  /// </summary>
  [Test]
  public void AFoldedHeadingHidesItsRowsAndStillSaysHowMany() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User };
    view.Rebuild(snapshot, delta);

    Assert.That(view.SetGroupCollapsed("root", true), Is.True);
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(4), "one folded heading, one open heading and its two rows");
    Assert.That(view.MatchCount, Is.EqualTo(2));
    foreach (var group in view.Groups)
      if (group.Label == "root")
        Assert.That(group.Count, Is.EqualTo(2), "folding it does not make its members stop existing");

    Assert.That(view.SetGroupCollapsed("root", false), Is.True);
    view.Rebuild(snapshot, delta);
    Assert.That(view.RowCount, Is.EqualTo(6));
  }

  /// <summary>
  /// The headings come out in the order the sort put their first row in, so sorting by memory brings
  /// the heaviest group to the top. Ordering them alphabetically would bury the group somebody
  /// sorted the table to find.
  /// </summary>
  [Test]
  public void TheHeadingsFollowTheSort() {
    var (snapshot, delta) = Machine();
    var descending = new ProcessView {
      Grouping = ProcessGrouping.User,
      SortColumn = ProcessField.PrivateBytes,
      SortDescending = true,
    };

    descending.Rebuild(snapshot, delta);
    Assert.That(Labels(descending), Is.EqualTo(new[] { "alice", "root" }), "alice holds the largest process");

    var ascending = new ProcessView {
      Grouping = ProcessGrouping.User,
      SortColumn = ProcessField.PrivateBytes,
      SortDescending = false,
    };

    ascending.Rebuild(snapshot, delta);
    Assert.That(Labels(ascending), Is.EqualTo(new[] { "root", "alice" }));
  }

  /// <summary>
  /// A value that is not there is a heading that says so, never an empty one and never a zero: a
  /// kernel thread really does have no executable, and that is a fact worth a heading of its own
  /// (PRD §72.3).
  /// </summary>
  [Test]
  public void ProcessesWithNothingToGroupOnGetAHeadingThatSaysSo() {
    var (snapshot, delta) = Machine();
    foreach (var grouping in (ReadOnlySpan<ProcessGrouping>)[
      ProcessGrouping.Service,
      ProcessGrouping.Executable,
      ProcessGrouping.Container,
      ProcessGrouping.Cgroup,
      ProcessGrouping.Package,
    ]) {
      var view = new ProcessView { Grouping = grouping };
      view.Rebuild(snapshot, delta);

      foreach (var group in view.Groups) {
        Assert.That(group.Label, Is.Not.Empty, grouping.ToString());
        Assert.That(group.Label, Does.Not.EqualTo("0"), grouping.ToString());
        Assert.That(group.Label, Does.Not.EqualTo("—"), grouping.ToString());
      }
    }
  }

  /// <summary>
  /// "Nobody looked" and "nothing claims this file" are different answers, and only the second is a
  /// finding. A heading that said "not packaged" for a session that never read a package database
  /// would be the confident zero this project keeps finding (PRD §72.3, §5.4).
  /// </summary>
  [Test]
  public void APackageNobodyLookedUpSaysSoRatherThanSayingNotPackaged() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.Package };
    view.Rebuild(snapshot, delta);

    // The fixture's records are default-constructed, which is exactly the state of a run that did
    // not ask for package identity.
    Assert.That(Labels(view), Is.EqualTo(new[] { "package not looked up" }));

    var records = snapshot.Processes;
    var checkedOne = records[0];
    checkedOne.Package = PackageIdentity.NotPackaged;
    var packaged = records[1];
    packaged.Package = new(PackageSource.Pacman, "bash", "5.3.15-1", null, UnknownReason.None);

    var second = new SystemSnapshot();
    var buffer = second.PrepareProcesses(2);
    buffer[0] = checkedOne;
    buffer[1] = packaged;

    var secondDelta = new SnapshotDelta();
    secondDelta.Update(null, second, CpuPercentMode.Normalized);

    var after = new ProcessView { Grouping = ProcessGrouping.Package, SortColumn = ProcessField.Pid, SortDescending = false };
    after.Rebuild(second, secondDelta);
    Assert.That(Labels(after), Is.EqualTo(new[] { "not packaged", "bash 5.3.15-1" }));
  }

  [Test]
  public void TheServiceHeadingIsTheInnermostUnit() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.Service };
    view.Rebuild(snapshot, delta);

    Assert.That(Labels(view), Does.Contain("sshd.service"));
    Assert.That(Labels(view), Does.Contain("not a service"), "a process in no unit is not in one");
  }

  /// <summary>
  /// The tree is one of the groupings rather than a flag beside them, so that a list cannot be
  /// nested by parentage and headed by user at the same time — two fields for one decision is two
  /// fields to disagree (PRD §5.1).
  /// </summary>
  [Test]
  public void TheTreeIsOneOfTheGroupings() {
    var view = new ProcessView { TreeMode = true };
    Assert.That(view.Grouping, Is.EqualTo(ProcessGrouping.ParentTree));

    view.Grouping = ProcessGrouping.User;
    Assert.That(view.TreeMode, Is.False);

    view.TreeMode = false;
    Assert.That(view.Grouping, Is.EqualTo(ProcessGrouping.None));
  }

  [Test]
  public void AnUngroupedViewHasNoHeadingsAtAll() {
    var (snapshot, delta) = Machine();
    foreach (var grouping in (ReadOnlySpan<ProcessGrouping>)[ProcessGrouping.None, ProcessGrouping.ParentTree]) {
      var view = new ProcessView { Grouping = grouping };
      view.Rebuild(snapshot, delta);

      Assert.That(view.Groups, Is.Empty, grouping.ToString());
      Assert.That(view.MatchCount, Is.EqualTo(view.RowCount), grouping.ToString());
      foreach (var row in view.Rows)
        Assert.That(row.IsGroupHeader, Is.False, grouping.ToString());
    }
  }

  /// <summary>
  /// An export carries the table's rows. A heading has no cells, and a CSV with a heading line in it
  /// is a CSV nothing can read (PRD §61).
  /// </summary>
  [Test]
  public void AnExportSkipsTheHeadings() {
    var (snapshot, delta) = Machine();
    var view = new ProcessView { Grouping = ProcessGrouping.User, SortColumn = ProcessField.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    var writer = new StringWriter();
    Exporter.Write(writer, ExportFormat.Csv, snapshot, delta, view, [ProcessField.Pid, ProcessField.Name]);

    var lines = writer.ToString().TrimEnd('\n').Split('\n');
    Assert.That(lines, Has.Length.EqualTo(5), "one header line and four processes — no group headings");
    Assert.That(lines[0], Is.EqualTo("pid,name"));
    foreach (var line in lines[1..])
      Assert.That(line.Split(',')[0], Is.Not.Empty, "every row is a process with a pid");
  }

  /// <summary>The grouping survives a restart, like everything else about the layout (PRD §11).</summary>
  [Test]
  public void TheGroupingSurvivesTheSettingsFile() {
    foreach (var grouping in Enum.GetValues<ProcessGrouping>()) {
      var written = new UserSettings { Grouping = grouping }.Write();
      Assert.That(UserSettings.Parse(written).Grouping, Is.EqualTo(grouping), grouping.ToString());
    }
  }

  [Test]
  public void EveryGroupingHasAWordTheFileAndTheCommandLineAgreeOn() {
    foreach (var grouping in Enum.GetValues<ProcessGrouping>()) {
      var name = UserSettings.NameOfGrouping(grouping);
      Assert.That(UserSettings.TryParseGrouping(name, out var parsed), Is.True, name);
      Assert.That(parsed, Is.EqualTo(grouping), name);
    }
  }

  [Test]
  public void AWordNoBuildKnowsIsRefusedRatherThanGuessedAt() {
    Assert.That(UserSettings.TryParseGrouping("publisher", out _), Is.False);
    Assert.That(UserSettings.TryParseGrouping(null, out _), Is.False);
    Assert.That(UserSettings.TryParseGrouping("  ", out _), Is.False);
  }

  #region a machine to group

  /// <summary>
  /// Four processes: two accounts, two of them in a unit, one with no executable at all.
  /// </summary>
  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Machine() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(4);

    Fill(ref records[0], 1, "systemd", "root", 0, "/usr/lib/systemd/systemd", "/init.scope", 4_000_000);
    Fill(ref records[1], 2, "kthreadd", "root", 0, null, null, 1_000_000);
    Fill(ref records[2], 3, "sshd", "alice", 1000, "/usr/sbin/sshd", "/system.slice/sshd.service", 9_000_000);
    Fill(ref records[3], 4, "bash", "alice", 1000, "/usr/bin/bash", "/user.slice/user-1000.slice", 2_000_000);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    return (snapshot, delta);
  }

  private static void Fill(
    ref ProcessRecord record,
    int pid,
    string name,
    string user,
    int uid,
    string? image,
    string? cgroup,
    ulong privateBytes
  ) {
    record = default;
    record.Key = new(pid, (ulong)pid);
    record.Name = name;
    record.UserName = user;
    record.UserId = uid;
    record.SessionId = uid == 0 ? 0 : 3;
    record.ImagePath = image;
    record.ContainerPath = cgroup;
    record.PrivateBytes = Counter.Of(privateBytes);
  }

  private static string[] Labels(ProcessView view) {
    var labels = new string[view.Groups.Count];
    for (var i = 0; i < labels.Length; ++i)
      labels[i] = view.Groups[i].Label;

    return labels;
  }

  #endregion

}
