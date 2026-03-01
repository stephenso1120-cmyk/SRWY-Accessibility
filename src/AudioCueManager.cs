using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace SRWYAccess
{
    /// <summary>
    /// Audio cue types for accessibility feedback.
    /// Each cue uses a distinct waveform/pattern for instant recognition.
    /// </summary>
    public enum AudioCue
    {
        TileEmpty,   // Single soft tick: cursor on empty tile
        TileAlly,    // Double ascending beep: cursor on ally unit
        TileEnemy    // Sharp buzz: cursor on enemy unit
    }

    /// <summary>
    /// Manages audio cue playback using Unity's AudioSource system.
    /// Routes through Unity's audio pipeline (same as the game), eliminating
    /// conflicts with the game's audio subsystem that occurred with winmm.dll
    /// PlaySound. Works reliably on all hardware configurations.
    ///
    /// Two-phase initialization:
    ///   Phase 1 (Initialize): Pre-generates float[] sample data on background thread.
    ///   Phase 2 (EnsureMainThreadInit): Creates Unity AudioSource + AudioClips on main thread.
    ///
    /// Three distinct sounds for map cursor:
    ///   Empty = soft low triangle wave "tock"
    ///   Ally  = two quick ascending sine beeps "di-dip"
    ///   Enemy = harsh square wave buzz "bzzt"
    /// </summary>
    public static class AudioCueManager
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int FadeMs = 5; // fade-in/fade-out to avoid click artifacts

        // Phase 1 state: sample data generated on background thread
        private static bool _samplesReady;
        private static readonly Dictionary<AudioCue, float[]> _sampleData = new();

        // Phase 2 state: Unity objects created on main thread
        private static bool _initialized;
        private static GameObject _audioGO;
        private static AudioSource _audioSource;
        private static readonly Dictionary<AudioCue, AudioClip> _clips = new();

        // Deduplication: prevent same cue from playing twice within DedupMs
        private static AudioCue _lastCue;
        private static long _lastPlayTicks;

        /// <summary>
        /// Phase 1: Generate sample data. Safe to call from any thread.
        /// Called during mod init (background thread, Phase 7).
        /// Unity AudioClip creation is deferred to EnsureMainThreadInit().
        /// </summary>
        public static void Initialize()
        {
            try
            {
                GenerateAllCues();
                _samplesReady = true;
                DebugHelper.Write($"AudioCueManager: {_sampleData.Count} cues generated (samples ready)");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"AudioCueManager: sample generation failed: {ex.Message}");
                _samplesReady = false;
            }
        }

        /// <summary>
        /// Phase 2: Create Unity AudioSource and AudioClips from pre-generated samples.
        /// Must be called on Unity main thread. Called once from OnMainThreadUpdate.
        /// Creates a hidden persistent GameObject with AudioSource that bypasses
        /// all game audio effects (reverb, EQ, etc.) for clean cue playback.
        /// </summary>
        public static void EnsureMainThreadInit()
        {
            if (_initialized || !_samplesReady) return;

            try
            {
                // Create persistent hidden GameObject with AudioSource
                _audioGO = new GameObject("SRWYAccess_Audio");
                UnityEngine.Object.DontDestroyOnLoad(_audioGO);
                _audioGO.hideFlags = HideFlags.HideAndDontSave;

                _audioSource = _audioGO.AddComponent<AudioSource>();
                _audioSource.spatialBlend = 0f;              // 2D sound (no spatialization)
                _audioSource.bypassEffects = true;            // skip game audio effects
                _audioSource.bypassListenerEffects = true;    // skip listener effects
                _audioSource.bypassReverbZones = true;        // skip reverb zones
                _audioSource.volume = 1.0f;                   // volume baked into samples
                _audioSource.playOnAwake = false;

                // Create AudioClips from pre-generated sample data
                foreach (var kvp in _sampleData)
                {
                    var samples = kvp.Value;
                    var clip = AudioClip.Create(kvp.Key.ToString(), samples.Length, Channels, SampleRate, false);

                    // Convert managed float[] to IL2CPP array for SetData
                    var il2cppSamples = new Il2CppStructArray<float>(samples.Length);
                    for (int i = 0; i < samples.Length; i++)
                        il2cppSamples[i] = samples[i];

                    clip.SetData(il2cppSamples, 0);
                    _clips[kvp.Key] = clip;
                }

                // Free managed sample arrays (no longer needed)
                _sampleData.Clear();

                _initialized = true;
                DebugHelper.Write($"AudioCueManager: Unity AudioSource initialized, {_clips.Count} clips loaded");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"AudioCueManager: main thread init failed: {ex.Message}");
                _initialized = false;
            }
        }

        /// <summary>
        /// Play an audio cue. Non-blocking.
        /// Safe to call from main thread context - never throws.
        /// Setting clip + Play() interrupts any previous cue automatically.
        /// </summary>
        public static void Play(AudioCue cue)
        {
            if (!_initialized || !ModConfig.AudioCuesEnabled)
                return;

            try
            {
                // Dedup: skip if same cue within threshold
                long now = DateTime.UtcNow.Ticks;
                long dedupTicks = ModConfig.AudioCueDedupMs * TimeSpan.TicksPerMillisecond;
                if (cue == _lastCue && (now - _lastPlayTicks) < dedupTicks)
                    return;

                if (!_clips.TryGetValue(cue, out var clip) || (object)clip == null)
                    return;

                // Verify AudioSource is still alive (IL2CPP object check)
                if ((object)_audioSource == null) return;

                _audioSource.clip = clip;
                _audioSource.Play();
                _lastCue = cue;
                _lastPlayTicks = now;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"AudioCue: Play error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop any currently playing audio cue.
        /// </summary>
        public static void Stop()
        {
            if (!_initialized) return;
            try
            {
                if ((object)_audioSource != null)
                    _audioSource.Stop();
            }
            catch { }
        }

        /// <summary>
        /// Cleanup on mod shutdown. Nulls references and clears state.
        /// Does not call Unity Object.Destroy (may not be on main thread).
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized && !_samplesReady) return;

            try
            {
                if ((object)_audioSource != null)
                {
                    try { _audioSource.Stop(); } catch { }
                }

                _clips.Clear();
                _sampleData.Clear();
                _audioGO = null;
                _audioSource = null;
                _initialized = false;
                _samplesReady = false;

                DebugHelper.Write("AudioCueManager: shutdown complete");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"AudioCueManager: shutdown error: {ex.Message}");
            }
        }

        private static void GenerateAllCues()
        {
            float vol = ModConfig.AudioCueVolume;

            // Empty tile: soft low triangle wave "tock" (single short note)
            _sampleData[AudioCue.TileEmpty] = GenerateTriangleTone(330, 50, vol * 0.7f);

            // Ally unit: two quick ascending sine beeps "di-dip"
            _sampleData[AudioCue.TileAlly] = GenerateDoubleBeep(480, 640, 35, 20, vol);

            // Enemy unit: harsh square wave buzz "bzzt"
            _sampleData[AudioCue.TileEnemy] = GenerateSquareBuzz(880, 12, 80, vol);
        }

        /// <summary>
        /// Triangle wave: softer, rounder than sine. Good for neutral/empty cue.
        /// Output range: [-1.0, 1.0] scaled by volume.
        /// </summary>
        private static float[] GenerateTriangleTone(int frequencyHz, int durationMs, float volume)
        {
            int sampleCount = SampleRate * durationMs / 1000;
            int fadeSamples = SampleRate * FadeMs / 1000;
            float[] samples = new float[sampleCount];
            float amplitude = Math.Min(volume, 1.0f);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / SampleRate;
                // Triangle wave via asin(sin(x))
                double phase = 2.0 * Math.PI * frequencyHz * t;
                float value = (float)(Math.Asin(Math.Sin(phase)) * (2.0 / Math.PI) * amplitude);

                // Fade-in/out
                if (i < fadeSamples)
                    value *= (float)i / fadeSamples;
                if (i >= sampleCount - fadeSamples)
                    value *= (float)(sampleCount - 1 - i) / fadeSamples;

                samples[i] = ClampF(value);
            }

            return samples;
        }

        /// <summary>
        /// Two quick ascending sine beeps with a gap. Friendly "di-dip" pattern.
        /// Output range: [-1.0, 1.0] scaled by volume.
        /// </summary>
        private static float[] GenerateDoubleBeep(int freq1, int freq2, int beepMs, int gapMs, float volume)
        {
            int beepSamples = SampleRate * beepMs / 1000;
            int gapSamples = SampleRate * gapMs / 1000;
            int totalSamples = beepSamples + gapSamples + beepSamples;
            int fadeSamples = SampleRate * FadeMs / 1000;
            float[] samples = new float[totalSamples];
            float amplitude = Math.Min(volume, 1.0f);

            // First beep (lower pitch)
            for (int i = 0; i < beepSamples; i++)
            {
                double t = (double)i / SampleRate;
                float value = (float)(Math.Sin(2.0 * Math.PI * freq1 * t) * amplitude);

                if (i < fadeSamples)
                    value *= (float)i / fadeSamples;
                if (i >= beepSamples - fadeSamples)
                    value *= (float)(beepSamples - 1 - i) / fadeSamples;

                samples[i] = ClampF(value);
            }

            // Gap: silence (already 0)

            // Second beep (higher pitch)
            int offset = beepSamples + gapSamples;
            for (int i = 0; i < beepSamples; i++)
            {
                double t = (double)i / SampleRate;
                float value = (float)(Math.Sin(2.0 * Math.PI * freq2 * t) * amplitude);

                if (i < fadeSamples)
                    value *= (float)i / fadeSamples;
                if (i >= beepSamples - fadeSamples)
                    value *= (float)(beepSamples - 1 - i) / fadeSamples;

                samples[offset + i] = ClampF(value);
            }

            return samples;
        }

        /// <summary>
        /// Square wave with amplitude modulation (tremolo). Creates a harsh "bzzt" buzz.
        /// tremoloHz controls how fast the volume pulses (higher = more aggressive).
        /// Output range: [-1.0, 1.0] scaled by volume.
        /// </summary>
        private static float[] GenerateSquareBuzz(int frequencyHz, int tremoloHz, int durationMs, float volume)
        {
            int sampleCount = SampleRate * durationMs / 1000;
            int fadeSamples = SampleRate * FadeMs / 1000;
            float[] samples = new float[sampleCount];
            float amplitude = Math.Min(volume, 1.0f);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / SampleRate;

                // Square wave: +1 or -1
                double square = Math.Sin(2.0 * Math.PI * frequencyHz * t) >= 0 ? 1.0 : -1.0;

                // Tremolo (amplitude modulation): pulsing between 0.3 and 1.0
                double tremolo = 0.65 + 0.35 * Math.Sin(2.0 * Math.PI * tremoloHz * t);

                float value = (float)(square * tremolo * amplitude);

                // Fade-in/out
                if (i < fadeSamples)
                    value *= (float)i / fadeSamples;
                if (i >= sampleCount - fadeSamples)
                    value *= (float)(sampleCount - 1 - i) / fadeSamples;

                samples[i] = ClampF(value);
            }

            return samples;
        }

        private static float ClampF(float value)
        {
            if (value > 1.0f) return 1.0f;
            if (value < -1.0f) return -1.0f;
            return value;
        }
    }
}
