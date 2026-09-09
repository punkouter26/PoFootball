using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Where the game is heard from.
    ///
    /// THE PROBLEM THIS SOLVES, IN Systems_AudioView's OWN WORDS. That class builds
    /// every voice with spatialBlend = 0 and explains why: "Real spatialisation
    /// would attenuate by distance from the listener, and the listener is a camera
    /// forty metres up — every sound on the field would be equally far away and
    /// equally quiet." The diagnosis is exactly right and the conclusion does not
    /// follow. The fault is not that spatial audio is wrong for this game, it is
    /// that the listener was in the wrong place. Unity puts an AudioListener on the
    /// main camera by default and nothing here ever moved it, so the microphone was
    /// wherever the broadcast camera happened to be — which for a top-down 2D
    /// football game is a point in the sky that is nearly equidistant from all
    /// twenty-two players at all times.
    ///
    /// A real broadcast does not mic the camera. It mics the field: a shotgun on
    /// the sideline and a parabolic following the ball. This component is the
    /// second one. The listener sits a short distance above the play and TRACKS THE
    /// BALL, so a tackle at the far numbers is genuinely further from the
    /// microphone than one at the near hash, and the difference is audible because
    /// the distances involved are now tens of metres rather than a constant forty.
    ///
    /// HEIGHT IS THE WHOLE TUNING. At zero the mic would sit in the pile and a
    /// tackle two metres away would be deafening next to one twenty metres away —
    /// a 10:1 ratio that reads as a mixing fault. The height sets a floor under the
    /// distance to every sound, so the ratio across the width of the field is
    /// closer to 3:1: clearly positional, never violent. This is the same reason a
    /// boom operator holds the mic above the action rather than in it.
    ///
    /// IT TAKES OVER THE EXISTING LISTENER RATHER THAN ADDING ONE. Two enabled
    /// AudioListeners in a scene is a Unity warning and undefined behaviour — the
    /// engine picks one and does not say which — so any listener already in the
    /// scene is disabled before this one is created.
    ///
    /// Off in training and headless like every other view: --no-graphics has no
    /// audio device, and a trainer has nothing to listen with.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    [DisallowMultipleComponent]
    public sealed class Systems_BroadcastMicView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// Metres above the plane of play. Roughly a first down's worth of
        /// distance, which is the scale that makes cross-field position audible
        /// without making near sounds overwhelming — see the class note.
        /// </summary>
        private const float MIC_HEIGHT = 9f;

        /// <summary>
        /// How fast the mic chases the ball, per second. Slower than the camera on
        /// purpose: a microphone that snapped to an interception across the field
        /// would swing the whole stereo image in one frame, which is a lurch no
        /// real broadcast produces.
        /// </summary>
        private const float FOLLOW_RATE = 3.5f;

        private Systems_BallModel _ball;
        private Systems_PresentationBudget _budget;

        private Transform _transform;

        [Inject]
        public void Construct(Systems_BallModel ball, Systems_PresentationBudget budget)
        {
            _ball = ball;
            _budget = budget;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled || _ball == null)
            {
                enabled = false;
                return;
            }

            DisableExistingListeners();

            GameObject mic = new GameObject("BroadcastMic");
            mic.transform.SetParent(transform, false);
            mic.AddComponent<AudioListener>();

            _transform = mic.transform;
            _transform.position = MicTarget();
        }

        /// <summary>
        /// Silences every listener already in the scene — in practice the one Unity
        /// puts on the main camera, which is the listener this component exists to
        /// replace.
        /// </summary>
        private static void DisableExistingListeners()
        {
            AudioListener[] listeners =
                FindObjectsByType<AudioListener>(FindObjectsInactive.Include);

            for (int index = 0; index < listeners.Length; index++)
            {
                listeners[index].enabled = false;
            }
        }

        /// <summary>
        /// LateUpdate, and for the same reason Systems_BroadcastCameraView uses it:
        /// the ball model is written by the physics step, so a mic moved in Update
        /// is one tick behind the position every sound this frame was emitted at.
        /// </summary>
        private void LateUpdate()
        {
            if (_transform == null)
            {
                return;
            }

            _transform.position = Vector3.Lerp(
                _transform.position, MicTarget(), FOLLOW_RATE * Time.deltaTime);
        }

        /// <summary>
        /// Above the ball, on the axis the camera looks down. Z is negative because
        /// this is a 2D scene viewed down -Z, so "toward the viewer" is -Z and the
        /// mic has to be on the same side of the field as the camera or every
        /// sound would be behind the listener.
        /// </summary>
        private Vector3 MicTarget()
        {
            Vector2 ball = _ball.Position;
            return new Vector3(ball.x, ball.y, -MIC_HEIGHT);
        }
    }
}
