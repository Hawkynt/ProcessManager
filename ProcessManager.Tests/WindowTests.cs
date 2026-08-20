using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Windows and the processes behind them (PRD §39).
/// </summary>
/// <remarks>
/// The enumeration itself needs a display and is exercised by the capture leg; what is checked here
/// is the thing a display cannot settle — that "no windows" and "this session will not tell you
/// about windows" stay different answers.
/// </remarks>
[TestFixture]
public sealed class WindowTests {

  /// <summary>
  /// Half of all Linux desktops are Wayland, where the protocol refuses by design. A caller that
  /// only receives a list cannot tell that from a machine with nothing on screen, and the difference
  /// is the whole explanation somebody needs (PRD §5.3).
  /// </summary>
  [Test]
  public void RefusingAndHavingNoneAreDifferentAnswers() {
    var refused = new WindowList(WindowSourceState.WaylandRefuses, []);
    var empty = new WindowList(WindowSourceState.Available, []);

    Assert.That(refused.Windows, Is.Empty);
    Assert.That(empty.Windows, Is.Empty);
    Assert.That(refused.State, Is.Not.EqualTo(empty.State));
    Assert.That(refused.Explain(), Is.Not.Empty);
    Assert.That(empty.Explain(), Is.Empty, "a desktop that answered needs no excuse");
  }

  [Test]
  public void EveryStateThatIsNotAvailableExplainsItself() {
    foreach (var state in Enum.GetValues<WindowSourceState>()) {
      var list = new WindowList(state, []);
      if (state == WindowSourceState.Available)
        continue;

      Assert.That(list.Explain(), Is.Not.Empty, state.ToString());
    }
  }

  /// <summary>
  /// The Wayland sentence has to say that XWayland windows still appear, because they do — a
  /// Wayland desktop shows a short list rather than none, and an unexplained short list reads as a
  /// broken program.
  /// </summary>
  [Test]
  public void TheWaylandExplanationMentionsXWayland() =>
    Assert.That(new WindowList(WindowSourceState.WaylandRefuses, []).Explain(), Does.Contain("XWayland"));

  [Test]
  public void ANotImplementedListIsTheDefaultAndSaysSo() {
    Assert.That(WindowList.NotImplemented.State, Is.EqualTo(WindowSourceState.NotImplemented));
    Assert.That(WindowList.NotImplemented.Explain(), Does.Contain("not implemented"));
  }

  /// <summary>
  /// A window whose owner the display server will not name is still worth listing: it is evidence
  /// that something is on screen the process list cannot account for.
  /// </summary>
  [Test]
  public void AWindowWithNoKnownOwnerIsStillAWindow() {
    var orphan = new WindowRecord(0x2000001, -1, "Something", "Unknown", (0, 0, 800, 600), true);

    Assert.That(orphan.Pid, Is.EqualTo(-1));
    Assert.That(orphan.Title, Is.Not.Empty);
  }

  /// <summary>
  /// A probe that has not learnt to look reports that, and never "this machine has no windows".
  /// </summary>
  [Test]
  public void AProbeWithNoImplementationSaysSoRatherThanClaimingNone() {
    // Typed as the interface deliberately: a default interface method is not inherited into the
    // implementing type and is reachable only through the interface, which is also how every caller
    // in the program reaches it.
    Abstractions.ISystemProbe probe = new Bare();

    Assert.That(probe.GetWindows().State, Is.EqualTo(WindowSourceState.NotImplemented));
    Assert.That(probe.WindowUnderPointer(), Is.Null);
  }

  private sealed class Bare : Abstractions.ISystemProbe {
    public string Description => "bare";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) => snapshot.PrepareProcesses(0);
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

}
