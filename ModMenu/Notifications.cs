using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Head-following toasts shown above the menu area, whether or not the menu is open.
    /// Each toast lives a few seconds; up to three stack, newest at the bottom.
    /// </summary>
    internal sealed class Notifications
    {
        private const float Lifetime = 5f;
        private const float FadeTime = 0.6f;
        private const int MaxVisible = 3;
        private const float Distance = 0.75f;
        private const float Lift = 0.12f;
        private const float FollowSharpness = 8f;

        public bool Enabled = true;
        public bool Chime = true;

        private readonly List<(string text, float shownAt)> _toasts = new();
        private GameObject _root;
        private TextMeshPro _text;
        private AudioSource _source;
        private AudioClip _chime;
        private bool _snap = true;

        public void Show(string title, string body = null)
        {
            if (!Enabled || string.IsNullOrEmpty(title))
                return;
            string text = string.IsNullOrEmpty(body)
                ? $"<b>{title}</b>"
                : $"<b>{title}</b>\n<size=80%><color=#B5B0A8>{body}</color></size>";
            _toasts.Add((text, Time.unscaledTime));
            while (_toasts.Count > MaxVisible)
                _toasts.RemoveAt(0);
            Pulse();
        }

        public void LateUpdate(Transform head)
        {
            float now = Time.unscaledTime;
            _toasts.RemoveAll(t => now - t.shownAt > Lifetime);
            if (_toasts.Count == 0 || head == null)
            {
                if (_root != null && _root.activeSelf)
                    _root.SetActive(false);
                return;
            }
            if (!EnsureObjects())
                return;
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
                _snap = true;
            }

            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f)
                fwd = head.up;
            fwd.y = 0f;
            fwd.Normalize();
            Vector3 target = head.position + fwd * Distance + Vector3.up * Lift;
            var t = _root.transform;
            t.position = _snap ? target
                : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime));
            _snap = false;
            t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);

            var sb = new StringBuilder();
            foreach (var (text, shownAt) in _toasts)
            {
                float left = Lifetime - (now - shownAt);
                int alpha = (int)(255 * Mathf.Clamp01(left / FadeTime));
                sb.Append($"<alpha=#{alpha:X2}>{text}\n");
            }
            _text.text = sb.ToString();
        }

        private void Pulse()
        {
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid)
                    dev.SendHapticImpulse(0u, 0.25f, 0.06f);
            }
            if (Chime && EnsureObjects() && _chime != null)
            {
                // a 2D chime: it is for you, not the room
                _source.PlayOneShot(_chime, 0.5f);
            }
        }

        private bool EnsureObjects()
        {
            if (_root != null)
                return true;
            var font = FindFont();
            if (font == null)
                return false;

            _root = new GameObject("SocialSpaceModMenu.Notifications");
            Object.DontDestroyOnLoad(_root);
            _text = _root.AddComponent(Il2CppType.Of<TextMeshPro>()).Cast<TextMeshPro>();
            _text.font = font;
            _text.fontSize = 0.3f;
            _text.richText = true;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.alignment = TextAlignmentOptions.Bottom;
            _text.color = Color.white;
            _text.sortingOrder = 5001;
            _text.rectTransform.sizeDelta = new Vector2(0.7f, 0.3f);
            _text.rectTransform.pivot = new Vector2(0.5f, 0f); // grow upwards from the anchor

            _source = _root.AddComponent(Il2CppType.Of<AudioSource>()).Cast<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            try
            {
                _chime = TapFeedback.MakeClip("SocialSpaceModMenu.Chime", SynthesizeChime(44100), 44100);
            }
            catch (Exception)
            {
                _chime = null; // stay silent rather than break notifications
            }
            _root.SetActive(false);
            return true;
        }

        // Two soft bell tones a fifth apart.
        private static float[] SynthesizeChime(int rate)
        {
            int n = (int)(rate * 0.45f);
            var s = new float[n];
            int second = (int)(rate * 0.09f);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float a = Bell(t, 880f);
                float b = i >= second ? Bell((float)(i - second) / rate, 1318.5f) : 0f;
                s[i] = 0.45f * (a + b);
            }
            return s;
        }

        private static float Bell(float t, float freq)
        {
            float env = (1f - Mathf.Exp(-t * 400f)) * Mathf.Exp(-t * 9f);
            return env * (Mathf.Sin(2f * Mathf.PI * freq * t) + 0.25f * Mathf.Sin(2f * Mathf.PI * freq * 2.01f * t));
        }

        // The default TMP font if the build has one, otherwise whichever font the game's UI is using.
        internal static TMP_FontAsset FindFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            if (font != null)
                return font;
            var any = Object.FindAnyObjectByType(Il2CppType.Of<TMP_Text>());
            var tmp = any != null ? any.TryCast<TMP_Text>() : null;
            return tmp != null ? tmp.font : null;
        }
    }
}
