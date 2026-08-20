namespace Hawkynt.ProcessManager.Query;

/// <summary>What a CPU feature is for, which is how the page groups them (PRD §46).</summary>
public enum CpuFeatureKind {

  /// <summary>SSE, AVX, AMX and the rest of the arithmetic.</summary>
  InstructionSet,

  /// <summary>Ciphers, hashes and random number generation in silicon.</summary>
  Cryptography,

  /// <summary>Mitigations and hardening: NX, SMEP, CET, the speculation controls.</summary>
  Security,

  /// <summary>Hardware virtualisation, and whether this machine is itself a guest.</summary>
  Virtualisation,

  /// <summary>Everything else worth naming — threading, timers, memory hints.</summary>
  Other,

}

/// <summary>One feature the processor reports.</summary>
public readonly record struct CpuFeature(string Name, CpuFeatureKind Kind);

/// <summary>
/// What the processor can do, decoded from <c>CPUID</c> (PRD §46).
/// </summary>
/// <remarks>
/// <para>
/// The decoding is a pure function of the register values, so the whole table is exercised on every
/// CI leg — including the ARM one, which has no <c>CPUID</c> instruction to run (PRD §9.2). A
/// machine's flags can be recorded once and asserted against forever, which is the only way to test
/// a feature nobody's laptop has.
/// </para>
/// <para>
/// Deliberately not the whole of what <c>CPUID</c> reports. Two hundred flags is a data dump; these
/// are the ones somebody reads a system page to find out — whether AVX-512 is really there, whether
/// the speculation mitigations are in silicon or in microcode, whether this is a virtual machine.
/// </para>
/// </remarks>
public static class CpuFeatures {

  /// <summary>Reads one <c>CPUID</c> leaf. Returns the four registers in order.</summary>
  public delegate (int Eax, int Ebx, int Ecx, int Edx) Reader(int leaf, int subLeaf);

  private readonly record struct Bit(int Leaf, int SubLeaf, int Register, int Index, string Name, CpuFeatureKind Kind);

  private const int _Eax = 0;
  private const int _Ebx = 1;
  private const int _Ecx = 2;
  private const int _Edx = 3;

  private const int _Extended = unchecked((int)0x80000000);

  /// <summary>
  /// The table, in the order a reader wants it: what the chip computes with, then what it secures
  /// with, then what it is.
  /// </summary>
  private static readonly Bit[] _Bits = [
    // Leaf 1, EDX — the old guard.
    new(1, 0, _Edx, 23, "MMX", CpuFeatureKind.InstructionSet),
    new(1, 0, _Edx, 25, "SSE", CpuFeatureKind.InstructionSet),
    new(1, 0, _Edx, 26, "SSE2", CpuFeatureKind.InstructionSet),
    new(1, 0, _Edx, 4, "TSC", CpuFeatureKind.Other),
    new(1, 0, _Edx, 15, "CMOV", CpuFeatureKind.Other),
    new(1, 0, _Edx, 28, "HTT", CpuFeatureKind.Other),

    // Leaf 1, ECX.
    new(1, 0, _Ecx, 0, "SSE3", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 9, "SSSE3", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 19, "SSE4.1", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 20, "SSE4.2", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 12, "FMA", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 28, "AVX", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 29, "F16C", CpuFeatureKind.InstructionSet),
    new(1, 0, _Ecx, 22, "MOVBE", CpuFeatureKind.Other),
    new(1, 0, _Ecx, 23, "POPCNT", CpuFeatureKind.Other),
    new(1, 0, _Ecx, 26, "XSAVE", CpuFeatureKind.Other),
    new(1, 0, _Ecx, 1, "PCLMULQDQ", CpuFeatureKind.Cryptography),
    new(1, 0, _Ecx, 25, "AES-NI", CpuFeatureKind.Cryptography),
    new(1, 0, _Ecx, 30, "RDRAND", CpuFeatureKind.Cryptography),
    new(1, 0, _Ecx, 5, "VMX", CpuFeatureKind.Virtualisation),
    // Not a capability but a fact, and the one people most want from this page: the bit a
    // hypervisor sets to admit it is there. No real processor sets it.
    new(1, 0, _Ecx, 31, "hypervisor", CpuFeatureKind.Virtualisation),

    // Leaf 7:0, EBX.
    new(7, 0, _Ebx, 5, "AVX2", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 3, "BMI1", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 8, "BMI2", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 16, "AVX-512F", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 17, "AVX-512DQ", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 21, "AVX-512IFMA", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 28, "AVX-512CD", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 30, "AVX-512BW", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 31, "AVX-512VL", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ebx, 9, "ERMS", CpuFeatureKind.Other),
    new(7, 0, _Ebx, 19, "ADX", CpuFeatureKind.Other),
    new(7, 0, _Ebx, 24, "CLWB", CpuFeatureKind.Other),
    new(7, 0, _Ebx, 18, "RDSEED", CpuFeatureKind.Cryptography),
    new(7, 0, _Ebx, 29, "SHA", CpuFeatureKind.Cryptography),
    new(7, 0, _Ebx, 7, "SMEP", CpuFeatureKind.Security),
    new(7, 0, _Ebx, 20, "SMAP", CpuFeatureKind.Security),

    // Leaf 7:0, ECX.
    new(7, 0, _Ecx, 1, "AVX-512VBMI", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ecx, 6, "AVX-512VBMI2", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ecx, 11, "AVX-512VNNI", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ecx, 12, "AVX-512BITALG", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ecx, 14, "AVX-512VPOPCNTDQ", CpuFeatureKind.InstructionSet),
    new(7, 0, _Ecx, 8, "GFNI", CpuFeatureKind.Cryptography),
    new(7, 0, _Ecx, 9, "VAES", CpuFeatureKind.Cryptography),
    new(7, 0, _Ecx, 10, "VPCLMULQDQ", CpuFeatureKind.Cryptography),
    new(7, 0, _Ecx, 2, "UMIP", CpuFeatureKind.Security),
    new(7, 0, _Ecx, 3, "PKU", CpuFeatureKind.Security),
    new(7, 0, _Ecx, 7, "CET-SS", CpuFeatureKind.Security),
    new(7, 0, _Ecx, 22, "RDPID", CpuFeatureKind.Other),
    new(7, 0, _Ecx, 28, "MOVDIR64B", CpuFeatureKind.Other),

    // Leaf 7:0, EDX.
    new(7, 0, _Edx, 8, "AVX-512VP2INTERSECT", CpuFeatureKind.InstructionSet),
    new(7, 0, _Edx, 23, "AVX-512FP16", CpuFeatureKind.InstructionSet),
    new(7, 0, _Edx, 22, "AMX-BF16", CpuFeatureKind.InstructionSet),
    new(7, 0, _Edx, 24, "AMX-TILE", CpuFeatureKind.InstructionSet),
    new(7, 0, _Edx, 25, "AMX-INT8", CpuFeatureKind.InstructionSet),
    new(7, 0, _Edx, 4, "FSRM", CpuFeatureKind.Other),
    new(7, 0, _Edx, 14, "SERIALIZE", CpuFeatureKind.Other),
    new(7, 0, _Edx, 20, "CET-IBT", CpuFeatureKind.Security),
    new(7, 0, _Edx, 26, "IBRS/IBPB", CpuFeatureKind.Security),
    new(7, 0, _Edx, 27, "STIBP", CpuFeatureKind.Security),
    new(7, 0, _Edx, 28, "L1D-FLUSH", CpuFeatureKind.Security),
    new(7, 0, _Edx, 31, "SSBD", CpuFeatureKind.Security),

    // Leaf 7:1, EAX — the newer arithmetic.
    new(7, 1, _Eax, 4, "AVX-VNNI", CpuFeatureKind.InstructionSet),
    new(7, 1, _Eax, 5, "AVX-512BF16", CpuFeatureKind.InstructionSet),

    // Extended leaf 0x80000001.
    new(_Extended + 1, 0, _Edx, 20, "NX", CpuFeatureKind.Security),
    new(_Extended + 1, 0, _Edx, 26, "1GB pages", CpuFeatureKind.Other),
    new(_Extended + 1, 0, _Edx, 27, "RDTSCP", CpuFeatureKind.Other),
    new(_Extended + 1, 0, _Edx, 29, "64-bit", CpuFeatureKind.Other),
    new(_Extended + 1, 0, _Ecx, 5, "LZCNT", CpuFeatureKind.Other),
    new(_Extended + 1, 0, _Ecx, 2, "SVM", CpuFeatureKind.Virtualisation),
  ];

  /// <summary>
  /// Every feature the processor reports, in table order.
  /// </summary>
  /// <remarks>
  /// A leaf beyond what the processor supports returns whatever was last in the registers rather
  /// than zero, so the maximum leaf has to be checked before every read — reading leaf 7 on a
  /// processor whose maximum is 1 invents a page of features it does not have.
  /// </remarks>
  public static IReadOnlyList<CpuFeature> Decode(Reader cpuid) {
    ArgumentNullException.ThrowIfNull(cpuid);

    var maximumBasic = cpuid(0, 0).Eax;
    var maximumExtended = cpuid(_Extended, 0).Eax;
    if (maximumBasic < 1)
      return [];

    var features = new List<CpuFeature>();
    foreach (var bit in _Bits) {
      var extended = bit.Leaf < 0;
      var highest = extended ? maximumExtended : maximumBasic;

      // Unsigned, because an extended leaf is 0x8000_0001 and a signed comparison makes that
      // negative and therefore always "supported".
      if ((uint)bit.Leaf > (uint)highest)
        continue;

      var registers = cpuid(bit.Leaf, bit.SubLeaf);
      var value = bit.Register switch {
        _Eax => registers.Eax,
        _Ebx => registers.Ebx,
        _Ecx => registers.Ecx,
        _ => registers.Edx,
      };

      if ((value & (1 << bit.Index)) != 0)
        features.Add(new(bit.Name, bit.Kind));
    }

    return features;
  }

  /// <summary>The vendor string of leaf 0, as the processor spells it: <c>GenuineIntel</c>.</summary>
  public static string? Vendor(Reader cpuid) {
    ArgumentNullException.ThrowIfNull(cpuid);

    var (_, ebx, ecx, edx) = cpuid(0, 0);
    if ((ebx | ecx | edx) == 0)
      return null;

    Span<char> text = stackalloc char[12];
    Write(text[..4], ebx);
    Write(text[4..8], edx);
    Write(text[8..], ecx);
    return new string(text).Trim();
  }

  /// <summary>
  /// The brand string of the extended leaves — the marketing name, which is the only place a
  /// processor says what it is called.
  /// </summary>
  public static string? Brand(Reader cpuid) {
    ArgumentNullException.ThrowIfNull(cpuid);

    if ((uint)cpuid(_Extended, 0).Eax < unchecked((uint)(_Extended + 4)))
      return null;

    Span<char> text = stackalloc char[48];
    for (var i = 0; i < 3; ++i) {
      var (eax, ebx, ecx, edx) = cpuid(_Extended + 2 + i, 0);
      var part = text.Slice(i * 16, 16);
      Write(part[..4], eax);
      Write(part[4..8], ebx);
      Write(part[8..12], ecx);
      Write(part[12..], edx);
    }

    var end = text.IndexOf('\0');
    var brand = new string(end < 0 ? text : text[..end]).Trim();
    return brand.Length > 0 ? brand : null;
  }

  /// <summary>
  /// The processor's own identity: family, model and stepping, as leaf 1 encodes them.
  /// </summary>
  /// <remarks>
  /// The encoding is the awkward part and the reason this is worth a function. Family and model are
  /// each split across two fields, and the extended halves are added for family 15 and folded in for
  /// families 6 and 15 respectively — an Intel Core reports family 6, model 13, and is only a Tiger
  /// Lake once the extended model's 8 is shifted in to make 141.
  /// </remarks>
  public static (int Family, int Model, int Stepping)? Signature(Reader cpuid) {
    ArgumentNullException.ThrowIfNull(cpuid);

    if (cpuid(0, 0).Eax < 1)
      return null;

    var eax = cpuid(1, 0).Eax;
    var family = (eax >> 8) & 0xF;
    var model = (eax >> 4) & 0xF;
    var extendedFamily = (eax >> 20) & 0xFF;
    var extendedModel = (eax >> 16) & 0xF;

    if (family == 0xF)
      family += extendedFamily;

    if (family is 0x6 or 0xF)
      model += extendedModel << 4;

    return (family, model, eax & 0xF);
  }

  /// <summary>Four little-endian bytes of a register as characters.</summary>
  private static void Write(Span<char> destination, int register) {
    for (var i = 0; i < 4; ++i)
      destination[i] = (char)((register >> (i * 8)) & 0xFF);
  }

}
