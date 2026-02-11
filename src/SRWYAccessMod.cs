using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.UIs;

[assembly: MelonInfo(typeof(SRWYAccess.SRWYAccessMod), "SRWYAccess", "0.1.0", "SRWYAccess Team")]
[assembly: MelonGame("Bandai Namco Entertainment", "SUPER ROBOT WARS Y")]

namespace SRWYAccess
{
    public class SRWYAccessMod : MelonMod
    {
        static SRWYAccessMod()
        {
            // Must start from static constructor because MelonLoader's Support Module
            // fails to load on this game (Unity 2022 IL2CPP), so OnInitializeMelon
            // and OnUpdate callbacks never fire. The background thread uses a long
            // initial delay to avoid interfering with MelonLoader's init sequence.
            DebugHelper.Write("Static constructor: scheduling mod start.");
            ModCore.EnsureStarted();
        }

        public override void OnInitializeMelon()
        {
            DebugHelper.Write("OnInitializeMelon called.");
            ModCore.EnsureStarted();
        }
    }

    internal static class DebugHelper
    {
        private static readonly string LogPath;
        private static StreamWriter _writer;
        private static readonly object _writeLock = new object();
        private static int _writeCount;

        static DebugHelper()
        {
            // Use temp directory - always writable, unlike Program Files
            LogPath = Path.Combine(Path.GetTempPath(), "SRWYAccess_debug.log");

            try
            {
                // Fresh log each session - StreamWriter with buffered writes.
                // AutoFlush=false: we flush every N writes to reduce disk IO
                // while still capturing data before crashes.
                _writer = new StreamWriter(LogPath, false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = false
                };
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SRWYAccess debug log initialized at {LogPath}");
                _writer.Flush();
            }
            catch { }

            // Leave a breadcrumb in the game directory pointing to the real log
            try
            {
                string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                string pointer = Path.Combine(gameDir, "SRWYAccess_debug.log");
                File.WriteAllText(pointer, $"Debug log location: {LogPath}\n");
            }
            catch { }
        }

        internal static void Write(string msg)
        {
            lock (_writeLock)
            {
                try
                {
                    if (_writer == null) return;
                    _writer.Write('[');
                    _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    _writer.Write("] ");
                    _writer.WriteLine(msg);
                    _writeCount++;
                    if (_writeCount % ModConfig.LogFlushInterval == 0)
                        _writer.Flush();
                }
                catch { }
            }
        }

        /// <summary>
        /// Flush buffered log entries to disk. Call on shutdown or important events.
        /// </summary>
        internal static void Flush()
        {
            lock (_writeLock)
            {
                try { _writer?.Flush(); }
                catch { }
            }
        }

        /// <summary>
        /// Close the log writer. Call on process exit.
        /// </summary>
        internal static void Close()
        {
            lock (_writeLock)
            {
                try
                {
                    _writer?.Flush();
                    _writer?.Close();
                    _writer = null;
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Core mod logic running on a background polling thread.
    /// </summary>
    internal static class ModCore
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_OEM_4 = 0xDB; // [ key
        private const int VK_OEM_6 = 0xDD; // ] key
        private const int VK_R = 0x52;     // R key (repeat result)

        private static bool _started;
        private static volatile bool _shutdownRequested;
        private static Thread _pollThread;
        private static readonly object _lock = new object();
        private static GenericMenuReader _genericMenuReader;
        private static DialogHandler _dialogHandler;
        private static TutorialHandler _tutorialHandler;
        private static AdventureDialogueHandler _adventureDialogueHandler;
        private static BattleSubtitleHandler _battleSubtitleHandler;
        private static BattleResultHandler _battleResultHandler;
        private static TacticalMapHandler _tacticalMapHandler;
        private static ScreenReviewManager _screenReviewManager;

        /// <summary>
        /// Thread-safe: ensures the polling thread is started exactly once.
        /// Called from both static constructor and OnInitializeMelon as fallback.
        /// </summary>
        internal static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }

            try
            {
                _pollThread = new Thread(RunLoop);
                _pollThread.IsBackground = true;
                _pollThread.Start();
                DebugHelper.Write("Mod polling thread started.");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Thread start error: {ex}");
            }
        }

        /// <summary>
        /// Check if Il2CppInterop runtime is initialized by checking BaseHost
        /// (a simple container in Il2CppInterop.Common) via reflection.
        /// Safe: BaseHost's static constructor does NOT depend on UnityVersionHandler.
        /// </summary>
        private static bool IsIl2CppInteropReady()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var baseHostType = asm.GetType("Il2CppInterop.Common.Host.BaseHost");
                    if (baseHostType == null) continue;

                    // Check all static fields for a non-null host instance
                    var fields = baseHostType.GetFields(
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    foreach (var f in fields)
                    {
                        try
                        {
                            var val = f.GetValue(null);
                            if (val != null) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void RunLoop()
        {
            // Wait for MelonLoader to finish its initialization (support module, scene hooks, etc.)
            // before touching any Il2Cpp or Unity APIs
            DebugHelper.Write($"RunLoop: Waiting {ModConfig.InitDelayMs}ms for MelonLoader to finish init...");
            Thread.Sleep(ModConfig.InitDelayMs);

            // Register this thread with the IL2CPP GC.
            // Without this, any IL2CPP allocation on our thread can trigger
            // a GC "Collecting from unknown thread" fatal crash.
            try
            {
                var domain = IL2CPP.il2cpp_domain_get();
                if (domain != IntPtr.Zero)
                {
                    IL2CPP.il2cpp_thread_attach(domain);
                    DebugHelper.Write("RunLoop: Thread attached to IL2CPP domain.");
                }
                else
                {
                    DebugHelper.Write("RunLoop: WARNING - il2cpp_domain_get returned null");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"RunLoop: Thread attach failed: {ex.Message}");
            }

            DebugHelper.Write("RunLoop: Waiting for Il2Cpp...");

            // Phase 1: Wait for Il2Cpp InputManager class to be available
            bool il2cppReady = false;
            for (int i = 0; i < ModConfig.MaxInitAttempts; i++)
            {
                Thread.Sleep(500);
                try
                {
                    var ptr = Il2CppClassPointerStore<InputManager>.NativeClassPtr;
                    if (ptr != IntPtr.Zero)
                    {
                        DebugHelper.Write($"Il2Cpp ready at attempt {i}.");
                        il2cppReady = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (i % 10 == 0)
                        DebugHelper.Write($"Wait attempt {i}: {ex.GetType().Name}");
                }
            }

            if (!il2cppReady)
            {
                DebugHelper.Write("Il2Cpp never became ready. Aborting.");
                return;
            }

            // Phase 2: Initialize Tolk
            try
            {
                ScreenReaderOutput.Initialize();
                DebugHelper.Write($"Tolk: IsAvailable={ScreenReaderOutput.IsAvailable}");
                ScreenReaderOutput.Say("SRWYAccess loading");
                DebugHelper.Write("Tolk test speak done.");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Tolk init error: {ex}");
                return;
            }

            // Phase 3: Wait for Il2CppInterop runtime to be ready
            // (FindObjectOfType needs this; calling too early permanently poisons type initializers)
            DebugHelper.Write("Waiting for Il2CppInterop runtime (BaseHost check)...");
            for (int i = 0; i < ModConfig.MaxInitAttempts; i++)
            {
                Thread.Sleep(500);
                try
                {
                    if (IsIl2CppInteropReady())
                    {
                        DebugHelper.Write($"Il2CppInterop runtime ready at attempt {i}.");
                        break;
                    }
                }
                catch { }
                if (i % 10 == 0)
                    DebugHelper.Write($"Attempt {i}: Il2CppInterop not ready yet.");
            }

            // Verify Il2CppInterop is ready before proceeding
            if (!IsIl2CppInteropReady())
            {
                DebugHelper.Write("WARNING: Il2CppInterop runtime never became ready. Continuing with caution...");
            }

            // Phase 4: Wait for InputManager to exist in the scene
            DebugHelper.Write("Looking for InputManager (FindObjectOfType)...");
            bool singletonReady = false;
            for (int i = 0; i < ModConfig.MaxInitAttempts; i++)
            {
                Thread.Sleep(500);
                try
                {
                    var mgr = UnityEngine.Object.FindObjectOfType<InputManager>();
                    if ((object)mgr != null)
                    {
                        DebugHelper.Write($"InputManager found at attempt {i}.");
                        singletonReady = true;
                        break;
                    }
                    else
                    {
                        if (i < 5 || i % 10 == 0)
                            DebugHelper.Write($"Attempt {i}: InputManager not in scene yet.");
                    }
                }
                catch (Exception ex)
                {
                    if (i % 10 == 0)
                        DebugHelper.Write($"Attempt {i}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!singletonReady)
            {
                DebugHelper.Write("InputManager never found. Aborting.");
                return;
            }

            // Phase 5: Initialize mod systems
            try
            {
                Loc.Initialize();
                GameStateTracker.Initialize();
                _genericMenuReader = new GenericMenuReader();
                _dialogHandler = new DialogHandler();
                _tutorialHandler = new TutorialHandler();
                _adventureDialogueHandler = new AdventureDialogueHandler();
                _battleSubtitleHandler = new BattleSubtitleHandler();
                _battleResultHandler = new BattleResultHandler();
                _tacticalMapHandler = new TacticalMapHandler();
                _screenReviewManager = new ScreenReviewManager(
                    _genericMenuReader, _dialogHandler, _tutorialHandler,
                    _adventureDialogueHandler, _battleSubtitleHandler,
                    _battleResultHandler, _tacticalMapHandler);

                ScreenReaderOutput.Say(Loc.Get("mod_loaded"));
                DebugHelper.Write("Fully initialized. Entering poll loop.");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Mod init error: {ex}");
                return;
            }

            // Register shutdown handler so the poll loop exits cleanly
            // when the game process is closing. Without this, the background
            // thread can hang in IL2CPP calls during runtime teardown.
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                _shutdownRequested = true;
                // Interrupt Thread.Sleep so the loop can check the flag immediately
                try { _pollThread?.Interrupt(); } catch { }
                // Wait up to 2s for the poll thread to detach from IL2CPP and exit.
                // If it doesn't exit in time, the process will terminate anyway
                // (IsBackground=true), but clean detach prevents IL2CPP deadlocks.
                try { _pollThread?.Join(2000); } catch { }
                // Clean up native resources
                ScreenReaderOutput.Shutdown();
                DebugHelper.Close();
            };

            // Phase 6: Main polling loop (~10 times per second)
            //
            // SAFETY: FindObjectOfType/FindObjectsOfType is not thread-safe.
            // Calling from background thread during scene transitions causes
            // native crashes (AccessViolationException, uncatchable in .NET 6).
            //
            // Protection strategy:
            //  1. GameStateTracker uses InputManager.instance (static field read)
            //     which is safe during scene transitions. It runs EVERY cycle.
            //  2. UI handlers use FindObjectOfType (dangerous). They are frozen
            //     for several seconds after state changes or handler loss.
            //  3. Only ONE FindObjectOfType/FindObjectsOfType runs per cycle (round-robin).
            //  4. GenericMenuReader uses controlBehaviour matching to find the
            //     active handler without needing type-specific searches.
            int searchSlot = 0;
            int gstPause = ModConfig.InitialGstPause;

            // Timer-based stabilization: after state changes, wait a fixed number
            // of cycles with NO IL2CPP access before resuming handler polling.
            bool stabilizing = false;
            int stabilizeWait = 0;
            int heartbeat = 0;
            int searchCooldown = 0;
            int battlePoll = 0;    // battle subtitle polling rate limiter
            int debugTraceCount = 0; // temporary: trace first N cycles after stabilize
            bool lastKeyLeft = false;  // [ key previous state (screen review browse prev)
            bool lastKeyRight = false; // ] key previous state (screen review browse next)
            bool lastKeyR = false;     // R key previous state (screen review read all)
            int resultPoll = 0;        // battle result polling rate limiter
            var lastKnownMode = GameStateTracker.CurrentMode;
            // The game's title menu runs under InputMode.NONE, not MAIN_MENU.
            // Track when we transition from TITLE to allow handlers during NONE.
            bool postTitle = false;
            // Tactical menus run under InputMode.NONE too. Track when we've
            // been in a tactical state so NONE is treated as menu, not loading.
            bool postTactical = false;
            // After save load, InputMode may stay NONE even though the game is
            // running. Track consecutive NONE loading cycles and probe after timeout.
            int noneLoadingCount = 0;
            // 3s of NONE loading → probe for active handlers
            // After battle ends, result screen shows in TACTICAL_PART (not NONE).
            bool resultPending = false;
            int resultTimeout = 0;
            // NONE probe mode: handler was found via NONE probe (not from a known
            // state transition like tactical→NONE). Be extra cautious with
            // FindObjectOfType calls since the game could transition at any time.
            bool noneProbeActive = false;

            while (!_shutdownRequested)
            {
                // Simple sleep. Exit hang is handled by ProcessExit handler
                // (sets _shutdownRequested + Thread.Interrupt to wake from sleep).
                // Previous approaches (detach/attach per cycle, sleep chunks with
                // il2cpp_domain_get) both caused AV crashes.
                try { Thread.Sleep(ModConfig.PollIntervalMs); }
                catch (ThreadInterruptedException) { break; }

                try
                {

                    // Heartbeat: log every ~30 seconds to track crash location
                    heartbeat++;
                    if (heartbeat % ModConfig.HeartbeatInterval == 0)
                    {
                        DebugHelper.Write($"Heartbeat #{heartbeat / 300}: mode={lastKnownMode}, stabilizing={stabilizing}, gstPause={gstPause}, postTitle={postTitle}, postTactical={postTactical}, resultPending={resultPending}");
                    }

                    // GST pause: skip ALL IL2CPP calls during scene transitions.
                    if (gstPause > 0)
                    {
                        gstPause--;
                        continue;
                    }

                    // Soft stabilize: after gstPause expires, skip ALL IL2CPP calls
                    // for the first N cycles of stabilization. This prevents AV crashes
                    // in GetCurrentInputBehaviour() during scene loading, where the main
                    // thread is still setting up InputManager internals.
                    if (stabilizing && stabilizeWait < ModConfig.SoftStabilizeCycles)
                    {
                        stabilizeWait++;
                        continue;
                    }

                    // Active stabilization: use lightweight CheckPointerStable() (2-3
                    // IL2CPP calls) instead of full Update() (17+ calls). If the pointer
                    // changes during monitoring, extend the wait. Full mode detection
                    // only runs ONCE after pointer confirmed stable for N cycles.
                    if (stabilizing)
                    {
                        bool pointerStable = GameStateTracker.CheckPointerStable();

                        if (!pointerStable)
                        {
                            // Pointer changed or unavailable: scene still transitioning.
                            // Reset to just after soft phase to extend monitoring.
                            if (stabilizeWait > ModConfig.SoftStabilizeCycles)
                            {
                                DebugHelper.Write($"Stabilize: pointer unstable at wait={stabilizeWait}, resetting");
                                stabilizeWait = ModConfig.SoftStabilizeCycles;
                            }
                            stabilizeWait++;

                            // Safety: don't wait forever
                            if (stabilizeWait >= ModConfig.MaxStabilizeWait)
                            {
                                stabilizing = false;
                                searchCooldown = ModConfig.PostStabilityCooldown;
                                DebugHelper.Write($"Stabilized (max wait reached: {stabilizeWait} cycles)");
                            }
                            continue;
                        }

                        stabilizeWait++;
                        if (stabilizeWait >= ModConfig.StabilityThreshold)
                        {
                            stabilizing = false;
                            searchCooldown = ModConfig.PostStabilityCooldown;
                            debugTraceCount = 0;
                            DebugHelper.Write($"Stabilized after {stabilizeWait} wait cycles ({stabilizeWait * 100}ms total)");

                            // Full mode detection now that pointer is confirmed stable.
                            // The 17 GetInputBehaviour calls are much safer here because
                            // the pointer hasn't changed for N consecutive cycles.
                            GameStateTracker.Update();
                            DebugHelper.Flush();

                            var settledMode = GameStateTracker.CurrentMode;
                            if (settledMode != lastKnownMode)
                            {
                                // Mode changed during stabilization (e.g. NONE->BATTLE_SCENE
                                // while we were stabilizing from TACTICAL->NONE).
                                DebugHelper.Write($"Post-stabilize mode change: {lastKnownMode} -> {settledMode}");
                                HandleModeChange(settledMode, ref lastKnownMode, ref postTitle,
                                    ref postTactical, ref noneProbeActive, ref resultPending,
                                    ref resultTimeout);

                                _screenReviewManager?.Clear();
                                // Short re-stabilization: skip soft phase since pointer
                                // was just confirmed stable for multiple cycles.
                                stabilizing = true;
                                stabilizeWait = ModConfig.SoftStabilizeCycles;
                                gstPause = ModConfig.GstPausePostStabilize;
                                lastKnownMode = settledMode;
                                DebugHelper.Flush();
                                continue;
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Normal (non-stabilizing) path: full mode detection.
                    GameStateTracker.Update();
                    if (_shutdownRequested) break;

                    // When behaviour pointer changed but no InputMode matched,
                    // the game is in a scene transition (loading mission, etc.).
                    // Pause ALL IL2CPP calls to avoid AV on freed native objects.
                    if (GameStateTracker.NoModeMatched)
                    {
                        gstPause = ModConfig.GstPauseNoMode;
                        noneLoadingCount = 0;
                        if (postTactical || postTitle || noneProbeActive)
                        {
                            // In known menu states, behaviour pointer changes are normal
                            // (sub-menu transitions, UI overlays). Use short cooldown
                            // instead of NoneProbeThreshold (8s) which kills cursor tracking.
                            searchCooldown = Math.Max(searchCooldown, ModConfig.PostStabilityCooldown / 2);
                            DebugHelper.Write($"NoModeMatched in menu state, short cooldown={ModConfig.PostStabilityCooldown / 2}");
                        }
                        else
                        {
                            searchCooldown = Math.Max(searchCooldown, ModConfig.NoneProbeThreshold);
                        }
                        continue;
                    }

                    // If behaviour pointer changed but mode didn't, the game is
                    // transitioning within the same mode (e.g. scene loading in NONE).
                    if (GameStateTracker.BehaviourJustChanged)
                        searchCooldown = Math.Max(searchCooldown, ModConfig.PostStabilityCooldown / 2);

                    var currentMode = GameStateTracker.CurrentMode;
                    if (currentMode != lastKnownMode)
                    {
                        HandleModeChange(currentMode, ref lastKnownMode, ref postTitle,
                            ref postTactical, ref noneProbeActive, ref resultPending,
                            ref resultTimeout);

                        // Check for resultPending skip stabilize
                        if (GameStateTracker.IsTacticalMode(currentMode) && resultPending)
                        {
                            DebugHelper.Write($"State change to {currentMode}, resultPending skip stabilize");
                            lastKnownMode = currentMode;
                            continue;
                        }

                        // Enter stabilizing mode
                        stabilizing = true;
                        stabilizeWait = 0;
                        _screenReviewManager?.Clear();

                        var fromMode = lastKnownMode;
                        lastKnownMode = currentMode;

                        // Brief GST pause to let scene transition begin
                        if (lastKnownMode == InputManager.InputMode.BATTLE_SCENE)
                            gstPause = ModConfig.GstPauseBattle;
                        else if (lastKnownMode == InputManager.InputMode.ADVENTURE)
                            gstPause = ModConfig.GstPauseAdventure;
                        else if (fromMode == InputManager.InputMode.ADVENTURE)
                            gstPause = ModConfig.GstPauseFromAdventure;
                        else if (fromMode == InputManager.InputMode.BATTLE_SCENE
                            || fromMode == InputManager.InputMode.NONE)
                            gstPause = ModConfig.GstPauseFromBattleOrNone;
                        else
                            gstPause = postTactical ? ModConfig.GstPausePostTactical : ModConfig.GstPauseGeneric;
                        DebugHelper.Flush();
                        continue;
                    }

                    // Post-stabilization cooldown: skip FindObjectOfType
                    // to let Unity finish loading objects before we search.
                    bool searchAllowed = true;
                    if (searchCooldown > 0)
                    {
                        searchCooldown--;
                        searchAllowed = false;
                    }

                    // Skip handler updates during loading/splash states.
                    // NONE is allowed when postTitle (main menu) or postTactical (tactical menus).
                    bool isNoneLoading = currentMode == InputManager.InputMode.NONE
                        && !postTitle && !postTactical;
                    bool isLoadingState = currentMode == InputManager.InputMode.LOGO
                        || currentMode == InputManager.InputMode.ENTRY
                        || currentMode == InputManager.InputMode.TITLE
                        || isNoneLoading;

                    if (isNoneLoading)
                    {
                        noneLoadingCount++;
                        // After save load, InputMode may stay NONE while the game
                        // is actually running (e.g. during ADVENTURE loading sequence).
                        // After timeout, probe to detect if we're in an active scene.
                        if (searchAllowed && noneLoadingCount >= ModConfig.NoneProbeThreshold && noneLoadingCount % ModConfig.NoneProbeThreshold == 0)
                        {
                            if (GameStateTracker.BehaviourJustChanged)
                            {
                                DebugHelper.Write("NONE probe: behaviour pointer unstable, skipping");
                                continue;
                            }
                            // Re-read GST to check for late mode changes
                            GameStateTracker.Update();
                            var probeMode = GameStateTracker.CurrentMode;
                            if (probeMode != InputManager.InputMode.NONE)
                            {
                                DebugHelper.Write($"NONE probe: detected mode change to {probeMode}");
                                // Let the next cycle handle the state change normally
                                continue;
                            }

                            // Still NONE - check if there are active UI handlers
                            // (meaning game is running despite NONE mode)
                            try
                            {
                                var handlers = UnityEngine.Object.FindObjectsOfType<UIHandlerBase>();
                                if (handlers != null && handlers.Count > 0)
                                {
                                    DebugHelper.Write($"NONE probe: found {handlers.Count} active UIHandlers, treating as active");
                                    // Force into postTactical mode so handlers can run
                                    postTactical = true;
                                    noneProbeActive = true;
                                    isLoadingState = false;
                                    noneLoadingCount = 0;
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        noneLoadingCount = 0;
                    }

                    if (isLoadingState) continue;

                    bool isBattle = currentMode == InputManager.InputMode.BATTLE_SCENE;
                    bool isAdventure = currentMode == InputManager.InputMode.ADVENTURE;

                    if (isBattle)
                    {
                        // BATTLE_SCENE: subtitle handler only.
                        // No menu searches during battle animation.
                        // During search cooldown, skip all IL2CPP access (including subtitles).
                        if (!searchAllowed) { /* cooldown active, skip */ }
                        else
                        {
                            battlePoll++;
                            if (battlePoll % 2 == 0)
                            {
                                GameStateTracker.Update();
                                if (GameStateTracker.CurrentMode != InputManager.InputMode.BATTLE_SCENE)
                                {
                                    DebugHelper.Write("Battle poll: mode changed before handler, skipping");
                                    continue;
                                }
                                // SAFETY: If behaviour pointer changed while still in BATTLE_SCENE,
                                // the game is transitioning (battle ending, phase switch).
                                // Skip FindObjectOfType during this unstable moment.
                                if (GameStateTracker.BehaviourJustChanged)
                                    continue;
                                bool readStats = (battlePoll % 30 == 0);
                                _battleSubtitleHandler?.Update(readStats);
                            }
                        }

                    }
                    else
                    {
                        // ALL non-battle states: universal menu reader + dialog
                        battlePoll = 0;
                        searchSlot = (searchSlot + 1) % ModConfig.TotalSearchSlots;

                        // GenericMenuReader: auto-detects any active UIHandlerBase
                        // Skip search during ADVENTURE - adventure uses dialogue system, not UIHandlerBase menus.
                        // FindObjectsOfType during adventure→NONE transitions causes uncatchable AV crashes.
                        bool canSearchMenu = searchAllowed && (searchSlot == 0) && !isAdventure;
                        if (debugTraceCount < 5)
                            DebugHelper.Write($"TRACE[{debugTraceCount}]: pre-menu mode={currentMode} search={canSearchMenu}");
                        bool menuLost = _genericMenuReader?.Update(canSearchMenu) ?? false;
                        if (debugTraceCount < 5)
                            DebugHelper.Write($"TRACE[{debugTraceCount}]: post-menu menuLost={menuLost}");

                        if (menuLost)
                        {
                            // If menu lost during postTitle NONE, the title scene
                            // is being unloaded (e.g. save load). Clear postTitle
                            // so NONE becomes a loading state (skip all handlers).
                            if (currentMode == InputManager.InputMode.NONE)
                            {
                                if (postTitle)
                                {
                                    postTitle = false;
                                    DebugHelper.Write("Menu handler lost in postTitle NONE: cleared postTitle (now loading)");
                                }
                                if (postTactical)
                                {
                                    postTactical = false;
                                    noneProbeActive = false;
                                    DebugHelper.Write("Menu handler lost in postTactical NONE: cleared postTactical (now loading)");
                                }
                            }
                            stabilizing = true;
    
                            stabilizeWait = 0;
    
                            _screenReviewManager?.Clear();
                            ReleaseUIHandlers();
                            DebugHelper.Write("Menu handler lost, stabilizing");
                            continue;
                        }

                        // Dialog handler runs in all non-battle states
                        if (debugTraceCount < 5)
                            DebugHelper.Write($"TRACE[{debugTraceCount}]: pre-dialog");
                        _dialogHandler?.Update();
                        if (debugTraceCount < 5)
                            DebugHelper.Write($"TRACE[{debugTraceCount}]: post-dialog");

                        if (isAdventure)
                        {
                            // Adventure: dialogue only. No FindObjectOfType searches for
                            // tutorial/menus during adventure - reduces AV crash risk
                            // during adventure→NONE scene transitions.
                            _tutorialHandler?.Update(false);
                            _adventureDialogueHandler?.Update(searchAllowed && searchSlot == 2);

                            // When ADH detects dialogue ending (text empty), the adventure
                            // scene is about to be destroyed. Preemptively pause ALL IL2CPP
                            // access to prevent AV during the ~400ms window before InputMode
                            // changes from ADVENTURE. Without this, GST.Update() calls
                            // GetCurrentInputBehaviour() on freed native objects.
                            if (_adventureDialogueHandler?.RefsJustReleased == true)
                            {
                                _adventureDialogueHandler.RefsJustReleased = false;
                                DebugHelper.Write("ADH refs released: preemptive pause for scene transition");
                                gstPause = ModConfig.GstPauseAdhRelease;
                                stabilizing = true;
        
                                stabilizeWait = 0;
        
                                _screenReviewManager?.Clear();
                                ReleaseUIHandlers();
                                continue;
                            }

                            PollBattleResult(ref resultPoll, ref resultTimeout, ref resultPending);
                        }
                        else
                        {
                            // Tactical, strategy, postTitle, and all other states.
                            // Skip tutorial FindObjectOfType during NONE probe mode -
                            // no tutorial expected on result/info screens, and
                            // FindObjectOfType is dangerous during NONE transitions.
                            bool canSearchTutorial = searchAllowed && searchSlot == 1 && !noneProbeActive;
                            _tutorialHandler?.Update(canSearchTutorial);
                            PollBattleResult(ref resultPoll, ref resultTimeout, ref resultPending);

                            // Tactical map cursor + unit cycling
                            if (currentMode == InputManager.InputMode.TACTICAL_PART)
                            {
                                if (debugTraceCount < 5)
                                    DebugHelper.Write($"TRACE[{debugTraceCount}]: pre-tactical");
                                _tacticalMapHandler?.Update(searchAllowed && searchSlot == 2);
                                if (debugTraceCount < 5)
                                    DebugHelper.Write($"TRACE[{debugTraceCount}]: post-tactical");
                            }
                            else if (currentMode == InputManager.InputMode.TACTICAL_PART_BUTTON_UI
                                || currentMode == InputManager.InputMode.TACTICAL_PART_WEAPON_LIST_SELECT_UI
                                || currentMode == InputManager.InputMode.TACTICAL_PART_ROBOT_LIST_SELECT_UI
                                || currentMode == InputManager.InputMode.TACTICAL_PART_READY_PAGE_BUTTON_UI)
                                _tacticalMapHandler?.UpdateUnitOnly(searchAllowed && searchSlot == 2);
                        }
                    }

                    if (debugTraceCount < 5)
                    {
                        DebugHelper.Write($"TRACE[{debugTraceCount}]: pre-review");
                        debugTraceCount++;
                    }

                    // Universal screen review keys (R / [ / ])
                    // Works in ALL non-loading states (battle, tactical, menus, adventure)
                    CheckReviewKeys(ref lastKeyR, ref lastKeyLeft, ref lastKeyRight,
                        currentMode, isBattle, isAdventure, postTactical);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_shutdownRequested) break;
                    DebugHelper.Write($"Poll error: {ex.GetType().Name}: {ex.Message}");
                    DebugHelper.Flush(); // Flush immediately for crash diagnostics
                    stabilizing = true;  // re-stabilize after error
                    stabilizeWait = 0;
                    _screenReviewManager?.Clear();
                    gstPause = ModConfig.GstPauseOnError;
                    ReleaseUIHandlers();
                    try { Thread.Sleep(2000); }
                    catch (ThreadInterruptedException) { break; }
                }
            }

            // Detach from IL2CPP domain before thread exits.
            // Without this, the IL2CPP runtime may deadlock during teardown
            // waiting for our thread, causing the game process to hang on exit.
            try
            {
                var thread = IL2CPP.il2cpp_thread_current();
                if (thread != IntPtr.Zero)
                {
                    IL2CPP.il2cpp_thread_detach(thread);
                    DebugHelper.Write("IL2CPP thread detached.");
                }
            }
            catch { }

            DebugHelper.Write("Poll loop exited (shutdown).");
        }

        /// <summary>
        /// Release all UI handler references. Does NOT release GameStateTracker's
        /// InputManager cache, since InputManager is a persistent singleton that
        /// survives scene transitions.
        /// </summary>
        private static void ReleaseUIHandlers(bool keepResultHandler = false)
        {
            _genericMenuReader?.ReleaseHandler();
            _dialogHandler?.ReleaseHandler();
            _tutorialHandler?.ReleaseHandler();
            _adventureDialogueHandler?.ReleaseHandler();
            _battleSubtitleHandler?.ReleaseHandler();
            _tacticalMapHandler?.ReleaseHandler();
            if (!keepResultHandler)
                _battleResultHandler?.ReleaseHandler();
        }

        /// <summary>
        /// Handle a detected mode change: update flags, release handlers.
        /// Extracted so both normal path and post-stabilize path share logic.
        /// Does NOT set stabilizing/gstPause (caller decides).
        /// </summary>
        private static void HandleModeChange(
            InputManager.InputMode newMode,
            ref InputManager.InputMode lastKnownMode,
            ref bool postTitle, ref bool postTactical,
            ref bool noneProbeActive, ref bool resultPending,
            ref int resultTimeout)
        {
            if (lastKnownMode == InputManager.InputMode.TITLE
                && newMode == InputManager.InputMode.NONE)
            {
                postTitle = true;
                ReleaseUIHandlers();
                DebugHelper.Write("TITLE -> NONE: post-title mode, stabilizing");
            }
            else if (lastKnownMode == InputManager.InputMode.BATTLE_SCENE
                && newMode == InputManager.InputMode.NONE)
            {
                resultPending = true;
                resultTimeout = 0;
                ReleaseUIHandlers(keepResultHandler: true);
                DebugHelper.Write("BATTLE_SCENE -> NONE: resultPending set, stabilizing (kept result handler)");
            }
            else
            {
                bool lastWasTactical = GameStateTracker.IsTacticalMode(lastKnownMode);
                bool isTacticalSub = GameStateTracker.IsTacticalMode(newMode);

                if (newMode != InputManager.InputMode.NONE)
                {
                    postTitle = false;
                    noneProbeActive = false;
                    if (!isTacticalSub)
                        postTactical = false;
                }

                if (lastWasTactical && newMode == InputManager.InputMode.NONE)
                {
                    postTactical = true;
                    noneProbeActive = false;
                    ReleaseUIHandlers();
                    DebugHelper.Write("Tactical -> NONE: postTactical mode, stabilizing");
                }
                else if (resultPending && isTacticalSub)
                {
                    // Handled by caller (resultPending skip stabilize)
                }
                else
                {
                    if (newMode == InputManager.InputMode.BATTLE_SCENE)
                    {
                        if (resultPending)
                        {
                            resultPending = false;
                            DebugHelper.Write("resultPending cleared (new battle)");
                        }
                    }
                    else if (newMode == InputManager.InputMode.ADVENTURE)
                    {
                        if (resultPending)
                        {
                            resultPending = false;
                            DebugHelper.Write("resultPending cleared (adventure)");
                        }
                    }

                    if (!lastWasTactical || newMode != InputManager.InputMode.NONE)
                        ReleaseUIHandlers();
                }

                DebugHelper.Write($"State change to {newMode}, stabilizing");
            }
        }

        /// <summary>
        /// Poll battle result handler with rate-limiting.
        /// Only polls when resultPending (after BATTLE_SCENE → NONE transition).
        /// Uses GameManager singleton chain internally (no FindObjectOfType).
        /// </summary>
        private static void PollBattleResult(ref int resultPoll, ref int resultTimeout, ref bool resultPending)
        {
            if (_battleResultHandler == null || !resultPending) return;

            resultTimeout++;
            if (resultTimeout > ModConfig.ResultTimeout)
            {
                resultPending = false;
                DebugHelper.Write("resultPending timeout, clearing");
                return;
            }
            resultPoll++;
            if (resultPoll % 2 == 0)
                _battleResultHandler.Update();
        }

        /// <summary>
        /// Check R/[/] keys for universal screen review.
        /// R: read all screen info, [: browse prev, ]: browse next.
        /// Works in ALL non-loading states.
        /// </summary>
        private static void CheckReviewKeys(
            ref bool lastKeyR, ref bool lastKeyLeft, ref bool lastKeyRight,
            InputManager.InputMode currentMode, bool isBattle, bool isAdventure, bool postTactical)
        {
            if (_screenReviewManager == null) return;

            bool keyR = (GetAsyncKeyState(VK_R) & 0x8000) != 0;
            bool keyLeft = (GetAsyncKeyState(VK_OEM_4) & 0x8000) != 0;
            bool keyRight = (GetAsyncKeyState(VK_OEM_6) & 0x8000) != 0;

            if (keyR && !lastKeyR)
                _screenReviewManager.ReadAll(currentMode, isBattle, isAdventure, postTactical);

            if (keyLeft && !lastKeyLeft)
                _screenReviewManager.BrowsePrev(currentMode, isBattle, isAdventure, postTactical);

            if (keyRight && !lastKeyRight)
                _screenReviewManager.BrowseNext(currentMode, isBattle, isAdventure, postTactical);

            lastKeyR = keyR;
            lastKeyLeft = keyLeft;
            lastKeyRight = keyRight;
        }
    }
}
