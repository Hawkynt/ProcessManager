namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What an ARM processor can do under Windows, which asks one question at a time (PRD §46).
/// </summary>
/// <remarks>
/// <para>
/// ARM has no <c>CPUID</c> reachable from user code, and Windows has no auxiliary vector to publish
/// <c>AT_HWCAP</c> in. What it has instead is <c>IsProcessorFeaturePresent</c>: a call per feature,
/// answering yes or no to a numbered question. So where the Linux side decodes two words of bits,
/// this asks eighty questions once, at startup, and never again.
/// </para>
/// <para>
/// The ordinals are Windows' own and are an ABI — assigned once, answered the same way by every
/// release since. They are held against a vendored copy of the <c>PF_*</c> list in a test, for the
/// same reason the arm64 bit table is held against the kernel's header: nobody working on this has
/// a Windows-on-ARM machine, and a wrong ordinal produces a plausible feature list on a machine none
/// of us can look at.
/// </para>
/// <para>
/// The names match <see cref="ArmFeatures"/>' wherever the two describe the same silicon, so a
/// reader comparing a Windows tablet against a Linux board is comparing like with like rather than
/// two vendors' spellings (PRD §5.1).
/// </para>
/// </remarks>
public static class WindowsProcessorFeatures {

  private readonly record struct Feature(uint Ordinal, string Name, CpuFeatureKind Kind, string WindowsName);

  /// <summary>
  /// The ARM questions worth asking, in the order a reader wants the answers.
  /// </summary>
  /// <remarks>
  /// The x86 ordinals are deliberately absent. Windows on x86 answers the same questions
  /// <c>CPUID</c> does, in less detail and one call at a time, and <see cref="CpuFeatures"/> already
  /// has the instruction itself — asking the operating system instead would be a second, worse
  /// source for a table that already has a first (PRD §5.1).
  /// </remarks>
  private static readonly Feature[] _Arm = [
    new(19, "ASIMD (NEON)", CpuFeatureKind.InstructionSet, "PF_ARM_NEON_INSTRUCTIONS_AVAILABLE"),
    new(29, "ARMv8", CpuFeatureKind.InstructionSet, "PF_ARM_V8_INSTRUCTIONS_AVAILABLE"),
    new(24, "Integer divide", CpuFeatureKind.InstructionSet, "PF_ARM_DIVIDE_INSTRUCTION_AVAILABLE"),
    new(27, "FMAC", CpuFeatureKind.InstructionSet, "PF_ARM_FMAC_INSTRUCTIONS_AVAILABLE"),
    new(43, "DOTPROD", CpuFeatureKind.InstructionSet, "PF_ARM_V82_DP_INSTRUCTIONS_AVAILABLE"),
    new(67, "FP16", CpuFeatureKind.InstructionSet, "PF_ARM_V82_FP16_INSTRUCTIONS_AVAILABLE"),
    new(66, "I8MM", CpuFeatureKind.InstructionSet, "PF_ARM_V82_I8MM_INSTRUCTIONS_AVAILABLE"),
    new(68, "BF16", CpuFeatureKind.InstructionSet, "PF_ARM_V86_BF16_INSTRUCTIONS_AVAILABLE"),
    new(69, "EBF16", CpuFeatureKind.InstructionSet, "PF_ARM_V86_EBF16_INSTRUCTIONS_AVAILABLE"),
    new(44, "JSCVT", CpuFeatureKind.InstructionSet, "PF_ARM_V83_JSCVT_INSTRUCTIONS_AVAILABLE"),
    new(46, "SVE", CpuFeatureKind.InstructionSet, "PF_ARM_SVE_INSTRUCTIONS_AVAILABLE"),
    new(47, "SVE2", CpuFeatureKind.InstructionSet, "PF_ARM_SVE2_INSTRUCTIONS_AVAILABLE"),
    new(48, "SVE2p1", CpuFeatureKind.InstructionSet, "PF_ARM_SVE2_1_INSTRUCTIONS_AVAILABLE"),
    new(57, "SVE-I8MM", CpuFeatureKind.InstructionSet, "PF_ARM_SVE_I8MM_INSTRUCTIONS_AVAILABLE"),
    new(58, "SVE-F32MM", CpuFeatureKind.InstructionSet, "PF_ARM_SVE_F32MM_INSTRUCTIONS_AVAILABLE"),
    new(59, "SVE-F64MM", CpuFeatureKind.InstructionSet, "PF_ARM_SVE_F64MM_INSTRUCTIONS_AVAILABLE"),
    new(52, "SVE-BF16", CpuFeatureKind.InstructionSet, "PF_ARM_SVE_BF16_INSTRUCTIONS_AVAILABLE"),
    new(70, "SME", CpuFeatureKind.InstructionSet, "PF_ARM_SME_INSTRUCTIONS_AVAILABLE"),
    new(71, "SME2", CpuFeatureKind.InstructionSet, "PF_ARM_SME2_INSTRUCTIONS_AVAILABLE"),

    // One question covering four instruction groups, so it is named for all four: Windows asks
    // whether the optional cryptographic extension is there, and the architecture makes AES, PMULL,
    // SHA1 and SHA2 one option. Splitting it into four rows would claim four readings from one bit.
    new(30, "Crypto extension (AES, PMULL, SHA1, SHA2)", CpuFeatureKind.Cryptography, "PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE"),
    new(31, "CRC32", CpuFeatureKind.Cryptography, "PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE"),
    new(64, "SHA3", CpuFeatureKind.Cryptography, "PF_ARM_SHA3_INSTRUCTIONS_AVAILABLE"),
    new(65, "SHA512", CpuFeatureKind.Cryptography, "PF_ARM_SHA512_INSTRUCTIONS_AVAILABLE"),
    new(49, "SVE-AES", CpuFeatureKind.Cryptography, "PF_ARM_SVE_AES_INSTRUCTIONS_AVAILABLE"),
    new(50, "SVE-PMULL", CpuFeatureKind.Cryptography, "PF_ARM_SVE_PMULL128_INSTRUCTIONS_AVAILABLE"),
    new(51, "SVE-BITPERM", CpuFeatureKind.Cryptography, "PF_ARM_SVE_BITPERM_INSTRUCTIONS_AVAILABLE"),
    new(55, "SVE-SHA3", CpuFeatureKind.Cryptography, "PF_ARM_SVE_SHA3_INSTRUCTIONS_AVAILABLE"),
    new(56, "SVE-SM4", CpuFeatureKind.Cryptography, "PF_ARM_SVE_SM4_INSTRUCTIONS_AVAILABLE"),

    new(34, "LSE atomics", CpuFeatureKind.Other, "PF_ARM_V81_ATOMIC_INSTRUCTIONS_AVAILABLE"),
    new(62, "LSE2", CpuFeatureKind.Other, "PF_ARM_LSE2_AVAILABLE"),
    new(45, "LRCPC", CpuFeatureKind.Other, "PF_ARM_V83_LRCPC_INSTRUCTIONS_AVAILABLE"),
    new(25, "64-bit atomic load/store", CpuFeatureKind.Other, "PF_ARM_64BIT_LOADSTORE_ATOMIC"),
  ];

  /// <summary>
  /// Every ordinal this program asks about, with the name <c>winnt.h</c> gives it — so a test can
  /// hold the table against a vendored copy of that list and catch a transcription slip.
  /// </summary>
  public static IReadOnlyList<(uint Ordinal, string WindowsName)> KernelNames {
    get {
      var names = new (uint, string)[_Arm.Length];
      for (var i = 0; i < names.Length; ++i)
        names[i] = (_Arm[i].Ordinal, _Arm[i].WindowsName);

      return names;
    }
  }

  /// <summary>
  /// Everything the processor answers yes to, in table order.
  /// </summary>
  /// <param name="present">
  /// <c>IsProcessorFeaturePresent</c>, or a stand-in. A delegate rather than the call itself, so the
  /// whole table is exercised on every CI leg — including the ones on machines that have no such
  /// function to call (PRD §9.4).
  /// </param>
  public static IReadOnlyList<CpuFeature> Decode(Func<uint, bool> present) {
    ArgumentNullException.ThrowIfNull(present);

    var features = new List<CpuFeature>();
    foreach (var feature in _Arm)
      if (present(feature.Ordinal))
        features.Add(new(feature.Name, feature.Kind));

    return features;
  }

}
