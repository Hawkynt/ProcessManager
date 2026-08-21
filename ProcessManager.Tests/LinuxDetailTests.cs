using System.Linq;
using System.Net;
using System.Net.Sockets;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The modules and descriptors of the process running the test, against the real kernel
/// (PRD §9.3, §31, §32).
/// </summary>
/// <remarks>
/// The fixture tests prove the parsers; these prove that what the parsers are handed is the right
/// file. They ask about <em>this</em> process, so the expected answer can be arranged rather than
/// guessed: a file opened to a known offset has that offset, and a socket that is listening on a
/// known port is in the network tables under the inode its descriptor reports.
/// </remarks>
[TestFixture]
[Platform("Linux", Reason = "Reads the live /proc of the process running the test.")]
public sealed partial class LinuxDetailTests {

  private static ProcessKey Self => new(Environment.ProcessId, 0);

  private static LinuxProbe Probe() => new(new LinuxProbeOptions());

  /// <summary>
  /// The count behind the window's handle column, against the kernel's own answer (PRD §32).
  /// </summary>
  /// <remarks>
  /// This is the on-demand path rather than the sampled one: the window asks for a count on its own
  /// schedule, because counting descriptors for every process every second is exactly the expense
  /// §5.4 exists to avoid. Every other test in the suite stubs it, so nothing held it against a real
  /// process until now.
  /// </remarks>
  [Test]
  public void ItCountsTheDescriptorsThisProcessHolds() {
    using var probe = Probe();

    // The kernel's own answer, read a completely different way.
    var expected = Directory.GetFileSystemEntries("/proc/self/fd").Length;

    var counted = probe.GetHandleCount(Self);

    Assert.That(counted.HasValue, Is.True, $"unknown, for a process we are inside: {counted.Reason}");
    // Not equality: opening the directory to count it is itself a descriptor, and it is open for one
    // of the two reads and not the other. A drift of a few is expected; a count of nought is not.
    Assert.That(counted.Value, Is.GreaterThan(0ul), "a running process holds descriptors");
    Assert.That((long)counted.Value, Is.EqualTo(expected).Within(4), "the kernel's count, near enough");
  }

  /// <summary>
  /// Opening more of them moves the number. A count that is merely plausible but fixed would pass
  /// the test above and still be wrong.
  /// </summary>
  [Test]
  public void OpeningDescriptorsMovesTheCount() {
    using var probe = Probe();
    var before = probe.GetHandleCount(Self);

    var files = new List<FileStream>();
    try {
      for (var i = 0; i < 8; ++i)
        files.Add(File.OpenRead("/proc/self/status"));

      var after = probe.GetHandleCount(Self);

      Assert.That(before.HasValue && after.HasValue, Is.True);
      Assert.That(after.Value, Is.EqualTo(before.Value + 8), "eight more descriptors, eight more counted");
    } finally {
      foreach (var file in files)
        file.Dispose();
    }
  }

  [Test]
  public void ItListsTheImagesThisProcessHasMapped() {
    using var probe = Probe();
    var modules = probe.GetModules(Self);

    Assert.That(modules, Is.Not.Empty);
    foreach (var module in modules)
      Assert.Multiple(() => {
        Assert.That(module.EndAddress, Is.GreaterThan(module.BaseAddress), module.Path);
        Assert.That(module.MappingCount, Is.GreaterThan(0), module.Path);
        // Own process, so smaps is readable and every one of these must be a number rather than a
        // reason. A silent fall-back to maps would show up here and nowhere else.
        Assert.That(module.ResidentBytes.HasValue, Is.True, module.Path);
        Assert.That(module.ResidentBytes.Value, Is.LessThanOrEqualTo(module.Size), module.Path);
      });
  }

  [Test]
  public void AMapFileLargerThanOnePageIsReadToItsEnd() {
    // /proc is a seq_file: it starts with a one-page internal buffer and returns whatever whole
    // records fit in it, so a read asking for sixteen kilobytes of a process's memory map comes back
    // with about four thousand bytes and more still to come. Treating that as end of file listed the
    // first mapping of this test host and dropped the other two hundred and eighty.
    var lines = File.ReadAllLines("/proc/self/maps");
    Assert.That(lines.Length, Is.GreaterThan(64), "the map file has to exceed one page for this to mean anything");

    // Parsed here, independently and crudely, so that the assertion is against the kernel's file and
    // not against a second call into the code under test.
    var expected = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in lines) {
      var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length < 6)
        continue;

      var start = line.IndexOf(fields[4], StringComparison.Ordinal) + fields[4].Length;
      var path = line[start..].Trim();
      if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
        path = path[..^" (deleted)".Length];

      if (path.StartsWith('/'))
        expected.Add(path);
    }

    using var probe = Probe();
    var reported = new HashSet<string>(StringComparer.Ordinal);
    foreach (var module in probe.GetModules(Self))
      reported.Add(module.Path);

    // Superset rather than equality: the runtime maps another assembly whenever it jits one, and the
    // two reads are a few milliseconds apart. What cannot happen is a path in the kernel's file that
    // the module list never saw.
    Assert.That(reported, Is.SupersetOf(expected));
    Assert.That(expected, Has.Count.GreaterThan(20), "this process maps more images than fit in one page of maps");
  }

  [Test]
  public void ASharedObjectIsDescribedFromItsOwnHeader() {
    using var probe = Probe();
    var modules = probe.GetModules(Self);
    ModuleRecord? found = null;
    foreach (var module in modules)
      if (Path.GetFileName(module.Path).StartsWith("libc.so", StringComparison.Ordinal))
        found = module;

    if (found is not { } libc) {
      Assert.Ignore($"This process has no libc mapping among its {modules.Count}: "
        + string.Join(", ", modules.Select(m => m.Path)));
      return;
    }

    Assert.Multiple(() => {
      Assert.That(libc.Type, Is.EqualTo(ModuleType.SharedObject));
      Assert.That(libc.Soname, Is.EqualTo(Path.GetFileName(libc.Path)));
      Assert.That(libc.Architecture, Is.Not.Null);
      // Cross-checked against the file system rather than against another read of /proc: the size the
      // modules view reports has to be the size of the file it names.
      Assert.That(libc.FileSizeBytes.Value, Is.EqualTo((ulong)new FileInfo(libc.Path).Length));
      Assert.That(libc.FileModifiedUtcTicks, Is.EqualTo(new FileInfo(libc.Path).LastWriteTimeUtc.Ticks));
      // A library is mapped several times over — text, rodata, relro, data — and the row is the
      // library, not the first of them.
      Assert.That(libc.MappingCount, Is.GreaterThan(1));
      Assert.That(libc.EntryPoint.HasValue, Is.True);
    });
  }

  /// <summary>
  /// <c>lseek</c>, because nothing in the BCL moves a descriptor's kernel offset any more.
  /// </summary>
  /// <remarks>
  /// .NET's Unix <c>FileStream</c> reads through <c>pread</c> and keeps its position in managed code,
  /// so a stream that has read forty bytes leaves the kernel's own offset at zero — which is the
  /// right answer, and makes a test written around <c>Stream.Read</c> assert nothing at all. The
  /// number this checks has to be put there by a syscall.
  /// </remarks>
  [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "lseek", SetLastError = true)]
  private static partial long Seek(int fd, long offset, int whence);

  [Test]
  public void AnOpenFileIsReportedWithItsPositionAndAccess() {
    var path = Path.Combine(Path.GetTempPath(), $"pm-descriptor-{Environment.ProcessId}.bin");
    File.WriteAllBytes(path, new byte[100]);
    try {
      using var stream = File.OpenRead(path);
      var fd = (int)stream.SafeFileHandle.DangerousGetHandle();
      Assert.That(Seek(fd, 40, 0), Is.EqualTo(40L), "lseek to the offset the assertion below expects");

      using var probe = Probe();
      HandleRecord? found = null;
      foreach (var handle in probe.GetHandles(Self))
        if (handle.Handle == (ulong)fd)
          found = handle;

      Assert.That(found, Is.Not.Null, "the descriptor this test opened is not in the list");
      var descriptor = found!.Value;
      Assert.That(descriptor.Name, Is.EqualTo(path));
      Assert.Multiple(() => {
        Assert.That(descriptor.Kind, Is.EqualTo(HandleKind.File));
        // The descriptor was seeked to forty of a hundred bytes, so the kernel's idea of where the
        // next read starts is forty — which is the whole of what "file offset" means in §32.
        Assert.That(descriptor.Position.Value, Is.EqualTo(40ul));
        Assert.That(descriptor.Access, Is.EqualTo("r"));
        Assert.That(descriptor.Inode.HasValue, Is.True);
        Assert.That(descriptor.TargetPid.HasValue, Is.False);
      });
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void ASocketDescriptorJoinsToTheConnectionItIs() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try {
      var port = ((IPEndPoint)listener.LocalEndpoint).Port;
      using var probe = Probe();

      var inodes = new HashSet<ulong>();
      foreach (var handle in probe.GetHandles(Self))
        if (handle.Kind == HandleKind.Socket && handle.Inode.TryGetValue(out var inode))
          inodes.Add(inode);

      Assert.That(inodes, Is.Not.Empty, "a listening socket has a descriptor and the descriptor has an inode");

      ConnectionRecord? listening = null;
      foreach (var connection in probe.GetConnections(Self))
        if (connection.LocalPort == port && connection.State == "LISTEN")
          listening = connection;

      Assert.That(listening, Is.Not.Null, $"nothing in the network tables is listening on {port}");
      // The join key of §32 and §40: the inode the descriptor reports is the inode of the row in
      // /proc/net/tcp. Without it, "this process holds a socket" is as far as the answer goes.
      Assert.That(inodes, Does.Contain(listening!.Value.Inode));
    } finally {
      listener.Stop();
    }
  }

  [Test]
  public void EveryDescriptorOfThisProcessIsListedAndClassified() {
    using var probe = Probe();
    var handles = probe.GetHandles(Self);
    var counted = probe.GetHandleCount(Self);

    Assert.That(counted.HasValue, Is.True);
    // Within one: the listing itself holds a descriptor on /proc/self/fd while it runs, and the two
    // reads are not simultaneous. Equality would be a flaky test rather than a stricter one.
    Assert.That(handles.Count, Is.EqualTo((int)counted.Value).Within(1));

    // Every descriptor the kernel would name is classified. One it would not is a descriptor that
    // closed between the directory listing and the readlink, which is a race the kernel is entitled
    // to win and not a gap in the classifier.
    foreach (var handle in handles)
      if (handle.Name is not null)
        Assert.That(handle.Kind, Is.Not.EqualTo(HandleKind.Unknown), handle.Name);
  }

}
