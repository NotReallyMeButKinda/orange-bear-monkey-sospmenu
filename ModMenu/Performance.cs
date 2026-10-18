using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Frame-rate help for spaces with lots of objects, purely local (nobody else is affected and nothing
    /// is saved): renderers of space objects whose bounds are further than the draw distance stop drawing,
    /// and objects past a nearer distance stop casting shadows, which is where big builds spend most of
    /// their GPU time. A slice of the renderers is checked each frame, so the cost stays flat. "Auto"
    /// only kicks in once a space has more than AutoThreshold objects. Everything is restored when off.
    /// </summary>
    internal sealed class Performance
    {
        public enum Mode { Off = 0, Auto = 1, On = 2 }
        public const int ModeCount = 3;
        public const int AutoThreshold = 300;
        public static readonly float[] Distances = { 40f, 60f, 100f, 150f };
        private const float ShadowFraction = 0.3f; // shadows only within this share of the draw distance
        private const int PerFrame = 400;
        private const float RescanInterval = 3f;

        public Mode Setting;
        public float DrawDistance = 60f;
        public bool Active { get; private set; }
        public int Hidden { get; private set; }
        public int Objects { get; private set; }

        private sealed class Tracked
        {
            public Renderer Renderer;
            public ShadowCastingMode Shadows;
            public bool HiddenByUs, ShadowsOffByUs;
        }

        private readonly List<Tracked> _tracked = new();
        private readonly HashSet<System.IntPtr> _known = new();
        private Transform _parent;
        private int _cursor;
        private float _nextScan;

        public static string Label(Mode m) => m switch
        {
            Mode.Auto => $"auto (over {AutoThreshold} objects)",
            Mode.On => "on",
            _ => "off",
        };

        public void Update()
        {
            var sm = SpaceIO.FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            if (parent != _parent)
            {
                RestoreAll();
                _parent = parent;
                _nextScan = 0f;
            }
            Objects = parent != null ? parent.childCount : 0;
            bool want = parent != null && (Setting == Mode.On || (Setting == Mode.Auto && Objects > AutoThreshold));
            if (!want)
            {
                if (Active)
                    RestoreAll();
                return;
            }
            Active = true;
            var cam = Camera.main;
            if (cam == null)
                return;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Scan(parent);
            }
            if (_tracked.Count == 0)
                return;

            Vector3 eye = cam.transform.position;
            float draw = DrawDistance * DrawDistance;
            float shadow = DrawDistance * ShadowFraction;
            shadow *= shadow;
            int n = Mathf.Min(PerFrame, _tracked.Count);
            for (int k = 0; k < n; k++)
            {
                if (_cursor >= _tracked.Count)
                    _cursor = 0;
                var t = _tracked[_cursor];
                if (t.Renderer == null)
                {
                    _tracked.RemoveAt(_cursor);
                    continue;
                }
                _cursor++;
                float d = SqrDistance(t.Renderer.bounds, eye); // 0 inside, so floors you stand on stay
                bool hide = d > draw;
                if (hide != t.HiddenByUs)
                {
                    if (hide && !t.Renderer.enabled)
                        continue; // the game hid it; leave it to the game
                    t.Renderer.enabled = !hide;
                    t.HiddenByUs = hide;
                    Hidden += hide ? 1 : -1;
                }
                bool noShadow = !hide && d > shadow && t.Shadows != ShadowCastingMode.Off;
                if (noShadow != t.ShadowsOffByUs)
                {
                    t.Renderer.shadowCastingMode = noShadow ? ShadowCastingMode.Off : t.Shadows;
                    t.ShadowsOffByUs = noShadow;
                }
            }
        }

        // distance from a point to a box, squared; Bounds.SqrDistance's interop wrapper is invalid IL here
        private static float SqrDistance(Bounds b, Vector3 p)
        {
            Vector3 c = b.center, e = b.extents;
            float dx = Mathf.Max(0f, Mathf.Abs(p.x - c.x) - e.x);
            float dy = Mathf.Max(0f, Mathf.Abs(p.y - c.y) - e.y);
            float dz = Mathf.Max(0f, Mathf.Abs(p.z - c.z) - e.z);
            return dx * dx + dy * dy + dz * dz;
        }

        private void Scan(Transform parent)
        {
            foreach (var c in parent.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
            {
                var r = c.TryCast<Renderer>();
                if (r == null || !_known.Add(r.Pointer))
                    continue;
                if (r.TryCast<ParticleSystemRenderer>() != null)
                    continue; // effects manage themselves
                _tracked.Add(new Tracked { Renderer = r, Shadows = r.shadowCastingMode });
            }
        }

        public void RestoreAll()
        {
            foreach (var t in _tracked)
            {
                if (t.Renderer == null)
                    continue;
                if (t.HiddenByUs)
                    t.Renderer.enabled = true;
                if (t.ShadowsOffByUs)
                    t.Renderer.shadowCastingMode = t.Shadows;
            }
            _tracked.Clear();
            _known.Clear();
            _cursor = 0;
            Hidden = 0;
            Active = false;
        }
    }
}
