using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The one per-process energy reading PRD §22 accepts, and the three states it has to keep apart.
/// </summary>
/// <remarks>
/// <para>
/// Runs on every OS, for the reason the mitigation tests do: the call cannot be made from here and
/// the decoding is portable arithmetic, which is where a mistake that survives a demo would live.
/// The mistake this file exists to prevent is a specific one — collapsing three states into two.
/// <c>PROCESS_POWER_THROTTLING_STATE</c> carries a control mask and a state mask, and a bit missing
/// from the control mask means nobody has set that behaviour either way. Reading only the state mask
/// would report the whole machine as "not throttled", which is a confident answer to a question
/// nobody asked (PRD §72.3).
/// </para>
/// <para>
/// The bit values are transcribed from <c>PROCESS_POWER_THROTTLING_STATE</c>'s own documentation:
/// <c>PROCESS_POWER_THROTTLING_EXECUTION_SPEED</c> is <c>0x1</c> and
/// <c>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION</c> is <c>0x4</c>. There is no <c>0x2</c>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class EnergyFieldTests {

  private const ulong _EXECUTION_SPEED = 0x1;
  private const ulong _IGNORE_TIMER_RESOLUTION = 0x4;

  /// <summary>The two masks as the probe packs them: control low, state high.</summary>
  private static ProcessRecord Throttling(ulong control, ulong state)
    => new() { PowerThrottling = Counter.Of(control | (state << 32)) };

  private static string Text(ProcessField wanted, ProcessRecord record)
    => FieldAccessor.Text(wanted, in record, null, 0);

  private static string? Raw(ProcessField wanted, ProcessRecord record)
    => FieldAccessor.RawText(wanted, in record);

  [Test]
  public void AProcessNobodyHasSetAPolicyOnIsSystemManagedRatherThanUnthrottled() {
    var record = Throttling(0, 0);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, record), Is.EqualTo("system managed"));
    Assert.That(Text(ProcessField.EcoMode, record), Is.EqualTo("system managed"));

    // And a state bit set without the matching control bit is still nobody's decision: the state
    // mask is only meaningful for the bits the control mask claims.
    var stateOnly = Throttling(0, _EXECUTION_SPEED);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, stateOnly), Is.EqualTo("system managed"));
    Assert.That(Text(ProcessField.EcoMode, stateOnly), Is.EqualTo("system managed"));
  }

  [Test]
  public void AProcessPutIntoEfficiencyModeSaysSoInBothColumns() {
    var record = Throttling(_EXECUTION_SPEED, _EXECUTION_SPEED);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, record), Is.EqualTo("throttled"));
    Assert.That(Text(ProcessField.EcoMode, record), Is.EqualTo("on"));
    Assert.That(Raw(ProcessField.EcoMode, record), Is.EqualTo("on"));
  }

  [Test]
  public void AProcessHeldAtFullSpeedIsNotTheSameAsOneNobodyHasSet() {
    var record = Throttling(_EXECUTION_SPEED, 0);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, record), Is.EqualTo("high performance"));
    Assert.That(Text(ProcessField.EcoMode, record), Is.EqualTo("off"));
  }

  /// <summary>
  /// The second behaviour in the same word is named rather than dropped.
  /// </summary>
  /// <remarks>
  /// A decoder that read only the first bit would lose it silently, and it is a real setting with a
  /// real effect on a battery: a process whose requests for a finer system timer are being ignored.
  /// </remarks>
  [Test]
  public void TheTimerResolutionBitIsNotSwallowedByTheSpeedBit() {
    var both = Throttling(_EXECUTION_SPEED | _IGNORE_TIMER_RESOLUTION, _EXECUTION_SPEED | _IGNORE_TIMER_RESOLUTION);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, both), Is.EqualTo("throttled, timer resolution ignored"));
    // The efficiency column is deliberately the other question and says only what its own name means.
    Assert.That(Text(ProcessField.EcoMode, both), Is.EqualTo("on"));

    var timerOnly = Throttling(_IGNORE_TIMER_RESOLUTION, _IGNORE_TIMER_RESOLUTION);
    Assert.That(Text(ProcessField.BackgroundQualityOfService, timerOnly), Is.EqualTo("system managed, timer resolution ignored"));
    Assert.That(Text(ProcessField.EcoMode, timerOnly), Is.EqualTo("system managed"));
  }

  /// <summary>
  /// A reading nobody took, and one nobody was allowed to take, are not a process that is running
  /// flat out.
  /// </summary>
  [Test]
  public void AReadingThatWasNeverTakenIsNotAPolicyThatIsOff() {
    foreach (var (reading, reason) in new[] {
      (Counter.NotSampledYet, UnknownReason.NotSampledYet),
      (Counter.NotPermitted, UnknownReason.NotPermitted),
      (Counter.NotSupported, UnknownReason.NotSupportedOnPlatform),
    }) {
      var record = new ProcessRecord { PowerThrottling = reading };
      var placeholder = Humanize.Placeholder(reason);
      Assert.That(record.PowerThrottling.HasValue, Is.False);
      Assert.That(Text(ProcessField.BackgroundQualityOfService, record), Is.EqualTo(placeholder));
      Assert.That(Text(ProcessField.EcoMode, record), Is.EqualTo(placeholder));
      Assert.That(Raw(ProcessField.BackgroundQualityOfService, record), Is.Null);
      Assert.That(Raw(ProcessField.EcoMode, record), Is.Null);
      Assert.That(FieldAccessor.Number(ProcessField.EcoMode, in record, null, 0), Is.Null);
    }
  }

  /// <summary>
  /// The default record — the one nobody filled — must not read as an answer.
  /// </summary>
  /// <remarks>
  /// <c>default(Counter)</c> is a confident nought: it reports <c>HasValue</c> because its reason is
  /// <see cref="UnknownReason.None"/>. That is the defect this repository keeps meeting, and the
  /// reason every probe's records go through <c>ClearPlatformReadings</c> before anything fills them.
  /// </remarks>
  [Test]
  public void TheDefaultRecordDoesNotClaimAPolicy() {
    var record = new ProcessRecord();
    ProcessRecord.ClearPlatformReadings(ref record);
    Assert.That(record.PowerThrottling.HasValue, Is.False);
    Assert.That(
      Text(ProcessField.EcoMode, record),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

}
