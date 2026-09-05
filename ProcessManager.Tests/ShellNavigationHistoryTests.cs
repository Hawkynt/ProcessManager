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
