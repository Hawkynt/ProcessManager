namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which channel and which band a frequency is (PRD §49).
/// </summary>
/// <remarks>
/// The arithmetic is fixed by the standard rather than by the driver: 2.4 GHz counts up in fives
/// from 2412, 5 GHz and 6 GHz count in fives from 5000 and 5950, and channel 14 is the one exception
/// in the whole plan — it sits twelve megahertz above channel 13 instead of five, and is Japan's
/// alone. A formula without that case reports it as channel 13, which is a different channel that
/// exists.
/// <para>
/// A pure function of a number, so it is tested on every CI leg without a wireless card anywhere
/// near it (PRD §9.2).
/// </para>
/// </remarks>
public static class WirelessChannel {

  /// <param name="Band">"2.4 GHz", "5 GHz", "6 GHz", "60 GHz" — the band as people name it.</param>
  /// <param name="Channel">The channel number, or 0 where the frequency is not on the grid.</param>
  public readonly record struct Placement(string Band, int Channel);

  /// <summary>Where a frequency in megahertz sits, or null when it is on no band we can name.</summary>
  public static Placement? Of(int megahertz) => megahertz switch {
    2484 => new("2.4 GHz", 14),
    >= 2412 and <= 2472 when (megahertz - 2412) % 5 == 0 => new("2.4 GHz", ((megahertz - 2412) / 5) + 1),
    // The 5 GHz plan starts at 5000 and runs in fives; the low end of it overlaps the 6 GHz band's
    // numbering, which is why the band has to be decided before the channel.
    >= 5160 and <= 5885 when megahertz % 5 == 0 => new("5 GHz", (megahertz - 5000) / 5),
    >= 5955 and <= 7115 when (megahertz - 5955) % 5 == 0 => new("6 GHz", ((megahertz - 5955) / 5) + 1),
    >= 57_240 and <= 70_200 => new("60 GHz", ((megahertz - 56_160) / 2160)),
    _ => null,
  };

}
