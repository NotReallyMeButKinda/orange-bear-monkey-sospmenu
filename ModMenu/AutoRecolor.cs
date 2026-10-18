using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Recolours primitives the moment you place them with the Creatool. Source "Creatool" runs the
    /// Creatool's own Recolour step (CreatoolRecolour.ApplyRecolourTo) with whatever colour, material and
    /// tiling are picked in its Recolour menu; "Custom" applies a colour from this mod's menu (or a random
    /// one) and keeps the object's own material. Either way it goes through ObjectInternals.ApplyMaterial,
    /// so it syncs and saves like a normal recolour.
    ///
    /// The hook is the Creatool's place action itself (CreatoolCreate.OnTriggerPressed), which selects
    /// what it just spawned; that object is recoloured a few frames later, once it has finished setting
    /// itself up (recolouring in the spawn frame got overwritten). Loads and duplicates are left alone.
    /// </summary>
    internal sealed class AutoRecolor
    {
        public enum Source { Off = 0, Creatool = 1, Custom = 2 }
        public const int SourceCount = 3;
        private const int DelayFrames = 3;

        public Source Mode;
        public int CustomColour; // index into Cosmetics.Colours, or Cosmetics.Colours.Length = random

        private static readonly List<(GameObject Go, int Frame)> Pending = new();
        private static bool _armed; // only collect while a source is picked

        public static string Label(Source s) => s switch
        {
            Source.Creatool => "Creatool's color",
            Source.Custom => "custom color",
            _ => "off",
        };

        public static string ColourLabel(int index) =>
            index >= 0 && index < Cosmetics.Colours.Length ? Cosmetics.Colours[index].Name : "random";

        // after the Creatool places something, it selects it: queue whatever is selected now
        [HarmonyPatch(typeof(CreatoolCreate), nameof(CreatoolCreate.OnTriggerPressed))]
        private static class PlacePatch
        {
            private static void Postfix(CreatoolCreate __instance)
            {
                try
                {
                    if (!_armed || __instance == null)
                        return;
                    var tool = CreatoolHighlight.LocalTool();
                    var select = tool != null ? tool.selectModule : null;
                    var entries = select != null ? select.entries : null;
                    if (entries == null || entries.Count == 0 || entries.Count > 3)
                        return;
                    foreach (var e in entries)
                    {
                        var go = e != null && e.view != null ? e.view.gameObject : null;
                        if (go != null && !Pending.Exists(p => p.Go == go))
                            Pending.Add((go, Time.frameCount + DelayFrames));
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLoader.Melon<ModMenu>.Logger.Warning($"Auto recolor hook: {ex.Message}");
                }
            }
        }

        public void Update()
        {
            _armed = Mode != Source.Off;
            if (Pending.Count == 0)
                return;
            if (!_armed)
            {
                Pending.Clear();
                return;
            }
            var tool = CreatoolHighlight.LocalTool();
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                var (go, frame) = Pending[i];
                if (go != null && Time.frameCount < frame)
                    continue;
                Pending.RemoveAt(i);
                if (go == null || tool == null || SpaceIO.Busy)
                    continue;
                var view = go.GetComponent(Il2CppType.Of<Il2CppSharp.SharpView>())?.TryCast<Il2CppSharp.SharpView>();
                if (view == null || !view.isMine || !IsPrimitive(go.transform))
                    continue;
                var internals = go.GetComponent(Il2CppType.Of<ObjectInternals>())?.TryCast<ObjectInternals>();
                if (internals != null && (internals.groupId == null || !internals.groupId.StartsWith("ssmm-")))
                    Apply(tool, internals);
            }
        }

        private static bool IsPrimitive(Transform t)
        {
            var c = t.GetComponent(Il2CppType.Of<ObjectConfigurables>());
            var cfg = c != null ? c.TryCast<ObjectConfigurables>() : null;
            return cfg != null && cfg.bakedTags != null && cfg.bakedTags.Contains("primitive");
        }

        private void Apply(Creatool tool, ObjectInternals internals)
        {
            if (Mode == Source.Creatool)
            {
                var recolour = tool.recolourModule;
                if (recolour != null)
                {
                    recolour.ApplyRecolourTo(internals); // exactly what Recolour mode does to a target
                    return;
                }
                var mat = tool.RecolourMaterial != null ? tool.RecolourMaterial : internals.FirstSpaceMaterial();
                if (mat != null)
                    internals.ApplyMaterial(mat, tool.RecolourColour, tool.RecolourTiling);
                return;
            }
            var own = internals.FirstSpaceMaterial();
            if (own == null)
                return;
            Color colour = CustomColour >= 0 && CustomColour < Cosmetics.Colours.Length
                ? Cosmetics.Colours[CustomColour].Colour
                : Color.HSVToRGB(Random.value, 0.75f, 0.95f);
            internals.ApplyMaterial(own, colour, internals.materialTiling > 0f ? internals.materialTiling : 1f);
        }
    }
}
