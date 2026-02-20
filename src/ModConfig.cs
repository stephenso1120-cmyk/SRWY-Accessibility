namespace SRWYAccess
{
    /// <summary>
    /// Centralized configuration for all mod thresholds, timings, and limits.
    /// Grouped by subsystem for easy tuning.
    ///
    /// Main thread architecture: mod logic runs on Unity main thread via
    /// native hook on InputManager.Update(). Poll cycles are throttled
    /// to every PollFrameInterval frames (~67ms at 60fps).
    /// Values in "poll cycles" = PollFrameInterval frames each.
    /// </summary>
    internal static class ModConfig
    {
        // ===== Main Thread Poll (ModCore) =====

        /// <summary>Initial delay before starting mod (ms). Lets MelonLoader finish init.</summary>
        public const int InitDelayMs = 8000;

        /// <summary>Max attempts to wait for IL2CPP / InputManager (each 500ms).</summary>
        public const int MaxInitAttempts = 120;

        /// <summary>Run handler updates every N frames. 4 frames ≈ 67ms at 60fps.</summary>
        public const int PollFrameInterval = 4;

        /// <summary>Faster poll interval for interactive modes. 2 frames ≈ 33ms at 60fps.
        /// Menu navigation, cursor tracking, and dialogue detection benefit from
        /// snappier response. Used for all modes except BATTLE_SCENE and guard mode.</summary>
        public const int FastPollFrameInterval = 2;

        /// <summary>Round-robin search slots: 0=GenericMenuReader, 1=Tutorial, 2=Adventure.</summary>
        public const int TotalSearchSlots = 3;

        /// <summary>Search cooldown after mode changes (poll cycles).
        /// Lets Unity finish loading new scene objects before we search.
        /// 3 cycles ≈ 200ms at 60fps.</summary>
        public const int SearchCooldownAfterChange = 3;

        /// <summary>Extended cooldown for ADVENTURE transitions (poll cycles).
        /// Major scene transitions (full scene destroy + load). Crash #28b showed
        /// that AV can occur 2.2s after TACTICAL→ADVENTURE. With guard mode (~530ms)
        /// + this cooldown, total protection must exceed ~2.5s.
        /// 30 cycles * ~67ms ≈ 2s at 60fps (+ ~530ms guard = ~2.5s total).</summary>
        public const int AdventureTransitionCooldown = 30;

        /// <summary>Extended cooldown for TACTICAL_PART → NONE transitions (poll cycles).
        /// Tactical transitions destroy command menu UI. At end of player phase the game
        /// also initializes enemy AI, which is heavier. 8 cycles ≈ 530ms at 60fps.</summary>
        public const int TacticalTransitionCooldown = 8;

        /// <summary>Cooldown after errors (poll cycles). 12 cycles ≈ 0.8s.</summary>
        public const int ErrorCooldownCycles = 12;

        /// <summary>Poll cycles of NONE-loading before probing for active UI handlers.
        /// Must be long enough to survive the slowest scene transitions (5-6s observed).
        /// 55 cycles * ~67ms ≈ 3.7 seconds. Guard mode covers the first ~1s of transitions,
        /// and post-guard immediate probe handles the fast-recovery path.</summary>
        public const int NoneProbeThreshold = 55;

        /// <summary>Heartbeat log interval in poll cycles (~30 seconds).</summary>
        public const int HeartbeatInterval = 450;

        /// <summary>Guard mode warmup cycles. During warmup, GST skips all SafeCall
        /// to avoid side effects while scene objects are in flux.</summary>
        public const int GuardWarmupCycles = 8;

        /// <summary>Guard mode stability threshold (poll cycles).
        /// After NoModeMatched, pointer must be unchanged for this many consecutive
        /// cycles before running the full 17-mode detection loop again.
        /// Prevents AccessViolationException from GetInputBehaviour on freed native
        /// objects during scene transitions. 15 cycles ≈ 1 second.</summary>
        public const int GuardStabilityThreshold = 15;

        /// <summary>Max total cycles in guard mode before force-exiting.
        /// Safety valve to prevent permanent guard lock. 900 cycles ≈ 60 seconds.</summary>
        public const int GuardMaxCycles = 900;

        /// <summary>Max poll cycles waiting for battle result before timeout.</summary>
        public const int ResultTimeout = 300;

        // ===== GenericMenuReader =====

        /// <summary>Miss count before declaring handler lost.</summary>
        public const int MenuMissThreshold = 3;

        /// <summary>Fault count before disabling GenericMenuReader.</summary>
        public const int MenuMaxFaults = 5;

        /// <summary>Unchanged cursor cycles before reducing poll frequency (~5s).</summary>
        public const int MenuStalePollLimit = 75;

        /// <summary>Probe interval when in stale mode (every Nth cycle).</summary>
        public const int MenuStaleProbeInterval = 5;

        /// <summary>Mode change re-announcement cooldown cycles.</summary>
        public const int MenuModeChangeCooldown = 10;

        /// <summary>Battle warmup: skip ALL handler code for first N poll cycles after
        /// entering BATTLE_SCENE. Provides minimal buffer for scene objects to load.
        /// Keep short - VEH protection + danger zone (polls 10-20) handle safety.
        /// 5 polls × ~67ms = ~330ms of silence.</summary>
        public const int BattleWarmupPolls = 5;

        // ===== BattleSubtitleHandler =====

        /// <summary>Unchanged subtitle cycles before entering stale mode.</summary>
        public const int BattleStaleLimit = 2;

        /// <summary>Probe interval when in stale mode.</summary>
        public const int BattleStaleProbeInterval = 5;

        /// <summary>Max retries for GetComponentsInChildren name reads.</summary>
        public const int BattleNamesMaxRetries = 3;

        // ===== AdventureDialogueHandler =====

        /// <summary>Unchanged text cycles before reducing poll frequency (~3s).</summary>
        public const int AdvStaleTextLimit = 45;

        /// <summary>Probe interval when in stale text mode.</summary>
        public const int AdvStaleProbeInterval = 5;

        /// <summary>Search cooldown cycles after dialogue refs released (~1.5s).</summary>
        public const int AdvSearchCooldown = 23;

        /// <summary>Effective AdvSearchCooldown. Reduced from 23 to 12 when SEH available (~800ms).</summary>
        public static int AdvSearchCooldownEffective = AdvSearchCooldown;

        /// <summary>Max stale text count to prevent overflow.</summary>
        public const int AdvMaxStaleCount = 10000;

        // ===== DialogHandler =====

        /// <summary>Re-read attempt interval (every Nth cycle).</summary>
        public const int DialogRereadInterval = 3;

        /// <summary>Max re-read attempts before announcing as-is.</summary>
        public const int DialogRereadMaxAttempts = 15;

        // ===== ScreenReaderOutput =====

        /// <summary>Deduplication window in milliseconds.</summary>
        public const int DedupWindowMs = 300;

        // ===== DebugHelper =====

        /// <summary>Flush log buffer every N writes.</summary>
        public const int LogFlushInterval = 5;

        // ===== Screen Review =====

        /// <summary>Max TMP items to read for info screens.</summary>
        public const int ReviewInfoMaxItems = 40;

        /// <summary>Max TMP items for ReadAllVisibleText fallback.</summary>
        public const int ReviewVisibleMaxItems = 12;

        /// <summary>Max chars for ReadAllVisibleText fallback.</summary>
        public const int ReviewVisibleMaxChars = 300;

        /// <summary>Max non-button text items for supplementary review.</summary>
        public const int ReviewNonButtonMaxItems = 30;

        /// <summary>Max chars for non-button text review.</summary>
        public const int ReviewNonButtonMaxChars = 1000;

        // ===== Audio Cues =====

        /// <summary>Whether audio cues are enabled. Toggled at runtime via F4.</summary>
        public static bool AudioCuesEnabled = true;

        /// <summary>Audio cue volume (0.0 to 1.0). 0.8 for clear audibility alongside screen reader speech.</summary>
        public const float AudioCueVolume = 0.8f;

        /// <summary>Dedup window for audio cues (ms). Same cue within this window is skipped.</summary>
        public const int AudioCueDedupMs = 30;

        // ===== SEH-Optimized Timings =====
        // Mutable fields set by ApplySEHTimings() when SRWYSafe.dll is available.
        // When SEH catches AV instead of crashing, we can be much more aggressive.

        /// <summary>Transition blackout for tactical→NONE (frames). Shorter than
        /// adventure/battle because tactical command menus are lightweight UI overlays.
        /// 10 frames ≈ 167ms at 60fps.</summary>
        public const int TacticalBlackoutFrames = 10;

        /// <summary>Effective GuardWarmupCycles. Reduced from 8 to 3 when SEH available.</summary>
        public static int GuardWarmupCyclesEffective = GuardWarmupCycles;

        /// <summary>Effective NoneProbeThreshold. Reduced from 55 to 15 when SEH available.</summary>
        public static int NoneProbeThresholdEffective = NoneProbeThreshold;

        /// <summary>Effective GuardStabilityThreshold. Reduced from 15 to 5 when SEH available.</summary>
        public static int GuardStabilityThresholdEffective = GuardStabilityThreshold;

        /// <summary>Effective GuardMaxCycles. Reduced from 900 to 150 when SEH available.</summary>
        public static int GuardMaxCyclesEffective = GuardMaxCycles;

        /// <summary>Effective SearchCooldownAfterChange. Reduced from 3 to 2 when SEH available.</summary>
        public static int SearchCooldownEffective = SearchCooldownAfterChange;

        /// <summary>Effective TacticalTransitionCooldown. Reduced from 8 to 6 when SEH available.</summary>
        public static int TacticalTransitionCooldownEffective = TacticalTransitionCooldown;

        /// <summary>Effective AdventureTransitionCooldown. Reduced from 30 to 18 when SEH available (~1.2s).</summary>
        public static int AdventureTransitionCooldownEffective = AdventureTransitionCooldown;

        /// <summary>
        /// Apply reduced timings when SEH protection is active.
        /// Called by ModCore after SafeCall.Initialize() succeeds.
        /// </summary>
        public static void ApplySEHTimings()
        {
            NoneProbeThresholdEffective = 15;      // ~1s (was 55 = 3.7s)
            GuardWarmupCyclesEffective = 3;         // ~200ms (was 8 = 530ms)
            GuardStabilityThresholdEffective = 5;   // ~333ms (was 15 = 1s)
            GuardMaxCyclesEffective = 150;          // 10s (was 900 = 60s)
            SearchCooldownEffective = 2;            // ~133ms (was 3 = 200ms)
            TacticalTransitionCooldownEffective = 6; // ~400ms (was 8 = 530ms)
            AdvSearchCooldownEffective = 12;         // ~800ms (was 23 = 1.5s)
            AdventureTransitionCooldownEffective = 18; // ~1.2s (was 30 = 2s)
        }
    }
}
