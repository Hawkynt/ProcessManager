using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What an export is written as (PRD §61).</summary>
public enum ExportFormat : byte {

  /// <summary>Aligned columns, the way the list looks on screen.</summary>
  Text,

  Csv,
  Tsv,

  /// <summary>One object per process inside one document.</summary>
  Json,

  /// <summary>One object per line, for streaming into anything that reads a line at a time.</summary>
  JsonLines,

  /// <summary>A pipe table, for pasting into an issue.</summary>
  Markdown,

}

/// <summary>
/// Writes a process list out in any of the supported formats (PRD §61), over any set of fields from
/// <see cref="FieldRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Machine formats carry raw exact values; human formats carry what the screen shows.</b> PRD §76
/// requires that raw values stay reachable through export, and it is the whole point of exporting:
/// a CSV that says "1.5G" cannot be summed, and a JSON number that has been rounded to one decimal
/// is not the measurement any more. So CSV, TSV, JSON and JSON Lines emit bytes as bytes and
/// nanoseconds as nanoseconds, while text and Markdown emit "1.5G" because a human is going to read
/// them.
/// </para>
/// <para>
/// A value that is not known is an empty cell and a JSON <c>null</c> — never a zero, and never the
/// placeholder glyph, which would turn an unknown into the string "—" in a column of numbers
/// (PRD §72.3).
/// </para>
/// </remarks>
public static class Exporter {

  /// <summary>
  /// The version of the JSON shape. Bumped whenever a key is renamed or removed — adding one does
  /// not need a bump, because a reader that does not know a key ignores it.
  /// </summary>
  public const int SchemaVersion = 1;

  /// <summary>The fields a `--columns` argument gets when it does not name any.</summary>
  public static readonly ProcessField[] DefaultFields = [
    ProcessField.Pid,
    ProcessField.ParentPid,
    ProcessField.Name,
    ProcessField.UserName,
    ProcessField.State,
    ProcessField.CpuPercent,
    ProcessField.PrivateBytes,
    ProcessField.WorkingSetBytes,
    ProcessField.ThreadCount,
    ProcessField.CommandLine,
  ];

  /// <summary>Resolves a comma-separated list of field keys, as `--columns` takes it.</summary>
  public static bool TryParseFields(string? text, out ProcessField[] fields, out string? error) {
    error = null;
    if (string.IsNullOrWhiteSpace(text)) {
      fields = DefaultFields;
      return true;
    }

    var parsed = new List<ProcessField>();
    foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      if (!FieldRegistry.TryParse(part, out var field)) {
        fields = [];
        error = $"there is no field called '{part}'";
        return false;
      }

      // A graph is drawn, not written; there is nothing to put in a cell of a CSV.
      if (FieldRegistry.Get(field).IsGraph) {
        fields = [];
        error = $"'{part}' is a drawn history and cannot be exported as a column";
        return false;
      }

      parsed.Add(field);
    }

    fields = [.. parsed];
    return true;
  }

  public static bool TryParseFormat(string? text, out ExportFormat format) {
    format = ExportFormat.Text;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    switch (text.Trim().ToLowerInvariant()) {
      case "text" or "txt" or "table": format = ExportFormat.Text; return true;
      case "csv": format = ExportFormat.Csv; return true;
      case "tsv": format = ExportFormat.Tsv; return true;
      case "json": format = ExportFormat.Json; return true;
      case "jsonl" or "jsonlines" or "ndjson": format = ExportFormat.JsonLines; return true;
      case "md" or "markdown": format = ExportFormat.Markdown; return true;
      default: return false;
    }
  }

  /// <summary>
  /// Writes every visible row of <paramref name="view"/>.
  /// </summary>
  /// <param name="treeIndent">
  /// Indent names by their depth. Only for the human formats: indenting a CSV cell would corrupt the
  /// value for anything that reads it.
  /// </param>
  public static void Write(
    TextWriter output,
    ExportFormat format,
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[]? fields = null,
    bool treeIndent = false
  ) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(view);

    fields ??= DefaultFields;
    switch (format) {
      case ExportFormat.Csv: WriteSeparated(output, ',', snapshot, delta, view, fields); break;
      case ExportFormat.Tsv: WriteSeparated(output, '\t', snapshot, delta, view, fields); break;
      case ExportFormat.Json: WriteJson(output, snapshot, delta, view, fields, lines: false); break;
      case ExportFormat.JsonLines: WriteJson(output, snapshot, delta, view, fields, lines: true); break;
      case ExportFormat.Markdown: WriteMarkdown(output, snapshot, delta, view, fields, treeIndent); break;
      default: WriteText(output, snapshot, delta, view, fields, treeIndent); break;
    }
  }

  #region the two kinds of value

  /// <summary>
  /// The value as a machine wants it: a number for anything numeric, the underlying string for
  /// anything textual, and <see langword="null"/> for anything unknown.
  /// </summary>
  private static string? RawCell(
    ProcessField field,
    in ProcessRecord process,
    SnapshotDelta? delta,
    int index,
    out bool isNumber
  ) {
    isNumber = false;
    switch (FieldRegistry.Get(field).Serialisation) {
      // A drawn history has no cell. TryParseFields refuses one by name, so this is only reachable
      // through the API.
      case FieldSerialisation.None: return null;
      case FieldSerialisation.Text: return FieldAccessor.RawText(field, in process, delta, index);

      // A timestamp is a number internally and useless as one in a file. ISO 8601 sorts correctly as
      // text, which is what anybody importing this will do with it.
      //
      // Every field the catalogue declares a timestamp, and not the start time alone: this named one
      // field, so the image's creation time and a signature's countersigning date — both added long
      // after — exported as null in every row while the column beside them showed a date. That is
      // precisely the second definition of a field one catalogue exists to prevent (PRD §5.1).
      case FieldSerialisation.Timestamp:
        return FieldAccessor.Number(field, in process, delta, index) is { } ticks && ticks > 0
          ? new DateTime((long)ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
          : null;

      default: break;
    }

    if (FieldAccessor.Number(field, in process, delta, index) is not { } number)
      return null;

    isNumber = true;
    // "R" would give 1.0000000000000002 for figures that are exactly representable; 17 significant
    // digits round-trips a double without the noise.
    return number == Math.Floor(number) && Math.Abs(number) < 9.2e18
      ? ((long)number).ToString(CultureInfo.InvariantCulture)
      : number.ToString("G17", CultureInfo.InvariantCulture);
  }

  /// <summary>The value as a person reads it — exactly what the column shows on screen.</summary>
  private static string HumanCell(ProcessField field, in ProcessRecord process, SnapshotDelta? delta, int index)
    => FieldAccessor.Text(field, in process, delta, index);

  #endregion

  #region formats

  private static void WriteSeparated(
    TextWriter output,
    char separator,
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[] fields
  ) {
    for (var i = 0; i < fields.Length; ++i) {
      if (i > 0)
        output.Write(separator);

      output.Write(Escape(FieldRegistry.Get(fields[i]).Key, separator));
    }

    output.Write('\n');

    var processes = snapshot.Processes;
    foreach (var row in view.Rows) {
      // A heading is not a process and has no cells; an export carries the table's rows (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      for (var i = 0; i < fields.Length; ++i) {
        if (i > 0)
          output.Write(separator);

        var value = RawCell(fields[i], in process, delta, row.Index, out _);
        if (value is not null)
          output.Write(Escape(value, separator));
      }

      output.Write('\n');
    }
  }

  /// <summary>
  /// Quotes a cell when it contains anything that would end it early.
  /// </summary>
  /// <remarks>
  /// Command lines routinely contain commas, quotes and newlines, and a process can be named almost
  /// anything at all — this is the same class of input as the <c>comm</c> containing a bracket that
  /// breaks a naive /proc parser (PRD §98).
  /// </remarks>
  private static string Escape(string value, char separator) {
    var needsQuotes = value.Contains(separator, StringComparison.Ordinal)
      || value.Contains('"', StringComparison.Ordinal)
      || value.Contains('\n', StringComparison.Ordinal)
      || value.Contains('\r', StringComparison.Ordinal);

    if (!needsQuotes)
      return value;

    return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
  }

  private static void WriteJson(
    TextWriter output,
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[] fields,
    bool lines
  ) {
    // Written by hand rather than through a serializer: the shape is a documented contract (§59) and
    // System.Text.Json's reflection-free path would want a source-generated context for a shape this
    // small (PRD §8.3).
    var builder = new StringBuilder(1024);
    if (!lines)
      // Stamped, because §59 asks for a versioned schema and because the keys changed once already:
      // they are the registry keys now rather than a second set of camel-cased names kept alongside
      // them. A consumer that checks this field will notice the next change rather than mis-reading it.
      output.Write($"{{\"schema\":{SchemaVersion},\"processes\":[");

    var processes = snapshot.Processes;
    var first = true;
    foreach (var row in view.Rows) {
      // A heading is not a process and has no cells; an export carries the table's rows (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      builder.Clear();
      if (!lines && !first)
        builder.Append(',');

      first = false;
      builder.Append('{');
      for (var i = 0; i < fields.Length; ++i) {
        if (i > 0)
          builder.Append(',');

        builder.Append(JsonString(FieldRegistry.Get(fields[i]).Key)).Append(':');
        var value = RawCell(fields[i], in process, delta, row.Index, out var isNumber);
        if (value is null)
          // null, not 0 and not "": the field has no value, and JSON has a word for that.
          builder.Append("null");
        else if (isNumber)
          builder.Append(value);
        else
          builder.Append(JsonString(value));
      }

      builder.Append('}');
      if (lines)
        builder.Append('\n');

      output.Write(builder.ToString());
    }

    if (!lines)
      output.Write("]}\n");
  }

  private static string JsonString(string value) {
    var builder = new StringBuilder(value.Length + 2);
    builder.Append('"');
    foreach (var character in value)
      switch (character) {
        case '"': builder.Append("\\\""); break;
        case '\\': builder.Append("\\\\"); break;
        case '\n': builder.Append("\\n"); break;
        case '\r': builder.Append("\\r"); break;
        case '\t': builder.Append("\\t"); break;
        default:
          // Control characters have to be escaped; a process name or an environment value can
          // legitimately contain one, and an unescaped one is invalid JSON.
          if (character < ' ')
            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
          else
            builder.Append(character);

          break;
      }

    builder.Append('"');
    return builder.ToString();
  }

  private static void WriteText(
    TextWriter output,
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[] fields,
    bool treeIndent
  ) {
    var widths = MeasureColumns(snapshot, delta, view, fields, treeIndent);
    for (var i = 0; i < fields.Length; ++i) {
      var descriptor = FieldRegistry.Get(fields[i]);
      output.Write(Pad(descriptor.Header, widths[i], descriptor.RightAligned));
      if (i < fields.Length - 1)
        output.Write(' ');
    }

    output.Write('\n');

    var processes = snapshot.Processes;
    foreach (var row in view.Rows) {
      // A heading is not a process and has no cells; an export carries the table's rows (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      for (var i = 0; i < fields.Length; ++i) {
        var descriptor = FieldRegistry.Get(fields[i]);
        var cell = Cell(fields[i], in process, delta, row.Index, row.Depth, treeIndent);
        output.Write(Pad(cell, widths[i], descriptor.RightAligned));
        if (i < fields.Length - 1)
          output.Write(' ');
      }

      output.Write('\n');
    }
  }

  private static void WriteMarkdown(
    TextWriter output,
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[] fields,
    bool treeIndent
  ) {
    output.Write('|');
    foreach (var field in fields)
      output.Write($" {FieldRegistry.Get(field).Header} |");

    output.Write("\n|");
    foreach (var field in fields)
      // The alignment marker is the same one the column uses on screen, so a pasted table keeps its
      // numbers right-aligned.
      output.Write(FieldRegistry.Get(field).RightAligned ? " ---: |" : " --- |");

    output.Write('\n');

    var processes = snapshot.Processes;
    foreach (var row in view.Rows) {
      // A heading is not a process and has no cells; an export carries the table's rows (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      output.Write('|');
      foreach (var field in fields) {
        var cell = Cell(field, in process, delta, row.Index, row.Depth, treeIndent);
        // A pipe inside a cell would end it; a command line can easily contain one.
        output.Write($" {cell.Replace("|", "\\|", StringComparison.Ordinal)} |");
      }

      output.Write('\n');
    }
  }

  private static string Cell(
    ProcessField field,
    in ProcessRecord process,
    SnapshotDelta delta,
    int index,
    int depth,
    bool treeIndent
  ) {
    var text = HumanCell(field, in process, delta, index);
    return treeIndent && field == ProcessField.Name && depth > 0
      ? new string(' ', Math.Min(depth * 2, 40)) + text
      : text;
  }

  private static int[] MeasureColumns(
    SystemSnapshot snapshot,
    SnapshotDelta delta,
    ProcessView view,
    ProcessField[] fields,
    bool treeIndent
  ) {
    // Measured rather than fixed, unlike the live table: an export is written once and never
    // repainted, so there is no jitter to avoid and clipping a value would lose it (PRD §11).
    var widths = new int[fields.Length];
    for (var i = 0; i < fields.Length; ++i)
      widths[i] = FieldRegistry.Get(fields[i]).Header.Length;

    var processes = snapshot.Processes;
    foreach (var row in view.Rows) {
      // A heading is not a process and has no cells; an export carries the table's rows (PRD §83).
      if (row.IsGroupHeader)
        continue;

      ref readonly var process = ref processes[row.Index];
      for (var i = 0; i < fields.Length; ++i)
        widths[i] = Math.Max(widths[i], Cell(fields[i], in process, delta, row.Index, row.Depth, treeIndent).Length);
    }

    return widths;
  }

  private static string Pad(string value, int width, bool right) {
    if (value.Length >= width)
      return value;

    return right ? value.PadLeft(width) : value.PadRight(width);
  }

  #endregion

}
