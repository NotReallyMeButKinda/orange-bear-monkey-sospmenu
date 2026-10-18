using Il2Cpp;
using UnityEngine;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Makes you bigger or smaller by scaling your XR rig: view height, reach, arm span and the body capsule
    /// all scale together, and walking speed with them. Your tracked head and hands sync as usual, so
    /// everyone sees them at the scaled height; mod users also see your avatar's head, body and hands
    /// resized (the scale rides along with the cosmetics). Back to 1x restores the rig exactly.
    /// </summary>
    internal sealed class PlayerScale
    {
        public static readonly float[] Presets = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 5f };
        public const int DefaultIndex = 3;

        public float Wanted = 1f;
        public float Applied { get; private set; } = 1f;

        private Transform _origin;
        private Vector3 _baseScale;
        private SnapMove _move;
        private float _baseSpeed;

        public static string Label(float s) => s.ToString("0.##") + "x";

        public void Update()
        {
            var loco = SnapLocomotion.Instance;
            var origin = loco != null ? loco.Origin : null;
            if (origin != _origin)
            {
                // new rig (respawn / space change): start from its own scale
                _origin = origin;
                _baseScale = origin != null ? origin.localScale : Vector3.one;
                _move = loco != null ? loco.Move : null;
                _baseSpeed = _move != null ? _move.moveSpeed : 0f;
                Applied = 1f;
            }
            if (origin == null || Mathf.Approximately(Wanted, Applied))
                return;
            origin.localScale = _baseScale * Wanted;
            if (_move != null && _baseSpeed > 0f)
                _move.moveSpeed = _baseSpeed * Mathf.Sqrt(Wanted); // big strides, but not 5x speed
            Applied = Wanted;
        }

        public void Reset()
        {
            Wanted = 1f;
            Update();
        }
    }
}
