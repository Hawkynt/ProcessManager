using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Picks the probe for the running platform.
/// </summary>
/// <remarks>
/// A plain <c>if</c> on <see cref="OperatingSystem"/>, not a registry or a scan. The trimmer can see
/// that a Linux build never reaches the Windows branch and drops that assembly entirely, which is
/// what keeps a single-platform publish small (PRD §2).
/// </remarks>
internal static class ProbeFactory {

  /// <summary>
  /// The channel to the privileged helper, shared by the probe and the actions.
  /// </summary>
  /// <remarks>
  /// Constructed eagerly and <em>started</em> lazily: making the object costs nothing and prompts
  /// nobody, and the first request that needs root is what raises the polkit dialog. A program that
  /// asked for a password on start-up would be a program people stop opening (PRD §8.1).
  /// </remarks>
  public static ElevatedChannel? Elevated { get; private set; }

  /// <param name="wantSecurityContext">
  /// Whether anything on this run actually asked for the LSM label. It costs a file per process, so
  /// it is read only when a column or a filter names it — which is §5.4 enforced rather than stated.
  /// </param>
  /// <param name="wantHandleCount">
  /// Whether anything on this run asked how many descriptors each process holds. It costs a
  /// directory listing per process per sample — the most expensive thing in the sampler — so it is
  /// read only when a column or a filter names it (PRD §5.4).
  /// </param>
  /// <param name="wantSupplementaryGroups">
  /// Whether anything on this run asked for the group list. It costs a string per process per
  /// sample, so it is kept only when a column or a filter names it (PRD §5.4).
  /// </param>
  /// <param name="wantGpuUsage">
  /// Whether anything on this run asked what the processes are doing to the graphics adapters. It
  /// costs a scan of every process's descriptors and a library call per card, so it is collected
  /// only when a column, a filter or <c>--gpu</c> names it — §5.4 enforced rather than stated.
  /// </param>
  /// <param name="wantCpuAffinity">
  /// Whether anything on this run asked which processors each process may use. The line is free and
  /// keeping it is a string per process per sample, so it is kept only when a column or a filter
  /// names it (PRD §5.4, §15).
  /// </param>
  /// <param name="wantCpuThrottling">
  /// Whether anything on this run asked how often each process's cgroup has been held back. It
  /// costs a file per cgroup per sample, so it is read only when a column or a filter names it.
  /// </param>
  /// <param name="wantImageHashes">
  /// Whether anything on this run asked for the digest of each process's image. Its cost is the
  /// size of the files rather than a syscall, so it happens only when a column or a filter names
  /// one of the two digests (PRD §5.4, §21).
  /// </param>
  /// <param name="wantDescriptorKinds">
  /// Whether anything on this run asked how many sockets, files or pipes each process holds. It
  /// costs the descriptor scan plus a link resolved per descriptor, which is the most expensive
  /// read there is, so it is done only when a column or a filter names one of the three (PRD §20).
  /// </param>
  /// <param name="wantSocketCounts">
  /// Whether anything on this run asked how many sockets each process holds. It costs a
  /// <c>readlink</c> per open descriptor on the machine, which makes it dearer than the descriptor
  /// count on its own, so the same rule applies (PRD §5.4, §18).
  /// </param>
  public static ISystemProbe? Create(
    string? probeRoot,
    bool useHelper = true,
    bool wantSecurityContext = false,
    bool wantProportionalSetSize = false,
    bool wantSupplementaryGroups = false,
    bool wantGpuUsage = false,
    bool wantHandleCount = false,
    bool wantCpuAffinity = false,
    bool wantCpuThrottling = false,
    bool wantDescriptorKinds = false,
    bool wantImageHashes = false,
    bool wantSocketCounts = false
  ) {
    if (OperatingSystem.IsLinux()) {
      // A recorded tree is somebody else's machine; asking a helper about pids in it would be asking
      // about whatever happens to hold those pids here.
      if (useHelper && probeRoot is null)
        Elevated = new(FindHelper());

      // The batteries and sensor chips, for anything that asks. Only for the live machine: a
      // recorded tree is somebody else's, and this machine's battery has nothing to do with it.
      if (probeRoot is null) {
        var sensors = new Platform.Linux.SysfsSensorReader();
        Query.SensorSources.Batteries = sensors.ReadBatteries;
        Query.SensorSources.Sensors = sensors.ReadSensors;
      }

      return new Platform.Linux.LinuxProbe(
        probeRoot is null
          ? new() {
            Elevated = Elevated,
            ReadSecurityContext = wantSecurityContext,
            UseProportionalSetSize = wantProportionalSetSize,
            ReadSupplementaryGroups = wantSupplementaryGroups,
            ReadGpuUsage = wantGpuUsage,
            CountFileDescriptors = wantHandleCount,
            ReadCpuAffinity = wantCpuAffinity,
            CountDescriptorKinds = wantDescriptorKinds,
            ReadImageHashes = wantImageHashes,
            ReadCpuThrottling = wantCpuThrottling,
            ReadSocketCounts = wantSocketCounts,
          }
          // A recorded tree was captured by somebody else, so the live user's id would refuse every
          // file in it. Root reads everything, which is what a replay wants (PRD §9.1).
          : new() {
            ProcRoot = probeRoot,
            PasswdPath = Path.Combine(probeRoot, "passwd"),
            EffectiveUserId = 0,
            ReadSecurityContext = wantSecurityContext,
            ReadSupplementaryGroups = wantSupplementaryGroups,
            // A recorded tree has no adapters, and the live machine's cards have nothing to do
            // with the machine that was recorded. Its descriptors are another matter: a captured
            // tree carries the fd directories it was captured with, and counting those is the
            // point of having captured them.
            ReadGpuUsage = false,
            CountFileDescriptors = wantHandleCount,
            ReadCpuAffinity = wantCpuAffinity,
            CountDescriptorKinds = wantDescriptorKinds,
            ReadImageHashes = wantImageHashes,
            // The recorded tree carries the process's cgroup path, but the group it names lives on
            // the machine that was recorded and its counters are not in the capture.
            ReadCpuThrottling = false,
            // The socket tables and the fd links are both in a recorded tree, so this replays the
            // same way the descriptor count does.
            ReadSocketCounts = wantSocketCounts,
          }
      );
    }

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProbe();

    if (OperatingSystem.IsMacOS())
      return new Platform.MacOS.MacOsProbe();

    return null;
  }

  public static IProcessActions? CreateActions(string? probeRoot) {
    if (OperatingSystem.IsLinux())
      return new Platform.Linux.LinuxProcessActions(
        probeRoot is null ? new() { Elevated = Elevated } : new() { ProcRoot = probeRoot }
      );

    if (OperatingSystem.IsWindows())
      return new Platform.Windows.WindowsProcessActions();

    return null;
  }

  /// <summary>
  /// Where the helper is. The installed path first, because that is the one the polkit policy names
  /// and therefore the only one that can actually be elevated; the build layout second, so that
  /// `--helper-check` works from a source tree.
  /// </summary>
  private static string FindHelper() {
    foreach (var candidate in (ReadOnlySpan<string>)[
      "/usr/lib/procman/procman-helper",
      "/usr/local/lib/procman/procman-helper",
      Path.Combine(AppContext.BaseDirectory, "procman-helper"),
    ])
      if (File.Exists(candidate))
        return candidate;

    return "/usr/lib/procman/procman-helper";
  }

}
