using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.Map;
using Il2CppCom.BBStudio.SRTeam.Data;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppCom.BBStudio.SRTeam.UI.StrategyPart.Option;
using MonoMod.RuntimeDetour;

[assembly: MelonInfo(typeof(SRWYAccess.SRWYAccessMod), "SRWYAccess", "2.5.7", "SRWYAccess Team")]
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
    /// Core mod logic. Initialization runs on a background thread, then a native
    /// hook on InputManager.Update() runs all handler logic on the Unity main thread.
    /// </summary>
    internal static class ModCore
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static uint _currentProcessId;

        private const int VK_OEM_4 = 0xDB; // [ key
        private const int VK_OEM_6 = 0xDD; // ] key
        private const int VK_R = 0x52;     // R key
        private const int VK_OEM_1 = 0xBA; // ; key (prev enemy)
        private const int VK_OEM_7 = 0xDE; // ' key (next enemy)
        private const int VK_OEM_PERIOD = 0xBE; // . key (prev ally)
        private const int VK_OEM_2 = 0xBF; // / key (next ally)
        private const int VK_OEM_5 = 0xDC; // \ key (repeat last distance)
        private const int VK_P = 0x50;    // P key (path prediction to last target)
        private const int VK_MENU = 0x12;    // Alt modifier
        private const int VK_CONTROL = 0x11; // Ctrl modifier

        private const int VK_Q = 0x51;    // Q key (L1 - tab switch left)
        private const int VK_E = 0x45;    // E key (R1 - tab switch right)
        private const int VK_1 = 0x31;    // 1 key (L1 - tab switch left alt)
        private const int VK_3 = 0x33;    // 3 key (R1 - tab switch right alt)
        private const int VK_OEM_PLUS = 0xBB;  // = key (movement range)
        private const int VK_OEM_MINUS = 0xBD; // - key (attack range)
        private const int VK_F2 = 0x71;   // F2 key (toggle mod)
        private const int VK_F3 = 0x72;   // F3 key (reload mod state)
        private const int VK_F4 = 0x73;   // F4 key (toggle audio cues)

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
        private static MapWeaponTargetHandler _mapWeaponTargetHandler;
        private static TacticalMapHandler _tacticalMapHandler;
        private static UnitDistanceHandler _unitDistanceHandler;
        private static ScreenReviewManager _screenReviewManager;
        private static SupporterHandler _supporterHandler;
        private static SystemOptionHandler _systemOptionHandler;
        private static MissionDetailHandler _missionDetailHandler;

        // Main thread state (only accessed from main thread after _initialized = true)
        private static int _frameCount;
        private static int _searchSlot;
        private static int _searchCooldown;
        private static int _noneLoadingCount;
        private static int _heartbeat;
        private static int _battlePoll;
        private static int _battleSceneDiag; // diagnostic: log first N frames after BATTLE_SCENE entry
        // _adventureDiag removed: diagnostic logging disabled to reduce I/O overhead
        private static int _resultPoll;
        private static int _resultTimeout;
        private static bool _postTitle;
        private static bool _postTactical;
        private static bool _postAdventure; // suppress NONE probe after ADVENTURE→NONE (crash #28)
        private static bool _noneProbeActive;
        private static int _transitionBlackout; // skip ALL mod logic during dangerous transitions
        private static bool _pendingTurnSummary; // announce unit count after enemy turn ends

        // FindObjectsOfType throttling
        private static int _lastFOTFrame = 0;
        private const int FOT_MIN_INTERVAL = 15; // ~250ms at 60fps

        // Safe mode: graceful degradation when too many critical errors occur
        private static int _criticalErrors = 0;
        private static bool _safeMode = false;
        private const int SAFE_MODE_THRESHOLD = 3;
        private static bool _resultPending;
        private static InputManager.InputMode _lastKnownMode;
        private static bool _lastKeyR;
        private static bool _lastKeyLeft;
        private static bool _lastKeyRight;
        private static bool _lastKeySemicolon;
        private static bool _lastKeyApostrophe;
        private static bool _lastKeyPeriod;
        private static bool _lastKeySlash;
        private static bool _lastKeyBackslash;
        private static bool _lastKeyBackslashSortie; // \ key for sortie prep (separate from tactical map usage)
        private static bool _lastKeyP;
        private static bool _lastKeyQ;
        private static bool _lastKeyE;
        private static bool _lastKey1;
        private static bool _lastKey3;

        private static bool _lastKeyEquals;
        private static bool _lastKeyMinus;
        private static bool _lastKeyF2;
        private static bool _lastKeyF3;
        private static bool _lastKeyF4;
        private static bool _modEnabled = true;
        private static bool _isInLoadingState; // guards CheckReviewKeys during transitions
        private static int _lastBattleAnimSetting = -99; // tracks EBattleAnimation changes

        // Crash breadcrumb: tracks execution stage in OnMainThreadUpdate.
        // Logged with each heartbeat. On process-level crash, the last heartbeat
        // reveals which stage the hook was in when it last completed successfully.
        // Stages: 0=entry, 1=keyChecks, 2=GST, 3=modeChange, 4=handlers, 99=done
        private static int _breadcrumb;

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

        // ===== Native Hook for InputManager.Update() =====
        // IL2CPP instance methods use: void fn(IntPtr thisPtr, IntPtr methodInfo)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_InputManagerUpdate(IntPtr thisPtr, IntPtr methodInfo);

        private static d_InputManagerUpdate _origUpdate;
        private static d_InputManagerUpdate _hookDelegate;
        private static GCHandle _hookGcHandle; // prevent GC collection of delegate
        private static int _hookCallCount;
        private static NativeDetour _nativeDetour; // inline hook (patches actual machine code)

        private static unsafe bool TryNativeHook()
        {
            IntPtr classPtr = Il2CppClassPointerStore<InputManager>.NativeClassPtr;
            DebugHelper.Write($"NativeHook: classPtr=0x{classPtr:X}");

            if (classPtr == IntPtr.Zero)
            {
                DebugHelper.Write("NativeHook: InputManager class pointer is zero");
                return false;
            }

            // Get the Il2CppMethodInfo* for InputManager.Update()
            IntPtr methodInfoPtr = IL2CPP.GetIl2CppMethod(
                classPtr, false, "Update", "System.Void");

            if (methodInfoPtr == IntPtr.Zero)
            {
                DebugHelper.Write("NativeHook: GetIl2CppMethod returned zero");
                return false;
            }

            DebugHelper.Write($"NativeHook: methodInfo=0x{methodInfoPtr:X}");

            // Il2CppMethodInfo.methodPointer is at offset 0
            IntPtr originalFnPtr = *(IntPtr*)methodInfoPtr;
            DebugHelper.Write($"NativeHook: original fn=0x{originalFnPtr:X}");

            if (originalFnPtr == IntPtr.Zero)
            {
                DebugHelper.Write("NativeHook: method pointer is zero");
                return false;
            }

            // Create our hook delegate and pin it to prevent GC
            _hookDelegate = new d_InputManagerUpdate(NativeUpdateHook);
            _hookGcHandle = GCHandle.Alloc(_hookDelegate);
            IntPtr hookFnPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

            DebugHelper.Write($"NativeHook: hook fn=0x{hookFnPtr:X}");

            // Use NativeDetour for inline hooking - patches actual machine code bytes
            // at the start of the native function with a JMP to our hook.
            // This is immune to vtable resets or Il2CppMethodInfo pointer overwrites.
            _nativeDetour = new NativeDetour(originalFnPtr, hookFnPtr);
            _origUpdate = _nativeDetour.GenerateTrampoline<d_InputManagerUpdate>();

            DebugHelper.Write("NativeHook: NativeDetour applied (inline hook)");
            DebugHelper.Flush();
            return true;
        }

        private static void NativeUpdateHook(IntPtr thisPtr, IntPtr methodInfo)
        {
            // Call original InputManager.Update()
            // Guard against race condition: NativeDetour patches native code before
            // GenerateTrampoline completes. If the game thread fires Update() in
            // between, _origUpdate is still null.
            var orig = _origUpdate;
            if (orig == null) return;
            _breadcrumb = 5; // about to call original InputManager.Update()
            orig(thisPtr, methodInfo);
            _breadcrumb = 6; // original InputManager.Update() returned OK

            // Diagnostic: log first few hook calls + periodic alive confirmation
            _hookCallCount++;
            if (_hookCallCount <= 3)
            {
                DebugHelper.Write($"NativeHook fired #{_hookCallCount}");
                DebugHelper.Flush();
            }
            else if (_hookCallCount % 18000 == 0) // ~5 minutes at 60fps
            {
                DebugHelper.Write($"NativeHook alive: {_hookCallCount} calls");
                DebugHelper.Flush();
            }

            // Run mod logic on main thread
            try
            {
                OnMainThreadUpdate();
            }
            catch (Exception ex)
            {
                _criticalErrors++;
                DebugHelper.Write($"MainThread CRITICAL ERROR #{_criticalErrors}: {ex.GetType().Name}: {ex.Message}");
                DebugHelper.Write($"Stack: {ex.StackTrace}");
                DebugHelper.Flush();

                if (_criticalErrors >= SAFE_MODE_THRESHOLD && !_safeMode)
                {
                    _safeMode = true;
                    DebugHelper.Write("*** ENTERING SAFE MODE: Too many critical errors ***");
                    DebugHelper.Flush();
                    try
                    {
                        ScreenReaderOutput.Say(Loc.Get("safe_mode_enabled"));
                    }
                    catch { }
                }
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

            // Phase 5.5: Initialize SEH-protected native call wrappers
            try
            {
                SafeCall.Initialize();
                DebugHelper.Write($"SafeCall initialized: available={SafeCall.IsAvailable}, fields={SafeCall.FieldsAvailable}");
                if (SafeCall.IsAvailable)
                {
                    ModConfig.ApplySEHTimings();
                    DebugHelper.Write("Applied SEH-optimized timings");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall init error: {ex.Message} (continuing without SEH protection)");
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
                AudioCueManager.Initialize();
                GameStateTracker.Initialize();
                _genericMenuReader = new GenericMenuReader();
                _dialogHandler = new DialogHandler();
                _tutorialHandler = new TutorialHandler();
                _adventureDialogueHandler = new AdventureDialogueHandler();
                _battleSubtitleHandler = new BattleSubtitleHandler();
                _battleResultHandler = new BattleResultHandler();
                _mapWeaponTargetHandler = new MapWeaponTargetHandler();
                _tacticalMapHandler = new TacticalMapHandler();
                _unitDistanceHandler = new UnitDistanceHandler();
                _supporterHandler = new SupporterHandler();
                _systemOptionHandler = new SystemOptionHandler();
                _missionDetailHandler = new MissionDetailHandler();
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

            // Phase 8: Hook InputManager.Update() at native level
            try
            {
                if (!TryNativeHook())
                {
                    DebugHelper.Write("Native hook failed. Aborting.");
                    DebugHelper.Flush();
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Native hook FAILED: {ex}");
                DebugHelper.Flush();
                return;
            }

            // Phase 8.5: Register global exception handler
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    string msg = ex != null
                        ? $"Unhandled: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                        : $"Unhandled non-Exception object: {e.ExceptionObject}";
                    DebugHelper.Write("FATAL: " + msg);
                    DebugHelper.Flush();
                    // Attempt to notify user (may fail if Tolk is already shut down)
                    try
                    {
                        ScreenReaderOutput.Say(Loc.Get("mod_critical_error"));
                    }
                    catch { }
                }
                catch { }
            };
            DebugHelper.Write("Global exception handler registered.");

            // Register shutdown handler
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                _initialized = false;
                _shutdownRequested = true;

                // Release GCHandle to prevent resource leak
                if (_hookGcHandle.IsAllocated)
                {
                    try
                    {
                        _hookGcHandle.Free();
                        DebugHelper.Write("ProcessExit: GCHandle released.");
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.Write($"ProcessExit: Failed to free GCHandle: {ex.Message}");
                    }
                }

                AudioCueManager.Shutdown();
                SafeCall.Shutdown();
                ScreenReaderOutput.Shutdown();
                DebugHelper.Close();
            };

            // Cache current process ID for focus detection
            _currentProcessId = (uint)Environment.ProcessId;

            // Phase 9: Activate main thread updates
            _initialized = true;
            ScreenReaderOutput.Say(Loc.Get("mod_loaded"));
            DebugHelper.Write($"Fully initialized. Lang={Loc.CurrentLang}. Main thread updates active.");
            DebugHelper.Flush();

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
        /// Called every frame from NativeUpdateHook (native hook on InputManager.Update).
        /// Runs on Unity main thread - all IL2CPP access is safe.
        /// </summary>
        internal static void OnMainThreadUpdate()
        {
            if (!_initialized || _shutdownRequested) return;

            // Create Unity AudioSource on first main thread frame (Phase 2 of audio init).
            // Phase 1 (sample generation) ran on background thread during mod startup.
            AudioCueManager.EnsureMainThreadInit();

            // Skip all mod logic when game window is not focused.
            // GetAsyncKeyState is global and would capture keys from other apps.
            if (!IsGameFocused()) return;

            // Mod hotkeys: F2 toggle, F3 reload - checked every frame, even when disabled
            CheckModHotkeys();

            if (!_modEnabled) return;

            // Transition blackout: skip ALL mod logic (including GST.Update(), key checks,
            // handler updates) to minimize interference during dangerous scene transitions.
            // Only F2/F3 hotkeys still work (checked above).
            if (_transitionBlackout > 0)
            {
                _transitionBlackout--;
                return;
            }

            // Don't reset _breadcrumb here: heartbeat logs the PREVIOUS poll's final stage.
            // On crash, the last heartbeat breadcrumb reveals what completed before the crash.
            _frameCount++;

            // Re-check language until SaveLoadManager data is available
            Loc.TryConfirmLanguage();

            // Battle animation toggle is handled in BattleCheckMenuHandler (demoText).

            // Safe mode: only handle basic functionality
            if (_safeMode)
            {
                // In safe mode, only allow screen reader output and key checks
                // Disable all handler updates to prevent further crashes
                CheckReviewKeys();  // Basic screen review still works
                return;
            }

            // ===== Battle warmup: two-phase protection =====
            // Phase 1 (polls 0-5): Complete silence. No SafeCall, no handlers.
            // Phase 2 (polls 6-20): Subtitle-only. BattleSubtitleHandler runs
            //   but GST.Update() and key checks are skipped. This avoids VEH
            //   overhead during the game engine's heavy initialization window
            //   (observed: game drops to ~5fps at polls 6-10, VEH calls interfere).
            // Phase 3 (poll 21+): Full operation. GST, keys, all handlers.
            if (_lastKnownMode == InputManager.InputMode.BATTLE_SCENE && _battlePoll <= ModConfig.BattleWarmupPolls)
            {
                if (_frameCount % ModConfig.PollFrameInterval != 0) return;
                _battlePoll++;
                return;
            }

            if (_lastKnownMode == InputManager.InputMode.BATTLE_SCENE && _battlePoll <= 20)
            {
                if (_frameCount % ModConfig.PollFrameInterval != 0) return;
                if (_battlePoll == ModConfig.BattleWarmupPolls + 1)
                {
                    DebugHelper.Write($"Battle phase 2: subtitle-only (poll={_battlePoll})");
                    DebugHelper.Flush();
                }
                _battlePoll++;
                if (_battlePoll % 2 == 0)
                    _battleSubtitleHandler?.Update(false, _battlePoll);
                return;
            }

            // Throttle all mod logic to poll frames. Interactive modes use faster
            // interval (~33ms) for snappier response. Standard interval during
            // BATTLE_SCENE, guard mode, and NONE-loading (no interactive UI needs
            // fast response during scene transitions - reduces SafeCall frequency).
            // Key checks also run on poll frames only (33ms response is imperceptible
            // for keyboard input, but saves 11 GetAsyncKeyState kernel transitions
            // per non-poll frame).
            bool isNoneTransition = _lastKnownMode == InputManager.InputMode.NONE
                && !_postTitle && !_postTactical && !_postAdventure && !_noneProbeActive;
            bool useFastPoll = !GameStateTracker.IsInGuardMode
                && !isNoneTransition
                && _lastKnownMode != InputManager.InputMode.BATTLE_SCENE;
            int pollInterval = useFastPoll
                ? ModConfig.FastPollFrameInterval
                : ModConfig.PollFrameInterval;

            // Defensive check: ensure pollInterval is positive to prevent division by zero
            if (pollInterval <= 0)
            {
                DebugHelper.Write($"WARNING: Invalid pollInterval={pollInterval}, defaulting to 2");
                pollInterval = 2;
            }

            if (_frameCount % pollInterval != 0) return;

            // Key checks on poll frames (skip during guard mode - no interactive UI)
            if (!GameStateTracker.IsInGuardMode)
            {
                CheckReviewKeys();
                CheckDistanceKeys();
                CheckSortieInfoKey();

                CheckRangeKeys();
                CheckTabSwitchKeys();
            }

            // CRITICAL: Timing-critical counters (_searchCooldown, _noneLoadingCount,
            // _heartbeat) were calibrated for PollFrameInterval (4 frames). With fast
            // polling (2 frames), they'd expire in half the intended time, causing
            // premature access to transitioning scene objects. Only decrement/increment
            // these counters on standard ticks to maintain correct timing.
            bool isStandardTick = (_frameCount % ModConfig.PollFrameInterval == 0);

            // Heartbeat - _breadcrumb still holds value from LAST poll frame
            // (99 if completed, or lower if crash happened mid-cycle)
            if (isStandardTick)
            {
                _heartbeat++;
                if (_heartbeat % ModConfig.HeartbeatInterval == 0)
                {
                    var gcr = SafeCall.GameCrashRecoveries;
                    DebugHelper.Write($"Heartbeat #{_heartbeat / ModConfig.HeartbeatInterval}: mode={_lastKnownMode}, postTitle={_postTitle}, postTactical={_postTactical}, postAdv={_postAdventure}, resultPending={_resultPending}, bc={_breadcrumb}{(gcr > 0 ? $", gameCrashRecoveries={gcr}" : "")}");
                    DebugHelper.Flush();
                }
            }

            // Update game state (safe on main thread)
            _breadcrumb = 10; // GST update
            GameStateTracker.Update();

            // NoModeMatched: behaviour pointer changed but no InputMode recognized
            if (GameStateTracker.NoModeMatched)
            {
                _isInLoadingState = true;
                _noneLoadingCount = 0;
                if (_postTactical || _postTitle || _noneProbeActive)
                {
                    _searchCooldown = Math.Max(_searchCooldown, 1);
                    DebugHelper.Write("NoModeMatched in menu state, short cooldown");
                }
                else if (_searchCooldown == 0)
                {
                    // Only set full cooldown if not already counting down.
                    // Repeated NoModeMatched events during guard mode no longer
                    // restart the countdown, preventing compounded delays.
                    _searchCooldown = ModConfig.NoneProbeThresholdEffective;
                }
                DebugHelper.Flush();
                return;
            }

            // Guard mode active: scene is transitioning, IL2CPP objects may be
            // partially destroyed. Skip ALL handler updates (FindObjectsOfType,
            // cursor reads, TMP text reads) to prevent uncatchable AV.
            // Guard warmup resets NoModeMatched to false (above check misses it),
            // so this explicit check catches the remaining guard cycles.
            // Allow _searchCooldown to decrement during guard: guard already provides
            // equivalent protection, so running them concurrently saves ~333ms on
            // adventure transitions (cooldown starts counting during guard instead of after).
            if (GameStateTracker.IsInGuardMode)
            {
                if (isStandardTick && _searchCooldown > 0)
                    _searchCooldown--;
                return;
            }

            // Behaviour pointer changed within same mode
            if (GameStateTracker.BehaviourJustChanged)
                _searchCooldown = Math.Max(_searchCooldown, 1);

            var currentMode = GameStateTracker.CurrentMode;

            // Post-guard: reset _noneLoadingCount so NONE probe doesn't fire
            // immediately after guard exits. Adds ~1s of safety (threshold cycles).
            if (GameStateTracker.GuardJustExited)
                _noneLoadingCount = 0;

            // Post-guard immediate probe: guard mode confirmed pointer stability
            // for ~1s. If we're in NONE without flags, immediately check for
            // active UI handlers instead of waiting for NoneProbeThreshold.
            if (GameStateTracker.GuardJustExited && currentMode == InputManager.InputMode.NONE
                && !_postTitle && !_postTactical && !_postAdventure)
            {
                // Throttle FOT calls: minimum 30 frames (~500ms) between calls
                if (_frameCount - _lastFOTFrame >= FOT_MIN_INTERVAL)
                {
                    try
                    {
                        var handlers = UnityEngine.Object.FindObjectsOfType<UIHandlerBase>();
                        _lastFOTFrame = _frameCount;
                        if (handlers != null && handlers.Count > 0)
                        {
                            DebugHelper.Write($"Post-guard probe: found {handlers.Count} handlers, activating");
                            _postTactical = true;
                            _noneProbeActive = true;
                            _noneLoadingCount = 0;
                            _searchCooldown = ModConfig.SearchCooldownEffective;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.Write($"Post-guard probe error: {ex.GetType().Name}");
                    }
                }
            }

            // Mode change
            if (currentMode != _lastKnownMode)
            {
                var previousMode = _lastKnownMode;
                DebugHelper.Write($"GameState: {_lastKnownMode} -> {currentMode}");
                DebugHelper.Flush();
                HandleModeChange(currentMode);

                // Special case: resultPending + tactical = skip cooldown for result detection
                if (GameStateTracker.IsTacticalMode(currentMode) && _resultPending)
                {
                    _lastKnownMode = currentMode;
                    _searchCooldown = ModConfig.SearchCooldownEffective;
                    // Don't return - let handlers run for result polling
                }
                else
                {
                    _lastKnownMode = currentMode;
                    // Scene transitions involving ADVENTURE involve a full scene
                    // destroy + load. Use extended cooldown to let Unity finish before
                    // handlers run FindObjectsOfType (partially-destroyed objects can
                    // cause uncatchable AV on property access in .NET 6).
                    // ADVENTURE -> NONE is equally dangerous: adventure scene objects
                    // are destroyed and the NONE probe's FindObjectsOfType can hit
                    // partially-freed UIHandlerBase objects.
                    if (currentMode == InputManager.InputMode.ADVENTURE
                        || previousMode == InputManager.InputMode.ADVENTURE)
                        _searchCooldown = ModConfig.AdventureTransitionCooldownEffective;
                    else if (GameStateTracker.IsTacticalMode(previousMode)
                        && currentMode == InputManager.InputMode.NONE)
                        _searchCooldown = ModConfig.TacticalTransitionCooldownEffective;
                    else
                        _searchCooldown = ModConfig.SearchCooldownEffective;
                    _isInLoadingState = true; // block review keys during mode change settle
                    _screenReviewManager?.Clear();
                    DebugHelper.Flush();
                    return; // Let scene settle
                }
            }

            _breadcrumb = 20; // mode change handled

            // Search cooldown (decrement at standard rate to preserve calibrated timing)
            bool searchAllowed = true;
            if (_searchCooldown > 0)
            {
                if (isStandardTick) _searchCooldown--;
                searchAllowed = false;
            }

            // Loading state detection
            bool isNoneLoading = currentMode == InputManager.InputMode.NONE
                && !_postTitle && !_postTactical && !_postAdventure;
            bool isLoadingState = currentMode == InputManager.InputMode.LOGO
                || currentMode == InputManager.InputMode.ENTRY
                || currentMode == InputManager.InputMode.TITLE
                || isNoneLoading;

            if (isNoneLoading)
            {
                if (isStandardTick) _noneLoadingCount++;
                // NONE probe: detect if game is running despite NONE mode.
                // CRITICAL: Suppress during guard mode. Guard mode means GST is monitoring
                // a scene transition (e.g. ADVENTURE→NONE). FindObjectsOfType during this
                // window can hit partially-destroyed scene objects → uncatchable AV.
                if (searchAllowed && !GameStateTracker.IsInGuardMode
                    && _noneLoadingCount >= ModConfig.NoneProbeThresholdEffective
                    && _noneLoadingCount % ModConfig.NoneProbeThresholdEffective == 0)
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

                    // Still NONE - check for active UI handlers (with throttling)
                    if (_frameCount - _lastFOTFrame >= FOT_MIN_INTERVAL)
                    {
                        try
                        {
                            var handlers = UnityEngine.Object.FindObjectsOfType<UIHandlerBase>();
                            _lastFOTFrame = _frameCount;
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
            }
            else
            {
                _noneLoadingCount = 0;
            }

            _isInLoadingState = isLoadingState;
            if (isLoadingState) return;

            _breadcrumb = 30; // entering handler updates
            // ===== Handler updates =====
            bool isBattle = currentMode == InputManager.InputMode.BATTLE_SCENE;
            bool isAdventure = currentMode == InputManager.InputMode.ADVENTURE;
            _searchSlot = (_searchSlot + 1) % ModConfig.TotalSearchSlots;

            if (isBattle)
            {
                _breadcrumb = 50; // battle path entry
                _battlePoll++;
                if (_battleSceneDiag > 0)
                {
                    DebugHelper.Write($"BATTLE_DIAG: handler update, battlePoll={_battlePoll}");
                    DebugHelper.Flush();
                }
                // Poll subtitles every 2nd cycle (~133ms)
                if (_battlePoll % 2 == 0)
                {
                    _breadcrumb = 51; // subtitle handler about to run
                    bool readStats = (_battlePoll % 32 == 0); // stats every ~2s (aligned with %2), reduced for stability
                    _battleSubtitleHandler?.Update(readStats, _battlePoll);
                    _breadcrumb = 52; // subtitle handler done
                }
            }
            else
            {
                _battlePoll = 0;

                // Q/E unit switching while tactical command menu is open (NONE+postTactical):
                // Detect unit change BEFORE GenericMenuReader so the unit name can be
                // prepended to whatever menu item GenericMenuReader announces.
                if (currentMode == InputManager.InputMode.NONE && _postTactical)
                {
                    _tacticalMapHandler?.UpdateUnitOnlySilent();
                    string switchedUnit = _tacticalMapHandler?.ConsumePendingUnitSwitch();
                    if (!string.IsNullOrEmpty(switchedUnit))
                        _genericMenuReader?.SetUnitSwitchPrefix(switchedUnit);
                }

                // GenericMenuReader (safe on main thread, including during ADVENTURE)
                _breadcrumb = 41; // GenericMenuReader
                bool canSearchMenu = searchAllowed && (_searchSlot == 0);
                bool menuLost = _genericMenuReader?.Update(canSearchMenu) ?? false;

                // Sort/filter text monitoring (reads TMP from list handlers)
                _genericMenuReader?.CheckSortFilterText();

                // Supporter handler (MonoBehaviour-based, not found by GenericMenuReader)
                _supporterHandler?.Update(canSearchMenu);

                // System option handler (column-based cursor + value tracking)
                _systemOptionHandler?.Update(canSearchMenu);

                // Mission detail handler: DISABLED - functionality integrated into GenericMenuReader
                // _missionDetailHandler?.Update(canSearchMenu);

                _breadcrumb = 42; // GenericMenuReader done
                if (menuLost)
                {
                    // Keep _postTitle and _postTactical flags on handler loss.
                    // They suppress the NONE probe's FindObjectsOfType which can crash
                    // on partially-destroyed objects during transitions (e.g. battle check
                    // → battle scene). Flags are cleared when a non-NONE mode is detected.
                    if (currentMode == InputManager.InputMode.NONE)
                    {
                        if (_postTitle)
                            DebugHelper.Write("Menu lost in postTitle NONE (flag kept)");
                        if (_postTactical)
                            DebugHelper.Write("Menu lost in postTactical NONE (flag kept)");
                    }
                    _searchCooldown = ModConfig.SearchCooldownEffective;
                    _screenReviewManager?.Clear();
                    ReleaseUIHandlers();
                    return;
                }

                // Dialog handler
                _breadcrumb = 43; // DialogHandler
                _dialogHandler?.Update();

                if (isAdventure)
                {
                    // Tutorial search now safe on main thread (was disabled for bg thread)
                    _tutorialHandler?.Update(searchAllowed && _searchSlot == 1);
                    // Adventure dialogue: search every poll cycle (not just slot 2) for
                    // faster subtitle discovery. FindDialogueObjects batches all 5 types
                    // in one call, so the per-frame cost is acceptable.
                    _adventureDialogueHandler?.Update(searchAllowed);

                    if (_adventureDialogueHandler?.RefsJustReleased == true)
                    {
                        _adventureDialogueHandler.RefsJustReleased = false;
                        DebugHelper.Write("ADH refs released: cooldown for scene transition");
                        _searchCooldown = ModConfig.AdventureTransitionCooldownEffective;
                        _screenReviewManager?.Clear();
                        // Release other handlers but NOT ADH - it already released its own
                        // refs via ReleaseDialogueRefs() with _searchCooldown=23.
                        // Calling ReleaseUIHandlers() would call ADH.ReleaseHandler()
                        // which resets _searchCooldown to 0, allowing immediate re-search
                        // of partially-destroyed scene objects → AV crash.
                        _genericMenuReader?.ReleaseHandler();
                        _dialogHandler?.ReleaseHandler();
                        _tutorialHandler?.ReleaseHandler();
                        _battleSubtitleHandler?.ReleaseHandler();
                        _tacticalMapHandler?.ReleaseHandler();
                        _unitDistanceHandler?.ReleaseHandler();
                        _supporterHandler?.ReleaseHandler();
                        _missionDetailHandler?.ReleaseHandler();
                        return;
                    }

                    if (isStandardTick) PollBattleResult();
                }
                else
                {
                    _breadcrumb = 44; // Tutorial+Result
                    bool canSearchTutorial = searchAllowed && _searchSlot == 1 && !_noneProbeActive;
                    _tutorialHandler?.Update(canSearchTutorial);
                    if (isStandardTick) PollBattleResult();

                    // Turn summary: count actionable units after enemy turn ends
                    if (_pendingTurnSummary && searchAllowed)
                    {
                        _pendingTurnSummary = false;
                        AnnounceTurnSummary();
                    }

                    if (currentMode == InputManager.InputMode.TACTICAL_PART)
                    {
                        _breadcrumb = 45; // TacticalMap
                        _tacticalMapHandler?.Update(searchAllowed && _searchSlot == 2);
                    }
                    else if (currentMode == InputManager.InputMode.TACTICAL_PART_BUTTON_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_WEAPON_LIST_SELECT_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_ROBOT_LIST_SELECT_UI
                        || currentMode == InputManager.InputMode.TACTICAL_PART_READY_PAGE_BUTTON_UI)
                    {
                        _tacticalMapHandler?.UpdateUnitOnly(searchAllowed && _searchSlot == 2);
                    }
                    // MAP weapon target count (runs during all tactical modes;
                    // handler itself checks for active PlayerAttackMapWeaponTask)
                    _mapWeaponTargetHandler?.Update();
                }
            }

            _breadcrumb = 99; // completed successfully
        }

        private static void HandleModeChange(InputManager.InputMode newMode)
        {
            DebugHelper.Write($"HandleModeChange: {_lastKnownMode} -> {newMode}");

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
                    _postAdventure = false;
                    _noneProbeActive = false;
                    if (!isTacticalSub)
                        _postTactical = false;
                }

                if (lastWasTactical && newMode == InputManager.InputMode.NONE)
                {
                    // Only announce unit name on first entry (not after Move/Attack re-entry).
                    // _postTactical stays true throughout the command session, so checking
                    // it here distinguishes first open (false) from re-entry (true).
                    if (!_postTactical)
                    {
                        string unitInfo = _tacticalMapHandler?.LastUnitInfo;
                        if (!string.IsNullOrEmpty(unitInfo))
                            _genericMenuReader?.SetPendingUnitAnnouncement(unitInfo);
                    }
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
                        _battleSceneDiag = 0; // DISABLED: diagnostic logging causes I/O overhead (Crash #19)
                        // DebugHelper.Write("BATTLE_DIAG: armed (600 frames)");
                        // DebugHelper.Flush();
                        if (_resultPending)
                        {
                            _resultPending = false;
                            DebugHelper.Write("resultPending cleared (new battle)");
                        }
                        // Preemptive guard for tactical→battle transition
                        if (lastWasTactical)
                        {
                            GameStateTracker.EnterGuardMode();
                            DebugHelper.Write("Guard mode: TACTICAL→BATTLE transition");
                        }
                    }
                    else if (newMode == InputManager.InputMode.ADVENTURE)
                    {
                        // ADV_DIAG disabled: diagnostic logging removed to reduce I/O overhead
                        if (_resultPending)
                        {
                            _resultPending = false;
                            DebugHelper.Write("resultPending cleared (adventure)");
                        }
                    }

                    if (!lastWasTactical || newMode != InputManager.InputMode.NONE)
                        ReleaseUIHandlers();
                }

                // Preemptive guard mode for major scene transitions.
                // The game destroys scene objects during these transitions
                // (adventure objects, map objects during enemy battle initiation),
                // and subsequent GetInputBehaviour calls or FindObjectsOfType
                // can hit freed native memory → uncatchable AccessViolationException.
                if ((_lastKnownMode == InputManager.InputMode.ADVENTURE
                    || lastWasTactical)
                    && newMode == InputManager.InputMode.NONE)
                {
                    GameStateTracker.EnterGuardMode();
                    // Blackout: skip ALL mod logic to avoid interfering during
                    // scene destruction. Tactical→NONE uses shorter blackout
                    // (~167ms) since command menus are lightweight UI overlays.
                    // Adventure→NONE uses longer blackout (~1s) for heavy
                    // scene transitions (full scene destroy + load).
                    _transitionBlackout = lastWasTactical
                        ? ModConfig.TacticalBlackoutFrames  // ~100ms
                        : ModConfig.AdventureBlackoutFramesEffective; // ~400ms with SEH, ~1s without
                    // Crash #28: ADVENTURE→NONE must suppress the NONE probe.
                    // The game's scene objects are still being cleaned up for
                    // several seconds after the mode change. FindObjectsOfType
                    // during this window hits partially-freed objects → AV at
                    // GameAssembly.dll+0x338959. Unlike tactical→NONE (which
                    // needs the probe for command menus), adventure→NONE always
                    // transitions to another mode (tactical/adventure) detected
                    // naturally by GST.
                    if (_lastKnownMode == InputManager.InputMode.ADVENTURE)
                    {
                        _postAdventure = true;
                        DebugHelper.Write("Adventure -> NONE: postAdventure mode (NONE probe suppressed)");
                    }
                }
                // Guard mode for ANY major → ADVENTURE transition.
                // The game destroys previous scene objects while loading adventure
                // objects. Without guard, GST runs the 17-mode detection loop and
                // handlers call FindObjectOfType on partially-freed objects.
                // Crash #28b: TACTICAL→ADVENTURE was unprotected, causing AV at
                // GameAssembly.dll+0x338959 ~2.2s after transition.
                if (newMode == InputManager.InputMode.ADVENTURE
                    && (_lastKnownMode == InputManager.InputMode.NONE
                        || _lastKnownMode == InputManager.InputMode.BATTLE_SCENE
                        || lastWasTactical))
                {
                    GameStateTracker.EnterGuardMode();
                }

                // Turn summary: enemy turn → player turn
                if (_lastKnownMode == InputManager.InputMode.TACTICAL_PART_NON_PLAYER_TURN
                    && newMode == InputManager.InputMode.TACTICAL_PART)
                {
                    _pendingTurnSummary = true;
                    DebugHelper.Write("Turn summary pending (enemy → player)");
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
            _unitDistanceHandler?.ReleaseHandler();
            _supporterHandler?.ReleaseHandler();
            _mapWeaponTargetHandler?.Reset();
            _systemOptionHandler?.ReleaseHandler();
            _missionDetailHandler?.ReleaseHandler();
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

        /// <summary>
        /// Count actionable player units and announce turn summary.
        /// Uses TacticalBoard.GetAllPawns() with SafeCall protection.
        /// Called after searchCooldown expires to ensure stable scene.
        /// </summary>
        private static void AnnounceTurnSummary()
        {
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) goto fallback;
                if (!SafeCall.ProbeObject(mm.Pointer)) goto fallback;

                TacticalBoard board;
                try { board = mm.TacticalBoard; }
                catch { goto fallback; }
                if ((object)board == null) goto fallback;
                if (!SafeCall.ProbeObject(board.Pointer)) goto fallback;

                Il2CppSystem.Collections.Generic.List<PawnUnit> allPawns;
                try { allPawns = board.GetAllPawns(); }
                catch { goto fallback; }
                if ((object)allPawns == null) goto fallback;

                int count;
                try { count = allPawns.Count; }
                catch { goto fallback; }

                int totalPlayer = 0;
                int actionable = 0;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var pu = allPawns[i];
                        if ((object)pu == null || pu.Pointer == IntPtr.Zero) continue;
                        if (!SafeCall.ProbeObject(pu.Pointer)) continue;

                        // Check if player side
                        bool playerSide;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsPlayerSideSafe(pu.Pointer);
                            if (!ok) continue;
                            playerSide = val;
                        }
                        else
                        {
                            try { playerSide = pu.IsPlayerSide; }
                            catch { continue; }
                        }
                        if (!playerSide) continue;

                        // Check if alive
                        bool alive;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsAliveSafe(pu.Pointer);
                            if (!ok) continue;
                            alive = val;
                        }
                        else
                        {
                            try { alive = pu.IsAlive; }
                            catch { continue; }
                        }
                        if (!alive) continue;

                        totalPlayer++;

                        // Check if can still act
                        try
                        {
                            Pawn pawnData = null;
                            if (SafeCall.PawnMethodsAvailable)
                            {
                                IntPtr pdPtr = SafeCall.ReadPawnDataSafe(pu.Pointer);
                                if (pdPtr != IntPtr.Zero && SafeCall.ProbeObject(pdPtr))
                                    pawnData = new Pawn(pdPtr);
                            }
                            if ((object)pawnData != null)
                            {
                                if (pawnData.IsPossibleToAction())
                                    actionable++;
                            }
                        }
                        catch { }
                    }
                    catch { continue; }
                }

                string msg = Loc.Get("turn_summary", actionable, totalPlayer);
                ScreenReaderOutput.Say(msg);
                DebugHelper.Write($"Turn summary: {actionable}/{totalPlayer} actionable");
                return;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Turn summary error: {ex.GetType().Name}");
            }

            fallback:
            ScreenReaderOutput.Say(Loc.Get("turn_summary_none"));
        }

        private static bool IsGameFocused()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == _currentProcessId;
        }

        private static void CheckModHotkeys()
        {
            bool keyF2 = (GetAsyncKeyState(VK_F2) & 0x8000) != 0;
            bool keyF3 = (GetAsyncKeyState(VK_F3) & 0x8000) != 0;

            if (keyF2 && !_lastKeyF2)
            {
                _modEnabled = !_modEnabled;
                string msg = _modEnabled ? Loc.Get("mod_enabled") : Loc.Get("mod_disabled");
                ScreenReaderOutput.Say(msg);
                DebugHelper.Write($"Mod toggled: enabled={_modEnabled}");
                if (!_modEnabled)
                {
                    // Release all handler state when disabling
                    ReleaseUIHandlers();
                    _screenReviewManager?.Clear();
                }
            }

            if (keyF3 && !_lastKeyF3 && _modEnabled)
            {
                ResetModState();
                ScreenReaderOutput.Say(Loc.Get("mod_reset"));
                DebugHelper.Write("Mod state reset via F3");
            }

            bool keyF4 = (GetAsyncKeyState(VK_F4) & 0x8000) != 0;
            if (keyF4 && !_lastKeyF4)
            {
                ModConfig.AudioCuesEnabled = !ModConfig.AudioCuesEnabled;
                string msg = ModConfig.AudioCuesEnabled ? Loc.Get("audio_cues_on") : Loc.Get("audio_cues_off");
                ScreenReaderOutput.Say(msg);
                DebugHelper.Write($"Audio cues toggled: enabled={ModConfig.AudioCuesEnabled}");
            }

            _lastKeyF2 = keyF2;
            _lastKeyF3 = keyF3;
            _lastKeyF4 = keyF4;
        }

        /// <summary>
        /// Read sortie preparation info (unit/ship count) from SortiePreparationManager.
        /// Called when \ key is pressed in sortie preparation screen (NONE/STRATEGY_PART mode).
        /// </summary>
        private static void ReadSortieInfo()
        {
            DebugHelper.Write("ReadSortieInfo: Starting...");
            try
            {
                // Find SortiePreparationManager object
                var sortieManager = UnityEngine.Object.FindObjectOfType<SortiePreparationManager>();
                if ((object)sortieManager == null)
                {
                    DebugHelper.Write("ReadSortieInfo: SortiePreparationManager not found");
                    ScreenReaderOutput.Say(Loc.Get("sortie_info_not_available"));
                    return;
                }

                DebugHelper.Write($"ReadSortieInfo: Found SortiePreparationManager at {sortieManager.Pointer:X}");

                if (!SafeCall.ProbeObject(sortieManager.Pointer))
                {
                    DebugHelper.Write("ReadSortieInfo: ProbeObject failed");
                    return;
                }

                var parts = new System.Collections.Generic.List<string>();

                // Read unit count using GetSelectedSortieNum and GetRemainSortieNum
                try
                {
                    int selectedUnits = sortieManager.GetSelectedSortieNum(SortiePreparationManager.SelectSoriteType.Unit);
                    int remainUnits = sortieManager.GetRemainSortieNum(SortiePreparationManager.SelectSoriteType.Unit);
                    int totalUnits = selectedUnits + remainUnits;

                    string unitCount = $"{selectedUnits}/{totalUnits}";
                    parts.Add(Loc.Get("sortie_unit_count", unitCount));
                    DebugHelper.Write($"ReadSortieInfo: Unit count: {unitCount}");
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"ReadSortieInfo: Error reading unit count: {ex.Message}");
                }

                // Read ship count
                try
                {
                    int selectedShips = sortieManager.GetSelectedSortieNum(SortiePreparationManager.SelectSoriteType.Ship);
                    int remainShips = sortieManager.GetRemainSortieNum(SortiePreparationManager.SelectSoriteType.Ship);
                    int totalShips = selectedShips + remainShips;

                    string shipCount = $"{selectedShips}/{totalShips}";
                    parts.Add(Loc.Get("sortie_ship_count", shipCount));
                    DebugHelper.Write($"ReadSortieInfo: Ship count: {shipCount}");
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"ReadSortieInfo: Error reading ship count: {ex.Message}");
                }

                if (parts.Count > 0)
                {
                    string announcement = string.Join(". ", parts);
                    ScreenReaderOutput.Say(announcement);
                    DebugHelper.Write($"ReadSortieInfo: Announced: {announcement}");
                }
                else
                {
                    DebugHelper.Write("ReadSortieInfo: No info to announce");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"ReadSortieInfo: FAULT: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool _battleAnimCheckFailed;
        private static int _battleAnimPollCount;
        private static void CheckBattleAnimationSetting()
        {
            if (_battleAnimCheckFailed) return;
            try
            {
                int current = -1;
                // Method 1: OptionDataEx.BattleAnimation() static method
                try
                {
                    current = (int)OptionDataEx.BattleAnimation();
                }
                catch
                {
                    // Method 2: OptionDataEx.PlayerSettings().battleAnimation
                    try
                    {
                        var ps = OptionDataEx.PlayerSettings();
                        if ((object)ps != null && ps.Pointer != IntPtr.Zero)
                            current = ps.battleAnimation;
                    }
                    catch (Exception ex2)
                    {
                        DebugHelper.Write($"BattleAnim: all methods failed: {ex2.GetType().Name}: {ex2.Message}");
                        _battleAnimCheckFailed = true;
                        return;
                    }
                }
                _battleAnimPollCount++;
                // Diagnostic: log value every ~15s
                if (_battleAnimPollCount % 30 == 0)
                    DebugHelper.Write($"BattleAnim: poll #{_battleAnimPollCount} current={current} last={_lastBattleAnimSetting}");
                if (current < 0) return;
                if (current == _lastBattleAnimSetting) return;
                int prev = _lastBattleAnimSetting;
                _lastBattleAnimSetting = current;
                DebugHelper.Write($"BattleAnim: changed {prev} -> {current}");
                if (prev == -99) return; // skip first read
                string key = current switch
                {
                    0 => "battle_anim_on",
                    1 => "battle_anim_face",
                    2 => "battle_anim_off",
                    _ => null
                };
                if (key != null)
                    ScreenReaderOutput.Say(Loc.Get(key));
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleAnim: error: {ex.GetType().Name}: {ex.Message}");
                _battleAnimCheckFailed = true;
            }
        }

        private static void ResetModState()
        {
            ReleaseUIHandlers();
            _screenReviewManager?.Clear();
            _searchCooldown = 0;
            _noneLoadingCount = 0;
            _postTitle = false;
            _postTactical = false;
            _postAdventure = false;
            _noneProbeActive = false;
            _resultPending = false;
            _resultPoll = 0;
            _resultTimeout = 0;
            _battlePoll = 0;
            _pendingTurnSummary = false;
            _isInLoadingState = false;
            _safeMode = false;
            _transitionBlackout = 0;
            // Re-detect current game state
            GameStateTracker.Update();
            _lastKnownMode = GameStateTracker.CurrentMode;
        }

        private static void CheckReviewKeys()
        {
            if (_screenReviewManager == null) return;
            // Block review during loading/transitions to prevent FOT on freed objects
            if (_isInLoadingState || _searchCooldown > 0) return;

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

        /// <summary>
        /// Check Q/E keys (L1/R1 tab switch). When pressed, force GenericMenuReader
        /// to re-announce the current item since tab content changes without cursor move.
        /// </summary>
        private static void CheckTabSwitchKeys()
        {
            if (_genericMenuReader == null || !_genericMenuReader.HasHandler) return;
            if (_isInLoadingState || _searchCooldown > 0) return;

            bool keyQ = (GetAsyncKeyState(VK_Q) & 0x8000) != 0;
            bool keyE = (GetAsyncKeyState(VK_E) & 0x8000) != 0;
            bool key1 = (GetAsyncKeyState(VK_1) & 0x8000) != 0;
            bool key3 = (GetAsyncKeyState(VK_3) & 0x8000) != 0;

            if ((keyQ && !_lastKeyQ) || (keyE && !_lastKeyE)
                || (key1 && !_lastKey1) || (key3 && !_lastKey3))
                _genericMenuReader.ForceReannounce();

            _lastKeyQ = keyQ;
            _lastKeyE = keyE;
            _lastKey1 = key1;
            _lastKey3 = key3;
        }


        /// <summary>
        /// Check =/- keys (movement range / attack range).
        /// Active during any tactical mode.
        /// </summary>
        private static void CheckRangeKeys()
        {
            if (_tacticalMapHandler == null) return;
            if (_isInLoadingState || _searchCooldown > 0) return;

            var currentMode = GameStateTracker.CurrentMode;
            if (!GameStateTracker.IsTacticalMode(currentMode)
                && !(currentMode == InputManager.InputMode.NONE && (_postTactical || _noneProbeActive)))
            {
                _lastKeyEquals = false;
                _lastKeyMinus = false;
                return;
            }

            bool keyEquals = (GetAsyncKeyState(VK_OEM_PLUS) & 0x8000) != 0;
            if (keyEquals && !_lastKeyEquals)
                _tacticalMapHandler.AnnounceMovementRange();
            _lastKeyEquals = keyEquals;

            bool keyMinus = (GetAsyncKeyState(VK_OEM_MINUS) & 0x8000) != 0;
            if (keyMinus && !_lastKeyMinus)
                _tacticalMapHandler.AnnounceAttackRange();
            _lastKeyMinus = keyMinus;
        }

        /// <summary>
        /// Check ;/' (enemy distance) and .// (ally distance) keys.
        /// Only active during TACTICAL_PART mode (map navigation/movement).
        /// </summary>
        /// <summary>
        /// Check \ key for sortie preparation info (unit count).
        /// Active in NONE and STRATEGY_PART modes (sortie prep screen).
        /// </summary>
        private static void CheckSortieInfoKey()
        {
            if (_isInLoadingState || _searchCooldown > 0) return;

            var currentMode = GameStateTracker.CurrentMode;

            // Check if we're in sortie preparation mode (NONE or STRATEGY_PART)
            bool inSortieMode = currentMode == InputManager.InputMode.NONE
                || currentMode == InputManager.InputMode.STRATEGY_PART;

            if (!inSortieMode)
            {
                _lastKeyBackslashSortie = false;
                return;
            }

            bool keyBackslash = (GetAsyncKeyState(VK_OEM_5) & 0x8000) != 0;

            if (keyBackslash && !_lastKeyBackslashSortie)
            {
                _lastKeyBackslashSortie = true;
                DebugHelper.Write($"CheckSortieInfoKey: \\ key pressed in mode {currentMode}");
                ReadSortieInfo();
            }
            else if (!keyBackslash)
            {
                _lastKeyBackslashSortie = false;
            }
        }

        private static void CheckDistanceKeys()
        {
            if (_unitDistanceHandler == null) return;
            if (_isInLoadingState || _searchCooldown > 0) return;

            var currentMode = GameStateTracker.CurrentMode;
            if (currentMode != InputManager.InputMode.TACTICAL_PART)
            {
                _lastKeySemicolon = false;
                _lastKeyApostrophe = false;
                _lastKeyPeriod = false;
                _lastKeySlash = false;
                _lastKeyBackslash = false;
                _lastKeyP = false;
                return;
            }

            // Get current cursor position from TacticalMapHandler
            var cursorCoord = _tacticalMapHandler?.LastCoord ?? new Vector2Int(-999, -999);
            if (cursorCoord.x == -999 && cursorCoord.y == -999)
            {
                _lastKeySemicolon = false;
                _lastKeyApostrophe = false;
                _lastKeyPeriod = false;
                _lastKeySlash = false;
                _lastKeyBackslash = false;
                _lastKeyP = false;
                return;
            }

            bool keySemicolon = (GetAsyncKeyState(VK_OEM_1) & 0x8000) != 0;
            bool keyApostrophe = (GetAsyncKeyState(VK_OEM_7) & 0x8000) != 0;
            bool keyPeriod = (GetAsyncKeyState(VK_OEM_PERIOD) & 0x8000) != 0;
            bool keySlash = (GetAsyncKeyState(VK_OEM_2) & 0x8000) != 0;
            bool keyBackslash = (GetAsyncKeyState(VK_OEM_5) & 0x8000) != 0;
            bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            // ; key: Alt=named enemies, Ctrl=enemies by lowest HP, plain=all enemies
            if (keySemicolon && !_lastKeySemicolon)
            {
                if (_mapWeaponTargetHandler != null && _mapWeaponTargetHandler.IsActive)
                {
                    // ; in MAP weapon mode: read enemy names in range
                    string names = _mapWeaponTargetHandler.ReadEnemyNames();
                    if (!string.IsNullOrEmpty(names))
                        ScreenReaderOutput.Say(names);
                }
                else
                {
                    string msg;
                    if (altHeld)
                        msg = _unitDistanceHandler.CycleNamedEnemy(-1, cursorCoord);
                    else if (ctrlHeld)
                        msg = _unitDistanceHandler.CycleEnemyByHp(-1, cursorCoord);
                    else
                        msg = _unitDistanceHandler.CycleEnemy(-1, cursorCoord);
                    if (!string.IsNullOrEmpty(msg))
                        ScreenReaderOutput.Say(msg);
                }
            }

            // ' key: Alt=named enemies, Ctrl=enemies by lowest HP, plain=all enemies
            if (keyApostrophe && !_lastKeyApostrophe)
            {
                if (_mapWeaponTargetHandler != null && _mapWeaponTargetHandler.IsActive)
                {
                    // ' in MAP weapon mode: read ally names in range
                    string names = _mapWeaponTargetHandler.ReadAllyNames();
                    if (!string.IsNullOrEmpty(names))
                        ScreenReaderOutput.Say(names);
                }
                else
                {
                    string msg;
                    if (altHeld)
                        msg = _unitDistanceHandler.CycleNamedEnemy(1, cursorCoord);
                    else if (ctrlHeld)
                        msg = _unitDistanceHandler.CycleEnemyByHp(1, cursorCoord);
                    else
                        msg = _unitDistanceHandler.CycleEnemy(1, cursorCoord);
                    if (!string.IsNullOrEmpty(msg))
                        ScreenReaderOutput.Say(msg);
                }
            }

            // . key: Alt=unacted, Ctrl=acted, plain=ally distance
            if (keyPeriod && !_lastKeyPeriod)
            {
                string msg;
                if (altHeld)
                    msg = _unitDistanceHandler.CycleUnacted(-1, cursorCoord);
                else if (ctrlHeld)
                    msg = _unitDistanceHandler.CycleActed(-1, cursorCoord);
                else
                    msg = _unitDistanceHandler.CycleAlly(-1, cursorCoord);
                if (!string.IsNullOrEmpty(msg))
                    ScreenReaderOutput.Say(msg);
            }

            // / key: Alt=unacted, Ctrl=acted, plain=ally distance
            if (keySlash && !_lastKeySlash)
            {
                string msg;
                if (altHeld)
                    msg = _unitDistanceHandler.CycleUnacted(1, cursorCoord);
                else if (ctrlHeld)
                    msg = _unitDistanceHandler.CycleActed(1, cursorCoord);
                else
                    msg = _unitDistanceHandler.CycleAlly(1, cursorCoord);
                if (!string.IsNullOrEmpty(msg))
                    ScreenReaderOutput.Say(msg);
            }

            if (keyBackslash && !_lastKeyBackslash)
            {
                if (ctrlHeld)
                {
                    // Ctrl+\: announce enemy closest to mission destination point
                    DebugHelper.Write($"Ctrl+\\ pressed at cursor ({cursorCoord.x},{cursorCoord.y})");
                    string msg = _unitDistanceHandler.GetEnemyNearestToMissionPoint(cursorCoord);
                    DebugHelper.Write($"Ctrl+\\ result: {msg}");
                    if (!string.IsNullOrEmpty(msg))
                        ScreenReaderOutput.Say(msg);
                }
                else if (altHeld)
                {
                    // Alt+\: announce mission destination point direction/distance
                    DebugHelper.Write($"Alt+\\ pressed at cursor ({cursorCoord.x},{cursorCoord.y})");
                    string msg = _unitDistanceHandler.GetMissionPointInfo(cursorCoord);
                    DebugHelper.Write($"Alt+\\ result: {msg}");
                    if (!string.IsNullOrEmpty(msg))
                        ScreenReaderOutput.Say(msg);
                }
                else
                {
                    string msg = _unitDistanceHandler.RepeatLast(cursorCoord);
                    if (!string.IsNullOrEmpty(msg))
                        ScreenReaderOutput.Say(msg);
                }
            }

            // P key: path prediction to last queried target
            bool keyP = (GetAsyncKeyState(VK_P) & 0x8000) != 0;
            if (keyP && !_lastKeyP)
            {
                string path = _unitDistanceHandler.GetPathToLastTarget(cursorCoord);
                if (!string.IsNullOrEmpty(path))
                    ScreenReaderOutput.Say(path);
                else
                    ScreenReaderOutput.Say(Loc.Get("path_no_unit"));
            }
            _lastKeyP = keyP;

            _lastKeySemicolon = keySemicolon;
            _lastKeyApostrophe = keyApostrophe;
            _lastKeyPeriod = keyPeriod;
            _lastKeySlash = keySlash;
            _lastKeyBackslash = keyBackslash;
        }
    }

}
