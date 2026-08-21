using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// What each process is doing to the machine's graphics adapters (PRD §19).
/// </summary>
/// <remarks>
/// <para>
/// Two sources, because there is no third. The kernel publishes per-client engine time and memory in
/// <c>/proc/[pid]/fdinfo</c> — the <c>drm-usage-stats</c> interface, which amdgpu, i915, xe and the
/// rest implement — and NVIDIA's proprietary driver publishes none of it and answers only NVML.
/// This machine's NVIDIA card was checked: a process rendering on it has <em>no</em> <c>drm-</c> line
/// in any of its descriptors, so a DRM-only implementation would report a busy card as idle.
/// </para>
/// <para>
/// The two are kept apart on purpose (PRD §19). The DRM half is the operating system's own and runs
/// whatever happens; the NVML half is a vendor library that may be absent, may be a version older
/// than the entry points asked for, and must not be able to take the sampler down with it. Every
/// call into it is guarded, the outcome is remembered, and a card that will not answer costs one
/// failed call and then nothing.
/// </para>
/// <para>
/// Expensive, and therefore off unless asked for (PRD §5.4). Reading the descriptors of every
/// process is the same shape of cost as counting them, which is the work that had to leave the
/// sample loop in the first place; NVML's per-process utilisation call was measured at 5-25 ms per
/// card, against a whole-sample budget of 25.
/// </para>
/// </remarks>
internal sealed class LinuxGpuAccounting {

  /// <summary>
  /// How many processes and descriptors the buffers start at. Grown on demand, never shrunk, so a
  /// steady state allocates nothing (PRD §4).
  /// </summary>
  private const int _InitialCapacity = 64;

  /// <summary>How many samples apart a process's descriptors are listed in full. See <see cref="ListDescriptors"/>.</summary>
  private const int _RescanInterval = 8;

  private readonly ProcFileReader _reader;
  private readonly ProcIo _io;
  private readonly byte[] _procRootUtf8;
  private readonly byte[] _scratch = new byte[16 * 1024];

  /// <summary>Every card, by the PCI address its clients name in <c>drm-pdev</c>.</summary>
  private readonly List<(string PciAddress, string Card)> _cards = [];

  /// <summary>The NVIDIA cards, with the handle NVML found for each. Empty without NVML.</summary>
  private readonly List<(string Card, nint Device)> _nvidia = [];

  private readonly Dictionary<int, NvmlReader.NvmlProcessSample> _nvidiaByPid = [];
  private readonly Dictionary<int, NvmlReader.NvmlProcessSample> _perCard = [];
  private readonly Dictionary<int, string> _nvidiaAdapter = [];
  private readonly List<int> _descriptors = [];
  private readonly List<long> _clients = [];
  private readonly Dictionary<ProcessKey, DescriptorCache> _descriptorCache = [];
  private int _sample;

  private NvmlReader.ProcessInfo[] _processBuffer = [];
  private NvmlReader.ProcessInfoV1[] _narrowBuffer = [];
  private NvmlReader.ProcessUtilization[] _utilizationBuffer = [];

  public LinuxGpuAccounting(LinuxProbeOptions options, ProcFileReader reader, ProcIo io, byte[] procRootUtf8) {
    ArgumentNullException.ThrowIfNull(options);
    this._reader = reader;
    this._io = io;
    this._procRootUtf8 = procRootUtf8;
    this.Discover(options.SysRoot);
  }

  /// <summary>
  /// Whether anything is known about this machine's adapters, readable or not.
  /// </summary>
  /// <remarks>
  /// False means nobody has established that there is an adapter here at all, and it is what makes
  /// the columns say "not implemented here" rather than nought — §19's own requirement, and the
  /// whole reason the fields exist in the registry before every driver can fill them.
  /// <para>
  /// A card whose driver publishes nothing is indistinguishable from a card nobody is using until
  /// somebody uses it, so the DRM half of this only turns true once a client has been seen. On any
  /// machine with a display that is the compositor, on the first sample.
  /// </para>
  /// <para>
  /// A card that is known and cannot be read counts, because that state is described per process by
  /// <see cref="Fill"/> and does not need the whole table overwritten.
  /// </para>
  /// </remarks>
  public bool CanRead => this._nvidia.Count > 0 || this._unreadableAdapters > 0 || this.SeenDrmClient;

  /// <summary>True once any process has been found with a DRM client on it.</summary>
  public bool SeenDrmClient { get; private set; }

  /// <summary>Whether any card answered the per-process utilisation call this sample.</summary>
  private bool _utilizationAsked;

  #region what the machine has

  /// <summary>
  /// Which cards are here, and which of them NVML claims, asked once.
  /// </summary>
  /// <remarks>
  /// By PCI address throughout: it is what <c>drm-pdev</c> writes into every client's fdinfo and
  /// what NVML's own lookup takes, so the same string joins the kernel's <c>cardN</c> to the vendor
  /// library's handle without either side having to be enumerated in the other's order.
  /// </remarks>
  private void Discover(string sysRoot) {
    var drm = Path.Combine(sysRoot, "class", "drm");
    if (!Directory.Exists(drm))
      return;

    var entries = new List<string>();
    foreach (var entry in Directory.EnumerateDirectories(drm))
      if (LinuxDeviceReader.IsCard(Path.GetFileName(entry)))
        entries.Add(entry);

    entries.Sort(StringComparer.Ordinal);
    foreach (var entry in entries) {
      var name = Path.GetFileName(entry);
      var uevent = ReadText(Path.Combine(entry, "device", "uevent"));
      if (uevent is null)
        continue;

      var slot = UeventParser.Value(uevent, "PCI_SLOT_NAME");
      if (slot.IsEmpty)
        continue;

      var address = new string(slot);
      this._cards.Add((address, name));

      var driver = UeventParser.Value(uevent, "DRIVER");
      if (!driver.SequenceEqual("nvidia") && !driver.SequenceEqual("nvidia-drm"))
        continue;

      var device = NvmlReader.DeviceAt(address);
      if (device != 0)
        this._nvidia.Add((name, device));
      else
        ++this._unreadableAdapters;
    }
  }

  /// <summary>
  /// Cards on this machine that nothing here can be asked about.
  /// </summary>
  /// <remarks>
  /// One stack, and it is the one that matters. NVIDIA's proprietary driver publishes no
  /// <c>drm-usage-stats</c> at all — verified on the machine this was written on, where a process
  /// rendering on the card has no <c>drm-</c> line in any of its descriptors — so without NVML there
  /// is nothing left to read. A card in that state makes "this process is using no GPU"
  /// unsayable about any process, because a process with no kernel-visible client may be sitting on
  /// that card with sixteen gigabytes of its memory. Which is exactly what happened when the library
  /// was hidden and the process holding 15.5 GB of VRAM rendered as <c>0 B</c>.
  /// </remarks>
  private int _unreadableAdapters;

  private static string? ReadText(string path) {
    try {
      return File.Exists(path) ? File.ReadAllText(path) : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  #endregion

  #region one sample

  /// <summary>
  /// Asks every NVIDIA card what its processes are doing, once for the whole sample.
  /// </summary>
  /// <remarks>
  /// Per card and not per process: NVML answers about all of a card's clients in one call, so doing
  /// this inside the process loop would ask the same question six hundred times for the same answer.
  /// </remarks>
  public void BeginSample() {
    ++this._sample;
    this._nvidiaByPid.Clear();
    this._nvidiaAdapter.Clear();
    if (this._nvidia.Count == 0)
      return;

    if (this._processBuffer.Length == 0) {
      this._processBuffer = NvmlReader.NewProcessBuffer(_InitialCapacity);
      this._narrowBuffer = NvmlReader.NewNarrowProcessBuffer(_InitialCapacity);
      this._utilizationBuffer = NvmlReader.NewUtilizationBuffer(_InitialCapacity * 4);
    }

    this._utilizationAsked = false;
    foreach (var (card, device) in this._nvidia) {
      this._perCard.Clear();
      NvmlReader.TryReadProcessMemory(device, this._perCard, this._processBuffer, this._narrowBuffer);
      this._utilizationAsked |= NvmlReader.TryReadProcessUtilization(device, this._perCard, this._utilizationBuffer);

      foreach (var (pid, sample) in this._perCard) {
        // First card wins for a process that somehow appears on two: one row cannot describe two
        // adapters, and inventing a total across cards of different sizes would mean nothing.
        if (this._nvidiaByPid.TryAdd(pid, sample))
          this._nvidiaAdapter[pid] = card;
      }

      // A card with more clients than the buffer holds truncates. Growing after the fact rather than
      // before means one short sample the first time a machine gets busy, and none after it.
      if (this._perCard.Count >= this._processBuffer.Length)
        this._processBuffer = NvmlReader.NewProcessBuffer(this._processBuffer.Length * 2);
    }
  }

  /// <summary>
  /// Fills one process's graphics fields, from whichever source knows about it (PRD §19).
  /// </summary>
  /// <remarks>
  /// NVML first where it has an answer, because a hybrid laptop has both a card NVML describes and
  /// one the kernel does, and the discrete card is the one a reader is asking about. A process with
  /// clients on both is attributed to one of them and says which, rather than adding two adapters'
  /// figures into a number that describes neither.
  /// </remarks>
  public void Fill(ProcessKey key, ref ProcessRecord record) {
    if (this._nvidiaByPid.TryGetValue(key.Pid, out var nvidia)) {
      FillFromNvml(in nvidia, this._utilizationAsked, ref record);
      record.GpuAdapter = this._nvidiaAdapter.GetValueOrDefault(key.Pid);
      record.GpuAdapterReason = record.GpuAdapter is null ? UnknownReason.NotImplementedHere : UnknownReason.None;
      return;
    }

    if (this.TryFillFromDrm(key, ref record))
      return;

    // A card nothing can be asked about makes "none" unsayable: this process may be sitting on it.
    if (this._unreadableAdapters > 0) {
      NotCollected(ref record, UnknownReason.NotImplementedHere);
      return;
    }

    // Looked, and this process is using none of it. A measured nought, which is a different thing
    // from the nought a field nobody filled would carry (PRD §72.3) — and the reason the caller only
    // reaches this after establishing that every adapter on this machine can be read.
    Idle(ref record);
  }

  /// <summary>Everything unknown, with the reason, for a process nothing was asked about.</summary>
  public static void NotCollected(ref ProcessRecord record, UnknownReason reason) {
    var unknown = Counter.Unknown(reason);
    record.GpuGraphicsNs = unknown;
    record.GpuComputeNs = unknown;
    record.GpuCopyNs = unknown;
    record.GpuEncodeNs = unknown;
    record.GpuDecodeNs = unknown;
    record.GpuBusyPercent = unknown;
    record.GpuEncodePercent = unknown;
    record.GpuDecodePercent = unknown;
    record.GpuDedicatedBytes = unknown;
    record.GpuSharedBytes = unknown;
    record.GpuBusyEngine = GpuEngine.Unknown;
    record.GpuAdapter = null;
    record.GpuAdapterReason = reason;
  }

  /// <summary>
  /// A process that has no client on any adapter: nought of everything, and no adapter to name.
  /// </summary>
  /// <remarks>
  /// The engine counters are nought rather than unknown because they are cumulative and this process
  /// has genuinely never run anything on an engine — the difference between two of them is nought
  /// too, which is the correct percentage. The adapter carries no reason at all, so the cell is empty
  /// rather than saying "n/i" about a machine that answered perfectly well.
  /// </remarks>
  private static void Idle(ref ProcessRecord record) {
    var none = Counter.Of(0ul);
    record.GpuGraphicsNs = none;
    record.GpuComputeNs = none;
    record.GpuCopyNs = none;
    record.GpuEncodeNs = none;
    record.GpuDecodeNs = none;
    record.GpuBusyPercent = none;
    record.GpuEncodePercent = none;
    record.GpuDecodePercent = none;
    record.GpuDedicatedBytes = none;
    record.GpuSharedBytes = none;
    record.GpuBusyEngine = GpuEngine.Unknown;
    record.GpuAdapter = null;
    record.GpuAdapterReason = UnknownReason.None;
  }

  /// <summary>
  /// What NVML said, in the record's own terms.
  /// </summary>
  /// <remarks>
  /// The engine counters stay unknown, and that is the finding rather than a gap in this code: NVML
  /// publishes no per-process engine time of any kind. What it has is a sampled percentage covering
  /// the shaders as a whole, so the split between graphics and compute is not available at any price
  /// and claiming nought for one of them would be inventing it.
  /// </remarks>
  /// <param name="utilizationAsked">
  /// Whether the driver answered the utilisation call at all this sample. It keeps two different
  /// silences apart: a driver too old to sample per-process utilisation will never answer, while one
  /// that simply has no sample for this process yet will answer next time. The second is
  /// <see cref="UnknownReason.NotSampledYet"/> and shows as a pending mark rather than as a gap in
  /// the program, which is what it is — NVML's sample buffer belongs to the client that opened it,
  /// so a freshly started reader finds it empty for the first interval or so.
  /// </param>
  private static void FillFromNvml(in NvmlReader.NvmlProcessSample sample, bool utilizationAsked, ref ProcessRecord record) {
    var unavailable = Counter.Unknown(UnknownReason.NotImplementedHere);
    var pending = utilizationAsked ? Counter.NotSampledYet : unavailable;
    record.GpuGraphicsNs = unavailable;
    record.GpuComputeNs = unavailable;
    record.GpuCopyNs = unavailable;
    record.GpuEncodeNs = unavailable;
    record.GpuDecodeNs = unavailable;
    record.GpuSharedBytes = unavailable;

    record.GpuDedicatedBytes = sample.HasMemory ? Counter.Of(sample.DedicatedBytes) : unavailable;
    record.GpuBusyPercent = sample.HasUtilization ? Counter.Of(sample.BusyPercent) : pending;
    record.GpuEncodePercent = sample.HasUtilization ? Counter.Of(sample.EncodePercent) : pending;
    record.GpuDecodePercent = sample.HasUtilization ? Counter.Of(sample.DecodePercent) : pending;

    // Which list the process was in, which is the only split NVML offers: a CUDA client is reported
    // as compute and a client that only ever draws as graphics. A process holding both contexts is
    // called compute, the one figure covering both either way.
    record.GpuBusyEngine = sample.IsCompute ? GpuEngine.Compute : GpuEngine.Graphics;
  }

  #endregion

  #region the kernel's own accounting

  /// <summary>
  /// Sums this process's DRM clients out of <c>/proc/[pid]/fdinfo</c>.
  /// </summary>
  /// <remarks>
  /// Deduplicated by <c>drm-client-id</c>, without which the figures are simply wrong: a descriptor
  /// that has been duplicated, inherited or passed over a socket appears once per descriptor with
  /// the same client behind all of them, and summing those multiplies a process's memory by however
  /// many copies of the handle it happens to hold. Two genuinely different clients of one process do
  /// add up, which is why this deduplicates rather than taking the first.
  /// </remarks>
  private bool TryFillFromDrm(ProcessKey key, ref ProcessRecord record) {
    var pid = key.Pid;
    Span<byte> directory = stackalloc byte[ProcPath.MaxLength];
    var path = ProcPath.Build(directory, this._procRootUtf8, pid, "fdinfo"u8);

    if (!this._descriptorCache.TryGetValue(key, out var cache)) {
      cache = new();
      this._descriptorCache[key] = cache;
      cache.RescanAt = this._sample;
    }

    var rescan = cache.RescanAt <= this._sample;
    if (!rescan) {
      this._descriptors.Clear();
      this._descriptors.AddRange(cache.Descriptors);
    } else if (!this.ListDescriptors(path, cache, pid))
      return false;

    while (true) {
      var outcome = this.SumClients(path, cache, rescan, ref record);
      if (outcome != DrmScan.Restale)
        return outcome == DrmScan.Found;

      // A remembered descriptor is gone or is no longer a graphics one: the process closed its
      // device and opened another, and the fast path cannot tell which. Look again, now, rather
      // than reporting nothing until the next scheduled rescan.
      if (!this.ListDescriptors(path, cache, pid))
        return false;

      rescan = true;
    }
  }

  /// <summary>Whether a pass over a process's descriptors found anything, or has to look again.</summary>
  private enum DrmScan : byte { None, Found, Restale }

  /// <summary>
  /// Reads every descriptor of a process and adds up the graphics clients behind them.
  /// </summary>
  /// <remarks>
  /// Deduplicated by <c>drm-client-id</c>, without which the figures are simply wrong: a descriptor
  /// that has been duplicated, inherited or passed over a socket appears once per descriptor with
  /// the same client behind all of them, and summing those multiplies a process's memory by however
  /// many copies of the handle it happens to hold. Two genuinely different clients of one process do
  /// add up, which is why this deduplicates rather than taking the first.
  /// </remarks>
  private DrmScan SumClients(ReadOnlySpan<byte> path, DescriptorCache cache, bool rescan, ref ProcessRecord record) {
    this._clients.Clear();
    if (rescan)
      cache.Descriptors.Clear();
    var engines = DrmEngineFlags.None;
    ulong graphics = 0, compute = 0, copy = 0, encode = 0, decode = 0, dedicated = 0, shared = 0;
    var hasDedicated = false;
    var hasShared = false;
    string? adapter = null;
    var found = false;

    Span<byte> file = stackalloc byte[ProcPath.MaxLength];
    foreach (var descriptor in this._descriptors) {
      var descriptorPath = ProcPath.Build(file, path, descriptor);
      var read = this._reader.TryRead(descriptorPath, out var content, out _);
      if (!read || !DrmFdinfoParser.TryParse(content, out var client)) {
        // On the fast path every descriptor in the list was a graphics one last time. One that is
        // not any more means the list is stale, and the answer is to look again rather than to
        // report a process as idle because it renumbered its handles.
        if (!rescan)
          return DrmScan.Restale;

        continue;
      }

      this.SeenDrmClient = true;
      if (rescan)
        cache.Descriptors.Add(descriptor);

      if (client.ClientId >= 0) {
        if (this._clients.Contains(client.ClientId))
          continue;

        this._clients.Add(client.ClientId);
      }

      found = true;
      engines |= client.Engines;
      graphics += client.GraphicsNs;
      compute += client.ComputeNs;
      copy += client.CopyNs;
      encode += client.EncodeNs;
      decode += client.DecodeNs;
      if (client.HasDedicated) {
        dedicated += client.DedicatedBytes;
        hasDedicated = true;
      }

      if (client.HasShared) {
        shared += client.SharedBytes;
        hasShared = true;
      }

      adapter ??= this.CardAt(content.Slice(client.PciAddressOffset, client.PciAddressLength));
    }

    if (!found)
      return DrmScan.None;

    var unavailable = Counter.Unknown(UnknownReason.NotImplementedHere);
    record.GpuGraphicsNs = Engine(engines, DrmEngineFlags.Graphics, graphics, unavailable);
    record.GpuComputeNs = Engine(engines, DrmEngineFlags.Compute, compute, unavailable);
    record.GpuCopyNs = Engine(engines, DrmEngineFlags.Copy, copy, unavailable);
    record.GpuEncodeNs = Engine(engines, DrmEngineFlags.Encode, encode, unavailable);
    record.GpuDecodeNs = Engine(engines, DrmEngineFlags.Decode, decode, unavailable);
    record.GpuDedicatedBytes = hasDedicated ? Counter.Of(dedicated) : unavailable;
    record.GpuSharedBytes = hasShared ? Counter.Of(shared) : unavailable;

    // The kernel counts engine time; it samples no percentage, and the delta computes one from the
    // counters instead. Saying so is not the same as saying nought.
    record.GpuBusyPercent = unavailable;
    record.GpuEncodePercent = unavailable;
    record.GpuDecodePercent = unavailable;
    record.GpuBusyEngine = GpuEngine.Unknown;

    record.GpuAdapter = adapter;
    record.GpuAdapterReason = adapter is null ? UnknownReason.NotImplementedHere : UnknownReason.None;
    return DrmScan.Found;
  }

  /// <summary>
  /// Lists a process's descriptors afresh and arranges for the next full listing.
  /// </summary>
  /// <remarks>
  /// The expensive half, and the reason for the schedule around it. Reading every descriptor of
  /// every process costs 590 µs per process on a machine with nine hundred of them — half a second
  /// of CPU per sample, which is a front-end that stutters rather than one that shows a GPU column.
  /// So each process is listed in full once, and thereafter only every <see cref="_RescanInterval"/>
  /// samples, staggered by pid so that the cost is spread across the interval rather than landing on
  /// one sample in eight. In between, the descriptors already known to be graphics ones are read
  /// directly — one or two files for a process using the GPU and none at all for one that is not.
  /// <para>
  /// What this costs is latency, and only in one direction: a process that opens an adapter after it
  /// has been listed is invisible for up to the interval. It is never wrong, because a descriptor
  /// that stops being a graphics one forces the listing immediately.
  /// </para>
  /// </remarks>
  private bool ListDescriptors(ReadOnlySpan<byte> path, DescriptorCache cache, int pid) {
    cache.RescanAt = this._sample + _RescanInterval + pid % _RescanInterval;
    this._descriptors.Clear();
    // Descriptors start at nought, and nought is standard input rather than a name nobody uses. The
    // default here is 1, which is right for pids — there is no process nought — and quietly loses
    // fd 0 for everything else. A program that has its card open on fd 0 is unusual and entirely
    // legal: anything started with its standard input closed gets the next descriptor the kernel
    // hands out, and that is nought.
    return this._io.ListNumericEntries(path, this._scratch, this._descriptors, minimum: 0)
      && this._descriptors.Count > 0;
  }

  /// <summary>Which of a process's descriptors were graphics ones, and when to look again.</summary>
  private sealed class DescriptorCache {
    public readonly List<int> Descriptors = [];
    public int RescanAt;
  }

  /// <summary>Forgets a process that has exited, called by the probe as it prunes its own cache.</summary>
  public void Forget(ProcessKey key) => this._descriptorCache.Remove(key);

  private static Counter Engine(DrmEngineFlags present, DrmEngineFlags wanted, ulong nanoseconds, Counter unavailable)
    => (present & wanted) != 0 ? Counter.Of(nanoseconds) : unavailable;

  /// <summary>
  /// The <c>cardN</c> at a PCI address, matched without decoding the bytes into a string.
  /// </summary>
  /// <remarks>
  /// A handful of entries scanned linearly, like <see cref="DeviceNameCache"/> and for the same
  /// reason: a machine has one or two adapters, this runs once per DRM client per sample, and
  /// decoding the address would allocate a string per client per second for a value that never
  /// changes.
  /// </remarks>
  private string? CardAt(ReadOnlySpan<byte> pciAddress) {
    if (pciAddress.IsEmpty)
      return null;

    foreach (var (address, card) in this._cards)
      if (Matches(address, pciAddress))
        return card;

    return null;
  }

  private static bool Matches(string text, ReadOnlySpan<byte> utf8) {
    if (text.Length != utf8.Length)
      return false;

    for (var i = 0; i < utf8.Length; ++i)
      if (text[i] != (char)utf8[i])
        return false;

    return true;
  }

  #endregion

}
