using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Which balloon headlines are notable enough for the picture-in-picture window, settled by
    /// scanning every <c>set_balloon_headline</c> (primitive 41) call in the Complete Collection's
    /// object and global IFFs (1,999 calls).
    /// </summary>
    /// <remarks>
    /// <para>What the data says:</para>
    /// <list type="bullet">
    /// <item>Group 5 (<c>Headline</c>) is not a sim event: all 50 uses are status icons on
    /// objects (trash cans, spoiled food, flies).</item>
    /// <item>Groups 0, 2, 3, 4, 6, 9 and 10 are chat, motive, relationship, progress and magic
    /// balloons, and 8 is route failure: constant background traffic.</item>
    /// <item>Group 7 (<c>Algorithmic</c>, a thought or exclamation balloon showing an object's
    /// icon) is where the alarms are: <c>Fire.iff</c> "Panic" and <c>Snake.iff</c> "Interaction -
    /// Panic" raise an exclamation balloon (type 1) on the sim, with the fire or snake as the
    /// stack object (the icon). Other type-1 uses (waking sleepers, the obsessed fan) are not
    /// alarms.</item>
    /// <item>Burglars, death, and visitor arrival raise no balloon headline at all (visitors only
    /// get ordinary motive balloons), so this seam cannot see them.</item>
    /// </list>
    /// So the notable set is exactly: algorithmic group, exclamation type, a duration that sets
    /// (not clears) it, on a sim, with the icon object being a fire or a snake.
    /// </remarks>
    public static class PictureInPictureEventFilter
    {
        /// <summary><c>VMSetBalloonHeadlineOperandGroup.Algorithmic</c>.</summary>
        public const int AlgorithmicGroup = 7;

        /// <summary>The balloon background type of an exclamation (alarm) balloon.</summary>
        public const int ExclamationType = 1;

        /// <summary>The <c>Fire</c> object's GUID (Objects.far, Fire.iff).</summary>
        public const uint FireGuid = 0x24C95F99;

        /// <summary>The <c>Snake</c> object's GUID (ExpansionPack7.far, Snake.iff).</summary>
        public const uint SnakeGuid = 0x3EC42976;

        /// <summary>Whether a headline with these properties is a picture-in-picture event.</summary>
        /// <param name="group">The headline's balloon group.</param>
        /// <param name="type">The headline's balloon background type.</param>
        /// <param name="duration">Its duration; zero clears a headline.</param>
        /// <param name="targetIsSim">The headline is on a person.</param>
        /// <param name="iconGuid">GUID of the icon object, or null when there is none.</param>
        public static bool IsNotable(int group, int type, int duration, bool targetIsSim, uint? iconGuid)
        {
            return group == AlgorithmicGroup
                && type == ExclamationType
                && duration != 0
                && targetIsSim
                && (iconGuid == FireGuid || iconGuid == SnakeGuid);
        }
    }

    /// <summary>
    /// What decides whether a headline is worth a picture-in-picture window, and how often.
    /// No graphics, no VM: times and ids in, a yes or no out.
    /// </summary>
    /// <remarks>
    /// The window is a spotlight, not a feed of everything, so three things keep it quiet:
    /// <list type="bullet">
    /// <item>only the <c>Headline</c> balloon group qualifies (the one-off event headlines the
    /// behaviours raise; needs, relationships, chat and route failures are far too frequent);</item>
    /// <item>one notification per <see cref="GlobalCooldownSeconds"/> for the whole lot;</item>
    /// <item>one per <see cref="SubjectCooldownSeconds"/> for the same sim.</item>
    /// </list>
    /// A sim the player is already controlling is never a subject: its events are on screen.
    /// </remarks>
    public sealed class PictureInPictureEventGate
    {
        /// <summary>Minimum gap between any two notifications.</summary>
        public const double GlobalCooldownSeconds = 20;

        /// <summary>Minimum gap between two notifications about the same sim.</summary>
        public const double SubjectCooldownSeconds = 90;

        /// <summary>
        /// How long after the window is created events are ignored. Loading a save re-creates the
        /// headlines that were up, and those are old news, not events.
        /// </summary>
        public const double WarmUpSeconds = 3;

        /// <summary>How old a posted event may be before it is dropped instead of shown.</summary>
        public const double StaleSeconds = 1;

        private readonly Dictionary<int, double> lastForSubject = new Dictionary<int, double>();
        private double lastAny = double.NegativeInfinity;

        /// <summary>
        /// Whether an event at <paramref name="now"/> should open the window, and if so records it.
        /// </summary>
        /// <param name="now">Seconds on a monotonic clock.</param>
        /// <param name="subjectId">The sim's object id.</param>
        /// <param name="postedAt">When the event was raised, on the same clock.</param>
        /// <param name="windowAge">Seconds since the window was created.</param>
        /// <param name="isActiveSim">The sim is the one the player is controlling.</param>
        /// <param name="windowBusy">The window is already open on something the player asked for.</param>
        public bool TryAccept(double now, int subjectId, double postedAt, double windowAge, bool isActiveSim, bool windowBusy)
        {
            if (windowAge < WarmUpSeconds) return false;
            if (now - postedAt > StaleSeconds) return false;
            if (isActiveSim || windowBusy) return false;
            if (now - lastAny < GlobalCooldownSeconds) return false;
            if (lastForSubject.TryGetValue(subjectId, out var last) && now - last < SubjectCooldownSeconds) return false;

            lastAny = now;
            lastForSubject[subjectId] = now;
            if (lastForSubject.Count > 64) Prune(now);
            return true;
        }

        private void Prune(double now)
        {
            var old = new List<int>();
            foreach (var pair in lastForSubject)
                if (now - pair.Value >= SubjectCooldownSeconds) old.Add(pair.Key);
            foreach (var id in old) lastForSubject.Remove(id);
        }

        /// <summary>Forgets all history (a new lot).</summary>
        public void Reset()
        {
            lastForSubject.Clear();
            lastAny = double.NegativeInfinity;
        }
    }

    /// <summary>One event waiting for the window to look at it.</summary>
    public readonly struct PictureInPictureEvent
    {
        public PictureInPictureEvent(object subject, int subjectId, string caption, double postedAt)
        {
            Subject = subject;
            SubjectId = subjectId;
            Caption = caption;
            PostedAt = postedAt;
        }

        /// <summary>The VMEntity, carried as an object so this type stays engine-free.</summary>
        public object Subject { get; }
        public int SubjectId { get; }
        public string Caption { get; }
        public double PostedAt { get; }
    }

    /// <summary>
    /// The mailbox between the headline system (which raises events anywhere in the VM tick) and
    /// the window (which decides on its own update). Bounded, so an event storm cannot grow it.
    /// </summary>
    public static class PictureInPictureEventQueue
    {
        /// <summary>Most events held; the oldest is dropped past it.</summary>
        public const int Capacity = 8;

        private static readonly object Gate = new object();
        private static readonly Queue<PictureInPictureEvent> Events = new Queue<PictureInPictureEvent>();

        /// <summary>Seconds on the clock events are stamped with.</summary>
        public static double Now()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Queues an event for the window.</summary>
        public static void Post(object subject, int subjectId, string caption)
        {
            lock (Gate)
            {
                Events.Enqueue(new PictureInPictureEvent(subject, subjectId, caption, Now()));
                while (Events.Count > Capacity) Events.Dequeue();
            }
        }

        /// <summary>Takes everything queued, oldest first.</summary>
        public static List<PictureInPictureEvent> Drain()
        {
            lock (Gate)
            {
                var all = new List<PictureInPictureEvent>(Events);
                Events.Clear();
                return all;
            }
        }

        /// <summary>Empties the queue without looking at it.</summary>
        public static void Clear()
        {
            lock (Gate) { Events.Clear(); }
        }
    }
}
