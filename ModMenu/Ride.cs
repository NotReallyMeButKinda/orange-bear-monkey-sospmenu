using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Grip near another player's head, body or hand to hold on and ride them: every frame your player is
    /// moved by however far that part of them moved, and gravity is held off. Let go of grip to drop; you
    /// keep their momentum, so you can be thrown. Only moves you; they just see you hanging on.
    /// </summary>
    internal sealed class Ride
    {
        private const float HeadReach = 0.35f;
        private const float BodyReach = 0.45f;
        private const float HandReach = 0.2f;
        private const float BodyDrop = 0.65f;   // body centre below the head
        private const float MaxStep = 2f;       // a bigger jump in one frame is a respawn/teleport: let go
        private const float RescanInterval = 1f;

        public bool Active;
        /// <summary>True while holding on to someone; other hand features stand down.</summary>
        public static bool Riding { get; private set; }
        public static XRNode RidingHand { get; private set; }

        private sealed class Target
        {
            public AccountClientData Account;
            public Transform Head;
            public Transform[] Hands;
        }

        private readonly List<Target> _targets = new();
        private float _nextScan;
        private Transform _anchor;
        private Vector3 _anchorOffset; // grab point relative to the anchor, so body grabs follow the head
        private Vector3 _lastPoint;
        private Vector3 _velocity;
        private bool _wasLeft, _wasRight;

        public void Update()
        {
            var loco = SnapLocomotion.Instance;
            if (!Active || loco == null || loco.body == null || loco.gravity == null || loco.Head == null || loco.Head.parent == null)
            {
                Let(false);
                return;
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Rescan(loco);
            }

            bool left = Grip(XRNode.LeftHand), right = Grip(XRNode.RightHand);
            bool leftDown = left && !_wasLeft, rightDown = right && !_wasRight;
            _wasLeft = left;
            _wasRight = right;

            if (Riding)
            {
                bool held = RidingHand == XRNode.LeftHand ? left : right;
                if (!held || _anchor == null)
                {
                    Let(true);
                    return;
                }
                Vector3 point = _anchor.position + _anchor.rotation * _anchorOffset;
                Vector3 step = point - _lastPoint;
                _lastPoint = point;
                if (step.magnitude > MaxStep)
                {
                    Let(false);
                    return;
                }
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                _velocity = Vector3.Lerp(_velocity, step / dt, 0.25f);
                if (step != Vector3.zero)
                    loco.ApplyPosition(loco.PlayerPosition + step);
                loco.gravity.SetVelocity(Vector3.zero);
                return;
            }

            if (leftDown && TryGrab(loco, XRNode.LeftHand))
                return;
            if (rightDown)
                TryGrab(loco, XRNode.RightHand);
        }

        private bool TryGrab(SnapLocomotion loco, XRNode node)
        {
            if (GripGrab.HandBusy(node))
                return false; // already holding an object with that hand
            if (!HandPosition(node, loco.Head.parent, out Vector3 hand))
                return false;
            Transform best = null;
            float bestDist = float.MaxValue;
            Vector3 grabPoint = default;
            foreach (var t in _targets)
            {
                if (t.Account == null || t.Head == null)
                    continue;
                Consider(t.Head, t.Head.position, HeadReach, hand, ref best, ref bestDist, ref grabPoint);
                Consider(t.Head, t.Head.position + Vector3.down * BodyDrop, BodyReach, hand, ref best, ref bestDist, ref grabPoint);
                foreach (var h in t.Hands)
                    if (h != null && h.gameObject.activeInHierarchy)
                        Consider(h, h.position, HandReach, hand, ref best, ref bestDist, ref grabPoint);
            }
            if (best == null)
                return false;
            _anchor = best;
            _anchorOffset = Quaternion.Inverse(best.rotation) * (grabPoint - best.position);
            _lastPoint = grabPoint;
            _velocity = Vector3.zero;
            RidingHand = node;
            Riding = true;
            Haptics(node, 0.6f, 0.08f);
            return true;
        }

        private static void Consider(Transform anchor, Vector3 centre, float reach, Vector3 hand,
            ref Transform best, ref float bestDist, ref Vector3 grabPoint)
        {
            float d = (hand - centre).magnitude;
            if (d > reach || d >= bestDist)
                return;
            best = anchor;
            bestDist = d;
            grabPoint = hand;
        }

        private void Let(bool keepMomentum)
        {
            if (Riding && keepMomentum)
            {
                var loco = SnapLocomotion.Instance;
                if (loco != null && loco.gravity != null)
                    loco.gravity.SetVelocity(_velocity);
            }
            Riding = false;
            _anchor = null;
        }

        private void Rescan(SnapLocomotion loco)
        {
            _targets.Clear();
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None))
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc == null || (acc.sharpView != null && acc.sharpView.isMine))
                    continue;
                if (loco.Origin != null && acc.transform.IsChildOf(loco.Origin))
                    continue;
                var head = PlayerColliders.FindHead(acc);
                if (head == null || (head.position - loco.Head.position).sqrMagnitude < 0.1f)
                    continue;
                _targets.Add(new Target { Account = acc, Head = head, Hands = PlayerColliders.FindHands(acc, loco.Origin) });
            }
        }

        private static bool Grip(XRNode node)
        {
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
        }

        private static bool HandPosition(XRNode node, Transform trackingSpace, out Vector3 world)
        {
            world = default;
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid || !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 pos))
                return false;
            world = trackingSpace.TransformPoint(pos);
            return true;
        }

        private static void Haptics(XRNode node, float amplitude, float seconds)
        {
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            if (dev.isValid)
                dev.SendHapticImpulse(0, amplitude, seconds);
        }
    }
}
