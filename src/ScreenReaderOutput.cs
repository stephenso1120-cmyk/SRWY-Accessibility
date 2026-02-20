using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace SRWYAccess
{
    /// <summary>
    /// Wrapper for Tolk screen reader communication library.
    /// Provides P/Invoke access to Tolk.dll for NVDA/JAWS output.
    /// </summary>
    public static class ScreenReaderOutput
    {
        private static bool _loaded;
        private static string _lastMessage = "";

        // Deduplication: prevent the same message from being spoken repeatedly
        // within a short window (e.g. handler detects same change twice)
        private static string _lastSpokenText = "";
        private static long _lastSpokenTicks;
        private static readonly long DedupWindowTicks = ModConfig.DedupWindowMs * TimeSpan.TicksPerMillisecond;

        // Rate limiting: prevent overwhelming Tolk.dll/screen reader with rapid calls
        private static long _lastTolkCallTicks;
        private static readonly long MinTolkIntervalTicks = 50 * TimeSpan.TicksPerMillisecond; // 50ms minimum between calls
        private static int _tolkCallsThisSecond;
        private static long _tolkCallsSecondStart;
        private const int MaxTolkCallsPerSecond = 10; // safety limit

        #region Tolk P/Invoke

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Load();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Unload();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_IsLoaded();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_HasSpeech();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_HasBraille();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Output(string str, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Speak(string str, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Braille(string str);

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Silence();

        [DllImport("Tolk.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr Tolk_DetectScreenReader();

        #endregion

        /// <summary>
        /// Whether Tolk is loaded and a screen reader is detected.
        /// </summary>
        public static bool IsAvailable => _loaded;

        /// <summary>
        /// The last message that was spoken.
        /// </summary>
        public static string LastMessage => _lastMessage;

        /// <summary>
        /// Initialize Tolk. Call once at mod startup.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                Tolk_Load();
                _loaded = Tolk_IsLoaded();

                if (_loaded)
                {
                    bool hasSpeech = Tolk_HasSpeech();
                    bool hasBraille = Tolk_HasBraille();
                    IntPtr srNamePtr = Tolk_DetectScreenReader();
                    string srName = srNamePtr != IntPtr.Zero ? Marshal.PtrToStringUni(srNamePtr) : "Unknown";

                    MelonLogger.Msg($"[SRWYAccess] Tolk loaded. Screen reader: {srName}, Speech: {hasSpeech}, Braille: {hasBraille}");
                }
                else
                {
                    MelonLogger.Warning("[SRWYAccess] Tolk loaded but no screen reader detected.");
                }
            }
            catch (DllNotFoundException)
            {
                MelonLogger.Error("[SRWYAccess] Tolk.dll not found. Screen reader output disabled.");
                _loaded = false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SRWYAccess] Failed to initialize Tolk: {ex.Message}");
                _loaded = false;
            }
        }

        /// <summary>
        /// Speak text and show on braille display. Interrupts current speech.
        /// Deduplicates identical messages within a short window to prevent spam.
        /// Rate-limited to prevent overwhelming Tolk.dll/screen reader.
        /// </summary>
        public static void Say(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            long now = DateTime.UtcNow.Ticks;

            // Deduplicate: skip if same message within 300ms window
            if (message == _lastSpokenText && (now - _lastSpokenTicks) < DedupWindowTicks)
                return;

            // Rate limiting: enforce minimum interval between ANY calls
            if ((now - _lastTolkCallTicks) < MinTolkIntervalTicks)
            {
                try { MelonLogger.Warning($"[SRWYAccess] [SR-THROTTLED] {message}"); } catch { }
                return;
            }

            // Rate limiting: per-second call counter
            if (now - _tolkCallsSecondStart > TimeSpan.TicksPerSecond)
            {
                _tolkCallsThisSecond = 0;
                _tolkCallsSecondStart = now;
            }
            if (_tolkCallsThisSecond >= MaxTolkCallsPerSecond)
            {
                try { MelonLogger.Warning($"[SRWYAccess] [SR-RATELIMIT] {message}"); } catch { }
                return;
            }

            _lastSpokenText = message;
            _lastSpokenTicks = now;
            _lastTolkCallTicks = now;
            _tolkCallsThisSecond++;
            _lastMessage = message;

            try { MelonLogger.Msg($"[SRWYAccess] [SR] {message}"); } catch { }

            if (!_loaded) return;

            try
            {
                Tolk_Output(message, true);
            }
            catch (Exception ex)
            {
                try { MelonLogger.Error($"[SRWYAccess] Tolk_Output failed: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// Speak text without interrupting current speech (queued).
        /// Participates in deduplication to prevent repeated queued messages.
        /// Rate-limited to prevent overwhelming Tolk.dll/screen reader.
        /// </summary>
        public static void SayQueued(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            long now = DateTime.UtcNow.Ticks;

            // Deduplicate: skip if same message within dedup window
            if (message == _lastSpokenText && (now - _lastSpokenTicks) < DedupWindowTicks)
                return;

            // Rate limiting: enforce minimum interval between ANY calls
            if ((now - _lastTolkCallTicks) < MinTolkIntervalTicks)
            {
                try { MelonLogger.Warning($"[SRWYAccess] [SR+-THROTTLED] {message}"); } catch { }
                return;
            }

            // Rate limiting: per-second call counter
            if (now - _tolkCallsSecondStart > TimeSpan.TicksPerSecond)
            {
                _tolkCallsThisSecond = 0;
                _tolkCallsSecondStart = now;
            }
            if (_tolkCallsThisSecond >= MaxTolkCallsPerSecond)
            {
                try { MelonLogger.Warning($"[SRWYAccess] [SR+-RATELIMIT] {message}"); } catch { }
                return;
            }

            _lastSpokenText = message;
            _lastSpokenTicks = now;
            _lastTolkCallTicks = now;
            _tolkCallsThisSecond++;
            _lastMessage = message;

            try { MelonLogger.Msg($"[SRWYAccess] [SR+] {message}"); } catch { }

            if (!_loaded) return;

            try
            {
                Tolk_Output(message, false);
            }
            catch (Exception ex)
            {
                try { MelonLogger.Error($"[SRWYAccess] Tolk_Output failed: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// Stop current speech immediately.
        /// </summary>
        public static void Silence()
        {
            if (!_loaded) return;

            try
            {
                Tolk_Silence();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SRWYAccess] Tolk_Silence failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanup Tolk on mod unload.
        /// </summary>
        public static void Shutdown()
        {
            if (_loaded)
            {
                try
                {
                    Tolk_Unload();
                }
                catch { }
                _loaded = false;
            }
        }
    }
}
