using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Local lighting overrides: a brightness preset that scales every light and the ambient light,
    /// and a switch to turn real-time shadows off. The game's values are remembered per light and
    /// restored on Default, so spaces that change lighting keep working. Purely visual and local.
    /// </summary>
    internal sealed class Lighting
    {
        public enum Preset { Default = 0, Bright = 1, Fullbright = 2, Dim = 3 }
        public const int PresetCount = 4;

        public Preset Brightness = Preset.Default;
        public bool Shadows = true;

        private const float RescanInterval = 2f;

        private sealed class Saved
        {
            public Light Light;
            public float Intensity;
            public LightShadows Shadows;
        }

        private readonly Dictionary<IntPtr, Saved> _lights = new();
        private bool _ambientSaved;
        private AmbientMode _ambientMode;
        private Color _ambientLight;
        private float _ambientIntensity;
        private float _nextScan;
        private Preset _applied = Preset.Default;
        private bool _appliedShadows = true;

        public static string Label(Preset p) => p switch
        {
            Preset.Bright => "bright",
            Preset.Fullbright => "fullbright",
            Preset.Dim => "dim",
            _ => "default",
        };

        private bool Overriding => Brightness != Preset.Default || !Shadows;

        public void Update()
        {
            bool changed = Brightness != _applied || Shadows != _appliedShadows;
            if (!Overriding)
            {
                if (changed)
                    RestoreAll();
                _applied = Brightness;
                _appliedShadows = Shadows;
                return;
            }
            if (!changed && Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + RescanInterval;
            if (changed)
                RestoreAll(); // reapply from the game's values, not on top of the last preset

            float scale = Brightness switch
            {
                Preset.Bright => 1.6f,
                Preset.Fullbright => 1f,
                Preset.Dim => 0.5f,
                _ => 1f,
            };

            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<Light>(), FindObjectsSortMode.None))
            {
                var light = obj.TryCast<Light>();
                if (light == null)
                    continue;
                if (!_lights.TryGetValue(light.Pointer, out var saved))
                {
                    saved = new Saved { Light = light, Intensity = light.intensity, Shadows = light.shadows };
                    _lights[light.Pointer] = saved;
                }
                light.intensity = saved.Intensity * scale;
                light.shadows = Shadows ? saved.Shadows : LightShadows.None;
            }

            SaveAmbient();
            switch (Brightness)
            {
                case Preset.Fullbright:
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = Color.white;
                    RenderSettings.ambientIntensity = 1f;
                    break;
                case Preset.Bright:
                case Preset.Dim:
                    RenderSettings.ambientMode = _ambientMode;
                    RenderSettings.ambientLight = _ambientLight * scale;
                    RenderSettings.ambientIntensity = _ambientIntensity * scale;
                    break;
                default:
                    RestoreAmbient();
                    break;
            }
            _applied = Brightness;
            _appliedShadows = Shadows;
        }

        private void SaveAmbient()
        {
            if (_ambientSaved)
                return;
            _ambientMode = RenderSettings.ambientMode;
            _ambientLight = RenderSettings.ambientLight;
            _ambientIntensity = RenderSettings.ambientIntensity;
            _ambientSaved = true;
        }

        private void RestoreAmbient()
        {
            if (!_ambientSaved)
                return;
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientLight = _ambientLight;
            RenderSettings.ambientIntensity = _ambientIntensity;
        }

        public void RestoreAll()
        {
            foreach (var saved in _lights.Values)
            {
                if (saved.Light == null)
                    continue;
                saved.Light.intensity = saved.Intensity;
                saved.Light.shadows = saved.Shadows;
            }
            _lights.Clear();
            RestoreAmbient();
            _ambientSaved = false; // a new space may have its own ambient; re-read it next time
        }
    }
}
