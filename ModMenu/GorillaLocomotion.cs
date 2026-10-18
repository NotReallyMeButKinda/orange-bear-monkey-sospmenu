// Arm-swing locomotion ported from Another-Axiom/GorillaLocomotion (Player.cs), adapted to drive
// Social Space's SnapRigidbody / SnapGravity player instead of a standalone rigidbody rig.
//
// MIT License
//
// Copyright (c) 2021 Another-Axiom
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    internal sealed class GorillaLocomotion
    {
        // tuning (upstream inspector values that feel like the original game)
        public float MaxArmLength = 1.5f;
        public float UnStickDistance = 1f;
        public float VelocityLimit = 0.4f;
        public float MaxJumpSpeed = 6.5f;
        public float JumpMultiplier = 1.1f;
        public float MinimumRaycastDistance = 0.05f;
        public float DefaultSlideFactor = 0.03f;
        public float DefaultPrecision = 0.995f;
        public float HeadRadius = 0.12f;
        public int VelocityHistorySize = 8;
        public Vector3 HandOffset = new(0f, -0.02f, 0.04f); // controller grip -> knuckles
        public float HeightScale = 0.4f; // your in-game height vs your real standing height
        public float Boost = 1f;         // speed/jump boost from the menu; 1 = upstream feel
        public bool PinHandVisuals = true; // draw the hands where they stopped, not inside the wall
        public Vector3 LeftHandOffset => new(-HandOffset.x, HandOffset.y, HandOffset.z);

        /// <summary>Raised when a hand starts touching a surface: (isLeft, contact point, hand speed m/s).</summary>
        public event Action<bool, Vector3, float> HandTapped;

        public bool Active { get; private set; }

        private SnapLocomotion _loco;
        private SnapRigidbody _body;
        private SnapGravity _gravity;
        private Behaviour _move, _jump;
        private bool _moveWasEnabled, _jumpWasEnabled;
        private Transform _offset; // XR camera offset; lowering it lowers head and hands together
        private Vector3 _offsetBase;
        private float _drop;

        private Vector3 _lastLeftHand, _lastRightHand, _lastHead, _lastPosition;
        private Vector3 _rawLeftPrev, _rawRightPrev;
        private float _lastYaw;
        private bool _wasLeftTouching, _wasRightTouching;
        private bool _initialized;

        private Vector3[] _velocityHistory;
        private int _velocityIndex;
        private Vector3 _velocityAverage;

        private readonly Il2CppStructArray<RaycastHit> _hits = new(16); // shared cast buffer

        // The visible hands are SmoothControllerFollows chasing the controller; while active they chase a
        // stand-in placed at the collided hand position instead (upstream's hand follower).
        private sealed class HandPin
        {
            public SmoothControllerFollow Follow;
            public Transform Original;
            public Transform Stand;
        }
        private HandPin _leftPin, _rightPin;
        private float _nextPinSearch;

        // ---- lifecycle

        public bool Enable()
        {
            _loco = SnapLocomotion.Instance;
            if (_loco == null || _loco.body == null || _loco.gravity == null)
                return false;
            _body = _loco.body;
            _gravity = _loco.gravity;

            // the game's stick walking and jump fight the hands, so park them; turning stays on
            _move = _loco.move;
            var jump = Object.FindAnyObjectByType(Il2CppType.Of<SnapJump>());
            _jump = jump != null ? jump.TryCast<Behaviour>() : null;
            if (_move != null)
            {
                _moveWasEnabled = _move.enabled;
                _move.enabled = false;
            }
            if (_jump != null)
            {
                _jumpWasEnabled = _jump.enabled;
                _jump.enabled = false;
            }
            _gravity.SetHorizontalVelocity(Vector3.zero);

            Active = true;
            _initialized = false;
            return true;
        }

        public void Disable()
        {
            if (!Active)
                return;
            Active = false;
            if (_move != null)
                _move.enabled = _moveWasEnabled;
            if (_jump != null)
                _jump.enabled = _jumpWasEnabled;
            if (_offset != null)
                _offset.localPosition = _offsetBase;
            _offset = null;
            ReleasePins();
        }

        // ---- per frame

        public void Update()
        {
            if (!Active)
                return;
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;
            if (_loco == null || _body == null || _gravity == null || SnapLocomotion.Instance != _loco)
            {
                // the rig was rebuilt (space change); grab the new one
                Disable();
                if (!Enable())
                    return;
            }

            Transform head = _loco.Head;
            Transform origin = _loco.Origin;
            if (head == null || origin == null || head.parent == null)
                return;
            if (!TryHand(XRNode.LeftHand, head.parent, LeftHandOffset, out Vector3 rawLeft) ||
                !TryHand(XRNode.RightHand, head.parent, HandOffset, out Vector3 rawRight))
            {
                _initialized = false; // controllers asleep or untracked
                return;
            }

            ApplyHeight(head);

            if (!_initialized || (head.position - _lastHead).sqrMagnitude > 9f)
                Initialize(head, origin, rawLeft, rawRight); // first frame or a teleport

            RotateHistoryForTurn(origin);

            Vector3 headPos = head.position;
            Vector3 left = ClampToArm(rawLeft, headPos);
            Vector3 right = ClampToArm(rawRight, headPos);
            Vector3 gravityStep = Vector3.down * 2f * 9.8f * dt * dt;

            bool leftColliding = false, rightColliding = false;
            Vector3 leftPush = Vector3.zero, rightPush = Vector3.zero;

            // a touching hand stays pinned where it touched; the body moves by the difference
            if (IterativeSphereCast(_lastLeftHand, MinimumRaycastDistance, left - _lastLeftHand + gravityStep,
                    DefaultPrecision, out Vector3 hit, true))
            {
                leftPush = (_wasLeftTouching ? _lastLeftHand : hit) - left;
                _gravity.SetVelocity(Vector3.zero);
                leftColliding = true;
            }
            if (IterativeSphereCast(_lastRightHand, MinimumRaycastDistance, right - _lastRightHand + gravityStep,
                    DefaultPrecision, out hit, true))
            {
                rightPush = (_wasRightTouching ? _lastRightHand : hit) - right;
                _gravity.SetVelocity(Vector3.zero);
                rightColliding = true;
            }

            bool bothHolding = (leftColliding || _wasLeftTouching) && (rightColliding || _wasRightTouching);
            Vector3 movement = bothHolding ? (leftPush + rightPush) / 2f : leftPush + rightPush;

            // keep the head out of walls and ceilings
            if (IterativeSphereCast(_lastHead, HeadRadius, headPos + movement - _lastHead, DefaultPrecision,
                    out hit, false))
            {
                movement = hit - _lastHead;
                Vector3 step = headPos - _lastHead + movement;
                if (Raycast(_lastHead, step, step.magnitude + HeadRadius * DefaultPrecision * 0.999f, out _))
                    movement = _lastHead - headPos;
            }

            if (movement != Vector3.zero)
                _body.Move(movement, false); // the game's capsule sweep handles the body itself

            headPos = head.position;
            _lastHead = headPos;
            left = ClampToArm(rawLeft, headPos);
            right = ClampToArm(rawRight, headPos);

            // settle each hand for next frame
            if (IterativeSphereCast(_lastLeftHand, MinimumRaycastDistance, left - _lastLeftHand, DefaultPrecision,
                    out hit, !bothHolding))
            {
                _lastLeftHand = hit;
                leftColliding = true;
            }
            else
            {
                _lastLeftHand = left;
            }
            if (IterativeSphereCast(_lastRightHand, MinimumRaycastDistance, right - _lastRightHand, DefaultPrecision,
                    out hit, !bothHolding))
            {
                _lastRightHand = hit;
                rightColliding = true;
            }
            else
            {
                _lastRightHand = right;
            }

            StoreVelocity(origin.position, dt);

            // throw yourself: release momentum when pushing off fast enough
            if ((leftColliding || rightColliding) && _velocityAverage.magnitude > VelocityLimit)
            {
                // boost scales both the push-off strength and the speed cap (2x: ~1.8x push, 2x cap)
                float maxSpeed = MaxJumpSpeed * Boost;
                float multiplier = JumpMultiplier * (1f + (Boost - 1f) * 0.6f);
                Vector3 launch = _velocityAverage.magnitude * multiplier > maxSpeed
                    ? _velocityAverage.normalized * maxSpeed
                    : multiplier * _velocityAverage;
                _gravity.SetVelocity(launch);
            }

            // a hand dragged far from where it is stuck lets go, unless something is in the way
            if (leftColliding && (left - _lastLeftHand).magnitude > UnStickDistance &&
                !SphereCast(headPos, MinimumRaycastDistance * DefaultPrecision, left - headPos,
                    (left - headPos).magnitude - MinimumRaycastDistance, out _))
            {
                _lastLeftHand = left;
                leftColliding = false;
            }
            if (rightColliding && (right - _lastRightHand).magnitude > UnStickDistance &&
                !SphereCast(headPos, MinimumRaycastDistance * DefaultPrecision, right - headPos,
                    (right - headPos).magnitude - MinimumRaycastDistance, out _))
            {
                _lastRightHand = right;
                rightColliding = false;
            }

            UpdatePins(rawLeft, rawRight);

            if (leftColliding && !_wasLeftTouching)
                HandTapped?.Invoke(true, _lastLeftHand, (rawLeft - _rawLeftPrev).magnitude / dt);
            if (rightColliding && !_wasRightTouching)
                HandTapped?.Invoke(false, _lastRightHand, (rawRight - _rawRightPrev).magnitude / dt);

            _rawLeftPrev = rawLeft;
            _rawRightPrev = rawRight;
            _wasLeftTouching = leftColliding;
            _wasRightTouching = rightColliding;
        }

        private void Initialize(Transform head, Transform origin, Vector3 rawLeft, Vector3 rawRight)
        {
            _velocityHistory = new Vector3[Math.Max(1, VelocityHistorySize)];
            _velocityIndex = 0;
            _velocityAverage = Vector3.zero;
            _lastHead = head.position;
            _lastLeftHand = _rawLeftPrev = rawLeft;
            _lastRightHand = _rawRightPrev = rawRight;
            _lastPosition = origin.position;
            _lastYaw = origin.eulerAngles.y;
            _wasLeftTouching = _wasRightTouching = false;
            _initialized = true;
        }

        // ---- hand visuals

        private void UpdatePins(Vector3 rawLeft, Vector3 rawRight)
        {
            if (!PinHandVisuals)
            {
                ReleasePins();
                return;
            }
            if (!PinAlive(_leftPin) || !PinAlive(_rightPin))
            {
                if (Time.unscaledTime < _nextPinSearch)
                    return;
                _nextPinSearch = Time.unscaledTime + 2f;
                ReleasePins();
                FindPins(rawLeft, rawRight);
            }
            Drive(_leftPin, _lastLeftHand - rawLeft);
            Drive(_rightPin, _lastRightHand - rawRight);
        }

        private static bool PinAlive(HandPin pin) =>
            pin != null && pin.Follow != null && pin.Original != null && pin.Stand != null;

        private static void Drive(HandPin pin, Vector3 correction)
        {
            if (!PinAlive(pin))
                return;
            pin.Stand.SetPositionAndRotation(pin.Original.position + correction, pin.Original.rotation);
        }

        // our hand visuals are the followers whose targets sit on our controllers
        private void FindPins(Vector3 rawLeft, Vector3 rawRight)
        {
            const float match = 0.3f;
            SmoothControllerFollow bestLeft = null, bestRight = null;
            float dl = match, dr = match;
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<SmoothControllerFollow>(), FindObjectsSortMode.None))
            {
                var f = obj.TryCast<SmoothControllerFollow>();
                if (f == null || f.target == null)
                    continue;
                Vector3 t = f.target.position;
                float l = (t - rawLeft).magnitude, r = (t - rawRight).magnitude;
                if (l < dl && l <= r) { dl = l; bestLeft = f; }
                else if (r < dr) { dr = r; bestRight = f; }
            }
            _leftPin = Hook(bestLeft, "Left");
            _rightPin = Hook(bestRight, "Right");
            if (_leftPin == null || _rightPin == null)
                MelonLoader.Melon<ModMenu>.Logger.Warning("Gorilla locomotion: couldn't find both hand visuals to pin");
        }

        private static HandPin Hook(SmoothControllerFollow follow, string side)
        {
            if (follow == null)
                return null;
            var stand = new GameObject("SocialSpaceModMenu." + side + "HandPin").transform;
            var pin = new HandPin { Follow = follow, Original = follow.target, Stand = stand };
            stand.SetPositionAndRotation(pin.Original.position, pin.Original.rotation);
            follow.target = stand;
            return pin;
        }

        private void ReleasePins()
        {
            foreach (var pin in new[] { _leftPin, _rightPin })
            {
                if (pin == null)
                    continue;
                if (pin.Follow != null && pin.Original != null)
                    pin.Follow.target = pin.Original;
                if (pin.Stand != null)
                    Object.Destroy(pin.Stand.gameObject);
            }
            _leftPin = _rightPin = null;
        }

        // ---- helpers

        // Lowers the tracking space so you stand HeightScale as tall: hands reach the floor the way they do
        // in Gorilla Tag. DynamicColliderBounds then fits the body capsule to the lower head by itself.
        private void ApplyHeight(Transform head)
        {
            Transform offset = head.parent;
            if (offset != _offset)
            {
                if (_offset != null)
                    _offset.localPosition = _offsetBase;
                _offset = offset;
                _offsetBase = offset.localPosition;
                // calibrate from your real standing height (head height inside the tracking space)
                float standing = Mathf.Max(head.localPosition.y, 1.2f);
                _drop = Mathf.Clamp(standing * (1f - HeightScale), 0f, 1.5f);
                _initialized = false; // hands jump with the drop; don't treat it as movement
            }
            // reapplied every frame in case the XR origin re-centres the offset
            offset.localPosition = _offsetBase + Vector3.down * _drop;
        }

        private static bool TryHand(XRNode node, Transform trackingSpace, Vector3 offset, out Vector3 world)
        {
            world = default;
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid ||
                !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 pos) ||
                !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion rot))
                return false;
            world = trackingSpace.TransformPoint(pos) + trackingSpace.rotation * rot * offset;
            return true;
        }

        private Vector3 ClampToArm(Vector3 hand, Vector3 head)
        {
            Vector3 d = hand - head;
            return d.magnitude < MaxArmLength ? hand : head + d.normalized * MaxArmLength;
        }

        private void StoreVelocity(Vector3 position, float dt)
        {
            _velocityIndex = (_velocityIndex + 1) % _velocityHistory.Length;
            Vector3 oldest = _velocityHistory[_velocityIndex];
            Vector3 current = (position - _lastPosition) / dt;
            _velocityAverage += (current - oldest) / _velocityHistory.Length;
            _velocityHistory[_velocityIndex] = current;
            _lastPosition = position;
        }

        // SnapTurn rotates the origin; turn the remembered velocities with it (upstream Player.Turn).
        private void RotateHistoryForTurn(Transform origin)
        {
            float yaw = origin.eulerAngles.y;
            float delta = Mathf.DeltaAngle(_lastYaw, yaw);
            _lastYaw = yaw;
            if (Mathf.Abs(delta) < 0.01f)
                return;
            var rot = Quaternion.Euler(0f, delta, 0f);
            _velocityAverage = rot * _velocityAverage;
            for (int i = 0; i < _velocityHistory.Length; i++)
                _velocityHistory[i] = rot * _velocityHistory[i];
            _lastLeftHand = RotateAbout(_lastLeftHand, _lastHead, rot);
            _lastRightHand = RotateAbout(_lastRightHand, _lastHead, rot);
        }

        private static Vector3 RotateAbout(Vector3 p, Vector3 pivot, Quaternion rot) => pivot + rot * (p - pivot);

        // Upstream's iterative sphere cast: on a hit, slide a little along the surface so two-handed
        // grabs and head-braced pushes don't stick 100%.
        private bool IterativeSphereCast(Vector3 start, float radius, Vector3 movement, float precision,
            out Vector3 end, bool singleHand)
        {
            if (CollisionsSphereCast(start, radius * precision, movement, precision, out end, out RaycastHit hit))
            {
                Vector3 first = end;
                float slip = singleHand ? 0.001f : DefaultSlideFactor;
                Vector3 slide = Vector3.ProjectOnPlane(start + movement - first, hit.normal) * slip;
                if (CollisionsSphereCast(end, radius, slide, precision * precision, out end, out _))
                    return true;
                if (CollisionsSphereCast(slide + first, radius, start + movement - (slide + first),
                        precision * precision * precision, out end, out _))
                    return true;
                end = first; // the slide went round a corner; don't take it
                return true;
            }
            // a smaller probe catches casts that began already touching a surface
            if (CollisionsSphereCast(start, radius * precision * 0.66f,
                    movement.normalized * (movement.magnitude + radius * precision * 0.34f), precision * 0.66f,
                    out end, out _))
            {
                end = start;
                return true;
            }
            end = Vector3.zero;
            return false;
        }

        // Sphere cast that keeps the sphere one radius off whatever it hits, with anti-tunnelling checks.
        private bool CollisionsSphereCast(Vector3 start, float radius, Vector3 movement, float precision,
            out Vector3 final, out RaycastHit hit)
        {
            if (SphereCast(start, radius * precision, movement, movement.magnitude + radius * (1 - precision), out hit))
            {
                final = hit.point + hit.normal * radius;
                Vector3 toFinal = final - start;
                if (SphereCast(start, radius * precision * precision, toFinal,
                        toFinal.magnitude + radius * (1 - precision * precision), out RaycastHit inner))
                {
                    final = start + toFinal.normalized * Mathf.Max(0, hit.distance - radius * (1f - precision * precision));
                    hit = inner;
                }
                else if (Raycast(start, toFinal, toFinal.magnitude + radius * precision * precision * 0.999f, out inner))
                {
                    final = start;
                    hit = inner;
                }
                return true;
            }
            if (Raycast(start, movement, movement.magnitude + radius * precision * 0.999f, out hit))
            {
                final = start;
                return true;
            }
            final = Vector3.zero;
            return false;
        }

        // Physics.SphereCast is stripped from this build; a capsule with both ends at one point is a
        // sphere. Like SphereCast, overlaps at the start are ignored, and so are the player's own colliders.
        private bool SphereCast(Vector3 origin, float radius, Vector3 direction, float distance, out RaycastHit best)
        {
            best = default;
            if (distance <= 1e-6f || direction.sqrMagnitude < 1e-12f)
                return false;
            int n = Physics.CapsuleCastNonAlloc(origin, origin, radius, direction.normalized, _hits, distance,
                _body.collisionLayers.value, QueryTriggerInteraction.Ignore);
            return Closest(n, out best);
        }

        private bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit best)
        {
            best = default;
            if (distance <= 1e-6f || direction.sqrMagnitude < 1e-12f)
                return false;
            int n = Physics.RaycastNonAlloc(origin, direction.normalized, _hits, distance,
                _body.collisionLayers.value, QueryTriggerInteraction.Ignore);
            return Closest(n, out best);
        }

        private bool Closest(int n, out RaycastHit best)
        {
            best = default;
            bool found = false;
            Transform origin = _loco.Origin;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = _hits[i];
                if (h.distance <= 0f && h.point == Vector3.zero)
                    continue; // started inside it
                Collider col = h.collider;
                if (col == null || _body.IsSelf(col) || (origin != null && col.transform.IsChildOf(origin)))
                    continue;
                if (!found || h.distance < best.distance)
                {
                    best = h;
                    found = true;
                }
            }
            return found;
        }
    }
}
