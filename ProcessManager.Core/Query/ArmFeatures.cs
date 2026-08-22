namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What an ARM processor can do, decoded from the kernel's hardware capability words (PRD §46).
/// </summary>
/// <remarks>
/// <para>
/// ARM has no <c>CPUID</c>. The identification registers exist but are privileged — <c>MRS</c> on
/// <c>ID_AA64ISAR0_EL1</c> traps from user code, and the kernel emulates only some of them and only
/// on some configurations. What every Linux kernel does publish is <c>AT_HWCAP</c> and
/// <c>AT_HWCAP2</c> in the auxiliary vector: two words of bits, one per feature, which is the
/// architecture's answer to the same question and the one every runtime actually uses.
/// </para>
/// <para>
/// The bit assignments are the kernel's, not the architecture's — they are an ABI between the kernel
/// and userspace, documented in <c>Documentation/arch/arm64/elf_hwcaps.rst</c>, and they never
/// change once assigned. That is what makes decoding them a pure function worth testing.
/// </para>
/// <para>
/// Same shape as the x86 table in <see cref="CpuFeatures"/> deliberately: both produce
/// <see cref="CpuFeature"/> in the same kinds, so the page that renders them does not need to know
/// which architecture it is describing.
/// </para>
/// </remarks>
public static class ArmFeatures {

  private readonly record struct Bit(int Word, int Index, string Name, CpuFeatureKind Kind);

  /// <summary>
  /// AT_HWCAP and AT_HWCAP2 for arm64, in the order a reader wants them.
  /// </summary>
  /// <remarks>
  /// Named as the architecture names them rather than as the kernel's lowercase strings: a reader
  /// looking for SVE2 should not have to know it is spelled <c>sve2</c> in one place and SVE2 in
  /// the manual. The exception is ASIMD, which is what ARM calls NEON in AArch64 and what everybody
  /// still calls NEON — so it says both.
  /// </remarks>
  private static readonly Bit[] _Arm64 = [
    new(1, 0, "FP", CpuFeatureKind.InstructionSet),
    new(1, 1, "ASIMD (NEON)", CpuFeatureKind.InstructionSet),
    new(1, 9, "FP16", CpuFeatureKind.InstructionSet),
    new(1, 10, "ASIMD-FP16", CpuFeatureKind.InstructionSet),
    new(1, 12, "RDMA", CpuFeatureKind.InstructionSet),
    new(1, 13, "JSCVT", CpuFeatureKind.InstructionSet),
    new(1, 14, "FCMA", CpuFeatureKind.InstructionSet),
    new(1, 20, "DOTPROD", CpuFeatureKind.InstructionSet),
    new(1, 22, "SVE", CpuFeatureKind.InstructionSet),
    new(1, 23, "ASIMD-FHM", CpuFeatureKind.InstructionSet),
    new(2, 1, "SVE2", CpuFeatureKind.InstructionSet),
    new(2, 9, "SVE-I8MM", CpuFeatureKind.InstructionSet),
    new(2, 10, "SVE-F32MM", CpuFeatureKind.InstructionSet),
    new(2, 11, "SVE-F64MM", CpuFeatureKind.InstructionSet),
    new(2, 12, "SVE-BF16", CpuFeatureKind.InstructionSet),
    new(2, 13, "I8MM", CpuFeatureKind.InstructionSet),
    new(2, 14, "BF16", CpuFeatureKind.InstructionSet),
    new(2, 23, "SME", CpuFeatureKind.InstructionSet),
    new(2, 37, "SME2", CpuFeatureKind.InstructionSet),
    new(2, 36, "SVE2p1", CpuFeatureKind.InstructionSet),
    new(2, 32, "EBF16", CpuFeatureKind.InstructionSet),
    new(2, 43, "MOPS", CpuFeatureKind.InstructionSet),

    new(1, 3, "AES", CpuFeatureKind.Cryptography),
    new(1, 4, "PMULL", CpuFeatureKind.Cryptography),
    new(1, 5, "SHA1", CpuFeatureKind.Cryptography),
    new(1, 6, "SHA2", CpuFeatureKind.Cryptography),
    new(1, 7, "CRC32", CpuFeatureKind.Cryptography),
    new(1, 17, "SHA3", CpuFeatureKind.Cryptography),
    new(1, 18, "SM3", CpuFeatureKind.Cryptography),
    new(1, 19, "SM4", CpuFeatureKind.Cryptography),
    new(1, 21, "SHA512", CpuFeatureKind.Cryptography),
    new(2, 2, "SVE-AES", CpuFeatureKind.Cryptography),
    new(2, 3, "SVE-PMULL", CpuFeatureKind.Cryptography),
    new(2, 4, "SVE-BITPERM", CpuFeatureKind.Cryptography),
    new(2, 5, "SVE-SHA3", CpuFeatureKind.Cryptography),
    new(2, 6, "SVE-SM4", CpuFeatureKind.Cryptography),
    new(2, 16, "RNG", CpuFeatureKind.Cryptography),

    // Pointer authentication and branch target identification are ARM's answer to the same problem
    // Intel's CET solves, so they sit in the same group as CET does on the other table.
    new(1, 30, "PACA", CpuFeatureKind.Security),
    new(1, 31, "PACG", CpuFeatureKind.Security),
    new(1, 28, "SSBS", CpuFeatureKind.Security),
    new(1, 29, "SB", CpuFeatureKind.Security),
    new(2, 17, "BTI", CpuFeatureKind.Security),
    new(2, 18, "MTE", CpuFeatureKind.Security),
    new(2, 22, "MTE3", CpuFeatureKind.Security),
    new(2, 63, "POE", CpuFeatureKind.Security),

    new(1, 8, "LSE atomics", CpuFeatureKind.Other),
    new(1, 15, "LRCPC", CpuFeatureKind.Other),
    new(1, 16, "DCPOP", CpuFeatureKind.Other),
    new(1, 24, "DIT", CpuFeatureKind.Other),
    new(1, 25, "USCAT", CpuFeatureKind.Other),
    new(1, 26, "ILRCPC", CpuFeatureKind.Other),
    new(1, 27, "FLAGM", CpuFeatureKind.Other),
    new(1, 11, "CPUID registers", CpuFeatureKind.Other),
    new(2, 0, "DCPODP", CpuFeatureKind.Other),
    new(2, 7, "FLAGM2", CpuFeatureKind.Other),
    new(2, 8, "FRINT", CpuFeatureKind.Other),
    new(2, 19, "ECV", CpuFeatureKind.Other),
    new(2, 20, "AFP", CpuFeatureKind.Other),
    new(2, 21, "RPRES", CpuFeatureKind.Other),
    new(2, 31, "WFXT", CpuFeatureKind.Other),
    new(2, 34, "CSSC", CpuFeatureKind.Other),
    new(2, 46, "LRCPC3", CpuFeatureKind.Other),
    new(2, 47, "LSE128", CpuFeatureKind.Other),
  ];

  /// <summary>
  /// The kernel's own name for each bit, so a test can hold the table against
  /// <c>arch/arm64/include/uapi/asm/hwcap.h</c> and catch a transcription slip.
  /// </summary>
  /// <remarks>
  /// These are an ABI and never change once assigned, but they were transcribed by hand and two of
  /// them were wrong on the first pass — <c>MTE3</c> and <c>SME</c> — which is exactly the kind of
  /// mistake nobody without ARM hardware would ever see (PRD §9.2).
  /// </remarks>
  public static IReadOnlyList<(int Word, int Index, string KernelName)> KernelNames { get; } = [
    (1, 0, "FP"), (1, 1, "ASIMD"), (1, 3, "AES"), (1, 4, "PMULL"), (1, 5, "SHA1"), (1, 6, "SHA2"),
    (1, 7, "CRC32"), (1, 8, "ATOMICS"), (1, 9, "FPHP"), (1, 10, "ASIMDHP"), (1, 11, "CPUID"),
    (1, 12, "ASIMDRDM"), (1, 13, "JSCVT"), (1, 14, "FCMA"), (1, 15, "LRCPC"), (1, 16, "DCPOP"),
    (1, 17, "SHA3"), (1, 18, "SM3"), (1, 19, "SM4"), (1, 20, "ASIMDDP"), (1, 21, "SHA512"),
    (1, 22, "SVE"), (1, 23, "ASIMDFHM"), (1, 24, "DIT"), (1, 25, "USCAT"), (1, 26, "ILRCPC"),
    (1, 27, "FLAGM"), (1, 28, "SSBS"), (1, 29, "SB"), (1, 30, "PACA"), (1, 31, "PACG"),
    (2, 0, "DCPODP"), (2, 1, "SVE2"), (2, 2, "SVEAES"), (2, 3, "SVEPMULL"), (2, 4, "SVEBITPERM"),
    (2, 5, "SVESHA3"), (2, 6, "SVESM4"), (2, 7, "FLAGM2"), (2, 8, "FRINT"), (2, 9, "SVEI8MM"),
    (2, 10, "SVEF32MM"), (2, 11, "SVEF64MM"), (2, 12, "SVEBF16"), (2, 13, "I8MM"), (2, 14, "BF16"),
    (2, 16, "RNG"), (2, 17, "BTI"), (2, 18, "MTE"), (2, 19, "ECV"), (2, 20, "AFP"), (2, 21, "RPRES"),
    (2, 22, "MTE3"), (2, 23, "SME"), (2, 31, "WFXT"), (2, 32, "EBF16"), (2, 34, "CSSC"),
    (2, 36, "SVE2P1"), (2, 37, "SME2"), (2, 43, "MOPS"), (2, 46, "LRCPC3"), (2, 47, "LSE128"),
    (2, 63, "POE"),
  ];

  /// <summary>
  /// AT_HWCAP and AT_HWCAP2 for 32-bit ARM, which share not one bit position with the table above.
  /// </summary>
  /// <remarks>
  /// A different word entirely: 32-bit ARM was assigning these before AArch64 existed, so NEON is
  /// bit 12 here and bit 1 there, and decoding one architecture's words with the other's table
  /// produces a full and entirely wrong feature list. That is why the architecture is chosen by the
  /// process's own, and not by whether the words happen to look plausible.
  /// <para>
  /// The names are the same as the arm64 table's wherever the two mean the same silicon — FP16,
  /// DOTPROD, BF16, I8MM — so a reader comparing a phone against a server is comparing like with
  /// like. Where 32-bit ARM has something AArch64 does not, it keeps its own name: VFP is not
  /// AArch64's FP and calling it that would be a claim about the instruction set.
  /// </para>
  /// </remarks>
  private static readonly Bit[] _Arm32 = [
    new(1, 2, "Thumb", CpuFeatureKind.InstructionSet),
    new(1, 11, "ThumbEE", CpuFeatureKind.InstructionSet),
    new(1, 5, "FPA", CpuFeatureKind.InstructionSet),
    new(1, 6, "VFP", CpuFeatureKind.InstructionSet),
    new(1, 13, "VFPv3", CpuFeatureKind.InstructionSet),
    new(1, 14, "VFPv3-D16", CpuFeatureKind.InstructionSet),
    new(1, 16, "VFPv4", CpuFeatureKind.InstructionSet),
    new(1, 19, "VFP-D32", CpuFeatureKind.InstructionSet),
    new(1, 7, "DSP extensions", CpuFeatureKind.InstructionSet),
    new(1, 8, "Jazelle", CpuFeatureKind.InstructionSet),
    new(1, 9, "iWMMXt", CpuFeatureKind.InstructionSet),
    new(1, 10, "Crunch", CpuFeatureKind.InstructionSet),
    new(1, 12, "NEON", CpuFeatureKind.InstructionSet),
    new(1, 17, "IDIVA", CpuFeatureKind.InstructionSet),
    new(1, 18, "IDIVT", CpuFeatureKind.InstructionSet),
    new(1, 22, "FP16", CpuFeatureKind.InstructionSet),
    new(1, 23, "ASIMD-FP16", CpuFeatureKind.InstructionSet),
    new(1, 24, "DOTPROD", CpuFeatureKind.InstructionSet),
    new(1, 25, "ASIMD-FHM", CpuFeatureKind.InstructionSet),
    new(1, 26, "BF16", CpuFeatureKind.InstructionSet),
    new(1, 27, "I8MM", CpuFeatureKind.InstructionSet),

    new(2, 0, "AES", CpuFeatureKind.Cryptography),
    new(2, 1, "PMULL", CpuFeatureKind.Cryptography),
    new(2, 2, "SHA1", CpuFeatureKind.Cryptography),
    new(2, 3, "SHA2", CpuFeatureKind.Cryptography),
    new(2, 4, "CRC32", CpuFeatureKind.Cryptography),

    new(2, 5, "SB", CpuFeatureKind.Security),
    new(2, 6, "SSBS", CpuFeatureKind.Security),

    new(1, 0, "SWP", CpuFeatureKind.Other),
    new(1, 1, "Half-word loads", CpuFeatureKind.Other),
    new(1, 3, "26-bit mode", CpuFeatureKind.Other),
    new(1, 4, "Fast multiply", CpuFeatureKind.Other),
    new(1, 15, "TLS register", CpuFeatureKind.Other),
    new(1, 20, "LPAE", CpuFeatureKind.Other),
    new(1, 21, "Event stream", CpuFeatureKind.Other),
  ];

  /// <summary>
  /// The kernel's own name for each 32-bit bit, for the same test the arm64 table gets.
  /// </summary>
  public static IReadOnlyList<(int Word, int Index, string KernelName)> Arm32KernelNames { get; } = [
    (1, 0, "SWP"), (1, 1, "HALF"), (1, 2, "THUMB"), (1, 3, "26BIT"), (1, 4, "FAST_MULT"),
    (1, 5, "FPA"), (1, 6, "VFP"), (1, 7, "EDSP"), (1, 8, "JAVA"), (1, 9, "IWMMXT"),
    (1, 10, "CRUNCH"), (1, 11, "THUMBEE"), (1, 12, "NEON"), (1, 13, "VFPv3"), (1, 14, "VFPv3D16"),
    (1, 15, "TLS"), (1, 16, "VFPv4"), (1, 17, "IDIVA"), (1, 18, "IDIVT"), (1, 19, "VFPD32"),
    (1, 20, "LPAE"), (1, 21, "EVTSTRM"), (1, 22, "FPHP"), (1, 23, "ASIMDHP"), (1, 24, "ASIMDDP"),
    (1, 25, "ASIMDFHM"), (1, 26, "ASIMDBF16"), (1, 27, "I8MM"),
    (2, 0, "AES"), (2, 1, "PMULL"), (2, 2, "SHA1"), (2, 3, "SHA2"), (2, 4, "CRC32"), (2, 5, "SB"),
    (2, 6, "SSBS"),
  ];

  /// <summary>Everything the two capability words report, in table order.</summary>
  public static IReadOnlyList<CpuFeature> Decode(ulong hwcap, ulong hwcap2) => Decode(_Arm64, hwcap, hwcap2);

  /// <summary>
  /// The same two words read as 32-bit ARM assigns them (PRD §46).
  /// </summary>
  /// <remarks>
  /// A separate entry point rather than a flag on <see cref="Decode"/>, because the caller always
  /// knows which architecture it is on and a wrong default here decodes silently into a wrong
  /// answer: every bit is defined in both tables, so there is nothing for a check to fail on.
  /// </remarks>
  public static IReadOnlyList<CpuFeature> DecodeArm32(ulong hwcap, ulong hwcap2) => Decode(_Arm32, hwcap, hwcap2);

  private static IReadOnlyList<CpuFeature> Decode(Bit[] table, ulong hwcap, ulong hwcap2) {
    var features = new List<CpuFeature>();
    foreach (var bit in table) {
      var word = bit.Word == 1 ? hwcap : hwcap2;
      if ((word & (1ul << bit.Index)) != 0)
        features.Add(new(bit.Name, bit.Kind));
    }

    return features;
  }

  /// <summary>
  /// Who made the core, from the implementer field of <c>MIDR_EL1</c>.
  /// </summary>
  /// <remarks>
  /// ASCII, and a joke that outlived its author: the codes are the initials of the companies, so
  /// 0x41 is 'A' for Arm and 0x51 is 'Q' for Qualcomm. Unknown returns null rather than a guess —
  /// a new implementer appears every few years and naming it wrongly is worse than not naming it.
  /// </remarks>
  public static string? Implementer(ulong midr) => (byte)((midr >> 24) & 0xFF) switch {
    0x41 => "Arm",
    0x42 => "Broadcom",
    0x43 => "Cavium",
    0x44 => "DEC",
    0x46 => "Fujitsu",
    0x48 => "HiSilicon",
    0x49 => "Infineon",
    0x4D => "Motorola",
    0x4E => "NVIDIA",
    0x50 => "Applied Micro",
    0x51 => "Qualcomm",
    0x53 => "Samsung",
    0x56 => "Marvell",
    0x61 => "Apple",
    0x66 => "Faraday",
    0x69 => "Intel",
    0x6D => "Microsoft",
    0x70 => "Phytium",
    0xC0 => "Ampere",
    _ => null,
  };

  /// <summary>
  /// The signature: implementer, part, variant and revision, as <c>MIDR_EL1</c> packs them.
  /// </summary>
  /// <remarks>
  /// The ARM counterpart of x86's family/model/stepping, and used for the same thing: errata and
  /// mitigations are written against a part and revision, not against a marketing name.
  /// </remarks>
  public static string? Signature(ulong midr) {
    if (midr == 0)
      return null;

    var part = (midr >> 4) & 0xFFF;
    var variant = (midr >> 20) & 0xF;
    var revision = midr & 0xF;
    var maker = Implementer(midr);
    var who = maker is null
      ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "implementer 0x{0:x2}", (midr >> 24) & 0xFF)
      : maker;

    return string.Format(
      System.Globalization.CultureInfo.InvariantCulture,
      "{0} part 0x{1:x3}, variant {2}, revision {3}",
      who,
      part,
      variant,
      revision
    );
  }

}
