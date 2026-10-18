using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Rope swinging. Squeeze a trigger to fire a line from that hand; it sticks to what it hits and
    /// acts as a rope (it never pushes, only stops you going further than its length), so the game's
    /// own gravity turns falls into swings. Squeeze grip on the same hand to reel in; let go of the
    /// trigger to release and keep your momentum. Works alongside the arm locomotion.
    /// </summary>
    internal sealed class WebSwing
    {
        public float MaxRange = 60f;
        public float MinRange = 1.5f;     // closer than this the trigger is left to the game (grabbing)
        public float AimRadius = 0.5f;    // forgiving aim when the exact ray misses
        public float AimPitch = -20f;     // degrees; raises the aim from the grip pose
        public float ReelSpeed = 7f;      // rope shortening, m/s
        public float ReelPull = 10f;      // extra acceleration toward the anchor while reeling
        public float MinRopeLength = 0.75f;
        public float Boost = 1f;          // shares the menu's speed/jump boost
        private const int CrosshairPoints = 24;

        public bool Active { get; private set; }

        private sealed class Hand
        {
            public XRNode Node;
            public bool TriggerWasDown;
            public bool Attached;
            public Transform AnchorTransform; // what the line stuck to, so moving objects carry it
            public bool HadTransform;
            public Vector3 AnchorLocal;
            public Vector3 AnchorWorld;
            public float Length;
            public LineRenderer Line;
            public LineRenderer Crosshair;
        }

        private readonly Hand _left = new() { Node = XRNode.LeftHand };
        private readonly Hand _right = new() { Node = XRNode.RightHand };
        private readonly Il2CppStructArray<RaycastHit> _hits = new(16);
        private Material _lineMaterial;
        private bool _materialSearched;
        private readonly WebNet _net = new();
        private Vector3? _sentLeft, _sentRight;
        private float _nextDriftCheck;

        /// <summary>Other mod users' ropes; runs whether or not local swinging is on.</summary>
        public void UpdateRemote() => _net.Update(LineMaterial());

        public void ClearRemote() => _net.ClearAll();

        // tell other mod users where our ropes are: on change, and when a carried anchor has moved
        private void PublishIfChanged(bool force)
        {
            Vector3? left = _left.Attached ? _left.AnchorWorld : null;
            Vector3? right = _right.Attached ? _right.AnchorWorld : null;
            if (!force && Same(left, _sentLeft) && Same(right, _sentRight))
                return;
            _sentLeft = left;
            _sentRight = right;
            WebNet.Publish(left, right);
        }

        private static bool Same(Vector3? a, Vector3? b) =>
            a.HasValue == b.HasValue && (!a.HasValue || (a.Value - b.Value).sqrMagnitude < 0.25f);

        public void SetActive(bool on)
        {
            Active = on;
            if (!on)
            {
                Detach(_left);
                Detach(_right);
                HideCrosshair(_left);
                HideCrosshair(_right);
            }
        }

        public void Update()
        {
            if (!Active)
                return;
            var loco = SnapLocomotion.Instance;
            if (loco == null || loco.body == null || loco.gravity == null)
                return;
            Transform head = loco.Head;
            if (head == null || head.parent == null)
                return;
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            Vector3 velocity = loco.gravity.Velocity;
            bool anyAttached = false;
            foreach (var hand in new[] { _left, _right })
            {
                XRInputDevice dev = InputDevices.GetDeviceAtXRNode(hand.Node);
                if (!dev.isValid ||
                    !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 localPos) ||
                    !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion localRot))
                {
                    Detach(hand);
                    HideCrosshair(hand);
                    continue;
                }
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float trigger);
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float grip);

                Transform space = head.parent;
                Vector3 handPos = space.TransformPoint(localPos);
                Quaternion handRot = space.rotation * localRot * Quaternion.Euler(AimPitch, 0f, 0f);

                bool triggerDown = trigger > 0.6f;
                // pointing at something grabbable: the trigger belongs to the game's grab
                if (triggerDown && !hand.TriggerWasDown && !GripGrab.HandBusy(hand.Node))
                    TryAttach(hand, loco, handPos, handRot * Vector3.forward, dev);
                else if (!triggerDown && hand.Attached)
                    Detach(hand); // momentum carries on through SnapGravity
                hand.TriggerWasDown = triggerDown;

                if (!hand.Attached)
                {
                    UpdateCrosshair(hand, loco, handPos, handRot * Vector3.forward);
                    continue;
                }
                HideCrosshair(hand);
                if (hand.AnchorTransform != null)
                    hand.AnchorWorld = hand.AnchorTransform.TransformPoint(hand.AnchorLocal);
                else if (hand.HadTransform)
                {
                    Detach(hand); // what we were stuck to was deleted
                    continue;
                }

                Vector3 toHand = handPos - hand.AnchorWorld;
                float dist = toHand.magnitude;
                if (dist < 1e-4f)
                    continue;
                Vector3 n = toHand / dist;

                if (grip > 0.5f && !GripGrab.HandBusy(hand.Node) && !(Ride.Riding && Ride.RidingHand == hand.Node))
                {
                    hand.Length = Mathf.Max(MinRopeLength, hand.Length - ReelSpeed * Boost * dt);
                    velocity -= n * ReelPull * Boost * dt;
                }

                // rope constraint: kill outward speed, then pull back any stretch
                if (dist > hand.Length)
                {
                    float outward = Vector3.Dot(velocity, n);
                    if (outward > 0f)
                        velocity -= n * outward;
                    loco.body.Move(-n * (dist - hand.Length), false);
                }

                DrawLine(hand, handPos);
                anyAttached = true;
            }

            if (anyAttached)
                loco.gravity.SetVelocity(velocity);
            if (anyAttached && Time.unscaledTime >= _nextDriftCheck)
            {
                _nextDriftCheck = Time.unscaledTime + 0.5f;
                PublishIfChanged(false);
            }
        }

        private void TryAttach(Hand hand, SnapLocomotion loco, Vector3 origin, Vector3 dir, XRInputDevice dev)
        {
            if (!Aim(loco, origin, dir, out RaycastHit hit))
                return;

            hand.Attached = true;
            hand.AnchorTransform = hit.collider != null ? hit.collider.transform : null;
            hand.HadTransform = hand.AnchorTransform != null;
            hand.AnchorWorld = hit.point;
            hand.AnchorLocal = hand.AnchorTransform != null ? hand.AnchorTransform.InverseTransformPoint(hit.point) : Vector3.zero;
            hand.Length = Vector3.Distance(origin, hit.point);
            dev.SendHapticImpulse(0u, 0.6f, 0.05f);
            WebNet.PlayAt(WebNet.FireClip(), origin, 0.6f);
            PublishIfChanged(true);
        }

        private void Detach(Hand hand)
        {
            bool was = hand.Attached;
            hand.Attached = false;
            hand.AnchorTransform = null;
            if (hand.Line != null)
                hand.Line.enabled = false;
            if (was)
            {
                var loco = SnapLocomotion.Instance;
                if (loco != null && loco.Head != null)
                    WebNet.PlayAt(WebNet.ReleaseClip(), loco.Head.position, 0.35f);
                PublishIfChanged(true);
            }
        }

        // where a line fired now would stick; nothing within MinRange (that's left to the game's grab)
        private bool Aim(SnapLocomotion loco, Vector3 origin, Vector3 dir, out RaycastHit hit) =>
            (Cast(loco, origin, dir, 0f, out hit) || Cast(loco, origin, dir, AimRadius, out hit)) && hit.distance >= MinRange;

        // Raycast (radius 0) or sphere cast via a zero-length capsule (Physics.SphereCast is stripped).
        private bool Cast(SnapLocomotion loco, Vector3 origin, Vector3 dir, float radius, out RaycastHit best)
        {
            best = default;
            int mask = loco.body.collisionLayers.value;
            int n = radius <= 0f
                ? Physics.RaycastNonAlloc(origin, dir, _hits, MaxRange, mask, QueryTriggerInteraction.Ignore)
                : Physics.CapsuleCastNonAlloc(origin, origin, radius, dir, _hits, MaxRange, mask, QueryTriggerInteraction.Ignore);
            Transform self = loco.Origin;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = _hits[i];
                if (h.distance <= 0f && h.point == Vector3.zero)
                    continue;
                Collider col = h.collider;
                if (col == null || loco.body.IsSelf(col) || (self != null && col.transform.IsChildOf(self)))
                    continue;
                if (!found || h.distance < best.distance)
                {
                    best = h;
                    found = true;
                }
            }
            return found;
        }

        // ---- visuals

        private void DrawLine(Hand hand, Vector3 handPos)
        {
            if (hand.Line == null)
                hand.Line = MakeLine(hand.Node == XRNode.LeftHand ? "Left" : "Right");
            hand.Line.enabled = true;
            hand.Line.SetPosition(0, handPos);
            hand.Line.SetPosition(1, hand.AnchorWorld);
        }

        // a ring lying on the surface the line would stick to, sized to stay readable at range
        private void UpdateCrosshair(Hand hand, SnapLocomotion loco, Vector3 origin, Vector3 dir)
        {
            if (GripGrab.HandBusy(hand.Node) || !Aim(loco, origin, dir, out RaycastHit hit))
            {
                HideCrosshair(hand);
                return;
            }
            if (hand.Crosshair == null)
                hand.Crosshair = MakeCrosshair(hand.Node == XRNode.LeftHand ? "Left" : "Right");
            Vector3 normal = hit.normal.sqrMagnitude > 1e-4f ? hit.normal.normalized : -dir;
            Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(normal, u);
            Vector3 centre = hit.point + normal * 0.02f;
            float radius = Mathf.Clamp(hit.distance * 0.012f, 0.04f, 0.5f);
            for (int i = 0; i < CrosshairPoints; i++)
            {
                float a = i * Mathf.PI * 2f / CrosshairPoints;
                hand.Crosshair.SetPosition(i, centre + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
            }
            float width = radius * 0.2f;
            hand.Crosshair.startWidth = width;
            hand.Crosshair.endWidth = width;
            hand.Crosshair.enabled = true;
        }

        private static void HideCrosshair(Hand hand)
        {
            if (hand.Crosshair != null)
                hand.Crosshair.enabled = false;
        }

        private LineRenderer MakeCrosshair(string side)
        {
            var line = MakeLine("Crosshair" + side);
            line.positionCount = CrosshairPoints;
            line.loop = true;
            line.numCapVertices = 0;
            line.startColor = new Color(1f, 1f, 1f, 0.85f);
            line.endColor = new Color(1f, 1f, 1f, 0.85f);
            return line;
        }

        private LineRenderer MakeLine(string side)
        {
            var go = new GameObject($"SocialSpaceModMenu.Web{side}");
            Object.DontDestroyOnLoad(go);
            var line = go.AddComponent(Il2CppType.Of<LineRenderer>()).Cast<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.012f;
            line.endWidth = 0.006f;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            var mat = LineMaterial();
            if (mat != null)
                line.sharedMaterial = mat;
            line.startColor = new Color(0.95f, 0.95f, 0.95f, 0.9f);
            line.endColor = new Color(0.85f, 0.85f, 0.9f, 0.8f);
            return line;
        }

        // Copy the material of a line the game already draws (its XR pointer ray) so it works in its
        // render pipeline; fall back to URP's unlit shader.
        private Material LineMaterial()
        {
            if (_lineMaterial != null || _materialSearched)
                return _lineMaterial;
            _materialSearched = true;
            try
            {
                var existing = Object.FindAnyObjectByType(Il2CppType.Of<LineRenderer>(), FindObjectsInactive.Include);
                var lr = existing != null ? existing.TryCast<LineRenderer>() : null;
                if (lr != null && lr.sharedMaterial != null)
                    _lineMaterial = new Material(lr.sharedMaterial);
                else
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader != null)
                        _lineMaterial = new Material(shader);
                }
                if (_lineMaterial != null)
                {
                    _lineMaterial.color = Color.white;
                    _lineMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
            }
            catch (Exception e)
            {
                MelonLoader.Melon<ModMenu>.Logger.Warning($"Web line material: {e.Message}");
            }
            return _lineMaterial;
        }
    }
}
