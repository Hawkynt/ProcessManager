using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The column that says in words what a row's colour says in colour (PRD §74).
/// </summary>
/// <remarks>
/// Row colouring is the program's fastest way of saying what a process is, and for a reader who
/// cannot tell the colours apart it says nothing at all. The colour cannot be the only carrier, so
/// the same classifier that picks the colour is also readable as a column — not a second opinion
/// about what a process is, the same one.
/// </remarks>
[TestFixture]
public sealed class CategoryFieldTests {

  private static ProcessRecord Process(
    int pid = 100,
    int userId = 1000,
    string name = "a",
    ProcessState state = ProcessState.Running
  ) => new() {
    Key = new(pid, 1),
    UserId = userId,
    Name = name,
    State = state,
  };

  private static string Text(in ProcessRecord process, int currentUserId) {
    var previous = ProcessCategories.CurrentUserId;
    try {
      ProcessCategories.CurrentUserId = currentUserId;
      return FieldAccessor.Text(ProcessField.Category, in process, null, 0);
    } finally {
      ProcessCategories.CurrentUserId = previous;
    }
  }

  /// <summary>
  /// The words are the classifier's, not a second set written beside it. If the column said one
  /// thing and the legend another, a reader would have to work out which of them to believe.
  /// </summary>
  [Test]
  public void TheColumnSaysWhatTheLegendSays() {
    var process = Process();

    Assert.That(
      Text(in process, 1000),
      Is.EqualTo(ProcessCategories.Describe(ProcessCategories.Classify(in process, 1000, false)))
    );
  }

  [Test]
  public void AProcessOfYoursSaysSo() {
    var process = Process(userId: 1000);

    Assert.That(Text(in process, 1000), Is.EqualTo(ProcessCategories.Describe(ProcessCategory.Own)));
  }

  /// <summary>
  /// And somebody else's does not become yours because you are the one looking. This is the whole
  /// reason the classifier is handed a user id rather than assuming one.
  /// </summary>
  [Test]
  public void SomebodyElsesIsNotYours() {
    var process = Process(userId: 1000);

    Assert.That(Text(in process, 4242), Is.Not.EqualTo(ProcessCategories.Describe(ProcessCategory.Own)));
  }

  /// <summary>
  /// Nobody having said who is looking classifies nothing as yours. The default is minus one, and
  /// the honest answer to "is this yours" when nobody knows who you are is no.
  /// </summary>
  [Test]
  public void WithNobodyNamedNothingIsYours() {
    var process = Process(userId: 0);

    Assert.That(Text(in process, -1), Is.Not.EqualTo(ProcessCategories.Describe(ProcessCategory.Own)));
  }

  /// <summary>
  /// Every category has words, and a compound name never reaches the reader as itself.
  /// <c>Suspended</c> describing as "Suspended" is English and fine; <c>ImageReplaced</c> doing the
  /// same would be an identifier that had leaked out of the enum.
  /// </summary>
  [Test]
  public void EveryCategorySaysSomethingInEnglish() {
    Assert.Multiple(() => {
      foreach (var category in Enum.GetValues<ProcessCategory>()) {
        var name = category.ToString();
        var described = ProcessCategories.Describe(category);
        Assert.That(described, Is.Not.Empty, $"{category}");

        var isCompound = name.Skip(1).Any(char.IsUpper);
        if (isCompound)
          Assert.That(described, Is.Not.EqualTo(name), $"{category} reaches the reader as its own name");
      }
    });
  }

  /// <summary>
  /// And no two categories say the same thing, or the column would merge two kinds of process the
  /// colours keep apart.
  /// </summary>
  [Test]
  public void NoTwoCategoriesSayTheSameThing() {
    var said = new HashSet<string>(StringComparer.Ordinal);

    Assert.Multiple(() => {
      foreach (var category in Enum.GetValues<ProcessCategory>())
        Assert.That(said.Add(ProcessCategories.Describe(category)), Is.True, $"{category} repeats another");
    });
  }

  /// <summary>
  /// Sorting a column of words has to sort by those words. Ordering the column by an enum's
  /// declaration order would put "Zombie" before "Own" and look like a bug to whoever clicked.
  /// </summary>
  [Test]
  public void SortingAgreesWithWhatIsShown() {
    var mine = Process(userId: 1000);
    var theirs = Process(userId: 0, name: "b");
    var previous = ProcessCategories.CurrentUserId;

    try {
      ProcessCategories.CurrentUserId = 1000;
      var byWords = string.Compare(
        FieldAccessor.Text(ProcessField.Category, in mine, null, 0),
        FieldAccessor.Text(ProcessField.Category, in theirs, null, 0),
        StringComparison.OrdinalIgnoreCase
      );

      Assert.That(
        Math.Sign(FieldAccessor.Compare(ProcessField.Category, in mine, 0, in theirs, 1, null)),
        Is.EqualTo(Math.Sign(byWords))
      );
    } finally {
      ProcessCategories.CurrentUserId = previous;
    }
  }

  /// <summary>
  /// It costs nothing to read — it is a judgement about fields already sampled, so it may sit in a
  /// default layout without making the program open a single extra file.
  /// </summary>
  [Test]
  public void ItAsksTheMachineForNothing() {
    var descriptor = FieldRegistry.Get(ProcessField.Category);

    Assert.That(descriptor.Cost, Is.EqualTo(FieldCost.Free));
  }

}
