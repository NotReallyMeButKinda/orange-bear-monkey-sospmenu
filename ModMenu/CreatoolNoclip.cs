using Il2Cpp;
using UnityEngine;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// While the local Creatool is flying, clears the player body's collision mask so SnapRigidbody's sweeps
    /// and depenetration find nothing: you fly through walls, floors and objects. The mask is put back the
    /// moment flying stops (or the option is turned off). The game's own solidity checks elsewhere are untouched.
    /// </summary>
    internal sealed class CreatoolNoclip
    {
        public bool Enabled;
        public bool Noclipping => _body != null;

        private SnapRigidbody _body;
        private int _savedMask;
        private Creatool _tool;
        private float _nextToolSearch;

        public void Update()
        {
            var loco = SnapLocomotion.Instance;
            var body = loco != null ? loco.body : null;
            bool want = Enabled && body != null && Flying();

            if (_body != null && (!want || body == null || _body.Pointer != body.Pointer))
                Restore();
            if (want && _body == null)
            {
                _body = body;
                _savedMask = body.collisionLayers.value;
                SetMask(body, 0);
            }
        }

        private bool Flying()
        {
            if ((_tool == null || !_tool.isActiveAndEnabled) && Time.unscaledTime >= _nextToolSearch)
            {
                _nextToolSearch = Time.unscaledTime + 1f;
                _tool = CreatoolHighlight.LocalTool();
            }
            return _tool != null && _tool.isActiveAndEnabled && _tool.IsFlying;
        }

        public void Restore()
        {
            if (_body != null)
                SetMask(_body, _savedMask);
            _body = null;
        }

        private static void SetMask(SnapRigidbody body, int value)
        {
            LayerMask mask = default;
            mask.value = value;
            body.collisionLayers = mask;
        }
    }
}
