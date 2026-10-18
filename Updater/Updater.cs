using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(SocialSpaceModMenu.Updater.Updater), "Social Space Mod Menu Updater", "1.0.2", "Cupri")]
[assembly: MelonGame("Smooth Cube", "Social Space")]

namespace SocialSpaceModMenu.Updater
{
    /// <summary>
    /// Keeps Mods/SocialSpaceModMenu.dll on the latest GitHub release. Plugins start before mods are
    /// loaded, so the DLL can be replaced here before the game locks it. Runs once per launch; any
    /// failure (offline, rate limited, bad download) just leaves the installed mod alone.
    /// Turned off by the mod's "Auto update" menu item, which creates the no-autoupdate file.
    /// </summary>
    public sealed class Updater : MelonPlugin
    {
        public const string Repo = "NotReallyMeButKinda/orange-bear-monkey-sospmenu";
        public const string AssetName = "SocialSpaceModMenu.dll";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public static string StateDir => Path.Combine(MelonEnvironment.UserDataDirectory, "SocialSpaceModMenu");
        public static string DisabledFlag => Path.Combine(StateDir, "no-autoupdate");
        public static string UpdatedMarker => Path.Combine(StateDir, "updated.txt");

        public override void OnPreModsLoaded()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                LoggerInstance.Warning($"Update check failed: {e.GetBaseException().Message}");
            }
        }

        private void Run()
        {
            if (File.Exists(DisabledFlag))
            {
                LoggerInstance.Msg("Auto update is off");
                return;
            }
            string target = Path.Combine(MelonEnvironment.ModsDirectory, AssetName);
            if (!File.Exists(target))
                return; // the mod was removed; don't reinstall it
            Version local = ParseVersion(ReadMelonVersion(File.ReadAllBytes(target)));

            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("social-space-mod-menu-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").GetAwaiter().GetResult();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString();
            Version remote = ParseVersion(tag);
            if (remote == null || (local != null && remote <= local))
            {
                LoggerInstance.Msg($"Mod is up to date (v{local})");
                return;
            }

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"release {tag} has no {AssetName}");
            string url = asset.GetProperty("browser_download_url").GetString();
            string digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

            LoggerInstance.Msg($"Updating mod v{local} -> {tag}");
            byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();

            // refuse anything that isn't the release we were told about
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (!actual.Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("download checksum mismatch");
            }
            if (ParseVersion(ReadMelonVersion(bytes)) != remote)
                throw new InvalidDataException("downloaded DLL doesn't carry the release version");

            Directory.CreateDirectory(StateDir);
            File.Copy(target, Path.Combine(StateDir, "previous-" + AssetName), true);
            string temp = target + ".download";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, true);
            File.WriteAllText(UpdatedMarker, tag);
            LoggerInstance.Msg($"Installed {tag}");
        }

        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            return Version.TryParse(s.Trim().TrimStart('v', 'V'), out var v) ? v : null;
        }

        /// <summary>The version string in a mod DLL's [assembly: MelonInfo(type, name, version, ...)], without loading it.</summary>
        public static string ReadMelonVersion(byte[] dll)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(handle);
                if (AttributeName(md, attr) != "MelonInfoAttribute")
                    continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1)
                    continue; // not a valid attribute blob prolog
                blob.ReadSerializedString(); // melon type
                blob.ReadSerializedString(); // name
                return blob.ReadSerializedString();
            }
            return null;
        }

        private static string AttributeName(MetadataReader md, CustomAttribute attr)
        {
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                var parent = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
                if (parent.Kind == HandleKind.TypeReference)
                    return md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
            }
            else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var type = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
                return md.GetString(md.GetTypeDefinition(type).Name);
            }
            return null;
        }
    }
}
