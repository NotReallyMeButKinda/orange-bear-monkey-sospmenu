using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSharp;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Hats, shirt / visor / hand / head colours that other mod users can see. Your choice travels as one extra key ("ssmm_cosmetic") in your
    /// player data, which the game already relays to everyone in the instance; players without the mod
    /// ignore the unknown key. When it's sent, your current player data goes along unchanged, so nothing
    /// the game set is lost. Each mod user builds the hats locally from primitives on the wearer's head,
    /// tints the wearer's body renderers for the shirt (a property block, so the game's materials are
    /// untouched), and does the same for the visor, the hands and the head's skin.
    /// </summary>
    internal sealed class Cosmetics
    {
        public const string Key = "ssmm_cosmetic";
        private const float PublishInterval = 3f;
        private const float RescanInterval = 1f;
        private const float ResendAfter = 30f; // if the server never echoed it back, try again

        public enum Hat { None = 0, TopHat, PartyHat, Crown, Halo, Horns, CatEars }
        public const int HatCount = 7;

        public static readonly (string Name, Color Colour)[] Colours =
        {
            ("red", new Color(0.86f, 0.15f, 0.15f)), ("orange", new Color(0.96f, 0.55f, 0.1f)),
            ("yellow", new Color(0.98f, 0.85f, 0.15f)), ("green", new Color(0.2f, 0.75f, 0.3f)),
            ("cyan", new Color(0.15f, 0.8f, 0.9f)), ("blue", new Color(0.2f, 0.4f, 0.95f)),
            ("purple", new Color(0.6f, 0.3f, 0.9f)), ("pink", new Color(0.95f, 0.45f, 0.75f)),
            ("white", new Color(0.95f, 0.95f, 0.95f)), ("black", new Color(0.08f, 0.08f, 0.1f)),
        };

        public const int NoColour = -1; // shirt / band "off"

        public Hat MyHat;
        public int MyColour;
        public int MyShirt = NoColour;
        public int MyHands = NoColour;
        public int MyHead = NoColour;
        public int MyVisor = NoColour;
        public float MyScale = 1f;
        public string MyShirtMaterial = ""; // a Creatool material name, "" = the avatar's own

        private struct Look
        {
            public Hat Hat;
            public int HatColour, Shirt, Visor, Hands, Head;
            public float Scale;
            public string ShirtMaterial;
            public bool Any => Hat != Hat.None || Shirt >= 0 || Visor >= 0 || Hands >= 0 || Head >= 0 ||
                !Mathf.Approximately(Scale, 1f) || !string.IsNullOrEmpty(ShirtMaterial);
        }

        private sealed class Worn
        {
            public AccountClientData Account;
            public string Value;
            public GameObject Root;
            public List<Renderer> Shirt = new();
            public List<(Transform T, Vector3 Scale)> Scaled = new();
            public List<(Renderer R, Material M)> Swapped = new();
        }

        private static List<(string Name, Material Mat)> _materials;

        /// <summary>The Creatool's texture materials (its Recolor cards), found once the Creatool menu exists.</summary>
        public static List<(string Name, Material Mat)> ShirtMaterials()
        {
            if (_materials != null && _materials.Count > 0)
                return _materials;
            _materials = new List<(string, Material)>();
            foreach (var o in Object.FindObjectsByType(Il2CppType.Of<CreatoolMenuAssets>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var assets = o.TryCast<CreatoolMenuAssets>();
                var list = assets != null ? assets.textures : null;
                if (list == null)
                    continue;
                foreach (var e in list)
                {
                    if (e.material == null)
                        continue;
                    string name = string.IsNullOrEmpty(e.name) ? e.material.name : e.name;
                    name = name.Replace("|", "/");
                    if (!_materials.Exists(m => m.Name == name))
                        _materials.Add((name, e.material));
                }
                if (_materials.Count > 0)
                    break;
            }
            return _materials;
        }

        private static Material FindMaterial(string name)
        {
            foreach (var m in ShirtMaterials())
                if (m.Name == name)
                    return m.Mat;
            return null;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private bool _loggedBody, _loggedHead, _loggedHands;

        private readonly Dictionary<IntPtr, Worn> _worn = new();
        private float _nextPublish, _nextScan;
        private string _lastSent;
        private float _lastSentAt;
        private bool _wasInInstance;
        private Shader _lit;

        public static string Label(Hat h) => h switch
        {
            Hat.TopHat => "top hat",
            Hat.PartyHat => "party hat",
            Hat.CatEars => "cat ears",
            _ => h.ToString().ToLowerInvariant(),
        };

        public static string ColourName(int index) =>
            index >= 0 && index < Colours.Length ? Colours[index].Name : "off";

        // v1 "1|hat|colour" (hats only, still read from older mod versions); v2 adds
        // "|shirt|bands|visor|scale%|shirtMaterial|hands|head" (-1 = off; older versions sent fewer fields).
        // "bands" (1.14-1.19 arm bands) is always sent as -1 now, so older versions draw none.
        public static string Encode(Hat hat, int colour, int shirt, int visor, float scale, string shirtMaterial, int hands, int head)
        {
            colour = Mathf.Clamp(colour, 0, Colours.Length - 1);
            shirt = Valid(shirt);
            visor = Valid(visor);
            hands = Valid(hands);
            head = Valid(head);
            int pct = Mathf.RoundToInt(Mathf.Clamp(scale, 0.1f, 10f) * 100f);
            shirtMaterial = (shirtMaterial ?? "").Replace("|", "/");
            if (shirt < 0 && visor < 0 && hands < 0 && head < 0 && pct == 100 && shirtMaterial.Length == 0)
                return hat == Hat.None ? "" : $"1|{(int)hat}|{colour}";
            return $"2|{(int)hat}|{colour}|{shirt}|-1|{visor}|{pct}|{shirtMaterial}|{hands}|{head}";
        }

        private static int Valid(int c) => c >= 0 && c < Colours.Length ? c : NoColour;

        private string MyValue() => Encode(MyHat, MyColour, MyShirt, MyVisor, MyScale, MyShirtMaterial, MyHands, MyHead);

        private static bool TryDecode(string value, out Look look)
        {
            look = new Look { Shirt = NoColour, Visor = NoColour, Hands = NoColour, Head = NoColour, Scale = 1f };
            if (string.IsNullOrEmpty(value))
                return false;
            var parts = value.Split('|');
            if (parts.Length < 3 || (parts[0] != "1" && parts[0] != "2") ||
                !int.TryParse(parts[1], out int h) || !int.TryParse(parts[2], out int colour))
                return false;
            look.Hat = h > 0 && h < HatCount ? (Hat)h : Hat.None;
            look.HatColour = Mathf.Clamp(colour, 0, Colours.Length - 1);
            if (parts[0] == "2" && parts.Length >= 5)
            {
                if (int.TryParse(parts[3], out int s) && s >= 0 && s < Colours.Length)
                    look.Shirt = s;
                if (parts.Length >= 6 && int.TryParse(parts[5], out int v) && v >= 0 && v < Colours.Length)
                    look.Visor = v;
                if (parts.Length >= 7 && int.TryParse(parts[6], out int pct))
                    look.Scale = Mathf.Clamp(pct / 100f, 0.1f, 10f);
                if (parts.Length >= 8)
                    look.ShirtMaterial = parts[7];
                if (parts.Length >= 9 && int.TryParse(parts[8], out int hn) && hn >= 0 && hn < Colours.Length)
                    look.Hands = hn;
                if (parts.Length >= 10 && int.TryParse(parts[9], out int hd) && hd >= 0 && hd < Colours.Length)
                    look.Head = hd;
            }
            return look.Any;
        }

        public void Update()
        {
            bool inInstance = SharpNetwork.InInstance;
            if (!inInstance && _wasInInstance)
                _lastSent = null; // new instance, new player data
            _wasInInstance = inInstance;

            if (inInstance && Time.unscaledTime >= _nextPublish)
            {
                _nextPublish = Time.unscaledTime + PublishInterval;
                Publish();
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Scan();
            }
        }

        private void Publish()
        {
            string want = MyValue();
            AccountClientData mine = ClientDataChannel.Mine();
            string have = mine != null ? mine.GetString(Key) ?? "" : null;
            if (have == want)
            {
                _lastSent = want;
                return;
            }
            if (_lastSent == want && Time.unscaledTime - _lastSentAt < ResendAfter)
                return; // sent; waiting for it to come back

            ClientDataChannel.Set(Key, want);
            _lastSent = want;
            _lastSentAt = Time.unscaledTime;
            Melon<ModMenu>.Logger.Msg($"Cosmetic sent: {(want.Length == 0 ? "none" : $"hat {Label(MyHat)} ({Colours[MyColour].Name}), shirt {ColourName(MyShirt)}, visor {ColourName(MyVisor)}, hands {ColourName(MyHands)}, head {ColourName(MyHead)}, scale {MyScale:0.##}, shirt material {(MyShirtMaterial.Length == 0 ? "own" : MyShirtMaterial)}")}");
        }

        private void Scan()
        {
            var seen = new HashSet<IntPtr>();
            var loco = SnapLocomotion.Instance;
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None))
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc == null)
                    continue;
                bool mine = acc.sharpView != null && acc.sharpView.isMine;
                seen.Add(acc.Pointer);
                // your own hat goes on a layer only mirror cameras draw, so it never blocks your view
                string value = mine ? MyValue() : acc.GetString(Key) ?? "";
                _worn.TryGetValue(acc.Pointer, out var worn);
                if (worn != null && worn.Value == value && Intact(worn))
                    continue;
                if (worn != null)
                    Remove(worn);
                _worn.Remove(acc.Pointer);
                if (!TryDecode(value, out var look))
                    continue;
                var head = PlayerColliders.FindHead(acc);
                if (head == null || (!mine && loco != null && loco.Origin != null && head.IsChildOf(loco.Origin)))
                    continue;
                worn = new Worn { Account = acc, Value = value };
                if (look.Hat != Hat.None)
                {
                    worn.Root = Build(look.Hat, Colours[look.HatColour].Colour, head);
                    if (mine)
                        SetLayer(worn.Root, MirrorOnly.Layer);
                }
                if (!string.IsNullOrEmpty(look.ShirtMaterial))
                    SwapShirtMaterial(acc, look.ShirtMaterial, worn.Swapped);
                if (look.Shirt >= 0)
                    TintBody(acc, Colours[look.Shirt].Colour, worn.Shirt);
                if (!Mathf.Approximately(look.Scale, 1f))
                    ScaleAvatar(acc, head, look.Scale, worn.Scaled);
                if (look.Visor >= 0)
                    TintVisor(head, Colours[look.Visor].Colour, worn.Shirt);
                if (look.Head >= 0)
                    TintHead(head, Colours[look.Head].Colour, worn.Shirt);
                if (look.Hands >= 0)
                    TintHands(HandVisuals(acc), Colours[look.Hands].Colour, worn.Shirt);
                _worn[acc.Pointer] = worn;
            }
            foreach (var key in new List<IntPtr>(_worn.Keys))
            {
                if (seen.Contains(key))
                    continue;
                Remove(_worn[key]);
                _worn.Remove(key);
            }
        }

        private static bool Intact(Worn w)
        {
            if (TryDecode(w.Value, out var look))
            {
                if (look.Hat != Hat.None && w.Root == null)
                    return false;
            }
            return true;
        }

        private static void Remove(Worn w)
        {
            if (w.Root != null)
                Object.Destroy(w.Root);
            foreach (var r in w.Shirt)
                if (r != null)
                    r.SetPropertyBlock(null);
            foreach (var (r, m) in w.Swapped)
                if (r != null)
                    r.sharedMaterial = m;
            foreach (var (t, s) in w.Scaled)
                if (t != null)
                    t.localScale = s;
            w.Shirt.Clear();
            w.Swapped.Clear();
            w.Scaled.Clear();
        }

        private static PlayerBodyFollow BodyOf(AccountClientData acc)
        {
            var follow = acc.GetComponentInChildren(Il2CppType.Of<PlayerBodyFollow>(), true);
            if (follow == null)
                follow = acc.GetComponentInParent(Il2CppType.Of<PlayerBodyFollow>(), true);
            return follow != null ? follow.TryCast<PlayerBodyFollow>() : null;
        }

        // the shirt mesh(es) of an avatar: named like a tee / shirt under the body
        private static List<Renderer> ShirtRenderers(AccountClientData acc)
        {
            var result = new List<Renderer>();
            var pbf = BodyOf(acc);
            if (pbf == null || pbf.bodyTransform == null)
                return result;
            foreach (var c in pbf.bodyTransform.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true))
            {
                var r = c.TryCast<Renderer>();
                if (r == null)
                    continue;
                string n = r.gameObject.name.ToLowerInvariant();
                if (n.Contains("tee") || n.Contains("shirt") || n.Contains("hoodie") || n.Contains("jacket"))
                    result.Add(r);
            }
            return result;
        }

        private static void SwapShirtMaterial(AccountClientData acc, string name, List<(Renderer, Material)> into)
        {
            var mat = FindMaterial(name);
            if (mat == null)
                return; // not loaded here (yet); the next change retries
            foreach (var r in ShirtRenderers(acc))
            {
                into.Add((r, r.sharedMaterial));
                r.sharedMaterial = mat;
            }
        }

        // a mod user at another size (you included): their head, body and hand visuals scaled about themselves.
        // Anything under the local rig is skipped, PlayerScale already scales that with the origin.
        private static void ScaleAvatar(AccountClientData acc, Transform head, float scale, List<(Transform, Vector3)> into)
        {
            var targets = new List<Transform> { head };
            var pbf = BodyOf(acc);
            if (pbf != null && pbf.bodyTransform != null && !pbf.bodyTransform.IsChildOf(head))
                targets.Add(pbf.bodyTransform);
            targets.AddRange(HandVisuals(acc));
            var origin = SnapLocomotion.Instance != null ? SnapLocomotion.Instance.Origin : null;
            foreach (var t in targets)
            {
                if (t == null || (origin != null && t.IsChildOf(origin)))
                    continue;
                into.Add((t, t.localScale));
                t.localScale *= scale;
            }
        }

        // the shirt: every renderer under the avatar's body (the torso that PlayerBodyFollow swings), tinted
        private void TintBody(AccountClientData acc, Color colour, List<Renderer> into)
        {
            var follow = acc.GetComponentInChildren(Il2CppType.Of<PlayerBodyFollow>(), true);
            if (follow == null)
                follow = acc.GetComponentInParent(Il2CppType.Of<PlayerBodyFollow>(), true);
            var pbf = follow != null ? follow.TryCast<PlayerBodyFollow>() : null;
            var body = pbf != null ? pbf.bodyTransform : null;
            if (body == null)
                return;
            var names = new List<string>();
            var all = new List<Renderer>();
            foreach (var c in body.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true))
            {
                var r = c.TryCast<Renderer>();
                if (r == null || r.transform.IsChildOf(pbf.headTransform) || r.gameObject.name.StartsWith("SocialSpaceModMenu"))
                    continue;
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<LineRenderer>() != null)
                    continue;
                all.Add(r);
            }
            // the avatar's shirt is its own mesh ("SocialTee"); "Body" is skin and stays as it is
            var shirts = all.FindAll(r =>
            {
                string n = r.gameObject.name.ToLowerInvariant();
                return n.Contains("tee") || n.Contains("shirt") || n.Contains("top") || n.Contains("hoodie") || n.Contains("jacket");
            });
            foreach (var r in shirts.Count > 0 ? shirts : all)
            {
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                block.SetColor(BaseColorId, colour);
                block.SetColor(ColorId, colour);
                r.SetPropertyBlock(block);
                into.Add(r);
                names.Add(r.gameObject.name);
            }
            if (!_loggedBody)
            {
                _loggedBody = true;
                Melon<ModMenu>.Logger.Msg($"Shirt tints body renderers: {(names.Count == 0 ? "none found" : string.Join(", ", names))}");
            }
        }

        // the visor: head renderers whose object or material is named like a visor / face screen / glass;
        // the names found are logged once so the match can be tightened
        private static readonly string[] VisorWords = { "visor", "face", "screen", "glass", "lens", "goggle", "eye" };

        private void TintVisor(Transform head, Color colour, List<Renderer> into)
        {
            var names = new List<string>();
            int tinted = 0;
            foreach (var c in head.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true))
            {
                var r = c.TryCast<Renderer>();
                if (r == null || r.gameObject.name.StartsWith("SocialSpaceModMenu"))
                    continue;
                string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "";
                names.Add(r.gameObject.name + "/" + mat);
                string n = (r.gameObject.name + " " + mat).ToLowerInvariant();
                bool match = false;
                foreach (var w in VisorWords)
                    match |= n.Contains(w);
                if (!match)
                    continue;
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                block.SetColor(BaseColorId, colour);
                block.SetColor(ColorId, colour);
                block.SetColor(EmissionId, colour * 0.6f);
                r.SetPropertyBlock(block);
                into.Add(r);
                tinted++;
            }
            if (!_loggedHead)
            {
                _loggedHead = true;
                Melon<ModMenu>.Logger.Msg($"Visor: {tinted} tinted; head renderers: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
            }
        }

        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private static void Tint(Renderer r, Color colour, List<Renderer> into)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, colour);
            block.SetColor(ColorId, colour);
            r.SetPropertyBlock(block);
            into.Add(r);
        }

        private static bool NamedLike(Renderer r, params string[] words)
        {
            string n = (r.gameObject.name + " " + (r.sharedMaterial != null ? r.sharedMaterial.name : "")).ToLowerInvariant();
            foreach (var w in words)
                if (n.Contains(w))
                    return true;
            return false;
        }

        // the head's skin: the "Head" mesh (SkinTone material), not the visor, ears, hair or hats
        private void TintHead(Transform head, Color colour, List<Renderer> into)
        {
            foreach (var c in head.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true))
            {
                var r = c.TryCast<Renderer>();
                if (r == null || r.gameObject.name.StartsWith("SocialSpaceModMenu") || NamedLike(r, "visor", "hair", "ear", "eye"))
                    continue;
                if (NamedLike(r, "head", "skin", "face"))
                    Tint(r, colour, into);
            }
        }

        // The visible hands aren't inside the avatar: they're SmoothControllerFollow objects chasing a target
        // (your controllers, or another player's synced hand points). A follower is this player's hand when
        // its target sits on one of their tracked hand / controller points. Matched by position, because
        // Gorilla locomotion swaps the local followers' targets for stand-ins.
        private static List<Transform> HandVisuals(AccountClientData acc)
        {
            const float match = 0.15f;
            var points = new List<Transform>();
            var tracker = acc.GetComponentInChildren(Il2CppType.Of<RemoteLocalTracker>(), true)?.TryCast<RemoteLocalTracker>();
            if (tracker != null)
            {
                foreach (var go in new[] { tracker.LeftHand, tracker.RightHand, tracker.LocalLeftController, tracker.LocalRightController })
                    if (go != null)
                        points.Add(go.transform);
            }
            var result = new List<Transform>();
            foreach (var c in acc.GetComponentsInChildren(Il2CppType.Of<SmoothControllerFollow>(), true))
                result.Add(c.transform);
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<SmoothControllerFollow>(), FindObjectsSortMode.None))
            {
                var f = obj.TryCast<SmoothControllerFollow>();
                if (f == null || f.target == null || result.Exists(t => t == f.transform))
                    continue;
                Vector3 at = f.target.position;
                foreach (var p in points)
                {
                    if (p != null && (p.position - at).sqrMagnitude < match * match)
                    {
                        result.Add(f.transform);
                        break;
                    }
                }
            }
            return result;
        }

        // the hands: the hand visuals' meshes, never whatever the hand is holding (the Creatool)
        private void TintHands(List<Transform> hands, Color colour, List<Renderer> into)
        {
            var names = new List<string>();
            int tinted = 0;
            foreach (var hand in hands)
            {
                if (hand == null)
                    continue;
                foreach (var c in hand.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true))
                {
                    var r = c.TryCast<Renderer>();
                    if (r == null || r.gameObject.name.StartsWith("SocialSpaceModMenu") || r.GetComponentInParent(Il2CppType.Of<Creatool>(), true) != null)
                        continue;
                    if (r.TryCast<LineRenderer>() != null || r.TryCast<ParticleSystemRenderer>() != null)
                        continue; // pointer rays and effects
                    names.Add(r.gameObject.name + "/" + (r.sharedMaterial != null ? r.sharedMaterial.name : ""));
                    Tint(r, colour, into);
                    tinted++;
                }
            }
            if (!_loggedHands)
            {
                _loggedHands = true;
                Melon<ModMenu>.Logger.Msg($"Hands: {tinted} tinted; hand renderers: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
            }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren(Il2CppType.Of<Transform>(), true))
                t.gameObject.layer = layer;
        }

        public void ClearAll()
        {
            foreach (var w in _worn.Values)
                Remove(w);
            _worn.Clear();
        }

        // ---- hats, in the head's local space (y up, z forward), sized for a ~0.2 m head

        private GameObject Build(Hat hat, Color colour, Transform head)
        {
            var root = new GameObject("SocialSpaceModMenu.Cosmetic." + hat);
            root.transform.SetParent(head, false);
            root.transform.localPosition = new Vector3(0f, 0.12f, -0.01f);
            Color dark = colour * 0.35f;
            dark.a = 1f;
            Color gold = new(1f, 0.8f, 0.2f);
            switch (hat)
            {
                case Hat.TopHat:
                    Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.0f, 0), Vector3.zero, new Vector3(0.3f, 0.008f, 0.3f), dark);
                    Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.08f, 0), Vector3.zero, new Vector3(0.19f, 0.08f, 0.19f), dark);
                    Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.025f, 0), Vector3.zero, new Vector3(0.195f, 0.015f, 0.195f), colour);
                    break;
                case Hat.PartyHat:
                    for (int i = 0; i < 6; i++) // a stacked cone
                    {
                        float t = i / 6f;
                        float w = Mathf.Lerp(0.17f, 0.03f, t);
                        Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.02f + t * 0.2f, 0), Vector3.zero,
                            new Vector3(w, 0.018f, w), i % 2 == 0 ? colour : Color.white);
                    }
                    Part(root, PrimitiveType.Sphere, new Vector3(0, 0.24f, 0), Vector3.zero, Vector3.one * 0.04f, gold);
                    break;
                case Hat.Crown:
                    Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.02f, 0), Vector3.zero, new Vector3(0.19f, 0.03f, 0.19f), gold);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI * 2f / 6f;
                        var p = new Vector3(Mathf.Sin(a) * 0.085f, 0.07f, Mathf.Cos(a) * 0.085f);
                        Part(root, PrimitiveType.Cube, p, new Vector3(0, i * 60f, 45f), new Vector3(0.035f, 0.035f, 0.012f), gold);
                        Part(root, PrimitiveType.Sphere, p + Vector3.up * 0.03f, Vector3.zero, Vector3.one * 0.022f, colour);
                    }
                    break;
                case Hat.Halo:
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * Mathf.PI * 2f / 16f;
                        Part(root, PrimitiveType.Sphere, new Vector3(Mathf.Sin(a) * 0.11f, 0.08f, Mathf.Cos(a) * 0.11f), Vector3.zero,
                            Vector3.one * 0.03f, Color.Lerp(colour, Color.white, 0.6f), true);
                    }
                    break;
                case Hat.Horns:
                    foreach (float side in new[] { -1f, 1f })
                        for (int i = 0; i < 4; i++)
                        {
                            float t = i / 4f;
                            float w = Mathf.Lerp(0.045f, 0.012f, t);
                            Part(root, PrimitiveType.Sphere, new Vector3(side * (0.07f + t * 0.04f), -0.01f + t * 0.07f, 0.03f - t * 0.02f),
                                Vector3.zero, Vector3.one * w, colour);
                        }
                    break;
                case Hat.CatEars:
                    foreach (float side in new[] { -1f, 1f })
                    {
                        Part(root, PrimitiveType.Cube, new Vector3(side * 0.065f, 0.02f, 0.02f), new Vector3(0, 0, side * -20f + 45f),
                            new Vector3(0.055f, 0.055f, 0.015f), colour);
                        Part(root, PrimitiveType.Cube, new Vector3(side * 0.065f, 0.02f, 0.028f), new Vector3(0, 0, side * -20f + 45f),
                            new Vector3(0.03f, 0.03f, 0.005f), new Color(1f, 0.7f, 0.8f));
                    }
                    break;
            }
            return root;
        }

        private void Part(GameObject root, PrimitiveType type, Vector3 pos, Vector3 euler, Vector3 scale, Color colour, bool glow = false)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent(Il2CppType.Of<Collider>());
            if (col != null)
                Object.Destroy(col); // purely visual; never in anyone's way
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localEulerAngles = euler;
            go.transform.localScale = scale;
            var r = go.GetComponent(Il2CppType.Of<Renderer>()).Cast<Renderer>();
            if (_lit == null)
                _lit = Shader.Find("Universal Render Pipeline/Lit");
            var m = _lit != null ? new Material(_lit) : new Material(r.sharedMaterial);
            m.SetColor("_BaseColor", colour);
            m.SetFloat("_Smoothness", 0.4f);
            if (glow)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", colour * 1.5f);
            }
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
