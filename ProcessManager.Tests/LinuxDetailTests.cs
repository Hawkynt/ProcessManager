using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

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

  /// <summary>
  /// <c>open</c>, for the one descriptor the managed API will not make: a directory.
  /// </summary>
  /// <remarks>
  /// .NET refuses to open a directory as a stream, and a directory descriptor is exactly what the
  /// classifier had to guess about before the kernel was asked — so the test that proves it no
  /// longer guesses has to make one the way a file manager does.
  /// </remarks>
  [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
  private static partial int OpenPath(string path, int flags);

  [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
  private static partial int ClosePath(int fd);

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

  /// <summary>
  /// §31's version, description, company and product, end to end against this machine's own
  /// packaging database.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The parser tests replay a recorded database; this asks the live one about a file the process
  /// running the test actually has mapped, which is the half that proves the path from a mapping to
  /// a package exists at all. Skipped rather than failed where no packaging system this program can
  /// read is installed — a machine managed by <c>rpm</c> is not a machine where this is broken.
  /// </para>
  /// <para>
  /// The values themselves are not asserted: what <c>libc</c>'s package is called and who assembled
  /// it differ between distributions, and pinning either would be a test of the machine rather than
  /// of the lookup. What is asserted is that a package answered, and that the answer is the package's
  /// and is marked as such.
  /// </para>
  /// </remarks>
  [Test]
  public void ThePackagingDatabaseAnswersForALibraryThisProcessHasMapped() {
    using var probe = Probe();
    string? library = null;
    foreach (var module in probe.GetModules(Self))
      if (Path.GetFileName(module.Path).StartsWith("libc.so", StringComparison.Ordinal))
        library = module.Path;

    if (library is null) {
      Assert.Ignore("This process has no libc mapping to ask about.");
      return;
    }

    var trust = probe.DescribeImage(library);
    if (!trust.Package.WasChecked) {
      Assert.Ignore($"No packaging database this program reads is installed: {trust.Package.Reason}.");
      return;
    }

    if (trust.Package.Source == PackageSource.None) {
      Assert.Ignore($"Nothing on this machine claims {library}.");
      return;
    }

    Assert.Multiple(() => {
      // Product and version: the package's, and the cell says which system answered.
      Assert.That(trust.Package.Name, Is.Not.Null.And.Not.Empty);
      Assert.That(trust.Package.Version, Is.Not.Null.And.Not.Empty);
      Assert.That(trust.Package.Text, Does.Contain(trust.Package.Name!));

      // Description and company. Either may be absent from a given package, and absent is null
      // rather than an empty string a properties box would render as a blank field (PRD §72.3).
      Assert.That(trust.Summary, Is.Null.Or.Not.Empty);
      Assert.That(trust.Publisher, Is.Null.Or.Not.Empty);

      // Nothing was hashed, because nobody asked. The verdict is the one field that costs a read of
      // the whole file, and this call did not request one (PRD §5.4).
      Assert.That(trust.Sha256, Is.Null);
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.NotChecked));
    });
  }

  #region what the kernel says a descriptor points at (PRD §32 — file type, device)

  /// <summary>
  /// The file type, against the seven the kernel actually reports — including the one that is not a
  /// type at all.
  /// </summary>
  /// <remarks>
  /// Every kind is opened here rather than looked for in whatever the runtime happens to hold,
  /// because the interesting one is the anonymous inode: an eventfd's <c>st_mode</c> is <c>0600</c>
  /// with the type bits clear, and a reader that maps that nought onto a POSIX type files every
  /// event descriptor on the machine under something it is not (PRD §72.3).
  /// </remarks>
  [Test]
  public void EveryKindOfNodeIsReportedAsTheKernelsOwnStatWouldReportIt() {
    var path = Path.Combine(Path.GetTempPath(), $"pm-nodes-{Environment.ProcessId}.bin");
    File.WriteAllBytes(path, new byte[8]);
    var fifo = Path.Combine(Path.GetTempPath(), $"pm-fifo-{Environment.ProcessId}");
    try {
      using var file = File.OpenRead(path);
      using var device = File.OpenRead("/dev/null");
      // O_RDONLY | O_DIRECTORY, as the kernel numbers them on every architecture this builds for.
      var directory = OpenPath(Path.GetTempPath(), 0x10000);
      var pipe = new AnonymousPipeServerStream();
      using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

      using var probe = Probe();
      var byFd = probe.GetHandles(Self).ToDictionary(h => h.Handle);

      Assert.Multiple(() => {
        Assert.That(Node(byFd, file).NodeType, Is.EqualTo(FileNodeType.Regular));
        Assert.That(Node(byFd, directory).NodeType, Is.EqualTo(FileNodeType.Directory));
        Assert.That(Node(byFd, socket.Handle.ToInt64()).NodeType, Is.EqualTo(FileNodeType.Socket));
        Assert.That(Node(byFd, pipe.ClientSafePipeHandle.DangerousGetHandle().ToInt64()).NodeType, Is.EqualTo(FileNodeType.Fifo));

        // /dev/null is character device 1:3 on every Linux there has ever been, and the number it
        // *is* must not be confused with the device it is *on* — the devtmpfs it lives on has a
        // different one, and that is the other column.
        var nul = Node(byFd, device);
        Assert.That(nul.NodeType, Is.EqualTo(FileNodeType.CharacterDevice));
        Assert.That(nul.NodeDevice, Is.EqualTo("1:3"));
        Assert.That(nul.NodeDevice, Is.Not.EqualTo(nul.Device));
        Assert.That(Humanize.FileNode(nul.NodeType, nul.NodeDevice), Is.EqualTo("character 1:3"));
      });

      pipe.Dispose();
      if (directory >= 0)
        ClosePath(directory);
    } finally {
      File.Delete(path);
      File.Delete(fifo);
    }
  }

  /// <summary>
  /// An anonymous inode has no file type, which is a fact about eventfds and not a failure to look.
  /// </summary>
  [Test]
  public void AnAnonymousInodeHasNoFileTypeAndSaysThatRatherThanNothing() {
    using var probe = Probe();
    var anonymous = probe.GetHandles(Self)
      .Where(h => h.Kind is HandleKind.Event or HandleKind.EventPoll or HandleKind.Timer or HandleKind.Signal)
      .ToList();

    if (anonymous.Count == 0) {
      Assert.Ignore("This runtime is holding no anonymous inode to ask about.");
      return;
    }

    Assert.Multiple(() => {
      foreach (var handle in anonymous)
        Assert.That(handle.NodeType, Is.EqualTo(FileNodeType.None), handle.Name);

      Assert.That(Humanize.FileNode(FileNodeType.None), Is.EqualTo("no type"));
      // And the two that must never render alike: "there is no type" and "nobody asked".
      Assert.That(Humanize.FileNode(FileNodeType.None), Is.Not.EqualTo(Humanize.FileNode(FileNodeType.Unknown)));
    });
  }

  /// <summary>
  /// The kernel corrects the name where the name was only a guess: a FIFO on a path outside
  /// <c>/dev</c> looks exactly like an ordinary file to the classifier that reads the link target.
  /// </summary>
  [Test]
  public void ANamedPipeOutsideDevIsAPipeAndNotAFile() {
    using var probe = Probe();
    foreach (var handle in probe.GetHandles(Self))
      if (handle.NodeType == FileNodeType.Fifo)
        Assert.That(handle.Kind, Is.EqualTo(HandleKind.Pipe), handle.Name);
  }

  #endregion

  #region how many holders a socket has (PRD §32 — reference count)

  /// <summary>
  /// The reference count of §32, out of the network table's own column and held against the fact
  /// that no live socket has none.
  /// </summary>
  /// <remarks>
  /// Not asserted as an exact number. A socket held by one descriptor commonly reads two or three —
  /// the descriptor is one reference and the protocol's hash tables are the rest — and pinning the
  /// figure would be a test of this kernel version rather than of the parse. What is asserted is
  /// what the field means: it was read, and it is not nought.
  /// </remarks>
  [Test]
  public void AListeningSocketHasHoldersAndTheTableSaysHowMany() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try {
      var port = ((IPEndPoint)listener.LocalEndpoint).Port;
      using var probe = Probe();

      ConnectionRecord? listening = null;
      foreach (var connection in probe.GetConnections(Self))
        if (connection.LocalPort == port && connection.State == "LISTEN")
          listening = connection;

      Assert.That(listening, Is.Not.Null, $"nothing in the network tables is listening on {port}");
      Assert.Multiple(() => {
        Assert.That(listening!.Value.References.HasValue, Is.True, "the column is there on every kernel that has the table");
        Assert.That(listening!.Value.References.Value, Is.GreaterThan(0ul), "a socket in the table has at least the table's own reference");
      });
    } finally {
      listener.Stop();
    }
  }

  /// <summary>
  /// A Unix socket's count is in the same column of a different table, printed in a different base
  /// — hex there, decimal in the internet tables — which is a fact about the kernel's format strings
  /// and not about sockets.
  /// </summary>
  [Test]
  public void AUnixSocketsCountIsReadFromItsOwnTable() {
    using var probe = Probe();
    var unix = probe.GetConnections(Self).Where(c => c.Protocol == ConnectionProtocol.Unix).ToList();
    if (unix.Count == 0) {
      Assert.Ignore("This process holds no Unix socket.");
      return;
    }

    foreach (var connection in unix)
      Assert.That(connection.References.HasValue, Is.True, connection.LocalAddress);
  }

  #endregion

  /// <summary>
  /// The measurement behind §32's unticked "creation / open time": the timestamps on
  /// <c>/proc/[pid]/fd/[n]</c> belong to the <c>procfs</c> directory entry and not to the open.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Shown by reusing a descriptor number. One file is opened and its link looked at; the
  /// descriptor is closed, a second and quite different file is opened over a second later, and the
  /// kernel hands back the same number. The link now points at the second file and its timestamp
  /// has not moved by a nanosecond — so a program reporting it as an open time would say the second
  /// file had been open since before it existed.
  /// </para>
  /// <para>
  /// Kept as a test rather than as a sentence in a document, because it is the whole justification
  /// for leaving a box unticked and it is the kind of claim that quietly stops being true. If a
  /// kernel ever starts recording the open, this fails and the box becomes answerable.
  /// </para>
  /// </remarks>
  [Test]
  public void TheKernelRecordsNoTimeAtWhichADescriptorWasOpened() {
    var first = Path.Combine(Path.GetTempPath(), $"pm-opened-a-{Environment.ProcessId}.bin");
    var second = Path.Combine(Path.GetTempPath(), $"pm-opened-b-{Environment.ProcessId}.bin");
    File.WriteAllBytes(first, new byte[8]);
    File.WriteAllBytes(second, new byte[8]);
    try {
      var fd = OpenPath(first, 0);
      Assert.That(fd, Is.GreaterThanOrEqualTo(0), "the first file did not open");

      var link = $"/proc/self/fd/{fd}";
      var before = new FileInfo(link).LastWriteTimeUtc;
      ClosePath(fd);

      // Well past any clock granularity. Nothing else in this test opens a descriptor, so the
      // kernel hands the lowest free number back — the one just closed.
      Thread.Sleep(1200);
      var again = OpenPath(second, 0);
      if (again != fd) {
        ClosePath(again);
        Assert.Ignore("The kernel did not reuse the descriptor number, so there is nothing to compare.");
        return;
      }

      var after = new FileInfo(link).LastWriteTimeUtc;
      var target = File.ResolveLinkTarget(link, returnFinalTarget: false)?.FullName;
      ClosePath(again);

      Assert.Multiple(() => {
        // A genuinely different open, over a second later.
        Assert.That(target, Is.EqualTo(second));
        Assert.That(
          after,
          Is.EqualTo(before),
          "the timestamp is the directory entry's; it did not move when the descriptor behind it was replaced"
        );
      });
    } finally {
      File.Delete(first);
      File.Delete(second);
    }
  }

  private static HandleRecord Node(Dictionary<ulong, HandleRecord> byFd, FileStream stream)
    => Node(byFd, stream.SafeFileHandle.DangerousGetHandle().ToInt64());

  private static HandleRecord Node(Dictionary<ulong, HandleRecord> byFd, long fd) {
    Assert.That(byFd.ContainsKey((ulong)fd), Is.True, $"descriptor {fd} is not in the list");
    return byFd[(ulong)fd];
  }

}
