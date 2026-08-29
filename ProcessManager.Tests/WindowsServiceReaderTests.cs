using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The service control manager, which nothing here opened until now (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// <c>WindowsProbe.GetServices</c> answered with an empty list, so the services view existed on
/// Windows and had no rows in it — a view that looks like a machine with no services rather than
/// like a feature nobody wrote.
/// </para>
/// <para>
/// Runs on the Windows leg only, because there is no fixture for a service control manager and
/// pretending there is would test a mock rather than the API. The text half of the reader is in Core
/// and is covered on every leg by <see cref="ServiceImagePathTests"/>, which is why it was moved
/// there.
/// </para>
/// </remarks>
[TestFixture]
[Platform("Win", Reason = "opens the service control manager")]
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceReaderTests {

  private static IReadOnlyList<ServiceRecord> Services() {
    using var probe = new WindowsProbe(new());
    return probe.GetServices();
  }

  /// <summary>
  /// A Windows machine has services. This is the assertion the empty list would have failed, and the
  /// whole reason the box was open.
  /// </summary>
  [Test]
  public void TheMachineHasServices() {
    var services = Services();

    Assert.That(services, Is.Not.Empty, "no Windows machine has no services");
    TestContext.Out.WriteLine($"{services.Count} services and drivers");
  }

  /// <summary>
  /// Every row is named, because a nameless row cannot be commanded, sorted or looked up — and a
  /// blank in the first column is the shape a marshalling mistake takes.
  /// </summary>
  [Test]
  public void EveryServiceIsNamed() {
    Assert.Multiple(() => {
      foreach (var service in Services())
        Assert.That(service.Name, Is.Not.Null.And.Not.Empty);
    });
  }

  /// <summary>
  /// Something is running, and everything running has a pid. A machine where the state came through
  /// and the process id did not would pass the test above and be useless.
  /// </summary>
  [Test]
  public void WhatIsRunningSaysWhichProcessItIs() {
    var running = 0;
    var withPid = 0;
    foreach (var service in Services())
      if (service.State == ServiceState.Running) {
        ++running;
        if (service.MainPid > 0)
          ++withPid;
      }

    Assert.That(running, Is.GreaterThan(0), "a Windows machine always has services running");

    // Not every one: a driver reports Running with no process, because it has none. So the assertion
    // is that the field is populated at all rather than that it is populated everywhere — the second
    // would be false about the machine rather than about the reader.
    Assert.That(withPid, Is.GreaterThan(0), "nothing running reported a process id");
  }

  /// <summary>
  /// The two things a configuration query is for. Not every service answers both — the query needs a
  /// handle and some services decline one to an ordinary account — so this asserts that the pass runs
  /// rather than that it always succeeds, which is the distinction §72.3 is about.
  /// </summary>
  [Test]
  public void TheConfigurationIsReadForAtLeastSomeOfThem() {
    var withCommand = 0;
    var withAccount = 0;
    var withType = 0;
    foreach (var service in Services()) {
      if (service.Command is { Length: > 0 })
        ++withCommand;
      if (service.Account is { Length: > 0 })
        ++withAccount;
      if (service.Type is { Length: > 0 } and not "unknown")
        ++withType;
    }

    Assert.Multiple(() => {
      Assert.That(withCommand, Is.GreaterThan(0), "no service gave up its image path");
      Assert.That(withAccount, Is.GreaterThan(0), "no service gave up its account");
      Assert.That(withType, Is.GreaterThan(0), "no service was classified");
    });
  }

  /// <summary>
  /// The enabled flag distinguishes something, rather than being the same value on every row. A
  /// reader that got the start type wrong would most likely report one answer for the whole machine,
  /// which every assertion above would pass.
  /// </summary>
  [Test]
  public void SomeStartOnTheirOwnAndSomeDoNot() {
    var on = 0;
    var off = 0;
    foreach (var service in Services())
      if (service.Enabled is true)
        ++on;
      else if (service.Enabled is false)
        ++off;

    Assert.That(on, Is.GreaterThan(0), "nothing on this machine starts by itself");
    Assert.That(off, Is.GreaterThan(0), "everything on this machine starts by itself");
  }

  /// <summary>
  /// The name and the key agree. The path is not a unit file here — a Windows service is configured
  /// in the registry — and a row whose key named a different service than its name would be a row
  /// that leads somewhere wrong.
  /// </summary>
  [Test]
  public void TheRegistryKeyNamesTheSameService() {
    Assert.Multiple(() => {
      foreach (var service in Services())
        Assert.That(service.Path, Does.EndWith("\\" + service.Name));
    });
  }

}
