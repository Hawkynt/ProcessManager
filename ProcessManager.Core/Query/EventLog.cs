using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What kind of thing happened, for the column that sorts a timeline by category (PRD §63).
/// </summary>
public enum EventCategory : byte {

  /// <summary>Nobody said. Shown as such rather than folded into the mildest category.</summary>
  Unclassified = 0,

  /// <summary>A process appeared or went.</summary>
  Lifecycle,

  /// <summary>Something crossed a threshold somebody set.</summary>
  Threshold,

  /// <summary>A unit changed state.</summary>
  Service,

  /// <summary>Somebody using this program did something to the machine.</summary>
  UserAction,

  /// <summary>This program itself gained or used privilege.</summary>
  Privilege,

}

/// <summary>
/// One entry in the timeline (PRD §63).
/// </summary>
/// <param name="UtcTicks">When, on the wall clock — a timeline is read against the day.</param>
/// <param name="Category">What kind of thing it was.</param>
/// <param name="Text">The whole sentence, the same one in every front-end.</param>
/// <param name="Pid">
/// Which process it was about, or nought where it was about none. Nought rather than a nullable so
/// the record stays a value type the ring can hold without allocating, and because a timeline entry
/// about the machine rather than about a process is an ordinary thing.
/// </param>
public readonly record struct TimelineEvent(long UtcTicks, EventCategory Category, string Text, int Pid);

/// <summary>
/// What has happened while this program has been watching (PRD §63).
/// </summary>
/// <remarks>
/// <para>
/// A table says what is true now. It cannot say that the process using the processor a minute ago
/// has since exited, which is the question somebody who looked away and looked back is actually
/// asking — and the one a monitor is worst at answering.
/// </para>
/// <para>
/// <b>In memory and bounded, and nothing is written to disk.</b> That is the difference between this
/// and §44's usage record, which is off unless asked for because it outlives the session: a ring
/// that dies with the program records nothing about anybody after they close it. The bound is a
/// count rather than a duration, because a machine that starts a thousand processes a minute and one
/// that starts none both have to stay inside the same memory.
/// </para>
/// <para>
/// Fed from what the sampler already computed. Nothing here reads a file or takes a reading of its
/// own — a timeline that cost a measurement would be a monitor watching itself.
/// </para>
/// </remarks>
public sealed class EventLog {

  private readonly TimelineEvent[] _events;
  private int _next;
  private int _count;

  /// <summary>How many entries are kept before the oldest is dropped.</summary>
  public int Capacity => this._events.Length;

  /// <summary>How many are in it.</summary>
  public int Count => this._count;

  /// <summary>How many have ever been recorded, including those the ring has dropped.</summary>
  /// <remarks>
  /// Kept because "showing 500 of 40,000" is a different thing to tell somebody than "showing 500",
  /// and the second reads as though that is all there was.
  /// </remarks>
  public long Total { get; private set; }

  public EventLog(int capacity = 500) {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
    this._events = new TimelineEvent[capacity];
  }

  /// <summary>The entries, oldest first.</summary>
  public IReadOnlyList<TimelineEvent> Entries {
    get {
      var entries = new List<TimelineEvent>(this._count);
      var first = this._count == this._events.Length ? this._next : 0;
      for (var i = 0; i < this._count; ++i)
        entries.Add(this._events[(first + i) % this._events.Length]);

      return entries;
    }
  }

  /// <summary>Puts one entry in.</summary>
  public void Record(long utcTicks, EventCategory category, string text, int pid = 0) {
    if (text is not { Length: > 0 })
      return;

    this._events[this._next] = new(utcTicks, category, text, pid);
    this._next = (this._next + 1) % this._events.Length;
    if (this._count < this._events.Length)
      ++this._count;

    ++this.Total;
  }

  /// <summary>Forgets everything, which is what somebody asking to clear it means.</summary>
  public void Clear() {
    Array.Clear(this._events);
    this._next = 0;
    this._count = 0;
    this.Total = 0;
  }

  /// <summary>
  /// Records what this sample's delta says happened.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Started and ended come straight off the delta, which computed them for the table's own colours,
  /// so this costs a walk of two short lists and no reading at all.
  /// </para>
  /// <para>
  /// The first sample records nothing. Every process on the machine is new to a program that has just
  /// started looking, and a timeline whose first line is four hundred processes starting says nothing
  /// about the machine and hides everything after it.
  /// </para>
  /// </remarks>
  public void Add(SystemSnapshot snapshot, SnapshotDelta delta, bool firstSample, long nowUtcTicks) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);
    if (firstSample)
      return;

    // IsNew rather than a list of indices, because that is what the delta computes for the table's
    // own "started since the last refresh" colour — one flag per row, already there.
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (!delta.IsNew(i))
        continue;

      this.Record(nowUtcTicks, EventCategory.Lifecycle, $"{processes[i].Name} started", processes[i].Pid);
    }

    // The exited carry an identity and not a name: the record they belonged to went with them, which
    // is the whole difficulty of describing something that has ended. The pid is what there is.
    foreach (var gone in delta.Exited)
      this.Record(nowUtcTicks, EventCategory.Lifecycle, $"PID {gone.Pid} ended", gone.Pid);
  }

  /// <summary>
  /// Records what somebody asked to be told about, so the timeline and the alerts agree (PRD §64).
  /// </summary>
  /// <remarks>
  /// The same sentence, because two wordings for one event is two things for a reader to reconcile.
  /// A notification is the interruption and this is the record of it.
  /// </remarks>
  public void Add(IReadOnlyList<Notification> notifications, long nowUtcTicks) {
    ArgumentNullException.ThrowIfNull(notifications);
    foreach (var notification in notifications)
      this.Record(nowUtcTicks, CategoryOf(notification.Kind), notification.Text);
  }

  /// <summary>
  /// Records something the person using this program did to the machine (PRD §63).
  /// </summary>
  /// <remarks>
  /// Worth its own category because it is the one kind of entry the machine did not cause. Somebody
  /// reading a timeline after an incident needs to be able to tell what the machine did from what
  /// they did to it, and a line that does not distinguish them is a line that will be misread under
  /// exactly the pressure the timeline exists for.
  /// </remarks>
  public void RecordAction(long utcTicks, string what, int pid = 0)
    => this.Record(utcTicks, EventCategory.UserAction, what, pid);

  private static EventCategory CategoryOf(NotificationKind kind) => kind switch {
    NotificationKind.ProcessStarted => EventCategory.Lifecycle,
    NotificationKind.ProcessEnded => EventCategory.Lifecycle,
    NotificationKind.NamedProcessStarted => EventCategory.Lifecycle,
    NotificationKind.CpuAboveThreshold => EventCategory.Threshold,
    NotificationKind.MemoryAboveThreshold => EventCategory.Threshold,
    NotificationKind.DiskAboveThreshold => EventCategory.Threshold,
    NotificationKind.ServiceStopped => EventCategory.Service,
    // Including Unclassified. A kind nobody sorted shows as unsorted rather than as the mildest
    // category there is, for the reason §72.3 gives about defaults.
    _ => EventCategory.Unclassified,
  };

  /// <summary>The word for a category, for the column and for a filter.</summary>
  public static string Describe(EventCategory category) => category switch {
    EventCategory.Lifecycle => "started or ended",
    EventCategory.Threshold => "over a threshold",
    EventCategory.Service => "a unit changed",
    EventCategory.UserAction => "you did it",
    EventCategory.Privilege => "privilege",
    _ => "uncategorised",
  };

}
