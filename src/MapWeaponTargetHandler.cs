using System;
using System.Collections.Generic;
using Il2CppCom.BBStudio.SRTeam.Manager;
using Il2CppCom.BBStudio.SRTeam.State.GameState;
using Il2CppCom.BBStudio.SRTeam.State.GameState.TacticalPart;
using Il2CppCom.BBStudio.SRTeam.Map;

namespace SRWYAccess
{
    /// <summary>
    /// Announces enemy/ally counts during MAP weapon direction selection.
    ///
    /// Accesses the active PlayerAttackMapWeaponTask via:
    ///   GameManager.Instance → gameStateHandler → currentState
    ///   → TryCast&lt;TacticalPartState&gt;() → Task → activeTask
    ///   → TryCast&lt;PlayerAttackMapWeaponTask&gt;()
    ///
    /// Only auto-announces for weapons with OnMapwFriendlyFire = true.
    /// Tracks target counts and only re-announces when they change
    /// (indicating direction was rotated).
    ///
    /// Hotkeys (called from SRWYAccessMod):
    ///   Alt+; → ReadEnemyNames (list enemy unit names in range)
    ///   Alt+' → ReadAllyNames (list ally unit names in range)
    ///
    /// SAFETY: All IL2CPP null checks use (object)x != null.
    /// All property access wrapped in try/catch.
    /// </summary>
    public class MapWeaponTargetHandler
    {
        private int _lastEnemyCount = -1;
        private int _lastAllyCount = -1;
        private int _pollSkip = 0;

        // Suppress first announcement when entering MAP weapon mode
        // (counts are announced on CHANGE, not on initial entry)
        private bool _initialReading = true;

        /// <summary>
        /// Whether we currently detect an active MAP weapon task.
        /// Used by main mod to decide whether to intercept hotkeys.
        /// </summary>
        public bool IsActive { get; private set; }

        public void Reset()
        {
            _lastEnemyCount = -1;
            _lastAllyCount = -1;
            _pollSkip = 0;
            _initialReading = true;
            IsActive = false;
        }

        public void Update()
        {
            // Rate limit: check every 3 frames
            if (++_pollSkip < 3)
                return;
            _pollSkip = 0;

            try
            {
                UpdateInner();
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MapWeaponTarget: error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateInner()
        {
            var mapTask = GetActiveMapWeaponTask();
            if ((object)mapTask == null)
            {
                ClearIfActive();
                IsActive = false;
                return;
            }

            IsActive = true;

            // Check if the weapon has friendly fire
            bool hasFriendlyFire = false;
            try
            {
                var weapon = mapTask.weapon;
                if ((object)weapon == null || weapon.Pointer == IntPtr.Zero
                    || !SafeCall.ProbeObject(weapon.Pointer))
                {
                    ClearIfActive();
                    return;
                }
                hasFriendlyFire = weapon.OnMapwFriendlyFire;
            }
            catch
            {
                ClearIfActive();
                return;
            }

            if (!hasFriendlyFire)
            {
                ClearIfActive();
                return;
            }

            // Read targetPawnUnits and count allies/enemies
            int enemyCount = 0;
            int allyCount = 0;

            try
            {
                var targets = mapTask.targetPawnUnits;
                if ((object)targets == null || targets.Pointer == IntPtr.Zero
                    || !SafeCall.ProbeObject(targets.Pointer))
                {
                    AnnounceIfChanged(0, 0);
                    return;
                }

                int count = targets.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var pu = targets[i];
                        if ((object)pu == null || pu.Pointer == IntPtr.Zero
                            || !SafeCall.ProbeObject(pu.Pointer))
                            continue;

                        bool isPlayerSide = false;
                        try { isPlayerSide = pu.IsPlayerSide; } catch { continue; }

                        if (isPlayerSide)
                            allyCount++;
                        else
                            enemyCount++;
                    }
                    catch { }
                }
            }
            catch
            {
                ClearIfActive();
                return;
            }

            AnnounceIfChanged(enemyCount, allyCount);
        }

        /// <summary>
        /// Read names of enemy units in the MAP weapon's affected area.
        /// Returns null if not in MAP weapon mode.
        /// </summary>
        public string ReadEnemyNames()
        {
            return ReadTargetNames(playerSide: false);
        }

        /// <summary>
        /// Read names of ally units in the MAP weapon's affected area.
        /// Returns null if not in MAP weapon mode.
        /// </summary>
        public string ReadAllyNames()
        {
            return ReadTargetNames(playerSide: true);
        }

        private string ReadTargetNames(bool playerSide)
        {
            try
            {
                var mapTask = GetActiveMapWeaponTask();
                if ((object)mapTask == null) return null;

                var targets = mapTask.targetPawnUnits;
                if ((object)targets == null || targets.Pointer == IntPtr.Zero
                    || !SafeCall.ProbeObject(targets.Pointer))
                    return Loc.Get("map_weapon_no_targets");

                var names = new List<string>();
                int count = targets.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var pu = targets[i];
                        if ((object)pu == null || pu.Pointer == IntPtr.Zero
                            || !SafeCall.ProbeObject(pu.Pointer))
                            continue;

                        bool isPlayer = false;
                        try { isPlayer = pu.IsPlayerSide; } catch { continue; }

                        if (isPlayer != playerSide) continue;

                        string name = GetUnitName(pu);
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
                    catch { }
                }

                if (names.Count == 0)
                    return Loc.Get("map_weapon_no_targets");

                return string.Join(", ", names);
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MapWeaponTarget: ReadTargetNames error: {ex.GetType().Name}");
                return null;
            }
        }

        /// <summary>
        /// Get display name for a PawnUnit: "PilotName (RobotName)" or just one.
        /// </summary>
        private static string GetUnitName(PawnUnit pu)
        {
            string robotName = null;
            string pilotName = null;

            try
            {
                var pawn = pu.PawnData;
                if ((object)pawn == null || pawn.Pointer == IntPtr.Zero) return null;

                var robot = pawn.BelongRobot;
                if ((object)robot == null || robot.Pointer == IntPtr.Zero) return null;

                if (SafeCall.NameMethodsAvailable)
                {
                    robotName = SafeCall.ReadRobotNameSafe(robot.Pointer);

                    try
                    {
                        var pilot = robot.MainPilot;
                        if ((object)pilot != null && pilot.Pointer != IntPtr.Zero)
                            pilotName = SafeCall.ReadPilotNameSafe(pilot.Pointer);
                    }
                    catch { }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(pilotName) && !string.IsNullOrEmpty(robotName))
                return pilotName + " (" + robotName + ")";
            if (!string.IsNullOrEmpty(robotName))
                return robotName;
            if (!string.IsNullOrEmpty(pilotName))
                return pilotName;
            return null;
        }

        /// <summary>
        /// Try to get the active PlayerAttackMapWeaponTask from the game state chain.
        /// Returns null if not in MAP weapon targeting mode.
        /// </summary>
        private static PlayerAttackMapWeaponTask GetActiveMapWeaponTask()
        {
            try
            {
                var gm = GameManager.Instance;
                if ((object)gm == null || gm.Pointer == IntPtr.Zero) return null;

                var gsh = gm.gameStateHandler;
                if ((object)gsh == null || gsh.Pointer == IntPtr.Zero) return null;

                var stateBase = gsh.currentState;
                if ((object)stateBase == null || stateBase.Pointer == IntPtr.Zero) return null;

                var tps = stateBase.TryCast<TacticalPartState>();
                if ((object)tps == null || tps.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(tps.Pointer)) return null;

                var taskControl = tps.Task;
                if ((object)taskControl == null || taskControl.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(taskControl.Pointer)) return null;

                var activeTask = taskControl.activeTask;
                if ((object)activeTask == null || activeTask.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(activeTask.Pointer)) return null;

                return activeTask.TryCast<PlayerAttackMapWeaponTask>();
            }
            catch
            {
                return null;
            }
        }

        private void AnnounceIfChanged(int enemyCount, int allyCount)
        {
            if (enemyCount == _lastEnemyCount && allyCount == _lastAllyCount)
                return;

            _lastEnemyCount = enemyCount;
            _lastAllyCount = allyCount;

            // Skip the very first reading (when we first detect the task)
            if (_initialReading)
            {
                _initialReading = false;
                return;
            }

            if (allyCount > 0)
            {
                ScreenReaderOutput.Say(Loc.Get("map_weapon_targets", enemyCount, allyCount));
            }
            else
            {
                ScreenReaderOutput.Say(Loc.Get("map_weapon_targets_enemy_only", enemyCount));
            }

            DebugHelper.Write($"MapWeaponTarget: enemies={enemyCount}, allies={allyCount}");
        }

        /// <summary>
        /// Clear tracked counts when MAP weapon task is no longer active.
        /// </summary>
        private void ClearIfActive()
        {
            if (_lastEnemyCount != -1 || _lastAllyCount != -1)
            {
                _lastEnemyCount = -1;
                _lastAllyCount = -1;
                _initialReading = true;
            }
        }
    }
}
