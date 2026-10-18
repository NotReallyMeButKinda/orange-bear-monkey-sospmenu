using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Outlines what the Creatool is pointing at (white) and what it has selected (blue), using the
    /// game's own screen-space outline (the same path SnapGrabHoverOutline uses for grab hover).
    /// Only space content is outlined: objects and chips under the SpaceBuilder parents.
    /// </summary>
    internal sealed class CreatoolHighlight
    {
        private const float UpdateInterval = 0.05f;
        private const float LocalToolRange = 2.5f; // a tool this close to our head is ours
        private static readonly Color HoverColour = new(1f, 1f, 1f, 1f);
        private static readonly Color SelectedColour = new(0.267f, 0.58f, 0.996f, 1f); // the game's selection blue

        public bool Active { get; private set; }

        private enum Role { Hover, Selected }

        private readonly Dictionary<IntPtr, (Renderer renderer, Role role)> _outlined = new();
        private Material _hoverMat, _selectedMat;
        private float _next;

        public void SetActive(bool on)
        {
            Active = on;
            if (!on)
                ClearAll();
        }

        public void Update()
        {
            if (!Active || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + UpdateInterval;
            if (!EnsureMaterials())
                return;

            var desired = new Dictionary<IntPtr, (Renderer renderer, Role role)>();
            var tool = LocalTool();
            var sm = SpaceIO.FindManager();
            var builder = sm != null ? sm._spaceBuilder : null;
            if (tool != null && builder != null)
            {
                var selected = tool._selected;
                if (selected != null)
                    for (int i = 0; i < selected.Count; i++)
                        Collect(selected[i], Role.Selected, desired);

                if (tool.RayDidHit)
                {
                    var hit = tool.RayHit;
                    var root = hit.collider != null ? ContentRoot(hit.collider.transform, builder) : null;
                    if (root != null && !tool.IsSelected(root.gameObject))
                        Collect(root.gameObject, Role.Hover, desired);
                }
            }

            // drop outlines that are no longer wanted (or changed role)
            foreach (var kv in new List<KeyValuePair<IntPtr, (Renderer renderer, Role role)>>(_outlined))
            {
                if (!desired.TryGetValue(kv.Key, out var want) || want.role != kv.Value.role)
                {
                    if (kv.Value.renderer != null)
                        SnapGrabHoverOutline.UnregisterRenderer(kv.Value.renderer);
                    _outlined.Remove(kv.Key);
                }
            }
            foreach (var kv in desired)
            {
                if (_outlined.ContainsKey(kv.Key))
                    continue;
                SnapGrabHoverOutline.RegisterRenderer(kv.Value.renderer, kv.Value.role == Role.Hover ? _hoverMat : _selectedMat);
                _outlined[kv.Key] = kv.Value;
            }
        }

        private static void Collect(GameObject go, Role role, Dictionary<IntPtr, (Renderer, Role)> into)
        {
            if (go == null)
                return;
            foreach (var c in go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
            {
                var r = c.TryCast<Renderer>();
                if (r == null || r.TryCast<LineRenderer>() != null)
                    continue; // wires and ropes are lines; outlining them looks wrong
                if (!into.ContainsKey(r.Pointer) || role == Role.Selected)
                    into[r.Pointer] = (r, role);
            }
        }

        // The object (or chip) the hit belongs to: the ancestor that sits directly under a space parent.
        private static Transform ContentRoot(Transform t, SpaceBuilder builder)
        {
            Transform objects = builder.objectsParent, circuits = builder.circuitsParent;
            for (; t != null; t = t.parent)
            {
                var parent = t.parent;
                if (parent != null && (parent == objects || parent == circuits))
                    return t;
            }
            return null;
        }

        internal static Creatool LocalTool()
        {
            var head = SnapLocomotion.Instance != null ? SnapLocomotion.Instance.Head : null;
            if (head == null)
                return null;
            var found = Object.FindObjectsByType(Il2CppType.Of<Creatool>(), FindObjectsSortMode.None);
            foreach (var obj in found)
            {
                var tool = obj.TryCast<Creatool>();
                if (tool == null || !tool.isActiveAndEnabled)
                    continue;
                var origin = tool.rayOrigin != null ? tool.rayOrigin : tool.transform;
                if ((origin.position - head.position).sqrMagnitude < LocalToolRange * LocalToolRange)
                    return tool;
            }
            return null;
        }

        // Tinted copies of the game's grab-hover outline material.
        private bool EnsureMaterials()
        {
            if (_hoverMat != null && _selectedMat != null)
                return true;
            var hover = Object.FindAnyObjectByType(Il2CppType.Of<SnapGrabHoverOutline>(), FindObjectsInactive.Include);
            var source = hover != null ? hover.TryCast<SnapGrabHoverOutline>() : null;
            if (source == null || source.outlineMaterial == null)
                return false;
            _hoverMat = Tinted(source.outlineMaterial, HoverColour);
            _selectedMat = Tinted(source.outlineMaterial, SelectedColour);
            return true;
        }

        private static Material Tinted(Material source, Color colour)
        {
            var m = new Material(source) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            // the outline shader's colour property name isn't known; setting a missing one is a no-op
            foreach (var prop in new[] { "_OutlineColor", "_OutlineColour", "_Color", "_BaseColor" })
                m.SetColor(prop, colour);
            return m;
        }

        private void ClearAll()
        {
            foreach (var kv in _outlined)
                if (kv.Value.renderer != null)
                    SnapGrabHoverOutline.UnregisterRenderer(kv.Value.renderer);
            _outlined.Clear();
        }
    }
}
