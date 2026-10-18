using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Pictures from your own image files, hung in the space for every mod user, and saved with it.
    ///
    /// A picture is a real (networked) thin board object, so it can be moved, rotated and scaled with the
    /// Creatool like anything else; its group id "ssmm-pic:1|id|frame|aspect|chunks" says which image and
    /// frame it shows. The image itself is shrunk, JPEG-encoded and split into base64 chunks stored in the
    /// group ids of tiny hidden marker cubes far below the map ("ssmm-img:1|id|index|data"). Objects and
    /// their group ids already sync to everyone and are saved, so no file ever has to be sent or hosted.
    /// Each mod user rebuilds the picture and its frame locally on top of the board; players without the
    /// mod see a plain dark panel.
    /// </summary>
    internal sealed class Pictures
    {
        public const string PicPrefix = "ssmm-pic:1|";
        public const string ChunkPrefix = "ssmm-img:1|";
        public static readonly string Folder = Path.Combine(MelonEnvironment.UserDataDirectory, "SocialSpaceModMenu", "Pictures");
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };
        private const int ChunkChars = 3000;
        private const float ScanInterval = 1f;
        private const float FrameWidth = 0.05f, FrameDepth = 0.04f;

        public enum Frame { None = 0, Black, White, Wood, Gold, Silver }
        public const int FrameCount = 6;
        public static readonly (string Name, int MaxSide, int Quality)[] Qualities =
        {
            ("low (128 px)", 128, 70), ("medium (256 px)", 256, 72), ("high (384 px)", 384, 75),
        };

        public Frame NewFrame = Frame.Wood;
        public int QualityIndex = 1;

        private sealed class Shown
        {
            public ObjectInternals Board;
            public string Id, Group;
            public Frame Frame;
            public GameObject Root;
            public Vector3 BuiltScale;
        }

        private readonly Dictionary<IntPtr, Shown> _shown = new();
        private readonly Dictionary<string, Texture2D> _textures = new();
        private readonly Dictionary<string, Dictionary<int, string>> _chunks = new();
        private readonly Dictionary<string, int> _chunkCounts = new();
        private readonly HashSet<string> _failed = new();
        private float _nextScan;
        private Shader _unlit, _lit;

        public static string Label(Frame f) => f == Frame.None ? "no frame" : f.ToString().ToLowerInvariant();

        public static List<string> Files()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                return new DirectoryInfo(Folder).GetFiles()
                    .Where(f => Extensions.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        // ---- placing

        /// <summary>Hangs an image file in front of you; returns what happened.</summary>
        public string Place(string path)
        {
            var loco = SnapLocomotion.Instance;
            var head = loco != null ? loco.Head : null;
            if (head == null)
                return "No player yet";
            if (SpaceIO.Busy)
                return "Busy, wait for the current load";

            byte[] jpg;
            int w, h;
            try
            {
                (jpg, w, h) = Shrink(File.ReadAllBytes(path), Qualities[Mathf.Clamp(QualityIndex, 0, Qualities.Length - 1)]);
            }
            catch (Exception e)
            {
                Melon<ModMenu>.Logger.Warning($"Picture '{Path.GetFileName(path)}': {e.Message}");
                return "Couldn't read that image";
            }
            string data = Convert.ToBase64String(jpg);
            int chunks = (data.Length + ChunkChars - 1) / ChunkChars;
            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            float aspect = (float)w / h;

            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
                flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            flat.Normalize();
            float height = 0.8f, width = height * aspect;
            if (width > 1.6f)
            {
                width = 1.6f;
                height = width / aspect;
            }
            Vector3 pos = head.position + flat * 1.5f;
            // the board's +Z points away from you, so its -Z face (the picture side) faces you
            Quaternion rot = Quaternion.LookRotation(flat, Vector3.up);

            var objects = new List<string>
            {
                ObjectJson(pos, rot, new Vector3(width, height, 0.02f), PicPrefix + $"{id}|{(int)NewFrame}|{F(aspect)}|{chunks}", "#1A1A1EFF"),
            };
            for (int i = 0; i < chunks; i++)
            {
                string part = data.Substring(i * ChunkChars, Math.Min(ChunkChars, data.Length - i * ChunkChars));
                objects.Add(ObjectJson(new Vector3(0f, -4000f - i * 0.05f, 0f), Quaternion.identity, Vector3.one * 0.01f,
                    ChunkPrefix + $"{id}|{i}|{part}", "#000000FF"));
            }
            if (!SpaceIO.SpawnObjects("[" + string.Join(",", objects) + "]"))
                return "Can't place pictures here (not in a space?)";
            Melon<ModMenu>.Logger.Msg($"Picture {id}: {Path.GetFileName(path)} {w}x{h}, {jpg.Length} bytes in {chunks} chunk(s)");
            return $"Placing {Path.GetFileNameWithoutExtension(path)} ({chunks} part{(chunks == 1 ? "" : "s")})";
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static string ObjectJson(Vector3 p, Quaternion r, Vector3 s, string group, string colour) =>
            "{\"id\":\"o_" + Guid.NewGuid().ToString("N").Substring(0, 12) + "\",\"n\":\"Cube\"," +
            $"\"p\":[{F(p.x)},{F(p.y)},{F(p.z)}],\"r\":[{F(r.x)},{F(r.y)},{F(r.z)},{F(r.w)}],\"s\":[{F(s.x)},{F(s.y)},{F(s.z)}]," +
            "\"obs\":{\"ObjectConfigurables\":{\"tg\":\"primitive\",\"md\":2,\"gr\":false,\"cl\":false,\"cs\":false,\"rs\":false}," +
            "\"ObjectInternals\":{\"m\":\"Sand\",\"c\":\"" + colour + "\",\"tl\":1.0,\"g\":\"" + group + "\"}}}";

        // decode, scale to fit the quality's longest side, re-encode as JPEG
        private static (byte[] Jpg, int W, int H) Shrink(byte[] file, (string Name, int MaxSide, int Quality) q)
        {
            var src = new Texture2D(2, 2);
            try
            {
                if (!ImageConversion.LoadImage(src, file))
                    throw new InvalidDataException("not a PNG or JPEG");
                float k = Mathf.Min(1f, (float)q.MaxSide / Mathf.Max(src.width, src.height));
                int w = Mathf.Max(8, Mathf.RoundToInt(src.width * k)), h = Mathf.Max(8, Mathf.RoundToInt(src.height * k));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(src, rt);
                    RenderTexture.active = rt;
                    var small = new Texture2D(w, h, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    small.Apply();
                    byte[] jpg = ImageConversion.EncodeToJPG(small, q.Quality);
                    Object.Destroy(small);
                    return (jpg, w, h);
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            finally
            {
                Object.Destroy(src);
            }
        }

        // ---- showing

        public void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                Scan();
            }
            foreach (var s in _shown.Values)
                Follow(s);
        }

        private void Scan()
        {
            var sm = SpaceIO.FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            var seen = new HashSet<IntPtr>();
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    var oi = child.GetComponent(Il2CppType.Of<ObjectInternals>())?.TryCast<ObjectInternals>();
                    string g = oi != null ? oi.groupId : null;
                    if (string.IsNullOrEmpty(g))
                        continue;
                    if (g.StartsWith(ChunkPrefix))
                        AddChunk(g);
                    else if (g.StartsWith(PicPrefix))
                    {
                        seen.Add(oi.Pointer);
                        Board(oi, g);
                    }
                }
            }
            foreach (var key in new List<IntPtr>(_shown.Keys))
            {
                if (seen.Contains(key))
                    continue;
                if (_shown[key].Root != null)
                    Object.Destroy(_shown[key].Root);
                _shown.Remove(key);
            }
        }

        private void AddChunk(string g)
        {
            var parts = g.Substring(ChunkPrefix.Length).Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[1], out int index) || _textures.ContainsKey(parts[0]))
                return;
            if (!_chunks.TryGetValue(parts[0], out var map))
                _chunks[parts[0]] = map = new Dictionary<int, string>();
            map[index] = parts[2];
        }

        private void Board(ObjectInternals oi, string g)
        {
            var parts = g.Substring(PicPrefix.Length).Split('|');
            if (parts.Length < 4 || !int.TryParse(parts[1], out int frame) || !int.TryParse(parts[3], out int chunks))
                return;
            string id = parts[0];
            _chunkCounts[id] = chunks;
            var tex = Texture(id);
            _shown.TryGetValue(oi.Pointer, out var s);
            if (s != null && s.Group == g && s.Root != null)
                return;
            if (s?.Root != null)
                Object.Destroy(s.Root);
            if (tex == null)
            {
                _shown.Remove(oi.Pointer);
                return; // still arriving
            }
            s = new Shown { Board = oi, Id = id, Group = g, Frame = (Frame)Mathf.Clamp(frame, 0, FrameCount - 1) };
            Build(s, tex);
            _shown[oi.Pointer] = s;
        }

        private Texture2D Texture(string id)
        {
            if (_textures.TryGetValue(id, out var tex))
                return tex;
            if (_failed.Contains(id) || !_chunkCounts.TryGetValue(id, out int count) || !_chunks.TryGetValue(id, out var map) || map.Count < count)
                return null;
            try
            {
                var data = string.Concat(Enumerable.Range(0, count).Select(i => map[i]));
                tex = new Texture2D(2, 2) { name = "SocialSpaceModMenu.Picture." + id, wrapMode = TextureWrapMode.Clamp };
                if (!ImageConversion.LoadImage(tex, Convert.FromBase64String(data)))
                    throw new InvalidDataException("bad image data");
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _textures[id] = tex;
                _chunks.Remove(id);
                return tex;
            }
            catch (Exception e)
            {
                _failed.Add(id);
                Melon<ModMenu>.Logger.Warning($"Picture {id} couldn't be decoded ({e.Message}); its data may have been cut short by the server");
                return null;
            }
        }

        // the picture and frame live in their own (unscaled) root that follows the board, so the frame
        // keeps its thickness however the board is stretched
        private void Build(Shown s, Texture2D tex)
        {
            var root = new GameObject("SocialSpaceModMenu.Picture." + s.Id);
            s.Root = root;
            Vector3 size = s.Board.transform.lossyScale;
            s.BuiltScale = size;
            float w = Mathf.Abs(size.x), h = Mathf.Abs(size.y), d = Mathf.Abs(size.z);

            _unlit ??= Shader.Find("Universal Render Pipeline/Unlit");
            var pic = Part(root, PrimitiveType.Quad, new Vector3(0f, 0f, -d / 2f - 0.002f), Quaternion.identity, new Vector3(w, h, 1f));
            var m = new Material(_unlit);
            m.SetTexture("_BaseMap", tex);
            m.mainTexture = tex;
            pic.sharedMaterial = m;

            if (s.Frame != Frame.None)
            {
                Color c = s.Frame switch
                {
                    Frame.Black => new Color(0.06f, 0.06f, 0.07f),
                    Frame.White => new Color(0.92f, 0.92f, 0.9f),
                    Frame.Wood => new Color(0.42f, 0.26f, 0.13f),
                    Frame.Gold => new Color(0.95f, 0.72f, 0.25f),
                    _ => new Color(0.75f, 0.77f, 0.8f),
                };
                float fw = FrameWidth, fd = Mathf.Max(FrameDepth, d + 0.01f);
                var mat = FrameMaterial(c, s.Frame == Frame.Gold || s.Frame == Frame.Silver);
                foreach (float side in new[] { -1f, 1f })
                {
                    Part(root, PrimitiveType.Cube, new Vector3(side * (w + fw) / 2f, 0f, 0f), Quaternion.identity, new Vector3(fw, h + 2f * fw, fd)).sharedMaterial = mat;
                    Part(root, PrimitiveType.Cube, new Vector3(0f, side * (h + fw) / 2f, 0f), Quaternion.identity, new Vector3(w, fw, fd)).sharedMaterial = mat;
                }
            }
            Follow(s);
        }

        private Material FrameMaterial(Color c, bool metal)
        {
            _lit ??= Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(_lit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metal ? 0.9f : 0f);
            m.SetFloat("_Smoothness", metal ? 0.7f : 0.25f);
            return m;
        }

        private static Renderer Part(GameObject root, PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent(Il2CppType.Of<Collider>());
            if (col != null)
                Object.Destroy(col); // the board underneath is what you grab and point at
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var r = go.GetComponent(Il2CppType.Of<Renderer>()).Cast<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        private void Follow(Shown s)
        {
            if (s.Board == null || s.Root == null)
                return;
            var t = s.Board.transform;
            if ((t.lossyScale - s.BuiltScale).sqrMagnitude > 1e-6f)
            {
                // resized with the Creatool: rebuild at the new size next scan
                Object.Destroy(s.Root);
                s.Root = null;
                s.Group = null;
                return;
            }
            s.Root.transform.SetPositionAndRotation(t.position, t.rotation);
            if (s.Root.activeSelf != s.Board.gameObject.activeInHierarchy)
                s.Root.SetActive(s.Board.gameObject.activeInHierarchy);
        }

        // ---- editing

        /// <summary>Changes the frame of the picture nearest to you (synced).</summary>
        public string CycleNearestFrame()
        {
            var nearest = Nearest();
            if (nearest == null)
                return "No picture nearby";
            var parts = nearest.Group.Substring(PicPrefix.Length).Split('|');
            int frame = (int.TryParse(parts[1], out int f) ? f + 1 : 0) % FrameCount;
            parts[1] = frame.ToString();
            nearest.Board.SetGroup(PicPrefix + string.Join("|", parts));
            return "Frame: " + Label((Frame)frame);
        }

        private Shown Nearest()
        {
            var head = SnapLocomotion.Instance != null ? SnapLocomotion.Instance.Head : null;
            if (head == null)
                return null;
            Shown best = null;
            float bestD = 6f * 6f;
            foreach (var s in _shown.Values)
            {
                if (s.Board == null || s.Group == null)
                    continue;
                float d = (s.Board.transform.position - head.position).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>Deletes image data no picture uses any more (its board was deleted).</summary>
        public string RemoveUnused()
        {
            var sm = SpaceIO.FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            if (parent == null)
                return "Not in a space";
            var used = new HashSet<string>();
            var chunks = new List<(GameObject Go, string Id)>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var oi = child.GetComponent(Il2CppType.Of<ObjectInternals>())?.TryCast<ObjectInternals>();
                string g = oi != null ? oi.groupId : null;
                if (g == null)
                    continue;
                if (g.StartsWith(PicPrefix))
                    used.Add(g.Substring(PicPrefix.Length).Split('|')[0]);
                else if (g.StartsWith(ChunkPrefix))
                    chunks.Add((child.gameObject, g.Substring(ChunkPrefix.Length).Split('|')[0]));
            }
            int removed = 0;
            foreach (var (go, id) in chunks)
            {
                if (used.Contains(id))
                    continue;
                Il2CppSharp.SharpNetwork.Destroy(go);
                removed++;
            }
            return removed == 0 ? "No unused picture data" : $"Removed {removed} unused picture part(s)";
        }

        public void ClearAll()
        {
            foreach (var s in _shown.Values)
                if (s.Root != null)
                    Object.Destroy(s.Root);
            _shown.Clear();
        }
    }
}
