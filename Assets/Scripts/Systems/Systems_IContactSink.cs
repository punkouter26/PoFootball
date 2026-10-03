using MessagePipe;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Where a player reports a collision with an opponent that the referee has
    /// no interest in.
    ///
    /// THE SAME SEAM AS Systems_IIntentSink, FOR THE SAME REASON. The collision
    /// callback lives on Agent_FootballPlayer, the things that draw and sound it
    /// live in PoFootball.Views, and those two assemblies cannot see each other.
    /// It is also bound the same way — on the presentation budget — so a training
    /// run gets the null object and a block costs it one empty interface call.
    ///
    /// NOT THROUGH Systems_Referee, which is where the carrier's contacts go. The
    /// referee rules on those; it has nothing to rule on here, and routing
    /// decoration through the class that decides when a play ends would put a
    /// presentation concern inside every referee test.
    /// </summary>
    public interface Systems_IContactSink
    {
        /// <summary>
        /// Called from OnCollisionEnter2D, once per colliding pair. Implementations
        /// must not allocate.
        /// </summary>
        void Report(Vector2 point, Vector2 normal, float impulse);

        /// <summary>
        /// Called from OnCollisionStay2D, on every tick a defender stays in
        /// contact with the carrier. The referee is told about the same contact
        /// separately and rules on it; this is only so it can be heard.
        /// Implementations must not allocate.
        /// </summary>
        void ReportGrind(Vector2 point, float relativeSpeed);
    }

    /// <summary>
    /// The presentation binding: turns a reported contact into a
    /// <see cref="Systems_ContactMessage"/>, if it was hard enough to be worth one.
    ///
    /// THE FLOOR IS APPLIED HERE, ONCE, rather than by each subscriber. Bodies in
    /// a sustained block chatter — separate, re-touch, separate — and those
    /// re-touches outnumber real collisions several to one. Dropping them before
    /// they are published means no view has to remember to.
    /// </summary>
    public sealed class Systems_ContactRelay : Systems_IContactSink
    {
        private readonly IPublisher<Systems_ContactMessage> _publisher;
        private readonly IPublisher<Systems_GrindMessage> _grindPublisher;

        public Systems_ContactRelay(
            IPublisher<Systems_ContactMessage> publisher,
            IPublisher<Systems_GrindMessage> grindPublisher)
        {
            _publisher = publisher;
            _grindPublisher = grindPublisher;
        }

        public void Report(Vector2 point, Vector2 normal, float impulse)
        {
            if (impulse < Systems_ContactMessage.AUDIBLE_IMPULSE)
            {
                return;
            }

            _publisher.Publish(new Systems_ContactMessage(point, normal, impulse));
        }

        /// <summary>
        /// The same floor, for the same reason: two bodies moving as one are in
        /// contact on every tick and scraping on none of them.
        /// </summary>
        public void ReportGrind(Vector2 point, float relativeSpeed)
        {
            if (relativeSpeed < Systems_GrindMessage.AUDIBLE_SPEED)
            {
                return;
            }

            _grindPublisher.Publish(new Systems_GrindMessage(point, relativeSpeed));
        }
    }

    /// <summary>
    /// The training and headless binding: throw it away. A null object rather
    /// than a null check in the agent — see Systems_NullIntentSink.
    /// </summary>
    public sealed class Systems_NullContactSink : Systems_IContactSink
    {
        public void Report(Vector2 point, Vector2 normal, float impulse)
        {
        }

        public void ReportGrind(Vector2 point, float relativeSpeed)
        {
        }
    }
}
