using UnityEngine;
using UnityEngine.XR;
using Il2Cpp;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Hold grip to grab whatever that hand's SnapGrabber is hovering, release grip to drop it.
    /// Uses the grabber's own TryGrab / Drop, so throwing, haptics and networking behave as normal.
    /// The game's trigger grab and B/Y drop keep working alongside it.
    /// </summary>
    internal sealed class GripGrab
    {
        private const float Press = 0.6f;
        private const float Release = 0.35f; // hysteresis so a half-relaxed grip doesn't drop things

        public bool Active { get; set; }

        private readonly bool[] _gripDown = new bool[2];
        private readonly bool[] _gripHolding = new bool[2]; // the current hold was started by grip

        public static SnapGrabber GrabberFor(XRNode node)
        {
            var all = SnapGrabber.All;
            if (all == null)
                return null;
            var want = node == XRNode.LeftHand ? SnapGrabber.Handedness.Left : SnapGrabber.Handedness.Right;
            for (int i = 0; i < all.Count; i++)
            {
                var g = all[i];
                if (g != null && g.isActiveAndEnabled && g.handedness == want)
                    return g;
            }
            return null;
        }

        /// <summary>True when that hand is hovering or holding something the game can grab.</summary>
        public static bool HandBusy(XRNode node)
        {
            var g = GrabberFor(node);
            return g != null && (g.hovered != null || g.held != null);
        }

        public void Update()
        {
            for (int i = 0; i < 2; i++)
            {
                XRNode node = i == 0 ? XRNode.LeftHand : XRNode.RightHand;
                XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
                float grip = 0f;
                if (dev.isValid)
                    dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out grip);
                bool down = _gripDown[i] ? grip > Release : grip > Press;
                bool pressed = down && !_gripDown[i];
                bool released = !down && _gripDown[i];
                _gripDown[i] = down;

                var grabber = GrabberFor(node);
                if (grabber == null)
                {
                    _gripHolding[i] = false;
                    continue;
                }
                if (_gripHolding[i] && grabber.held == null)
                    _gripHolding[i] = false; // dropped another way (B/Y, object deleted)

                if (!Active)
                    continue;
                if (pressed && grabber.held == null && grabber.hovered != null)
                {
                    grabber.TryGrab();
                    _gripHolding[i] = grabber.held != null;
                }
                else if (released && _gripHolding[i])
                {
                    grabber.Drop();
                    _gripHolding[i] = false;
                }
            }
        }
    }
}
