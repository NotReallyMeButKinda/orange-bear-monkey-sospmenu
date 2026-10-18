using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

[assembly: MelonInfo(typeof(SocialSpaceModMenu.ModMenu), "Social Space Mod Menu", "1.19.0", "Cupri")]
[assembly: MelonGame("Smooth Cube", "Social Space")]

namespace SocialSpaceModMenu
{
    /// <summary>
    /// World-space menu panel that renders inside the headset (and in flat mode).
    ///   Both thumbsticks together  show / hide the panel
    ///   Left thumbstick click      next menu item
    ///   Right thumbstick click     activate the selected item (loading asks twice)
    /// Keyboard: F1 panel, F2 nameplates, F3 next item, F4 activate.
    /// Built from 3D TextMeshPro because IL2CPP stripped GUI.Button/GUILayout from this build.
    /// </summary>
    public class ModMenu : MelonMod
    {
        private const float RefreshInterval = 1f;
        private const float ConfirmSeconds = 5f;
        private const float PanelDistance = 0.8f;
        private const float PanelDrop = 0.15f;
        private const float FollowSharpness = 6f;
        private const int MaxPlayerRows = 8;
        private const int MaxLoadEntries = 5;

        private MelonPreferences_Category _prefs;
        private MelonPreferences_Entry<bool> _showPanel, _hideNameplates;
        private MelonPreferences_Entry<bool> _gorillaMode, _tapSounds, _tapHaptics;
        private MelonPreferences_Entry<int> _boostIndex;
        private static readonly float[] Boosts = { 1f, 1.25f, 1.5f, 2f };

        // the game's own walk speed / jump height, captured before boosting so "off" restores them
        private SnapMove _boostedMove;
        private SnapJump _boostedJump;
        private float _baseMoveSpeed, _baseJumpHeight;

        private readonly GorillaLocomotion _gorilla = new();
        private readonly TapFeedback _taps = new();
        private readonly Notifications _notify = new();
        private readonly WebSwing _web = new();
        private readonly PlayerColliders _playerColliders = new();
        private readonly GripGrab _gripGrab = new();
        private MelonPreferences_Entry<bool> _webOn, _playerCollisionOn, _gripGrabOn;
        private readonly CreatoolHighlight _highlight = new();
        private readonly DesktopMode _desktop = new();
        private readonly Lighting _lighting = new();
        private readonly PostProcessing _post = new();
        private readonly FpsUnlock _fps2 = new();
        private readonly CreatoolNoclip _noclip = new();
        private readonly Ride _ride = new();
        private readonly Cosmetics _cosmetics = new();
        private readonly Mirrors _mirrors = new();
        private readonly AutoRecolor _recolor = new();
        private readonly Skybox _sky = new();
        private GameMenu _gameSettings, _gameHome;
        private readonly Performance _perf = new();
        private readonly PlayerScale _scale = new();
        private readonly Pictures _pictures = new();
        private MelonPreferences_Entry<int> _pictureFrame, _pictureQuality;
        private List<string> _pictureFiles = new();
        private MelonPreferences_Entry<int> _scaleIndex;
        private MelonPreferences_Entry<string> _shirtMaterial;
        private MelonPreferences_Entry<int> _perfMode, _perfDistance;
        private MelonPreferences_Entry<bool> _discordSpace;
        private int _homeArmed = -1;
        private bool _renamePick;     // Home tab: choosing which file to rename
        private string _renamePath;   // Home tab: typing a new name for this file
        private float _homeArmedUntil;
        private MelonPreferences_Entry<int> _recolorSource, _recolorColour;
        private MelonPreferences_Entry<bool> _discordOn;
        private MelonPreferences_Entry<string> _discordAppId;
        private DiscordRpc _discord;
        private float _nextPresence;
        private MelonPreferences_Entry<int> _mirrorSize, _mirrorQuality;
        private MelonPreferences_Entry<bool> _noclipOn, _rideOn;
        private MelonPreferences_Entry<int> _fpsCap, _hat, _hatColour, _shirt, _bands, _visor; // shirt/bands/visor: 0 = off, else colour + 1
        private string _updateNotice;
        private bool _newerNoticeShown;
        private MelonPreferences_Entry<bool> _postOn, _postVignette;
        private MelonPreferences_Entry<int> _postBloom, _postGrade, _postTone;
        private MelonPreferences_Entry<int> _lightingPreset;
        private MelonPreferences_Entry<bool> _lightingShadows;
        private MelonPreferences_Entry<bool> _highlightOn;
        private MelonPreferences_Entry<int> _desktopSetting;
        private MelonPreferences_Entry<bool> _notifyOn, _notifyChime;

        // mailbox items already seen; null until the first look, so mail that was waiting at login is not toasted
        private HashSet<string> _seenMail;
        private AccountMailboxNetworked _mailbox;

        private GameObject _panel;
        private TextMeshPro _text;
        private bool _snapPanel = true;

        private float _fps, _nextRefresh;
        private readonly List<PlayerRow> _players = new();
        private List<SpaceIO.SavedFile> _files = new();

        private readonly List<MenuItem> _items = new();
        private int _selected;
        private int _armedItem = -1;
        private float _armedUntil;

        // thumbstick chord tracking: what was held during the current press
        private bool _gestureActive, _sawLeft, _sawRight, _sawBoth;

        private string _flash;
        private float _flashUntil;

        private struct PlayerRow
        {
            public string Name;
            public float Distance;
        }

        private class MenuItem
        {
            public string Label;
            public Action Run;
            public bool Confirm;
        }

        public override void OnInitializeMelon()
        {
            try
            {
                AutoUpdate.InstallPlugin();
                string updated = AutoUpdate.TakeUpdatedTag();
                if (updated != null)
                    _updateNotice = "Updated to " + updated;
                if (AutoUpdate.Enabled)
                    AutoUpdate.CheckInBackground(Info.Version);
            }
            catch (Exception e)
            {
                LoggerInstance.Warning($"Auto update setup failed: {e.Message}");
            }
            _prefs = MelonPreferences.CreateCategory("SocialSpaceModMenu");
            _showPanel = _prefs.CreateEntry("ShowPanel", true);
            _hideNameplates = _prefs.CreateEntry("HideNameplates", false);
            _gorillaMode = _prefs.CreateEntry("GorillaLocomotion", false);
            _tapSounds = _prefs.CreateEntry("TapSounds", true);
            _tapHaptics = _prefs.CreateEntry("TapHaptics", true);
            _boostIndex = _prefs.CreateEntry("BoostIndex", 0);
            _notifyOn = _prefs.CreateEntry("Notifications", true);
            _notifyChime = _prefs.CreateEntry("NotificationChime", true);
            _webOn = _prefs.CreateEntry("WebSwinging", false);
            _playerCollisionOn = _prefs.CreateEntry("PlayerCollision", false);
            _gripGrabOn = _prefs.CreateEntry("GripToGrab", false);
            _highlightOn = _prefs.CreateEntry("CreatoolHighlights", true);
            _lightingPreset = _prefs.CreateEntry("LightingPreset", (int)Lighting.Preset.Default);
            _lightingShadows = _prefs.CreateEntry("LightingShadows", true);
            _postOn = _prefs.CreateEntry("PostProcessing", false);
            _postBloom = _prefs.CreateEntry("PostBloom", (int)PostProcessing.BloomLevel.Low);
            _postGrade = _prefs.CreateEntry("PostColor", (int)PostProcessing.Grade.Vivid);
            _postTone = _prefs.CreateEntry("PostTonemapping", (int)PostProcessing.Tone.Aces);
            _postVignette = _prefs.CreateEntry("PostVignette", true);
            _fpsCap = _prefs.CreateEntry("FpsCap", 0);
            _perfMode = _prefs.CreateEntry("BigSpaceOptimizations", (int)Performance.Mode.Auto);
            _perfDistance = _prefs.CreateEntry("BigSpaceDrawDistance", 1);
            _discordSpace = _prefs.CreateEntry("DiscordShowSpace", true);
            _noclipOn = _prefs.CreateEntry("CreatoolNoclip", false);
            _rideOn = _prefs.CreateEntry("RidePlayers", false);
            _hat = _prefs.CreateEntry("CosmeticHat", 0);
            _hatColour = _prefs.CreateEntry("CosmeticColor", 0);
            _shirt = _prefs.CreateEntry("CosmeticShirt", 0);
            _bands = _prefs.CreateEntry("CosmeticArmBands", 0);
            _visor = _prefs.CreateEntry("CosmeticVisor", 0);
            _shirtMaterial = _prefs.CreateEntry("CosmeticShirtMaterial", "");
            _scaleIndex = _prefs.CreateEntry("PlayerScale", PlayerScale.DefaultIndex);
            _pictureFrame = _prefs.CreateEntry("PictureFrame", (int)Pictures.Frame.Wood);
            _pictureQuality = _prefs.CreateEntry("PictureQuality", 1);
            _mirrorSize = _prefs.CreateEntry("MirrorSize", 1);
            _recolorSource = _prefs.CreateEntry("AutoRecolor", 0);
            _recolorColour = _prefs.CreateEntry("AutoRecolorColor", 0);
            _discordOn = _prefs.CreateEntry("DiscordPresence", true);
            _discordAppId = _prefs.CreateEntry("DiscordAppId", "",
                description: "Discord application id for Rich Presence; leave empty for the mod's own");
            _mirrorQuality = _prefs.CreateEntry("MirrorQuality", 1);
            _desktopSetting = _prefs.CreateEntry("DesktopMode", (int)DesktopMode.Setting.Auto);
            SpaceIO.StatusChanged += status =>
            {
                bool failed = status.Contains("fail") || status.Contains("stopped");
                _notify.Show(failed ? "<color=#F87171>Space file</color>" : "<color=#4ADE80>Space file</color>", status);
            };
            _gorilla.HandTapped += (left, point, speed) =>
            {
                _taps.Sounds = _tapSounds.Value;
                _taps.Haptics = _tapHaptics.Value;
                _taps.Tap(left, point, speed);
            };
            LoggerInstance.Msg($"Loaded. Saved spaces go to {SpaceIO.Folder}");
            LoggerInstance.Msg($"Custom tap sounds (.wav) go in {TapFeedback.SoundsFolder}");
        }

        public override void OnUpdate()
        {
            if (_updateNotice != null && Time.unscaledTime > 5f)
            {
                _notify.Show("<color=#4ADE80>Mod menu</color>", _updateNotice);
                _updateNotice = null;
            }
            if (!_newerNoticeShown && AutoUpdate.NewerRelease != null)
            {
                _newerNoticeShown = true;
                _notify.Show("<color=#FBBF24>Mod menu update</color>",
                    AutoUpdate.NewerRelease + " is out; restart the game to install it");
            }
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                _fps = _fps <= 0f ? 1f / dt : Mathf.Lerp(_fps, 1f / dt, 0.1f);

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                RefreshPlayers();
                ApplyNameplates();
                _files = SpaceIO.ListFiles(MaxLoadEntries);
                _pictureFiles = Pictures.Files();
                // (re)attach after spawning or a space change
                if (_gorillaMode.Value && !_gorilla.Active)
                    _gorilla.Enable();
                ApplyBoost();
                CheckMailbox();
            }
            if (_playerCollisionOn.Value != _playerColliders.Active)
                _playerColliders.SetActive(_playerCollisionOn.Value);
            if (_webOn.Value != _web.Active)
                _web.SetActive(_webOn.Value);
            _gripGrab.Active = _gripGrabOn.Value;
            RunSafely("Player collision", _playerColliders.Update, () => _playerCollisionOn.Value = false);
            RunSafely("Grip to grab", _gripGrab.Update, () => _gripGrabOn.Value = false);
            RunSafely("Web swinging", _web.Update, () => _webOn.Value = false);
            if (_highlightOn.Value != _highlight.Active)
                _highlight.SetActive(_highlightOn.Value);
            RunSafely("Creatool highlights", _highlight.Update, () => _highlightOn.Value = false);
            _desktop.Mode = (DesktopMode.Setting)Mathf.Clamp(_desktopSetting.Value, 0, 2);
            _lighting.Brightness = (Lighting.Preset)Mathf.Clamp(_lightingPreset.Value, 0, Lighting.PresetCount - 1);
            _lighting.Shadows = _lightingShadows.Value;
            RunSafely("Lighting", _lighting.Update, () =>
            {
                _lightingPreset.Value = (int)Lighting.Preset.Default;
                _lightingShadows.Value = true;
            });
            _post.Enabled = _postOn.Value;
            _post.BloomSetting = (PostProcessing.BloomLevel)Mathf.Clamp(_postBloom.Value, 0, PostProcessing.BloomCount - 1);
            _post.GradeSetting = (PostProcessing.Grade)Mathf.Clamp(_postGrade.Value, 0, PostProcessing.GradeCount - 1);
            _post.ToneSetting = (PostProcessing.Tone)Mathf.Clamp(_postTone.Value, 0, PostProcessing.ToneCount - 1);
            _post.VignetteOn = _postVignette.Value;
            RunSafely("Post processing", _post.Update, () => _postOn.Value = false);
            _fps2.Index = _fpsCap.Value;
            RunSafely("FPS unlock", _fps2.Update, () => _fpsCap.Value = 0);
            _noclip.Enabled = _noclipOn.Value;
            RunSafely("Creatool noclip", _noclip.Update, () => { _noclipOn.Value = false; _noclip.Restore(); });
            _ride.Active = _rideOn.Value;
            RunSafely("Ride players", _ride.Update, () => _rideOn.Value = false);
            _cosmetics.MyHat = (Cosmetics.Hat)Mathf.Clamp(_hat.Value, 0, Cosmetics.HatCount - 1);
            _cosmetics.MyColour = Mathf.Clamp(_hatColour.Value, 0, Cosmetics.Colours.Length - 1);
            _cosmetics.MyShirt = _shirt.Value - 1;
            _cosmetics.MyBands = _bands.Value - 1;
            _cosmetics.MyVisor = _visor.Value - 1;
            _cosmetics.MyShirtMaterial = _shirtMaterial.Value ?? "";
            _scale.Wanted = PlayerScale.Presets[Mathf.Clamp(_scaleIndex.Value, 0, PlayerScale.Presets.Length - 1)];
            RunSafely("Player scale", _scale.Update, () => { _scaleIndex.Value = PlayerScale.DefaultIndex; _scale.Reset(); });
            _cosmetics.MyScale = _scale.Applied;
            RunSafely("Cosmetics", _cosmetics.Update, () => { _hat.Value = 0; _shirt.Value = 0; _bands.Value = 0; _visor.Value = 0; _shirtMaterial.Value = ""; _cosmetics.ClearAll(); });
            _mirrors.SizeIndex = _mirrorSize.Value;
            _mirrors.QualityIndex = _mirrorQuality.Value;
            RunSafely("Mirrors", _mirrors.Update, _mirrors.RemoveAll);
            _perf.Setting = (Performance.Mode)Mathf.Clamp(_perfMode.Value, 0, Performance.ModeCount - 1);
            _perf.DrawDistance = Performance.Distances[Mathf.Clamp(_perfDistance.Value, 0, Performance.Distances.Length - 1)];
            RunSafely("Big space optimizations", _perf.Update, () => { _perf.RestoreAll(); _perfMode.Value = 0; });
            MirrorOnly.HideFromMainCamera();
            _recolor.Mode = (AutoRecolor.Source)Mathf.Clamp(_recolorSource.Value, 0, AutoRecolor.SourceCount - 1);
            _recolor.CustomColour = _recolorColour.Value;
            RunSafely("Auto recolor", _recolor.Update, () => _recolorSource.Value = 0);
            RunSafely("Sky", _sky.Update, () => { });
            _pictures.NewFrame = (Pictures.Frame)Mathf.Clamp(_pictureFrame.Value, 0, Pictures.FrameCount - 1);
            _pictures.QualityIndex = Mathf.Clamp(_pictureQuality.Value, 0, Pictures.Qualities.Length - 1);
            RunSafely("Pictures", _pictures.Update, _pictures.ClearAll);
            RunSafely("Remote webs", _web.UpdateRemote, _web.ClearRemote);
            RunSafely("Mod networking", ClientDataChannel.Flush, () => { });
            RunSafely("Discord presence", UpdatePresence, () => _discordOn.Value = false);
            _gameSettings ??= new GameMenu(Il2Cpp.MenuNavigationManager.Tab.Settings, "Mods", GameSettingsHeader, GameSettingsRows);
            _gameHome ??= new GameMenu(Il2Cpp.MenuNavigationManager.Tab.Home, "Saved spaces", GameHomeHeader, GameHomeRows);
            RunSafely("Game settings menu", _gameSettings.Update, _gameSettings.Destroy);
            RunSafely("Game home menu", _gameHome.Update, _gameHome.Destroy);
            RunSafely("Desktop mode", _desktop.Update, () => _desktopSetting.Value = (int)DesktopMode.Setting.Off);
            try
            {
                _gorilla.Update();
            }
            catch (Exception e)
            {
                _gorilla.Disable();
                _gorillaMode.Value = false;
                LoggerInstance.Error($"Gorilla locomotion stopped: {e}");
                Flash("Gorilla locomotion error (see log)");
            }
            BuildMenu();

            HandleKeyboard();
            HandleControllers();
            if (_armedItem >= 0 && Time.unscaledTime > _armedUntil)
                _armedItem = -1;
        }

        public override void OnLateUpdate()
        {
            RunSafely("Desktop mode", _desktop.LateUpdate, () => _desktopSetting.Value = (int)DesktopMode.Setting.Off);
            _notify.Enabled = _notifyOn.Value;
            _notify.Chime = _notifyChime.Value;
            _notify.LateUpdate(Head());

            if (!_showPanel.Value)
            {
                if (_panel != null && _panel.activeSelf)
                    _panel.SetActive(false);
                return;
            }
            var head = Head();
            if (head == null || !EnsurePanel())
                return;
            if (!_panel.activeSelf)
            {
                _panel.SetActive(true);
                _snapPanel = true;
            }
            FollowHead(head);
            _text.text = BuildText();
        }

        // ---- menu

        private enum Page { Main, Movement, Visuals, Players, World, Building, Spaces, Settings }

        private static readonly (Page Page, string Title, string Hint)[] Categories =
        {
            (Page.Movement, "Movement", "gorilla, boost, web, ride, noclip, desktop"),
            (Page.Visuals, "Visuals", "big space optimizations, lighting, post, FPS"),
            (Page.Players, "Players", "collision, nameplates, hats, shirts, visors"),
            (Page.World, "World", "mirrors, camera mirror"),
            (Page.Building, "Building", "pictures, auto-recolor, sky, highlights"),
            (Page.Spaces, "Spaces", "save, load, clear"),
            (Page.Settings, "Settings", "notifications, auto update, Discord status"),
        };

        private Page _page = Page.Main;
        private int _mainSelected;

        private void BuildMenu()
        {
            _items.Clear();
            if (_page != Page.Main)
                _items.Add(new MenuItem { Label = "<color=#B5B0A8><</color> Back", Run = GoBack });
            switch (_page)
            {
                case Page.Main:
                    foreach (var (page, title, hint) in Categories)
                    {
                        var target = page;
                        _items.Add(new MenuItem
                        {
                            Label = $"{title}  <size=70%><color=#B5B0A8>{hint}</color></size>  >",
                            Run = () => Open(target)
                        });
                    }
                    _items.Add(new MenuItem { Label = "Save this space to file", Run = SpaceIO.Save });
                    for (int i = 0; i < _files.Count && i < 3; i++)
                    {
                        string path = _files[i].Path;
                        _items.Add(new MenuItem { Label = "Load " + _files[i].Label, Run = () => SpaceIO.Load(path), Confirm = true });
                    }
                    if (_files.Count > 3)
                        _items.Add(new MenuItem { Label = $"<color=#B5B0A8>All {_files.Count} saved spaces</color>  >", Run = () => Open(Page.Spaces) });
                    break;
                case Page.Movement: BuildMovement(); break;
                case Page.Visuals: BuildVisuals(); break;
                case Page.Players: BuildPlayers(); break;
                case Page.World: BuildWorld(); break;
                case Page.Building: BuildBuilding(); break;
                case Page.Spaces: BuildSpaces(); break;
                case Page.Settings: BuildSettings(); break;
            }
            _selected = Mathf.Clamp(_selected, 0, _items.Count - 1);
        }

        private void Open(Page page)
        {
            _mainSelected = _selected;
            _page = page;
            _selected = 0;
            _armedItem = -1;
        }

        private void GoBack()
        {
            if (_page == Page.Main)
                return;
            _page = Page.Main;
            _selected = _mainSelected;
            _armedItem = -1;
        }

        // ---- the game's own menu: the whole mod menu in its Settings tab, spaces in its Home tab

        private string GameSettingsHeader()
        {
            string head = _page == Page.Main ? "<b><color=#D97757>Social Space Mod Menu</color></b>"
                : $"<b><color=#D97757>Mod Menu</color></b> <color=#B5B0A8>/</color> <b>{PageTitle(_page)}</b>";
            string note = Time.unscaledTime < _flashUntil ? _flash : SpaceIO.Status;
            return string.IsNullOrEmpty(note) ? head : $"{head}\n<size=70%><color=#4ADE80>{note}</color></size>";
        }

        private List<GameMenu.Row> GameSettingsRows()
        {
            var rows = new List<GameMenu.Row>();
            for (int i = 0; i < _items.Count; i++)
            {
                int index = i;
                string label = _items[i].Label;
                if (i == _armedItem)
                    label += "  <color=#F87171>click again to confirm</color>";
                rows.Add(new GameMenu.Row
                {
                    Label = label,
                    Run = () =>
                    {
                        _selected = index;
                        Activate();
                        BuildMenu(); // show the new page / value straight away
                    }
                });
            }
            return rows;
        }

        private string GameHomeHeader()
        {
            string head = _renamePick ? "<b><color=#D97757>Rename which space?</color></b>"
                : "<b><color=#D97757>Saved spaces</color></b>  <size=70%><color=#B5B0A8>Social Space Mod Menu</color></size>";
            string note = Time.unscaledTime < _flashUntil ? _flash : SpaceIO.Status;
            if (!Il2CppSharp.SharpNetwork.IsMaster)
                note = (string.IsNullOrEmpty(note) ? "" : note + "   ") + "<color=#F87171>not space master: loads may be refused</color>";
            return string.IsNullOrEmpty(note) ? head : $"{head}\n<size=70%><color=#4ADE80>{note}</color></size>";
        }

        private List<GameMenu.Row> GameHomeRows()
        {
            if (_homeArmed >= 0 && Time.unscaledTime > _homeArmedUntil)
                _homeArmed = -1;
            var rows = new List<GameMenu.Row>();
            if (_renamePath != null)
            {
                rows.Add(new GameMenu.Row
                {
                    Label = "<color=#4ADE80>Save name</color>",
                    Run = () =>
                    {
                        Flash(SpaceIO.Rename(_renamePath, _gameHome.InputText));
                        _renamePath = null;
                        _gameHome.EndInput();
                        _nextRefresh = 0f;
                    }
                });
                rows.Add(new GameMenu.Row { Label = "Cancel", Run = () => { _renamePath = null; _gameHome.EndInput(); } });
                return rows;
            }
            if (_renamePick)
            {
                rows.Add(new GameMenu.Row { Label = "<color=#B5B0A8><</color> Back", Run = () => _renamePick = false });
                for (int i = 0; i < _files.Count && rows.Count < 16; i++)
                {
                    var f = _files[i];
                    rows.Add(new GameMenu.Row
                    {
                        Label = "Rename " + f.Label,
                        Run = () =>
                        {
                            _renamePick = false;
                            if (_gameHome.BeginInput(f.Label))
                                _renamePath = f.Path;
                            else
                                Flash("Can't show a text box here (see log)");
                        }
                    });
                }
                return rows;
            }
            void Add(string label, Action run, bool confirm)
            {
                int index = rows.Count;
                rows.Add(new GameMenu.Row
                {
                    Label = index == _homeArmed ? label + "  <color=#F87171>click again to confirm</color>" : label,
                    Run = () =>
                    {
                        if (SpaceIO.Busy)
                        {
                            Flash("Busy, wait for the current load");
                            return;
                        }
                        if (confirm && _homeArmed != index)
                        {
                            _homeArmed = index;
                            _homeArmedUntil = Time.unscaledTime + ConfirmSeconds;
                            return;
                        }
                        _homeArmed = -1;
                        run();
                        _nextRefresh = 0f;
                    }
                });
            }
            Add("Save this space to file", SpaceIO.Save, false);
            Add("<color=#F87171>Clear all objects</color>", SpaceIO.ClearAll, true);
            Add("Teleport to 0, 0, 0", () => Flash(TeleportHome()), false);
            if (_files.Count > 0)
                Add("Rename a saved space...", () => _renamePick = true, false);
            if (_files.Count == 0)
                rows.Add(new GameMenu.Row { Label = "<color=#B5B0A8>(no saved spaces yet)</color>", Run = null });
            for (int i = 0; i < _files.Count && rows.Count < 16; i++)
            {
                string path = _files[i].Path;
                Add("Load " + _files[i].Label, () => SpaceIO.Load(path), true);
            }
            return rows;
        }

        private void CycleShirtMaterial()
        {
            var mats = Cosmetics.ShirtMaterials();
            if (mats.Count == 0)
            {
                Flash("Materials load with the Creatool; open it once, then try again");
                return;
            }
            int at = mats.FindIndex(m => m.Name == _shirtMaterial.Value); // -1 = avatar's own
            _shirtMaterial.Value = at + 1 < mats.Count ? mats[at + 1].Name : "";
            _prefs.SaveToFile(false);
        }

        private static string TeleportHome()
        {
            if (SnapLocomotion.Instance == null)
                return "No player yet";
            return SnapLocomotion.SetPosition(Vector3.zero) ? "Teleported to 0, 0, 0" : "Teleport refused";
        }

        private static string PageTitle(Page page)
        {
            foreach (var c in Categories)
                if (c.Page == page)
                    return c.Title;
            return "";
        }

        private void BuildMovement()
        {
            _items.Add(new MenuItem { Label = "Teleport to 0, 0, 0", Run = () => Flash(TeleportHome()) });
            _items.Add(new MenuItem
            {
                Label = "Player scale: " + PlayerScale.Label(PlayerScale.Presets[Mathf.Clamp(_scaleIndex.Value, 0, PlayerScale.Presets.Length - 1)]) +
                    "  <size=70%><color=#B5B0A8>everyone sees your height; mod users see your avatar resized</color></size>",
                Run = () => CyclePref(_scaleIndex, PlayerScale.Presets.Length)
            });
            _items.Add(new MenuItem
            {
                Label = "Gorilla locomotion: " + (_gorilla.Active ? "on" : _gorillaMode.Value ? "waiting for body" : "off"),
                Run = ToggleGorilla
            });
            if (_gorillaMode.Value)
            {
                _items.Add(new MenuItem
                {
                    Label = "   Tap sounds: " + (_tapSounds.Value ? "on" : "off"),
                    Run = () => TogglePref(_tapSounds)
                });
                _items.Add(new MenuItem
                {
                    Label = "   Tap vibration: " + (_tapHaptics.Value ? "on" : "off"),
                    Run = () => TogglePref(_tapHaptics)
                });
            }
            float boost = CurrentBoost();
            _items.Add(new MenuItem
            {
                Label = "Speed/jump boost: " + (boost <= 1f ? "off" : $"{boost:0.##}x"),
                Run = CycleBoost
            });
            _items.Add(new MenuItem
            {
                Label = "Web swinging: " + (_webOn.Value ? "on (trigger fire, grip reel)" : "off"),
                Run = () => { TogglePref(_webOn); Flash(_webOn.Value ? "Point and squeeze trigger to swing" : "Web swinging off"); }
            });
            _items.Add(new MenuItem
            {
                Label = "Grip to grab: " + (_gripGrabOn.Value ? "on" : "off"),
                Run = () => TogglePref(_gripGrabOn)
            });
            _items.Add(new MenuItem
            {
                Label = "Ride players (grip on them): " + (_rideOn.Value ? (Ride.Riding ? "on (riding)" : "on") : "off"),
                Run = () => TogglePref(_rideOn)
            });
            _items.Add(new MenuItem
            {
                Label = "Creatool noclip (while flying): " + (_noclipOn.Value ? (_noclip.Noclipping ? "on (active)" : "on") : "off"),
                Run = () => TogglePref(_noclipOn)
            });
            string desktopLabel = (DesktopMode.Setting)_desktopSetting.Value switch
            {
                DesktopMode.Setting.On => "on",
                DesktopMode.Setting.Off => "off",
                _ => _desktop.Active ? "auto (active)" : "auto",
            };
            _items.Add(new MenuItem
            {
                Label = "Desktop mode: " + desktopLabel,
                Run = () =>
                {
                    _desktopSetting.Value = (_desktopSetting.Value + 1) % 3;
                    _prefs.SaveToFile(false);
                    Flash("Desktop: WASD move, Shift sprint, Space jump, right mouse / Tab to look");
                }
            });
        }

        private void BuildVisuals()
        {
            var perfMode = (Performance.Mode)Mathf.Clamp(_perfMode.Value, 0, Performance.ModeCount - 1);
            _items.Add(new MenuItem
            {
                Label = "Big space optimizations: " + Performance.Label(perfMode) +
                    (_perf.Active ? $"  <size=70%><color=#4ADE80>{_perf.Hidden}/{_perf.Objects} far objects hidden</color></size>" : ""),
                Run = () => CyclePref(_perfMode, Performance.ModeCount)
            });
            if (perfMode != Performance.Mode.Off)
            {
                _items.Add(new MenuItem
                {
                    Label = $"   Draw distance: {Performance.Distances[Mathf.Clamp(_perfDistance.Value, 0, Performance.Distances.Length - 1)]:0} m",
                    Run = () => CyclePref(_perfDistance, Performance.Distances.Length)
                });
            }
            _items.Add(new MenuItem
            {
                Label = "Lighting: " + Lighting.Label((Lighting.Preset)_lightingPreset.Value),
                Run = () =>
                {
                    _lightingPreset.Value = (_lightingPreset.Value + 1) % Lighting.PresetCount;
                    _prefs.SaveToFile(false);
                }
            });
            _items.Add(new MenuItem
            {
                Label = "Shadows: " + (_lightingShadows.Value ? "on" : "off"),
                Run = () => TogglePref(_lightingShadows)
            });
            _items.Add(new MenuItem
            {
                Label = "FPS cap: " + FpsUnlock.Label(_fpsCap.Value),
                Run = () =>
                {
                    CyclePref(_fpsCap, FpsUnlock.Caps.Length);
                    if (DesktopMode.HeadsetPresent())
                        Flash("In VR the headset's refresh rate sets the frame rate");
                }
            });
            _items.Add(new MenuItem
            {
                Label = "Post processing: " + (_postOn.Value ? "on" : "off"),
                Run = () => TogglePref(_postOn)
            });
            if (_postOn.Value)
            {
                _items.Add(new MenuItem
                {
                    Label = "   Bloom: " + PostProcessing.Label((PostProcessing.BloomLevel)_postBloom.Value),
                    Run = () => CyclePref(_postBloom, PostProcessing.BloomCount)
                });
                _items.Add(new MenuItem
                {
                    Label = "   Color: " + PostProcessing.Label((PostProcessing.Grade)_postGrade.Value),
                    Run = () => CyclePref(_postGrade, PostProcessing.GradeCount)
                });
                _items.Add(new MenuItem
                {
                    Label = "   Tonemapping: " + PostProcessing.Label((PostProcessing.Tone)_postTone.Value),
                    Run = () => CyclePref(_postTone, PostProcessing.ToneCount)
                });
                _items.Add(new MenuItem
                {
                    Label = "   Vignette: " + (_postVignette.Value ? "on" : "off"),
                    Run = () => TogglePref(_postVignette)
                });
            }
        }

        private void BuildPlayers()
        {
            _items.Add(new MenuItem
            {
                Label = "Player collision: " + (_playerCollisionOn.Value ? "on" : "off"),
                Run = () => TogglePref(_playerCollisionOn)
            });
            _items.Add(new MenuItem
            {
                Label = "Nameplates: " + (_hideNameplates.Value ? "hidden" : "shown"),
                Run = ToggleNameplates
            });
            _items.Add(new MenuItem
            {
                Label = "Hat (mod users see it): " + Cosmetics.Label((Cosmetics.Hat)Mathf.Clamp(_hat.Value, 0, Cosmetics.HatCount - 1)),
                Run = () => CyclePref(_hat, Cosmetics.HatCount)
            });
            if (_hat.Value != 0)
            {
                _items.Add(new MenuItem
                {
                    Label = "   Hat color: " + Cosmetics.Colours[Mathf.Clamp(_hatColour.Value, 0, Cosmetics.Colours.Length - 1)].Name,
                    Run = () => CyclePref(_hatColour, Cosmetics.Colours.Length)
                });
            }
            _items.Add(new MenuItem
            {
                Label = "Shirt color (mod users see it): " + Cosmetics.ColourName(_shirt.Value - 1),
                Run = () => CyclePref(_shirt, Cosmetics.Colours.Length + 1)
            });
            _items.Add(new MenuItem
            {
                Label = "Shirt material: " + (string.IsNullOrEmpty(_shirtMaterial.Value) ? "avatar's own" : _shirtMaterial.Value),
                Run = CycleShirtMaterial
            });
            _items.Add(new MenuItem
            {
                Label = "Visor color (mod users see it): " + Cosmetics.ColourName(_visor.Value - 1),
                Run = () => CyclePref(_visor, Cosmetics.Colours.Length + 1)
            });
            _items.Add(new MenuItem
            {
                Label = "Arm bands (mod users see them): " + Cosmetics.ColourName(_bands.Value - 1),
                Run = () => CyclePref(_bands, Cosmetics.Colours.Length + 1)
            });
        }

        private void BuildWorld()
        {
            _items.Add(new MenuItem
            {
                Label = $"Spawn mirror ({Mirrors.Sizes[Mathf.Clamp(_mirrorSize.Value, 0, Mirrors.Sizes.Length - 1)].Name})",
                Run = () =>
                {
                    try
                    {
                        Flash(_mirrors.Spawn());
                    }
                    catch (Exception e)
                    {
                        LoggerInstance.Error($"Mirror spawn failed: {e}");
                        Flash("Mirror failed (see log)");
                    }
                }
            });
            _items.Add(new MenuItem
            {
                Label = Mirrors.CameraCrashedLastTime && _mirrors.Count == 0
                    ? "<color=#F87171>Camera mirror crashed the game last time</color>  <size=70%><color=#B5B0A8>click to allow it again</color></size>"
                    : $"Spawn camera mirror ({Mirrors.Sizes[Mathf.Clamp(_mirrorSize.Value, 0, Mirrors.Sizes.Length - 1)].Name}, shows hats)",
                Run = () =>
                {
                    if (Mirrors.CameraCrashedLastTime && _mirrors.Count == 0)
                    {
                        Mirrors.ClearCrashMarker();
                        Flash("Camera mirror allowed again; spawn it to try");
                        return;
                    }
                    try
                    {
                        Flash(_mirrors.SpawnCamera());
                    }
                    catch (Exception e)
                    {
                        LoggerInstance.Error($"Camera mirror failed: {e}");
                        Flash("Camera mirror failed (see log)");
                    }
                }
            });
            _items.Add(new MenuItem
            {
                Label = "   Mirror size: " + Mirrors.Sizes[Mathf.Clamp(_mirrorSize.Value, 0, Mirrors.Sizes.Length - 1)].Name,
                Run = () => CyclePref(_mirrorSize, Mirrors.Sizes.Length)
            });
            _items.Add(new MenuItem
            {
                Label = "   Mirror quality: " + Mirrors.Qualities[Mathf.Clamp(_mirrorQuality.Value, 0, Mirrors.Qualities.Length - 1)].Name +
                    " (new mirrors)",
                Run = () => CyclePref(_mirrorQuality, Mirrors.Qualities.Length)
            });
            if (_mirrors.Count > 0)
            {
                _items.Add(new MenuItem { Label = "   Remove last mirror", Run = _mirrors.RemoveLast });
                _items.Add(new MenuItem { Label = $"   Remove all mirrors ({_mirrors.Count})", Run = _mirrors.RemoveAll });
            }
        }

        private void BuildBuilding()
        {
            var source = (AutoRecolor.Source)Mathf.Clamp(_recolorSource.Value, 0, AutoRecolor.SourceCount - 1);
            _items.Add(new MenuItem
            {
                Label = "Recolor primitives on place: " + AutoRecolor.Label(source),
                Run = () => CyclePref(_recolorSource, AutoRecolor.SourceCount)
            });
            if (source == AutoRecolor.Source.Custom)
            {
                _items.Add(new MenuItem
                {
                    Label = "   Color: " + AutoRecolor.ColourLabel(_recolorColour.Value),
                    Run = () => CyclePref(_recolorColour, Cosmetics.Colours.Length + 1) // + random
                });
            }
            else if (source == AutoRecolor.Source.Creatool)
            {
                _items.Add(new MenuItem
                {
                    Label = "<size=70%><color=#B5B0A8>   uses the color/material picked in the Creatool's Recolor mode</color></size>",
                    Run = () => { }
                });
            }
            _items.Add(new MenuItem
            {
                Label = "Picture frame: " + Pictures.Label((Pictures.Frame)Mathf.Clamp(_pictureFrame.Value, 0, Pictures.FrameCount - 1)) +
                    "   <size=70%><color=#B5B0A8>quality: " + Pictures.Qualities[Mathf.Clamp(_pictureQuality.Value, 0, Pictures.Qualities.Length - 1)].Name + "</color></size>",
                Run = () => CyclePref(_pictureFrame, Pictures.FrameCount)
            });
            _items.Add(new MenuItem
            {
                Label = "   Picture quality: " + Pictures.Qualities[Mathf.Clamp(_pictureQuality.Value, 0, Pictures.Qualities.Length - 1)].Name,
                Run = () => CyclePref(_pictureQuality, Pictures.Qualities.Length)
            });
            if (_pictureFiles.Count == 0)
                _items.Add(new MenuItem
                {
                    Label = "<size=70%><color=#B5B0A8>   put .png / .jpg files in UserData/SocialSpaceModMenu/Pictures to hang them</color></size>",
                    Run = () => { }
                });
            for (int i = 0; i < _pictureFiles.Count && i < 6; i++)
            {
                string path = _pictureFiles[i];
                _items.Add(new MenuItem
                {
                    Label = "Hang picture: " + System.IO.Path.GetFileNameWithoutExtension(path),
                    Run = () => Flash(_pictures.Place(path))
                });
            }
            _items.Add(new MenuItem { Label = "Change frame of nearest picture", Run = () => Flash(_pictures.CycleNearestFrame()) });
            _items.Add(new MenuItem { Label = "Remove unused picture data", Run = () => Flash(_pictures.RemoveUnused()) });
            _items.Add(new MenuItem
            {
                Label = "Sky (synced, saved with space): " + Skybox.Presets[_sky.Current].Name,
                Run = () => Flash(_sky.Set((_sky.Current + 1) % Skybox.Presets.Length))
            });
            _items.Add(new MenuItem
            {
                Label = "Creatool highlights: " + (_highlightOn.Value ? "on" : "off"),
                Run = () => TogglePref(_highlightOn)
            });
        }

        private void BuildSpaces()
        {
            _items.Add(new MenuItem { Label = "Save this space to file", Run = SpaceIO.Save });
            _items.Add(new MenuItem { Label = "<color=#F87171>Clear all objects</color>", Run = SpaceIO.ClearAll, Confirm = true });
            foreach (var f in _files)
            {
                string path = f.Path;
                _items.Add(new MenuItem { Label = "Load " + f.Label, Run = () => SpaceIO.Load(path), Confirm = true });
            }
        }

        private void UpdatePresence()
        {
            bool want = _discordOn.Value && IsAppId(DiscordAppId());
            if (!want)
            {
                if (_discord != null)
                {
                    _discord.Stop();
                    _discord = null;
                }
                return;
            }
            if (_discord == null)
            {
                _discord = new DiscordRpc(DiscordAppId());
                _discord.Start();
                _nextPresence = 0f;
            }
            if (Time.unscaledTime < _nextPresence)
                return;
            _nextPresence = Time.unscaledTime + 2f;
            if (Il2CppSharp.SharpNetwork.InInstance)
            {
                int players = Il2CppSharp.SharpNetwork.ClientData != null ? Il2CppSharp.SharpNetwork.ClientData.Count : 0;
                string space = _discordSpace.Value ? CurrentSpaceName() : null;
                _discord.Set(string.IsNullOrEmpty(space) ? "In a space" : "In " + space,
                    players > 1 ? $"with {players - 1} other{(players == 2 ? "" : "s")}" : "alone");
            }
            else
            {
                _discord.Set("In the menu", null);
            }
        }

        private static string CurrentSpaceName()
        {
            var sm = SpaceIO.FindManager();
            var st = sm != null ? sm.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.SpaceState>())?.TryCast<Il2Cpp.SpaceState>() : null;
            string name = st != null ? st.spaceName : null;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        private const string DefaultDiscordAppId = "1513736878031900693";

        private string DiscordAppId() =>
            string.IsNullOrWhiteSpace(_discordAppId.Value) ? DefaultDiscordAppId : _discordAppId.Value.Trim();

        private static bool IsAppId(string s)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s) || s.Length < 15 || s.Length > 21)
                return false;
            foreach (char c in s)
                if (c < '0' || c > '9')
                    return false;
            return true;
        }

        private void BuildSettings()
        {
            _items.Add(new MenuItem
            {
                Label = "Discord status: " + (!_discordOn.Value ? "off"
                    : !IsAppId(DiscordAppId()) ? "<color=#E8A33D>DiscordAppId in MelonPreferences.cfg isn't a valid id</color>"
                    : _discord != null && _discord.Connected ? "on" : "on (waiting for Discord)"),
                Run = () => _discordOn.Value = !_discordOn.Value
            });
            if (_discordOn.Value)
            {
                _items.Add(new MenuItem
                {
                    Label = "   Show space name: " + (_discordSpace.Value ? "on" : "off"),
                    Run = () => _discordSpace.Value = !_discordSpace.Value
                });
            }
            _items.Add(new MenuItem
            {
                Label = "Notifications: " + (!_notifyOn.Value ? "off" : _notifyChime.Value ? "on" : "on (silent)"),
                Run = CycleNotifications
            });
            _items.Add(new MenuItem
            {
                Label = "Auto update: " + (AutoUpdate.Enabled ? "on" : "off"),
                Run = () => AutoUpdate.Enabled = !AutoUpdate.Enabled
            });
        }

        private void NextItem()
        {
            if (_items.Count == 0)
                return;
            _selected = (_selected + 1) % _items.Count;
            _armedItem = -1;
        }

        private void Activate()
        {
            if (_selected < 0 || _selected >= _items.Count)
                return;
            if (SpaceIO.Busy)
            {
                Flash("Busy, wait for the current load");
                return;
            }
            var item = _items[_selected];
            if (item.Confirm && _armedItem != _selected)
            {
                _armedItem = _selected;
                _armedUntil = Time.unscaledTime + ConfirmSeconds;
                return;
            }
            _armedItem = -1;
            item.Run();
            _nextRefresh = 0f; // pick up a freshly saved file right away
        }

        // ---- input

        private void HandleKeyboard()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;
            if (kb.f1Key.wasPressedThisFrame)
                TogglePanel();
            if (kb.f2Key.wasPressedThisFrame)
                ToggleNameplates();
            if (_showPanel.Value && kb.f3Key.wasPressedThisFrame)
                NextItem();
            if (_showPanel.Value && kb.f4Key.wasPressedThisFrame)
                Activate();
            if (_showPanel.Value && kb.backspaceKey.wasPressedThisFrame)
                GoBack();
        }

        private static bool StickClicked(XRNode node)
        {
            XRInputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool down) && down;
        }

        // Acts when every stick is released: both held at any point = panel, else left = next, right = activate.
        private void HandleControllers()
        {
            bool left = StickClicked(XRNode.LeftHand);
            bool right = StickClicked(XRNode.RightHand);
            if (left || right)
            {
                _gestureActive = true;
                _sawLeft |= left;
                _sawRight |= right;
                _sawBoth |= left && right;
                return;
            }
            if (!_gestureActive)
                return;
            _gestureActive = false;
            if (_sawBoth)
                TogglePanel();
            else if (_showPanel.Value && _sawLeft)
                NextItem();
            else if (_showPanel.Value && _sawRight)
                Activate();
            _sawLeft = _sawRight = _sawBoth = false;
        }

        private void TogglePanel()
        {
            _showPanel.Value = !_showPanel.Value;
            _armedItem = -1;
            _prefs.SaveToFile(false);
        }

        private void ToggleGorilla()
        {
            _gorillaMode.Value = !_gorillaMode.Value;
            _prefs.SaveToFile(false);
            if (!_gorillaMode.Value)
            {
                _gorilla.Disable();
                Flash("Gorilla locomotion off, stick movement back");
            }
            else if (_gorilla.Enable())
            {
                Flash("Gorilla locomotion on: push off surfaces with your hands");
            }
            else
            {
                Flash("Gorilla locomotion will start once your body spawns");
            }
        }

        private void CycleNotifications()
        {
            // on -> on (silent) -> off -> on
            if (_notifyOn.Value && _notifyChime.Value)
                _notifyChime.Value = false;
            else if (_notifyOn.Value)
                _notifyOn.Value = false;
            else
            {
                _notifyOn.Value = true;
                _notifyChime.Value = true;
            }
            _prefs.SaveToFile(false);
            _notify.Enabled = _notifyOn.Value;
            _notify.Chime = _notifyChime.Value;
            _notify.Show("Notifications on");
        }

        // Toasts anything new in the account mailbox (friend requests, invites, mail).
        private void CheckMailbox()
        {
            if (_mailbox == null)
            {
                var found = Object.FindAnyObjectByType(Il2CppType.Of<AccountMailboxNetworked>());
                _mailbox = found != null ? found.TryCast<AccountMailboxNetworked>() : null;
                if (_mailbox == null)
                    return;
            }
            var items = _mailbox._mailbox;
            if (items == null)
                return;

            bool firstLook = _seenMail == null;
            _seenMail ??= new HashSet<string>();
            var present = new HashSet<string>();
            for (int i = 0; i < items.Count; i++)
            {
                var m = items[i];
                string key = $"{m.type}|{m.fromUuid}|{m.receivedAt:0.000}";
                present.Add(key);
                if (!_seenMail.Add(key) || firstLook)
                    continue;
                string title = string.IsNullOrEmpty(m.title) ? AccountMailboxNetworked.MailLabel(m.type) : m.title;
                _notify.Show("<color=#D97757>Mailbox</color>  " + (title ?? "New item"), m.description);
            }
            _seenMail.IntersectWith(present); // forget dismissed items
        }

        private float CurrentBoost() => Boosts[Mathf.Clamp(_boostIndex.Value, 0, Boosts.Length - 1)];

        private void CycleBoost()
        {
            _boostIndex.Value = (_boostIndex.Value + 1) % Boosts.Length;
            _prefs.SaveToFile(false);
            ApplyBoost();
            float b = CurrentBoost();
            Flash(b <= 1f ? "Boost off, normal speed" : $"Boost {b:0.##}x");
        }

        // Gorilla mode: stronger launches and a higher speed cap. Stick walking: scaled move speed and
        // jump height. Values are captured per rig instance, so a respawned rig starts from its own defaults.
        private void ApplyBoost()
        {
            float b = CurrentBoost();
            _gorilla.Boost = b;
            _web.Boost = b;

            var loco = SnapLocomotion.Instance;
            var move = loco != null ? loco.move : null;
            if (move != null && move != _boostedMove)
            {
                _boostedMove = move;
                _baseMoveSpeed = move.moveSpeed;
            }
            if (_boostedMove != null)
                _boostedMove.moveSpeed = _baseMoveSpeed * b;

            var jumpObj = Object.FindAnyObjectByType(Il2CppType.Of<SnapJump>());
            var jump = jumpObj != null ? jumpObj.TryCast<SnapJump>() : null;
            if (jump != null && jump != _boostedJump)
            {
                _boostedJump = jump;
                _baseJumpHeight = jump.jumpHeight;
            }
            if (_boostedJump != null)
                _boostedJump.jumpHeight = _baseJumpHeight * b;
        }

        // A feature that throws is switched off (and logged) instead of spamming every frame.
        private void RunSafely(string name, Action update, Action turnOff)
        {
            try
            {
                update();
            }
            catch (Exception e)
            {
                turnOff();
                _prefs.SaveToFile(false);
                LoggerInstance.Error($"{name} stopped: {e}");
                Flash($"{name} error (see log)");
            }
        }

        private void TogglePref(MelonPreferences_Entry<bool> entry)
        {
            entry.Value = !entry.Value;
            _prefs.SaveToFile(false);
        }

        private void CyclePref(MelonPreferences_Entry<int> entry, int count)
        {
            entry.Value = (entry.Value + 1) % count;
            _prefs.SaveToFile(false);
        }

        private void ToggleNameplates()
        {
            _hideNameplates.Value = !_hideNameplates.Value;
            _prefs.SaveToFile(false);
            ApplyNameplates();
            Flash(_hideNameplates.Value ? "Nameplates hidden" : "Nameplates shown");
        }

        private void Flash(string msg)
        {
            _flash = msg;
            _flashUntil = Time.unscaledTime + 2.5f;
        }

        // ---- game state

        private static Transform Head()
        {
            var loco = SnapLocomotion.Instance;
            if (loco != null && loco.Head != null)
                return loco.Head;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        private static Vector3? LocalPosition()
        {
            var loco = SnapLocomotion.Instance;
            return loco != null ? loco.PlayerPosition : null;
        }

        private void RefreshPlayers()
        {
            _players.Clear();
            var me = LocalPosition();
            var found = Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None);
            foreach (var obj in found)
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc == null)
                    continue;
                string name = acc.DisplayName;
                if (string.IsNullOrEmpty(name))
                    name = acc.AccountName;
                if (string.IsNullOrEmpty(name))
                    name = "(loading)";
                float dist = me.HasValue ? Vector3.Distance(me.Value, acc.transform.position) : -1f;
                _players.Add(new PlayerRow { Name = name, Distance = dist });
            }
            _players.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        // Re-applied every refresh so nameplates spawned later follow the setting too.
        private void ApplyNameplates()
        {
            var found = Object.FindObjectsByType(Il2CppType.Of<Nameplate>(), FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (var obj in found)
            {
                var plate = obj.TryCast<Nameplate>();
                if (plate == null || plate.nameText == null)
                    continue;
                plate.nameText.enabled = !_hideNameplates.Value;
            }
        }

        // ---- panel

        private bool EnsurePanel()
        {
            if (_panel != null)
                return true;
            var font = Notifications.FindFont();
            if (font == null)
                return false; // nothing loaded yet; try again next frame

            _panel = new GameObject("SocialSpaceModMenu.Panel");
            Object.DontDestroyOnLoad(_panel);
            _text = _panel.AddComponent(Il2CppType.Of<TextMeshPro>()).Cast<TextMeshPro>();
            _text.font = font;
            _text.fontSize = 0.32f;
            _text.richText = true;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.color = Color.white;
            _text.sortingOrder = 5000;
            _text.rectTransform.sizeDelta = new Vector2(0.55f, 0.7f);
            _snapPanel = true;
            return true;
        }

        private void FollowHead(Transform head)
        {
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f)
                fwd = head.up; // looking straight up or down
            fwd.y = 0f;
            fwd.Normalize();

            Vector3 target = head.position + fwd * PanelDistance + Vector3.down * PanelDrop;
            var t = _panel.transform;
            t.position = _snapPanel
                ? target
                : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime));
            _snapPanel = false;
            // TMP text reads correctly when its +Z points away from the viewer
            t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);
        }

        private string BuildText()
        {
            var sb = new StringBuilder();
            sb.Append(_page == Page.Main
                ? "<b><color=#D97757>Social Space Mod Menu</color></b>\n"
                : $"<b><color=#D97757>Mod Menu</color></b> <color=#B5B0A8>/</color> <b>{PageTitle(_page)}</b>\n");
            var me = LocalPosition();
            sb.Append($"<size=80%>FPS {_fps:0}   ");
            sb.Append(me.HasValue ? $"pos {me.Value.x:0.0}, {me.Value.y:0.0}, {me.Value.z:0.0}" : "not spawned");
            sb.Append("</size>\n");

            bool showPlayers = _page == Page.Main || _page == Page.Players;
            if (showPlayers)
                sb.Append($"<size=80%>Players here: {_players.Count}</size>\n");
            for (int i = 0; showPlayers && i < _players.Count && i < MaxPlayerRows; i++)
            {
                var p = _players[i];
                sb.Append(p.Distance >= 0
                    ? $"<size=75%><color=#B5B0A8>   {p.Name}  ({p.Distance:0.0} m)</color></size>\n"
                    : $"<size=75%><color=#B5B0A8>   {p.Name}</color></size>\n");
            }
            if (showPlayers && _players.Count > MaxPlayerRows)
                sb.Append($"<size=75%><color=#B5B0A8>   +{_players.Count - MaxPlayerRows} more</color></size>\n");

            sb.Append('\n');
            for (int i = 0; i < _items.Count; i++)
            {
                bool sel = i == _selected;
                string label = _items[i].Label;
                if (i == _armedItem)
                    label += "  <color=#F87171>click again to confirm</color>";
                sb.Append(sel ? $"<color=#D97757>> {label}</color>\n" : $"<color=#B5B0A8>   {label}</color>\n");
            }
            if ((_page == Page.Spaces || _page == Page.Main) && _files.Count == 0)
                sb.Append("<size=75%><color=#B5B0A8>   (no saved spaces yet)</color></size>\n");

            if ((_page == Page.Spaces || _page == Page.Main || _page == Page.Building) && !Il2CppSharp.SharpNetwork.IsMaster)
                sb.Append("<size=70%><color=#F87171>Not space master: loads may be refused or unsynced</color></size>\n");
            if (!string.IsNullOrEmpty(SpaceIO.Status))
                sb.Append($"<size=80%><color=#4ADE80>{SpaceIO.Status}</color></size>\n");
            if (Time.unscaledTime < _flashUntil)
                sb.Append($"<size=80%><color=#4ADE80>{_flash}</color></size>\n");
            sb.Append(_page == Page.Main
                ? "<size=65%><color=#B5B0A8>Both sticks: close   L stick: next   R stick: open</color></size>"
                : "<size=65%><color=#B5B0A8>Both sticks: close   L stick: next   R stick: select   (Back is first)</color></size>");
            return sb.ToString();
        }
    }
}
