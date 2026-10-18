using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Space-wide sky presets that sync and save with the space. The choice lives in a marker object: a tiny
    /// Cube far below the map whose group id is "ssmm-sky:1|&lt;preset&gt;". Objects (and their group ids)
    /// already sync to everyone and are saved by the game and by this mod's space files, so every mod user
    /// in the space sees the same sky, and it comes back when the space is loaded. Players without the mod
    /// see the normal sky. Changing it needs build rights in the space, like any other edit.
    ///
    /// The game's sky shader (Custom/SimpleSkybox) is a six-stop gradient (_Color1 top .. _Color6 bottom)
    /// plus a horizon band, a sun and stars switched by the _SunEnabled / _StarsEnabled floats.
    /// </summary>
    internal sealed class Skybox
    {
        private const string Prefix = "ssmm-sky:1|";
        private const float ScanInterval = 1f;

        public static readonly (string Name, Color Top, Color Horizon, Color Bottom, bool Sun, bool Stars)[] Presets =
        {
            ("game default", default, default, default, true, false),
            ("clear day", new Color(0.22f, 0.47f, 0.93f), new Color(0.72f, 0.86f, 1f), new Color(0.38f, 0.4f, 0.44f), true, false),
            ("sunset", new Color(0.18f, 0.22f, 0.52f), new Color(1f, 0.52f, 0.28f), new Color(0.24f, 0.14f, 0.14f), true, false),
            ("dusk", new Color(0.14f, 0.05f, 0.3f), new Color(0.86f, 0.36f, 0.6f), new Color(0.1f, 0.05f, 0.12f), false, true),
            ("night", new Color(0.01f, 0.02f, 0.06f), new Color(0.06f, 0.08f, 0.18f), new Color(0.01f, 0.01f, 0.02f), false, true),
            ("overcast", new Color(0.55f, 0.58f, 0.62f), new Color(0.75f, 0.77f, 0.8f), new Color(0.35f, 0.36f, 0.38f), false, false),
            ("alien", new Color(0.05f, 0.25f, 0.2f), new Color(0.55f, 0.95f, 0.6f), new Color(0.05f, 0.1f, 0.08f), true, true),
            ("void", Color.black, new Color(0.02f, 0.02f, 0.03f), Color.black, false, true),
        };

        public int Current { get; private set; } // preset in force in this space (0 = no marker)
        public bool HasMarker => _marker != null;

        private ObjectInternals _marker;
        private float _nextScan;
        private Material _original, _override;
        private int _applied = -1;
        private bool _loggedProps;

        public void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                FindMarker();
            }
            int want = 0;
            if (_marker != null && _marker.groupId != null && _marker.groupId.StartsWith(Prefix) &&
                int.TryParse(_marker.groupId.Substring(Prefix.Length), out int p) && p >= 0 && p < Presets.Length)
                want = p;
            Current = want;
            if (want != _applied || (want != 0 && RenderSettings.skybox != _override))
                Apply(want);
        }

        /// <summary>Sets the space's sky for everyone with the mod (and saves it with the space).</summary>
        public string Set(int preset)
        {
            preset = Mathf.Clamp(preset, 0, Presets.Length - 1);
            if (_marker != null)
            {
                _marker.SetGroup(Prefix + preset);
                return "Sky: " + Presets[preset].Name;
            }
            if (preset == 0)
                return "Sky: game default";
            string id = "o_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string json = "[{\"id\":\"" + id + "\",\"n\":\"Cube\",\"p\":[0.0,-4000.0,0.0],\"r\":[0.0,0.0,0.0,1.0]," +
                "\"s\":[0.01,0.01,0.01],\"obs\":{\"ObjectConfigurables\":{\"tg\":\"primitive\",\"md\":2,\"gr\":false," +
                "\"cl\":false,\"cs\":false,\"rs\":false},\"ObjectInternals\":{\"m\":\"Sand\",\"c\":\"#000000FF\",\"tl\":1.0," +
                "\"g\":\"" + Prefix + preset + "\"}}}]";
            return SpaceIO.SpawnObjects(json)
                ? "Sky: " + Presets[preset].Name + " (saving to the space...)"
                : "Can't change the sky here (not in a space, or a load is running)";
        }

        private void FindMarker()
        {
            if (_marker != null && _marker.groupId != null && _marker.groupId.StartsWith(Prefix))
                return;
            _marker = null;
            var sm = SpaceIO.FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            if (parent == null)
                return;
            foreach (var c in parent.GetComponentsInChildren(Il2CppType.Of<ObjectInternals>(), true))
            {
                var oi = c.TryCast<ObjectInternals>();
                if (oi != null && oi.groupId != null && oi.groupId.StartsWith(Prefix))
                {
                    _marker = oi;
                    return;
                }
            }
        }

        private void Apply(int preset)
        {
            _applied = preset;
            if (preset == 0)
            {
                if (_original != null && RenderSettings.skybox == _override)
                    RenderSettings.skybox = _original;
                UpdateAmbient();
                return;
            }
            var current = RenderSettings.skybox;
            if (current == null)
                return;
            if (current != _override)
            {
                _original = current; // the space's own sky; a new space may bring a new one
                if (_override != null)
                    Object.Destroy(_override);
                _override = new Material(current) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            var p = Presets[preset];
            var m = _override;
            // top -> horizon over stops 1..3, horizon -> bottom over 4..6
            Colour(m, "_Color1", p.Top);
            Colour(m, "_Color2", Color.Lerp(p.Top, p.Horizon, 0.5f));
            Colour(m, "_Color3", p.Horizon);
            Colour(m, "_Color4", Color.Lerp(p.Horizon, p.Bottom, 0.35f));
            Colour(m, "_Color5", Color.Lerp(p.Horizon, p.Bottom, 0.7f));
            Colour(m, "_Color6", p.Bottom);
            Colour(m, "_HorizonColor", p.Horizon);
            Colour(m, "_SunColor", Color.Lerp(Color.white, p.Horizon, 0.3f));
            Colour(m, "_StarColor", Color.white);
            if (m.HasProperty("_SunEnabled"))
                m.SetFloat("_SunEnabled", p.Sun ? 1f : 0f);
            if (m.HasProperty("_StarsEnabled"))
                m.SetFloat("_StarsEnabled", p.Stars ? 1f : 0f);
            Keyword("_SUN", p.Sun);
            Keyword("_STARS", p.Stars);
            if (!_loggedProps)
            {
                _loggedProps = true;
                Melon<ModMenu>.Logger.Msg($"Sky: '{m.shader.name}', preset {p.Name}");
            }
            RenderSettings.skybox = _override;
            UpdateAmbient();
        }

        private static void Colour(Material m, string name, Color c)
        {
            if (m.HasProperty(name))
                m.SetColor(name, c);
        }

        private void Keyword(string keyword, bool on)
        {
            if (on)
                _override.EnableKeyword(keyword);
            else
                _override.DisableKeyword(keyword);
        }

        private static void UpdateAmbient()
        {
            try
            {
                DynamicGI.UpdateEnvironment();
            }
            catch (Exception)
            {
                // stripped in some builds; the sky still changes, ambient just keeps the old tint
            }
        }
    }
}
