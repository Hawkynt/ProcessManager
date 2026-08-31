using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class SpikeInspectionWindowTests {

  [Test]
  public void ExitedProcessesRemainVisibleAsHistoricalEvidence() {
    var old = new ProcessKey(42, 1000ul);
    var live = new ProcessKey(77, 2000ul);
    var contributors = new[] {
      new SpikeContributor(old, "brief-worker", "alice", Rate.Of(91.5)),
      new SpikeContributor(live, "server", "bob", Rate.Of(12.25)),
    };
    var window = new SpikeInspectionWindow(
      SpikeMetric.Cpu,
      DateTime.UtcNow.Ticks,
      4,
      contributors,
      key => key == live
    );

    var text = window.Describe();
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("brief-worker (PID 42)"));
      Assert.That(text, Does.Contain("91.5 % — exited"));
      Assert.That(text, Does.Contain("server (PID 77)"));
      Assert.That(text, Does.Contain("12.25 % — running"));
      Assert.That(text, Does.Contain("Exited processes remain historical evidence"));
    });
  }

  [Test]
  public void IoAndMemoryContributionsAreRatesRatherThanPercentages() {
    var process = new ProcessKey(8, 900ul);
    var contributor = new[] {
      new SpikeContributor(process, "writer", null, Rate.Of(1024 * 1024)),
    };

    var io = new SpikeInspectionWindow(SpikeMetric.Io, 0, 3, contributor, _ => true).Describe();
    var memory = new SpikeInspectionWindow(SpikeMetric.MemoryGrowth, 0, 3, contributor, _ => true).Describe();

    Assert.Multiple(() => {
      Assert.That(io, Does.Contain("/s"));
      Assert.That(memory, Does.Contain("/s"));
      Assert.That(io, Does.Not.Contain("%"));
      Assert.That(memory, Does.Not.Contain("%"));
    });
  }

}
