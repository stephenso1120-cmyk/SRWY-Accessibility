namespace SRWYAccess
{
    /// <summary>
    /// Centralized configuration for all mod thresholds, timings, and limits.
    /// Grouped by subsystem for easy tuning. All values are in poll cycles
    /// (1 cycle = ~100ms) unless otherwise noted.
    /// </summary>
    internal static class ModConfig
    {
        // ===== Poll Loop (SRWYAccessMod / ModCore) =====

        /// <summary>Main poll interval in milliseconds.</summary>
        public const int PollIntervalMs = 100;

        /// <summary>Number of sleep chunks per poll cycle. Each chunk is followed by
        /// an IL2CPP safe-point (P/Invoke to il2cpp_domain_get). SleepChunks * SleepChunkMs
        /// should equal PollIntervalMs.</summary>
        public const int SleepChunks = 10;

        /// <summary>Duration of each sleep chunk in milliseconds.</summary>
        public const int SleepChunkMs = 10;

        /// <summary>Initial delay before starting mod (ms). Lets MelonLoader finish init.</summary>
        public const int InitDelayMs = 8000;

        /// <summary>Max attempts to wait for IL2CPP / InputManager (each 500ms).</summary>
        public const int MaxInitAttempts = 120;

        /// <summary>Round-robin search slots: 0=GenericMenuReader, 1=Tutorial, 2=Adventure.</summary>
        public const int TotalSearchSlots = 3;

        /// <summary>Initial GST pause cycles on startup (let InputManager stabilize).</summary>
        public const int InitialGstPause = 15;

        /// <summary>Minimum stabilizeWait value to exit stabilization.
        /// With SoftStabilizeCycles=3, gives 5 actual pointer-monitoring cycles (8-3=5).</summary>
        public const int StabilityThreshold = 8;

        /// <summary>Maximum cycles to wait for stability before proceeding anyway.</summary>
        public const int MaxStabilizeWait = 100;

        /// <summary>Soft stabilize: first N cycles of stabilization skip ALL IL2CPP calls
        /// (including GST.Update and GetCurrentInputBehaviour). Prevents AV crashes
        /// during scene loading when InputManager internals are being modified by
        /// the main thread. After this phase, lightweight pointer monitoring begins.</summary>
        public const int SoftStabilizeCycles = 3;

        /// <summary>Post-stabilization cooldown cycles before allowing FindObjectOfType.
        /// Short: pointer was actively confirmed stable during monitoring.</summary>
        public const int PostStabilityCooldown = 5;

        /// <summary>Cycles of NONE-loading before probing for active UI handlers.
        /// Must be long enough to survive the slowest scene transitions (5-6s observed).
        /// FindObjectsOfType in the probe crashes during scene loading.</summary>
        public const int NoneProbeThreshold = 80;

        /// <summary>Heartbeat log interval in cycles (~30 seconds).</summary>
        public const int HeartbeatInterval = 300;

        /// <summary>Max cycles waiting for battle result before timeout.</summary>
        public const int ResultTimeout = 200;

        // ===== GST Pause Values (cycles) =====

        /// <summary>GST pause when NoModeMatched (scene transition).</summary>
        public const int GstPauseNoMode = 2;

        /// <summary>GST pause when entering BATTLE_SCENE.</summary>
        public const int GstPauseBattle = 3;

        /// <summary>GST pause when entering ADVENTURE.</summary>
        public const int GstPauseAdventure = 7;

        /// <summary>GST pause when leaving ADVENTURE.</summary>
        public const int GstPauseFromAdventure = 5;

        /// <summary>GST pause when leaving BATTLE_SCENE or NONE.
        /// Pointer monitoring provides active safety during stabilization.</summary>
        public const int GstPauseFromBattleOrNone = 7;

        /// <summary>GST pause for postTactical transitions.
        /// Increased from 1: this transition is crash-prone (TACTICAL->NONE->BATTLE).</summary>
        public const int GstPausePostTactical = 2;

        /// <summary>GST pause for generic transitions.</summary>
        public const int GstPauseGeneric = 2;

        /// <summary>GST pause on poll error.</summary>
        public const int GstPauseOnError = 5;

        /// <summary>GST pause when ADH refs released (preemptive).</summary>
        public const int GstPauseAdhRelease = 5;

        /// <summary>GST pause after post-stabilize mode change.
        /// Short: pointer was just confirmed stable for 5 consecutive checks.</summary>
        public const int GstPausePostStabilize = 2;

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

        /// <summary>Unchanged subtitle cycles before entering stale mode.
        /// Lower = safer (less time using cached ref on potentially-destroyed object).
        /// At 300ms poll interval: 2 = 600ms window before switching to safe probes.</summary>
        public const int BattleStaleLimit = 2;

        /// <summary>Probe interval when in stale mode.</summary>
        public const int BattleStaleProbeInterval = 5;

        /// <summary>Max full stats reads per battle (each = 12+ IL2CPP accesses).</summary>
        public const int BattleMaxStatsReads = 3;

        /// <summary>Max retries for GetComponentsInChildren name reads.</summary>
        public const int BattleNamesMaxRetries = 3;

        /// <summary>Permanent stop threshold for stale dialog counter.
        /// At 300ms poll interval: 100 = ~30s of stale time. Limits total
        /// cached ref exposure during battle ending phase.</summary>
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
