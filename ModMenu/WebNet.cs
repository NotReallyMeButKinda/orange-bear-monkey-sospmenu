using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Web swinging for everyone with the mod: the local ropes' anchor points go out as "ssmm_web" in this
    /// player's client data (on fire, release, and when a carried anchor moves), and every other mod user's
    /// ropes are drawn from their avatar's hands with the same fire / release sounds, spatialised at the hand.
    /// </summary>
    internal sealed class WebNet
    {
        public const string Key = "ssmm_web";
        private const float RescanInterval = 0.2f;

        private sealed class Remote
        {
            public AccountClientData Account;
            public string Value;
            public Transform Head;
            public Transform[] Hands;
            public Vector3? Left, Right;
            public LineRenderer LeftLine, RightLine;
        }

        private readonly Dictionary<IntPtr, Remote> _remotes = new();
        private float _nextScan;
        private static AudioClip _fire, _release;

        // ---- local side

        public static string Encode(Vector3? left, Vector3? right) =>
            "1|" + Point(left) + "|" + Point(right);

        private static string Point(Vector3? p) => p.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", p.Value.x, p.Value.y, p.Value.z)
            : "";

        private static bool TryDecode(string value, out Vector3? left, out Vector3? right)
        {
            left = right = null;
            if (string.IsNullOrEmpty(value))
                return false;
            var parts = value.Split('|');
            if (parts.Length < 3 || parts[0] != "1")
                return false;
            left = ParsePoint(parts[1]);
            right = ParsePoint(parts[2]);
            return true;
        }

        private static Vector3? ParsePoint(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            var c = s.Split(',');
            if (c.Length != 3 ||
                !float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return null;
            return new Vector3(x, y, z);
        }

        public static void Publish(Vector3? left, Vector3? right) => ClientDataChannel.Set(Key, Encode(left, right));

        // ---- remote side

        public void Update(Material lineMaterial)
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Scan();
            }
            foreach (var r in _remotes.Values)
            {
                if (r.Account == null || r.Head == null)
                    continue;
                Draw(r, true, r.Left, ref r.LeftLine, lineMaterial);
                Draw(r, false, r.Right, ref r.RightLine, lineMaterial);
            }
        }

        private void Scan()
        {
            var seen = new HashSet<IntPtr>();
            var loco = SnapLocomotion.Instance;
            foreach (var acc in ClientDataChannel.Others())
            {
                seen.Add(acc.Pointer);
                string value = acc.GetString(Key) ?? "";
                if (!_remotes.TryGetValue(acc.Pointer, out var r))
                {
                    var head = PlayerColliders.FindHead(acc);
                    if (head == null)
                        continue;
                    r = new Remote { Account = acc, Head = head, Hands = PlayerColliders.FindHands(acc, loco != null ? loco.Origin : null) };
                    _remotes[acc.Pointer] = r;
                }
                if (r.Value == value)
                    continue;
                TryDecode(value, out var left, out var right);
                Sounds(r, true, r.Left, left);
                Sounds(r, false, r.Right, right);
                r.Value = value;
                r.Left = left;
                r.Right = right;
            }
            foreach (var key in new List<IntPtr>(_remotes.Keys))
            {
                if (seen.Contains(key))
                    continue;
                Clear(_remotes[key]);
                _remotes.Remove(key);
            }
        }

        private void Sounds(Remote r, bool left, Vector3? before, Vector3? after)
        {
            if (before.HasValue == after.HasValue && (!after.HasValue || (before.Value - after.Value).sqrMagnitude < 0.25f))
                return; // unchanged, or a carried anchor drifting
            Vector3 at = HandFor(r, left, after ?? before ?? r.Head.position);
            PlayAt(after.HasValue ? FireClip() : ReleaseClip(), at, after.HasValue ? 0.8f : 0.5f);
        }

        // the remote avatar's hand on that side (by its offset from the head), else the head
        private static Vector3 HandFor(Remote r, bool left, Vector3 fallbackTowards)
        {
            Transform best = null;
            foreach (var h in r.Hands)
            {
                if (h == null || !h.gameObject.activeInHierarchy)
                    continue;
                float side = r.Head.InverseTransformPoint(h.position).x;
                if ((left && side < 0f) || (!left && side >= 0f))
                {
                    best = h;
                    break;
                }
            }
            return best != null ? best.position : r.Head.position + (fallbackTowards - r.Head.position).normalized * 0.4f;
        }

        private static void Draw(Remote r, bool left, Vector3? anchor, ref LineRenderer line, Material mat)
        {
            if (!anchor.HasValue)
            {
                if (line != null)
                    line.enabled = false;
                return;
            }
            if (line == null)
                line = MakeLine(mat);
            line.enabled = true;
            line.SetPosition(0, HandFor(r, left, anchor.Value));
            line.SetPosition(1, anchor.Value);
        }

        internal static LineRenderer MakeLine(Material mat)
        {
            var go = new GameObject("SocialSpaceModMenu.RemoteWeb");
            Object.DontDestroyOnLoad(go);
            var line = go.AddComponent(Il2CppType.Of<LineRenderer>()).Cast<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.012f;
            line.endWidth = 0.006f;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            if (mat != null)
                line.sharedMaterial = mat;
            line.startColor = new Color(0.95f, 0.95f, 0.95f, 0.9f);
            line.endColor = new Color(0.85f, 0.85f, 0.9f, 0.8f);
            return line;
        }

        private static void Clear(Remote r)
        {
            if (r.LeftLine != null)
                Object.Destroy(r.LeftLine.gameObject);
            if (r.RightLine != null)
                Object.Destroy(r.RightLine.gameObject);
        }

        public void ClearAll()
        {
            foreach (var r in _remotes.Values)
                Clear(r);
            _remotes.Clear();
        }

        // ---- sounds (synthesised; shared with the local web swing)

        public static void PlayAt(AudioClip clip, Vector3 position, float volume)
        {
            if (clip == null)
                return;
            var go = new GameObject("SocialSpaceModMenu.WebSound");
            go.transform.position = position;
            var src = go.AddComponent(Il2CppType.Of<AudioSource>()).Cast<AudioSource>();
            src.clip = clip;
            src.volume = volume;
            src.spatialBlend = 1f;
            src.minDistance = 0.5f;
            src.maxDistance = 25f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            Object.Destroy(go, clip.length + 0.1f);
        }

        public static AudioClip FireClip() => _fire ??= TapFeedback.MakeClip("ssmm_web_fire", Thwip(44100), 44100);
        public static AudioClip ReleaseClip() => _release ??= TapFeedback.MakeClip("ssmm_web_release", Snap(44100), 44100);

        // a fast "thwip": filtered noise whose brightness sweeps down, with a short tonal zip
        private static float[] Thwip(int rate)
        {
            var rng = new System.Random(77);
            int n = (int)(rate * 0.18f);
            var s = new float[n];
            float lp = 0f, hp = 0f, prev = 0f, peak = 0f;
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float cutoff = Mathf.Lerp(0.6f, 0.08f, Mathf.Clamp01(t / 0.15f));
                float noise = (float)rng.NextDouble() * 2f - 1f;
                lp += (noise - lp) * cutoff;
                hp = lp - prev;
                prev = lp;
                phase += 2 * Math.PI * (1800f - 9000f * t) / rate;
                float zip = (float)Math.Sin(phase) * Mathf.Exp(-t * 40f);
                float env = (1f - Mathf.Exp(-t * 2000f)) * Mathf.Exp(-t * 22f);
                s[i] = env * (0.9f * (lp + hp * 0.6f) + 0.25f * zip);
                peak = Mathf.Max(peak, Mathf.Abs(s[i]));
            }
            Normalise(s, peak);
            return s;
        }

        // a soft release: a short low noise puff
        private static float[] Snap(int rate)
        {
            var rng = new System.Random(91);
            int n = (int)(rate * 0.09f);
            var s = new float[n];
            float lp = 0f, peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                lp += ((float)rng.NextDouble() * 2f - 1f - lp) * 0.15f;
                float env = (1f - Mathf.Exp(-t * 1500f)) * Mathf.Exp(-t * 55f);
                s[i] = env * lp;
                peak = Mathf.Max(peak, Mathf.Abs(s[i]));
            }
            Normalise(s, peak);
            return s;
        }

        private static void Normalise(float[] s, float peak)
        {
            if (peak <= 0f)
                return;
            float g = 0.8f / peak;
            for (int i = 0; i < s.Length; i++)
                s[i] *= g;
        }
    }
}
