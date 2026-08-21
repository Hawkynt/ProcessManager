using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Everything a key can be bound to (PRD §57.3).</summary>
public enum TerminalAction : byte {

  None,

  MoveUp, MoveDown, PageUp, PageDown, MoveFirst, MoveLast,
  Collapse, Expand, Details, Quit,

  ToggleTree, Pause, RefreshNow, RefreshInterval, CpuMode, UserFilter, Search, Filter, CaseSensitive,
  Graphs, LowerPane, PaneGrow, PaneShrink, Help, GroupBy,

  SortNext, SortPrevious, SortReverse, SortAlso, SortByCpu, SortByMemory, SortByPid,

  ColumnPrevious, ColumnNext, ColumnMoveLeft, ColumnMoveRight, ColumnNarrower, ColumnWider,
  ColumnAutoSize, ColumnAutoSizeAll, ColumnFreeze, ColumnReset, ColumnChooser,
  ScrollLeft, ScrollRight,

  MarkToggle, MarkAll, MarkInvert, MarkNone, CopyCell, CopyRow, CopyColumn, Export,

  ActionMenu, EndTask, Terminate, TerminateTree, Restart, SuspendResume, SchedulingClass,
  Threads, Modules, Handles, Network, CountHandles, ServiceMenu,

}

/// <summary>One action, its default keys, and the sentence the help screen shows for it.</summary>
public readonly record struct BindingInfo(TerminalAction Action, string Name, string Group, string Description, string[] DefaultKeys);

/// <summary>
/// Which key does what, and where a person may say otherwise (PRD §57.3).
/// </summary>
/// <remarks>
/// <para>
/// A table rather than a switch, for two reasons that are really one. The help screen is generated
/// from it, so a binding that exists is a binding that is documented and a customised key shows up
/// in the help as the key it now is — a help screen listing the defaults on a machine with a
/// <c>keys.conf</c> is worse than none. And a rebound key needs exactly one place to be rebound in.
/// </para>
/// <para>
/// The file is <c>keys.conf</c> beside the settings file: one <c>action = key, key</c> line each.
/// Anything unparsable is collected in <see cref="Errors"/> and reported once on the status line
/// rather than thrown — a typo in a config file may not be the reason a monitor will not start
/// (PRD §81).
/// </para>
/// </remarks>
public sealed class KeyBindings {

  private static readonly BindingInfo[] _catalogue = [
    new(TerminalAction.MoveUp, "up", "Moving", "previous row", ["up"]),
    new(TerminalAction.MoveDown, "down", "Moving", "next row", ["down"]),
    new(TerminalAction.PageUp, "page-up", "Moving", "a screen back", ["pgup"]),
    new(TerminalAction.PageDown, "page-down", "Moving", "a screen on", ["pgdn"]),
    new(TerminalAction.MoveFirst, "first", "Moving", "first row", ["home"]),
    new(TerminalAction.MoveLast, "last", "Moving", "last row", ["end"]),
    new(TerminalAction.Collapse, "collapse", "Moving", "collapse this subtree, or go to the parent", ["left"]),
    new(TerminalAction.Expand, "expand", "Moving", "expand this subtree", ["right"]),
    new(TerminalAction.Details, "details", "Moving", "open the process pages", ["enter", "i"]),
    new(TerminalAction.Quit, "quit", "Moving", "back, and out", ["q", "f10", "esc"]),

    new(TerminalAction.ToggleTree, "tree", "View", "tree or flat", ["f5", "t"]),
    new(TerminalAction.Pause, "pause", "View", "stop and start sampling", ["p"]),
    new(TerminalAction.RefreshNow, "refresh", "View", "sample now", ["r"]),
    // `d` for delay, which is what every other terminal monitor binds this to.
    new(TerminalAction.RefreshInterval, "interval", "View", "how often it samples", ["d"]),
    new(TerminalAction.CpuMode, "cpu-mode", "View", "CPU% against the machine or against one core", ["C"]),
    new(TerminalAction.UserFilter, "user-filter", "View", "only my processes", ["u"]),
    new(TerminalAction.Search, "search", "View", "find a row and keep the rest", ["/", "f3"]),
    new(TerminalAction.Filter, "filter", "View", "hide everything that does not match", ["\\", "f"]),
    new(TerminalAction.CaseSensitive, "case", "View", "match case, or ignore it", ["!"]),
    new(TerminalAction.Graphs, "graphs", "View", "the performance page", ["g"]),
    new(TerminalAction.LowerPane, "lower-pane", "View", "show or hide the lower pane", ["tab"]),
    new(TerminalAction.PaneGrow, "pane-grow", "View", "a taller lower pane", ["+"]),
    new(TerminalAction.PaneShrink, "pane-shrink", "View", "a shorter lower pane", ["-"]),
    new(TerminalAction.Help, "help", "View", "this page", ["?", "f1"]),
    new(TerminalAction.GroupBy, "group", "View", "group the rows by user, session, service…", ["G"]),

    new(TerminalAction.SortNext, "sort-next", "Sorting", "sort by the next column", ["f6", ">"]),
    new(TerminalAction.SortPrevious, "sort-previous", "Sorting", "sort by the previous column", ["<"]),
    new(TerminalAction.SortReverse, "sort-reverse", "Sorting", "reverse the order", ["I"]),
    new(TerminalAction.SortAlso, "sort-also", "Sorting", "break ties with this column too", ["o"]),
    new(TerminalAction.SortByCpu, "sort-cpu", "Sorting", "sort by CPU", ["P"]),
    new(TerminalAction.SortByMemory, "sort-memory", "Sorting", "sort by memory", ["M"]),
    new(TerminalAction.SortByPid, "sort-pid", "Sorting", "sort by pid", ["N"]),

    new(TerminalAction.ColumnPrevious, "column-previous", "Columns", "the column to the left", ["["]),
    new(TerminalAction.ColumnNext, "column-next", "Columns", "the column to the right", ["]"]),
    new(TerminalAction.ColumnMoveLeft, "column-move-left", "Columns", "move this column left", ["{"]),
    new(TerminalAction.ColumnMoveRight, "column-move-right", "Columns", "move this column right", ["}"]),
    new(TerminalAction.ColumnNarrower, "column-narrower", "Columns", "narrower", [","]),
    new(TerminalAction.ColumnWider, "column-wider", "Columns", "wider", ["."]),
    new(TerminalAction.ColumnAutoSize, "column-autosize", "Columns", "fit this column to what is on screen", ["a"]),
    new(TerminalAction.ColumnAutoSizeAll, "column-autosize-all", "Columns", "fit every column", ["A"]),
    new(TerminalAction.ColumnFreeze, "column-freeze", "Columns", "pin the columns up to here", ["#"]),
    new(TerminalAction.ColumnReset, "column-reset", "Columns", "back to the default columns", ["0"]),
    new(TerminalAction.ColumnChooser, "columns", "Columns", "choose columns and column sets", ["c"]),
    new(TerminalAction.ScrollLeft, "scroll-left", "Columns", "scroll the table left", ["ctrl+left", "("]),
    new(TerminalAction.ScrollRight, "scroll-right", "Columns", "scroll the table right", ["ctrl+right", ")"]),

    new(TerminalAction.MarkToggle, "mark", "Selection", "tick this row", ["space"]),
    new(TerminalAction.MarkAll, "mark-all", "Selection", "tick every row shown", ["ctrl+a"]),
    new(TerminalAction.MarkInvert, "mark-invert", "Selection", "invert the ticks", ["v"]),
    new(TerminalAction.MarkNone, "mark-none", "Selection", "clear the ticks", ["U"]),
    new(TerminalAction.CopyCell, "copy-cell", "Selection", "copy the cell under the column cursor", ["y"]),
    new(TerminalAction.CopyRow, "copy-row", "Selection", "copy the row, or every ticked row", ["Y"]),
    // The third of the three, on the same letter: the cell, the row, then the column down the page.
    new(TerminalAction.CopyColumn, "copy-column", "Selection", "copy this column down every row", ["ctrl+y"]),
    new(TerminalAction.Export, "export", "Selection", "write the table to a file", ["X"]),

    new(TerminalAction.ActionMenu, "actions", "Process", "everything that can be done to this process", ["x"]),
    new(TerminalAction.EndTask, "end-task", "Process", "ask it to close", ["e"]),
    new(TerminalAction.Terminate, "terminate", "Process", "terminate it", ["k", "f9"]),
    new(TerminalAction.TerminateTree, "terminate-tree", "Process", "terminate it and everything under it", ["K"]),
    new(TerminalAction.Restart, "restart", "Process", "stop it and start it again", ["R"]),
    new(TerminalAction.SuspendResume, "suspend", "Process", "suspend or resume it", ["S"]),
    new(TerminalAction.SchedulingClass, "scheduling", "Process", "change its scheduler class", ["s"]),
    new(TerminalAction.Threads, "threads", "Process", "its threads", ["T"]),
    new(TerminalAction.Modules, "modules", "Process", "its loaded images", ["m"]),
    new(TerminalAction.Handles, "handles", "Process", "its handles and descriptors", ["h"]),
    new(TerminalAction.Network, "network", "Process", "its connections", ["n"]),
    new(TerminalAction.CountHandles, "count-handles", "Process", "count the handles of every row on screen", ["ctrl+h"]),
    new(TerminalAction.ServiceMenu, "service", "Process", "start, stop or restart the unit it belongs to", ["w"]),
  ];

  private readonly Dictionary<string, TerminalAction> _byKey = new(StringComparer.Ordinal);
  private readonly Dictionary<TerminalAction, List<string>> _byAction = [];

  private KeyBindings() { }

  /// <summary>What was wrong with the file, if anything was.</summary>
  public IReadOnlyList<string> Errors { get; private set; } = [];

  /// <summary>Every action, in the order the help screen lists them.</summary>
  public static IReadOnlyList<BindingInfo> Catalogue => _catalogue;

  /// <summary>The built-in bindings.</summary>
  public static KeyBindings Default {
    get {
      var bindings = new KeyBindings();
      foreach (var entry in _catalogue)
        foreach (var key in entry.DefaultKeys)
          bindings.Bind(key, entry.Action);

      return bindings;
    }
  }

  /// <summary>Where <c>keys.conf</c> lives: beside the settings file.</summary>
  public static string DefaultPath
    => Path.Combine(Path.GetDirectoryName(Settings.SettingsStore.Path) ?? ".", "keys.conf");

  /// <summary>Reads the file if there is one, falling back to the defaults for anything it omits.</summary>
  public static KeyBindings Load(string? path = null) {
    path ??= DefaultPath;
    try {
      return File.Exists(path) ? Parse(File.ReadAllText(path)) : Default;
    } catch (IOException) {
      return Default;
    } catch (UnauthorizedAccessException) {
      return Default;
    }
  }

  /// <summary>
  /// Parses a <c>keys.conf</c>. A named action loses its default keys; every other action keeps them.
  /// </summary>
  public static KeyBindings Parse(string? text) {
    var bindings = Default;
    if (string.IsNullOrWhiteSpace(text))
      return bindings;

    var errors = new List<string>();
    foreach (var raw in text.Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] is '#' or ';')
        continue;

      var separator = line.IndexOf('=', StringComparison.Ordinal);
      if (separator < 0) {
        errors.Add($"keys.conf: '{line}' is not action = key");
        continue;
      }

      var name = line[..separator].Trim();
      var action = TerminalAction.None;
      foreach (var entry in _catalogue)
        if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) {
          action = entry.Action;
          break;
        }

      if (action == TerminalAction.None) {
        errors.Add($"keys.conf: no action called '{name}'");
        continue;
      }

      var tokens = new List<string>();
      foreach (var key in line[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (Normalize(key) is { Length: > 0 } token)
          tokens.Add(token);
        else
          errors.Add($"keys.conf: '{key}' is not a key this understands");

      // A line that named no key this understands leaves the action alone: unbinding it would take a
      // typo and answer it by making the action unreachable, which is worse than ignoring the line.
      if (tokens.Count == 0)
        continue;

      // Rebinding an action drops what it had, so a file that says "quit = Q" does not leave q
      // quitting as well — which would make the file look ignored.
      bindings.Unbind(action);
      foreach (var token in tokens)
        bindings.Bind(token, action);
    }

    bindings.Errors = errors;
    return bindings;
  }

  private void Bind(string key, TerminalAction action) {
    var token = Normalize(key);
    if (token.Length == 0)
      return;

    this._byKey[token] = action;
    if (!this._byAction.TryGetValue(action, out var keys))
      this._byAction[action] = keys = [];

    if (!keys.Contains(token))
      keys.Add(token);
  }

  private void Unbind(TerminalAction action) {
    if (!this._byAction.TryGetValue(action, out var keys))
      return;

    foreach (var key in keys)
      this._byKey.Remove(key);

    keys.Clear();
  }

  /// <summary>What this keypress means here, or <see cref="TerminalAction.None"/>.</summary>
  public TerminalAction Resolve(ConsoleKeyInfo key)
    => this._byKey.TryGetValue(Token(key), out var action) ? action : TerminalAction.None;

  /// <summary>The keys bound to an action, as the help screen prints them.</summary>
  public string KeysFor(TerminalAction action) {
    if (!this._byAction.TryGetValue(action, out var keys) || keys.Count == 0)
      return "unbound";

    var builder = new StringBuilder(16);
    foreach (var key in keys)
      builder.Append(Display(key)).Append(' ');

    return builder.ToString(0, builder.Length - 1);
  }

  /// <summary>
  /// A token as a person writes it: <c>f5</c> is <c>F5</c> on a keyboard and in every manual.
  /// </summary>
  /// <remarks>
  /// Only for showing. The stored form stays lower case so that a <c>keys.conf</c> written in any of
  /// the four ways people spell "PgDn" resolves to the same binding.
  /// </remarks>
  public static string Display(string token) {
    var plus = token.LastIndexOf('+') + 1;
    var prefix = token[..plus];
    var name = token[plus..];
    if (name.Length <= 1)
      return prefix + name;

    return prefix + name switch {
      "up" => "Up",
      "down" => "Down",
      "left" => "Left",
      "right" => "Right",
      "pgup" => "PgUp",
      "pgdn" => "PgDn",
      "home" => "Home",
      "end" => "End",
      "enter" => "Enter",
      "esc" => "Esc",
      "tab" => "Tab",
      "space" => "Space",
      "backspace" => "Backspace",
      "del" => "Del",
      "ins" => "Ins",
      _ => name.ToUpperInvariant(),
    };
  }

  /// <summary>
  /// One keypress as the token a binding is written with.
  /// </summary>
  /// <remarks>
  /// A printable character is itself, case included — <c>k</c> and <c>K</c> are different bindings and
  /// one of them ends a process tree. Everything else is a name, so a file can say <c>pgdn</c>
  /// without anybody having to write an escape sequence into it.
  /// </remarks>
  public static string Token(ConsoleKeyInfo key) {
    var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
    var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
    var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

    var name = NameOf(key);
    if (name is null)
      return string.Empty;

    var builder = new StringBuilder(name.Length + 12);
    if (control)
      builder.Append("ctrl+");
    if (alt)
      builder.Append("alt+");
    // Shift is already in the character for anything printable; naming it as well would make "K" and
    // "shift+k" two different tokens for one keypress.
    if (shift && name.Length > 1)
      builder.Append("shift+");

    return builder.Append(name).ToString();
  }

  private static string? NameOf(ConsoleKeyInfo key) {
    switch (key.Key) {
      case ConsoleKey.UpArrow: return "up";
      case ConsoleKey.DownArrow: return "down";
      case ConsoleKey.LeftArrow: return "left";
      case ConsoleKey.RightArrow: return "right";
      case ConsoleKey.PageUp: return "pgup";
      case ConsoleKey.PageDown: return "pgdn";
      case ConsoleKey.Home: return "home";
      case ConsoleKey.End: return "end";
      case ConsoleKey.Enter: return "enter";
      case ConsoleKey.Escape: return "esc";
      case ConsoleKey.Tab: return "tab";
      case ConsoleKey.Backspace: return "backspace";
      case ConsoleKey.Delete: return "del";
      case ConsoleKey.Insert: return "ins";
      case ConsoleKey.Spacebar: return "space";
    }

    if (key.Key is >= ConsoleKey.F1 and <= ConsoleKey.F12)
      return "f" + ((int)key.Key - (int)ConsoleKey.F1 + 1).ToString(CultureInfo.InvariantCulture);

    var character = key.KeyChar;
    // The four that are control characters *and* keys with names of their own. Without this they
    // would be read as Ctrl+M, Ctrl+I and Ctrl+H on a console layer that names no key for them.
    switch (character) {
      case ' ': return "space";
      case '\r' or '\n': return "enter";
      case '\t': return "tab";
      case '\b' or '\u007f': return "backspace";
    }

    // Ctrl+letter arrives as the control character itself, which is not something a file can contain.
    // The modifier flag is not relied on: not every console layer sets it for a control character,
    // and a binding that works on one terminal and not another is worse than none.
    if (character is > '\0' and < ' ')
      return ((char)(character + 96)).ToString();

    return character is >= ' ' and not '\u007f' ? character.ToString() : null;
  }

  /// <summary>The written form of a key, as a token this can resolve.</summary>
  private static string Normalize(string key) {
    var text = key.Trim();
    if (text.Length == 0)
      return string.Empty;

    var prefix = string.Empty;
    while (true) {
      var plus = text.IndexOf('+', StringComparison.Ordinal);
      if (plus <= 0 || plus == text.Length - 1)
        break;

      var modifier = text[..plus].ToLowerInvariant();
      if (modifier is not ("ctrl" or "control" or "alt" or "meta" or "shift"))
        break;

      prefix += modifier switch {
        "control" => "ctrl+",
        "meta" => "alt+",
        _ => modifier + "+",
      };

      text = text[(plus + 1)..];
    }

    // Named keys are case-insensitive because nobody writes "PgDn" the same way twice; single
    // characters are not, because their case is the binding.
    var lowered = text.ToLowerInvariant();
    if (text.Length > 1)
      return lowered is "up" or "down" or "left" or "right" or "pgup" or "pgdn" or "pageup" or "pagedown"
          or "home" or "end" or "enter" or "return" or "esc" or "escape" or "tab" or "backspace"
          or "del" or "delete" or "ins" or "insert" or "space"
          || (lowered.Length is 2 or 3 && lowered[0] == 'f' && int.TryParse(lowered[1..], out var number) && number is >= 1 and <= 12)
        ? prefix + lowered switch {
          "pageup" => "pgup",
          "pagedown" => "pgdn",
          "return" => "enter",
          "escape" => "esc",
          "delete" => "del",
          "insert" => "ins",
          _ => lowered,
        }
        : string.Empty;

    return prefix + text;
  }

}
