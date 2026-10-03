using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// The one additive material every effect view draws with, and the one place
    /// that knows what it is called.
    ///
    /// WHY THIS IS A CLASS AND NOT FIVE CONSTANTS. The ball trail, the drive trail,
    /// the impact burst, the intent overlay and the turf scuff each carried their
    /// own copy of the resource name and their own copy of the load-and-warn block
    /// under it. Renaming the material meant finding all five, and missing one is
    /// silent: Resources.Load returns null, the view disables itself with a
    /// warning, and that effect simply stops being drawn.
    ///
    /// Hands back the SHARED asset. A caller that needs to change a property makes
    /// its own instance and owns destroying it, as Systems_BallTrailView does.
    /// </summary>
    internal static class Systems_ParticleMaterial
    {
        private const string RESOURCE = "M_PoFootballParticle";

        /// <summary>
        /// False, with a warning naming the view and what it costs the picture,
        /// when the material is missing. The caller disables itself.
        /// </summary>
        internal static bool TryLoad(string viewName, string consequence, out Material material)
        {
            material = Resources.Load<Material>(RESOURCE);

            if (material != null)
            {
                return true;
            }

            Debug.LogWarning($"{viewName}: no material at Resources/{RESOURCE}. {consequence}");
            return false;
        }
    }
}
