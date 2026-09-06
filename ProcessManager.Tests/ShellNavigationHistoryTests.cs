using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class ShellNavigationHistoryTests {

  [Test]
  public void NewHistoryHasNowhereToMove() {
    var history = new ShellNavigationHistory();

    Assert.Multiple(() => {
      Assert.That(history.Current, Is.Null);
      Assert.That(history.Count, Is.Zero);
      Assert.That(history.CanGoBack, Is.False);
      Assert.That(history.CanGoForward, Is.False);
      Assert.That(history.Back(), Is.Null);
      Assert.That(history.Forward(), Is.Null);
    });
  }

  [Test]
  public void BackAndForwardTraverseVisitsChronologically() {
    var history = new ShellNavigationHistory();
    history.Push(new("Processes"));
    history.Push(new("Performance"));
    history.Push(new("Services"));

    Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Performance")));
    Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Processes")));
    Assert.That(history.Back(), Is.Null);
    Assert.That(history.Forward(), Is.EqualTo(new ShellLocation("Performance")));
    Assert.That(history.Forward(), Is.EqualTo(new ShellLocation("Services")));
    Assert.That(history.Forward(), Is.Null);
  }

  [Test]
  public void VisitingAfterBackDiscardsTheOldForwardBranch() {
    var history = new ShellNavigationHistory();
    history.Push(new("Processes"));
    history.Push(new("Performance"));
    history.Push(new("Services"));

    Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Performance")));
    history.Push(new("Network"));

    Assert.Multiple(() => {
      Assert.That(history.Count, Is.EqualTo(3));
      Assert.That(history.Current, Is.EqualTo(new ShellLocation("Network")));
      Assert.That(history.CanGoForward, Is.False);
      Assert.That(history.Forward(), Is.Null);
    });
  }

  [Test]
  public void PushingTheCurrentLocationDoesNotCreateAHistoryStep() {
    var history = new ShellNavigationHistory();
    history.Push(new("Processes"));
    history.Push(new("Processes"));

    Assert.Multiple(() => {
      Assert.That(history.Count, Is.EqualTo(1));
      Assert.That(history.CanGoBack, Is.False);
    });
  }

  [Test]
  public void ReplacingProcessSelectionDoesNotTurnRowMovementIntoNavigationHistory() {
    var first = new ProcessKey(42, 100);
    var second = new ProcessKey(43, 200);
    var history = new ShellNavigationHistory();
    history.Push(new("Services"));
    history.Push(new("Processes", first));

    history.Replace(new("Processes", second));

    Assert.Multiple(() => {
      Assert.That(history.Count, Is.EqualTo(2));
      Assert.That(history.Current, Is.EqualTo(new ShellLocation("Processes", second)));
      Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Services")));
      Assert.That(history.Forward(), Is.EqualTo(new ShellLocation("Processes", second)));
    });
  }

  [Test]
  public void ProcessHistoryPreservesStartTicksSoPidReuseCannotChangeTheTarget() {
    var oldProcess = new ProcessKey(4242, 10);
    var reusedPid = new ProcessKey(4242, 20);
    var history = new ShellNavigationHistory();
    history.Push(new("Processes", oldProcess));
    history.Push(new("Performance"));
    history.Push(new("Processes", reusedPid));

    Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Performance")));
    Assert.That(history.Back(), Is.EqualTo(new ShellLocation("Processes", oldProcess)));
    Assert.That(history.Forward(), Is.EqualTo(new ShellLocation("Performance")));
    Assert.That(history.Forward(), Is.EqualTo(new ShellLocation("Processes", reusedPid)));
  }

  [Test]
  public void BreadcrumbDescribesHierarchyRatherThanVisitedHistory() {
    var process = new ProcessKey(77, 1234);

    Assert.Multiple(() => {
      Assert.That(ShellBreadcrumb.For(new("Services")).ToString(), Is.EqualTo("Services"));
      Assert.That(ShellBreadcrumb.For(new("Processes")).ToString(), Is.EqualTo("Processes"));
      Assert.That(
        ShellBreadcrumb.For(new("Processes", process), "worker").ToString(),
        Is.EqualTo("Processes › worker (77)"));
    });
  }

  [Test]
  public void BreadcrumbNeverNeedsPidOnlyIdentityToRenderAnUnnamedProcess() {
    var process = new ProcessKey(77, 1234);
    var breadcrumb = ShellBreadcrumb.For(new("Processes", process));

    Assert.Multiple(() => {
      Assert.That(breadcrumb.Root, Is.EqualTo("Processes"));
      Assert.That(breadcrumb.Leaf, Is.EqualTo("Process (77)"));
      Assert.That(breadcrumb.HasAncestor, Is.True);
    });
  }
}

/// <summary>
/// The same model as seen from the window that owns it (PRD §9, §74).
/// </summary>
/// <remarks>
/// The fixture above proves the history behaves; this proves the window is actually holding it.
/// A model wired to nothing passes every one of those tests, and that is exactly what this branch
/// shipped before the two halves of <c>MainWindow</c> would compile together at all.
/// </remarks>
[TestFixture]
public sealed class ShellNavigationWindowTests {

  private static (MainWindow Window, Platform.Linux.LinuxProbe Probe) Machine() {
    var probe = TerminalFixture.Probe();
    var window = new MainWindow(new Sampling.Sampler(probe), probe, null);
    window.Say = _ => { };
    window.ApplySettings(new() { DesktopColumns = [Query.ProcessField.Name, Query.ProcessField.Pid] }, _ => true);
    window.Start();
    return (window, probe);
  }

  [Test]
  public void TheWindowOpensOnProcessesWithNowhereToGoBackTo() {
    var (window, probe) = Machine();
    using (probe)
      Assert.Multiple(() => {
        Assert.That(window.ShownView, Is.EqualTo("Processes"));
        Assert.That(window.CanNavigateBack, Is.False);
        Assert.That(window.CanNavigateForward, Is.False);
        Assert.That(window.Breadcrumb, Is.EqualTo("Processes"));
      });
  }

  [Test]
  public void ChangingViewIsAVisitAndBackReturnsToTheOneBefore() {
    var (window, probe) = Machine();
    using (probe) {
      Assert.That(window.ShowView("Services"), Is.True);
      Assert.That(window.CanNavigateBack, Is.True, "the view swap was recorded");

      Assert.That(window.NavigateBack(), Is.True);
      Assert.Multiple(() => {
        Assert.That(window.ShownView, Is.EqualTo("Processes"));
        Assert.That(window.CanNavigateBack, Is.False);
        Assert.That(window.CanNavigateForward, Is.True, "and Services is still ahead");
      });

      Assert.That(window.NavigateForward(), Is.True);
      Assert.That(window.ShownView, Is.EqualTo("Services"));
    }
  }

  /// <summary>
  /// Replaying a visit does not append another one, which is what makes Back repeatable rather than
  /// a switch between the last two places.
  /// </summary>
  [Test]
  public void GoingBackDoesNotRecordTheArrivalAsANewVisit() {
    var (window, probe) = Machine();
    using (probe) {
      window.ShowView("Services");
      window.ShowView("Network");
      Assert.That(window.NavigateBack(), Is.True);
      Assert.That(window.ShownView, Is.EqualTo("Services"));
      Assert.That(window.NavigateBack(), Is.True);
      Assert.Multiple(() => {
        Assert.That(window.ShownView, Is.EqualTo("Processes"));
        Assert.That(window.CanNavigateBack, Is.False, "three views visited, two steps back, and no more");
      });
    }
  }

  /// <summary>
  /// Moving the cursor is state within the process view, not another place to go back to.
  /// </summary>
  /// <remarks>
  /// The distinction is the reason <c>Replace</c> exists at all: with Push, twenty arrow keys would
  /// leave twenty entries behind, and Back would undo a selection rather than return anywhere.
  /// </remarks>
  [Test]
  public void SelectingARowChangesThePathWithoutAddingABackStep() {
    var (window, probe) = Machine();
    using (probe) {
      window.SelectFirstRow();

      Assert.Multiple(() => {
        Assert.That(window.SelectedProcessKey, Is.Not.Null, "the fixture has rows");
        Assert.That(window.CanNavigateBack, Is.False, "a selection is not a visit");
        Assert.That(window.Breadcrumb, Does.StartWith("Processes › "), "but the path says where it is");
      });
    }
  }

  /// <summary>
  /// And the process a visit names is restored by its whole identity when the visit is replayed.
  /// </summary>
  [Test]
  public void GoingBackRestoresTheExactProcessTheVisitNamed() {
    var (window, probe) = Machine();
    using (probe) {
      window.SelectFirstRow();
      var chosen = window.SelectedProcessKey;
      Assert.That(chosen, Is.Not.Null);

      window.ShowView("Services");
      Assert.That(window.NavigateBack(), Is.True);

      Assert.Multiple(() => {
        Assert.That(window.ShownView, Is.EqualTo("Processes"));
        Assert.That(window.SelectedProcessKey, Is.EqualTo(chosen));
      });
    }
  }

  /// <summary>
  /// The smoke leg reads a picture and a log, and the log is where a toolbar button that stopped
  /// being built would otherwise disappear without anything going red (PRD §9.6).
  /// </summary>
  [Test]
  public void TheCaptureLogSaysWhereTheShellIsAndWhereItCanGo() {
    var (window, probe) = Machine();
    using (probe) {
      window.ShowView("Services");
      var capture = window.DescribeForCapture();

      Assert.Multiple(() => {
        Assert.That(capture, Does.Contain("navigation:"));
        Assert.That(capture, Does.Contain("back yes"));
        Assert.That(capture, Does.Contain("forward no"));
        Assert.That(capture, Does.Contain("breadcrumb Services"));
      });
    }
  }

  /// <summary>
  /// A capture is published, and a process name and pid from the runner are not this log's business.
  /// </summary>
  [Test]
  public void TheCaptureNamesNoProcessOfTheMachineItRanOn() {
    var (window, probe) = Machine();
    using (probe) {
      window.SelectFirstRow();
      var key = window.SelectedProcessKey;
      Assert.That(key, Is.Not.Null);

      var line = Array.Find(
        window.DescribeForCapture().Split('\n'),
        text => text.StartsWith("navigation:", StringComparison.Ordinal)
      );

      Assert.That(line, Is.Not.Null);
      Assert.Multiple(() => {
        Assert.That(line, Does.Contain("breadcrumb Processes › selected process"));
        Assert.That(line, Does.Not.Contain(key!.Value.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      });
    }
  }
}
