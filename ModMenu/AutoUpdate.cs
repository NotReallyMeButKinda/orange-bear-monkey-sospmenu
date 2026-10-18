using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// The mod's half of auto updating. The actual swap happens in the SocialSpaceModMenu.Updater plugin
    /// at the next launch (the running game holds this DLL open); this side installs that plugin from
    /// the copy embedded in this DLL, owns the on/off switch, reports a finished update, and checks
    /// in the background whether a newer release is waiting.
    /// </summary>
    internal static class AutoUpdate
    {
        private const string Repo = "NotReallyMeButKinda/orange-bear-monkey-sospmenu";
        private const string PluginFile = "SocialSpaceModMenu.Updater.dll";

        private static string StateDir => Path.Combine(MelonEnvironment.UserDataDirectory, "SocialSpaceModMenu");
        private static string DisabledFlag => Path.Combine(StateDir, "no-autoupdate");
        private static string UpdatedMarker => Path.Combine(StateDir, "updated.txt");

        /// <summary>Set from the background check once a newer release than this build is found.</summary>
        public static volatile string NewerRelease;

        public static bool Enabled
        {
            get => !File.Exists(DisabledFlag);
            set
            {
                Directory.CreateDirectory(StateDir);
                if (value)
                    File.Delete(DisabledFlag);
                else
                    File.WriteAllText(DisabledFlag, "Delete this file (or use the mod menu) to turn auto update back on.");
            }
        }

        /// <summary>Puts the embedded updater plugin in Plugins if it's missing or out of date.</summary>
        public static void InstallPlugin()
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(PluginFile);
            if (res == null)
                return;
            var buffer = new MemoryStream();
            res.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();
            string dest = Path.Combine(MelonEnvironment.PluginsDirectory, PluginFile);
            if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes))
                return;
            try
            {
                Directory.CreateDirectory(MelonEnvironment.PluginsDirectory);
                File.WriteAllBytes(dest, bytes);
                Melon<ModMenu>.Logger.Msg("Installed the auto updater plugin; it runs from the next launch");
            }
            catch (IOException)
            {
                // an older updater is loaded right now; it still works, and this retries next launch
            }
        }

        /// <summary>The release tag the updater installed at this launch, if any (reported once).</summary>
        public static string TakeUpdatedTag()
        {
            if (!File.Exists(UpdatedMarker))
                return null;
            string tag = File.ReadAllText(UpdatedMarker).Trim();
            File.Delete(UpdatedMarker);
            return tag;
        }

        public static void CheckInBackground(string currentVersion)
        {
            Version current = Parse(currentVersion);
            Task.Run(() =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("social-space-mod-menu");
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    string json = http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").GetAwaiter().GetResult();
                    using var doc = JsonDocument.Parse(json);
                    string tag = doc.RootElement.GetProperty("tag_name").GetString();
                    Version latest = Parse(tag);
                    if (latest != null && current != null && latest > current)
                        NewerRelease = tag;
                }
                catch (Exception e)
                {
                    Melon<ModMenu>.Logger.Msg($"Update check skipped: {e.GetBaseException().Message}");
                }
            });
        }

        private static Version Parse(string s) =>
            !string.IsNullOrEmpty(s) && Version.TryParse(s.Trim().TrimStart('v', 'V'), out var v) ? v : null;
    }
}
