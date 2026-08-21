using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That every column says what it measures (PRD §102).
/// </summary>
/// <remarks>
/// <para>
/// v1 does not ship unless CPU and memory metrics have documented semantics, and the place that
/// documentation actually reaches a reader is the registry: it is what <c>--help-fields</c> prints,
/// what the column chooser shows and what the terminal's help screen carries. A sentence in this
/// file that nobody kept true would be worse than none.
/// </para>
/// <para>
/// The invariant with teeth is the denominator. "Forty per cent" is not a measurement until
/// something says forty per cent <em>of what</em>, and this program deliberately offers both
/// conventions — a share of the whole machine, and a share of one core, which differ by the core
/// count and so by a factor of sixteen on the machine this was written on.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DocumentedSemanticsTests {

  /// <summary>
  /// Every percentage says what a hundred of them would be.
  /// </summary>
  /// <remarks>
  /// This found one: the CPU history plot carried "the last sixty seconds of processor use" and no
  /// scale at all, which makes it a shape rather than a measurement — and it is the one column where
  /// a reader cannot work the scale out from the number, because there is no number.
  /// </remarks>
  [Test]
  public void EveryPercentageSaysWhatAHundredOfThemIs() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.Unit != FieldUnit.Percent)
          continue;

        // A denominator is stated either as a number — "100% is one core" — or in the ordinary
        // English form, "how much of its adapter". Both count; a sentence with neither does not.
        // "of processor use" deliberately does not match: that names the quantity, not the whole it
        // is a part of, and it is exactly what the CPU history plot used to say.
        var says = descriptor.Description.Contains("100", StringComparison.Ordinal)
          || descriptor.Description.Contains("share", StringComparison.OrdinalIgnoreCase)
          || descriptor.Description.Contains("of the ", StringComparison.OrdinalIgnoreCase)
          || descriptor.Description.Contains("of its ", StringComparison.OrdinalIgnoreCase)
          || descriptor.Description.Contains("of one ", StringComparison.OrdinalIgnoreCase);

        Assert.That(says, Is.True, $"{descriptor.Key} is a percentage of nothing stated: '{descriptor.Description}'");
      }
    });
  }

  /// <summary>
  /// Every field has a description at all. A column somebody can sort and filter by, with nothing
  /// said about it, is a number whose meaning lives only in whoever added it.
  /// </summary>
  [Test]
  public void EveryFieldIsDescribed() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All)
        Assert.That(descriptor.Description, Is.Not.Empty, descriptor.Key);
    });
  }

  /// <summary>
  /// And no two share one. Two columns described identically are two columns one of which is
  /// mislabelled, and the reader has no way to tell which.
  /// </summary>
  [Test]
  public void NoTwoFieldsShareADescription() {
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);

    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (seen.TryGetValue(descriptor.Description, out var other))
          Assert.Fail($"{descriptor.Key} and {other} are described identically");

        seen[descriptor.Description] = descriptor.Key;
      }
    });
  }

  /// <summary>
  /// A description says more than the header already did. "Handles: the handles" documents nothing,
  /// and it is the shape a description takes when somebody added a field in a hurry.
  /// </summary>
  [Test]
  public void ADescriptionSaysMoreThanTheHeaderDoes() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All)
        Assert.That(
          descriptor.Description.Length,
          Is.GreaterThan(descriptor.Header.Length),
          $"{descriptor.Key}: '{descriptor.Description}'"
        );
    });
  }

  /// <summary>
  /// Every memory figure in bytes says what it counts. The words differ — resident, private,
  /// committed, shared, swapped, mapped — and which of them a column means is the whole difference
  /// between the figures, several of which are legitimately larger than the machine's memory.
  /// </summary>
  [Test]
  public void EveryMemoryFigureSaysWhatItCounts() {
    string[] words = [
      "resident", "private", "commit", "shared", "shareable", "swap", "mapped", "file",
      "page", "heap", "stack", "address space", "working set", "proportional",
    ];

    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.Unit != FieldUnit.Bytes || !descriptor.Key.Contains('.', StringComparison.Ordinal))
          continue;

        if (!descriptor.Key.StartsWith("mem", StringComparison.Ordinal)
          && !descriptor.Key.StartsWith("ws", StringComparison.Ordinal)
          && !descriptor.Key.StartsWith("private", StringComparison.Ordinal)
          && !descriptor.Key.StartsWith("swap", StringComparison.Ordinal))
          continue;

        var says = false;
        foreach (var word in words)
          if (descriptor.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) {
            says = true;
            break;
          }

        Assert.That(says, Is.True, $"{descriptor.Key} names no kind of memory: '{descriptor.Description}'");
      }
    });
  }

}
