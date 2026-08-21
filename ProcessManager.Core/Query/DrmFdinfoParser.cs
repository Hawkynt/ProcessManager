using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// One DRM client, as one <c>/proc/[pid]/fdinfo/[fd]</c> file described it (PRD §19).
/// </summary>
/// <remarks>
/// The kernel's own per-process graphics accounting, documented as
/// <c>Documentation/gpu/drm-usage-stats.rst</c> and implemented by amdgpu, i915, xe, panfrost and
/// the rest. It is how every tool that shows per-process GPU use on a non-NVIDIA card gets its
/// numbers, NVIDIA's proprietary driver being the one that publishes nothing here and wants NVML
/// instead.
/// <para>
/// Plain fields rather than <see cref="Counter"/>s: a parser's job is to say what was in the file,
/// and a key that was absent is expressed by leaving its bit out of <see cref="Engines"/> rather
/// than by a counter carrying a reason. The caller turns that into the reason, because only the
/// caller knows whether an engine is missing from this file or missing from the machine.
/// </para>
/// </remarks>
public struct DrmClient {

  /// <summary>Which engines the file actually named, as <see cref="DrmEngineFlags"/>.</summary>
  public DrmEngineFlags Engines;

  /// <summary>Busy nanoseconds per engine. Only meaningful where <see cref="Engines"/> says so.</summary>
  public ulong GraphicsNs;
  public ulong ComputeNs;
  public ulong CopyNs;
  public ulong EncodeNs;
  public ulong DecodeNs;

  /// <summary>Adapter memory, in bytes, and whether the file said anything about it.</summary>
  public ulong DedicatedBytes;
  public bool HasDedicated;

  /// <summary>System memory the adapter holds for this client, and whether the file said so.</summary>
  public ulong SharedBytes;
  public bool HasShared;

  /// <summary>
  /// The driver's own client number, or -1 where it does not publish one.
  /// </summary>
  /// <remarks>
  /// The thing that stops a process being counted several times over. A descriptor duplicated by
  /// <c>dup</c>, inherited across a <c>fork</c>, or passed over a socket appears once per descriptor
  /// with identical figures behind it, and summing those reports a browser tab using four times the
  /// memory it has. Two <em>different</em> clients of the same process are real and do add up —
  /// which is why the answer is to deduplicate by this number rather than to take the first.
  /// </remarks>
  public long ClientId;

  /// <summary>Where <c>drm-pdev</c> sat in the content, so the caller can slice it without copying.</summary>
  public int PciAddressOffset;
  public int PciAddressLength;

  /// <summary>
  /// Which spelling of the memory figure has been taken so far, so a better one can replace it.
  /// </summary>
  /// <remarks>
  /// A file may carry two or three descriptions of the same memory — a total, a resident subset of
  /// it, and amdgpu's original <c>drm-memory-*</c> — and adding them together triples the answer.
  /// The rank picks one spelling and sums only the regions written in that spelling.
  /// </remarks>
  internal int DedicatedRank;
  internal int SharedRank;

}

/// <summary>Which engines a <see cref="DrmClient"/> carried a reading for.</summary>
[Flags]
public enum DrmEngineFlags : byte {
  None = 0,
  Graphics = 1,
  Compute = 2,
  Copy = 4,
  Encode = 8,
  Decode = 16,
}

/// <summary>
/// Reads the <c>drm-*</c> lines the kernel writes into a descriptor's <c>fdinfo</c> (PRD §19).
/// </summary>
/// <remarks>
/// In Core rather than in the Linux probe so that §9.2 gets it: the parsers carry no platform
/// attribute and are exercised on every CI leg, including the ones with no <c>/proc</c> to read.
/// </remarks>
public static class DrmFdinfoParser {

  private static ReadOnlySpan<byte> _prefix => "drm-"u8;
  private static ReadOnlySpan<byte> _pdev => "drm-pdev"u8;
  private static ReadOnlySpan<byte> _clientId => "drm-client-id"u8;
  private static ReadOnlySpan<byte> _engine => "drm-engine-"u8;
  private static ReadOnlySpan<byte> _capacity => "drm-engine-capacity-"u8;

  /// <summary>
  /// Parses one <c>fdinfo</c> file.
  /// </summary>
  /// <returns>
  /// <see langword="false"/> for a descriptor that is not a DRM one at all, which is nearly every
  /// descriptor on the machine — the cheap rejection this whole scan depends on.
  /// </returns>
  public static bool TryParse(ReadOnlySpan<byte> content, out DrmClient client) {
    client = default;
    client.ClientId = -1;
    client.PciAddressLength = 0;

    var found = false;
    var offset = 0;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      var lineStart = offset;
      offset += line.Length + 1;

      if (!AsciiScanner.StartsWith(line, _prefix))
        continue;

      var colon = line.IndexOf((byte)':');
      if (colon < 0)
        continue;

      found = true;
      var key = line[..colon];
      var value = line[(colon + 1)..];

      if (key.SequenceEqual(_pdev)) {
        var trimmed = TrimBlanks(value, out var skipped);
        client.PciAddressOffset = lineStart + colon + 1 + skipped;
        client.PciAddressLength = trimmed.Length;
        continue;
      }

      if (key.SequenceEqual(_clientId)) {
        var scan = new AsciiScanner(value);
        client.ClientId = scan.NextInt64();
        continue;
      }

      if (AsciiScanner.StartsWith(key, _engine)) {
        // "drm-engine-capacity-video" is how many of that engine the part has, not a time. Reading
        // it as nanoseconds attributes two nanoseconds of work to a video engine that did none.
        if (AsciiScanner.StartsWith(key, _capacity))
          continue;

        AddEngine(ref client, key[_engine.Length..], Value(value));
        continue;
      }

      AddMemory(ref client, key, value);
    }

    return found;
  }

  /// <summary>
  /// Which of the five engines a driver's name for it means.
  /// </summary>
  /// <remarks>
  /// The names are per-driver and the kernel deliberately does not standardise them: amdgpu writes
  /// <c>gfx</c>, <c>compute</c>, <c>dma</c>, <c>dec</c> and <c>enc</c>; i915 writes <c>render</c>,
  /// <c>copy</c>, <c>video</c> and <c>video-enhance</c>.
  /// <para>
  /// i915's <c>video</c> is the one that cannot be mapped honestly: one engine does encode and
  /// decode both, and the kernel reports the two together. It is counted as decode, which is what
  /// nvtop does and is right far more often than it is wrong — <c>video-enhance</c>, the engine that
  /// only ever runs alongside a transcode, is what carries encode. A reader watching a process
  /// encode on an Intel part sees the work; which column it lands in is the driver's choice, not a
  /// measurement of ours.
  /// </para>
  /// </remarks>
  private static void AddEngine(ref DrmClient client, ReadOnlySpan<byte> name, ulong nanoseconds) {
    if (Is(name, "render"u8) || Is(name, "gfx"u8) || Is(name, "3d"u8)) {
      client.GraphicsNs += nanoseconds;
      client.Engines |= DrmEngineFlags.Graphics;
      return;
    }

    if (Is(name, "compute"u8)) {
      client.ComputeNs += nanoseconds;
      client.Engines |= DrmEngineFlags.Compute;
      return;
    }

    if (Is(name, "copy"u8) || Is(name, "dma"u8) || Is(name, "blitter"u8)) {
      client.CopyNs += nanoseconds;
      client.Engines |= DrmEngineFlags.Copy;
      return;
    }

    // amdgpu numbers its second and further video engines: enc_1, dec_1. They are the same engine
    // as far as a column is concerned, so they are summed rather than being three more columns.
    if (StartsWith(name, "enc"u8) || Is(name, "video-enhance"u8)) {
      client.EncodeNs += nanoseconds;
      client.Engines |= DrmEngineFlags.Encode;
      return;
    }

    if (StartsWith(name, "dec"u8) || Is(name, "video"u8) || Is(name, "jpeg"u8)) {
      client.DecodeNs += nanoseconds;
      client.Engines |= DrmEngineFlags.Decode;
    }
  }

  private static ReadOnlySpan<byte> _memory => "drm-memory-"u8;
  private static ReadOnlySpan<byte> _resident => "drm-resident-"u8;
  private static ReadOnlySpan<byte> _total => "drm-total-"u8;

  /// <summary>
  /// The memory lines, of which there are three spellings of the same thing.
  /// </summary>
  /// <remarks>
  /// <c>drm-memory-vram</c> is amdgpu's original; <c>drm-resident-&lt;region&gt;</c> and
  /// <c>drm-total-&lt;region&gt;</c> are what the documented interface settled on. Resident beats
  /// total where both are present, for the same reason a resident set beats a virtual size: total
  /// counts buffers that have been evicted and are not costing the card anything.
  /// <para>
  /// The region names are the driver's again — <c>vram</c>, <c>system0</c>, <c>gtt</c>,
  /// <c>cpu</c>, <c>stolen-system0</c>. Anything that is not video memory is charged to shared,
  /// which on an integrated part is all of it, that being exactly what integrated means. Stolen
  /// memory is skipped: it is carved out of system memory and already counted in the system
  /// region, so adding it reports it twice.
  /// </para>
  /// </remarks>
  private static void AddMemory(ref DrmClient client, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) {
    int rank;
    ReadOnlySpan<byte> region;
    if (AsciiScanner.StartsWith(key, _memory)) {
      rank = 2;
      region = key[_memory.Length..];
    } else if (AsciiScanner.StartsWith(key, _resident)) {
      rank = 3;
      region = key[_resident.Length..];
    } else if (AsciiScanner.StartsWith(key, _total)) {
      rank = 1;
      region = key[_total.Length..];
    } else
      return;

    if (Contains(region, "stolen"u8))
      return;

    var bytes = Value(value);
    if (Contains(region, "vram"u8)) {
      if (rank < client.DedicatedRank)
        return;

      client.DedicatedBytes = rank > client.DedicatedRank ? bytes : client.DedicatedBytes + bytes;
      client.DedicatedRank = rank;
      client.HasDedicated = true;
      return;
    }

    if (rank < client.SharedRank)
      return;

    client.SharedBytes = rank > client.SharedRank ? bytes : client.SharedBytes + bytes;
    client.SharedRank = rank;
    client.HasShared = true;
  }

  /// <summary>
  /// A value with the unit the kernel wrote beside it.
  /// </summary>
  /// <remarks>
  /// Times come in nanoseconds and memory in <c>KiB</c> or <c>MiB</c>, and the suffix is not
  /// optional decoration: reading <c>28596 KiB</c> as bytes under-reports a client's memory by a
  /// factor of a thousand and reads as a process using nothing.
  /// </remarks>
  private static ulong Value(ReadOnlySpan<byte> text) {
    var scanner = new AsciiScanner(text);
    var number = scanner.NextUInt64();
    var unit = scanner.NextField();
    if (unit.IsEmpty)
      return number;

    if (Is(unit, "KiB"u8))
      return number * 1024;
    if (Is(unit, "MiB"u8))
      return number * 1024 * 1024;
    if (Is(unit, "GiB"u8))
      return number * 1024 * 1024 * 1024;

    return number;
  }

  private static ReadOnlySpan<byte> TrimBlanks(ReadOnlySpan<byte> value, out int skipped) {
    skipped = 0;
    while (skipped < value.Length && (value[skipped] == (byte)' ' || value[skipped] == (byte)'\t'))
      ++skipped;

    var rest = value[skipped..];
    var end = rest.Length;
    while (end > 0 && AsciiScanner.IsSpace(rest[end - 1]))
      --end;

    return rest[..end];
  }

  private static bool Is(ReadOnlySpan<byte> text, ReadOnlySpan<byte> literal) => text.SequenceEqual(literal);

  private static bool StartsWith(ReadOnlySpan<byte> text, ReadOnlySpan<byte> prefix)
    => AsciiScanner.StartsWith(text, prefix);

  private static bool Contains(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle) => text.IndexOf(needle) >= 0;

}
