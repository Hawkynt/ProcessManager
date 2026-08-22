using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a row says about itself in a few lines, for a pointer resting on it (PRD §24).
/// </summary>
/// <remarks>
/// <para>
/// The quick inspector answers "what is this?" without changing what is selected, which is the one
/// thing the lower pane cannot do: the pane describes the row somebody chose, and this describes the
/// row they are looking at. On a table of four hundred rows those are usually different questions.
/// </para>
/// <para>
/// <b>It performs no collection, and that is a property of how it is built rather than a rule
/// somebody has to remember.</b> Every line comes out of <see cref="FieldAccessor"/> against the
/// record already in the snapshot, and the accessor reads the record and nothing else. A field the
/// run never asked for renders as the mark for "nobody looked" — which is the honest thing for a
/// tooltip to say, and is what §24 means by never fetching synchronously: there is no path from here
/// to a file.
/// </para>
/// <para>
/// In Core because both front-ends want it and neither should decide on its own which facts a person
/// gets — a tooltip in the window saying more than the terminal's is the front-end disagreement §58
/// exists to stop.
/// </para>
/// </remarks>
public static class QuickFacts {

  /// <summary>
  /// The fields, in the order somebody reads them: what it is, whose it is, where it came from, what
  /// it is costing.
  /// </summary>
  /// <remarks>
  /// §24 names sixteen. The ones missing from this list are missing for a reason and not by
  /// oversight: network bytes per process have no portable source at all (§18), and a window title
  /// belongs to a process only on a compositor that will say so, which §39 found is not most of them.
  /// Everything else here is either in every snapshot or renders as "nobody looked".
  /// </remarks>
  private static readonly ProcessField[] _Fields = [
    ProcessField.Pid,
    ProcessField.ParentPid,
    ProcessField.ParentName,
    ProcessField.UserName,
    ProcessField.State,
    ProcessField.Category,
    ProcessField.StartTime,
    ProcessField.CpuPercent,
    ProcessField.PrivateBytes,
    ProcessField.WorkingSetBytes,
    ProcessField.IoTotalRate,
    ProcessField.ThreadCount,
    ProcessField.Runtime,
    ProcessField.Package,
    ProcessField.Container,
    ProcessField.ImagePath,
    ProcessField.CommandLine,
  ];

  /// <summary>How wide a value may be before it is cut, so one long path cannot become the tooltip.</summary>
  /// <remarks>
  /// A command line runs to hundreds of characters and a tooltip that wraps to twenty lines covers
  /// the table it is describing. Cut with an ellipsis, which says there is more rather than
  /// pretending that is all of it.
  /// </remarks>
  private const int _Widest = 96;

  /// <summary>
  /// The lines for one process, as label-and-value pairs.
  /// </summary>
  /// <remarks>
  /// The name is not among them: it is the heading, because it is what somebody is pointing at.
  /// </remarks>
  public static IReadOnlyList<KeyValuePair<string, string>> Of(
    in ProcessRecord process,
    SnapshotDelta? delta,
    int index
  ) {
    var facts = new List<KeyValuePair<string, string>>(_Fields.Length);
    foreach (var field in _Fields) {
      var descriptor = FieldRegistry.Get(field);
      var text = FieldAccessor.Text(field, in process, delta, index);
      if (text is not { Length: > 0 })
        continue;

      facts.Add(new(descriptor.Header, Shorten(text)));
    }

    return facts;
  }

  /// <summary>The same thing as one block of text, for a tooltip that takes a string.</summary>
  public static string Describe(in ProcessRecord process, SnapshotDelta? delta, int index) {
    var text = new System.Text.StringBuilder();
    text.Append(process.Name);
    foreach (var (label, value) in Of(in process, delta, index))
      text.Append('\n').Append(label).Append(": ").Append(value);

    return text.ToString();
  }

  private static string Shorten(string text)
    => text.Length <= _Widest ? text : text[.._Widest] + "…";

}
