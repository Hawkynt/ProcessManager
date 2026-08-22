using Hawkynt.ProcessManager.App;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The field catalogue (PRD §5.1) and the rule that nothing may be added to a front-end without
/// going through it (PRD §103).
/// </summary>
/// <remarks>
/// §103 said "a CI check enforces this" and nothing did, which made it a convention rather than a
/// rule. <see cref="EveryFieldInTheEnumIsRegistered"/> is that check: adding a value to
/// <see cref="ProcessField"/> and forgetting the descriptor now fails the build rather than
/// producing a column with no header that sorts by nothing.
/// </remarks>
[TestFixture]
public sealed class FieldRegistryTests {

  [Test]
  public void EveryFieldInTheEnumIsRegistered() {
    var missing = new List<ProcessField>();
    foreach (ProcessField field in Enum.GetValues<ProcessField>())
      if (FieldRegistry.Get(field).Id != field)
        missing.Add(field);

    Assert.That(missing, Is.Empty, "these fields have no descriptor in FieldRegistry.All");
  }

  [Test]
  public void EveryRegisteredFieldIsInTheEnum() {
    foreach (var descriptor in FieldRegistry.All)
      Assert.That(Enum.IsDefined(descriptor.Id), Is.True, $"{descriptor.Key} is not a ProcessField");
  }

  [Test]
  public void KeysAreUniqueAndSoAreHeaders() {
    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var descriptor in FieldRegistry.All) {
      Assert.That(keys.Add(descriptor.Key), Is.True, $"duplicate key: {descriptor.Key}");
      Assert.That(headers.Add(descriptor.Header), Is.True, $"duplicate header: {descriptor.Header}");
    }
  }

  /// <summary>
  /// An alias that collides with another field's key would resolve to whichever came first in the
  /// array, which is a sorting order nobody chose.
  /// </summary>
  [Test]
  public void NoAliasCollidesWithAnotherFieldsKeyOrAlias() {
    var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var descriptor in FieldRegistry.All) {
      Register(descriptor.Key, descriptor.Key);
      if (descriptor.Aliases is not { } aliases)
        continue;

      foreach (var alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        Register(alias, descriptor.Key);
    }

    void Register(string spelling, string owner) {
      Assert.That(
        seen.TryAdd(spelling, owner),
        Is.True,
        $"'{spelling}' is claimed by both {owner} and {(seen.TryGetValue(spelling, out var other) ? other : "?")}"
      );
    }
  }

  [Test]
  public void EveryKeyAndAliasParsesBackToItsOwnField() {
    foreach (var descriptor in FieldRegistry.All) {
      Assert.That(FieldRegistry.TryParse(descriptor.Key, out var byKey), Is.True, descriptor.Key);
      Assert.That(byKey, Is.EqualTo(descriptor.Id));

      Assert.That(FieldRegistry.TryParse(descriptor.Header, out var byHeader), Is.True, descriptor.Header);
      Assert.That(byHeader, Is.EqualTo(descriptor.Id));

      // Case and surrounding space must not matter: this is what a command line hands us.
      Assert.That(FieldRegistry.TryParse($"  {descriptor.Key.ToUpperInvariant()} ", out var loose), Is.True);
      Assert.That(loose, Is.EqualTo(descriptor.Id));

      if (descriptor.Aliases is not { } aliases)
        continue;

      foreach (var alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
        Assert.That(FieldRegistry.TryParse(alias, out var byAlias), Is.True, alias);
        Assert.That(byAlias, Is.EqualTo(descriptor.Id), alias);
      }
    }
  }

  [Test]
  public void NonsenseDoesNotParse() {
    Assert.That(FieldRegistry.TryParse("not-a-field", out _), Is.False);
    Assert.That(FieldRegistry.TryParse("", out _), Is.False);
    Assert.That(FieldRegistry.TryParse(null, out _), Is.False);
    Assert.That(FieldRegistry.TryParse("   ", out _), Is.False);
  }

  [Test]
  public void GraphsAreNotSortableAndEverythingElseIs() {
    foreach (var descriptor in FieldRegistry.All)
      Assert.That(
        descriptor.IsSortable,
        Is.EqualTo(descriptor.Kind != FieldKind.Graph),
        $"{descriptor.Key}: a drawn column has no order and a written one must have"
      );
  }

  /// <summary>
  /// Widths are what stop a header being clipped to something that names nothing — "PU %" was a real
  /// one. Every header must fit the column that carries it.
  /// </summary>
  [Test]
  public void EveryShortHeaderFitsItsTerminalColumn() {
    foreach (var descriptor in FieldRegistry.All)
      Assert.That(
        descriptor.ShortHeader.Length,
        Is.LessThanOrEqualTo(descriptor.TerminalWidth),
        $"{descriptor.Key}: '{descriptor.ShortHeader}' does not fit {descriptor.TerminalWidth} cells"
      );
  }

  [Test]
  public void AnExpensiveFieldIsNeverInTheDefaultTerminalColumns() {
    // PRD §5.4: displaying the ordinary process table must not require an expensive collector. The
    // handle count is the one exception, and it is sampled on its own schedule for that reason.
    foreach (var field in new[] { ProcessField.CpuPercent, ProcessField.PrivateBytes, ProcessField.Name })
      Assert.That(FieldRegistry.Get(field).Cost, Is.Not.EqualTo(FieldCost.High), field.ToString());
  }

  /// <summary>
  /// And never in the set a <c>--list</c> with no <c>--columns</c> writes, which is the opening set
  /// of the front-end that has no way to ask for one.
  /// </summary>
  /// <remarks>
  /// Whether a field is default-visible is each front-end's own decision — eighty columns of
  /// terminal cannot open with what a window does — but what the catalogue rules out is the same
  /// everywhere: a run that costs a syscall per process for a table nobody asked to be expensive
  /// (PRD §5.1, §5.4).
  /// </remarks>
  [Test]
  public void NothingExpensiveIsInTheSetAnExportOpensWith() {
    Assert.Multiple(() => {
      foreach (var field in Exporter.DefaultFields) {
        var descriptor = FieldRegistry.Get(field);
        Assert.That(descriptor.Cost, Is.Not.EqualTo(FieldCost.High), descriptor.Key);
      }
    });
  }

  #region reading a field

  [Test]
  public void EveryFieldCanBeReadFromAProcessWithoutThrowing() {
    var snapshot = OneProcess();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    foreach (var descriptor in FieldRegistry.All) {
      // The point is that none of these throw, whether or not the platform fills the field.
      var text = FieldAccessor.Text(descriptor.Id, in snapshot.Processes[0], delta, 0);
      Assert.That(text, Is.Not.Null, descriptor.Key);
      _ = FieldAccessor.Number(descriptor.Id, in snapshot.Processes[0], delta, 0);
      _ = FieldAccessor.RawText(descriptor.Id, in snapshot.Processes[0]);
    }
  }

  /// <summary>
  /// Before a second sample every derived field must read as "not sampled yet" rather than as zero —
  /// a fresh window showing 0.0% CPU for everything is a window that is lying (PRD §72.3).
  /// </summary>
  [Test]
  public void ADerivedFieldWithNoSecondSampleReadsAsPendingRatherThanZero() {
    var snapshot = OneProcess();
    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var pending = Humanize.Placeholder(UnknownReason.NotSampledYet);
    foreach (var field in new[] {
      ProcessField.CpuPercent, ProcessField.CpuPercentPerCore, ProcessField.ReadBytesPerSecond,
      ProcessField.WriteBytesPerSecond, ProcessField.IoTotalRate, ProcessField.PageFaultsDelta,
    }) {
      Assert.That(FieldAccessor.Text(field, in snapshot.Processes[0], delta, 0), Is.EqualTo(pending), field.ToString());
      Assert.That(FieldAccessor.Number(field, in snapshot.Processes[0], delta, 0), Is.Null, field.ToString());
    }
  }

  /// <summary>
  /// A field the platform does not report has no number, and a filter must not treat that as zero:
  /// "memory &gt; 0" should not match a process whose memory is unknown, and neither should
  /// "memory == 0".
  /// </summary>
  [Test]
  public void AnUnknownCounterHasNoNumberAtAll() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "test";
    records[0].PrivateBytes = Counter.NotSupported;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(FieldAccessor.Number(ProcessField.PrivateBytes, in snapshot.Processes[0], delta, 0), Is.Null);
    Assert.That(
      FieldAccessor.Text(ProcessField.PrivateBytes, in snapshot.Processes[0], delta, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  /// <summary>
  /// Sorting by a column and the text that column shows are read from the same place, so an order
  /// that disagrees with the display is not possible. This checks the halves agree.
  /// </summary>
  [Test]
  public void SortingAgreesWithTheNumbersTheColumnShows() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(3);
    for (var i = 0; i < 3; ++i) {
      records[i] = default;
      records[i].Key = new(i + 1, (ulong)(i + 1));
      records[i].Name = "p" + i;
      records[i].WorkingSetBytes = Counter.Of((ulong)((3 - i) * 1024));
      records[i].ThreadCount = i;
    }

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var processes = snapshot.Processes;

    // Working set descends across the three; thread count ascends. Compare must say so both times.
    Assert.That(FieldAccessor.Compare(ProcessField.WorkingSetBytes, in processes[0], 0, in processes[1], 1, delta), Is.GreaterThan(0));
    Assert.That(FieldAccessor.Compare(ProcessField.ThreadCount, in processes[0], 0, in processes[1], 1, delta), Is.LessThan(0));
    Assert.That(FieldAccessor.Compare(ProcessField.Name, in processes[0], 0, in processes[1], 1, delta), Is.LessThan(0));
  }

  /// <summary>An unknown value sorts below every known one, so reversing puts readable rows on top.</summary>
  [Test]
  public void AnUnknownValueSortsBelowEveryKnownOne() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(2);
    for (var i = 0; i < 2; ++i) {
      records[i] = default;
      records[i].Key = new(i + 1, (ulong)(i + 1));
      records[i].Name = "p" + i;
    }

    records[0].PrivateBytes = Counter.NotSupported;
    records[1].PrivateBytes = Counter.Of(0ul);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var processes = snapshot.Processes;

    // Even against a real zero, which is the case that matters: unknown is not zero.
    Assert.That(FieldAccessor.Compare(ProcessField.PrivateBytes, in processes[0], 0, in processes[1], 1, delta), Is.LessThan(0));
    Assert.That(FieldAccessor.Compare(ProcessField.PrivateBytes, in processes[1], 1, in processes[0], 0, delta), Is.GreaterThan(0));
  }

  #endregion

  private static SystemSnapshot OneProcess() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(4242, 100);
    records[0].Name = "test";
    records[0].UserName = "alice";
    records[0].ParentPid = 1;
    records[0].ThreadCount = 3;
    records[0].SessionId = 1;
    records[0].StartTimeUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    records[0].CpuTimeNs = Counter.Of(1_000_000_000ul);
    records[0].PrivateBytes = Counter.Of(1024ul * 1024);
    records[0].WorkingSetBytes = Counter.Of(2048ul * 1024);
    records[0].CommandLine = "/usr/bin/test --flag";
    records[0].ImagePath = "/usr/bin/test";
    return snapshot;
  }


  /// <summary>
  /// Every field that shows something on screen must export something too.
  /// </summary>
  /// <remarks>
  /// Asserted through the exporter rather than through the accessors, because the bug this catches
  /// lived in the seam between them: the exporter branches on the field's <see cref="FieldKind"/>
  /// and asks only <see cref="FieldAccessor.RawText"/> for a textual one, so a text field with a
  /// number but no raw text rendered a value in the window and wrote an empty cell to a file.
  /// Capabilities did exactly that — the column read 0x1ffffffffff and the CSV read nothing.
  /// </remarks>
  [Test]
  public void EveryFieldThatRendersAValueAlsoExportsOne() {
    var snapshot = OneProcess();
    var records = snapshot.PrepareProcesses(1);

    // Fill what the fixture leaves alone, so "empty because unknown" cannot be mistaken for
    // "empty because the exporter never asked".
    records[0].EffectiveCapabilities = Counter.Of(0x1ffffffffffUL);
    records[0].IsElevated = Counter.Of(1ul);
    records[0].SeccompMode = Counter.Of(2ul);
    records[0].NoNewPrivileges = Counter.Of(1ul);
    records[0].IntegrityLevel = Counter.Of(0x3000ul);
    records[0].SecurityContext = "unconfined";
    records[0].ContainerPath = "/user.slice";
    records[0].SwapBytes = Counter.Of(4096ul);
    records[0].LastCpu = 3;
    // A machine total, so the share-of-memory field is exercised as a number rather than skipped as
    // an unknown.
    snapshot.System.TotalMemoryBytes = Counter.Of(16ul * 1024 * 1024 * 1024);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var view = new ProcessView();
    view.Rebuild(snapshot, delta);

    foreach (var descriptor in FieldRegistry.All) {
      if (descriptor.IsGraph)
        continue;

      var shown = FieldAccessor.Text(descriptor.Id, in snapshot.Processes[0], delta, 0);
      if (shown.Length == 0 || IsPlaceholder(shown))
        continue;

      var writer = new StringWriter();
      Exporter.Write(writer, ExportFormat.Csv, snapshot, delta, view, [descriptor.Id]);
      var cell = writer.ToString().Split('\n')[1];

      Assert.That(cell, Is.Not.Empty, $"{descriptor.Key} shows '{shown}' and exports an empty cell");
    }
  }

  /// <summary>
  /// Whether what a column showed was one of the "there is no value" marks rather than a value.
  /// </summary>
  /// <remarks>
  /// An unknown number is deliberately an empty cell in a data file — a spreadsheet column with "?"
  /// in it is no longer numeric — so the rule above is about fields that <em>have</em> a value and
  /// still export nothing. That was one placeholder's worth of exception and should always have been
  /// all of them: the first field to be unknown for any other reason failed the test for being
  /// correct.
  /// </remarks>
  private static bool IsPlaceholder(string shown) {
    foreach (var reason in Enum.GetValues<UnknownReason>())
      if (reason != UnknownReason.None && shown == Humanize.Placeholder(reason))
        return true;

    return false;
  }

  #region what else an entry declares (PRD §5.1)

  /// <summary>One process with the three counters the row rings are kept from.</summary>
  private static SystemSnapshot Sampled(long timestampTicks, ulong cpuNs, ulong read, ulong write) {
    var snapshot = new SystemSnapshot { TimestampTicks = timestampTicks };
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(100, 1);
    records[0].Name = "sampled";
    records[0].CpuTimeNs = Counter.Of(cpuNs);
    records[0].PrivateBytes = Counter.Of(4096ul);
    records[0].ReadBytes = Counter.Of(read);
    records[0].WriteBytes = Counter.Of(write);
    snapshot.System.TotalMemoryBytes = Counter.Of(16ul * 1024 * 1024 * 1024);
    return snapshot;
  }

  /// <summary>
  /// A drawn column and the column it plots name the same ring, and nothing else claims one.
  /// </summary>
  /// <remarks>
  /// The series is what "eligible for history" means for the row rings: the plot draws it and the
  /// number beside it is what was stored. Two fields per series, exactly — the graph and its value —
  /// so a plot cannot be drawn from a reading no column shows.
  /// </remarks>
  [Test]
  public void EachRowSeriesIsClaimedByOneDrawnColumnAndOneValueColumn() {
    Assert.Multiple(() => {
      foreach (var series in Enum.GetValues<HistorySeries>()) {
        var drawn = new List<string>();
        var values = new List<string>();
        foreach (var descriptor in FieldRegistry.All) {
          if (descriptor.Series != series)
            continue;

          (descriptor.IsGraph ? drawn : values).Add(descriptor.Key);
        }

        Assert.That(drawn, Has.Count.EqualTo(1), $"{series} is drawn by {drawn.Count} columns");
        Assert.That(values, Has.Count.EqualTo(1), $"{series} is valued by {values.Count} columns");
      }
    });
  }

  /// <summary>
  /// Declaring a series and declaring that the row rings keep it are the same statement, so they
  /// cannot be made separately and disagree.
  /// </summary>
  [Test]
  public void AFieldNamesARowSeriesExactlyWhenTheRowRingsKeepIt() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All)
        Assert.That(
          descriptor.History.HasFlag(FieldHistory.Row),
          Is.EqualTo(descriptor.Series is not null),
          $"{descriptor.Key} says {descriptor.History} and names {descriptor.Series?.ToString() ?? "no series"}"
        );
    });
  }

  /// <summary>
  /// Nothing textual is kept over time. A history is a series of numbers, and a ring of process
  /// names would be a plot of nothing.
  /// </summary>
  [Test]
  public void OnlyANumberIsEligibleForHistory() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.History == FieldHistory.None)
          continue;

        Assert.That(
          descriptor.Kind,
          Is.AnyOf(FieldKind.Instant, FieldKind.Rate, FieldKind.Graph),
          $"{descriptor.Key} is kept as history and is a {descriptor.Kind}"
        );
      }
    });
  }

  /// <summary>
  /// What the sampler actually puts in a ring is the field the catalogue says feeds it.
  /// </summary>
  /// <remarks>
  /// The declaration would be decoration if nothing checked it against the code that stores the
  /// samples. This is the check: one process, one tick, and every ring compared against the number
  /// its declared field renders. It caught the I/O ring holding read plus write while the column it
  /// is drawn beside — <c>io.total</c> — is read plus write plus the third figure Windows keeps.
  /// </remarks>
  [Test]
  public void EveryRingHoldsTheFieldTheCatalogueSaysFeedsIt() {
    // Two samples a second apart, so the rates are readings rather than "not sampled yet" — a ring
    // full of unknowns would agree with any declaration at all.
    var before = Sampled(0, cpuNs: 0, read: 0, write: 0);
    var snapshot = Sampled(System.Diagnostics.Stopwatch.Frequency, cpuNs: 250_000_000, read: 1000, write: 2000);

    var delta = new SnapshotDelta();
    delta.Update(before, snapshot, CpuPercentMode.Normalized);
    var view = new ProcessView();
    view.Rebuild(snapshot, delta);

    var history = new ProcessHistory();
    history.Update(snapshot, delta, view, 0, view.RowCount);

    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        if (descriptor.IsGraph || descriptor.Series is not { } series)
          continue;

        var ring = history.Get(snapshot.Processes[0].Key, series);
        Assert.That(ring, Is.Not.Null, $"{series} was not kept for a row that is on screen");
        Assert.That(ring!.TryPeekLast(out var sample), Is.True, $"{series} kept no sample");

        var expected = FieldAccessor.Number(descriptor.Id, in snapshot.Processes[0], delta, 0);
        Assert.That(
          sample.HasValue ? sample.Value : (double?)null,
          Is.EqualTo(expected),
          $"the {series} ring holds something other than {descriptor.Key}"
        );
      }
    });
  }

  /// <summary>
  /// Every entry says what a machine format writes, and says it once.
  /// </summary>
  /// <remarks>
  /// Read off the kind and the unit rather than declared per entry, which is the point: a field
  /// added to the catalogue is serialised the day it is added. The exporter had the rule copied into
  /// it with one field named by hand, and every timestamp but that one exported as null.
  /// </remarks>
  [Test]
  public void EveryEntryDeclaresHowItIsSerialised() {
    Assert.Multiple(() => {
      foreach (var descriptor in FieldRegistry.All) {
        var expected = descriptor.IsGraph ? FieldSerialisation.None
          : descriptor.Unit == FieldUnit.Timestamp ? FieldSerialisation.Timestamp
          : descriptor.Kind is FieldKind.Text or FieldKind.State ? FieldSerialisation.Text
          : FieldSerialisation.Number;

        Assert.That(descriptor.Serialisation, Is.EqualTo(expected), descriptor.Key);
      }
    });
  }

  /// <summary>
  /// A field that needs the owner's authority is one this program can be refused, and it says so
  /// before somebody spends a column finding out.
  /// </summary>
  /// <remarks>
  /// Spot-checked against what the readers actually do rather than asserted over the whole
  /// catalogue, because the interesting claim is per field: <c>/proc/[pid]/io</c> has been mode 0400
  /// since 5.12 and <c>smaps_rollup</c> always was, while <c>stat</c> and <c>status</c> are read for
  /// anybody's process on a machine that has not been mounted <c>hidepid</c>.
  /// </remarks>
  [Test]
  public void TheFieldsBehindTheKernelsPtraceCheckSayThatTheyAre() {
    Assert.Multiple(() => {
      foreach (var key in (ReadOnlySpan<string>)["io.read", "io.write", "pss", "uss", "handles", "path", "runtime"]) {
        Assert.That(FieldRegistry.TryParse(key, out var field), Is.True, key);
        Assert.That(FieldRegistry.Get(field).Privilege, Is.EqualTo(FieldPrivilege.Owner), key);
      }

      foreach (var key in (ReadOnlySpan<string>)["pid", "name", "cpu", "private", "ws", "threads", "cmdline", "start"]) {
        Assert.That(FieldRegistry.TryParse(key, out var field), Is.True, key);
        Assert.That(FieldRegistry.Get(field).Privilege, Is.EqualTo(FieldPrivilege.Ordinary), key);
      }
    });
  }

  /// <summary>
  /// A field nobody can be refused is not worth the words; the ones that can are worth them in the
  /// one place somebody reads before choosing a column.
  /// </summary>
  [Test]
  public void TheFieldHelpSaysWhichColumnsNeedTheOwnersAuthority() {
    var help = CommandLineOptions.FieldHelpText;
    var lines = help.Split('\n');

    string LineFor(string key) {
      foreach (var line in lines)
        if (line.StartsWith("  " + key + " ", StringComparison.Ordinal))
          return line;

      return string.Empty;
    }

    Assert.Multiple(() => {
      Assert.That(LineFor("io.read"), Does.Contain("elevated helper"));
      Assert.That(LineFor("pid"), Does.Not.Contain("elevated helper"));
    });
  }

  #endregion

}
