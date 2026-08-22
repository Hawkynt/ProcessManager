/*
 * The PF_* processor-feature ordinals of <winnt.h> — the argument IsProcessorFeaturePresent takes.
 *
 * They are an ABI: a number is assigned once and every Windows since answers the same question for
 * it, which is why a table of them can be vendored beside a test without going stale. It can only
 * gain entries, so the test asks that every ordinal this program uses is defined here — not that
 * every ordinal here is used.
 *
 * The names are Microsoft's, from the IsProcessorFeaturePresent documentation. The numbers were
 * checked line by line against Wine's <winnt.h>, which is the reimplementation with the longest
 * record of getting them right. Nothing here is copied from anybody's implementation: the list is
 * the interface, and there is only one way to write down a number.
 */

#define PF_FLOATING_POINT_PRECISION_ERRATA          0
#define PF_FLOATING_POINT_EMULATED                  1
#define PF_COMPARE_EXCHANGE_DOUBLE                  2
#define PF_MMX_INSTRUCTIONS_AVAILABLE               3
#define PF_PPC_MOVEMEM_64BIT_OK                     4
#define PF_ALPHA_BYTE_INSTRUCTIONS                  5
#define PF_XMMI_INSTRUCTIONS_AVAILABLE              6
#define PF_3DNOW_INSTRUCTIONS_AVAILABLE             7
#define PF_RDTSC_INSTRUCTION_AVAILABLE              8
#define PF_PAE_ENABLED                              9
#define PF_XMMI64_INSTRUCTIONS_AVAILABLE            10
#define PF_SSE_DAZ_MODE_AVAILABLE                   11
#define PF_NX_ENABLED                               12
#define PF_SSE3_INSTRUCTIONS_AVAILABLE              13
#define PF_COMPARE_EXCHANGE128                      14
#define PF_COMPARE64_EXCHANGE128                    15
#define PF_CHANNELS_ENABLED                         16
#define PF_XSAVE_ENABLED                            17
#define PF_ARM_VFP_32_REGISTERS_AVAILABLE           18
#define PF_ARM_NEON_INSTRUCTIONS_AVAILABLE          19
#define PF_SECOND_LEVEL_ADDRESS_TRANSLATION         20
#define PF_VIRT_FIRMWARE_ENABLED                    21
#define PF_RDWRFSGSBASE_AVAILABLE                   22
#define PF_FASTFAIL_AVAILABLE                       23
#define PF_ARM_DIVIDE_INSTRUCTION_AVAILABLE         24
#define PF_ARM_64BIT_LOADSTORE_ATOMIC               25
#define PF_ARM_EXTERNAL_CACHE_AVAILABLE             26
#define PF_ARM_FMAC_INSTRUCTIONS_AVAILABLE          27
#define PF_RDRAND_INSTRUCTION_AVAILABLE             28
#define PF_ARM_V8_INSTRUCTIONS_AVAILABLE            29
#define PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE     30
#define PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE      31
#define PF_RDTSCP_INSTRUCTION_AVAILABLE             32
#define PF_RDPID_INSTRUCTION_AVAILABLE              33
#define PF_ARM_V81_ATOMIC_INSTRUCTIONS_AVAILABLE    34
#define PF_MONITORX_INSTRUCTION_AVAILABLE           35
#define PF_SSSE3_INSTRUCTIONS_AVAILABLE             36
#define PF_SSE4_1_INSTRUCTIONS_AVAILABLE            37
#define PF_SSE4_2_INSTRUCTIONS_AVAILABLE            38
#define PF_AVX_INSTRUCTIONS_AVAILABLE               39
#define PF_AVX2_INSTRUCTIONS_AVAILABLE              40
#define PF_AVX512F_INSTRUCTIONS_AVAILABLE           41
#define PF_ERMS_AVAILABLE                           42
#define PF_ARM_V82_DP_INSTRUCTIONS_AVAILABLE        43
#define PF_ARM_V83_JSCVT_INSTRUCTIONS_AVAILABLE     44
#define PF_ARM_V83_LRCPC_INSTRUCTIONS_AVAILABLE     45
#define PF_ARM_SVE_INSTRUCTIONS_AVAILABLE           46
#define PF_ARM_SVE2_INSTRUCTIONS_AVAILABLE          47
#define PF_ARM_SVE2_1_INSTRUCTIONS_AVAILABLE        48
#define PF_ARM_SVE_AES_INSTRUCTIONS_AVAILABLE       49
#define PF_ARM_SVE_PMULL128_INSTRUCTIONS_AVAILABLE  50
#define PF_ARM_SVE_BITPERM_INSTRUCTIONS_AVAILABLE   51
#define PF_ARM_SVE_BF16_INSTRUCTIONS_AVAILABLE      52
#define PF_ARM_SVE_EBF16_INSTRUCTIONS_AVAILABLE     53
#define PF_ARM_SVE_B16B16_INSTRUCTIONS_AVAILABLE    54
#define PF_ARM_SVE_SHA3_INSTRUCTIONS_AVAILABLE      55
#define PF_ARM_SVE_SM4_INSTRUCTIONS_AVAILABLE       56
#define PF_ARM_SVE_I8MM_INSTRUCTIONS_AVAILABLE      57
#define PF_ARM_SVE_F32MM_INSTRUCTIONS_AVAILABLE     58
#define PF_ARM_SVE_F64MM_INSTRUCTIONS_AVAILABLE     59
#define PF_BMI2_INSTRUCTIONS_AVAILABLE              60
#define PF_MOVDIR64B_INSTRUCTION_AVAILABLE          61
#define PF_ARM_LSE2_AVAILABLE                       62
#define PF_RESERVED_FEATURE                         63
#define PF_ARM_SHA3_INSTRUCTIONS_AVAILABLE          64
#define PF_ARM_SHA512_INSTRUCTIONS_AVAILABLE        65
#define PF_ARM_V82_I8MM_INSTRUCTIONS_AVAILABLE      66
#define PF_ARM_V82_FP16_INSTRUCTIONS_AVAILABLE      67
#define PF_ARM_V86_BF16_INSTRUCTIONS_AVAILABLE      68
#define PF_ARM_V86_EBF16_INSTRUCTIONS_AVAILABLE     69
#define PF_ARM_SME_INSTRUCTIONS_AVAILABLE           70
#define PF_ARM_SME2_INSTRUCTIONS_AVAILABLE          71
#define PF_ARM_SME2_1_INSTRUCTIONS_AVAILABLE        72
#define PF_ARM_SME2_2_INSTRUCTIONS_AVAILABLE        73
#define PF_ARM_SME_AES_INSTRUCTIONS_AVAILABLE       74
#define PF_ARM_SME_SBITPERM_INSTRUCTIONS_AVAILABLE  75
#define PF_ARM_SME_SF8MM4_INSTRUCTIONS_AVAILABLE    76
#define PF_ARM_SME_SF8MM8_INSTRUCTIONS_AVAILABLE    77
#define PF_ARM_SME_SF8DP2_INSTRUCTIONS_AVAILABLE    78
#define PF_ARM_SME_SF8DP4_INSTRUCTIONS_AVAILABLE    79
#define PF_ARM_SME_SF8FMA_INSTRUCTIONS_AVAILABLE    80
#define PF_ARM_SME_F8F32_INSTRUCTIONS_AVAILABLE     81
#define PF_ARM_SME_F8F16_INSTRUCTIONS_AVAILABLE     82
#define PF_ARM_SME_F16F16_INSTRUCTIONS_AVAILABLE    83
#define PF_ARM_SME_B16B16_INSTRUCTIONS_AVAILABLE    84
#define PF_ARM_SME_F64F64_INSTRUCTIONS_AVAILABLE    85
#define PF_ARM_SME_I16I64_INSTRUCTIONS_AVAILABLE    86
#define PF_ARM_SME_LUTv2_INSTRUCTIONS_AVAILABLE     87
#define PF_ARM_SME_FA64_INSTRUCTIONS_AVAILABLE      88
