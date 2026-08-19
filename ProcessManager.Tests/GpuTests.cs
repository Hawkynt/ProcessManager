using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Graphics adapters (PRD §50).
/// </summary>
/// <remarks>
/// The parsers carry no platform attribute, so they are tested on every CI leg — including the
/// Windows one, which has no <c>/sys</c> to read (PRD §9.2).
/// </remarks>
[TestFixture]
public sealed class GpuTests {

  #region uevent

  private const string _Uevent = """
    DRIVER=nvidia
    PCI_CLASS=30000
    PCI_ID=10DE:24B6
    PCI_SUBSYS_ID=1028:0A6A
    PCI_SLOT_NAME=0000:01:00.0
    MODALIAS=pci:v000010DEd000024B6sv00001028sd00000A6Abc03sc00i00
    """;

  [Test]
  public void TheDriverAndTheIdentityComeOutOfUevent() {
    Assert.That(UeventParser.Value(_Uevent, "DRIVER").ToString(), Is.EqualTo("nvidia"));
    Assert.That(UeventParser.Value(_Uevent, "PCI_ID").ToString(), Is.EqualTo("10DE:24B6"));
  }

  [Test]
  public void AKeyThatIsNotThereIsAnEmptySpan() =>
    Assert.That(UeventParser.Value(_Uevent, "NOT_A_KEY").IsEmpty, Is.True);

  /// <summary>
  /// <c>PCI_ID</c> must not be answered by <c>PCI_SUBSYS_ID</c>, which is a different card entirely
  /// — the board's maker rather than the chip's. Matching a suffix instead of the whole key would.
  /// </summary>
  [Test]
  public void AKeyMatchesWholeAndNotAsASuffix() =>
    Assert.That(UeventParser.Value(_Uevent, "ID").IsEmpty, Is.True);

  [Test]
  public void AValueContainingAnEqualsSignSurvivesIntact() =>
    Assert.That(UeventParser.Value("A=b=c\n", "A").ToString(), Is.EqualTo("b=c"));

  [Test]
  public void AFileEditedOnWindowsParsesToo() =>
    Assert.That(UeventParser.Value("DRIVER=i915\r\nPCI_ID=8086:9A60\r\n", "DRIVER").ToString(), Is.EqualTo("i915"));

  [Test]
  public void RubbishIsNotAnError() {
    Assert.That(UeventParser.Value(string.Empty, "DRIVER").IsEmpty, Is.True);
    Assert.That(UeventParser.Value("no equals sign here", "DRIVER").IsEmpty, Is.True);
    Assert.That(UeventParser.Value("=leading", "DRIVER").IsEmpty, Is.True);
  }

  #endregion

  #region pci names

  [Test]
  public void TheThreeVendorsThatMatterAreNamed() {
    Assert.That(PciNames.Vendor(0x10DE), Is.EqualTo("NVIDIA"));
    Assert.That(PciNames.Vendor(0x1002), Is.EqualTo("AMD"));
    Assert.That(PciNames.Vendor(0x8086), Is.EqualTo("Intel"));
  }

  [Test]
  public void AnIdentityBecomesSomethingAPersonRecognises() {
    Assert.That(PciNames.Describe("10DE:24B6"), Is.EqualTo("NVIDIA 24b6"));
    Assert.That(PciNames.Describe("8086:9a60"), Is.EqualTo("Intel 9a60"));
  }

  /// <summary>
  /// A vendor this build has never heard of gets no name at all rather than a wrong one. The raw
  /// identity beside the driver is more use than "Unknown 24b6".
  /// </summary>
  [Test]
  public void AVendorThatIsNotKnownIsNotGuessedAt() {
    Assert.That(PciNames.Vendor(0xBEEF), Is.Null);
    Assert.That(PciNames.Describe("BEEF:1234"), Is.Null);
  }

  [Test]
  public void AnIdentityThatMakesNoSenseIsRejected() {
    Assert.That(PciNames.Describe("nonsense"), Is.Null);
    Assert.That(PciNames.Describe("10DE:"), Is.Null);
    Assert.That(PciNames.Describe(":24B6"), Is.Null);
    Assert.That(PciNames.Describe(string.Empty), Is.Null);
  }

  [Test]
  public void ZeroExPrefixesAreAcceptedBecausePeopleWriteThem() {
    Assert.That(PciNames.TryParse("0x10DE:0x24B6", out var vendor, out var device), Is.True);
    Assert.That(vendor, Is.EqualTo(0x10DE));
    Assert.That(device, Is.EqualTo(0x24B6));
  }

  #endregion

  #region the page

  private sealed class StubProbe : ISystemProbe {
    public List<GpuInfo> Gpus { get; } = [];
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
    public IReadOnlyList<GpuInfo> DescribeGpus() => this.Gpus;
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

  private static GpuInfo Adapter(string name, string? model, Counter busy)
    => new(
      name, model, "amdgpu", busy,
      Counter.Of(2ul * 1024 * 1024 * 1024),
      Counter.Of(8ul * 1024 * 1024 * 1024),
      Counter.Of(52_000),
      Counter.Of(31_500_000),
      "D0"
    );

  private static IReadOnlyList<PerformanceSection> Sections(params GpuInfo[] gpus) {
    var probe = new StubProbe();
    probe.Gpus.AddRange(gpus);
    var snapshot = new SystemSnapshot();
    snapshot.PrepareProcesses(0);
    return PerformanceReport.Build(new(), snapshot, null, null, null, probe.DescribeGpus);
  }

  [Test]
  public void EachAdapterGetsItsOwnSection() {
    var titles = new List<string>();
    foreach (var section in Sections(
      Adapter("card0", "AMD 73ff", Counter.Of(42)),
      Adapter("card1", "Intel 9a60", Counter.Of(3))
    ))
      titles.Add(section.Title);

    Assert.That(titles, Does.Contain("GPU — AMD 73ff"));
    Assert.That(titles, Does.Contain("GPU — Intel 9a60"));
  }

  [Test]
  public void AMachineWithNoAdapterGetsNoHeading() {
    foreach (var section in Sections())
      Assert.That(section.Title, Does.Not.StartWith("GPU"));
  }

  [Test]
  public void AnAdapterThatWillNotSayHowBusyItIsSaysSoRatherThanZero() {
    var section = Find(Sections(Adapter("card0", "NVIDIA 24b6", Counter.Unknown(UnknownReason.NotImplementedHere))));

    Assert.That(section.PrimaryLabel, Is.EqualTo("n/i"));
    Assert.That(section.PrimaryLabel, Does.Not.Contain("0"));
    Assert.That(Value(section, "Utilisation"), Is.EqualTo("n/i"));
  }

  /// <summary>Nobody wants thousandths of a degree or millionths of a watt.</summary>
  [Test]
  public void TemperatureAndPowerAreShownInTheUnitsPeopleUse() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42))));

    Assert.That(Value(section, "Temperature"), Is.EqualTo("52.0 °C"));
    Assert.That(Value(section, "Power"), Is.EqualTo("31.5 W"));
  }

  [Test]
  public void AnAdapterWithNoRecognisedVendorIsStillListedByItsCardName() {
    var titles = new List<string>();
    foreach (var section in Sections(Adapter("card2", null, Counter.Of(1))))
      titles.Add(section.Title);

    Assert.That(titles, Does.Contain("GPU — card2"));
  }

  private static PerformanceSection Find(IReadOnlyList<PerformanceSection> sections) {
    foreach (var section in sections)
      if (section.Title.StartsWith("GPU", StringComparison.Ordinal))
        return section;

    Assert.Fail("no GPU section");
    return default;
  }

  private static string Value(PerformanceSection section, string label) {
    foreach (var row in section.Rows)
      if (row.Label == label)
        return row.Value;

    Assert.Fail($"no row called {label}");
    return string.Empty;
  }

  #endregion

}
