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
        private readonly List<GameObject> _mirrors = new();
        private readonly Dictionary<System.IntPtr, RenderTexture> _textures = new(); // camera mirrors
        private readonly Dictionary<System.IntPtr, Camera> _cameras = new();
        // Written when a camera mirror starts rendering and removed once it has rendered fine for a while.
        // If the game dies in between, the file is still there next launch and camera mirrors stay off.
        private static readonly string CrashMarker = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SocialSpaceModMenu", "camera-mirror-started");
        private const int SafeFrames = 300;
        private int _renderedFrames;

        /// <summary>True when the last camera mirror didn't survive (the game closed while it was starting).</summary>
        public static bool CameraCrashedLastTime => System.IO.File.Exists(CrashMarker);

        public static void ClearCrashMarker()
        {
            try { System.IO.File.Delete(CrashMarker); } catch (System.Exception) { }
        }

        private readonly UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest _request = new();
        private int _frame;
        private Shader _shader;
        private GameObject _held;
        private XRNode _heldHand;
        private Vector3 _heldPos;
        private Quaternion _heldRot;
        private bool _wasLeft, _wasRight;

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
            everything.value = ~0;
            mirror.reflectionLayers = everything;
            mirror.clipPlaneOffset = 0.02f;
            mirror.reflectionResolution = Qualities[Mathf.Clamp(QualityIndex, 0, Qualities.Length - 1)].Resolution;
            mirror.reflectionShadows = false;
            mirror.maxRenderDist = 25f;
            mirror.renderEveryNthFrame = 1;

            _mirrors.Add(go);
            return "Mirror placed; grip its edge to move it";
        }

        /// <summary>
        /// A camera mirror: a screen showing a live camera pointed back at you, flipped like a mirror.
        /// Unlike a reflection it can see the mirror-only layer, so it shows your own hat.
        /// </summary>
        public string SpawnCamera()
        {
            var loco = SnapLocomotion.Instance;
            var head = loco != null ? loco.Head : null;
            if (head == null)
                return "No player yet";
            _mirrors.RemoveAll(m => m == null);
            if (_mirrors.Count >= MaxMirrors)
                return $"Already {MaxMirrors} mirrors; remove one first";
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null)
                return "Unlit shader isn't loaded";

            var size = Sizes[Mathf.Clamp(SizeIndex, 0, Sizes.Length - 1)].Size;
            int res = Qualities[Mathf.Clamp(QualityIndex, 0, Qualities.Length - 1)].Resolution;
            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
                flatForward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            flatForward.Normalize();
            float floor = loco.PlayerPosition.y;

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "SocialSpaceModMenu.CameraMirror";
            var col = go.GetComponent(Il2CppType.Of<Collider>());
            if (col != null)
                Object.Destroy(col);
            go.transform.SetPositionAndRotation(
                head.position + flatForward * 1.5f + Vector3.up * (floor + size.y / 2f + 0.1f - head.position.y),
                Quaternion.LookRotation(flatForward, Vector3.up));
            go.transform.localScale = new Vector3(size.x, size.y, 1f);

            var rt = new RenderTexture(Mathf.RoundToInt(res * size.x / size.y), res, 24)
            {
                name = "SocialSpaceModMenu.CameraMirror",
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1,
            };
            rt.Create();
            var mat = new Material(unlit);
            mat.SetTexture("_BaseMap", rt);
            mat.mainTexture = rt;
            mat.mainTextureScale = new Vector2(-1f, 1f); // mirror-flip
            mat.mainTextureOffset = new Vector2(1f, 0f);
            var renderer = go.GetComponent(Il2CppType.Of<Renderer>()).Cast<Renderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // the quad is seen from its -Z side; the camera sits just off that face looking back out
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(go.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, -0.05f);
            camGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var cam = camGo.AddComponent(Il2CppType.Of<Camera>()).Cast<Camera>();
            cam.targetTexture = rt;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.fieldOfView = 2f * Mathf.Atan(size.y / 2f / 1.5f) * Mathf.Rad2Deg; // frames you at ~1.5 m
            cam.nearClipPlane = 0.06f;
            cam.farClipPlane = 60f;
            cam.cullingMask = ~0;
            // like the game's own mirror cameras: never rendered by URP on its own (that would happen inside
            // the VR frame, with XR on), only on request from Update, outside any camera's rendering
            cam.enabled = false;
            var extra = camGo.AddComponent(Il2CppType.Of<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>())
                .Cast<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            extra.allowXRRendering = false;
            extra.renderPostProcessing = false;
            extra.renderShadows = false;

            _textures[go.Pointer] = rt;
            _cameras[go.Pointer] = cam;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashMarker));
                System.IO.File.WriteAllText(CrashMarker, "A camera mirror was starting when the game closed. Delete this file to allow camera mirrors again.");
            }
            catch (System.Exception) { }
            _renderedFrames = 0;
            _mirrors.Add(go);
            return "Camera mirror placed; grip its edge to move it";
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
            if (_textures.TryGetValue(m.Pointer, out var rt))
            {
                _textures.Remove(m.Pointer);
                _cameras.Remove(m.Pointer);
                if (rt != null)
                {
                    rt.Release();
                    Object.Destroy(rt);
                }
            }
            Object.Destroy(m); // Mirror.OnDestroy frees its cameras and textures
        }

        // grip near a mirror to pick it up; it follows the hand rigidly until released
        public void Update()
        {
            if (_mirrors.Count == 0)
                return;
            RenderCameras();
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

        // every other frame, each camera mirror renders into its texture
        private void RenderCameras()
        {
            if (_cameras.Count == 0 || (++_frame & 1) != 0 || Mirror.IsRendering)
                return;
            foreach (var pair in new List<KeyValuePair<System.IntPtr, Camera>>(_cameras))
            {
                var cam = pair.Value;
                if (cam == null || !_textures.TryGetValue(pair.Key, out var rt) || rt == null)
                {
                    _cameras.Remove(pair.Key);
                    continue;
                }
                _request.destination = rt;
                _request.mipLevel = 0;
                _request.slice = 0;
                _request.face = CubemapFace.Unknown;
                if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, _request))
                {
                    _cameras.Remove(pair.Key);
                    MelonLoader.Melon<ModMenu>.Logger.Warning("Camera mirror: render requests aren't supported here");
                    continue;
                }
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, _request);
                if (++_renderedFrames == SafeFrames)
                    ClearCrashMarker(); // it renders fine on this machine
            }
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
