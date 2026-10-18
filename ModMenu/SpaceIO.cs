using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppNewtonsoft.Json;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Saves the current space to a JSON file and spawns a saved file's items into whichever space
    /// you are in. Both directions reuse the game's own code: SpaceBuilder.CreateObjectData /
    /// SpaceManager.GatherCircuits to capture, and SpaceManager.LoadObjectsPhase / LoadCircuitsPhase
    /// (networked SharpNetwork.Instantiate) to spawn, so everyone in the instance sees the result.
    /// Loading adds to the space; it never clears what is already there.
    /// </summary>
    internal static class SpaceIO
    {
        private const string Format = "SocialSpaceModMenu.space";
        public static readonly string Folder = Path.Combine(MelonEnvironment.UserDataDirectory, "SavedSpaces");

        public static bool Busy { get; private set; }
        private static string _status = "";

        /// <summary>Raised with each save/load status line (success, failure, progress).</summary>
        public static event Action<string> StatusChanged;

        public static string Status
        {
            get => _status;
            private set
            {
                _status = value;
                StatusChanged?.Invoke(value);
            }
        }

        public class SavedFile
        {
            public string Path;
            public string Label;
        }

        /// <summary>Renames a saved space file; returns what happened.</summary>
        public static string Rename(string path, string newName)
        {
            string name = (newName ?? "").Trim();
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5).TrimEnd();
            if (name.Length > 80)
                name = name.Substring(0, 80).TrimEnd();
            if (name.Length == 0)
                return "Rename: the name is empty";
            if (!File.Exists(path))
                return "Rename: that file is gone";
            string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), name + ".json");
            if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(path), StringComparison.Ordinal))
                return "Name unchanged";
            if (File.Exists(target) && !string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                return $"Rename: \"{name}\" already exists";
            try
            {
                var when = File.GetLastWriteTimeUtc(path);
                File.Move(path, target);
                File.SetLastWriteTimeUtc(target, when); // keep its place in the recent list
                return $"Renamed to \"{name}\"";
            }
            catch (Exception e)
            {
                Melon<ModMenu>.Logger.Warning($"Rename failed: {e.Message}");
                return "Rename failed (see log)";
            }
        }

        public static SpaceManager FindManager()
        {
            var sm = Object.FindAnyObjectByType(Il2CppType.Of<SpaceManager>());
            return sm != null ? sm.TryCast<SpaceManager>() : null;
        }

        public static List<SavedFile> ListFiles(int max)
        {
            if (!Directory.Exists(Folder))
                return new List<SavedFile>();
            return new DirectoryInfo(Folder).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max)
                .Select(f => new SavedFile { Path = f.FullName, Label = System.IO.Path.GetFileNameWithoutExtension(f.Name) })
                .ToList();
        }

        // ---- save

        public static void Save()
        {
            if (Busy)
                return;
            try
            {
                var sm = FindManager();
                if (sm == null || string.IsNullOrEmpty(sm.spaceUuid))
                {
                    Status = "Save failed: not in a space";
                    return;
                }
                var builder = sm._spaceBuilder;
                var parent = builder != null ? builder.objectsParent : null;
                if (parent == null)
                {
                    Status = "Save failed: space not built yet";
                    return;
                }

                // same capture loop as SpaceManager.PackageAndSaveRoutine
                var objects = new Il2CppSystem.Collections.Generic.List<ObjectData>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var data = builder.CreateObjectData(parent.GetChild(i), sm.manifest);
                    if (data != null)
                        objects.Add(data);
                }
                var circuits = sm.GatherCircuits();

                string objectsJson = JsonConvert.SerializeObject(objects);
                string circuitsJson = circuits != null ? JsonConvert.SerializeObject(circuits) : "[]";
                int circuitCount = circuits != null ? circuits.Count : 0;

                var sb = new StringBuilder();
                sb.Append("{\"format\":").Append(JsonSerializer.Serialize(Format));
                sb.Append(",\"version\":1");
                sb.Append(",\"spaceUuid\":").Append(JsonSerializer.Serialize(sm.spaceUuid));
                sb.Append(",\"savedAt\":").Append(JsonSerializer.Serialize(DateTime.UtcNow.ToString("o")));
                sb.Append(",\"objectCount\":").Append(objects.Count);
                sb.Append(",\"circuitCount\":").Append(circuitCount);
                sb.Append(",\"objects\":").Append(objectsJson);
                sb.Append(",\"circuits\":").Append(circuitsJson);
                sb.Append('}');

                Directory.CreateDirectory(Folder);
                string shortId = sm.spaceUuid.Length > 8 ? sm.spaceUuid.Substring(0, 8) : sm.spaceUuid;
                string file = System.IO.Path.Combine(Folder, $"space_{shortId}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(file, sb.ToString());

                Status = $"Saved {objects.Count} objects, {circuitCount} chips";
                Melon<ModMenu>.Logger.Msg($"Saved space {sm.spaceUuid} to {file}");
            }
            catch (Exception e)
            {
                Status = "Save failed (see log)";
                Melon<ModMenu>.Logger.Error($"Save failed: {e}");
            }
        }

        // ---- load

        public static void Load(string path)
        {
            if (Busy)
                return;
            try
            {
                var sm = FindManager();
                if (sm == null || sm._spaceBuilder == null || sm._spaceBuilder.objectsParent == null)
                {
                    Status = "Load failed: not in a space";
                    return;
                }

                string objectsJson, circuitsJson;
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != Format)
                    {
                        Status = "Load failed: not a saved space file";
                        return;
                    }
                    objectsJson = root.GetProperty("objects").GetRawText();
                    circuitsJson = root.TryGetProperty("circuits", out var c) ? c.GetRawText() : "[]";
                }
                RemapIds(ref objectsJson, ref circuitsJson);

                var objects = SpaceManager.ParseObjects(objectsJson);
                var circuits = SpaceManager.ParseCircuits(circuitsJson);
                MelonCoroutines.Start(LoadRoutine(sm, objects, circuits));
            }
            catch (Exception e)
            {
                Status = "Load failed (see log)";
                Melon<ModMenu>.Logger.Error($"Load failed: {e}");
            }
        }

        /// <summary>
        /// Deletes every object in the space through the game's networked delete (the Creatool's), a batch
        /// per frame. The mod's sky marker is kept so the space keeps its sky.
        /// </summary>
        public static void ClearAll()
        {
            if (Busy)
                return;
            var sm = FindManager();
            var parent = sm != null && sm._spaceBuilder != null ? sm._spaceBuilder.objectsParent : null;
            if (parent == null)
            {
                Status = "Clear failed: not in a space";
                return;
            }
            var targets = new List<GameObject>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var oi = child.GetComponent(Il2CppType.Of<ObjectInternals>())?.TryCast<ObjectInternals>();
                if (oi != null && oi.groupId != null && oi.groupId.StartsWith("ssmm-sky:"))
                    continue;
                targets.Add(child.gameObject);
            }
            MelonCoroutines.Start(ClearRoutine(sm, targets));
        }

        private static IEnumerator ClearRoutine(SpaceManager sm, List<GameObject> targets)
        {
            Busy = true;
            bool wasLoading = HideEffects(sm, true);
            int done = 0, failed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null)
                {
                    try
                    {
                        Il2CppSharp.SharpNetwork.Destroy(targets[i]);
                        done++;
                    }
                    catch (Exception e)
                    {
                        if (failed++ == 0)
                            Melon<ModMenu>.Logger.Warning($"Clear: {e.Message}");
                    }
                }
                if (i % 25 == 24)
                {
                    Status = $"Clearing... {i + 1}/{targets.Count}";
                    yield return null;
                }
            }
            yield return null;
            HideEffects(sm, wasLoading);
            Status = failed == 0 ? $"Cleared {done} objects" : $"Cleared {done} objects, {failed} refused";
            Melon<ModMenu>.Logger.Msg(Status);
            Busy = false;
        }

        // The game's spawn / delete particle effect (ObjectLifecycleEffect) only plays while the space
        // isn't loading, which is how its own space loads stay quiet. Flagging the space as loading for
        // the duration does the same for this mod's loads and clears. Returns the previous value.
        private static bool HideEffects(SpaceManager sm, bool loading)
        {
            var st = sm != null ? sm.GetComponent(Il2CppType.Of<SpaceState>())?.TryCast<SpaceState>() : null;
            if (st == null)
                return false;
            bool was = st.isLoading;
            st.isLoading = loading;
            return was;
        }

        /// <summary>Spawns objects from saved-format JSON (an array) through the same networked load path.</summary>
        public static bool SpawnObjects(string objectsJson)
        {
            if (Busy)
                return false;
            var sm = FindManager();
            if (sm == null || sm._spaceBuilder == null || sm._spaceBuilder.objectsParent == null)
                return false;
            var objects = SpaceManager.ParseObjects(objectsJson);
            var circuits = SpaceManager.ParseCircuits("[]");
            MelonCoroutines.Start(LoadRoutine(sm, objects, circuits));
            return true;
        }

        private static IEnumerator LoadRoutine(SpaceManager sm,
            Il2CppSystem.Collections.Generic.List<ObjectData> objects,
            Il2CppSystem.Collections.Generic.List<CircuitChipData> circuits)
        {
            Busy = true;
            bool wasLoading = HideEffects(sm, true);
            int objectCount = objects != null ? objects.Count : 0;
            int circuitCount = circuits != null ? circuits.Count : 0;
            Status = $"Loading {objectCount} objects...";

            // progress fields the load phases report against
            sm._progressBase = 0;
            sm._progressTotal = Math.Max(1, objectCount + circuitCount);

            if (objectCount > 0)
            {
                var phase = Pump(sm.LoadObjectsPhase(objects, objectCount));
                while (phase.MoveNext())
                    yield return phase.Current;
            }
            if (circuitCount > 0)
            {
                // circuits bind to objects by id, so they go after the objects exist
                Status = $"Loading {circuitCount} chips...";
                var phase = Pump(sm.LoadCircuitsPhase(circuits));
                while (phase.MoveNext())
                    yield return phase.Current;
            }
            try
            {
                sm._spaceBuilder.ThawLoadedBodies();
            }
            catch (Exception e)
            {
                Melon<ModMenu>.Logger.Warning($"ThawLoadedBodies: {e.Message}");
            }

            yield return null; // objects' Awake (where the effect would start) has run by now
            HideEffects(sm, wasLoading);
            Status = $"Loaded {objectCount} objects, {circuitCount} chips";
            Melon<ModMenu>.Logger.Msg(Status);
            Busy = false;
        }

        // Steps an IL2CPP coroutine from a Melon coroutine, one frame per step, descending into
        // nested coroutines. Waits (WaitForSeconds etc.) become a single frame.
        private static IEnumerator Pump(Il2CppSystem.Collections.IEnumerator e)
        {
            while (true)
            {
                bool more;
                try
                {
                    more = e.MoveNext();
                }
                catch (Exception ex)
                {
                    Melon<ModMenu>.Logger.Error($"Load step failed: {ex}");
                    Status = "Load stopped early (see log)";
                    yield break;
                }
                if (!more)
                    yield break;
                var nested = e.Current != null ? e.Current.TryCast<Il2CppSystem.Collections.IEnumerator>() : null;
                if (nested != null)
                {
                    var inner = Pump(nested);
                    while (inner.MoveNext())
                        yield return inner.Current;
                }
                else
                {
                    yield return null;
                }
            }
        }

        // Every object and chip gets a fresh id (wire / binding references follow), so one file can
        // be loaded into a space several times without clashing with what is already there.
        private static readonly Regex IdField = new("\"id\"\\s*:\\s*\"([^\"]+)\"");

        private static void RemapIds(ref string objectsJson, ref string circuitsJson)
        {
            var map = new Dictionary<string, string>();
            foreach (Match m in IdField.Matches(objectsJson + circuitsJson))
            {
                string old = m.Groups[1].Value;
                if (!map.ContainsKey(old))
                    map[old] = NewIdLike(old);
            }
            foreach (var kv in map)
            {
                objectsJson = objectsJson.Replace("\"" + kv.Key + "\"", "\"" + kv.Value + "\"");
                circuitsJson = circuitsJson.Replace("\"" + kv.Key + "\"", "\"" + kv.Value + "\"");
            }
        }

        private static string NewIdLike(string old)
        {
            if (Guid.TryParse(old, out _))
                return old.Length == 32 ? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("D");
            return old + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
