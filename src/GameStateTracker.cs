using System;
using MelonLoader;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.Inputs;

namespace SRWYAccess
{
    /// <summary>
    /// Tracks game state via InputManager input modes.
    /// Announces state transitions to screen reader.
    ///
    /// SAFETY: Uses InputManager.instance (static field read) instead of
    /// FindObjectOfType or cached instance references. Static field reads
    /// access TYPE metadata (never freed), not instance data (can be freed).
    /// During scene transitions, instance returns null → Update skips safely.
    /// After transition, instance returns the new manager → Update resumes.
    ///
    /// GUARD MODE: After NoModeMatched (behaviour pointer changed but no
    /// InputMode recognized), enters guard mode. In guard mode, subsequent
    /// pointer changes do NOT trigger the full 17-mode GetInputBehaviour
    /// detection loop. Instead, they just signal NoModeMatched and wait.
    /// Full detection only resumes after the pointer stabilizes for
    /// GuardStabilityThreshold consecutive cycles (~3 seconds), indicating
    /// the scene transition is complete and native objects are stable.
    /// This prevents uncatchable AccessViolationException from calling
    /// GetInputBehaviour on freed native objects during transitions.
    ///
    /// All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator.
    /// </summary>
    public static class GameStateTracker
    {
        private static InputManager.InputMode _currentMode = InputManager.InputMode.NONE;
        private static bool _initialized;
        private static IntPtr _lastBehaviourPtr = IntPtr.Zero;

        // Guard mode: protects against AV during scene transitions
        private static bool _guardMode;
        private static int _guardStableCount;
        private static int _guardTotalCount;
        private static int _consecutiveGuardFailures; // consecutive guard-exit-no-match cycles

        // All known modes - hardcoded because Enum.GetValues doesn't work on IL2CPP proxy enums
        private static readonly InputManager.InputMode[] _allModes = new InputManager.InputMode[]
        {
            InputManager.InputMode.NONE,
            InputManager.InputMode.ENTRY,
            InputManager.InputMode.LOGO,
            InputManager.InputMode.TITLE,
            InputManager.InputMode.MAIN_MENU,
            InputManager.InputMode.ADVENTURE,
            InputManager.InputMode.TACTICAL_PART,
            InputManager.InputMode.TACTICAL_PART_BUTTON_UI,
            InputManager.InputMode.TACTICAL_PART_READY_PAGE_BUTTON_UI,
            InputManager.InputMode.TACTICAL_PART_ROBOT_LIST_SELECT_UI,
            InputManager.InputMode.TACTICAL_PART_WEAPON_LIST_SELECT_UI,
            InputManager.InputMode.TACTICAL_PART_NON_PLAYER_TURN,
            InputManager.InputMode.TACTICAL_PART_AUTO_BATTLE,
            InputManager.InputMode.BATTLE_SCENE,
            InputManager.InputMode.STRATEGY_PART,
            InputManager.InputMode.OPENING_DEMO,
            InputManager.InputMode.GAME_CLEAR,
        };

        /// <summary>
        /// Current game input mode.
        /// </summary>
        public static InputManager.InputMode CurrentMode => _currentMode;

        /// <summary>
        /// True if the behaviour pointer changed during the last Update() call.
        /// Used by the main loop to detect scene transitions within the same mode
        /// (e.g. NONE→NONE with different behaviour) and delay FindObjectOfType.
        /// </summary>
        public static bool BehaviourJustChanged { get; private set; }

        /// <summary>
        /// True if the last Update() detected a behaviour pointer change that
        /// didn't match any known InputMode. Indicates a scene transition where
        /// IL2CPP objects may be unstable. Main loop should pause IL2CPP calls.
        /// Unlike BehaviourJustChanged, this does NOT reset _lastBehaviourPtr,
        /// so the fast-path still works when the pointer stabilizes.
        /// </summary>
        public static bool NoModeMatched { get; private set; }

        /// <summary>
        /// True for one cycle after guard mode exits via stability threshold.
        /// The main loop can use this to immediately probe for active handlers
        /// instead of waiting for NoneProbeThreshold, since guard already
        /// confirmed pointer stability for ~1 second.
        /// </summary>
        public static bool GuardJustExited { get; private set; }

        /// <summary>
        /// True while guard mode is active. The main loop uses this to suppress
        /// FindObjectsOfType calls (NONE probe) that can hit partially-destroyed
        /// scene objects during transitions.
        /// </summary>
        public static bool IsInGuardMode => _guardMode;


        public static void Initialize()
        {
            _currentMode = InputManager.InputMode.NONE;
            _initialized = true;
            MelonLogger.Msg("[SRWYAccess] GameStateTracker initialized.");
        }

        /// <summary>
        /// Enter guard mode preemptively. Call before a known dangerous
        /// transition (e.g. ADVENTURE → NONE) to protect the next pointer
        /// change from running the full 17-mode detection loop.
        /// </summary>
        public static void EnterGuardMode()
        {
            if (!_guardMode)
            {
                _guardMode = true;
                _guardStableCount = 0;
                _guardTotalCount = 0;
                _consecutiveGuardFailures = 0; // fresh preemptive guard resets retries
                DebugHelper.Write("GST: Entering guard mode (preemptive)");
            }
        }

        public static void Update()
        {
            if (!_initialized) return;
            BehaviourJustChanged = false;
            NoModeMatched = false;
            GuardJustExited = false;

            // Guard warmup: skip SafeCall for first N cycles after entering
            // guard mode. During scene transitions, InputManager's internal
            // pointers may be in flux and even VEH-protected SafeCall can
            // cause side effects. Reduced with SEH for faster recovery.
            if (_guardMode && _guardTotalCount < ModConfig.GuardWarmupCyclesEffective)
            {
                _guardTotalCount++;
                return;
            }

            // Read the singleton directly each cycle. Static field read is safe
            // even during scene transitions (reads from type metadata, not instance
            // data). Returns null if InputManager was destroyed.
            InputManager inputMgr;
            try
            {
                inputMgr = InputManager.instance;
            }
            catch
            {
                return; // Static field read failed - type not ready
            }

            if ((object)inputMgr == null)
            {
                // InputManager destroyed (scene transition). Reset behaviour
                // pointer so we do a full re-detection when it comes back.
                _lastBehaviourPtr = IntPtr.Zero;
                // CRITICAL: Do NOT exit guard mode on temporary null.
                // During scene transitions, InputManager can return null for
                // several frames while scenes load. Exiting guard here exposes
                // the mod to accessing freed IL2CPP objects on the next cycle.
                if (_guardMode)
                {
                    _guardTotalCount++;
                    return;
                }
                return;
            }

            // Verify native pointer is still valid before calling instance methods.
            // During scene transitions, the managed wrapper may survive but the
            // native object is destroyed → AccessViolationException (uncatchable).
            try
            {
                if (inputMgr.Pointer == IntPtr.Zero)
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    if (_guardMode) { _guardTotalCount++; return; }
                    return;
                }
            }
            catch
            {
                _lastBehaviourPtr = IntPtr.Zero;
                if (_guardMode) { _guardTotalCount++; return; }
                return;
            }

            IntPtr currentPtr;
            if (SafeCall.IsAvailable)
            {
                // SEH-protected: catches AV on freed native memory
                currentPtr = SafeCall.GetCurrentInputBehaviourSafe(inputMgr.Pointer);
                if (currentPtr == IntPtr.Zero)
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    if (_guardMode) { _guardTotalCount++; return; }
                    return;
                }
            }
            else
            {
                // Fallback: managed call (still can't catch AV in .NET 6)
                IInputBehaviour currentBehaviour;
                try
                {
                    currentBehaviour = inputMgr.GetCurrentInputBehaviour();
                }
                catch
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    if (_guardMode) { _guardTotalCount++; return; }
                    return;
                }
                if ((object)currentBehaviour == null)
                {
                    if (_guardMode) { _guardTotalCount++; return; }
                    return;
                }
                currentPtr = currentBehaviour.Pointer;
            }

            // Fast path: if behaviour pointer hasn't changed, mode is the same.
            // This avoids 17 GetInputBehaviour calls per cycle.
            if (currentPtr == _lastBehaviourPtr)
            {
                // In guard mode: count consecutive stable cycles.
                // When stable long enough, exit guard and run full detection.
                if (_guardMode)
                {
                    _guardTotalCount++;
                    _guardStableCount++;

                    if (_guardTotalCount >= ModConfig.GuardMaxCyclesEffective)
                    {
                        _guardMode = false;
                        GuardJustExited = true;
                        DebugHelper.Write("GST: Guard mode timeout, resuming normal detection");
                    }
                    else if (_guardStableCount >= ModConfig.GuardStabilityThresholdEffective)
                    {
                        _guardMode = false;
                        GuardJustExited = true;
                        DebugHelper.Write($"GST: Guard mode ended (stable for {_guardStableCount} cycles)");
                        // Pointer is stable - safe to run full detection now
                        RunFullModeDetection(inputMgr, currentPtr);
                    }
                }
                return;
            }

            // Pointer changed
            _lastBehaviourPtr = currentPtr;
            BehaviourJustChanged = true;

            // In guard mode: DON'T run the dangerous 17-mode detection loop.
            // Just signal the change and wait for stability.
            if (_guardMode)
            {
                _guardStableCount = 0;
                _guardTotalCount++;
                NoModeMatched = true;
                DebugHelper.Write($"GST: Guard mode - pointer changed to 0x{currentPtr:X}, deferring detection");
                return;
            }

            // Tactical mode pointer change: with SEH protection, we can safely
            // run immediate mode detection. This avoids the ~860ms guard delay
            // for tactical→tactical sub-mode transitions (command menus, weapon
            // select, etc.). If detection finds NONE or fails, the main loop's
            // HandleModeChange will set guard mode for the dangerous transition.
            if (IsTacticalMode(_currentMode))
            {
                if (SafeCall.IsAvailable)
                {
                    // SEH catches AV on freed objects → safe to detect immediately
                    DebugHelper.Write($"GST: Tactical pointer change, immediate detection (SEH)");
                    RunFullModeDetection(inputMgr, currentPtr);
                    return;
                }
                else
                {
                    // No SEH → guard mode for safety
                    _guardMode = true;
                    _guardStableCount = 0;
                    _guardTotalCount = 0;
                    _consecutiveGuardFailures = 0;
                    NoModeMatched = true;
                    DebugHelper.Write($"GST: Pointer change in {_currentMode} - guard deferred (no SEH)");
                    return;
                }
            }

            // NONE mode pointer change: always use guard mode since NONE
            // transitions can be anything (adventure unloading, battle loading,
            // menu navigation). Even with active UI flags (postTitle, postTactical),
            // the pointer change itself signals potential scene destruction.
            // Guard mode defers the 17-mode detection loop until the pointer
            // stabilizes, preventing AV on freed native objects.
            if (_currentMode == InputManager.InputMode.NONE)
            {
                _guardMode = true;
                _guardStableCount = 0;
                _guardTotalCount = 0;
                _consecutiveGuardFailures = 0;
                NoModeMatched = true;
                DebugHelper.Write($"GST: Pointer change in NONE - guard deferred detection");
                return;
            }

            RunFullModeDetection(inputMgr, currentPtr);
        }

        /// <summary>
        /// Run the full 17-mode detection loop. Only call when the scene is
        /// stable (not during guard mode transitions).
        /// </summary>
        private static void RunFullModeDetection(InputManager inputMgr, IntPtr currentPtr)
        {
            InputManager.InputMode detectedMode = InputManager.InputMode.NONE;
            if (SafeCall.IsAvailable)
            {
                // SEH-protected mode detection: catches AV on freed behaviour objects
                IntPtr mgrPtr = inputMgr.Pointer;
                foreach (var mode in _allModes)
                {
                    IntPtr modePtr = SafeCall.GetInputBehaviourSafe(mgrPtr, (int)mode);
                    if (modePtr != IntPtr.Zero && modePtr == currentPtr)
                    {
                        detectedMode = mode;
                        break;
                    }
                }
            }
            else
            {
                // Fallback: managed calls with try/catch
                foreach (var mode in _allModes)
                {
                    try
                    {
                        var modeBehaviour = inputMgr.GetInputBehaviour(mode);
                        if ((object)modeBehaviour != null && modeBehaviour.Pointer == currentPtr)
                        {
                            detectedMode = mode;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // No mode matched while already in NONE: scene is still transitioning.
            // Enter guard mode to protect subsequent pointer changes, but limit
            // consecutive retries to prevent infinite guard loop.
            if (detectedMode == InputManager.InputMode.NONE && _currentMode == InputManager.InputMode.NONE)
            {
                _consecutiveGuardFailures++;
                DebugHelper.Write($"GST: behaviour pointer changed (0x{currentPtr:X}) but no mode matched (attempt {_consecutiveGuardFailures})");
                NoModeMatched = true;

                // Accept NONE immediately after first guard cycle.
                // Previous retries (up to 3) wasted ~1.2s for stable pointers.
                // The preemptive guard (~535ms) provides sufficient protection.
                DebugHelper.Write($"GST: No mode matched (attempt {_consecutiveGuardFailures}), accepting NONE state");
            }

            if (detectedMode != _currentMode)
            {
                _consecutiveGuardFailures = 0; // successful detection resets retries
                var previousMode = _currentMode;
                _currentMode = detectedMode;
                OnModeChanged(previousMode, detectedMode);
            }
        }

        /// <summary>
        /// Lightweight pointer stability check for use during stabilization.
        /// Only reads InputManager.instance and GetCurrentInputBehaviour (2-3 IL2CPP
        /// calls) instead of iterating all 17 modes (17+ calls). Returns true if
        /// the behaviour pointer is unchanged from last check (stable).
        /// Sets BehaviourJustChanged when pointer changes.
        /// </summary>
        public static bool CheckPointerStable()
        {
            if (!_initialized) return false;
            BehaviourJustChanged = false;
            NoModeMatched = false;

            InputManager inputMgr;
            try { inputMgr = InputManager.instance; }
            catch { return false; }

            if ((object)inputMgr == null)
            {
                _lastBehaviourPtr = IntPtr.Zero;
                return false;
            }

            try
            {
                if (inputMgr.Pointer == IntPtr.Zero)
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    return false;
                }
            }
            catch
            {
                _lastBehaviourPtr = IntPtr.Zero;
                return false;
            }

            IntPtr currentPtr;
            if (SafeCall.IsAvailable)
            {
                currentPtr = SafeCall.GetCurrentInputBehaviourSafe(inputMgr.Pointer);
                if (currentPtr == IntPtr.Zero)
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    return false;
                }
            }
            else
            {
                IInputBehaviour currentBehaviour;
                try { currentBehaviour = inputMgr.GetCurrentInputBehaviour(); }
                catch
                {
                    _lastBehaviourPtr = IntPtr.Zero;
                    return false;
                }
                if ((object)currentBehaviour == null) return false;
                currentPtr = currentBehaviour.Pointer;
            }

            if (currentPtr != _lastBehaviourPtr)
            {
                _lastBehaviourPtr = currentPtr;
                BehaviourJustChanged = true;
                return false;
            }
            return true;
        }

        private static void OnModeChanged(InputManager.InputMode from, InputManager.InputMode to)
        {
            DebugHelper.Write($"GameState: {from} -> {to}");
            MelonLogger.Msg($"[SRWYAccess] Game state: {from} -> {to}");

            string announcement = GetModeAnnouncement(to);
            if (!string.IsNullOrEmpty(announcement))
            {
                ScreenReaderOutput.Say(announcement);
            }
        }

        /// <summary>
        /// Check if the given mode is any tactical-part sub-mode.
        /// </summary>
        public static bool IsTacticalMode(InputManager.InputMode mode)
        {
            return mode == InputManager.InputMode.TACTICAL_PART
                || mode == InputManager.InputMode.TACTICAL_PART_BUTTON_UI
                || mode == InputManager.InputMode.TACTICAL_PART_READY_PAGE_BUTTON_UI
                || mode == InputManager.InputMode.TACTICAL_PART_ROBOT_LIST_SELECT_UI
                || mode == InputManager.InputMode.TACTICAL_PART_WEAPON_LIST_SELECT_UI
                || mode == InputManager.InputMode.TACTICAL_PART_NON_PLAYER_TURN
                || mode == InputManager.InputMode.TACTICAL_PART_AUTO_BATTLE;
        }

        private static string GetModeAnnouncement(InputManager.InputMode mode)
        {
            switch (mode)
            {
                case InputManager.InputMode.TITLE:
                    return Loc.Get("state_title_press_any_key");
                case InputManager.InputMode.MAIN_MENU:
                    return Loc.Get("state_main_menu");
                case InputManager.InputMode.ADVENTURE:
                    return Loc.Get("state_adventure");
                case InputManager.InputMode.TACTICAL_PART:
                    return Loc.Get("state_tactical");
                case InputManager.InputMode.BATTLE_SCENE:
                    return Loc.Get("state_battle");
                case InputManager.InputMode.STRATEGY_PART:
                    return Loc.Get("state_strategy");
                case InputManager.InputMode.OPENING_DEMO:
                    return Loc.Get("state_opening_demo");
                case InputManager.InputMode.GAME_CLEAR:
                    return Loc.Get("state_game_clear");
                case InputManager.InputMode.TACTICAL_PART_BUTTON_UI:
                    return Loc.Get("state_command_menu");
                case InputManager.InputMode.TACTICAL_PART_NON_PLAYER_TURN:
                    return Loc.Get("state_enemy_turn");
                case InputManager.InputMode.TACTICAL_PART_AUTO_BATTLE:
                    return Loc.Get("state_auto_battle");
                default:
                    return null;
            }
        }

    }
}
