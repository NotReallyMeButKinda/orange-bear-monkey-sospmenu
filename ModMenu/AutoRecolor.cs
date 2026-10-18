using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Recolours primitives the moment you place them with the Creatool. Source "Creatool" uses whatever
    /// colour, material and tiling are picked in the Creatool's Recolour menu; "Custom" applies a colour
    /// from this mod's menu (or a random one) and keeps the object's own material. It goes through
    /// ObjectInternals.ApplyMaterial, the same call the Recolour mode makes, so it syncs and saves.
    /// Only fires in Create mode for a few new objects at once, so loads and duplicates are left alone.
    /// </summary>
    internal sealed class AutoRecolor
    {
        public enum Source { Off = 0, Creatool = 1, Custom = 2 }
        public const int SourceCount = 3;
        public const int RandomColour = -1; // index past the palette in the menu

        public Source Mode;
        public int CustomColour; // index into Cosmetics.Colours, or Cosmetics.Colours.Length = random

        private Transform _parent;
        private int _lastCount = -1;
        private readonly HashSet<System.IntPtr> _done = new();

        public static string Label(Source s) => s switch
        {
            Source.Creatool => "Creatool's color",
            Source.Custom => "custom color",
            _ => "off",
        };

        public static string ColourLabel(int index) =>
            index >= 0 && index < Cosmetics.Colours.Length ? Cosmetics.Colours[index].Name : "random";

        public void Update()
        {
            var sm = SpaceIO.FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            if (parent == null)
                return;
            if (parent != _parent)
            {
                _parent = parent;
                _lastCount = parent.childCount;
                _done.Clear();
                return;
            }
            int count = parent.childCount;
            int added = count - _lastCount;
            _lastCount = count;
            if (Mode == Source.Off || added <= 0 || added > 3 || SpaceIO.Busy)
                return;
            var tool = CreatoolHighlight.LocalTool();
            if (tool == null || tool.currentMode != Creatool.Mode.Create)
                return;

            for (int i = count - added; i < count; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || !_done.Add(child.Pointer))
                    continue;
                var view = child.GetComponent(Il2CppType.Of<Il2CppSharp.SharpView>());
                var sv = view != null ? view.TryCast<Il2CppSharp.SharpView>() : null;
                if (sv == null || !sv.isMine || !IsPrimitive(child))
                    continue;
                var oi = child.GetComponent(Il2CppType.Of<ObjectInternals>());
                var internals = oi != null ? oi.TryCast<ObjectInternals>() : null;
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
