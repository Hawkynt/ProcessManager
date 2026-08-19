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

  // Counter? and not Counter: default(Counter) is a confident zero, so it cannot be a sentinel for
  // "the caller said nothing" — which is the very bug the tests below are about.
  private static GpuInfo Adapter(string name, string? model, Counter busy, Counter? limit = null)
    => new(
      name, model, "amdgpu", busy,
      MemoryUsedBytes: Counter.Of(2ul * 1024 * 1024 * 1024),
      MemoryTotalBytes: Counter.Of(8ul * 1024 * 1024 * 1024),
      TemperatureMilliCelsius: Counter.Of(52_000),
      PowerMicrowatts: Counter.Of(31_500_000),
      PowerState: "D0",
      PowerLimitMicrowatts: limit ?? Counter.Unknown(UnknownReason.NotImplementedHere),
      PowerCapMicrowatts: Counter.Unknown(UnknownReason.NotImplementedHere),
      MemoryBusyPercent: Counter.Unknown(UnknownReason.NotImplementedHere),
      CoreClockHertz: Counter.Unknown(UnknownReason.NotImplementedHere),
      MemoryClockHertz: Counter.Unknown(UnknownReason.NotImplementedHere),
      FanPercent: Counter.Unknown(UnknownReason.NotImplementedHere)
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
    Assert.That(Value(section, "Power"), Is.EqualTo("31.5 W"), "no ceiling to compare against");
  }

  /// <summary>
  /// The draw is shown against the ceiling, because thirty watts means something entirely different
  /// at a forty-watt cap than at a four-hundred-watt one.
  /// </summary>
  [Test]
  public void PowerIsShownAgainstTheCeilingWhenThereIsOne() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42), Counter.Of(130_000_000))));

    Assert.That(Value(section, "Power"), Is.EqualTo("31.5 W of 130.0 W"));
  }

  /// <summary>
  /// The trap this record has no defaults for: default(Counter) is a reading of nought that was
  /// never taken, so a ceiling nobody supplied would render as "of 0.0 W" beside a card drawing
  /// thirty.
  /// </summary>
  [Test]
  public void AnUnknownCeilingIsNotAZeroWattCeiling() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42))));

    Assert.That(Value(section, "Power"), Does.Not.Contain("0.0 W"));
    Assert.That(Value(section, "Power cap"), Is.EqualTo("n/i"));
  }

  [Test]
  public void AnAdapterWithNoRecognisedVendorIsStillListedByItsCardName() {
    var titles = new List<string>();
    foreach (var section in Sections(Adapter("card2", null, Counter.Of(1))))
      titles.Add(section.Title);

    Assert.That(titles, Does.Contain("GPU — card2"));
  }

  #region the stack of graphs (PRD §50.1)

  /// <summary>
  /// A GPU is the case that forces more than one plot: six readings that move independently. A card
  /// can be at full utilisation and cold, or idle and hot, and only seeing both explains either.
  /// </summary>
  [Test]
  public void AnAdapterThatReportsEverythingGetsAGraphForEach() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42), Counter.Of(200_000_000))));

    var labels = new List<string>();
    foreach (var graph in section.Series)
      labels.Add(graph.Label);

    Assert.That(labels, Is.EqualTo(new[] { "Utilisation", "Dedicated memory", "Power", "Temperature" }));
  }

  /// <summary>
  /// §45.6: a category the hardware does not have is hidden, not emptied. A laptop card with no fan
  /// of its own must not get a permanently flat fan graph implying it has one that never spins.
  /// </summary>
  [Test]
  public void AReadingTheCardDoesNotReportGetsNoGraphAtAll() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42))));

    foreach (var graph in section.Series)
      Assert.That(graph.Label, Is.Not.EqualTo("Fan"));
  }

  /// <summary>
  /// Utilisation stays even when it is unknown, because its absence is the finding. "This card has
  /// no fan" and "nobody can tell you what this card is doing" are different sentences.
  /// </summary>
  [Test]
  public void UtilisationIsAlwaysPlottedEvenWhenNobodyCanReadIt() {
    var section = Find(Sections(Adapter("card0", "NVIDIA 24b6", Counter.Unknown(UnknownReason.NotImplementedHere))));

    Assert.That(section.Series[0].Label, Is.EqualTo("Utilisation"));
    Assert.That(section.Series[0].Value.HasValue, Is.False);
  }

  /// <summary>Temperature takes its own colour so it never reads as another utilisation figure.</summary>
  [Test]
  public void TemperatureIsNotDrawnLikeALoad() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42))));

    foreach (var graph in section.Series) {
      if (graph.Label != "Temperature")
        continue;

      Assert.That(graph.Accent, Is.EqualTo("temperature"));
      Assert.That(graph.Maximum, Is.EqualTo(100), "a fixed scale, or a card idling at 40-42 °C fills its graph");
      return;
    }

    Assert.Fail("no temperature graph");
  }

  /// <summary>Memory is scaled to the card's own VRAM, so the fill height is the fraction in use.</summary>
  [Test]
  public void DedicatedMemoryIsScaledToTheCardsOwnMemory() {
    var section = Find(Sections(Adapter("card0", "AMD 73ff", Counter.Of(42))));

    foreach (var graph in section.Series) {
      if (graph.Label != "Dedicated memory")
        continue;

      Assert.That(graph.Maximum, Is.EqualTo(8d * 1024 * 1024 * 1024));
      Assert.That(graph.ValueLabel, Is.EqualTo("2.0G / 8.0G"));
      return;
    }

    Assert.Fail("no memory graph");
  }

  /// <summary>
  /// A resource that named no series still has one — the one its own primary describes — so a caller
  /// never has to ask which shape it is dealing with.
  /// </summary>
  [Test]
  public void AResourceThatNamedNoGraphsStillHasOne() {
    foreach (var section in Sections()) {
      if (section.Title != "System")
        continue;

      Assert.That(section.Series, Has.Count.EqualTo(1));
      Assert.That(section.Series[0].Label, Is.EqualTo("System"));
      return;
    }

    Assert.Fail("no system section");
  }

  #endregion

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
