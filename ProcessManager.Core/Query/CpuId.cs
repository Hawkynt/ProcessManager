using System.Runtime.Intrinsics.X86;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The processor's own answer about itself (PRD §46).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="X86Base.CpuId"/> rather than an interop call or a file: the runtime emits the
/// instruction directly, so this needs no native library, no <c>[LibraryImport]</c> and no
/// platform-specific probe — one implementation answers on Linux and Windows alike, and
/// <see cref="X86Base.IsSupported"/> is false on ARM rather than being an error there.
/// </para>
/// <para>
/// Read once and cached. A processor does not change its feature set, and the instruction
/// serialises the pipeline — cheap once, rude in a loop.
/// </para>
/// </remarks>
public static class CpuId {

  private static IReadOnlyList<CpuFeature>? _features;
  private static string? _vendor;
  private static string? _brand;
  private static bool _read;

  /// <summary>Whether this machine has a <c>CPUID</c> instruction at all.</summary>
  public static bool IsSupported => X86Base.IsSupported;

  /// <summary>Everything the processor reports it can do; empty where there is no <c>CPUID</c>.</summary>
  public static IReadOnlyList<CpuFeature> Features {
    get {
      Read();
      return _features ?? [];
    }
  }

  /// <summary><c>GenuineIntel</c>, <c>AuthenticAMD</c>, or null.</summary>
  public static string? Vendor {
    get {
      Read();
      return _vendor;
    }
  }

  /// <summary>The marketing name, or null where the processor does not publish one.</summary>
  public static string? Brand {
    get {
      Read();
      return _brand;
    }
  }

  /// <summary>
  /// Family, model and stepping — which silicon this is, as opposed to what it is called.
  /// </summary>
  /// <remarks>
  /// The name is marketing and two very different parts can share one; the signature is what an
  /// erratum, a microcode update or a mitigation is written against.
  /// </remarks>
  public static string? Signature {
    get {
      Read();
      return _signature;
    }
  }

  private static string? _signature;

  private static void Read() {
    if (_read)
      return;

    _read = true;
    if (!X86Base.IsSupported)
      return;

    _features = CpuFeatures.Decode(Leaf);
    _vendor = CpuFeatures.Vendor(Leaf);
    _brand = CpuFeatures.Brand(Leaf);
    _signature = CpuFeatures.Signature(Leaf) is { } id
      ? string.Format(
          System.Globalization.CultureInfo.InvariantCulture,
          "family {0}, model {1}, stepping {2}",
          id.Family,
          id.Model,
          id.Stepping
        )
      : null;
  }

  private static (int Eax, int Ebx, int Ecx, int Edx) Leaf(int leaf, int subLeaf)
    => X86Base.CpuId(leaf, subLeaf);

}
