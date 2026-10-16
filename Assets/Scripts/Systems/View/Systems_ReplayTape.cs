using System;
using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// A fixed number of physics ticks of where every body and the ball were:
    /// the recording that the highlights and the scrubber both
    /// play back from.
    ///
    /// STRUCTURE OF ARRAYS, ALLOCATED ONCE. Twenty-two positions and headings a
    /// tick, plus the ball and who was carrying it, written fifty times a second
    /// for the whole game. A frame struct holding a Vector2[] per tick would be a
    /// managed array per frame; parallel float arrays sized capacity x players are
    /// three allocations for the life of the scene, and Append never allocates.
    /// 300 ticks x 22 players is about 80 KB per tape.
    ///
    /// A RING WHILE RECORDING, A LINE ONCE COPIED. The live tape overwrites its
    /// oldest tick when full, so frame 0 is always "the oldest thing still held"
    /// and the caller never sees the physical wrap. CopyNewestFrom lays the tail
    /// of one tape into another starting at slot 0, which is how a highlight is
    /// frozen without a second ring to reason about.
    ///
    /// SAMPLES BETWEEN TICKS. Playback runs on wall-clock time (the scrubber), so a rendered frame
    /// almost never lands on a recorded tick. Positions are lerped and headings go
    /// through Mathf.LerpAngle, so a body turning through 0 degrees does not spin
    /// the long way round for one frame. The carrier is not interpolated — the
    /// ball is in one pair of hands or another.
    /// </summary>
    internal sealed class Systems_ReplayTape
    {
        /// <summary>Written when no player was carrying on that tick.</summary>
        public const int NO_CARRIER = -1;

        private readonly int _capacity;
        private readonly int _playerCount;

        private readonly float[] _playerX;
        private readonly float[] _playerY;
        private readonly float[] _playerAngle;

        private readonly float[] _ballX;
        private readonly float[] _ballY;
        private readonly float[] _ballHeight;

        private readonly sbyte[] _carrier;

        /// <summary>Physical slot of logical frame 0, the oldest frame held.</summary>
        private int _head;

        private int _count;

        public Systems_ReplayTape(int capacity, int playerCount)
        {
            _capacity = Mathf.Max(1, capacity);
            _playerCount = Mathf.Max(0, playerCount);

            int bodies = _capacity * _playerCount;
            _playerX = new float[bodies];
            _playerY = new float[bodies];
            _playerAngle = new float[bodies];

            _ballX = new float[_capacity];
            _ballY = new float[_capacity];
            _ballHeight = new float[_capacity];

            _carrier = new sbyte[_capacity];
        }

        public int Count => _count;

        public int PlayerCount => _playerCount;

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Claims the slot for a new tick and returns it. When the tape is full the
        /// oldest tick is the one overwritten.
        /// </summary>
        public int Append()
        {
            if (_count < _capacity)
            {
                int slot = (_head + _count) % _capacity;
                _count++;
                return slot;
            }

            int oldest = _head;
            _head = (_head + 1) % _capacity;
            return oldest;
        }

        public void WritePlayer(int slot, int player, Vector2 position, float angle)
        {
            int index = (slot * _playerCount) + player;
            _playerX[index] = position.x;
            _playerY[index] = position.y;
            _playerAngle[index] = angle;
        }

        public void WriteBall(int slot, Vector2 position, float height)
        {
            _ballX[slot] = position.x;
            _ballY[slot] = position.y;
            _ballHeight[slot] = height;
        }

        public void WriteCarrier(int slot, int player)
        {
            _carrier[slot] = (sbyte)(player < 0 ? NO_CARRIER : player);
        }

        /// <summary>
        /// Who had the ball at <paramref name="time"/>, in frames. The floor tick,
        /// not the nearest: a catch is drawn on the tick it happened, not half a
        /// tick early.
        /// </summary>
        public int CarrierAt(float time)
        {
            if (_count == 0)
            {
                return NO_CARRIER;
            }

            int frame = Mathf.Clamp((int)time, 0, _count - 1);
            return _carrier[Physical(frame)];
        }

        public void SamplePlayer(float time, int player, out Vector2 position, out float angle)
        {
            if (_count == 0)
            {
                position = Vector2.zero;
                angle = 0f;
                return;
            }

            Bracket(time, out int lower, out int upper, out float blend);

            int from = (Physical(lower) * _playerCount) + player;
            int to = (Physical(upper) * _playerCount) + player;

            position = new Vector2(
                Mathf.Lerp(_playerX[from], _playerX[to], blend),
                Mathf.Lerp(_playerY[from], _playerY[to], blend));

            angle = Mathf.LerpAngle(_playerAngle[from], _playerAngle[to], blend);
        }

        public void SampleBall(float time, out Vector2 position, out float height)
        {
            if (_count == 0)
            {
                position = Vector2.zero;
                height = 0f;
                return;
            }

            Bracket(time, out int lower, out int upper, out float blend);

            int from = Physical(lower);
            int to = Physical(upper);

            position = new Vector2(
                Mathf.Lerp(_ballX[from], _ballX[to], blend),
                Mathf.Lerp(_ballY[from], _ballY[to], blend));

            height = Mathf.Lerp(_ballHeight[from], _ballHeight[to], blend);
        }

        /// <summary>
        /// The ball's plane position on one recorded tick, for framing a highlight.
        /// No interpolation: the caller is walking every tick anyway.
        /// </summary>
        public Vector2 BallAt(int frame)
        {
            int slot = Physical(Mathf.Clamp(frame, 0, Mathf.Max(0, _count - 1)));
            return new Vector2(_ballX[slot], _ballY[slot]);
        }

        /// <summary>
        /// Replaces this tape's contents with the newest <paramref name="frames"/>
        /// ticks of <paramref name="source"/>, laid out from slot 0. Array.Copy per
        /// tick, so nothing is allocated — this runs inside a message handler that
        /// can fire from a collision callback.
        /// </summary>
        public void CopyNewestFrom(Systems_ReplayTape source, int frames)
        {
            Clear();

            if (source == null || source._playerCount != _playerCount)
            {
                return;
            }

            frames = Mathf.Clamp(frames, 0, Mathf.Min(source._count, _capacity));
            int first = source._count - frames;

            for (int frame = 0; frame < frames; frame++)
            {
                int from = source.Physical(first + frame);

                Array.Copy(
                    source._playerX, from * _playerCount,
                    _playerX, frame * _playerCount, _playerCount);
                Array.Copy(
                    source._playerY, from * _playerCount,
                    _playerY, frame * _playerCount, _playerCount);
                Array.Copy(
                    source._playerAngle, from * _playerCount,
                    _playerAngle, frame * _playerCount, _playerCount);

                _ballX[frame] = source._ballX[from];
                _ballY[frame] = source._ballY[from];
                _ballHeight[frame] = source._ballHeight[from];
                _carrier[frame] = source._carrier[from];
            }

            _count = frames;
        }

        private int Physical(int frame)
        {
            return (_head + frame) % _capacity;
        }

        private void Bracket(float time, out int lower, out int upper, out float blend)
        {
            float clamped = Mathf.Clamp(time, 0f, _count - 1);
            lower = (int)clamped;
            upper = Mathf.Min(lower + 1, _count - 1);
            blend = clamped - lower;
        }
    }
}
