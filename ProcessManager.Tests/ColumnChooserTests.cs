using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The dialog that picks the columns (PRD §7.1).
/// </summary>
/// <remarks>
/// It listed every column the program knows in a box built at a fixed size, so the window was
/// resizable and the list inside it was not: dragging the frame out grew the dialog around a list
/// that stayed where it was.
/// </remarks>
[TestFixture]
public sealed class ColumnChooserTests {

  [Test]
  public void EveryColumnIsOfferedAndTheVisibleOnesAreTicked() {
    var chooser = new ColumnChooser([ProcessField.Name, ProcessField.Pid]);

    Assert.That(chooser.Selection, Does.Contain(ProcessField.Name));
    Assert.That(chooser.Selection, Does.Contain(ProcessField.Pid));
    Assert.That(chooser.Selection, Has.Count.EqualTo(2), "and nothing else");
  }

  /// <summary>
  /// A list of rows with no name is a list of numbers, so the name column cannot be turned off —
  /// including by handing the dialog a selection that never had it.
  /// </summary>
  [Test]
  public void TheNameColumnIsForcedOn() {
    var chooser = new ColumnChooser([ProcessField.Pid]);

    Assert.That(chooser.Selection[0], Is.EqualTo(ProcessField.Name));
  }

  /// <summary>
  /// The order somebody put the columns in survives a trip through the dialog. Columns can be
  /// reordered now, and a chooser that listed everything in registry order would throw that order
  /// away every time one more column was ticked (PRD §11).
  /// </summary>
  [Test]
  public void TheOrderOfTheShowingColumnsIsPreserved() {
    ProcessField[] chosen = [ProcessField.PrivateBytes, ProcessField.Name, ProcessField.Pid];
    var chooser = new ColumnChooser(chosen);

    Assert.That(chooser.Selection, Is.EqualTo(chosen));
  }

  [Test]
  public void ClosingItWithoutOkIsNotAnAcceptance() =>
    Assert.That(new ColumnChooser([ProcessField.Name]).Accepted, Is.False);

  [Test]
  public void TheListFollowsTheDialogWhenItIsResized() {
    var chooser = new ColumnChooser([ProcessField.Name]);
    var list = ListIn(chooser);
    var before = list.Bounds;

    chooser.Bounds = new(0, 0, chooser.Width + 300, chooser.Height + 400);
    chooser.ApplyLayout();

    Assert.That(list.Width, Is.GreaterThan(before.Width));
    Assert.That(list.Height, Is.GreaterThan(before.Height));
  }

  /// <summary>The buttons stay on screen and below the list, whatever the dialog is doing.</summary>
  [Test]
  public void TheButtonsStayInTheDialogBelowTheList() {
    var chooser = new ColumnChooser([ProcessField.Name]);
    chooser.Bounds = new(0, 0, 520, 700);
    chooser.ApplyLayout();

    var list = ListIn(chooser);
    foreach (var control in chooser.Controls)
      if (control is NativeForms.Button button) {
        Assert.That(button.Bounds.Top, Is.GreaterThanOrEqualTo(list.Bounds.Bottom), button.Text);
        Assert.That(button.Bounds.Bottom, Is.LessThanOrEqualTo(chooser.Height), button.Text);
        Assert.That(button.Bounds.Right, Is.LessThanOrEqualTo(chooser.Width), button.Text);
      }
  }

  /// <summary>
  /// Dragged small enough and the arithmetic could hand the list a negative height. A dialog nobody
  /// would choose to use is still not allowed to throw.
  /// </summary>
  [Test]
  public void ShrinkingItToNothingIsNotAnError() {
    var chooser = new ColumnChooser([ProcessField.Name]);

    Assert.That(() => {
      chooser.Bounds = new(0, 0, 40, 30);
      chooser.ApplyLayout();
    }, Throws.Nothing);

    Assert.That(ListIn(chooser).Height, Is.GreaterThan(0));
  }

  private static NativeForms.CheckedListBox ListIn(ColumnChooser chooser) {
    foreach (var control in chooser.Controls)
      if (control is NativeForms.CheckedListBox list)
        return list;

    Assert.Fail("the dialog has no list");
    return null!;
  }

}
