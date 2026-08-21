using Hawkynt.ProcessManager.App;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// A column the terminal shows is a column the sampler has to be told to collect (PRD §5.4).
/// </summary>
/// <remarks>
/// The expensive fields are opt-in, and the opt-in is inferred from the columns a run names rather
/// than from a switch — which was airtight while the only way to name one was <c>--columns</c>. The
/// terminal has its own list now: it keeps the drawn histories that a file cannot carry, and it can
/// come out of the settings file, where nothing on this command line mentions it. Without these, a
/// saved <c>columns.terminal</c> naming a descriptor tally would show "not sampled" on every row for
/// the whole session, which is honest and useless.
/// </remarks>
[TestFixture]
public sealed class TerminalColumnRequestTests {

  private static CommandLineOptions Parse(params string[] args) => CommandLineOptions.Parse(args);

  private static CommandLineOptions WithSavedTerminalColumns(params ProcessField[] fields)
    => CommandLineOptions.Parse(["--tui"], new UserSettings { TerminalColumns = fields });

  [Test]
  public void ASavedTerminalColumnAsksForWhatItNeeds() {
    var options = WithSavedTerminalColumns(ProcessField.Name, ProcessField.SocketCount);
    Assert.Multiple(() => {
      Assert.That(options.WantsDescriptorKinds, Is.True, "the tally the column is made of");
      Assert.That(options.WantsHandleCount, Is.True, "which is the descriptor scan underneath it");
    });
  }

  [Test]
  public void AndOnlyWhatItNeeds() {
    var options = WithSavedTerminalColumns(ProcessField.Name, ProcessField.CpuPercent);
    Assert.Multiple(() => {
      Assert.That(options.WantsDescriptorKinds, Is.False);
      Assert.That(options.WantsImageHashes, Is.False, "hashing every image for a CPU column would be absurd");
      Assert.That(options.WantsCpuAffinity, Is.False);
    });
  }

  [Test]
  public void AColumnSetNamedOnTheCommandLineAsksForItThroughTheTerminalListToo() {
    // "@security" keeps its drawn histories for the terminal and drops them for a file, so the two
    // lists really are different — and both have to count as a request.
    var options = Parse("--tui", "--columns", "name,hash.sha256");
    Assert.That(options.WantsImageHashes, Is.True);
    Assert.That(options.TerminalColumns, Does.Contain(ProcessField.ImageSha256));
  }

}
