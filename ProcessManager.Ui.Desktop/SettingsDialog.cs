using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Every setting this program actually has, in one box (PRD §67).
/// </summary>
/// <remarks>
/// <para>
/// The settings file has been readable and writable by hand since it existed, which §67 is explicit
/// is the point of it — and which is only half of "configurable". Somebody who wants the terminal to
/// stop taking their mouse, or the window to stop asking twice before it ends something, had to find
/// a file, learn a key name and restart the program to say so. This is the other half, and it writes
/// the same keys the file holds rather than a second set beside them.
/// </para>
/// <para>
/// <b>Nothing is in here that does not do something.</b> §67 lists eleven groups; this box shows the
/// three the program has behaviour behind. Symbols, reputation, history retention, telemetry and
/// plugins are not here, not because they were forgotten, but because a control that writes a key
/// nothing reads is worse than no control at all: it tells the person who set it that they have
/// changed something.
/// </para>
/// <para>
/// The record it is handed is the one it gives back, modified — so a column set somebody wrote by
/// hand and a key written by a newer build both come out of this dialog exactly as they went in. A
/// preferences box that quietly dropped the parts of the file it has no control for would undo the
/// one rule that makes this file safe to share between versions.
/// </para>
/// </remarks>
public sealed class SettingsDialog : Form {

  private const int _Margin = 16;
  private const int _RowHeight = 26;

  /// <summary>
  /// How tall a row holding a picker or a button is.
  /// </summary>
  /// <remarks>
  /// Taller than a checkbox row, and that is a measurement rather than a taste. At the checkbox row's
  /// height the three pickers drew their text clipped along the bottom edge — "1 s" with the foot of
  /// the s missing — because a native combo box on this desktop is drawn taller than the box it was
  /// given. The same photograph is the only thing that showed it (PRD §9.6).
  /// </remarks>
  private const int _PickerRowHeight = 36;

  private const int _HeadingGap = 12;

  /// <summary>
  /// How wide a picker and its caption are.
  /// </summary>
  /// <remarks>
  /// The picker is wide because its longest entry is a sentence — "parentage — the process tree" — and
  /// a combo box narrower than its own selection draws that selection with both ends cut off, which
  /// is what this box did at 220. The caption is narrow for the same total: they share one line.
  /// </remarks>
  private const int _LabelWidth = 220;

  private const int _FieldWidth = 300;

  private const int _ButtonHeight = 30;

  /// <summary>
  /// How wide a button is.
  /// </summary>
  /// <remarks>
  /// Both numbers are what a picture demanded rather than what the arithmetic suggested. At 90 the
  /// footer read "Impor…" and "Expor…" and at 200 the thresholds button read "Highlighting
  /// threshol…" — a button whose label is truncated into a different word than the one it was given,
  /// which is the defect the legend window records having shipped with (PRD §9.6).
  /// </remarks>
  private const int _ButtonWidth = 116;

  private const int _WideButtonWidth = 250;

  /// <summary>
  /// How wide the box is.
  /// </summary>
  /// <remarks>
  /// Wide enough that the three buttons acting on the file and the two closing the box are not one
  /// undifferentiated row of five: at 700 the gap between "Export…" and "OK" was fourteen pixels,
  /// the same as the gaps inside each group, and a reader has no way to see that the first three do
  /// something and the last two answer a question.
  /// </remarks>
  private const int _Width = 730;

  /// <summary>The word the interval picker uses for "the tick is off" (PRD §12).</summary>
  private const string _ByHand = "by hand";

  private readonly List<(Label Heading, int Gap)> _headings = [];
  private readonly List<Control> _rows = [];

  private readonly CheckBox _confirm = new() { Text = "Ask before ending or suspending one process" };
  private readonly CheckBox _lowerPane = new() { Text = "Show the detail pane under the process list" };
  private readonly CheckBox _hideTabs = new() { Text = "Hide a properties tab this machine cannot fill" };

  private readonly CheckBox _busiest = new() { Text = "Open the performance page on whatever is busiest" };
  private readonly CheckBox _compact = new() { Text = "Open the performance page tightened up" };
  private readonly Button _thresholds = new() { Text = "Highlighting thresholds…" };

  private readonly Label _intervalCaption = new() { Text = "Sample every" };
  private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Label _cpuCaption = new() { Text = "CPU percentages are" };
  private readonly ComboBox _cpuMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Label _decimalsCaption = new() { Text = "Percentages are written with" };
  private readonly ComboBox _decimals = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Label _groupingCaption = new() { Text = "Group the process list by" };
  private readonly ComboBox _grouping = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  /// <summary>
  /// How the terminal draws a history column, including letting it decide for itself.
  /// </summary>
  /// <remarks>
  /// A picker rather than the tickbox this used to be. The tickbox could only say "blocks or ASCII",
  /// which left the braille style — twice the samples in the same width — reachable from a flag and
  /// from nowhere a person would find it, and unremembered between runs either way.
  /// </remarks>
  private readonly ComboBox _graphs = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  private readonly Label _graphsCaption = new() { Text = "Terminal history columns" };

  /// <summary>
  /// The offered styles, in the order of how much a terminal has to be able to draw. Null is first
  /// because it is the default and because it is the answer that is right on a terminal nobody here
  /// has seen.
  /// </summary>
  private static readonly (GraphStyle? Style, string Label)[] _Graphs = [
    (null, "whatever the terminal can draw"),
    (GraphStyle.Blocks, "blocks — 1 sample a column, 8 levels"),
    (GraphStyle.Braille, "braille — 2 samples a column, 4 levels"),
    (GraphStyle.Ascii, "punctuation, for a plainer terminal"),
    (GraphStyle.Numbers, "no plot — the figures instead"),
  ];
  private readonly CheckBox _mouse = new() { Text = "Terminal: read the mouse" };

  private readonly Label _file = new();
  private readonly Button _import = new() { Text = "Import…" };
  private readonly Button _export = new() { Text = "Export…" };
  private readonly Button _defaults = new() { Text = "Restore defaults" };
  private readonly Button _ok = new() { Text = "OK" };
  private readonly Button _cancel = new() { Text = "Cancel" };

  private readonly SettingsLocation _location;

  /// <summary>
  /// The record everything not shown here is carried through in.
  /// </summary>
  /// <remarks>
  /// Replaced wholesale by an import, which is what an import means: the file somebody chose, not
  /// that file merged into this one.
  /// </remarks>
  private UserSettings _carried;

  /// <summary>
  /// Which entry the interval picker was filled with, so an untouched picker writes nothing back.
  /// </summary>
  /// <remarks>
  /// Six entries against a file that takes any number: without this, opening the box on a file that
  /// says <c>interval=3</c> and pressing OK writes 2.
  /// </remarks>
  private int _filledInterval = -1;

  /// <param name="settings">The settings as they stand, including everything this box cannot show.</param>
  /// <param name="location">Which file is being edited, and what put it there.</param>
  public SettingsDialog(UserSettings settings, SettingsLocation location) {
    ArgumentNullException.ThrowIfNull(settings);

    this._carried = settings;
    this._location = location;

    this.Text = "Settings";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;

    foreach (var seconds in UserSettings.OfferedIntervalSeconds)
      this._interval.Items.Add(UserSettings.NameOfInterval(seconds));

    this._interval.Items.Add(_ByHand);

    // Named as what the number means rather than as the enum: "of one core" and "of the machine" is
    // the whole of the difference, and a picker reading "PerCore" makes the reader guess which
    // (PRD §3.2).
    this._cpuMode.Items.Add("a share of the whole machine");
    this._cpuMode.Items.Add("a share of one core");

    // Worded as what the reader will see rather than as a count of digits: "1 decimal (12.3%)" says
    // what the column will look like, and "1" does not (PRD §15).
    for (var decimals = 0; decimals <= Humanize.MaximumPercentDecimals; ++decimals)
      this._decimals.Items.Add(NameOfPrecision(decimals));

    foreach (var grouping in _Groupings)
      this._grouping.Items.Add(Describe(grouping));

    this.Fill(settings);

    this._thresholds.Click += (_, _) => this.EditThresholds();
    this._defaults.Click += (_, _) => this.Fill(new UserSettings {
      // The defaults for what this box shows; the rest of the file is left alone, because
      // "restore defaults" pressed in a preferences box means the preferences, not somebody's saved
      // column sets. The whole-file reset is `--reset-settings`, and it says what it removes.
      ColumnSets = this._carried.ColumnSets,
      Colours = this._carried.Colours,
      TerminalColours = this._carried.TerminalColours,
      Unknown = this._carried.Unknown,
      DesktopColumns = this._carried.DesktopColumns,
      TerminalColumns = this._carried.TerminalColumns,
      DesktopColumnWidths = this._carried.DesktopColumnWidths,
      PinnedDesktopColumns = this._carried.PinnedDesktopColumns,
      PinnedTerminalColumns = this._carried.PinnedTerminalColumns,
      WindowWidth = this._carried.WindowWidth,
      WindowHeight = this._carried.WindowHeight,
      SplitPercent = this._carried.SplitPercent,
    });

    this._import.Click += (_, _) => this.Import();
    this._export.Click += (_, _) => this.Export();
    this._ok.Click += (_, _) => {
      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();

    this.Section("What the window does", 0);
    this.Row(this._confirm);
    this.Row(this._lowerPane);
    this.Row(this._hideTabs);

    this.Section("The performance page", _HeadingGap);
    this.Row(this._busiest);
    this.Row(this._compact);
    this.Row(this._thresholds);

    this.Section("How often, and what the numbers mean", _HeadingGap);
    this.Pair(this._intervalCaption, this._interval);
    this.Pair(this._cpuCaption, this._cpuMode);
    this.Pair(this._decimalsCaption, this._decimals);
    this.Pair(this._groupingCaption, this._grouping);

    this.Section("The terminal front-end", _HeadingGap);
    this.Pair(this._graphsCaption, this._graphs);
    this.Row(this._mouse);

    // A picker's caption is a separate label, so the picker itself has no text to be announced by
    // and reads as an unlabelled combo box (PRD §74). The checkboxes above need none: their caption
    // is their own text.
    foreach (var (picker, caption) in (ReadOnlySpan<(ComboBox Picker, Label Caption)>)[
      (this._interval, this._intervalCaption),
      (this._cpuMode, this._cpuCaption),
    ]) {
      picker.AccessibleName = caption.Text;
      caption.AccessibleRole = AccessibleRole.StaticText;
    }

    this._file.Text = "Settings file: " + location.Explain();
    this._file.AccessibleRole = AccessibleRole.StaticText;
    this._import.AccessibleDescription = "Replaces every setting with another file's. Nothing is written until OK.";
    this._export.AccessibleDescription = "Writes what is on screen to another file, keys this build does not understand included.";
    this._defaults.AccessibleDescription = "Puts the settings above back to their built-in values. Column sets and colours are left alone.";
    this.Controls.Add(this._file);
    this.Controls.Add(this._import);
    this.Controls.Add(this._export);
    this.Controls.Add(this._defaults);
    this.Controls.Add(this._ok);
    this.Controls.Add(this._cancel);

    this.Describe();

    // Sized to what it holds rather than to a round number: a box with a band of nothing across the
    // middle is what a fixed height produces the moment a row is added, and a row under the buttons
    // is what it produces when one is — neither of which any assertion here could see.
    var height = this.ContentHeight() + _Margin + 22 + 10 + _ButtonHeight + _Margin;
    this.Bounds = new(0, 0, _Width, height);
    this.MinimumSize = new(_Width, height);
    // Laid out by arithmetic rather than by anchoring: a child anchored inside a docked container
    // here grows without bound, which is what MainWindow's own layout note records.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>The groupings the picker offers, in the order §83 introduces them.</summary>
  private static readonly ProcessGrouping[] _Groupings = [
    ProcessGrouping.ParentTree,
    ProcessGrouping.None,
    ProcessGrouping.User,
    ProcessGrouping.Session,
    ProcessGrouping.Service,
    ProcessGrouping.Executable,
    ProcessGrouping.Container,
    ProcessGrouping.Cgroup,
    ProcessGrouping.Package,
  ];

  /// <summary>True when the box was closed with OK.</summary>
  public bool Accepted { get; private set; }

  /// <summary>
  /// The settings as the box now stands — the record it was handed, with what is on screen written
  /// over it.
  /// </summary>
  public UserSettings Settings {
    get {
      var chosen = this._interval.SelectedIndex;
      var manual = chosen == this._interval.Items.Count - 1;
      var settings = this._carried with {
        ConfirmDestructiveActions = this._confirm.Checked,
        LowerPaneVisible = this._lowerPane.Checked,
        HideUnavailableTabs = this._hideTabs.Checked,
        PerformanceOpensOnBusiest = this._busiest.Checked,
        CompactPerformancePage = this._compact.Checked,
        CpuMode = this._cpuMode.SelectedIndex == 1 ? CpuPercentMode.PerCore : CpuPercentMode.Normalized,
        PercentDecimals = Math.Clamp(this._decimals.SelectedIndex, 0, Humanize.MaximumPercentDecimals),
        Grouping = _Groupings[Math.Clamp(this._grouping.SelectedIndex, 0, _Groupings.Length - 1)],
        // The older key is kept in step rather than left behind: a build without tui.graphs reads
        // only that one, and it must not come back saying ASCII because somebody chose braille here.
        TerminalGraphs = _Graphs[Math.Clamp(this._graphs.SelectedIndex, 0, _Graphs.Length - 1)].Style,
        BlockCharacters = _Graphs[Math.Clamp(this._graphs.SelectedIndex, 0, _Graphs.Length - 1)].Style != GraphStyle.Ascii,
        TerminalMouse = this._mouse.Checked,
        ManualRefresh = manual,
      };

      // Kept when the tick is off, so switching it back on returns to the rate somebody chose rather
      // than to the default (PRD §12) — and kept when the picker was not touched, which is the case
      // that matters more. The file takes any number and this picker offers six, so a file saying
      // three seconds opens showing "2 s", the nearest one it has; writing that selection back would
      // silently round somebody's hand-written interval down every time they opened this box and
      // pressed OK.
      return manual || chosen < 0 || chosen == this._filledInterval
        ? settings
        : settings with { IntervalSeconds = UserSettings.OfferedIntervalSeconds[chosen] };
    }
  }

  /// <summary>Every line the box shows, for a test with no display to read it off.</summary>
  public string Description {
    get {
      var lines = new List<string>();
      foreach (var control in this._rows)
        lines.Add(control switch {
          CheckBox box => $"[{(box.Checked ? "x" : " ")}] {box.Text}",
          ComboBox picker => $"{this.CaptionOf(picker)}: {picker.SelectedItem}",
          _ => control.Text,
        });

      lines.Add(this._file.Text);
      return string.Join('\n', lines);
    }
  }

  /// <summary>How many settings the box offers — the empty-box detector a picture cannot give.</summary>
  public int RowCount => this._rows.Count;

  /// <summary>
  /// What every picker in the box is called, in the order they appear.
  /// </summary>
  /// <remarks>
  /// Exposed for one assertion: that no two of them share a caption. They used to be matched by a
  /// chain of comparisons ending in "otherwise it is the last one", so a fourth picker came up
  /// wearing the third one's label, in the third one's place — visible in a photograph and to
  /// nothing else.
  /// </remarks>
  public IReadOnlyList<string> PickerCaptions {
    get {
      var captions = new List<string>();
      foreach (var (picker, _) in this.Pickers())
        captions.Add(this.CaptionOf(picker));

      return captions;
    }
  }

  /// <summary>
  /// Every picker and the label that names it.
  /// </summary>
  /// <remarks>
  /// One table rather than a chain of comparisons ending in "otherwise it must be the last one".
  /// That fallthrough was silent and wrong the moment a fourth picker was added: it came up wearing
  /// the third one's caption, in the third one's place, and no assertion could see it — only a
  /// photograph could.
  /// </remarks>
  private (ComboBox Picker, Label Caption)[] Pickers() => [
    (this._interval, this._intervalCaption),
    (this._cpuMode, this._cpuCaption),
    (this._grouping, this._groupingCaption),
    (this._graphs, this._graphsCaption),
    (this._decimals, this._decimalsCaption),
  ];

  private string CaptionOf(ComboBox picker) {
    foreach (var (candidate, caption) in this.Pickers())
      if (ReferenceEquals(candidate, picker))
        return caption.Text;

    return string.Empty;
  }

  /// <summary>
  /// What a precision is called on screen: the count of digits and what a number will look like at it.
  /// </summary>
  /// <remarks>
  /// The example is the half that answers the question. "2" is a number somebody has to imagine a
  /// column of, and "2 decimals (12.34%)" is the column.
  /// </remarks>
  private static string NameOfPrecision(int decimals) {
    var example = 12.3456.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    return decimals switch {
      0 => $"no decimals ({example}%)",
      1 => $"1 decimal ({example}%)",
      _ => $"{decimals.ToString(CultureInfo.InvariantCulture)} decimals ({example}%)",
    };
  }

  #region filling and reading back

  private void Fill(UserSettings settings) {
    this._confirm.Checked = settings.ConfirmDestructiveActions;
    this._lowerPane.Checked = settings.LowerPaneVisible;
    this._hideTabs.Checked = settings.HideUnavailableTabs;
    this._busiest.Checked = settings.PerformanceOpensOnBusiest;
    this._compact.Checked = settings.CompactPerformancePage;
    if (this._graphs.Items.Count == 0)
      foreach (var (_, label) in _Graphs)
        this._graphs.Items.Add(label);

    // An older file that only said "blocks=false" states no style, and opens showing the punctuation
    // ramp rather than "whatever this terminal can draw" — which is what it was actually asking for.
    var chosen = settings.TerminalGraphs ?? (settings.BlockCharacters ? null : GraphStyle.Ascii);
    this._graphs.SelectedIndex = 0;
    for (var i = 0; i < _Graphs.Length; ++i)
      if (_Graphs[i].Style == chosen) {
        this._graphs.SelectedIndex = i;
        break;
      }
    this._mouse.Checked = settings.TerminalMouse;
    this._cpuMode.SelectedIndex = settings.CpuMode == CpuPercentMode.PerCore ? 1 : 0;
    this._decimals.SelectedIndex = Math.Clamp(settings.PercentDecimals, 0, Humanize.MaximumPercentDecimals);

    var grouping = Array.IndexOf(_Groupings, settings.Grouping);
    this._grouping.SelectedIndex = grouping < 0 ? 0 : grouping;

    if (settings.ManualRefresh) {
      this._interval.SelectedIndex = this._interval.Items.Count - 1;
      this._filledInterval = this._interval.SelectedIndex;
      return;
    }

    // The nearest offered rate, because the file takes any number and this picker offers six. A
    // settings file saying 3 seconds must not come back as 1 (PRD §12).
    var nearest = 0;
    for (var i = 1; i < UserSettings.OfferedIntervalSeconds.Count; ++i)
      if (Math.Abs(UserSettings.OfferedIntervalSeconds[i] - settings.IntervalSeconds)
          < Math.Abs(UserSettings.OfferedIntervalSeconds[nearest] - settings.IntervalSeconds))
        nearest = i;

    this._interval.SelectedIndex = nearest;
    this._filledInterval = nearest;
  }

  private void EditThresholds() {
    var dialog = new HighlightThresholdsDialog(this._carried.Thresholds);
    dialog.ShowDialog();
    if (dialog.Accepted)
      this._carried = this._carried with { Thresholds = dialog.Thresholds };
  }

  /// <summary>
  /// Reads another file in, replacing everything — including the parts this box does not show.
  /// </summary>
  /// <remarks>
  /// The point of an import is to make this machine's settings be that file's, so merging would be
  /// the wrong answer: somebody moving to a new machine wants their column sets, not this machine's
  /// column sets with their checkboxes on top. Nothing is written until OK, so an import opened by
  /// accident is one Cancel away from having changed nothing.
  /// </remarks>
  private void Import() {
    var dialog = new OpenFileDialog {
      Title = "Import settings",
      Filter = "Settings (*.conf)|*.conf|All files|*.*",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName.Length == 0)
      return;

    if (!File.Exists(dialog.FileName)) {
      MessageBox.Show($"There is no file at {dialog.FileName}.", "Process Manager");
      return;
    }

    this._carried = SettingsStore.Load(dialog.FileName);
    this.Fill(this._carried);
    this.Describe();
    this._file.Text = $"Read from {dialog.FileName}; written to {this._location.Path} when you press OK";
  }

  private void Export() {
    var dialog = new SaveFileDialog {
      Title = "Export settings",
      FileName = SettingsStore.FileName,
      Filter = "Settings (*.conf)|*.conf|All files|*.*",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName.Length == 0)
      return;

    // What is on screen rather than what is on disk: somebody who has just changed six things and
    // pressed Export means the six things.
    this._file.Text = SettingsStore.Save(this.Settings, dialog.FileName)
      ? $"Written to {dialog.FileName}"
      : $"{dialog.FileName} could not be written";
  }

  /// <summary>The one line the thresholds button cannot say for itself.</summary>
  private void Describe()
    => this._thresholds.AccessibleDescription =
      "When a cell is marked as busy. " + this._carried.Thresholds.WarmCpuPercent.ToString("0.#", CultureInfo.InvariantCulture)
      + "% of a core is warm; " + this._carried.Thresholds.HotCpuPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% is hot.";

  private static string Describe(ProcessGrouping grouping) => grouping switch {
    ProcessGrouping.ParentTree => "parentage — the process tree",
    ProcessGrouping.None => "nothing — one flat list",
    ProcessGrouping.User => "the account that owns them",
    ProcessGrouping.Session => "the login session",
    ProcessGrouping.Service => "the service that started them",
    ProcessGrouping.Executable => "the program on disk",
    ProcessGrouping.Container => "the container",
    ProcessGrouping.Cgroup => "the cgroup",
    _ => "the installed package",
  };

  #endregion

  #region layout

  private void Section(string text, int gap) {
    var heading = new Label { Text = text };
    this._headings.Add((heading, gap));
    this.Controls.Add(heading);
    this._rows.Add(heading);
  }

  private void Row(Control control) {
    this.Controls.Add(control);
    this._rows.Add(control);
  }

  private void Pair(Label caption, Control field) {
    this.Controls.Add(caption);
    this.Controls.Add(field);
    this._rows.Add(field);
  }

  /// <summary>
  /// How tall the settings themselves are, headings and their gaps included.
  /// </summary>
  /// <remarks>
  /// Counted off the rows rather than written out as a number, for the reason the legend's height is:
  /// a row added to a box whose height is a literal either lands under the buttons or leaves a band
  /// of nothing across the middle, and no assertion can see either.
  /// </remarks>
  private static int HeightOf(Control row) => row is ComboBox or Button ? _PickerRowHeight : _RowHeight;

  private int ContentHeight() {
    var height = _Margin;
    var heading = 0;
    foreach (var row in this._rows) {
      if (heading < this._headings.Count && ReferenceEquals(row, this._headings[heading].Heading)) {
        height += this._headings[heading].Gap;
        ++heading;
      }

      height += HeightOf(row);
    }

    return height;
  }

  public void ApplyLayout() {
    var y = _Margin;
    var heading = 0;
    var fieldLeft = _Margin + _LabelWidth;
    var wide = Math.Max(200, this.Width - (_Margin * 2));

    foreach (var row in this._rows) {
      if (heading < this._headings.Count && ReferenceEquals(row, this._headings[heading].Heading)) {
        y += this._headings[heading].Gap;
        ++heading;
      }

      if (row is ComboBox) {
        // The caption sits beside the picker rather than above it, so a settings box of three
        // pickers is three rows tall and not six.
        this.CaptionFor(row).Bounds = new(_Margin, y + 6, _LabelWidth - 10, 20);
        row.Bounds = new(fieldLeft, y, _FieldWidth, _ButtonHeight);
      } else if (row is Button button)
        button.Bounds = new(_Margin, y, _WideButtonWidth, _ButtonHeight);
      else
        row.Bounds = new(_Margin, y, wide, 22);

      y += HeightOf(row);
    }

    var buttons = Math.Max(y + 40, this.Height - _Margin - _ButtonHeight);
    this._file.Bounds = new(_Margin, buttons - 28, wide, 20);
    this._cancel.Bounds = new(this.Width - _Margin - _ButtonWidth, buttons, _ButtonWidth, _ButtonHeight);
    this._ok.Bounds = new(this._cancel.Bounds.X - _ButtonWidth - 10, buttons, _ButtonWidth, _ButtonHeight);
    this._defaults.Bounds = new(_Margin, buttons, 160, _ButtonHeight);
    this._import.Bounds = new(_Margin + 170, buttons, _ButtonWidth, _ButtonHeight);
    this._export.Bounds = new(_Margin + 170 + _ButtonWidth + 10, buttons, _ButtonWidth, _ButtonHeight);
  }

  private Label CaptionFor(Control field) {
    foreach (var (picker, caption) in this.Pickers())
      if (ReferenceEquals(picker, field))
        return caption;

    // Only reachable for a row that is not a picker at all, which the caller does not ask about.
    return this._groupingCaption;
  }

  #endregion

}
