namespace SRWYAccess
{
    /// <summary>
    /// Centralized configuration for all mod thresholds, timings, and limits.
    /// Grouped by subsystem for easy tuning.
    ///
    /// Main thread architecture: mod logic runs on Unity main thread via
    /// Harmony postfix on InputManager.Update(). Poll cycles are throttled
    /// to every PollFrameInterval frames (~100ms at 60fps).
    /// Values in "poll cycles" = PollFrameInterval frames each.
    /// </summary>
    internal static class ModConfig
    {
        // ===== Main Thread Poll (ModCore) =====

        /// <summary>Initial delay before starting mod (ms). Lets MelonLoader finish init.</summary>
        public const int InitDelayMs = 8000;

        /// <summary>Max attempts to wait for IL2CPP / InputManager (each 500ms).</summary>
        public const int MaxInitAttempts = 120;

        /// <summary>Run handler updates every N frames. 6 frames ≈ 100ms at 60fps.</summary>
        public const int PollFrameInterval = 6;

        /// <summary>Round-robin search slots: 0=GenericMenuReader, 1=Tutorial, 2=Adventure.</summary>
        public const int TotalSearchSlots = 3;

        /// <summary>Search cooldown after mode changes (poll cycles).
        /// Lets Unity finish loading new scene objects before we search.
        /// 3 cycles = ~300ms at 60fps.</summary>
        public const int SearchCooldownAfterChange = 3;

        /// <summary>Cooldown after errors (poll cycles). 20 cycles ≈ 2s.</summary>
        public const int ErrorCooldownCycles = 20;

        /// <summary>Poll cycles of NONE-loading before probing for active UI handlers.
        /// Must be long enough to survive the slowest scene transitions (5-6s observed).
        /// 80 cycles * ~100ms = 8 seconds.</summary>
        public const int NoneProbeThreshold = 80;

        /// <summary>Heartbeat log interval in poll cycles (~30 seconds).</summary>
        public const int HeartbeatInterval = 300;

        /// <summary>Max poll cycles waiting for battle result before timeout.</summary>
        public const int ResultTimeout = 200;

        // ===== GenericMenuReader =====

        /// <summary>Miss count before declaring handler lost.</summary>
        public const int MenuMissThreshold = 3;

        /// <summary>Fault count before disabling GenericMenuReader.</summary>
        public const int MenuMaxFaults = 5;

        /// <summary>Unchanged cursor cycles before reducing IL2CPP access.</summary>
        public const int MenuStalePollLimit = 50;

        /// <summary>Probe interval when in stale mode (every Nth cycle).</summary>
        public const int MenuStaleProbeInterval = 5;

        /// <summary>Mode change re-announcement cooldown cycles.</summary>
        public const int MenuModeChangeCooldown = 10;

        // ===== BattleSubtitleHandler =====

        /// <summary>Cache re-validation age (polls since BattleSceneUI cached).</summary>
        public const int BattleCacheMaxAge = 50;

        /// <summary>Unchanged subtitle cycles before entering stale mode.</summary>
        public const int BattleStaleLimit = 2;

        /// <summary>Probe interval when in stale mode.</summary>
        public const int BattleStaleProbeInterval = 5;

        /// <summary>Max full stats reads per battle (each = 12+ IL2CPP accesses).</summary>
        public const int BattleMaxStatsReads = 3;

        /// <summary>Max retries for GetComponentsInChildren name reads.</summary>
        public const int BattleNamesMaxRetries = 3;

        /// <summary>Permanent stop threshold for stale dialog counter.</summary>
        public const int BattleStalePermanentStop = 100;

        // ===== AdventureDialogueHandler =====

        /// <summary>Unchanged text cycles before reducing IL2CPP access.</summary>
        public const int AdvStaleTextLimit = 30;

        /// <summary>Probe interval when in stale text mode.</summary>
        public const int AdvStaleProbeInterval = 5;

        /// <summary>Search cooldown cycles after dialogue refs released.</summary>
        public const int AdvSearchCooldown = 15;

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
    }
}
