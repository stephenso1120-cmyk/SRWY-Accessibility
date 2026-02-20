using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    /// Manages audio cue playback using in-memory WAV generation
    /// and Windows PlaySound API. No external audio files required.
    ///
    /// Three distinct sounds for map cursor:
    ///   Empty = soft low triangle wave "tock"
    ///   Ally  = two quick ascending sine beeps "di-dip"
    ///   Enemy = harsh square wave buzz "bzzt"
    /// </summary>
    public static class AudioCueManager
    {
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_MEMORY = 0x0004;
        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;

        private const int SampleRate = 22050;
        private const int BitsPerSample = 16;
        private const int Channels = 1;
        private const int FadeMs = 2; // fade-in/fade-out to avoid click artifacts

        private static bool _initialized;
        private static readonly Dictionary<AudioCue, IntPtr> _buffers = new();
        private static readonly Dictionary<AudioCue, int> _bufferSizes = new();

        // Deduplication: prevent same cue from playing twice within DedupMs
        private static AudioCue _lastCue;
        private static long _lastPlayTicks;

        /// <summary>
        /// Initialize audio cue system. Generate all tone buffers.
        /// Call once during mod init (Phase 7).
        /// </summary>
        public static void Initialize()
        {
            try
            {
                GenerateAllCues();
                _initialized = true;
                DebugHelper.Write($"AudioCueManager: initialized, {_buffers.Count} cues loaded");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"AudioCueManager: init failed: {ex.Message}");
                _initialized = false;
            }
        }

        /// <summary>
        /// Play an audio cue. Non-blocking (SND_ASYNC).
        /// Safe to call from any context — never throws.
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

                if (!_buffers.TryGetValue(cue, out IntPtr buffer) || buffer == IntPtr.Zero)
                    return;

                bool result = PlaySound(buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                _lastCue = cue;
                _lastPlayTicks = now;

                if (!result)
                    DebugHelper.Write($"AudioCue: PlaySound returned false for {cue}");
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
            try
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, SND_ASYNC);
            }
            catch { }
        }

        private static void GenerateAllCues()
        {
            float vol = ModConfig.AudioCueVolume;

            // Empty tile: soft low triangle wave "tock" (single short note)
            AddCue(AudioCue.TileEmpty, GenerateTriangleTone(330, 50, vol * 0.7f));

            // Ally unit: two quick ascending sine beeps "di-dip"
            AddCue(AudioCue.TileAlly, GenerateDoubleBeep(480, 640, 35, 20, vol));

            // Enemy unit: harsh square wave buzz "bzzt"
            AddCue(AudioCue.TileEnemy, GenerateSquareBuzz(880, 12, 80, vol));
        }

        private static void AddCue(AudioCue cue, byte[] wavData)
        {
            if (wavData == null || wavData.Length == 0) return;

            IntPtr ptr = Marshal.AllocHGlobal(wavData.Length);
            Marshal.Copy(wavData, 0, ptr, wavData.Length);
            _buffers[cue] = ptr;
            _bufferSizes[cue] = wavData.Length;
        }

        /// <summary>
        /// Triangle wave: softer, rounder than sine. Good for neutral/empty cue.
        /// </summary>
        private static byte[] GenerateTriangleTone(int frequencyHz, int durationMs, float volume)
        {
            int sampleCount = SampleRate * durationMs / 1000;
            int fadeSamples = SampleRate * FadeMs / 1000;
            short[] samples = new short[sampleCount];
            double amplitude = short.MaxValue * Math.Min(volume, 1.0);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / SampleRate;
                // Triangle wave via asin(sin(x))
                double phase = 2.0 * Math.PI * frequencyHz * t;
                double value = Math.Asin(Math.Sin(phase)) * (2.0 / Math.PI) * amplitude;

                // Fade-in/out
                if (i < fadeSamples)
                    value *= (double)i / fadeSamples;
                if (i >= sampleCount - fadeSamples)
                    value *= (double)(sampleCount - 1 - i) / fadeSamples;

                samples[i] = Clamp(value);
            }

            return BuildWav(samples);
        }

        /// <summary>
        /// Two quick ascending sine beeps with a gap. Friendly "di-dip" pattern.
        /// </summary>
        private static byte[] GenerateDoubleBeep(int freq1, int freq2, int beepMs, int gapMs, float volume)
        {
            int beepSamples = SampleRate * beepMs / 1000;
            int gapSamples = SampleRate * gapMs / 1000;
            int totalSamples = beepSamples + gapSamples + beepSamples;
            int fadeSamples = SampleRate * FadeMs / 1000;
            short[] samples = new short[totalSamples];
            double amplitude = short.MaxValue * Math.Min(volume, 1.0);

            // First beep (lower pitch)
            for (int i = 0; i < beepSamples; i++)
            {
                double t = (double)i / SampleRate;
                double value = Math.Sin(2.0 * Math.PI * freq1 * t) * amplitude;

                if (i < fadeSamples)
                    value *= (double)i / fadeSamples;
                if (i >= beepSamples - fadeSamples)
                    value *= (double)(beepSamples - 1 - i) / fadeSamples;

                samples[i] = Clamp(value);
            }

            // Gap: silence (already 0)

            // Second beep (higher pitch)
            int offset = beepSamples + gapSamples;
            for (int i = 0; i < beepSamples; i++)
            {
                double t = (double)i / SampleRate;
                double value = Math.Sin(2.0 * Math.PI * freq2 * t) * amplitude;

                if (i < fadeSamples)
                    value *= (double)i / fadeSamples;
                if (i >= beepSamples - fadeSamples)
                    value *= (double)(beepSamples - 1 - i) / fadeSamples;

                samples[offset + i] = Clamp(value);
            }

            return BuildWav(samples);
        }

        /// <summary>
        /// Square wave with amplitude modulation (tremolo). Creates a harsh "bzzt" buzz.
        /// tremoloHz controls how fast the volume pulses (higher = more aggressive).
        /// </summary>
        private static byte[] GenerateSquareBuzz(int frequencyHz, int tremoloHz, int durationMs, float volume)
        {
            int sampleCount = SampleRate * durationMs / 1000;
            int fadeSamples = SampleRate * FadeMs / 1000;
            short[] samples = new short[sampleCount];
            double amplitude = short.MaxValue * Math.Min(volume, 1.0);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / SampleRate;

                // Square wave: +1 or -1
                double square = Math.Sin(2.0 * Math.PI * frequencyHz * t) >= 0 ? 1.0 : -1.0;

                // Tremolo (amplitude modulation): pulsing between 0.3 and 1.0
                double tremolo = 0.65 + 0.35 * Math.Sin(2.0 * Math.PI * tremoloHz * t);

                double value = square * tremolo * amplitude;

                // Fade-in/out
                if (i < fadeSamples)
                    value *= (double)i / fadeSamples;
                if (i >= sampleCount - fadeSamples)
                    value *= (double)(sampleCount - 1 - i) / fadeSamples;

                samples[i] = Clamp(value);
            }

            return BuildWav(samples);
        }

        private static short Clamp(double value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }

        /// <summary>
        /// Build a complete WAV file (RIFF header + PCM data) from sample array.
        /// </summary>
        private static byte[] BuildWav(short[] samples)
        {
            int dataSize = samples.Length * (BitsPerSample / 8);
            int fileSize = 44 + dataSize; // 44-byte header + data
            byte[] wav = new byte[fileSize];
            int pos = 0;

            // RIFF header
            WriteBytes(wav, ref pos, "RIFF");
            WriteInt32(wav, ref pos, fileSize - 8);  // chunk size
            WriteBytes(wav, ref pos, "WAVE");

            // fmt sub-chunk
            WriteBytes(wav, ref pos, "fmt ");
            WriteInt32(wav, ref pos, 16);             // sub-chunk size (PCM)
            WriteInt16(wav, ref pos, 1);              // audio format (1 = PCM)
            WriteInt16(wav, ref pos, (short)Channels);
            WriteInt32(wav, ref pos, SampleRate);
            WriteInt32(wav, ref pos, SampleRate * Channels * BitsPerSample / 8); // byte rate
            WriteInt16(wav, ref pos, (short)(Channels * BitsPerSample / 8));     // block align
            WriteInt16(wav, ref pos, (short)BitsPerSample);

            // data sub-chunk
            WriteBytes(wav, ref pos, "data");
            WriteInt32(wav, ref pos, dataSize);

            // PCM samples
            for (int i = 0; i < samples.Length; i++)
            {
                wav[pos++] = (byte)(samples[i] & 0xFF);
                wav[pos++] = (byte)((samples[i] >> 8) & 0xFF);
            }

            return wav;
        }

        private static void WriteBytes(byte[] buf, ref int pos, string text)
        {
            for (int i = 0; i < text.Length; i++)
                buf[pos++] = (byte)text[i];
        }

        private static void WriteInt32(byte[] buf, ref int pos, int value)
        {
            buf[pos++] = (byte)(value & 0xFF);
            buf[pos++] = (byte)((value >> 8) & 0xFF);
            buf[pos++] = (byte)((value >> 16) & 0xFF);
            buf[pos++] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt16(byte[] buf, ref int pos, short value)
        {
            buf[pos++] = (byte)(value & 0xFF);
            buf[pos++] = (byte)((value >> 8) & 0xFF);
        }
    }
}
