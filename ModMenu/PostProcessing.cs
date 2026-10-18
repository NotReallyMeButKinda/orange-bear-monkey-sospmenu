using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Local URP post processing: a mod-owned global Volume at a high priority whose profile overrides
    /// bloom, colour grading, exposure, tonemapping, vignette, film grain and chromatic aberration, plus
    /// turning post processing on for the main
    /// camera. Only the parameters set here are overridden; everything else stays as the space has it.
    /// Turning it off destroys the volume and restores the camera's own setting.
    /// </summary>
    internal sealed class PostProcessing
    {
        public enum BloomLevel { Off = 0, Low = 1, High = 2, Dreamy = 3 }
        public enum Grade { Default = 0, Vivid = 1, Warm = 2, Cool = 3, Mono = 4, Cinematic = 5, Sunset = 6, Pastel = 7, Noir = 8 }
        public enum Tone { Game = 0, Neutral = 1, Aces = 2 }
        public enum Amount { Off = 0, Light = 1, Strong = 2 }
        public const int BloomCount = 4, GradeCount = 9, ToneCount = 3, AmountCount = 3;
        public static readonly float[] Exposures = { -1f, -0.5f, 0f, 0.5f, 1f };
        public const int DefaultExposure = 2;

        public bool Enabled;
        public BloomLevel BloomSetting = BloomLevel.Low;
        public Grade GradeSetting = Grade.Vivid;
        public Tone ToneSetting = Tone.Aces;
        public bool VignetteOn = true;
        public int ExposureIndex = DefaultExposure;
        public Amount Grain, Chromatic;

        private const float CameraCheckInterval = 1f;

        private GameObject _go;
        private Volume _volume;
        private VolumeProfile _profile;
        private Bloom _bloom;
        private ColorAdjustments _color;
        private WhiteBalance _white;
        private Tonemapping _tone;
        private Vignette _vignette;
        private SplitToning _split;
        private FilmGrain _grain;
        private ChromaticAberration _chromatic;

        private UniversalAdditionalCameraData _camera;
        private bool _cameraHadPost;
        private float _nextCameraCheck;
        private (bool, BloomLevel, Grade, Tone, bool, int, Amount, Amount) _applied;

        public static string Label(BloomLevel b) => b switch { BloomLevel.Low => "low", BloomLevel.High => "high", BloomLevel.Dreamy => "dreamy", _ => "off" };
        public static string Label(Amount a) => a.ToString().ToLowerInvariant();
        public static string ExposureLabel(int i) =>
            Exposures[Mathf.Clamp(i, 0, Exposures.Length - 1)] switch { 0f => "normal", var e => (e > 0 ? "+" : "") + e.ToString("0.0") };
        public static string Label(Grade g) => g.ToString().ToLowerInvariant();
        public static string Label(Tone t) => t switch { Tone.Neutral => "neutral", Tone.Aces => "ACES", _ => "game" };

        public void Update()
        {
            if (!Enabled)
            {
                if (_go != null || _camera != null)
                    Teardown();
                return;
            }
            if (_go == null)
                Build();

            if (Time.unscaledTime >= _nextCameraCheck)
            {
                _nextCameraCheck = Time.unscaledTime + CameraCheckInterval;
                HookCamera();
            }

            var state = (Enabled, BloomSetting, GradeSetting, ToneSetting, VignetteOn, ExposureIndex, Grain, Chromatic);
            if (state != _applied)
            {
                Apply();
                _applied = state;
            }
        }

        private void Build()
        {
            _go = new GameObject("SocialSpaceModMenu.PostProcessing");
            Object.DontDestroyOnLoad(_go);
            _profile = ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).Cast<VolumeProfile>();
            _profile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _bloom = AddComponent<Bloom>();
            _color = AddComponent<ColorAdjustments>();
            _white = AddComponent<WhiteBalance>();
            _tone = AddComponent<Tonemapping>();
            _vignette = AddComponent<Vignette>();
            _split = AddComponent<SplitToning>();
            _grain = AddComponent<FilmGrain>();
            _chromatic = AddComponent<ChromaticAberration>();

            _volume = _go.AddComponent(Il2CppType.Of<Volume>()).Cast<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 1000f;
            _volume.weight = 1f;
            _volume.sharedProfile = _profile;
            _applied = default;
        }

        // VolumeProfile.Add<T> is generic; building the component and adding it to the list avoids
        // depending on an IL2CPP instantiation of it.
        private T AddComponent<T>() where T : VolumeComponent
        {
            var c = ScriptableObject.CreateInstance(Il2CppType.Of<T>()).Cast<T>();
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.active = true;
            _profile.components.Add(c);
            _profile.isDirty = true;
            return c;
        }

        private void Apply()
        {
            // bloom
            bool bloomOn = BloomSetting != BloomLevel.Off;
            _bloom.active = bloomOn;
            Set(_bloom.intensity, BloomSetting switch { BloomLevel.High => 1.2f, BloomLevel.Dreamy => 2.2f, _ => 0.4f });
            Set(_bloom.threshold, BloomSetting switch { BloomLevel.High => 0.8f, BloomLevel.Dreamy => 0.55f, _ => 1f });
            Set(_bloom.scatter, BloomSetting == BloomLevel.Dreamy ? 0.85f : 0.7f);

            // colour grade
            float exposure = 0f, contrast = 0f, saturation = 0f, temperature = 0f, tint = 0f;
            Color? splitShadows = null, splitHighlights = null;
            switch (GradeSetting)
            {
                case Grade.Vivid: contrast = 12f; saturation = 22f; exposure = 0.1f; break;
                case Grade.Warm: temperature = 25f; tint = 4f; saturation = 8f; break;
                case Grade.Cool: temperature = -25f; tint = -3f; contrast = 6f; break;
                case Grade.Mono: saturation = -100f; contrast = 18f; break;
                case Grade.Cinematic: // teal shadows, orange highlights
                    contrast = 15f; saturation = 5f;
                    splitShadows = new Color(0.25f, 0.55f, 0.6f); splitHighlights = new Color(0.95f, 0.65f, 0.4f);
                    break;
                case Grade.Sunset:
                    temperature = 35f; tint = 8f; saturation = 12f; contrast = 6f;
                    splitShadows = new Color(0.45f, 0.3f, 0.6f); splitHighlights = new Color(1f, 0.7f, 0.45f);
                    break;
                case Grade.Pastel: contrast = -15f; saturation = -25f; exposure = 0.2f; break;
                case Grade.Noir: saturation = -100f; contrast = 45f; exposure = -0.2f; break;
            }
            exposure += Exposures[Mathf.Clamp(ExposureIndex, 0, Exposures.Length - 1)];
            bool graded = GradeSetting != Grade.Default || exposure != 0f;
            _color.active = graded;
            Set(_color.postExposure, exposure);
            Set(_color.contrast, contrast);
            Set(_color.saturation, saturation);
            _white.active = graded && (temperature != 0f || tint != 0f);
            Set(_white.temperature, temperature);
            Set(_white.tint, tint);

            _split.active = splitShadows.HasValue;
            if (splitShadows.HasValue)
            {
                SetColour(_split.shadows, splitShadows.Value);
                SetColour(_split.highlights, splitHighlights.Value);
                Set(_split.balance, 0f);
            }

            // tonemapping
            _tone.active = ToneSetting != Tone.Game;
            _tone.mode.value = ToneSetting == Tone.Neutral ? TonemappingMode.Neutral : TonemappingMode.ACES;
            _tone.mode.overrideState = true;

            // vignette
            _vignette.active = VignetteOn;
            Set(_vignette.intensity, 0.3f);
            Set(_vignette.smoothness, 0.4f);

            // film grain and chromatic aberration
            _grain.active = Grain != Amount.Off;
            _grain.type.value = FilmGrainLookup.Thin1;
            _grain.type.overrideState = true;
            Set(_grain.intensity, Grain == Amount.Strong ? 0.6f : 0.25f);
            Set(_grain.response, 0.8f);
            _chromatic.active = Chromatic != Amount.Off;
            Set(_chromatic.intensity, Chromatic == Amount.Strong ? 0.6f : 0.2f);

            _profile.isDirty = true;
        }

        private static void Set(VolumeParameter<float> p, float value)
        {
            p.value = value;
            p.overrideState = true;
        }

        private static void SetColour(VolumeParameter<Color> p, Color value)
        {
            p.value = value;
            p.overrideState = true;
        }

        // URP only runs volumes on cameras with post processing on; turn it on for the main camera.
        private void HookCamera()
        {
            var cam = Camera.main;
            if (cam == null)
                return;
            var data = cam.GetComponent(Il2CppType.Of<UniversalAdditionalCameraData>());
            var urp = data != null ? data.TryCast<UniversalAdditionalCameraData>() : null;
            if (urp == null)
                return;
            if (_camera == null || _camera.Pointer != urp.Pointer)
            {
                ReleaseCamera();
                _camera = urp;
                _cameraHadPost = urp.renderPostProcessing;
                // the volume has to sit on a layer the camera's volume mask includes
                int mask = urp.volumeLayerMask.value;
                for (int i = 0; i < 32; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        _go.layer = i;
                        break;
                    }
                }
            }
            if (!urp.renderPostProcessing)
                urp.renderPostProcessing = true;
        }

        private void ReleaseCamera()
        {
            if (_camera != null)
                _camera.renderPostProcessing = _cameraHadPost;
            _camera = null;
        }

        private void Teardown()
        {
            ReleaseCamera();
            if (_go != null)
                Object.Destroy(_go);
            if (_profile != null)
            {
                foreach (var c in _profile.components)
                    if (c != null)
                        Object.Destroy(c);
                Object.Destroy(_profile);
            }
            _go = null;
            _volume = null;
            _profile = null;
            _applied = default;
        }
    }
}
