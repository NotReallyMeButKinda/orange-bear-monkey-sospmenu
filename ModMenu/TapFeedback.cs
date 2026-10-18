using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.XR;
using Object = UnityEngine.Object;
using XRInputDevice = UnityEngine.XR.InputDevice;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Hand-tap feedback for the arm locomotion: a 3D tap sound at the contact point and a haptic
    /// pulse on that controller, both scaled by how hard the hand hit.
    /// Drop .wav files (PCM 8/16/24-bit or float) into UserData/GorillaLocomotion/Sounds to replace
    /// the built-in synthesized tap. AudioSource.pitch and AudioClip.SetData are stripped from this
    /// build, so clips are filled through the create callback and pitch variety is pre-rendered.
    /// </summary>
    internal sealed class TapFeedback
    {
        private const float Cooldown = 0.1f;
        private const float MinSpeed = 0.25f; // m/s; slower contacts are silent
        private const float FullSpeed = 4f;   // m/s for full volume / vibration
        private static readonly float[] Pitches = { 0.86f, 0.93f, 1f, 1.07f, 1.15f };

        public static readonly string SoundsFolder =
            Path.Combine(MelonEnvironment.UserDataDirectory, "GorillaLocomotion", "Sounds");

        public bool Sounds = true;
        public bool Haptics = true;
        public float Volume = 0.8f;

        private readonly List<AudioClip> _clips = new();
        private readonly System.Random _rng = new();
        private AudioSource _leftSource, _rightSource;
        private float _leftNext, _rightNext;
        private bool _built;

        public void Tap(bool left, Vector3 point, float speed)
        {
            if (speed < MinSpeed)
                return;
            float now = Time.unscaledTime;
            if (now < (left ? _leftNext : _rightNext))
                return;
            if (left)
                _leftNext = now + Cooldown;
            else
                _rightNext = now + Cooldown;

            float strength = Mathf.InverseLerp(MinSpeed, FullSpeed, speed);
            if (Haptics)
            {
                XRInputDevice dev = InputDevices.GetDeviceAtXRNode(left ? XRNode.LeftHand : XRNode.RightHand);
                if (dev.isValid)
                    dev.SendHapticImpulse(0u, Mathf.Lerp(0.12f, 0.9f, strength), Mathf.Lerp(0.03f, 0.09f, strength));
            }
            if (Sounds && EnsureAudio())
            {
                var src = left ? _leftSource : _rightSource;
                src.transform.position = point;
                src.PlayOneShot(_clips[_rng.Next(_clips.Count)], Volume * Mathf.Lerp(0.2f, 1f, strength));
            }
        }

        // ---- setup

        private bool EnsureAudio()
        {
            if (_leftSource == null || _rightSource == null)
            {
                _leftSource = MakeSource("SocialSpaceModMenu.TapLeft");
                _rightSource = MakeSource("SocialSpaceModMenu.TapRight");
            }
            if (!_built)
            {
                _built = true;
                try
                {
                    BuildClips();
                }
                catch (Exception e)
                {
                    Melon<ModMenu>.Logger.Warning($"Tap sounds unavailable: {e.Message}");
                }
            }
            return _clips.Count > 0 && _leftSource != null && _rightSource != null;
        }

        private static AudioSource MakeSource(string name)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            var src = go.AddComponent(Il2CppType.Of<AudioSource>()).Cast<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            return src;
        }

        private void BuildClips()
        {
            var bases = new List<(float[] samples, int rate, string name)>();
            if (Directory.Exists(SoundsFolder))
            {
                foreach (var file in Directory.GetFiles(SoundsFolder, "*.wav"))
                {
                    if (TryReadWav(file, out var samples, out int rate))
                        bases.Add((samples, rate, Path.GetFileNameWithoutExtension(file)));
                    else
                        Melon<ModMenu>.Logger.Warning($"Skipped unsupported WAV: {file}");
                }
            }
            if (bases.Count == 0)
                bases.Add((SynthesizeTap(44100), 44100, "tap"));

            foreach (var (samples, rate, name) in bases)
                foreach (float pitch in Pitches)
                    _clips.Add(MakeClip($"{name}_{pitch:0.00}", Resample(samples, pitch), rate));
            Melon<ModMenu>.Logger.Msg($"Tap sounds ready: {bases.Count} source(s), {_clips.Count} clips");
        }

        internal static AudioClip MakeClip(string name, float[] samples, int rate)
        {
            int pos = 0;
            Action<Il2CppStructArray<float>> fill = data =>
            {
                for (int i = 0; i < data.Length; i++)
                    data[i] = pos < samples.Length ? samples[pos++] : 0f;
            };
            var callback = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(fill);
            var clip = AudioClip.Create(name, Math.Max(1, samples.Length), 1, rate, false, callback);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
        }

        // ---- sound data

        // A short knock: a pitch-dropping body tone plus a soft noise click, quick attack, fast decay.
        private static float[] SynthesizeTap(int rate)
        {
            var rng = new System.Random(1234);
            int n = (int)(rate * 0.12f);
            var s = new float[n];
            double phase = 0;
            float noiseLp = 0f, peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float freq = 120f + 150f * Mathf.Exp(-t * 45f);
                phase += 2 * Math.PI * freq / rate;
                float body = (float)Math.Sin(phase);
                noiseLp += ((float)rng.NextDouble() * 2f - 1f - noiseLp) * 0.35f;
                float env = (1f - Mathf.Exp(-t * 900f)) * Mathf.Exp(-t * 38f);
                float click = noiseLp * Mathf.Exp(-t * 160f);
                s[i] = env * (0.75f * body + 0.6f * click);
                peak = Mathf.Max(peak, Mathf.Abs(s[i]));
            }
            if (peak > 0f)
                for (int i = 0; i < n; i++)
                    s[i] *= 0.9f / peak;
            return s;
        }

        // Linear-interpolation resample; pitch > 1 plays higher and shorter.
        private static float[] Resample(float[] src, float pitch)
        {
            if (Mathf.Approximately(pitch, 1f))
                return src;
            int n = Math.Max(1, (int)(src.Length / pitch));
            var dst = new float[n];
            for (int i = 0; i < n; i++)
            {
                float x = i * pitch;
                int a = (int)x;
                int b = Math.Min(a + 1, src.Length - 1);
                float f = x - a;
                dst[i] = a < src.Length ? src[a] + (src[b] - src[a]) * f : 0f;
            }
            return dst;
        }

        // Minimal RIFF/WAVE reader, mixed down to mono.
        private static bool TryReadWav(string path, out float[] samples, out int rate)
        {
            samples = null;
            rate = 0;
            byte[] d;
            try
            {
                d = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return false;
            }
            if (d.Length < 44 || BitConverter.ToUInt32(d, 0) != 0x46464952 || BitConverter.ToUInt32(d, 8) != 0x45564157)
                return false; // "RIFF" / "WAVE"

            int format = 0, channels = 0, bits = 0, dataOff = -1, dataLen = 0;
            for (int p = 12; p + 8 <= d.Length;)
            {
                uint id = BitConverter.ToUInt32(d, p);
                int len = BitConverter.ToInt32(d, p + 4);
                int body = p + 8;
                if (len < 0 || body + len > d.Length)
                    len = d.Length - body;
                if (id == 0x20746D66 && len >= 16) // "fmt "
                {
                    format = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    bits = BitConverter.ToUInt16(d, body + 14);
                    if (format == 0xFFFE && len >= 26) // WAVE_FORMAT_EXTENSIBLE: real format in the subtype
                        format = BitConverter.ToUInt16(d, body + 24);
                }
                else if (id == 0x61746164) // "data"
                {
                    dataOff = body;
                    dataLen = len;
                }
                p = body + len + (len & 1);
            }
            if (dataOff < 0 || channels <= 0 || rate <= 0)
                return false;
            int bytesPer = bits / 8;
            if (!((format == 1 && bytesPer is 1 or 2 or 3) || (format == 3 && bytesPer == 4)))
                return false;

            int frames = dataLen / (bytesPer * channels);
            samples = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int o = dataOff + (f * channels + c) * bytesPer;
                    sum += bytesPer switch
                    {
                        1 => (d[o] - 128) / 128f,
                        2 => BitConverter.ToInt16(d, o) / 32768f,
                        3 => ((d[o] | (d[o + 1] << 8) | ((sbyte)d[o + 2] << 16))) / 8388608f,
                        _ => BitConverter.ToSingle(d, o),
                    };
                }
                samples[f] = sum / channels;
            }
            return frames > 0;
        }
    }
}
