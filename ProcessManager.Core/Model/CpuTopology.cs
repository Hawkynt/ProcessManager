namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What kind of core a logical processor sits on (PRD §46).
/// </summary>
/// <remarks>
/// Hybrid parts — Intel from Alder Lake, and every ARM big.LITTLE design — put cores of two very
/// different speeds in one package. A meter that treats them alike says a machine is half busy when
/// what it means is that the fast half is idle and the slow half is saturated, which are not the
/// same machine at all.
/// </remarks>
public enum CoreKind {

  /// <summary>Not a hybrid part, or nothing this machine exposes says which.</summary>
  Unknown,

  /// <summary>A performance core: Intel's <c>cpu_core</c>, ARM's big.</summary>
  Performance,

  /// <summary>An efficiency core: Intel's <c>cpu_atom</c>, ARM's LITTLE.</summary>
  Efficiency,

}

/// <summary>One logical processor and where it sits.</summary>
/// <param name="Logical">Its number, which is the index every per-core counter is keyed by.</param>
/// <param name="Package">
/// Which socket it is in, or -1 where the machine does not say — a container usually, or an
/// architecture that does not publish topology.
/// </param>
/// <param name="Core">
/// Which physical core within that socket. Two logical processors sharing one are SMT siblings, and
/// saturating both does not give twice the work of saturating one.
/// </param>
/// <param name="Kind">Performance, efficiency, or unknown.</param>
/// <param name="Node">
/// Which NUMA node its memory is local to, or -1 where the kernel publishes no node map — which is
/// every machine built without <c>CONFIG_NUMA</c> and every container that hides it.
/// </param>
public readonly record struct CoreDescriptor(int Logical, int Package, int Core, CoreKind Kind, int Node = -1);

/// <summary>
/// How the machine's logical processors are arranged (PRD §46).
/// </summary>
/// <remarks>
/// Read once: a machine does not repartition its cores while a program watches it. Offline cores are
/// not listed at all, so a caller must key by <see cref="CoreDescriptor.Logical"/> and never by
/// position in this list.
/// </remarks>
public sealed record CpuTopology(IReadOnlyList<CoreDescriptor> Cores) {

  public static readonly CpuTopology Empty = new([]);

  /// <summary>Whether this machine has cores of more than one kind.</summary>
  public bool IsHybrid {
    get {
      var seen = CoreKind.Unknown;
      foreach (var core in this.Cores) {
        if (core.Kind == CoreKind.Unknown)
          continue;

        if (seen == CoreKind.Unknown)
          seen = core.Kind;
        else if (seen != core.Kind)
          return true;
      }

      return false;
    }
  }

  /// <summary>The sockets, in order, or empty where the machine does not say which is which.</summary>
  public IReadOnlyList<int> Packages {
    get {
      var packages = new List<int>();
      foreach (var core in this.Cores)
        if (core.Package >= 0 && !packages.Contains(core.Package))
          packages.Add(core.Package);

      packages.Sort();
      return packages;
    }
  }

  /// <summary>
  /// The NUMA nodes that have processors on them, in order (PRD §46).
  /// </summary>
  /// <remarks>
  /// Empty on a machine that publishes no node map, and deliberately not "one node containing
  /// everything" in that case: a single-node machine and a machine that will not say are different
  /// answers, and only one of them is worth offering a per-node view of (PRD §5.3).
  /// <para>
  /// A node with memory and no processors — a CXL expander, a persistent-memory node — is not here,
  /// because this is the list of things whose utilisation can be plotted.
  /// </para>
  /// </remarks>
  public IReadOnlyList<int> Nodes {
    get {
      var nodes = new List<int>();
      foreach (var core in this.Cores)
        if (core.Node >= 0 && !nodes.Contains(core.Node))
          nodes.Add(core.Node);

      nodes.Sort();
      return nodes;
    }
  }

  /// <summary>The logical processors on one NUMA node, in the same order <see cref="Of"/> uses.</summary>
  public IReadOnlyList<CoreDescriptor> OnNode(int node) {
    var members = new List<CoreDescriptor>();
    foreach (var core in this.Cores)
      if (core.Node == node)
        members.Add(core);

    members.Sort(Order);
    return members;
  }

  /// <summary>
  /// The logical processors of one socket, performance cores first and efficiency cores after, each
  /// group in core order with SMT siblings adjacent.
  /// </summary>
  /// <remarks>
  /// The order is the point: a heat map is read as a picture, and a picture whose cells are in the
  /// kernel's enumeration order interleaves the two kinds on some machines and separates them on
  /// others. Sorting here means the same silicon always looks the same.
  /// </remarks>
  public IReadOnlyList<CoreDescriptor> Of(int package) {
    var members = new List<CoreDescriptor>();
    foreach (var core in this.Cores)
      if (core.Package == package)
        members.Add(core);

    members.Sort(Order);
    return members;
  }

  /// <summary>Fastest kind first, then by physical core, so SMT siblings land next to each other.</summary>
  private static int Order(CoreDescriptor a, CoreDescriptor b) {
    var kind = Rank(a.Kind).CompareTo(Rank(b.Kind));
    if (kind != 0)
      return kind;

    var core = a.Core.CompareTo(b.Core);
    return core != 0 ? core : a.Logical.CompareTo(b.Logical);
  }

  /// <summary>Performance before efficiency before unknown, which is fastest-first.</summary>
  private static int Rank(CoreKind kind) => kind switch {
    CoreKind.Performance => 0,
    CoreKind.Efficiency => 1,
    _ => 2,
  };

}
