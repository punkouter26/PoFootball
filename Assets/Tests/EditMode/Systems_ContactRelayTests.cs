using MessagePipe;
using NUnit.Framework;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// Systems_ContactRelay feeds a puff of dust and a thud, so nothing in the
    /// simulation depends on it — what these pin is the one decision it makes,
    /// which is what to throw away. Bodies locked in a block re-touch every few
    /// ticks, and if that chatter were published the line would hiss for the
    /// length of every play.
    /// </summary>
    public sealed class Systems_ContactRelayTests
    {
        private sealed class StubPublisher : IPublisher<Systems_ContactMessage>
        {
            public int Count { get; private set; }

            public Systems_ContactMessage Last { get; private set; }

            public void Publish(Systems_ContactMessage message)
            {
                Count++;
                Last = message;
            }
        }

        private sealed class StubGrindPublisher : IPublisher<Systems_GrindMessage>
        {
            public int Count { get; private set; }

            public Systems_GrindMessage Last { get; private set; }

            public void Publish(Systems_GrindMessage message)
            {
                Count++;
                Last = message;
            }
        }

        private StubPublisher _publisher;
        private StubGrindPublisher _grindPublisher;
        private Systems_ContactRelay _relay;

        [SetUp]
        public void SetUp()
        {
            _publisher = new StubPublisher();
            _grindPublisher = new StubGrindPublisher();
            _relay = new Systems_ContactRelay(_publisher, _grindPublisher);
        }

        [Test]
        public void BodiesMovingTogether_AreNotAGrind()
        {
            _relay.ReportGrind(Vector2.zero, Systems_GrindMessage.AUDIBLE_SPEED * 0.5f);

            Assert.That(_grindPublisher.Count, Is.EqualTo(0));
        }

        [Test]
        public void ADrag_IsPublishedAsItWasReported_AndNotAsAContact()
        {
            Vector2 point = new Vector2(-2f, 11f);
            float speed = Systems_GrindMessage.AUDIBLE_SPEED * 3f;

            _relay.ReportGrind(point, speed);

            Assert.That(_grindPublisher.Count, Is.EqualTo(1));
            Assert.That(_grindPublisher.Last.Point, Is.EqualTo(point));
            Assert.That(_grindPublisher.Last.RelativeSpeed, Is.EqualTo(speed));
            Assert.That(_grindPublisher.Last.Strength, Is.InRange(0f, 1f));
            Assert.That(_publisher.Count, Is.EqualTo(0));
        }

        [Test]
        public void ChatterBelowTheFloor_IsNotPublished()
        {
            _relay.Report(
                Vector2.zero, Vector2.up, Systems_ContactMessage.AUDIBLE_IMPULSE * 0.5f);

            Assert.That(_publisher.Count, Is.EqualTo(0));
        }

        [Test]
        public void ARealCollision_IsPublishedAsItWasReported()
        {
            Vector2 point = new Vector2(3f, -7f);
            float impulse = Systems_ContactMessage.FULL_IMPULSE;

            _relay.Report(point, Vector2.right, impulse);

            Assert.That(_publisher.Count, Is.EqualTo(1));
            Assert.That(_publisher.Last.Point, Is.EqualTo(point));
            Assert.That(_publisher.Last.Normal, Is.EqualTo(Vector2.right));
            Assert.That(_publisher.Last.Impulse, Is.EqualTo(impulse));
        }

        [Test]
        public void Strength_RunsFromTheFloorToFull_AndStopsThere()
        {
            float midpoint = (Systems_ContactMessage.AUDIBLE_IMPULSE
                + Systems_ContactMessage.FULL_IMPULSE) * 0.5f;

            Assert.That(StrengthOf(Systems_ContactMessage.AUDIBLE_IMPULSE), Is.EqualTo(0f));
            Assert.That(StrengthOf(midpoint), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(StrengthOf(Systems_ContactMessage.FULL_IMPULSE), Is.EqualTo(1f));

            // Harder than anything measured still has to be a valid volume and a
            // valid particle count.
            Assert.That(
                StrengthOf(Systems_ContactMessage.FULL_IMPULSE * 3f), Is.EqualTo(1f));
        }

        private static float StrengthOf(float impulse)
        {
            return new Systems_ContactMessage(Vector2.zero, Vector2.up, impulse).Strength;
        }
    }
}
