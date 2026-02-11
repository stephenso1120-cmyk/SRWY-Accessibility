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
    /// All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator.
    /// </summary>
    public static class GameStateTracker
    {
        private static InputManager.InputMode _currentMode = InputManager.InputMode.NONE;
        private static bool _initialized;
        private static IntPtr _lastBehaviourPtr = IntPtr.Zero;

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

        public static void Initialize()
        {
            _currentMode = InputManager.InputMode.NONE;
            _initialized = true;
            MelonLogger.Msg("[SRWYAccess] GameStateTracker initialized.");
        }

        public static void Update()
        {
            if (!_initialized) return;
            BehaviourJustChanged = false;
            NoModeMatched = false;

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
                    return;
                }
            }
            catch
            {
                _lastBehaviourPtr = IntPtr.Zero;
                return;
            }

            IInputBehaviour currentBehaviour;
            try
            {
                currentBehaviour = inputMgr.GetCurrentInputBehaviour();
            }
            catch
            {
                _lastBehaviourPtr = IntPtr.Zero;
                return;
            }

            if ((object)currentBehaviour == null) return;

            IntPtr currentPtr = currentBehaviour.Pointer;

            // Fast path: if behaviour pointer hasn't changed, mode is the same.
            // This avoids 17 GetInputBehaviour calls per cycle.
            if (currentPtr == _lastBehaviourPtr) return;
            _lastBehaviourPtr = currentPtr;
            BehaviourJustChanged = true;

            // Pointer changed - do full mode detection
            InputManager.InputMode detectedMode = InputManager.InputMode.NONE;
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

            // Log when pointer changed but no mode matched (stays NONE).
            // Signal main loop to pause IL2CPP calls during this transition.
            // Do NOT reset _lastBehaviourPtr: if the pointer stays the same next
            // cycle, the fast-path will skip re-detection (avoiding infinite loop).
            if (detectedMode == InputManager.InputMode.NONE && _currentMode == InputManager.InputMode.NONE)
            {
                DebugHelper.Write($"GST: behaviour pointer changed (0x{currentPtr:X}) but no mode matched");
                NoModeMatched = true;
            }

            if (detectedMode != _currentMode)
            {
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

            IInputBehaviour currentBehaviour;
            try { currentBehaviour = inputMgr.GetCurrentInputBehaviour(); }
            catch
            {
                _lastBehaviourPtr = IntPtr.Zero;
                return false;
            }

            if ((object)currentBehaviour == null) return false;

            IntPtr currentPtr = currentBehaviour.Pointer;
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
