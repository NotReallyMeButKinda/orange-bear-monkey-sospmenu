using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Invisible solid colliders that follow every other player's head, body and hands, on a layer the
    /// local body collides with. Your body bumps into and can stand on other players, and the arm
    /// locomotion and web lines can push off / stick to them. Purely local: nobody else is affected.
    /// Players are found through their AccountClientData (one per connected player); the avatar's
    /// PlayerBodyFollow gives the head and the SmoothControllerFollow hand visuals give the hands.
    /// </summary>
    internal sealed class PlayerColliders
    {
        private const float HeadRadius = 0.16f;
        private const float HandRadius = 0.07f;
        private const float BodyRadius = 0.2f;
        private const float BodyLength = 1.0f;   // capsule hanging below the head
        private const float NeckGap = 0.15f;
        private const float LocalSkip = 0.35f;   // fallback: an avatar whose head is this close to ours is us
        private const float RescanInterval = 1f;

        public bool Active { get; private set; }

        private sealed class Proxy
        {
            public AccountClientData Account;
            public Transform SourceHead;
            public Transform[] SourceHands;
            public Transform Head, Body;
            public Transform[] Hands;
            public GameObject Root;
        }

        private readonly Dictionary<System.IntPtr, Proxy> _proxies = new();
        private float _nextScan;
        private int _layer = -1;

        public void SetActive(bool on)
        {
            Active = on;
            if (!on)
                Clear();
        }

        public void Update()
        {
            if (!Active)
                return;
            var loco = SnapLocomotion.Instance;
            if (loco == null || loco.body == null || loco.Head == null)
                return;
            if (_layer < 0)
                _layer = PickLayer(loco.body.collisionLayers.value);

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Rescan(loco);
            }

            var dead = new List<System.IntPtr>();
            foreach (var kv in _proxies)
            {
                var p = kv.Value;
                if (p.Account == null || p.SourceHead == null)
                {
                    dead.Add(kv.Key);
                    continue;
                }
                Vector3 head = p.SourceHead.position;
                p.Head.position = head;
                p.Body.position = head + Vector3.down * (NeckGap + BodyLength / 2f);
                for (int i = 0; i < p.Hands.Length; i++)
                {
                    var src = p.SourceHands[i];
                    bool live = src != null && src.gameObject.activeInHierarchy;
                    p.Hands[i].gameObject.SetActive(live);
                    if (live)
                        p.Hands[i].position = src.position;
                }
            }
            foreach (var key in dead)
                Remove(key);
        }

        private void Rescan(SnapLocomotion loco)
        {
            Vector3 myHead = loco.Head.position;
            var found = Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None);
            foreach (var obj in found)
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc == null || _proxies.ContainsKey(acc.Pointer))
                    continue;
                var view = acc.sharpView;
                if (view != null && view.isMine)
                    continue;
                if (loco.Origin != null && acc.transform.IsChildOf(loco.Origin))
                    continue;

                Transform head = FindHead(acc);
                if (head == null)
                    continue; // avatar not spawned yet; try again next scan
                if (view == null && (head.position - myHead).sqrMagnitude < LocalSkip * LocalSkip)
                    continue;
                var hands = FindHands(acc, loco.Origin);
                _proxies[acc.Pointer] = Create(acc, head, hands);
                Melon<ModMenu>.Logger.Msg($"Player collision: tracking {acc.DisplayName ?? acc.AccountName} " +
                    $"(head '{head.name}', {hands.Length} hand(s))");
            }
        }

        internal static Transform FindHead(AccountClientData acc)
        {
            var follow = acc.GetComponentInChildren(Il2CppType.Of<PlayerBodyFollow>(), true);
            if (follow == null)
                follow = acc.GetComponentInParent(Il2CppType.Of<PlayerBodyFollow>(), true);
            var body = follow != null ? follow.TryCast<PlayerBodyFollow>() : null;
            if (body != null && body.headTransform != null)
                return body.headTransform;
            var tracker = acc.GetComponentInChildren(Il2CppType.Of<RemoteLocalTracker>(), true);
            var t = tracker != null ? tracker.TryCast<RemoteLocalTracker>() : null;
            return t != null && t.Head != null ? t.Head.transform : null;
        }

        internal static Transform[] FindHands(AccountClientData acc, Transform myOrigin)
        {
            var hands = new List<Transform>();
            foreach (var c in acc.GetComponentsInChildren(Il2CppType.Of<SmoothControllerFollow>(), true))
            {
                if (myOrigin != null && c.transform.IsChildOf(myOrigin))
                    continue;
                hands.Add(c.transform);
            }
            if (hands.Count == 0)
            {
                var tracker = acc.GetComponentInChildren(Il2CppType.Of<RemoteLocalTracker>(), true);
                var t = tracker != null ? tracker.TryCast<RemoteLocalTracker>() : null;
                if (t != null && t.LeftHand != null) hands.Add(t.LeftHand.transform);
                if (t != null && t.RightHand != null) hands.Add(t.RightHand.transform);
            }
            return hands.ToArray();
        }

        private Proxy Create(AccountClientData acc, Transform head, Transform[] hands)
        {
            var root = new GameObject("SocialSpaceModMenu.PlayerProxy");
            var p = new Proxy
            {
                Account = acc,
                SourceHead = head,
                SourceHands = hands,
                Root = root,
                Head = Sphere(root, "Head", HeadRadius),
                Hands = new Transform[hands.Length],
            };
            for (int i = 0; i < hands.Length; i++)
                p.Hands[i] = Sphere(root, "Hand" + i, HandRadius);
            var body = Part(root, "Body");
            var capsule = body.AddComponent(Il2CppType.Of<CapsuleCollider>()).Cast<CapsuleCollider>();
            capsule.direction = 1; // Y
            capsule.radius = BodyRadius;
            capsule.height = BodyLength;
            p.Body = body.transform;
            return p;
        }
        private Transform Sphere(GameObject root, string name, float radius)
        {
            var go = Part(root, name);
            var sphere = go.AddComponent(Il2CppType.Of<SphereCollider>()).Cast<SphereCollider>();
            sphere.radius = radius;
            return go.transform;
        }

        // Each part gets a kinematic rigidbody so moving it every frame is cheap for the physics scene.
        private GameObject Part(GameObject root, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.layer = _layer;
            var rb = go.AddComponent(Il2CppType.Of<Rigidbody>()).Cast<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            return go;
        }

        // Default (0) if the body collides with it, else the lowest layer it does collide with.
        private static int PickLayer(int mask)
        {
            if ((mask & 1) != 0)
                return 0;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                    return i;
            return 0;
        }

        private void Remove(System.IntPtr key)
        {
            if (_proxies.TryGetValue(key, out var p) && p.Root != null)
                Object.Destroy(p.Root);
            _proxies.Remove(key);
        }

        private void Clear()
        {
            foreach (var key in new List<System.IntPtr>(_proxies.Keys))
                Remove(key);
        }
    }
}
