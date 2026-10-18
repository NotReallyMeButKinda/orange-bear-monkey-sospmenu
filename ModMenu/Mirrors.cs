using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// VRChat-style mirrors you can spawn, using the game's own Mirror component (per-eye reflection
    /// cameras, the Custom/Mirror shader): a quad gets the shader, then Mirror is added and configured
    /// before its Start runs. Grip a mirror's edge to carry it; it keeps facing the way you turn it.
    /// Purely local: other players don't see your mirrors.
    ///
    /// Every mirror (these and the ones built into spaces) is also made to reflect the mirror-only layer,
    /// so you can see your own hat in it. (A separate "camera mirror" with its own camera crashed the game
    /// in VR, so the game's mirrors are used for that instead.)
    /// </summary>
    internal sealed class Mirrors
    {
        public const int MaxMirrors = 4;
        public static readonly (string Name, Vector2 Size)[] Sizes =
        {
            ("small", new Vector2(0.8f, 1.2f)), ("medium", new Vector2(1.4f, 2f)), ("large", new Vector2(2.6f, 2.4f)),
        };
        public static readonly (string Name, int Resolution)[] Qualities = { ("low", 512), ("medium", 1024), ("high", 2048) };

        public int SizeIndex = 1;
        public int QualityIndex = 1;
        public int Count => _mirrors.Count;

        private const float GrabReach = 0.15f;  // how close to the mirror surface a hand must be
        private const float LayerScanInterval = 2f;
        private static readonly string OldCrashMarker = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SocialSpaceModMenu", "camera-mirror-started");
        private readonly List<GameObject> _mirrors = new();
        private Shader _shader;
        private GameObject _held;
        private XRNode _heldHand;
        private Vector3 _heldPos;
        private Quaternion _heldRot;
        private bool _wasLeft, _wasRight;
        private float _nextLayerScan;

        public Mirrors()
        {
            try { System.IO.File.Delete(OldCrashMarker); } catch (System.Exception) { } // left by 1.17.x camera mirrors
        }

        public string Spawn()
        {
            var loco = SnapLocomotion.Instance;
            var head = loco != null ? loco.Head : null;
            if (head == null)
                return "No player yet";
            _mirrors.RemoveAll(m => m == null);
            if (_mirrors.Count >= MaxMirrors)
                return $"Already {MaxMirrors} mirrors; remove one first";
            if (_shader == null)
                _shader = Shader.Find("Custom/Mirror");
            if (_shader == null)
                return "The game's mirror shader isn't loaded";

            var size = Sizes[Mathf.Clamp(SizeIndex, 0, Sizes.Length - 1)].Size;
            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
                flatForward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            flatForward.Normalize();
            float floor = loco.PlayerPosition.y;

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "SocialSpaceModMenu.Mirror";
            var col = go.GetComponent(Il2CppType.Of<Collider>());
            if (col != null)
                Object.Destroy(col); // don't block people or the Creatool ray
            // a quad is seen from its -Z side; facing it away from us shows its front to us
            go.transform.SetPositionAndRotation(
                head.position + flatForward * 1.5f + Vector3.up * (floor + size.y / 2f + 0.1f - head.position.y),
                Quaternion.LookRotation(flatForward, Vector3.up));
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = go.GetComponent(Il2CppType.Of<Renderer>()).Cast<Renderer>();
            renderer.sharedMaterial = new Material(_shader);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var mirror = go.AddComponent(Il2CppType.Of<Mirror>()).Cast<Mirror>();
            LayerMask everything = default;
            everything.value = ~0; // includes the mirror-only layer, so your hat shows
            mirror.reflectionLayers = everything;
            mirror.clipPlaneOffset = 0.02f;
            mirror.reflectionResolution = Qualities[Mathf.Clamp(QualityIndex, 0, Qualities.Length - 1)].Resolution;
            mirror.reflectionShadows = false;
            mirror.maxRenderDist = 25f;
            mirror.renderEveryNthFrame = 1;

            _mirrors.Add(go);
            return "Mirror placed (shows your hat); grip its edge to move it";
        }

        public void RemoveLast()
        {
            _mirrors.RemoveAll(m => m == null);
            if (_mirrors.Count == 0)
                return;
            var last = _mirrors[_mirrors.Count - 1];
            _mirrors.RemoveAt(_mirrors.Count - 1);
            Destroy(last);
        }

        public void RemoveAll()
        {
            foreach (var m in _mirrors)
                Destroy(m);
            _mirrors.Clear();
            _held = null;
        }

        private void Destroy(GameObject m)
        {
            if (m == null)
                return;
            if (_held == m)
                _held = null;
            var r = m.GetComponent(Il2CppType.Of<Renderer>());
            var renderer = r != null ? r.TryCast<Renderer>() : null;
            if (renderer != null && renderer.sharedMaterial != null)
                Object.Destroy(renderer.sharedMaterial);
            Object.Destroy(m); // Mirror.OnDestroy frees its cameras and textures
        }

        public void Update()
        {
            if (Time.unscaledTime >= _nextLayerScan)
            {
                _nextLayerScan = Time.unscaledTime + LayerScanInterval;
                ReflectMirrorOnlyLayer();
            }
            if (_mirrors.Count == 0)
                return;
            UpdateHeld();
        }

        // the space's own mirrors reflect only the layers their builder picked; add the mirror-only one
        private static void ReflectMirrorOnlyLayer()
        {
            int bit = 1 << MirrorOnly.Layer;
            foreach (var o in Object.FindObjectsByType(Il2CppType.Of<Mirror>(), FindObjectsSortMode.None))
            {
                var m = o.TryCast<Mirror>();
                if (m == null || (m.reflectionLayers.value & bit) != 0)
                    continue;
                LayerMask mask = m.reflectionLayers;
                mask.value |= bit;
                m.reflectionLayers = mask;
            }
        }

        // grip near a mirror to pick it up; it follows the hand rigidly until released
        private void UpdateHeld()
        {
            var loco = SnapLocomotion.Instance;
            var space = loco != null && loco.Head != null ? loco.Head.parent : null;
            if (space == null)
                return;
            bool left = Grip(XRNode.LeftHand), right = Grip(XRNode.RightHand);
            bool leftDown = left && !_wasLeft, rightDown = right && !_wasRight;
            _wasLeft = left;
            _wasRight = right;

            if (_held != null)
            {
                bool still = _heldHand == XRNode.LeftHand ? left : right;
                if (!still || !Pose(_heldHand, space, out Vector3 p, out Quaternion q))
                {
                    _held = null;
                    return;
                }
                _held.transform.SetPositionAndRotation(p + q * _heldPos, q * _heldRot);
                return;
            }
            if (leftDown)
                TryPick(XRNode.LeftHand, space);
            else if (rightDown)
                TryPick(XRNode.RightHand, space);
        }

        private void TryPick(XRNode node, Transform space)
        {
            if (Ride.Riding || GripGrab.HandBusy(node) || !Pose(node, space, out Vector3 p, out Quaternion q))
                return;
            foreach (var m in _mirrors)
            {
                if (m == null)
                    continue;
                Vector3 local = m.transform.InverseTransformPoint(p); // quad spans -0.5..0.5 in x and y
                float depth = Mathf.Abs(local.z) * m.transform.lossyScale.z;
                if (Mathf.Abs(local.x) > 0.55f || Mathf.Abs(local.y) > 0.55f || depth > GrabReach)
                    continue;
                _held = m;
                _heldHand = node;
                var inv = Quaternion.Inverse(q);
                _heldPos = inv * (m.transform.position - p);
                _heldRot = inv * m.transform.rotation;
                XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid)
                    dev.SendHapticImpulse(0, 0.4f, 0.05f);
                return;
            }
        }

        private static bool Grip(XRNode node)
        {
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
        }

        private static bool Pose(XRNode node, Transform space, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid || !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 p) ||
                !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion q))
                return false;
            pos = space.TransformPoint(p);
            rot = space.rotation * q;
            return true;
        }
    }
}
