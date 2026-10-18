using UnityEngine;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Frame rate cap: turns vsync off and sets Application.targetFrameRate. This is what limits desktop
    /// mode; in VR the headset's compositor paces frames at its refresh rate, so set that in the headset's
    /// own software (SteamVR / Oculus / Virtual Desktop) instead. "Game" puts the original values back.
    /// </summary>
    internal sealed class FpsUnlock
    {
        public static readonly int[] Caps = { 0, 60, 90, 120, 144, 240, -1 }; // 0 = game default, -1 = unlimited
        public int Index;

        private bool _saved;
        private int _origTarget, _origVsync;
        private float _next;

        public static string Label(int index) => Caps[Mathf.Clamp(index, 0, Caps.Length - 1)] switch
        {
            0 => "game",
            -1 => "unlimited",
            int fps => fps + " fps",
        };

        public void Update()
        {
            int cap = Caps[Mathf.Clamp(Index, 0, Caps.Length - 1)];
            if (cap == 0)
            {
                if (_saved)
                {
                    Application.targetFrameRate = _origTarget;
                    QualitySettings.vSyncCount = _origVsync;
                    _saved = false;
                }
                return;
            }
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 1f; // re-applied in case the game resets either setting
            if (!_saved)
            {
                _origTarget = Application.targetFrameRate;
                _origVsync = QualitySettings.vSyncCount;
                _saved = true;
            }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = cap;
        }
    }
}
