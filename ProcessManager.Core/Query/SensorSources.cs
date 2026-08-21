using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Where the batteries and sensor chips come from, when anything is asking (PRD §45.3).
/// </summary>
/// <remarks>
/// <para>
/// These are not on <c>ISystemProbe</c> deliberately. A battery is a property of the machine rather
/// than of any process, nothing samples it on the tick, and adding two members to that interface
/// would oblige every implementation of it — including the recorded-fixture ones the tests are built
/// out of — to answer a question about hardware they are not pretending to have.
/// </para>
/// <para>
/// Set once, by whoever knows what platform this is, in the same place and for the same reason as
/// the elevated helper. Null until then, and null forever on a platform with no reader, which is why
/// every consumer treats "no source" as "this machine has none" rather than as a failure.
/// </para>
/// </remarks>
public static class SensorSources {

  /// <summary>Every battery, or null where nothing can answer.</summary>
  public static Func<IReadOnlyList<BatteryInfo>>? Batteries { get; set; }

  /// <summary>Every sensor chip, or null where nothing can answer.</summary>
  public static Func<IReadOnlyList<SensorGroup>>? Sensors { get; set; }

  /// <summary>
  /// Asks a source, and treats a source that throws as a machine with none.
  /// </summary>
  /// <remarks>
  /// A sensor chip can disappear between two reads — a USB power supply unplugged, a driver
  /// unloaded — and a performance page must not close because a fan controller went away.
  /// </remarks>
  public static IReadOnlyList<T> Ask<T>(Func<IReadOnlyList<T>>? source) {
    if (source is null)
      return [];

    try {
      return source();
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

}
