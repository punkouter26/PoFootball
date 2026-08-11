using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// Builds every sound in the game as raw samples at load time.
    ///
    /// WHY SYNTHESISED. docs/ASSETS.md is explicit that the project's only art
    /// dependency is eight generated PNGs, and that Asset Store audio packs cannot
    /// be fetched without a signed-in human clicking "Add to My Assets". Shipping a
    /// game that is silent until someone does that is worse than shipping one that
    /// makes its own noise, and a whistle is a swept square wave — there is not
    /// much to import. Every clip here is a few kilobytes of float array built once
    /// at startup.
    ///
    /// If real recordings are imported later, Systems_AudioView takes serialized
    /// overrides and these become the fallback.
    ///
    /// Nothing here allocates after Build — clips are created once and reused.
    /// </summary>
    /// <remarks>
    /// Five cues, one bed, one UI tick, and the synthesis primitives. A second
    /// file once extended this with crowd layers, cue variants, an organ and a PA;
    /// it was three times the size of this one and none of it told a viewer
    /// anything about the game, so it is gone. Keep additions here to sounds that
    /// carry information.
    /// </remarks>
    public static class Systems_ToneBank
    {
        private const int SAMPLE_RATE = 44100;

        /// <summary>
        /// Referee's whistle: a hard two-tone shriek with a fast attack. Two close
        /// frequencies beating against each other is what gives a real whistle its
        /// warble, and it is the single cue that reads as "football" instantly.
        /// </summary>
        public static AudioClip Whistle()
        {
            return Build("Whistle", 0.45f, (t, duration) =>
            {
                float envelope = Attack(t, 0.01f) * Release(t, duration, 0.12f);
                float warble = 1f + (0.010f * Mathf.Sin(2f * Mathf.PI * 18f * t));

                float first = Square(3150f * warble, t);
                float second = Square(4020f * warble, t);

                return envelope * 0.28f * ((first * 0.6f) + (second * 0.4f));
            });
        }

        /// <summary>
        /// Pad pop on a tackle: a short burst of filtered noise with a low thump
        /// under it. Pitched noise rather than a tone because a collision has no
        /// fundamental — it is broadband, and a sine here would sound like a beep.
        /// </summary>
        public static AudioClip Impact()
        {
            System.Random random = new System.Random(20260810);
            float lowPassState = 0f;

            return Build("Impact", 0.22f, (t, duration) =>
            {
                float envelope = Attack(t, 0.002f) * Release(t, duration, 0.16f);

                float noise = (float)((random.NextDouble() * 2.0) - 1.0);

                // One-pole low pass. Raw white noise reads as a hiss; rolling the
                // top off leaves the thud.
                lowPassState += (noise - lowPassState) * 0.28f;

                float thump = Mathf.Sin(2f * Mathf.PI * 90f * t) * Mathf.Exp(-22f * t);

                return envelope * 0.55f * ((lowPassState * 0.8f) + (thump * 0.6f));
            });
        }

        /// <summary>Rising two-note figure for a first down. Short, bright, unmistakable.</summary>
        public static AudioClip FirstDown()
        {
            return Build("FirstDown", 0.30f, (t, duration) =>
            {
                float envelope = Attack(t, 0.005f) * Release(t, duration, 0.18f);
                float frequency = t < 0.12f ? 660f : 880f;

                return envelope * 0.22f * Triangle(frequency, t);
            });
        }

        /// <summary>
        /// Touchdown fanfare: a three-note arpeggio. The one sound allowed to be
        /// longer than a third of a second.
        /// </summary>
        public static AudioClip Touchdown()
        {
            return Build("Touchdown", 0.9f, (t, duration) =>
            {
                float envelope = Attack(t, 0.005f) * Release(t, duration, 0.35f);

                float frequency = 523.25f;
                if (t > 0.30f) { frequency = 659.25f; }
                if (t > 0.55f) { frequency = 783.99f; }

                float body = Triangle(frequency, t);
                float shimmer = Triangle(frequency * 2f, t) * 0.25f;

                return envelope * 0.26f * (body + shimmer);
            });
        }

        /// <summary>Descending figure for a turnover — the inverse of the first-down cue.</summary>
        public static AudioClip Turnover()
        {
            return Build("Turnover", 0.5f, (t, duration) =>
            {
                float envelope = Attack(t, 0.005f) * Release(t, duration, 0.3f);
                float frequency = Mathf.Lerp(520f, 190f, t / duration);

                return envelope * 0.26f * Triangle(frequency, t);
            });
        }

        /// <summary>
        /// Looping crowd wash: many detuned noise bands. Not a real crowd, but at a
        /// low level under the action it does the job a stadium bed does — filling
        /// the silence so the discrete cues have something to cut through.
        /// </summary>
        public static AudioClip Crowd()
        {
            System.Random random = new System.Random(1926);
            float low = 0f;
            float band = 0f;

            return Build("Crowd", 4f, (t, duration) =>
            {
                float noise = (float)((random.NextDouble() * 2.0) - 1.0);

                low += (noise - low) * 0.05f;
                band += ((noise - low) - band) * 0.35f;

                // Slow swells so the bed breathes instead of sitting flat.
                float swell = 0.75f + (0.25f * Mathf.Sin(2f * Mathf.PI * 0.23f * t))
                    + (0.15f * Mathf.Sin(2f * Mathf.PI * 0.07f * t));

                // Cross-fade the loop into itself so the seam is inaudible.
                float seam = Mathf.Min(1f, Mathf.Min(t, duration - t) / 0.25f);

                return band * 0.5f * swell * seam;
            });
        }

        // --- Synthesis primitives -------------------------------------------

        private delegate float SampleSource(float time, float duration);

        private static AudioClip Build(string clipName, float duration, SampleSource source)
        {
            int sampleCount = Mathf.CeilToInt(SAMPLE_RATE * duration);
            float[] samples = new float[sampleCount];

            for (int index = 0; index < sampleCount; index++)
            {
                float time = index / (float)SAMPLE_RATE;
                samples[index] = Mathf.Clamp(source(time, duration), -1f, 1f);
            }

            AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, SAMPLE_RATE, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static float Attack(float time, float attackSeconds)
        {
            return attackSeconds <= 0f ? 1f : Mathf.Clamp01(time / attackSeconds);
        }

        private static float Release(float time, float duration, float releaseSeconds)
        {
            float remaining = duration - time;
            return releaseSeconds <= 0f ? 1f : Mathf.Clamp01(remaining / releaseSeconds);
        }

        private static float Square(float frequency, float time)
        {
            return Mathf.Sin(2f * Mathf.PI * frequency * time) >= 0f ? 1f : -1f;
        }

        /// <summary>
        /// Triangle rather than sine for the musical cues: it carries a little
        /// harmonic content, so it stays audible over the crowd bed at a volume a
        /// sine would disappear at.
        /// </summary>
        private static float Triangle(float frequency, float time)
        {
            float phase = (frequency * time) % 1f;
            return (4f * Mathf.Abs(phase - 0.5f)) - 1f;
        }
    }
}
