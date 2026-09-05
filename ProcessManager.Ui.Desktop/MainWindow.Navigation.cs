using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Ui.Desktop;

public sealed partial class MainWindow {

  private readonly ShellNavigationHistory _navigation = new();
  private ToolStripButton? _backButton;
  private ToolStripButton? _forwardButton;
  private ToolStripButton? _breadcrumbButton;
  private bool _suppressNavigationHistory;

  /// <summary>Whether the shell has an earlier chronological location to revisit.</summary>
  public bool CanNavigateBack => this._navigation.CanGoBack;

  /// <summary>Whether the shell has a later chronological location to revisit.</summary>
  public bool CanNavigateForward => this._navigation.CanGoForward;

  /// <summary>The structural path to the current shell location.</summary>
  public string Breadcrumb => this.CurrentBreadcrumb.ToString();

  /// <summary>The exact process currently selected in the process view, or null.</summary>
  public ProcessKey? SelectedProcessKey => this._binder.SelectedRow?.Key;

  /// <summary>
  /// Adds Back, Forward and the structural path before the view-specific command buttons.
  /// </summary>
  private void BuildNavigationCommands() {
    this._backButton = Command("← Back", () => this.NavigateBack());
    this._forwardButton = Command("Forward →", () => this.NavigateForward());
    this._breadcrumbButton = Command("Processes", this.NavigateToBreadcrumbAncestor);

    this._commands.Items.Add(this._backButton);
    this._commands.Items.Add(this._forwardButton);
    this._commands.Items.Add(this._breadcrumbButton);
    this._commands.Items.Add(new ToolStripSeparator());
    this.UpdateNavigationCommands();
  }

  /// <summary>True for a command whose availability follows history rather than process selection.</summary>
  private bool IsNavigationCommand(ToolStripButton button)
    => ReferenceEquals(button, this._backButton)
      || ReferenceEquals(button, this._forwardButton)
      || ReferenceEquals(button, this._breadcrumbButton);

  /// <summary>Records the showing content view as a newly visited chronological location.</summary>
  private void RecordNavigationVisit() {
    if (this._suppressNavigationHistory || this._shown is null)
      return;

    this._navigation.Push(this.CurrentShellLocation());
    this.UpdateNavigationCommands();
  }

  /// <summary>
  /// Replaces the process identity attached to the current visit without adding another Back step.
  /// </summary>
  /// <remarks>
  /// Cursor movement is state within the process page. Treating every Up/Down key as a new visit
  /// makes Back an undo stack for row selection rather than navigation through the shell.
  /// </remarks>
  private void UpdateProcessNavigationState() {
    if (this._suppressNavigationHistory
      || !string.Equals(this._shown?.Title, "Processes", StringComparison.Ordinal)
      || this._navigation.Current is not { View: "Processes" })
      return;

    this._navigation.Replace(this.CurrentShellLocation());
    this.UpdateNavigationCommands();
  }

  private ShellLocation CurrentShellLocation() {
    var view = this._shown?.Title ?? string.Empty;
    return string.Equals(view, "Processes", StringComparison.Ordinal)
      ? new(view, this._binder.SelectedRow?.Key)
      : new(view);
  }

  private ShellBreadcrumb CurrentBreadcrumb {
    get {
      var location = this._navigation.Current ?? this.CurrentShellLocation();
      if (string.IsNullOrEmpty(location.View))
        return new("Processes");

      var processName = location.Process is { } key ? this._binder.RowFor(key)?.Name : null;
      return ShellBreadcrumb.For(location, processName);
    }
  }

  /// <summary>Moves one entry backward through chronological shell history.</summary>
  public bool NavigateBack() {
    if (this._navigation.Back() is not { } location) {
      this.UpdateNavigationCommands();
      return false;
    }

    this.ReplayNavigation(location);
    return true;
  }

  /// <summary>Moves one entry forward through chronological shell history.</summary>
  public bool NavigateForward() {
    if (this._navigation.Forward() is not { } location) {
      this.UpdateNavigationCommands();
      return false;
    }

    this.ReplayNavigation(location);
    return true;
  }

  /// <summary>
  /// Replays a history entry without creating another one.
  /// </summary>
  /// <remarks>
  /// A process entry is restored by its complete <see cref="ProcessKey"/>. If that process has
  /// ended, selection is cleared. Falling back to the PID would be wrong because the operating
  /// system may already have assigned that number to a different process.
  /// </remarks>
  private void ReplayNavigation(ShellLocation location) {
    var wasSuppressed = this._suppressNavigationHistory;
    this._suppressNavigationHistory = true;
    try {
      if (!this.ShowView(location.View))
        return;

      if (!string.Equals(location.View, "Processes", StringComparison.Ordinal))
        return;

      if (location.Process is { } key && this.SelectExactProcess(key))
        return;

      this._tree.SelectedNode = null;
      if (location.Process is not null)
        this._status.Text = "the process from this history entry has ended; no replacement process was selected";
    } finally {
      this._suppressNavigationHistory = wasSuppressed;
      this.UpdateNavigationCommands();
    }
  }

  /// <summary>Selects only the process with this exact identity pair.</summary>
  private bool SelectExactProcess(ProcessKey key) {
    if (this._binder.RowFor(key) is null || this.NodeFor(key) is not { } node)
      return false;

    this._tree.SelectedNode = node;
    this.UpdateDetails();
    return this._binder.SelectedRow?.Key == key;
  }

  /// <summary>
  /// Moves from the process leaf in the breadcrumb to its structural parent.
  /// </summary>
  /// <remarks>
  /// The current breadcrumb item itself does not reload anything. Only an ancestor is actionable,
  /// matching breadcrumb conventions rather than turning the path into a second Refresh button.
  /// </remarks>
  private void NavigateToBreadcrumbAncestor() {
    if (!this.CurrentBreadcrumb.HasAncestor)
      return;

    var wasSuppressed = this._suppressNavigationHistory;
    this._suppressNavigationHistory = true;
    try {
      this.ShowView("Processes");
      this._tree.SelectedNode = null;
    } finally {
      this._suppressNavigationHistory = wasSuppressed;
    }

    this._navigation.Push(new("Processes"));
    this.UpdateNavigationCommands();
  }

  private void UpdateNavigationCommands() {
    if (this._backButton is { } back)
      back.Enabled = this._navigation.CanGoBack;

    if (this._forwardButton is { } forward)
      forward.Enabled = this._navigation.CanGoForward;

    if (this._breadcrumbButton is not { } breadcrumbButton)
      return;

    var breadcrumb = this.CurrentBreadcrumb;
    var text = breadcrumb.ToString();
    breadcrumbButton.Text = text.Length <= 48 ? text : text[..45] + "…";
    breadcrumbButton.Enabled = breadcrumb.HasAncestor;
  }

  /// <summary>
  /// The navigation state safe to put in a public capture log.
  /// </summary>
  /// <remarks>
  /// A process name and PID are machine data. The capture needs to prove that a leaf is represented,
  /// not publish which process happened to be selected on the runner.
  /// </remarks>
  private string NavigationForCapture() {
    var location = this._navigation.Current ?? this.CurrentShellLocation();
    var breadcrumb = location switch {
      { View: "Processes", Process: not null } => "Processes › selected process",
      { View.Length: > 0 } => location.View,
      _ => "(none)",
    };

    return $"back {(this.CanNavigateBack ? "yes" : "no")}, forward {(this.CanNavigateForward ? "yes" : "no")}, breadcrumb {breadcrumb}";
  }
}
