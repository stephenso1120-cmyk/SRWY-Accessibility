using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.UIs;

[assembly: MelonInfo(typeof(SRWYAccess.SRWYAccessMod), "SRWYAccess", "0.2.0", "SRWYAccess Team")]
[assembly: MelonGame("Bandai Namco Entertainment", "SUPER ROBOT WARS Y")]

namespace SRWYAccess
{
    public class SRWYAccessMod : MelonMod
    {
        static SRWYAccessMod()
        {
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
            LogPath = Path.Combine(Path.GetTempPath(), "SRWYAccess_debug.log");

            try
            {
                _writer = new StreamWriter(LogPath, false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = false
                };
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SRWYAccess debug log initialized at {LogPath}");
                _writer.Flush();
            }
            catch { }

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
                    {
                        _writer.Flush();
                    }
                }
                catch { }
            }
        }

        internal static void Flush()
        {
            lock (_writeLock)
            {
                try { _writer?.Flush(); }
                catch { }
            }
        }

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
    /// Core mod logic. Initialization runs on a background thread, then a Harmony
    /// patch on InputManager.Update() runs all handler logic on the Unity main thread.
    /// </summary>
    internal static class ModCore
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_OEM_4 = 0xDB; // [ key
        private const int VK_OEM_6 = 0xDD; // ] key
        private const int VK_R = 0x52;     // R key

        private static bool _started;
        private static volatile bool _initialized;
        private static volatile bool _shutdownRequested;
        private static readonly object _lock = new object();

        // Handler instances (created during init, used on main thread)
        private static GenericMenuReader _genericMenuReader;
        private static DialogHandler _dialogHandler;
        private static TutorialHandler _tutorialHandler;
        private static AdventureDialogueHandler _adventureDialogueHandler;
        private static BattleSubtitleHandler _battleSubtitleHandler;
        private static BattleResultHandler _battleResultHandler;
        private static TacticalMapHandler _tacticalMapHandler;
        private static ScreenReviewManager _screenReviewManager;

        // Main thread state (only accessed from main thread after _initialized = true)
        private static int _frameCount;
        private static int _searchSlot;
        private static int _searchCooldown;
        private static int _noneLoadingCount;
        private static int _heartbeat;
        private static int _battlePoll;
        private static int _resultPoll;
        private static int _resultTimeout;
        private static bool _postTitle;
        private static bool _postTactical;
        private static bool _noneProbeActive;
        private static bool _resultPending;
        private static InputManager.InputMode _lastKnownMode;
        private static bool _lastKeyR;
        private static bool _lastKeyLeft;
        private static bool _lastKeyRight;

        internal static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }

            try
            {
                var initThread = new Thread(Initialize);
                initThread.IsBackground = true;
                initThread.Start();
                DebugHelper.Write("Init thread started.");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Thread start error: {ex}");
            }
        }

        private static bool IsIl2CppInteropReady()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var baseHostType = asm.GetType("Il2CppInterop.Common.Host.BaseHost");
                    if (baseHostType == null) continue;

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

        private static void Initialize()
        {
            // Phase 1: Wait for MelonLoader init
            DebugHelper.Write($"Initialize: Waiting {ModConfig.InitDelayMs}ms for MelonLoader to finish init...");
            Thread.Sleep(ModConfig.InitDelayMs);

            // Phase 2: Attach to IL2CPP domain (needed for init operations from background thread)
            try
            {
                var domain = IL2CPP.il2cpp_domain_get();
                if (domain != IntPtr.Zero)
                {
                    IL2CPP.il2cpp_thread_attach(domain);
                    DebugHelper.Write("Initialize: Thread attached to IL2CPP domain.");
                }
                else
                {
                    DebugHelper.Write("Initialize: WARNING - il2cpp_domain_get returned null");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Initialize: Thread attach failed: {ex.Message}");
            }

            // Phase 3: Wait for IL2CPP InputManager class
            DebugHelper.Write("Initialize: Waiting for Il2Cpp...");
            bool il2cppReady = false;
            for (int i = 0; i < ModConfig.MaxInitAttempts && !_shutdownRequested; i++)
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

            // Phase 4: Initialize Tolk
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

            // Phase 5: Wait for Il2CppInterop runtime
            DebugHelper.Write("Waiting for Il2CppInterop runtime (BaseHost check)...");
            for (int i = 0; i < ModConfig.MaxInitAttempts && !_shutdownRequested; i++)
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

            if (!IsIl2CppInteropReady())
            {
                DebugHelper.Write("WARNING: Il2CppInterop runtime never became ready. Continuing with caution...");
            }

            // Phase 6: Wait for InputManager to exist in the scene
            DebugHelper.Write("Looking for InputManager (FindObjectOfType)...");
            bool singletonReady = false;
            for (int i = 0; i < ModConfig.MaxInitAttempts && !_shutdownRequested; i++)
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

            // Phase 7: Initialize mod systems
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

                _lastKnownMode = GameStateTracker.CurrentMode;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Mod init error: {ex}");
                return;
            }

            // Phase 8: Apply Harmony patch to InputManager.Update()
            try
            {
                var harmony = new HarmonyLib.Harmony("com.srwyaccess.mod");
                harmony.PatchAll(typeof(SRWYAccessMod).Assembly);
                DebugHelper.Write("Harmony patch applied to InputManager.Update().");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Harmony patch FAILED: {ex}");
                return;
            }

            // Register shutdown handler
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                _initialized = false;
                _shutdownRequested = true;
                ScreenReaderOutput.Shutdown();
                DebugHelper.Close();
            };

            // Phase 9: Activate main thread updates
            _initialized = true;
            ScreenReaderOutput.Say(Loc.Get("mod_loaded"));
            DebugHelper.Write("Fully initialized. Main thread updates active.");

            // Phase 10: Detach from IL2CPP and exit init thread
            try
            {
                var thread = IL2CPP.il2cpp_thread_current();
                if (thread != IntPtr.Zero)
                {
                    IL2CPP.il2cpp_thread_detach(thread);
                    DebugHelper.Write("Init thread: IL2CPP detached. Thread exiting.");
                }
            }
            catch { }
        }

        /// <summary>
        /// Called every frame from Harmony postfix on InputManager.Update().
        /// Runs on Unity main thread - all IL2CPP access is safe.
        /// </summary>
        internal static void OnMainThreadUpdate()
        {
            if (!_initialized || _shutdownRequested) return;

            _frameCount++;

            // Check review keys every frame for instant response
            CheckReviewKeys();

            // Throttle handler updates to every PollFrameInterval frames (~100ms at 60fps)
            if (_frameCount % ModConfig.PollFrameInterval != 0) return;

            // Heartbeat
            _heartbeat++;
            if (_heartbeat % ModConfig.HeartbeatInterval == 0)
            {
                DebugHelper.Write($"Heartbeat #{_heartbeat / ModConfig.HeartbeatInterval}: mode={_lastKnownMode}, postTitle={_postTitle}, postTactical={_postTactical}, resultPending={_resultPending}");
            }

            // Update game state (safe on main thread)
            GameStateTracker.Update();

            // NoModeMatched: behaviour pointer changed but no InputMode recognized
            if (GameStateTracker.NoModeMatched)
            {
                _noneLoadingCount = 0;
                if (_postTactical || _postTitle || _noneProbeActive)
                {
                    _searchCooldown = Math.Max(_searchCooldown, 1);
                    DebugHelper.Write("NoModeMatched in menu state, short cooldown");
                }
                else
                {
                    _searchCooldown = Math.Max(_searchCooldown, ModConfig.NoneProbeThreshold);
                }
                return;
            }

            // Behaviour pointer changed within same mode
            if (GameStateTracker.BehaviourJustChanged)
                _searchCooldown = Math.Max(_searchCooldown, 1);

            var currentMode = GameStateTracker.CurrentMode;

            // Mode change
            if (currentMode != _lastKnownMode)
            {
                DebugHelper.Write($"GameState: {_lastKnownMode} -> {currentMode}");
                HandleModeChange(currentMode);

                // Special case: resultPending + tactical = skip cooldown for result detection
                if (GameStateTracker.IsTacticalMode(currentMode) && _resultPending)
                {
                    _lastKnownMode = currentMode;
                    _searchCooldown = ModConfig.SearchCooldownAfterChange;
                    // Don't return - let handlers run for result polling
                }
                else
                {
                    _lastKnownMode = currentMode;
                    _searchCooldown = ModConfig.SearchCooldownAfterChange;
                    _screenReviewManager?.Clear();
                    return; // Let scene settle
                }
            }

            // Search cooldown
            bool searchAllowed = true;
            if (_searchCooldown > 0)
            {
                _searchCooldown--;
                searchAllowed = false;
            }

            // Loading state detection
            bool isNoneLoading = currentMode == InputManager.InputMode.NONE
                && !_postTitle && !_postTactical;
            bool isLoadingState = currentMode == InputManager.InputMode.LOGO
                || currentMode == InputManager.InputMode.ENTRY
                || currentMode == InputManager.InputMode.TITLE
                || isNoneLoading;

            if (isNoneLoading)
            {
                _noneLoadingCount++;
                // NONE probe: detect if game is running despite NONE mode
                if (searchAllowed && _noneLoadingCount >= ModConfig.NoneProbeThreshold
                    && _noneLoadingCount % ModConfig.NoneProbeThreshold == 0)
                {
                    if (GameStateTracker.BehaviourJustChanged)
                    {
                        DebugHelper.Write("NONE probe: behaviour pointer unstable, skipping");
                        return;
                    }
                    // Re-read GST to check for late mode changes
                    GameStateTracker.Update();
                    var probeMode = GameStateTracker.CurrentMode;
                    if (probeMode != InputManager.InputMode.NONE)
                    {
                        DebugHelper.Write($"NONE probe: detected mode change to {probeMode}");
                        return;
                    }

                    // Still NONE - check for active UI handlers
                    try
                    {
                        var handlers = UnityEngine.Object.FindObjectsOfType<UIHandlerBase>();
                        if (handlers != null && handlers.Count > 0)
                        {
                            DebugHelper.Write($"NONE probe: found {handlers.Count} active UIHandlers, treating as active");
                            _postTactical = true;
                            _noneProbeActive = true;
                            isLoadingState = false;
                            _noneLoadingCount = 0;
                        }
                    }
                    catch { }
                }
            }
            else
            {
                _noneLoadingCount = 0;
            }

            if (isLoadingState) return;

            // ===== Handler updates =====
            bool isBattle = currentMode == InputManager.InputMode.BATTLE_SCENE;
            bool isAdventure = currentMode == InputManager.InputMode.ADVENTURE;
            _searchSlot = (_searchSlot + 1) % ModConfig.TotalSearchSlots;

            if (isBattle)
            {
                _battlePoll++;
                if (_battlePoll % 2 == 0)
                {
                    // Re-check mode (battle can end mid-frame sequence)
                    GameStateTracker.Update();
                    if (GameStateTracker.CurrentMode != InputManager.InputMode.BATTLE_SCENE)
                        return;
                    if (GameStateTracker.BehaviourJustChanged)
                        return;
                    bool readStats = (_battlePoll % 30 == 0);
                    _battleSubtitleHandler?.Update(readStats);
                }
            }
            else
            {
                _battlePoll = 0;

                // GenericMenuReader
                bool canSearchMenu = searchAllowed && (_searchSlot == 0) && !isAdventure;
                bool menuLost = _genericMenuReader?.Update(canSearchMenu) ?? false;

                if (menuLost)
                {
                    if (currentMode == InputManager.InputMode.NONE)
                    {
                        if (_postTitle)
                        {
                            _postTitle = false;
                            DebugHelper.Write("Menu lost in postTitle NONE: cleared");
                        }
                        if (_postTactical)
                        {
                            _postTactical = false;
                            _noneProbeActive = false;
                            DebugHelper.Write("Menu lost in postTactical NONE: cleared");
                        }
                    }
                    _searchCooldown = ModConfig.SearchCooldownAfterChange;
                    _screenReviewManager?.Clear();
                    ReleaseUIHandlers();
                    return;
                }

                // Dialog handler
                _dialogHandler?.Update();

                if (isAdventure)
                {
                    _tutorialHandler?.Update(false);
                    _adventureDialogueHandler?.Update(searchAllowed && _searchSlot == 2);

                    if (_adventureDialogueHandler?.RefsJustReleased == true)
                    {
                        _adventureDialogueHandler.RefsJustReleased = false;
                        DebugHelper.Write("ADH refs released: cooldown for scene transition");
                        _searchCooldown = ModConfig.SearchCooldownAfterChange;
                        _screenReviewManager?.Clear();
                        ReleaseUIHandlers();
                        return;
                    }

                    PollBattleResult();
                }
                else
                {
                    bool canSearchTutorial = searchAllowed && _searchSlot == 1 && !_noneProbeActive;
                    _tutorialHandler?.Update(canSearchTutorial);
                    PollBattleResult();

                    if (currentMode == InputManager.InputMode.TACTICAL_PART)
                    {
                        _tacticalMapHandler?.Update(searchAllowed && _searchSlot == 2);
                    }
                    else if (currentMode == InputManager.InputMode.TACTICAL_PART_BUTTON_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_WEAPON_LIST_SELECT_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_ROBOT_LIST_SELECT_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_READY_PAGE_BUTTON_UI)
                    {
                        _tacticalMapHandler?.UpdateUnitOnly(searchAllowed && _searchSlot == 2);
                    }
                }
            }
        }

        private static void HandleModeChange(InputManager.InputMode newMode)
        {
            if (_lastKnownMode == InputManager.InputMode.TITLE
                && newMode == InputManager.InputMode.NONE)
            {
                _postTitle = true;
                ReleaseUIHandlers();
                DebugHelper.Write("TITLE -> NONE: post-title mode");
            }
            else if (_lastKnownMode == InputManager.InputMode.BATTLE_SCENE
                && newMode == InputManager.InputMode.NONE)
            {
                _resultPending = true;
                _resultTimeout = 0;
                ReleaseUIHandlers(keepResultHandler: true);
                DebugHelper.Write("BATTLE_SCENE -> NONE: resultPending set");
            }
            else
            {
                bool lastWasTactical = GameStateTracker.IsTacticalMode(_lastKnownMode);
                bool isTacticalSub = GameStateTracker.IsTacticalMode(newMode);

                if (newMode != InputManager.InputMode.NONE)
                {
                    _postTitle = false;
                    _noneProbeActive = false;
                    if (!isTacticalSub)
                        _postTactical = false;
                }

                if (lastWasTactical && newMode == InputManager.InputMode.NONE)
                {
                    _postTactical = true;
                    _noneProbeActive = false;
                    ReleaseUIHandlers();
                    DebugHelper.Write("Tactical -> NONE: postTactical mode");
                }
                else if (_resultPending && isTacticalSub)
                {
                    // Handled by caller (resultPending skip)
                }
                else
                {
                    if (newMode == InputManager.InputMode.BATTLE_SCENE)
                    {
                        if (_resultPending)
                        {
                            _resultPending = false;
                            DebugHelper.Write("resultPending cleared (new battle)");
                        }
                    }
                    else if (newMode == InputManager.InputMode.ADVENTURE)
                    {
                        if (_resultPending)
                        {
                            _resultPending = false;
                            DebugHelper.Write("resultPending cleared (adventure)");
                        }
                    }

                    if (!lastWasTactical || newMode != InputManager.InputMode.NONE)
                        ReleaseUIHandlers();
                }

                DebugHelper.Write($"State change to {newMode}");
            }
        }

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

        private static void PollBattleResult()
        {
            if (_battleResultHandler == null || !_resultPending) return;

            _resultTimeout++;
            if (_resultTimeout > ModConfig.ResultTimeout)
            {
                _resultPending = false;
                DebugHelper.Write("resultPending timeout, clearing");
                return;
            }
            _resultPoll++;
            if (_resultPoll % 2 == 0)
                _battleResultHandler.Update();
        }

        private static void CheckReviewKeys()
        {
            if (_screenReviewManager == null) return;

            var currentMode = GameStateTracker.CurrentMode;
            bool isBattle = currentMode == InputManager.InputMode.BATTLE_SCENE;
            bool isAdventure = currentMode == InputManager.InputMode.ADVENTURE;

            bool keyR = (GetAsyncKeyState(VK_R) & 0x8000) != 0;
            bool keyLeft = (GetAsyncKeyState(VK_OEM_4) & 0x8000) != 0;
            bool keyRight = (GetAsyncKeyState(VK_OEM_6) & 0x8000) != 0;

            if (keyR && !_lastKeyR)
                _screenReviewManager.ReadAll(currentMode, isBattle, isAdventure, _postTactical);

            if (keyLeft && !_lastKeyLeft)
                _screenReviewManager.BrowsePrev(currentMode, isBattle, isAdventure, _postTactical);

            if (keyRight && !_lastKeyRight)
                _screenReviewManager.BrowseNext(currentMode, isBattle, isAdventure, _postTactical);

            _lastKeyR = keyR;
            _lastKeyLeft = keyLeft;
            _lastKeyRight = keyRight;
        }
    }

    /// <summary>
    /// Harmony patch: injects mod update into InputManager.Update() on the Unity main thread.
    /// InputManager is a persistent SingletonMonoBehaviour that runs every frame.
    /// </summary>
    [HarmonyPatch(typeof(InputManager), "Update")]
    internal static class InputManagerUpdatePatch
    {
        static void Postfix()
        {
            try
            {
                ModCore.OnMainThreadUpdate();
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Main thread update error: {ex.GetType().Name}: {ex.Message}");
                DebugHelper.Flush();
            }
        }
    }
}
