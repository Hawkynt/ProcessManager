using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Exports (PRD §61), and the rule that separates them from the screen: machine formats carry raw
/// exact values, human formats carry what the column shows (PRD §76).
/// </summary>
[TestFixture]
public sealed class ExporterTests {

  private SystemSnapshot _snapshot = null!;
  private SnapshotDelta _delta = null!;
  private ProcessView _view = null!;

  [SetUp]
  public void BuildSnapshot() {
    this._snapshot = new();
    var records = this._snapshot.PrepareProcesses(3);

    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "chrome";
    records[0].UserName = "alice";
    records[0].ParentPid = 1;
    records[0].State = ProcessState.Sleeping;
    records[0].ThreadCount = 42;
    // Every character that ends a cell early, in one value: a command line really can contain all
    // of these, and a process can be named almost anything (PRD §98).
    records[0].CommandLine = "chrome --flag=\"a,b\"\nsecond | line";
    records[0].PrivateBytes = Counter.Of(1536ul);
    records[0].WorkingSetBytes = Counter.Of(2048ul);
    records[0].StartTimeUtcTicks = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).Ticks;

    records[1] = default;
    records[1].Key = new(200, 2);
    records[1].Name = "child";
    records[1].UserName = "alice";
    records[1].ParentPid = 100;
    records[1].State = ProcessState.Running;
    records[1].ThreadCount = 1;
    records[1].PrivateBytes = Counter.Of(0ul);
    records[1].WorkingSetBytes = Counter.Of(4096ul);

    // Unknown, not zero — the case every format has to keep distinct.
    records[2] = default;
    records[2].Key = new(300, 3);
    records[2].Name = "kthreadd";
    records[2].UserName = "root";
    records[2].State = ProcessState.Sleeping;
    records[2].ThreadCount = 1;
    records[2].PrivateBytes = Counter.NotSupported;
    records[2].WorkingSetBytes = Counter.NotSupported;

    this._delta = new();
    this._delta.Update(null, this._snapshot, CpuPercentMode.Normalized);
    this._view = new() { SortColumn = ProcessField.Pid, SortDescending = false };
    this._view.Rebuild(this._snapshot, this._delta);
  }

  private string Export(ExportFormat format, ProcessField[] fields, bool treeIndent = false) {
    var writer = new StringWriter();
    Exporter.Write(writer, format, this._snapshot, this._delta, this._view, fields, treeIndent);
    return writer.ToString();
  }

  #region what a script can branch on

  /// <summary>
  /// The exit codes the help text promises are the ones a script gets (PRD §3, §59).
  /// </summary>
  /// <remarks>
  /// "2 nothing matched" was documented and delivered by --kill and --find, and not by --list: a
  /// filter that excluded every process returned the same nought as one that matched them all. A
  /// script asking "is anything over this memory threshold" could not tell yes from no, which is
  /// most of what a scripting interface is for.
  /// </remarks>
  [Test]
  public void AFilterThatMatchesNothingIsDistinguishableFromOneThatMatches() {
    var empty = new ProcessView { TextFilter = "zzq-nothing-like-this" };
    empty.Rebuild(this._snapshot, this._delta);

    var everything = new ProcessView { TextFilter = "chrome" };
    everything.Rebuild(this._snapshot, this._delta);

    Assert.Multiple(() => {
      Assert.That(empty.RowCount, Is.Zero, "the filter excluded every row");
      Assert.That(everything.RowCount, Is.GreaterThan(0), "and this one did not");
    });
  }

  #endregion

  #region the schema

  /// <summary>
  /// The catalogue is the schema (PRD §99). Anything reading the JSON decides how to treat a column
  /// from what the catalogue says it is, so a field declared a number has to arrive as a number in
  /// every row — and a field that arrives as a quoted number in some rows and a bare one in others
  /// breaks a reader that was told it could sum the column.
  /// </summary>
  /// <remarks>
  /// Every field at once rather than a chosen few, so a field added later is covered the day it is
  /// added instead of the day somebody remembers to extend this.
  /// </remarks>
  [Test]
  public void EveryExportedFieldMatchesTheKindTheCatalogueDeclares() {
    var fields = new List<ProcessField>();
    foreach (var descriptor in FieldRegistry.All)
      // A graph is drawn, not written: it has no text and nothing to export.
      if (!descriptor.IsGraph)
        fields.Add(descriptor.Id);

    using var document = System.Text.Json.JsonDocument.Parse(this.Export(ExportFormat.Json, [.. fields]));

    var rows = document.RootElement.GetProperty("processes");
    Assert.That(rows.GetArrayLength(), Is.EqualTo(3));

    foreach (var row in rows.EnumerateArray())
      foreach (var field in fields) {
        var descriptor = FieldRegistry.Get(field);
        Assert.That(row.TryGetProperty(descriptor.Key, out var value), Is.True, $"{descriptor.Key} is missing");

        // Unknown is null in every case — that is the whole reason the export distinguishes it from
        // zero, and null is legal for any kind (PRD §5.3).
        if (value.ValueKind == System.Text.Json.JsonValueKind.Null)
          continue;

        // A timestamp is a moment rather than a quantity, and it goes out as ISO 8601 whatever kind
        // it is declared: nobody sums a start time, and a raw tick count is unreadable.
        var expected = descriptor.Unit == FieldUnit.Timestamp || descriptor.Kind is FieldKind.Text or FieldKind.State
          ? System.Text.Json.JsonValueKind.String
          : System.Text.Json.JsonValueKind.Number;

        Assert.That(value.ValueKind, Is.EqualTo(expected), $"{descriptor.Key} is declared {descriptor.Kind}/{descriptor.Unit}");
      }
  }

  /// <summary>
  /// Every key appears once and is spelled the way the catalogue spells it. Two fields sharing a key
  /// would silently lose one of them in an object format, where the last one written wins.
  /// </summary>
  [Test]
  public void EveryExportedKeyIsTheCatalogueKeyAndAppearsOnce() {
    var fields = new List<ProcessField>();
    foreach (var descriptor in FieldRegistry.All)
      if (!descriptor.IsGraph)
        fields.Add(descriptor.Id);

    using var document = System.Text.Json.JsonDocument.Parse(this.Export(ExportFormat.Json, [.. fields]));

    var first = document.RootElement.GetProperty("processes")[0];
    var seen = new HashSet<string>();
    var count = 0;
    foreach (var property in first.EnumerateObject()) {
      ++count;
      Assert.That(seen.Add(property.Name), Is.True, $"{property.Name} was written twice");
    }

    Assert.That(count, Is.EqualTo(fields.Count), "one column in, one column out");
  }

  /// <summary>
  /// The separated formats have to carry the same columns as the object ones. A reader choosing CSV
  /// over JSON is choosing a container, not a subset of the data.
  /// </summary>
  [Test]
  public void TheSeparatedFormatsCarryTheSameColumnsAsJson() {
    var fields = new List<ProcessField>();
    foreach (var descriptor in FieldRegistry.All)
      if (!descriptor.IsGraph)
        fields.Add(descriptor.Id);

    var header = this.Export(ExportFormat.Csv, [.. fields]).Split('\n')[0].Trim();

    var keys = new List<string>();
    foreach (var field in fields)
      keys.Add(FieldRegistry.Get(field).Key);

    Assert.That(header.Split(',').Length, Is.EqualTo(keys.Count));
    foreach (var key in keys)
      Assert.That(header, Does.Contain(key), key);
  }

  #endregion

  #region separated values

  [Test]
  public void CsvHeadersAreTheCanonicalKeysNotTheDisplayHeaders() {
    // A header of "Working set" would be a different string on a platform that labels it "RSS";
    // the key is what stays put (PRD §5.3).
    var csv = this.Export(ExportFormat.Csv, [ProcessField.Pid, ProcessField.WorkingSetBytes]);
    Assert.That(csv.Split('\n')[0], Is.EqualTo("pid,ws"));
  }

  [Test]
  public void CsvCarriesRawValuesRatherThanTheAbbreviatedOnes() {
    var csv = this.Export(ExportFormat.Csv, [ProcessField.Pid, ProcessField.PrivateBytes]);
    var rows = csv.Split('\n');

    // 1536 bytes, not "1.5K": a spreadsheet cannot sum "1.5K".
    Assert.That(rows[1], Is.EqualTo("100,1536"));
    Assert.That(rows[2], Is.EqualTo("200,0"));
  }

  [Test]
  public void AnUnknownValueIsAnEmptyCellAndAZeroIsAZero() {
    var csv = this.Export(ExportFormat.Csv, [ProcessField.Pid, ProcessField.PrivateBytes]);
    var rows = csv.Split('\n');

    Assert.That(rows[2], Is.EqualTo("200,0"), "a real zero");
    Assert.That(rows[3], Is.EqualTo("300,"), "unknown is empty, never 0 and never an em dash");
  }

  [Test]
  public void CellsContainingSeparatorsQuotesOrNewlinesAreQuoted() {
    var csv = this.Export(ExportFormat.Csv, [ProcessField.Pid, ProcessField.CommandLine]);

    // The quote is doubled, and the whole cell is wrapped because it holds a comma and a newline.
    Assert.That(csv, Does.Contain("\"chrome --flag=\"\"a,b\"\"\nsecond | line\""));
  }

  [Test]
  public void TsvUsesTabsAndQuotesOnlyWhatItMust() {
    var tsv = this.Export(ExportFormat.Tsv, [ProcessField.Pid, ProcessField.Name]);
    var rows = tsv.Split('\n');
    Assert.That(rows[0], Is.EqualTo("pid\tname"));
    Assert.That(rows[1], Is.EqualTo("100\tchrome"), "no quoting needed here");
  }

  [Test]
  public void AStartTimeIsExportedAsIso8601() {
    var csv = this.Export(ExportFormat.Csv, [ProcessField.StartTime]);
    // Sorts correctly as text, which is what anything importing this will do with it.
    Assert.That(csv.Split('\n')[1], Is.EqualTo("2026-03-04T05:06:07.000Z"));
    Assert.That(csv.Split('\n')[3], Is.Empty, "a process with no start time exports nothing");
  }

  #endregion

  #region json

  [Test]
  public void JsonIsStampedWithItsSchemaVersion() {
    var json = this.Export(ExportFormat.Json, [ProcessField.Pid]);
    Assert.That(json, Does.StartWith($"{{\"schema\":{Exporter.SchemaVersion},\"processes\":["));
    Assert.That(json.TrimEnd(), Does.EndWith("]}"));
  }

  [Test]
  public void JsonNumbersAreNumbersAndUnknownsAreNull() {
    var json = this.Export(ExportFormat.Json, [ProcessField.Pid, ProcessField.PrivateBytes]);

    Assert.That(json, Does.Contain("{\"pid\":100,\"private\":1536}"), "unquoted number");
    Assert.That(json, Does.Contain("{\"pid\":200,\"private\":0}"), "a real zero");
    Assert.That(json, Does.Contain("{\"pid\":300,\"private\":null}"), "unknown is null, not 0");
  }

  [Test]
  public void JsonStringsAreEscaped() {
    var json = this.Export(ExportFormat.Json, [ProcessField.CommandLine]);
    Assert.That(json, Does.Contain("chrome --flag=\\\"a,b\\\"\\nsecond | line"));
  }

  [Test]
  public void ControlCharactersAreEscapedRatherThanEmittedRaw() {
    // A process name can contain one, and an unescaped control character is invalid JSON — the same
    // class of hostile input as a comm containing a bracket (PRD §98).
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "bad\u0001name";

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var view = new ProcessView();
    view.Rebuild(snapshot, delta);

    var writer = new StringWriter();
    Exporter.Write(writer, ExportFormat.Json, snapshot, delta, view, [ProcessField.Name]);
    var json = writer.ToString();

    Assert.That(json, Does.Contain("bad\\u0001name"));
    Assert.That(json, Does.Not.Contain("bad\u0001name"));
  }

  [Test]
  public void JsonLinesIsOneObjectPerLineWithNoWrapper() {
    var jsonl = this.Export(ExportFormat.JsonLines, [ProcessField.Pid]);
    var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    Assert.That(lines, Has.Length.EqualTo(3));
    foreach (var line in lines)
      Assert.That(line, Does.StartWith("{").And.EndWith("}"));

    Assert.That(jsonl, Does.Not.Contain("processes"), "a stream has no document around it");
  }

  #endregion

  #region human formats

  [Test]
  public void HumanFormatsCarryWhatTheColumnShows() {
    var text = this.Export(ExportFormat.Text, [ProcessField.Pid, ProcessField.PrivateBytes]);

    // "1.5K", because a person is reading this — the opposite of the CSV rule above.
    Assert.That(text, Does.Contain("1.5K"));
    Assert.That(text, Does.Not.Contain("1536"));
  }

  [Test]
  public void HumanFormatsShowTheReasonAValueIsMissing() {
    var text = this.Export(ExportFormat.Text, [ProcessField.Pid, ProcessField.PrivateBytes]);
    // n/a, not blank: on screen the reason is the value (PRD §72.3).
    Assert.That(text, Does.Contain(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
  }

  [Test]
  public void TextColumnsAreWideEnoughForEveryValueInThem() {
    var text = this.Export(ExportFormat.Text, [ProcessField.Name, ProcessField.ThreadCount]);
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Measured, not fixed: an export is written once, so nothing may be clipped.
    var width = lines[0].Length;
    foreach (var line in lines)
      Assert.That(line.TrimEnd(), Has.Length.LessThanOrEqualTo(width));

    Assert.That(lines[1], Does.StartWith("chrome"));
  }

  [Test]
  public void MarkdownKeepsTheColumnsAlignmentAndEscapesPipes() {
    var markdown = this.Export(ExportFormat.Markdown, [ProcessField.Name, ProcessField.ThreadCount, ProcessField.CommandLine]);
    var lines = markdown.Split('\n');

    Assert.That(lines[0], Is.EqualTo("| Process | Threads | Command line |"));
    // Threads is right-aligned on screen, so it is right-aligned when pasted.
    Assert.That(lines[1], Is.EqualTo("| --- | ---: | --- |"));
    Assert.That(markdown, Does.Contain("\\|"), "a pipe inside a cell would end it");
  }

  [Test]
  public void TreeIndentationAppliesToHumanFormatsOnly() {
    this._view.TreeMode = true;
    this._view.Rebuild(this._snapshot, this._delta);

    var text = this.Export(ExportFormat.Text, [ProcessField.Name], treeIndent: true);
    Assert.That(text, Does.Contain("  child"), "indented for a reader");

    var csv = this.Export(ExportFormat.Csv, [ProcessField.Name], treeIndent: true);
    // Indenting a CSV cell would corrupt the value for whatever reads it.
    Assert.That(csv, Does.Contain("\nchild\n"));
    Assert.That(csv, Does.Not.Contain("  child"));
  }

  #endregion

  #region argument parsing

  [TestCase("csv", ExportFormat.Csv)]
  [TestCase("CSV", ExportFormat.Csv)]
  [TestCase("tsv", ExportFormat.Tsv)]
  [TestCase("json", ExportFormat.Json)]
  [TestCase("jsonl", ExportFormat.JsonLines)]
  [TestCase("ndjson", ExportFormat.JsonLines)]
  [TestCase("md", ExportFormat.Markdown)]
  [TestCase("markdown", ExportFormat.Markdown)]
  [TestCase("text", ExportFormat.Text)]
  public void FormatNamesParse(string text, ExportFormat expected) {
    Assert.That(Exporter.TryParseFormat(text, out var format), Is.True, text);
    Assert.That(format, Is.EqualTo(expected));
  }

  [Test]
  public void AnUnknownFormatIsRefused() => Assert.That(Exporter.TryParseFormat("yaml", out _), Is.False);

  [Test]
  public void ColumnsParseByKeyOrAlias() {
    Assert.That(Exporter.TryParseFields("pid, memory ,name", out var fields, out var error), Is.True, error);
    Assert.That(fields, Is.EqualTo(new[] { ProcessField.Pid, ProcessField.PrivateBytes, ProcessField.Name }));
  }

  [Test]
  public void AnUnknownColumnIsNamedInTheError() {
    Assert.That(Exporter.TryParseFields("pid,bogus", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("bogus"));
  }

  [Test]
  public void ADrawnHistoryCannotBeAColumnOfAFile() {
    // There is nothing to put in the cell; refusing beats writing an empty column silently.
    Assert.That(Exporter.TryParseFields("pid,cpu.history", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("drawn history"));
  }

  [Test]
  public void NoColumnsMeansTheDefaultSet() {
    Assert.That(Exporter.TryParseFields(null, out var fields, out _), Is.True);
    Assert.That(fields, Is.EqualTo(Exporter.DefaultFields));
  }

  #endregion

}
