using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Keyboard and mouse play without a headset. The game only records whether XR is running
    /// (FlatDetector) and has no flat controls of its own, so this drives the same player body:
    ///   Mouse (hold right button, or Tab to lock the cursor)  look
    ///   WASD  move   Shift  sprint   Space  jump   Esc  release the cursor
    /// The camera is held at standing eye height since nothing is tracking it.
    /// Hands aren't simulated, so grabbing and the Creatool still need controllers.
    /// </summary>
    internal sealed class DesktopMode
    {
        public enum Setting { Auto = 0, On = 1, Off = 2 }

        public Setting Mode = Setting.Auto;
        public float Boost = 1f;
        public float EyeHeight = 1.6f;
        public float MoveSpeed = 3f;
        public float SprintMultiplier = 1.8f;
        public float JumpSpeed = 4.5f;
        public float MouseSensitivity = 0.12f; // degrees per pixel

        public bool Active { get; private set; }

        private float _yaw, _pitch;
        private bool _cursorLocked;
        private bool _wasActive;

        /// <summary>Auto mode turns on when no headset is being tracked.</summary>
        public static bool HeadsetPresent()
        {
            XRInputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid;
        }

        public void Update()
        {
            Active = Mode == Setting.On || (Mode == Setting.Auto && !HeadsetPresent());
            var loco = SnapLocomotion.Instance;
            if (!Active || loco == null || loco.body == null || loco.gravity == null || loco.Head == null)
            {
                if (_wasActive)
                    ReleaseCursor();
                _wasActive = false;
                return;
            }
            if (!_wasActive)
            {
                _yaw = loco.PlayerYaw;
                _pitch = 0f;
                _wasActive = true;
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Time.deltaTime;

            // look
            if (kb != null && kb.tabKey.wasPressedThisFrame)
                SetCursorLocked(!_cursorLocked);
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
                ReleaseCursor();
            if (mouse != null && (_cursorLocked || mouse.rightButton.isPressed))
            {
                _yaw += mouse.delta.x.ReadValue() * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - mouse.delta.y.ReadValue() * MouseSensitivity, -85f, 85f);
            }
            loco.ApplyRotation(_yaw);

            // move: the game's body sweep handles collisions, its gravity handles falling
            if (kb != null && dt > 0f)
            {
                Vector3 input = Vector3.zero;
                if (kb.wKey.isPressed) input.z += 1f;
                if (kb.sKey.isPressed) input.z -= 1f;
                if (kb.dKey.isPressed) input.x += 1f;
                if (kb.aKey.isPressed) input.x -= 1f;
                if (input.sqrMagnitude > 1f)
                    input.Normalize();
                float speed = MoveSpeed * Boost * (kb.leftShiftKey.isPressed ? SprintMultiplier : 1f);
                Vector3 move = Quaternion.Euler(0f, _yaw, 0f) * input * speed * dt;
                if (move != Vector3.zero)
                    loco.body.Move(move, true);
                if (kb.spaceKey.wasPressedThisFrame && loco.gravity.isGrounded)
                    loco.gravity.SetVerticalVelocity(JumpSpeed * Mathf.Sqrt(Boost));
            }
        }

        // After the game's own updates: nothing tracks the camera without a headset, so place it.
        public void LateUpdate()
        {
            if (!Active || HeadsetPresent())
                return; // forced on with a headset connected: leave the real head pose alone
            var head = SnapLocomotion.Instance != null ? SnapLocomotion.Instance.Head : null;
            if (head == null)
                return;
            head.localPosition = new Vector3(0f, EyeHeight, 0f);
            head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void SetCursorLocked(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        }

        private void ReleaseCursor() => SetCursorLocked(false);
    }
}
