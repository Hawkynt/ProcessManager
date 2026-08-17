using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The filter language (PRD §56). One parser in Core, so what is asserted here is what the window,
/// the terminal and the command line all do.
/// </summary>
[TestFixture]
public sealed class ProcessQueryTests {

  private static SystemSnapshot _snapshot = null!;
  private static SnapshotDelta _delta = null!;

  [OneTimeSetUp]
  public void BuildSnapshot() {
    _snapshot = new();
    var records = _snapshot.PrepareProcesses(3);

    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "chrome";
    records[0].UserName = "alice";
    records[0].UserId = 1000;
    records[0].ParentPid = 1;
    records[0].State = ProcessState.Sleeping;
    records[0].ThreadCount = 42;
    records[0].CommandLine = "/opt/chrome/chrome --type=renderer";
    records[0].ImagePath = "/opt/chrome/chrome";
    records[0].PrivateBytes = Counter.Of(2ul * 1024 * 1024 * 1024);
    records[0].WorkingSetBytes = Counter.Of(512ul * 1024 * 1024);

    records[1] = default;
    records[1].Key = new(200, 2);
    records[1].Name = "sshd";
    records[1].UserName = "root";
    records[1].UserId = 0;
    records[1].State = ProcessState.Running;
    records[1].ThreadCount = 1;
    records[1].CommandLine = "/usr/sbin/sshd -D";
    records[1].ImagePath = "/usr/sbin/sshd";
    records[1].PrivateBytes = Counter.Of(8ul * 1024 * 1024);
    records[1].WorkingSetBytes = Counter.Of(4ul * 1024 * 1024);

    // The third has no memory reading at all — the case that separates "zero" from "unknown".
    records[2] = default;
    records[2].Key = new(300, 3);
    records[2].Name = "kthreadd";
    records[2].UserName = "root";
    records[2].UserId = 0;
    records[2].State = ProcessState.Sleeping;
    records[2].ThreadCount = 1;
    records[2].PrivateBytes = Counter.NotSupported;
    records[2].WorkingSetBytes = Counter.Of(0ul);

    _delta = new();
    _delta.Update(null, _snapshot, CpuPercentMode.Normalized);
  }

  private static List<string> Match(string query) {
    Assert.That(ProcessQuery.TryParse(query, out var parsed, out var error), Is.True, error);
    var names = new List<string>();
    var processes = _snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i)
      if (parsed.Matches(in processes[i], _delta, i))
        names.Add(processes[i].Name);

    return names;
  }

  #region free text

  [Test]
  public void ABareWordSearchesTheFieldsSomebodyPlausiblyMeant() {
    Assert.That(Match("chrome"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("renderer"), Is.EqualTo(new[] { "chrome" }), "the command line");
    Assert.That(Match("alice"), Is.EqualTo(new[] { "chrome" }), "the user");
    Assert.That(Match("/usr/sbin"), Is.EqualTo(new[] { "sshd" }), "the image path");
  }

  [Test]
  public void FreeTextIsCaseInsensitive() => Assert.That(Match("CHROME"), Is.EqualTo(new[] { "chrome" }));

  [Test]
  public void AQuotedTermIsAlwaysFreeTextEvenWhenItLooksLikeAQuery() {
    // Otherwise there is no way to search for a literal string containing a colon or an operator.
    Assert.That(Match("\"name:chrome\""), Is.Empty, "quoted, so it is a literal and matches nothing");
    Assert.That(Match("name:chrome"), Is.EqualTo(new[] { "chrome" }), "unquoted, so it is a comparison");
    Assert.That(Match("\"--type=renderer\""), Is.EqualTo(new[] { "chrome" }), "a literal with an '=' in it");
  }

  #endregion

  #region fields

  [Test]
  public void AFieldCanBeComparedByItsKey() {
    Assert.That(Match("pid:200"), Is.EqualTo(new[] { "sshd" }));
    Assert.That(Match("user:root"), Is.EqualTo(new[] { "sshd", "kthreadd" }));
    Assert.That(Match("threads:42"), Is.EqualTo(new[] { "chrome" }));
  }

  [Test]
  public void AFieldCanBeComparedByAnAlias() {
    // "memory" is an alias of "private", declared once in the registry and honoured everywhere.
    Assert.That(Match("memory:>1GiB"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("rss:>100MiB"), Is.EqualTo(new[] { "chrome" }));
  }

  [Test]
  public void ComparisonOperatorsWork() {
    Assert.That(Match("threads:>1"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("threads:>=1"), Is.EqualTo(new[] { "chrome", "sshd", "kthreadd" }));
    Assert.That(Match("threads:<2"), Is.EqualTo(new[] { "sshd", "kthreadd" }));
    Assert.That(Match("threads:<=1"), Is.EqualTo(new[] { "sshd", "kthreadd" }));
    Assert.That(Match("pid=200"), Is.EqualTo(new[] { "sshd" }));
    Assert.That(Match("pid!=200"), Is.EqualTo(new[] { "chrome", "kthreadd" }));
  }

  [Test]
  public void TheOperatorMayFollowTheColonOrReplaceIt() {
    // "cpu:>50" is the spelling in the PRD; "cpu>50" is what people type. Both must work.
    Assert.That(Match("threads:>1"), Is.EqualTo(Match("threads>1")));
    Assert.That(Match("threads:>=1"), Is.EqualTo(Match("threads>=1")));
  }

  [Test]
  public void SpacesAroundAnOperatorAreAllowed() {
    Assert.That(Match("threads > 1"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("user : root"), Is.EqualTo(new[] { "sshd", "kthreadd" }));
  }

  [Test]
  public void TextFieldsMatchBySubstringButEqualsIsExact() {
    Assert.That(Match("name:chr"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("name=chr"), Is.Empty);
    Assert.That(Match("name=chrome"), Is.EqualTo(new[] { "chrome" }));
  }

  [Test]
  public void StateIsMatchedByItsDisplayedName() => Assert.That(Match("state:sleep"), Is.EqualTo(new[] { "chrome", "kthreadd" }));

  /// <summary>
  /// The distinction the whole engine is built on: kthreadd's private bytes are unknown, not zero,
  /// so it matches neither side of the comparison (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnknownValueMatchesNeitherGreaterThanZeroNorEqualToZero() {
    Assert.That(Match("private:>0"), Is.EqualTo(new[] { "chrome", "sshd" }));
    Assert.That(Match("private:0"), Is.Empty, "kthreadd's memory is unknown, and unknown is not zero");
    Assert.That(Match("ws:0"), Is.EqualTo(new[] { "kthreadd" }), "…but a real zero still matches");
  }

  [Test]
  public void AnUnknownValueDoesNotMatchNotEqualEither() =>
    // "not equal to 5" is a claim about a value we do not have, and we will not make it.
    Assert.That(Match("private!=5"), Is.EqualTo(new[] { "chrome", "sshd" }));

  #endregion

  #region boolean structure

  [Test]
  public void TermsSideBySideMeanAnd() {
    Assert.That(Match("user:root state:sleep"), Is.EqualTo(new[] { "kthreadd" }));
    Assert.That(Match("user:root AND state:sleep"), Is.EqualTo(new[] { "kthreadd" }));
    Assert.That(Match("user:root && state:sleep"), Is.EqualTo(new[] { "kthreadd" }));
  }

  [Test]
  public void OrWorks() {
    Assert.That(Match("name:chrome OR name:sshd"), Is.EqualTo(new[] { "chrome", "sshd" }));
    Assert.That(Match("name:chrome || name:sshd"), Is.EqualTo(new[] { "chrome", "sshd" }));
  }

  [Test]
  public void NotWorks() {
    Assert.That(Match("NOT user:root"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("!user:root"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("-user:root"), Is.EqualTo(new[] { "chrome" }));
  }

  [Test]
  public void AndBindsTighterThanOr() {
    // "a AND b OR c" is "(a AND b) OR c", the way every language of this shape works.
    Assert.That(Match("user:root AND state:run OR name:chrome"), Is.EqualTo(new[] { "chrome", "sshd" }));
    Assert.That(Match("user:root AND (state:run OR name:chrome)"), Is.EqualTo(new[] { "sshd" }));
  }

  [Test]
  public void ParenthesesGroup() =>
    Assert.That(Match("(name:chrome OR name:sshd) AND user:root"), Is.EqualTo(new[] { "sshd" }));

  /// <summary>A keyword must be a whole word, or "ORacle" would be an OR followed by "acle".</summary>
  [Test]
  public void AWordMerelyStartingWithAKeywordIsNotOne() {
    Assert.That(ProcessQuery.TryParse("ORacle", out var query, out var error), Is.True, error);
    Assert.That(query.IsEmpty, Is.False);
    Assert.That(Match("ORacle"), Is.Empty, "it is a search for the string 'ORacle'");
  }

  #endregion

  #region regular expressions

  [Test]
  public void ABareRegexSearchesTheUsualFields() => Assert.That(Match("/chr.me/"), Is.EqualTo(new[] { "chrome" }));

  [Test]
  public void ARegexCanBeAppliedToOneField() {
    Assert.That(Match("name:/^ssh/"), Is.EqualTo(new[] { "sshd" }));
    Assert.That(Match("name:/^shd/"), Is.Empty);
  }

  [Test]
  public void AnInvalidRegexIsReportedRatherThanThrown() {
    Assert.That(ProcessQuery.TryParse("/[unclosed/", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("regular expression"));
  }

  #endregion

  #region errors

  [Test]
  public void AnUnknownFieldIsNamedInTheError() {
    Assert.That(ProcessQuery.TryParse("bogus:1", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("bogus"));
  }

  [Test]
  public void UnbalancedSyntaxIsReported() {
    Assert.That(ProcessQuery.TryParse("(name:chrome", out _, out var openParen), Is.False);
    Assert.That(openParen, Does.Contain("never closed"));

    Assert.That(ProcessQuery.TryParse("name:\"chrome", out _, out var openQuote), Is.False);
    Assert.That(openQuote, Does.Contain("never closed"));
  }

  [Test]
  public void AValueThatIsNotANumberIsReported() {
    Assert.That(ProcessQuery.TryParse("threads:lots", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("lots"));
  }

  /// <summary>
  /// An interactive box must not blank the list while somebody is still typing, so a half-written
  /// query degrades to a substring search rather than to nothing.
  /// </summary>
  [Test]
  public void AHalfTypedQueryFallsBackToSubstringSearch() {
    var query = ProcessQuery.ParseOrSubstring("chrome:");
    Assert.That(query.IsEmpty, Is.False);

    var processes = _snapshot.Processes;
    var matched = new List<string>();
    for (var i = 0; i < processes.Length; ++i)
      if (query.Matches(in processes[i], _delta, i))
        matched.Add(processes[i].Name);

    Assert.That(matched, Is.Empty, "'chrome:' is not a substring of anything here");
    Assert.That(ProcessQuery.ParseOrSubstring("chrom").IsEmpty, Is.False);
  }

  [Test]
  public void AnEmptyQueryMatchesEverything() {
    Assert.That(ProcessQuery.TryParse("", out var empty, out _), Is.True);
    Assert.That(empty.IsEmpty, Is.True);
    Assert.That(ProcessQuery.TryParse("   ", out var blank, out _), Is.True);
    Assert.That(blank.IsEmpty, Is.True);
  }

  #endregion

  #region units

  [TestCase("1KiB", FieldUnit.Bytes, 1024d)]
  [TestCase("1kB", FieldUnit.Bytes, 1000d)]
  [TestCase("1K", FieldUnit.Bytes, 1024d)]
  [TestCase("1MiB", FieldUnit.Bytes, 1048576d)]
  [TestCase("1GiB", FieldUnit.Bytes, 1073741824d)]
  [TestCase("1GB", FieldUnit.Bytes, 1000000000d)]
  [TestCase("1.5K", FieldUnit.Bytes, 1536d)]
  [TestCase("512", FieldUnit.Bytes, 512d)]
  [TestCase("1B", FieldUnit.Bytes, 1d)]
  [TestCase("1MB/s", FieldUnit.BytesPerSecond, 1000000d)]
  [TestCase("50", FieldUnit.Percent, 50d)]
  [TestCase("50%", FieldUnit.Percent, 50d)]
  public void BytesAreParsedWithTheRightBase(string text, FieldUnit unit, double expected) {
    Assert.That(Quantity.TryParse(text, unit, out var value), Is.True, text);
    Assert.That(value, Is.EqualTo(expected).Within(0.001), text);
  }

  /// <summary>
  /// The reason the parser is unit-aware at all: a thousand of a count is 1000, and a thousand bytes
  /// is 1024. Getting this wrong is a 2.4% error at K and 7.4% at G.
  /// </summary>
  [TestCase("1k", FieldUnit.Count, 1000d)]
  [TestCase("1M", FieldUnit.Count, 1000000d)]
  [TestCase("1G", FieldUnit.CountPerSecond, 1000000000d)]
  public void CountsScaleInThousandsNotIn1024s(string text, FieldUnit unit, double expected) {
    Assert.That(Quantity.TryParse(text, unit, out var value), Is.True, text);
    Assert.That(value, Is.EqualTo(expected).Within(0.001), text);
  }

  [TestCase("5s", 5_000_000_000d)]
  [TestCase("500ms", 500_000_000d)]
  [TestCase("1m", 60_000_000_000d)]
  [TestCase("2h", 7_200_000_000_000d)]
  [TestCase("5", 5_000_000_000d)]
  public void TimesAreParsedIntoNanoseconds(string text, double expected) {
    Assert.That(Quantity.TryParse(text, FieldUnit.Nanoseconds, out var value), Is.True, text);
    Assert.That(value, Is.EqualTo(expected).Within(0.001), text);
  }

  [TestCase("")]
  [TestCase("lots")]
  [TestCase("KiB")]
  [TestCase("1ZB")]
  public void NonsenseIsRefused(string text)
    => Assert.That(Quantity.TryParse(text, FieldUnit.Bytes, out _), Is.False, text);

  [Test]
  public void AUnitAwareQuantityReachesTheComparison() {
    // chrome has 2 GiB private; sshd has 8 MiB. The boundary must land between them either way it
    // is spelled.
    Assert.That(Match("private:>1GiB"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("private:>1073741823"), Is.EqualTo(new[] { "chrome" }));
    Assert.That(Match("private:>1MiB"), Is.EqualTo(new[] { "chrome", "sshd" }));
  }

  #endregion

}
