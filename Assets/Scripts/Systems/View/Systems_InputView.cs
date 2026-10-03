using UnityEngine;
using UnityEngine.InputSystem;

namespace PoFootball.Views
{
    /// <summary>
    /// The keyboard, for the one thing in this game a key is quicker at than a
    /// thumb: TAB opens and closes the DEBUG sheet.
    ///
    /// A thin adapter and nothing else, per .claude/rules/architecture.md — it owns
    /// the action, enables it in OnEnable, disables it in OnDisable, and forwards
    /// the press. Whether a sheet may be shown at all is the status HUD's decision
    /// (Systems_StatusHudView.DiagnosticsAllowed), not this class's.
    ///
    /// THE ACTION IS BUILT IN CODE, NOT FROM AN .inputactions ASSET. One binding
    /// does not justify an asset, a generated class and the import step that keeps
    /// the two in step; the project builds its screens from C# for the same reason
    /// (CLAUDE.md section 3). When there is a second action map this is where the
    /// asset goes.
    ///
    /// Touch and the mouse do not come through here. Taps, drags and pinches on
    /// the field are UI Toolkit pointer events on Systems_HudView's field surface,
    /// because the panel already knows which of them landed on a button and an
    /// InputAction does not.
    ///
    /// Spawned beside the status HUD by <see cref="Systems_StatusHudBootstrap"/>,
    /// so there is exactly one per player-facing scene and none in training.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class Systems_InputView : MonoBehaviour
    {
        private const string DEBUG_TOGGLE_BINDING = "<Keyboard>/tab";

        private InputAction _toggleDebug;
        private Systems_StatusHudView _statusHud;

        private void Awake()
        {
            _toggleDebug = new InputAction(
                nameof(_toggleDebug), InputActionType.Button, DEBUG_TOGGLE_BINDING);
        }

        private void OnEnable()
        {
            _toggleDebug.performed += OnToggleDebug;
            _toggleDebug.Enable();
        }

        private void OnDisable()
        {
            _toggleDebug.performed -= OnToggleDebug;
            _toggleDebug.Disable();
        }

        private void OnDestroy()
        {
            _toggleDebug.Dispose();
        }

        internal void Bind(Systems_StatusHudView statusHud)
        {
            _statusHud = statusHud;
        }

        private void OnToggleDebug(InputAction.CallbackContext context)
        {
            if (_statusHud != null)
            {
                _statusHud.ToggleDebugSheet();
            }
        }
    }
}
