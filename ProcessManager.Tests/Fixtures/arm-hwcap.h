/*
 * The AT_HWCAP and AT_HWCAP2 bit assignments of 32-bit ARM, from
 * arch/arm/include/uapi/asm/hwcap.h.
 *
 * A different word from arm64's, sharing not one bit position with it: 32-bit ARM was assigning
 * these before AArch64 existed, so NEON is bit 12 here and bit 1 there, and a table written for one
 * architecture decodes the other into a plausible list of the wrong features.
 *
 * They are an ABI between the kernel and userspace and never change once assigned, which is what
 * makes a vendored copy safe to hold a table against: it can only gain entries, so the test asks
 * that every bit this program reads is defined here — not that every bit here is read.
 *
 * HWCAP_IDIV is deliberately not a bit: the kernel defines it as the pair IDIVA | IDIVT, and a
 * parser that treated it as one would claim a bit the architecture never assigned.
 */

#define HWCAP_SWP		(1 << 0)
#define HWCAP_HALF		(1 << 1)
#define HWCAP_THUMB		(1 << 2)
#define HWCAP_26BIT		(1 << 3)
#define HWCAP_FAST_MULT		(1 << 4)
#define HWCAP_FPA		(1 << 5)
#define HWCAP_VFP		(1 << 6)
#define HWCAP_EDSP		(1 << 7)
#define HWCAP_JAVA		(1 << 8)
#define HWCAP_IWMMXT		(1 << 9)
#define HWCAP_CRUNCH		(1 << 10)
#define HWCAP_THUMBEE		(1 << 11)
#define HWCAP_NEON		(1 << 12)
#define HWCAP_VFPv3		(1 << 13)
#define HWCAP_VFPv3D16		(1 << 14)
#define HWCAP_TLS		(1 << 15)
#define HWCAP_VFPv4		(1 << 16)
#define HWCAP_IDIVA		(1 << 17)
#define HWCAP_IDIVT		(1 << 18)
#define HWCAP_VFPD32		(1 << 19)
#define HWCAP_IDIV		(HWCAP_IDIVA | HWCAP_IDIVT)
#define HWCAP_LPAE		(1 << 20)
#define HWCAP_EVTSTRM		(1 << 21)
#define HWCAP_FPHP		(1 << 22)
#define HWCAP_ASIMDHP		(1 << 23)
#define HWCAP_ASIMDDP		(1 << 24)
#define HWCAP_ASIMDFHM		(1 << 25)
#define HWCAP_ASIMDBF16		(1 << 26)
#define HWCAP_I8MM		(1 << 27)

#define HWCAP2_AES		(1 << 0)
#define HWCAP2_PMULL		(1 << 1)
#define HWCAP2_SHA1		(1 << 2)
#define HWCAP2_SHA2		(1 << 3)
#define HWCAP2_CRC32		(1 << 4)
#define HWCAP2_SB		(1 << 5)
#define HWCAP2_SSBS		(1 << 6)
