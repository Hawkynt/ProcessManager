using System.Text;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Which disk each mount and each swap area actually sits on (PRD §48).
/// </summary>
/// <remarks>
/// <para>
/// A mount names a source — <c>/dev/mapper/vg-root</c> — and a disk is several layers below it:
/// through the device-mapper target to its slaves, through a partition to the device holding it.
/// This walks that stack, using nothing but <c>/sys/block</c>, so the answer is the kernel's rather
/// than a guess from the names. <c>lsblk</c> draws the same tree from the same place.
/// </para>
/// <para>
/// The device number in <c>mountinfo</c> is deliberately not used for the join. btrfs and ZFS report
/// a synthetic <c>major:minor</c> of their own — 0:30 for the root of this machine — so a lookup by
/// device number finds nothing for exactly the file systems most likely to be the root one, and
/// would report the system disk as unknown on a machine whose root is plainly on it.
/// </para>
/// <para>
/// Read when the disks are first described, like the model and the capacity beside it. A machine
/// that mounts something afterwards is described as it was, which is the same contract every other
/// field on <see cref="Model.DiskInfo"/> has.
/// </para>
/// </remarks>
internal sealed class LinuxStorageLayout {

  private readonly string _sysRoot;

  /// <summary>Mount points per whole device, and which of them hold the root and swap.</summary>
  private readonly Dictionary<string, List<string>> _volumes = new(StringComparer.Ordinal);
  private readonly HashSet<string> _system = new(StringComparer.Ordinal);
  private readonly HashSet<string> _swap = new(StringComparer.Ordinal);

  /// <summary>Whether the mount table could be read at all, which is what makes empty mean empty.</summary>
  public bool Known { get; }

  public LinuxStorageLayout(string sysRoot, string procRoot) {
    this._sysRoot = sysRoot;

    var mounts = ReadMounts(procRoot);
    this.Known = mounts.Count > 0;
    if (!this.Known)
      return;

    foreach (var mount in mounts) {
      var disks = this.DisksBehind(mount.Source);
      foreach (var disk in disks) {
        if (!this._volumes.TryGetValue(disk, out var points))
          this._volumes[disk] = points = [];

        if (!points.Contains(mount.MountPoint))
          points.Add(mount.MountPoint);

        if (mount.MountPoint == "/")
          this._system.Add(disk);
      }
    }

    this.MarkSwap(procRoot, mounts);
  }

  /// <summary>Where a disk is mounted, or null when the mount table could not be read.</summary>
  public IReadOnlyList<string>? VolumesOf(string disk) {
    if (!this.Known)
      return null;

    if (!this._volumes.TryGetValue(disk, out var points))
      return [];

    points.Sort(StringComparer.Ordinal);
    return points;
  }

  public bool? IsSystemDisk(string disk) => this.Known ? this._system.Contains(disk) : null;

  public bool? HoldsSwap(string disk) => this.Known ? this._swap.Contains(disk) : null;

  /// <summary>
  /// Which disks a swap area lives on.
  /// </summary>
  /// <remarks>
  /// A swap partition names its device and needs only the same walk a mount does. A swap <em>file</em>
  /// names a path, and the disk it is on is the disk under the file system holding that path — found
  /// by the longest mount point the path begins with, which is exactly how the kernel resolves it.
  /// Longest and not first: <c>/swap/swapfile</c> is on <c>/swap</c> if that is a mount of its own
  /// and on <c>/</c> otherwise, and taking the first match would always answer <c>/</c>.
  /// </remarks>
  private void MarkSwap(string procRoot, IReadOnlyList<MountInfoParser.Mount> mounts) {
    var content = TryReadBytes(Path.Combine(procRoot, "swaps"));
    if (content is null)
      return;

    foreach (var area in SwapAreaParser.Parse(content)) {
      if (area.Kind == SwapAreaParser.SwapKind.Partition) {
        foreach (var disk in this.DisksBehind(area.Path))
          this._swap.Add(disk);

        continue;
      }

      var holder = LongestMountFor(mounts, area.Path);
      if (holder is not { } mount)
        continue;

      foreach (var disk in this.DisksBehind(mount.Source))
        this._swap.Add(disk);
    }
  }

  private static MountInfoParser.Mount? LongestMountFor(IReadOnlyList<MountInfoParser.Mount> mounts, string path) {
    MountInfoParser.Mount? best = null;
    foreach (var mount in mounts) {
      if (!Covers(mount.MountPoint, path))
        continue;

      if (best is not { } chosen || mount.MountPoint.Length > chosen.MountPoint.Length)
        best = mount;
    }

    return best;
  }

  /// <summary>
  /// Whether a mount point contains a path — by path component, never by prefix.
  /// </summary>
  /// <remarks>
  /// <c>/var</c> does not contain <c>/variable/swapfile</c>, which a plain <c>StartsWith</c> would
  /// say it does.
  /// </remarks>
  private static bool Covers(string mountPoint, string path) {
    if (mountPoint == "/")
      return path.StartsWith('/');

    if (!path.StartsWith(mountPoint, StringComparison.Ordinal))
      return false;

    return path.Length == mountPoint.Length || path[mountPoint.Length] == '/';
  }

  /// <summary>
  /// The whole devices underneath one mount source, following the stack down.
  /// </summary>
  /// <remarks>
  /// More than one is the ordinary answer for a RAID set or a btrfs spanning two disks, and both of
  /// them then genuinely hold the file system. A source that names no device at all — <c>tmpfs</c>,
  /// <c>proc</c>, an overlay — resolves to nothing, which is why the return is a list and not a
  /// nullable name.
  /// </remarks>
  private IReadOnlyList<string> DisksBehind(string source) {
    if (NameOfDevice(source) is not { } name)
      return [];

    var found = new List<string>();
    this.Descend(name, found, depth: 0);
    return found;
  }

  /// <summary>
  /// Walks from one block device down to everything it is made of, collecting each device on the way.
  /// </summary>
  /// <remarks>
  /// Every level, not only the bottom one. A root file system on LVM inside LUKS is genuinely on
  /// <c>dm-0</c>, on <c>dm-1</c> and on the <c>nvme1n1</c> underneath them, and each of those has a
  /// page of its own with a row saying what it carries. Naming only the physical device would leave
  /// the mapper targets describing themselves as unused while the machine boots off them.
  /// <para>
  /// Three shapes: a device-mapper target or a RAID set has slaves and is also made of them; a
  /// partition appears only as a directory inside its disk's and is charged to that disk; anything
  /// else in <c>/sys/block</c> is a disk in its own right. The depth is bounded because a stacked
  /// setup is a graph the kernel promises is acyclic, and this should not depend on that promise.
  /// </para>
  /// </remarks>
  private void Descend(string name, List<string> found, int depth) {
    if (depth > 8 || found.Contains(name))
      return;

    if (Directory.Exists(Path.Combine(this._sysRoot, "block", name))) {
      found.Add(name);

      var slaves = Path.Combine(this._sysRoot, "block", name, "slaves");
      if (Directory.Exists(slaves))
        foreach (var slave in Directory.EnumerateFileSystemEntries(slaves))
          this.Descend(Path.GetFileName(slave), found, depth + 1);

      return;
    }

    // A partition: it appears only as a directory inside the disk that holds it, and the kernel
    // charges the disk for everything the partition does.
    var root = Path.Combine(this._sysRoot, "block");
    if (!Directory.Exists(root))
      return;

    foreach (var disk in Directory.EnumerateDirectories(root)) {
      if (!Directory.Exists(Path.Combine(disk, name)))
        continue;

      this.Descend(Path.GetFileName(disk), found, depth + 1);
      return;
    }
  }

  /// <summary>
  /// The kernel's name for whatever a mount source points at, or null where it points at nothing.
  /// </summary>
  /// <remarks>
  /// <c>/dev/mapper/vg-root</c> is a symlink to <c>/dev/dm-0</c> and is resolved through the device
  /// mapper's own naming rather than by following it, because the link is in <c>/dev</c> and a
  /// recorded tree has none. <c>/dev/disk/by-uuid/…</c> is followed where the link exists and given
  /// up on where it does not, which leaves the mount unattributed rather than attributed wrongly.
  /// </remarks>
  private string? NameOfDevice(string source) {
    if (!source.StartsWith("/dev/", StringComparison.Ordinal))
      return null;

    var leaf = source["/dev/".Length..];
    if (leaf.Length == 0)
      return null;

    if (leaf.StartsWith("mapper/", StringComparison.Ordinal))
      return this.MapperTarget(leaf["mapper/".Length..]);

    if (!leaf.Contains('/', StringComparison.Ordinal))
      return leaf;

    try {
      // by-uuid, by-partlabel and the rest: symlinks into /dev, and the leaf of the target is the
      // name /sys/block knows.
      var target = File.ResolveLinkTarget(source, returnFinalTarget: true);
      var resolved = target is null ? null : Path.GetFileName(target.FullName);
      return resolved is { Length: > 0 } ? resolved : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>Which <c>dm-N</c> carries a given device-mapper name, from the kernel's own listing.</summary>
  private string? MapperTarget(string mapperName) {
    var root = Path.Combine(this._sysRoot, "block");
    if (mapperName.Length == 0 || !Directory.Exists(root))
      return null;

    foreach (var candidate in Directory.EnumerateDirectories(root, "dm-*")) {
      var named = TryReadText(Path.Combine(candidate, "dm", "name"));
      if (string.Equals(named, mapperName, StringComparison.Ordinal))
        return Path.GetFileName(candidate);
    }

    return null;
  }

  /// <summary>
  /// The caller's own mount table.
  /// </summary>
  /// <remarks>
  /// <c>self/mountinfo</c> rather than <c>1/mountinfo</c>: the program describes the disks it can
  /// itself see, and inside a container the init process's table is either unreadable or a
  /// description of a machine this program is not on.
  /// </remarks>
  private static IReadOnlyList<MountInfoParser.Mount> ReadMounts(string procRoot) {
    var content = TryReadBytes(Path.Combine(procRoot, "self", "mountinfo"));
    if (content is null)
      return [];

    var mounts = new List<MountInfoParser.Mount>();
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty)
      if (MountInfoParser.TryParse(scanner.NextLine(), out var mount))
        mounts.Add(mount);

    return mounts;
  }

  private static byte[]? TryReadBytes(string path) {
    try {
      // Read as text and re-encoded rather than File.ReadAllBytes: several files under /proc report
      // a size of nought and a read of a zero-length file returns nothing at all.
      return File.Exists(path) ? Encoding.UTF8.GetBytes(File.ReadAllText(path)) : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  private static string? TryReadText(string path) {
    try {
      return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}
