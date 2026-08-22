using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// That a row formats everything anything reads off it (PRD §71.5, §72.3).
/// </summary>
/// <remarks>
/// <para>
/// A row used to format all hundred and sixty-odd catalogue fields for every process on every
/// sample, of which a table shows perhaps ten. Invisible at four hundred processes and the whole of
/// a 193 ms frame at ten thousand, which made it the largest single cost in the window.
/// </para>
/// <para>
/// It formats what it is asked for now, and the danger in that is a blank cell rather than an
/// error — a page reads a field nobody told the row about and quietly shows nothing. So the set is
/// held here against what every consumer declares it needs, rather than against somebody's memory
/// of which pages exist.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FormattedFieldsTests {

  /// <summary>A probe that answers nothing, because none of this reads the machine.</summary>
  private sealed class SilentProbe : ISystemProbe {
    public string Description => "stub";
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

  /// <summary>
  /// Every field the properties window puts on a page is one the rows are told to format.
  /// </summary>
  /// <remarks>
  /// This is the assertion the change needed to be safe. Without it, adding a row to a properties
  /// page works when the same field happens to be a column and shows an empty cell when it does not
  /// — which is the worst shape a regression can take, because it looks like the machine having
  /// nothing to say.
  /// </remarks>
  [Test]
  public void EveryFieldAPropertiesPageShowsIsOneTheRowsFormat() {
    var probe = new SilentProbe();
    var window = new MainWindow(new Sampler(probe), probe, null);
    var formatted = new HashSet<ProcessField>(window.FieldsRead());

    Assert.Multiple(() => {
      foreach (var field in ProcessPropertiesWindow.FieldsShown)
        Assert.That(
          formatted.Contains(field),
          Is.True,
          $"{FieldRegistry.Get(field).Key} is on a properties page and no row formats it"
        );
    });
  }

  /// <summary>
  /// And the columns on screen, which is the obvious half and the one a change to the column set
  /// could still break.
  /// </summary>
  [Test]
  public void EveryVisibleColumnIsOneTheRowsFormat() {
    var probe = new SilentProbe();
    var window = new MainWindow(new Sampler(probe), probe, null);
    var formatted = new HashSet<ProcessField>(window.FieldsRead());

    Assert.Multiple(() => {
      foreach (var field in ColumnSet.Default)
        Assert.That(formatted.Contains(field), Is.True, FieldRegistry.Get(field).Key);
    });
  }

  /// <summary>
  /// It really is a subset, or the change bought nothing. The point was to stop formatting the whole
  /// catalogue, and a set that happens to contain all of it would pass every assertion above while
  /// doing exactly what it replaced.
  /// </summary>
  [Test]
  public void ItIsFewerThanTheWholeCatalogue() {
    var probe = new SilentProbe();
    var window = new MainWindow(new Sampler(probe), probe, null);

    TestContext.Out.WriteLine($"{window.FieldsRead().Count} of {FieldRegistry.All.Length} formatted");
    Assert.That(window.FieldsRead(), Has.Count.LessThan(FieldRegistry.All.Length / 2));
  }

}
