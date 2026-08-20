using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Pressure stall information, from <c>/proc/pressure/*</c> (PRD §46, §47, §48).
/// </summary>
/// <remarks>
/// <para>
/// The best answer Linux has to "is this machine actually struggling", and a different question from
/// utilisation. A processor at 100 % is not in trouble if nothing is waiting for it; a processor at
/// 60 % with things queued behind it is. Pressure measures the second — how much of the last ten
/// seconds <em>something was stalled</em> — which is what a person means when they say the machine
/// feels slow.
/// </para>
/// <para>
/// <b>some</b> is any task stalled, <b>full</b> is every task stalled at once. Full is the serious
/// one: it means nothing ran at all, and a machine showing full memory pressure above a few percent
/// is thrashing rather than busy.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class PressureParser {

  /// <summary>
  /// Parses one pressure file. Lines the kernel does not emit leave their half unknown.
  /// </summary>
  /// <remarks>
  /// <c>/proc/pressure/irq</c> has only a <c>full</c> line — an interrupt stalls everything or
  /// nothing, so "some" would be meaningless — which is why the two halves are read independently
  /// rather than one being inferred from the other.
  /// </remarks>
  public static PressureReading Parse(ReadOnlySpan<char> text) {
    var some = PressureShare.Unknown;
    var full = PressureShare.Unknown;

    while (!text.IsEmpty) {
      var newline = text.IndexOf('\n');
      var line = newline < 0 ? text : text[..newline];
      text = newline < 0 ? default : text[(newline + 1)..];
      if (line.IsEmpty)
        continue;

      var space = line.IndexOf(' ');
      if (space <= 0)
        continue;

      var share = ParseShare(line[(space + 1)..]);
      if (line[..space].SequenceEqual("some"))
        some = share;
      else if (line[..space].SequenceEqual("full"))
        full = share;
    }

    return new(some, full);
  }

  private static PressureShare ParseShare(ReadOnlySpan<char> fields) {
    double? ten = null, sixty = null, threeHundred = null;
    ulong total = 0;
    var haveTotal = false;

    foreach (var range in fields.Split(' ')) {
      var field = fields[range].Trim();
      var equals = field.IndexOf('=');
      if (equals <= 0)
        continue;

      var name = field[..equals];
      var value = field[(equals + 1)..];

      if (name.SequenceEqual("total")) {
        haveTotal = ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
        continue;
      }

      if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var average))
        continue;

      if (name.SequenceEqual("avg10"))
        ten = average;
      else if (name.SequenceEqual("avg60"))
        sixty = average;
      else if (name.SequenceEqual("avg300"))
        threeHundred = average;
    }

    return ten is null && sixty is null && threeHundred is null && !haveTotal
      ? PressureShare.Unknown
      : new(
        ten is { } a ? Rate.Of(a) : Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
        sixty is { } b ? Rate.Of(b) : Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
        threeHundred is { } c ? Rate.Of(c) : Rate.Unknown(UnknownReason.NotSupportedOnPlatform),
        haveTotal ? Counter.Of(total) : Counter.NotSupported
      );
  }

}
