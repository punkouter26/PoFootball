using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Moves the ball sprite to wherever the ball actually is.
    ///
    /// Before this existed the sprite sat wherever the scene author left it and
    /// never moved, so the yellow dot on screen had nothing to do with possession.
    /// It was also a live Rigidbody2D, making it a loose obstacle that 22 agents
    /// could shove around; the physics components are removed from the prefab.
    ///
    /// Reads the model directly in LateUpdate rather than subscribing, because the
    /// ball moves every tick and a per-change callback would allocate.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Systems_BallView : MonoBehaviour, Systems_IInjectableView
    {
        [SerializeField] private float _zOffset = -1f;

        private Systems_BallModel _ball;
        private Transform _transform;
        private SpriteRenderer _renderer;

        [Inject]
        public void Construct(Systems_BallModel ball)
        {
            _ball = ball;
        }

        private void Awake()
        {
            _transform = transform;
            _renderer = GetComponent<SpriteRenderer>();
        }

        private void LateUpdate()
        {
            if (_ball == null)
            {
                return;
            }

            Vector2 position = _ball.Position;
            _transform.position = new Vector3(position.x, position.y, _zOffset);

            // A held ball is redundant with the white carrier highlight, so only
            // show the sprite when the ball is genuinely separate from a player.
            if (_renderer != null)
            {
                _renderer.enabled = _ball.IsInFlight;
            }
        }
    }
}
