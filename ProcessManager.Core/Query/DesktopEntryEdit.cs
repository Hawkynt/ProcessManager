namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Turning a <c>.desktop</c> file's autostart on and off, as text (PRD §42).
/// </summary>
/// <remarks>
/// <para>
/// In Core and touching no file, so the rule can be replayed against fixtures on every CI leg. What
/// it produces is the whole new contents of a file; who writes it, and where, is the platform's
/// business.
/// </para>
/// <para>
/// <c>Hidden=true</c> is the specification's mechanism and the only one written. The desktop-entry
/// specification is explicit that a hidden entry should be treated as though it were not there at
/// all, which is precisely "the user turned this off" — and unlike deleting the file it is
/// reversible, and unlike <c>X-GNOME-Autostart-enabled</c> it is not one desktop's private
/// convention. The reader honours that key as well, because entries written by GNOME's own tools
/// carry it; a file that has it is cleared of it when enabled here, or the two keys would disagree
/// and the entry would stay off for reasons the file no longer visibly gives.
/// </para>
/// </remarks>
public static class DesktopEntryEdit {

  /// <summary>
  /// The contents that file should have for the entry to be on or off.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Every other line is preserved exactly, including comments, blank lines, translations and the
  /// action groups after the main one. A desktop file is somebody's, or their distribution's, and
  /// rewriting it from a parsed model would silently drop every key this program does not read —
  /// which is most of them.
  /// </para>
  /// <para>
  /// The keys are only ever touched inside <c>[Desktop Entry]</c>. An action group may legitimately
  /// carry its own <c>Hidden</c>, and setting that one would turn off a right-click item rather than
  /// the autostart.
  /// </para>
  /// </remarks>
  public static string Apply(string contents, bool enabled) {
    // Whatever the file already used. "Every other line is preserved exactly" has to include the
    // ends of them: rewriting a file's endings while claiming to leave it alone is exactly the sort
    // of quiet damage this reads a file line by line to avoid.
    var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var result = new List<string>(lines.Length + 1);

    var inDesktopEntry = false;
    var wroteHidden = false;
    // Where the key belongs if the file has not got one: the end of the main group, so it lands
    // among that group's keys rather than after some later group's.
    var endOfDesktopEntry = -1;

    for (var i = 0; i < lines.Length; ++i) {
      var line = lines[i];
      var trimmed = line.Trim();

      if (trimmed.StartsWith('[')) {
        if (inDesktopEntry)
          endOfDesktopEntry = result.Count;

        inDesktopEntry = trimmed.Equals("[Desktop Entry]", StringComparison.Ordinal);
        result.Add(line);
        continue;
      }

      if (!inDesktopEntry || trimmed.Length == 0 || trimmed[0] == '#') {
        result.Add(line);
        continue;
      }

      var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
      var key = separator > 0 ? trimmed[..separator].Trim() : string.Empty;

      switch (key) {
        case "Hidden":
          // Rewritten in place rather than removed and appended, so a file keeps its own order.
          if (!enabled && !wroteHidden) {
            result.Add("Hidden=true");
            wroteHidden = true;
          }

          continue;

        // Dropped when enabling, because a file carrying both would be off for a reason its
        // Hidden line no longer gives. Left alone when disabling: Hidden already says it.
        case "X-GNOME-Autostart-enabled" when enabled:
          continue;

        default:
          result.Add(line);
          continue;
      }
    }

    if (inDesktopEntry)
      endOfDesktopEntry = result.Count;

    if (!enabled && !wroteHidden)
      // No main group at all means nothing to hide and nowhere to say it. The file is not a desktop
      // entry, and inventing a group for it would make one.
      if (endOfDesktopEntry >= 0)
        result.Insert(endOfDesktopEntry, "Hidden=true");

    var joined = string.Join(newline, result);
    return joined.EndsWith(newline, StringComparison.Ordinal) ? joined : joined + newline;
  }

  /// <summary>
  /// What a user's override of a system entry should contain, given the system file.
  /// </summary>
  /// <remarks>
  /// A copy rather than a stub. The specification's rule is that a user file with the same name
  /// <em>replaces</em> the system one entirely rather than merging with it, so a two-line stub
  /// naming only <c>Hidden</c> would lose the entry's name, its command and everything else — and
  /// enabling it again afterwards would leave a file that starts nothing.
  /// </remarks>
  public static string Override(string systemContents) => Apply(systemContents, enabled: false);

}
