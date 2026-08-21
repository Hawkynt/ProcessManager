using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Batteries and sensor chips, from <c>/sys</c> (PRD §45.3).
/// </summary>
/// <remarks>
/// On demand rather than on the sampling tick: a laptop's charge moves over minutes and a fan over
/// seconds, so reading a few dozen small files when somebody opens the page is affordable where
/// reading them four hundred times a second is not (PRD §5.4).
/// </remarks>
public sealed class SysfsSensorReader {

  private readonly string _powerSupplyRoot;
  private readonly string _hwmonRoot;

  public SysfsSensorReader(
    string powerSupplyRoot = "/sys/class/power_supply",
    string hwmonRoot = "/sys/class/hwmon"
  ) {
    this._powerSupplyRoot = powerSupplyRoot;
    this._hwmonRoot = hwmonRoot;
  }

  /// <summary>
  /// Every battery the machine has, in the order the kernel names them.
  /// </summary>
  /// <remarks>
  /// A desktop has none, which is an empty list rather than a failure — and rather than a battery
  /// reported at nought per cent, which is what a caller that assumed one would show.
  /// </remarks>
  public IReadOnlyList<BatteryInfo> ReadBatteries() {
    var batteries = new List<BatteryInfo>();
    foreach (var directory in Directories(this._powerSupplyRoot)) {
      var attributes = ReadAttributes(directory);
      if (!attributes.TryGetValue("type", out var type) || type != "Battery")
        continue;

      batteries.Add(PowerSupplyParser.Parse(Path.GetFileName(directory), attributes, this.OnExternalPower()));
    }

    return batteries;
  }

  /// <summary>
  /// Whether a mains supply says it is online.
  /// </summary>
  /// <remarks>
  /// Read from the mains supply rather than inferred from the battery's state, because "Full" and
  /// "Not charging" are both what a machine says while plugged in with a full battery, and neither
  /// of them is the question.
  /// </remarks>
  private bool OnExternalPower() {
    foreach (var directory in Directories(this._powerSupplyRoot)) {
      var attributes = ReadAttributes(directory);
      if (attributes.TryGetValue("type", out var type)
        && type == "Mains"
        && attributes.TryGetValue("online", out var online)
        && online == "1")
        return true;
    }

    return false;
  }

  /// <summary>Every sensor chip that reports at least one reading.</summary>
  public IReadOnlyList<SensorGroup> ReadSensors() {
    var groups = new List<SensorGroup>();
    foreach (var directory in Directories(this._hwmonRoot)) {
      var attributes = ReadAttributes(directory);
      if (!attributes.TryGetValue("name", out var name) || name.Length == 0)
        continue;

      var group = HwmonParser.Parse(name, attributes);
      if (group.Readings.Count > 0)
        groups.Add(group);
    }

    // Chips appear as hwmon0, hwmon1 … in whatever order they registered, which changes between
    // boots. Sorted by name so the page does not reorder itself when the machine is restarted.
    groups.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
    return groups;
  }

  private static IEnumerable<string> Directories(string root) {
    string[] entries;
    try {
      entries = Directory.GetDirectories(root);
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }

    Array.Sort(entries, StringComparer.Ordinal);
    return entries;
  }

  /// <summary>
  /// Every attribute in one directory.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read as a set rather than asked for by name: these directories publish what the driver felt
  /// like publishing, the numbering has gaps, and probing for a file that is not there is both
  /// slower and a worse description of what happened.
  /// </para>
  /// <para>
  /// Files that cannot be read are left out, not recorded as empty. <c>energy_uj</c> under
  /// <c>powercap</c> is root-only on any current kernel, and a caller must be able to tell "this
  /// machine does not report it" from "you may not read it" — the parser says the first when an
  /// attribute is absent, which is why an unreadable one must not arrive as an empty string.
  /// </para>
  /// </remarks>
  private static Dictionary<string, string> ReadAttributes(string directory) {
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
    string[] files;
    try {
      files = Directory.GetFiles(directory);
    } catch (IOException) {
      return attributes;
    } catch (UnauthorizedAccessException) {
      return attributes;
    }

    foreach (var file in files)
      try {
        attributes[Path.GetFileName(file)] = File.ReadAllText(file).Trim();
      } catch (IOException) {
        // Write-only, root-only, or a driver that returned an error for this attribute this time.
      } catch (UnauthorizedAccessException) {
      }

    return attributes;
  }

}
