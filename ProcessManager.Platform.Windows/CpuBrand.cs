using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The processor's own name, asked of the processor.
/// </summary>
/// <remarks>
/// Windows keeps this in the registry, but the chip will say it directly: leaves 0x80000002 through
/// 0x80000004 return the 48-character brand string, and leaf 0 returns the vendor. Asking the
/// hardware avoids a registry dependency the trimmer would have to be told about, and it is the same
/// string the registry holds because that is where Windows got it.
/// <para>
/// x86 and x64 only. ARM64 has no CPUID and no portable equivalent, so it reports that it does not
/// know rather than inventing a name.
/// </para>
/// </remarks>
internal static class CpuBrand {

  public readonly record struct Brand(string? Model, string? Vendor);

  public static Brand Read() {
    if (!X86Base.IsSupported)
      return default;

    return new(ReadModel(), ReadVendor());
  }

  private static string? ReadVendor() {
    // Leaf 0 returns the twelve-character vendor across ebx, edx, ecx — in that order, which is not
    // the order they come back in.
    var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
    var text = new StringBuilder(12);
    Append(text, ebx);
    Append(text, edx);
    Append(text, ecx);
    var vendor = text.ToString().Trim();
    return vendor.Length > 0 ? vendor : null;
  }

  private static string? ReadModel() {
    // Leaf 0x80000000 reports the highest extended leaf; the brand string needs 0x80000004.
    var (highest, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
    if ((uint)highest < 0x80000004)
      return null;

    var text = new StringBuilder(48);
    for (var leaf = 0x80000002; leaf <= 0x80000004; ++leaf) {
      var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((int)leaf), 0);
      Append(text, eax);
      Append(text, ebx);
      Append(text, ecx);
      Append(text, edx);
    }

    // The string is NUL-padded to its full 48 bytes and often carries runs of spaces inside it.
    var model = text.ToString().Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    while (model.Contains("  ", StringComparison.Ordinal))
      model = model.Replace("  ", " ", StringComparison.Ordinal);

    return model.Length > 0 ? model : null;
  }

  private static void Append(StringBuilder text, int register) {
    for (var i = 0; i < 4; ++i)
      text.Append((char)(byte)(register >> (i * 8)));
  }

  /// <summary>
  /// The rated speed out of the brand string's trailing clause, where the vendor put one.
  /// </summary>
  /// <remarks>
  /// The same reading the Linux side falls back to when the kernel does not publish
  /// <c>base_frequency</c>; kept here rather than shared because it is the only thing Windows has.
  /// </remarks>
  public static Counter BaseHertzFrom(string? model) {
    if (model is null)
      return Counter.Unknown(UnknownReason.NotImplementedHere);

    var at = model.LastIndexOf('@');
    if (at < 0)
      return Counter.NotSupported;

    var tail = model[(at + 1)..].Trim();
    var scale = tail.EndsWith("GHz", StringComparison.OrdinalIgnoreCase) ? 1_000_000_000d
      : tail.EndsWith("MHz", StringComparison.OrdinalIgnoreCase) ? 1_000_000d
      : 0d;

    if (scale == 0)
      return Counter.NotSupported;

    return double.TryParse(tail[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
      ? Counter.Of((ulong)(number * scale))
      : Counter.NotSupported;
  }

}
