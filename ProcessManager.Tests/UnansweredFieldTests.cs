using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Two fields that used to answer a question nobody had asked them (PRD §14, §72.3).
/// </summary>
/// <remarks>
/// A zeroed <see cref="UnknownReason"/> is <see cref="UnknownReason.None"/>, which means "the value
/// is present" — the defect this project keeps meeting, here in two fields whose boxes were already
/// ticked. The Windows probe never touched either, so <c>app.name</c> came out as "none", which is a
/// real Linux answer meaning the machine has no desktop entry for the program, and <c>runtime</c>
/// came out as an empty placeholder. Both read as findings.
/// </remarks>
[TestFixture]
public sealed class UnansweredFieldTests {

  /// <summary>
  /// The two reasons are different sentences, and the difference is the whole point: one says this
  /// platform has no such thing, the other says it has and nobody here has read it.
  /// </summary>
  [Test]
  public void NotSupportedAndNotWrittenAreDifferentAnswers() {
    Assert.That(
      Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
      Is.Not.EqualTo(Humanize.Placeholder(UnknownReason.NotImplementedHere))
    );
  }

  /// <summary>
  /// A record that says so renders the mark for it rather than "none".
  /// </summary>
  [Test]
  public void AnApplicationNameThatDoesNotApplySaysSo() {
    var record = new ProcessRecord {
      Key = new(100, 1),
      Name = "a",
      ApplicationName = null,
      ApplicationNameReason = UnknownReason.NotSupportedOnPlatform,
    };

    Assert.That(
      FieldAccessor.Text(ProcessField.ApplicationName, in record, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  [Test]
  public void ARuntimeNobodyLookedForSaysThatInstead() {
    var record = new ProcessRecord {
      Key = new(100, 1),
      Name = "a",
      Runtime = ProcessRuntime.Unknown,
      RuntimeReason = UnknownReason.NotImplementedHere,
    };

    Assert.That(
      FieldAccessor.Text(ProcessField.Runtime, in record, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotImplementedHere))
    );
  }

  /// <summary>
  /// And neither renders as the answer a Linux probe would have given. "none" is a finding — the
  /// machine has no desktop entry for this program — and a platform that has no desktop entries at
  /// all has not found that.
  /// </summary>
  [Test]
  public void NeitherReadsLikeAFinding() {
    var unanswered = new ProcessRecord {
      Key = new(100, 1),
      Name = "a",
      ApplicationNameReason = UnknownReason.NotSupportedOnPlatform,
      RuntimeReason = UnknownReason.NotImplementedHere,
    };

    var found = new ProcessRecord {
      Key = new(100, 1),
      Name = "a",
      ApplicationName = null,
      ApplicationNameReason = UnknownReason.None,
      Runtime = ProcessRuntime.Native,
    };

    Assert.Multiple(() => {
      Assert.That(
        FieldAccessor.Text(ProcessField.ApplicationName, in unanswered, null, 0),
        Is.Not.EqualTo(FieldAccessor.Text(ProcessField.ApplicationName, in found, null, 0))
      );
      Assert.That(
        FieldAccessor.Text(ProcessField.Runtime, in unanswered, null, 0),
        Is.Not.EqualTo(FieldAccessor.Text(ProcessField.Runtime, in found, null, 0))
      );
    });
  }

  /// <summary>
  /// Exported, the two are empty rather than carrying a mark meant for a person. A spreadsheet cell
  /// holding "n/a" is a string where every other row has a name.
  /// </summary>
  [Test]
  public void AnUnansweredFieldExportsAsNothing() {
    var record = new ProcessRecord {
      Key = new(100, 1),
      Name = "a",
      ApplicationNameReason = UnknownReason.NotSupportedOnPlatform,
      RuntimeReason = UnknownReason.NotImplementedHere,
    };

    Assert.That(FieldAccessor.RawText(ProcessField.ApplicationName, in record, null, 0), Is.Null.Or.Empty);
    Assert.That(FieldAccessor.RawText(ProcessField.Runtime, in record, null, 0), Is.Null.Or.Empty);
  }

}
