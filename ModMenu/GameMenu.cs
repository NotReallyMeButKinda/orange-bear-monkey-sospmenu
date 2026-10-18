using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Puts the mod into the game's own wrist/visor menu: a button in the corner of a tab's section opens
    /// an overlay listing rows from a provider; clicking a row runs it. The overlay sits on top of the tab's
    /// own content (nothing of the game's is moved or hidden) and is clicked with the game's normal UI ray.
    /// </summary>
    internal sealed class GameMenu
    {
        public struct Row
        {
            public string Label;
            public Action Run;
        }

        private const int MaxRows = 16;
        private static readonly Color Panel = new(0.09f, 0.09f, 0.1f, 0.97f);
        private static readonly Color RowIdle = new(1f, 1f, 1f, 0.04f);
        private static readonly Color Accent = new(0.85f, 0.47f, 0.34f, 1f);

        private readonly MenuNavigationManager.Tab _tab;
        private readonly string _buttonText;
        private readonly Func<string> _header;
        private readonly Func<List<Row>> _rows;

        private GameObject _section, _toggle, _overlay;
        private TextMeshProUGUI _toggleText, _headerText;
        private readonly List<(GameObject Go, TextMeshProUGUI Text)> _rowViews = new();
        private List<Row> _current = new();
        private readonly string[] _shown = new string[MaxRows];
        private string _shownHeader;
        private bool _open, _logged, _wasShown;
        private TMP_InputField _input;
        private float _rowH;
        private float _nextFind;

        public GameMenu(MenuNavigationManager.Tab tab, string buttonText, Func<string> header, Func<List<Row>> rows)
        {
            _tab = tab;
            _buttonText = buttonText;
            _header = header;
            _rows = rows;
        }

        public void Update()
        {
            if (_section == null)
            {
                _toggle = _overlay = null;
                _rowViews.Clear();
                if (Time.unscaledTime < _nextFind)
                    return;
                _nextFind = Time.unscaledTime + 2f;
                _section = FindSection(_tab);
                if (_section == null)
                    return;
                Build();
            }
            if (_toggle == null || _overlay == null)
            {
                _section = null; // rebuilt next time
                return;
            }
            if (!_section.activeInHierarchy)
            {
                if (_open)
                    SetOpen(false);
                _wasShown = false;
                return;
            }
            if (!_wasShown)
            {
                _wasShown = true;
                SetOpen(true); // open by default each time the tab is shown; "Close" shows the game's tab
            }
            if (_overlay.transform.GetSiblingIndex() < _section.transform.childCount - 2)
            {
                _overlay.transform.SetAsLastSibling(); // stay on top of anything the game adds
                _toggle.transform.SetAsLastSibling();
            }
            if (_open)
                Refresh();
        }

        public void Destroy()
        {
            if (_overlay != null)
                Object.Destroy(_overlay);
            _input = null;
            if (_toggle != null)
                Object.Destroy(_toggle);
            _section = _overlay = _toggle = null;
            _rowViews.Clear();
        }

        private static GameObject FindSection(MenuNavigationManager.Tab tab)
        {
            foreach (var o in Object.FindObjectsByType(Il2CppType.Of<MenuNavigationManager>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var nav = o.TryCast<MenuNavigationManager>();
                var tabs = nav != null ? nav.tabs : null;
                if (tabs == null || (int)tab >= tabs.Length || tabs[(int)tab] == null)
                    continue;
                var section = tabs[(int)tab].section;
                if (section != null && section.GetComponent(Il2CppType.Of<RectTransform>()) != null)
                    return section;
            }
            return null;
        }

        private void Build()
        {
            var sectionRt = _section.GetComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            Rect r = sectionRt.rect;
            float w = Mathf.Max(r.width, 100f), h = Mathf.Max(r.height, 100f);
            var font = _section.GetComponentInChildren(Il2CppType.Of<TMP_Text>(), true)?.TryCast<TMP_Text>();
            if (!_logged)
            {
                _logged = true;
                Melon<ModMenu>.Logger.Msg($"Game menu: {_tab} section '{_section.name}' {w:0}x{h:0}, font {(font != null && font.font != null ? font.font.name : "none")}");
            }

            float rowH = h / (MaxRows + 3.5f);
            _rowH = rowH;
            float fontSize = rowH * 0.5f;

            _overlay = Ui("SocialSpaceModMenu.GameMenu." + _tab, _section.transform);
            var ort = Rt(_overlay);
            ort.anchorMin = Vector2.zero;
            ort.anchorMax = Vector2.one;
            ort.offsetMin = ort.offsetMax = Vector2.zero;
            var bg = _overlay.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            bg.color = Panel; // also blocks clicks from reaching the tab underneath

            _headerText = Text(_overlay.transform, font, fontSize * 1.15f, new Vector2(0.04f, 1f), new Vector2(0.75f, 1f),
                new Vector2(0f, -rowH * 1.6f), new Vector2(0f, -rowH * 0.3f));
            for (int i = 0; i < MaxRows; i++)
            {
                int index = i;
                var row = Ui("Row" + i, _overlay.transform);
                var rrt = Rt(row);
                rrt.anchorMin = new Vector2(0.03f, 1f);
                rrt.anchorMax = new Vector2(0.97f, 1f);
                rrt.offsetMin = new Vector2(0f, -rowH * (2.0f + i + 0.92f));
                rrt.offsetMax = new Vector2(0f, -rowH * (2.0f + i));
                var img = row.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                img.color = RowIdle;
                var button = row.AddComponent(Il2CppType.Of<Button>()).Cast<Button>();
                var colours = button.colors;
                colours.highlightedColor = new Color(1f, 1f, 1f, 3f);
                colours.pressedColor = Accent;
                button.colors = colours;
                button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Click(index))));
                var text = Text(row.transform, font, fontSize, new Vector2(0.02f, 0f), new Vector2(0.98f, 1f), Vector2.zero, Vector2.zero);
                _rowViews.Add((row, text));
            }
            _overlay.SetActive(false);

            _toggle = Ui("SocialSpaceModMenu.GameMenuButton." + _tab, _section.transform);
            var trt = Rt(_toggle);
            trt.anchorMin = trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(1f, 1f);
            trt.sizeDelta = new Vector2(w * 0.22f, rowH * 1.2f);
            trt.anchoredPosition = new Vector2(-w * 0.02f, -rowH * 0.3f);
            var timg = _toggle.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            timg.color = Accent;
            var tb = _toggle.AddComponent(Il2CppType.Of<Button>()).Cast<Button>();
            tb.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => SetOpen(!_open))));
            _toggleText = Text(_toggle.transform, font, fontSize, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _toggleText.alignment = TextAlignmentOptions.Center;
            _toggleText.text = _buttonText;
        }

        /// <summary>Shows a text box in place of the header, focused so the game's VR keyboard comes up.</summary>
        public bool BeginInput(string initial)
        {
            if (_overlay == null || !_open)
                return false;
            if (_input == null)
                _input = MakeInput();
            if (_input == null)
                return false;
            _input.gameObject.SetActive(true);
            _input.text = initial ?? "";
            _headerText.gameObject.SetActive(false);
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
                es.SetSelectedGameObject(_input.gameObject);
            _input.ActivateInputField();
            _input.caretPosition = _input.text.Length;
            return true;
        }

        public string InputText => _input != null ? _input.text : "";

        public void EndInput()
        {
            if (_input != null)
            {
                _input.DeactivateInputField();
                _input.gameObject.SetActive(false);
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.currentSelectedGameObject == _input.gameObject)
                    es.SetSelectedGameObject(null);
            }
            if (_headerText != null)
                _headerText.gameObject.SetActive(true);
        }

        // a copy of the game's own space search box, so it looks right and the VR keyboard knows it
        private TMP_InputField MakeInput()
        {
            TMP_InputField template = null;
            foreach (var o in Object.FindObjectsByType(Il2CppType.Of<SpaceSearchUI>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var ui = o.TryCast<SpaceSearchUI>();
                if (ui != null && ui.searchInput != null)
                {
                    template = ui.searchInput;
                    break;
                }
            }
            if (template == null)
                template = Object.FindAnyObjectByType(Il2CppType.Of<TMP_InputField>(), FindObjectsInactive.Include)?.TryCast<TMP_InputField>();
            if (template == null)
            {
                Melon<ModMenu>.Logger.Warning("Game menu: no input field to copy for typing");
                return null;
            }
            var go = Object.Instantiate(template.gameObject, _overlay.transform, false);
            go.name = "SocialSpaceModMenu.Input";
            go.SetActive(false);
            var field = go.GetComponent(Il2CppType.Of<TMP_InputField>()).Cast<TMP_InputField>();
            // the copy brings the search box's inspector-wired handlers; switch them off
            Mute(field.onValueChanged);
            Mute(field.onEndEdit);
            Mute(field.onSubmit);
            Mute(field.onSelect);
            Mute(field.onDeselect);
            field.characterLimit = 80;
            field.lineType = TMP_InputField.LineType.SingleLine;
            var le = go.GetComponent(Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>()
                ?? go.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
            le.ignoreLayout = true;
            var rt = Rt(go);
            rt.anchorMin = new Vector2(0.03f, 1f);
            rt.anchorMax = new Vector2(0.75f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(0f, -_rowH * 1.75f);
            rt.offsetMax = new Vector2(0f, -_rowH * 0.25f);
            return field;
        }

        private static void Mute(UnityEventBase e)
        {
            if (e == null)
                return;
            for (int i = 0; i < e.GetPersistentEventCount(); i++)
                e.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        private void SetOpen(bool open)
        {
            if (!open)
                EndInput();
            _open = open;
            if (_overlay != null)
                _overlay.SetActive(open);
            if (_toggleText != null)
                _toggleText.text = open ? "Close" : _buttonText;
            if (open)
            {
                _shownHeader = null;
                Array.Clear(_shown, 0, _shown.Length);
                Refresh();
            }
        }

        private void Refresh()
        {
            _current = _rows() ?? new List<Row>();
            string header = _header();
            if (header != _shownHeader)
            {
                _shownHeader = header;
                _headerText.text = header;
            }
            for (int i = 0; i < _rowViews.Count; i++)
            {
                bool has = i < _current.Count;
                var (go, text) = _rowViews[i];
                if (go.activeSelf != has)
                    go.SetActive(has);
                string label = has ? _current[i].Label : null;
                if (has && label != _shown[i])
                    text.text = label;
                _shown[i] = label;
            }
        }

        private void Click(int index)
        {
            try
            {
                if (index < _current.Count)
                    _current[index].Run?.Invoke();
                Refresh();
            }
            catch (Exception e)
            {
                Melon<ModMenu>.Logger.Error($"Game menu click: {e}");
            }
        }

        // ---- uGUI helpers

        private GameObject Ui(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.AddComponent(Il2CppType.Of<RectTransform>());
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
            le.ignoreLayout = true; // the section's own layout group must leave these alone
            return go;
        }

        private static RectTransform Rt(GameObject go) => go.GetComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();

        private TextMeshProUGUI Text(Transform parent, TMP_Text like, float size, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = Ui("Text", parent);
            var rt = Rt(go);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            var t = go.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
            if (like != null && like.font != null)
            {
                t.font = like.font;
                t.fontSharedMaterial = like.fontSharedMaterial;
            }
            t.fontSize = size;
            t.enableAutoSizing = true;
            t.fontSizeMin = size * 0.5f;
            t.fontSizeMax = size;
            t.richText = true;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }
    }
}
