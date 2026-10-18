using UnityEngine;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// A render layer the headset camera skips but mirror cameras draw: your own hat sits on it so you
    /// see it in mirrors (the game's and the camera mirror) without it blocking your view.
    /// The highest layer with no name in the project is used.
    /// </summary>
    internal static class MirrorOnly
    {
        private static int _layer = -1;

        public static int Layer
        {
            get
            {
                if (_layer < 0)
                {
                    _layer = 31;
                    for (int i = 31; i >= 8; i--)
                    {
                        if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                        {
                            _layer = i;
                            break;
                        }
                    }
                }
                return _layer;
            }
        }

        /// <summary>Keeps the layer out of the main camera (re-checked, the game may rebuild its rig).</summary>
        public static void HideFromMainCamera()
        {
            var cam = Camera.main;
            if (cam == null)
                return;
            int bit = 1 << Layer;
            if ((cam.cullingMask & bit) != 0)
                cam.cullingMask &= ~bit;
        }
    }
}
