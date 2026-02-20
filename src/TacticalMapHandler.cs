using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.Map;
using Il2CppCom.BBStudio.SRTeam.Data;

namespace SRWYAccess
{
    /// <summary>
    /// Reads tactical map cursor position and announces unit/terrain info.
    /// Uses MapManager.Instance (static singleton, safe from background thread)
    /// to get FloatingCursor position and GetPawnHere() for unit at cursor.
    ///
    /// Also tracks unit changes via PawnController.SelectedPawnInfo to announce
    /// unit names when the player cycles units with Q/E (allies) or 1/3 (enemies),
    /// and when selecting attack targets.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class TacticalMapHandler
    {
        private Vector2Int _lastCoord = new Vector2Int(-999, -999);
        private string _lastUnitInfo;

        // Track selected unit pointer to detect Q/E and 1/3 unit cycling
        private IntPtr _lastPawnPtr = IntPtr.Zero;
        private bool _cursorFound;

        // Public access for screen review
        public Vector2Int LastCoord => _lastCoord;
        public string LastUnitInfo => _lastUnitInfo;

        public void ReleaseHandler()
        {
            _lastCoord = new Vector2Int(-999, -999);
            _lastUnitInfo = null;
            _lastPawnPtr = IntPtr.Zero;
            _cursorFound = false;
        }

        /// <summary>
        /// Main update for TACTICAL_PART mode (map navigation).
        /// Tracks both coord changes and unit changes.
        /// SAFETY: Gets fresh FloatingCursor from MapManager.Instance.Cursor every poll.
        /// Never caches the cursor ref - cached IL2CPP refs crash when Unity recreates
        /// objects (Pointer stays non-zero on destroyed objects → uncatchable AV).
        /// </summary>
        public void Update(bool canSearch)
        {
            var cursor = GetFreshCursor();
            if ((object)cursor == null) return;

            // Track unit cycling (Q/E, 1/3) via PawnController
            CheckUnitChange();

            // Read current coord via direct field read (bypasses virtual dispatch)
            Vector2Int coord;
            if (SafeCall.TacticalFieldsAvailable)
            {
                var (ok, cx, cy) = SafeCall.ReadCurrentCoordSafe(cursor.Pointer);
                if (!ok) return;
                coord = new Vector2Int(cx, cy);
            }
            else
            {
                // SAFETY: If SafeCall unavailable, skip functionality completely.
                // DO NOT attempt direct IL2CPP access - try/catch cannot catch AV.
                return;
            }

            if (coord.x == _lastCoord.x && coord.y == _lastCoord.y) return;
            _lastCoord = coord;

            DebugHelper.Write($"TacticalMap: coord changed to {coord.x},{coord.y}, announcing");
            AnnouncePosition(coord, cursor);
        }

        /// <summary>
        /// Lightweight update for tactical sub-modes (button UI, weapon select, etc.).
        /// Only tracks unit changes (no coord announcements).
        /// </summary>
        public void UpdateUnitOnly(bool canSearch)
        {
            CheckUnitChange();
        }

        /// <summary>
        /// Get fresh FloatingCursor from MapManager (static singleton chain, safe).
        /// NEVER cache this ref - use it within a single poll cycle only.
        ///
        /// SAFETY: Uses SafeCall direct field reads (SafeReadPtr under VEH) instead
        /// of IL2CPP property getters (il2cpp_runtime_invoke). Property getters can
        /// crash on freed objects even when ProbeObject passes, because they use
        /// virtual dispatch which dereferences vtable pointers that may be corrupted.
        /// Direct field reads at known offsets only touch the object's own memory,
        /// which VEH catches if the page is unmapped.
        /// </summary>
        private FloatingCursor GetFreshCursor()
        {
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return null;

                // SEH probe: verify MapManager native object is still alive.
                if (!SafeCall.ProbeObject(mm.Pointer)) return null;

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.TacticalFieldsAvailable) return null;

                IntPtr cursorPtr = SafeCall.ReadMapCursorPtrSafe(mm.Pointer);
                if (cursorPtr == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(cursorPtr)) return null;

                var cursor = new FloatingCursor(cursorPtr);

                if (!_cursorFound)
                {
                    _cursorFound = true;
                    DebugHelper.Write("TacticalMap: Got FloatingCursor from MapManager");
                }
                return cursor;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Detect unit cycling (Q/E for allies, 1/3 for enemies, attack target selection).
        /// Uses PawnController.SelectedPawnInfo from MapManager (static singleton chain,
        /// no FindObjectOfType needed).
        /// </summary>
        private void CheckUnitChange()
        {
            IntPtr currentPawnPtr = IntPtr.Zero;
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return;
                if (!SafeCall.ProbeObject(mm.Pointer)) return;

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.TacticalFieldsAvailable) return;

                IntPtr pcPtr = SafeCall.ReadPawnControllerPtrSafe(mm.Pointer);
                if (pcPtr == IntPtr.Zero) return;
                if (!SafeCall.ProbeObject(pcPtr)) return;

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.PawnFieldsAvailable) return;

                IntPtr infoPtr = SafeCall.ReadSelectedPawnInfoPtrSafe(pcPtr);
                if (infoPtr != IntPtr.Zero && SafeCall.ProbeObject(infoPtr))
                {
                    IntPtr puPtr = SafeCall.ReadPawnUnitPtrSafe(infoPtr);
                    if (puPtr != IntPtr.Zero)
                        currentPawnPtr = puPtr;
                }
            }
            catch
            {
                return;
            }

            if (currentPawnPtr == _lastPawnPtr) return;
            _lastPawnPtr = currentPawnPtr;

            if (currentPawnPtr == IntPtr.Zero)
            {
                _lastUnitInfo = null;
                return;
            }

            // Unit changed - read and announce name
            string unitName = ReadUnitFromPawnController();
            if (!string.IsNullOrEmpty(unitName))
            {
                _lastUnitInfo = unitName;
                ScreenReaderOutput.Say(unitName);
                DebugHelper.Write($"TacticalMap: Unit changed: {unitName}");
            }
        }

        /// <summary>
        /// Read unit name from PawnController.SelectedPawnInfo.
        /// </summary>
        private string ReadUnitFromPawnController()
        {
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(mm.Pointer)) return null;

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.TacticalFieldsAvailable) return null;
                if (!SafeCall.PawnFieldsAvailable) return null;

                IntPtr pcPtr = SafeCall.ReadPawnControllerPtrSafe(mm.Pointer);
                if (pcPtr == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(pcPtr)) return null;

                IntPtr infoPtr = SafeCall.ReadSelectedPawnInfoPtrSafe(pcPtr);
                if (infoPtr == IntPtr.Zero || !SafeCall.ProbeObject(infoPtr)) return null;

                IntPtr puPtr = SafeCall.ReadPawnUnitPtrSafe(infoPtr);

                if (puPtr == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(puPtr)) return null;
                var pawnUnit = new PawnUnit(puPtr);

                return ReadPawnUnitName(pawnUnit);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract pilot/robot name from a PawnUnit.
        /// CRITICAL: All IL2CPP property access wrapped in try-catch.
        /// These try-catch cannot catch AV but can catch NullReferenceException.
        /// </summary>
        private static string ReadPawnUnitName(PawnUnit pawnUnit)
        {
            if (!SafeCall.ProbeObject(pawnUnit.Pointer)) return null;

            try
            {
                var pawn = pawnUnit.PawnData;
                if ((object)pawn == null) return null;

                var robot = pawn.BelongRobot;
                if ((object)robot == null) return null;

                // CRITICAL: Use SafeCall to read names - GetName() can AV on destroyed objects
                string robotName = null;
                string pilotName = null;

                if (SafeCall.NameMethodsAvailable)
                {
                    robotName = SafeCall.ReadRobotNameSafe(robot.Pointer);

                    try
                    {
                        var pilot = robot.MainPilot;
                        if ((object)pilot != null && pilot.Pointer != IntPtr.Zero)
                        {
                            pilotName = SafeCall.ReadPilotNameSafe(pilot.Pointer);
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(pilotName) && !string.IsNullOrEmpty(robotName))
                    return pilotName + " / " + robotName;
                return robotName ?? pilotName;
            }
            catch
            {
                return null;
            }
        }

        private void AnnouncePosition(Vector2Int coord, FloatingCursor cursor)
        {
            var (unitInfo, isPlayerSide) = ReadUnitAtCursorWithSide(cursor);
            _lastUnitInfo = unitInfo;

            // Audio cue: different tone for empty/ally/enemy
            if (isPlayerSide == null)
                AudioCueManager.Play(AudioCue.TileEmpty);
            else if (isPlayerSide == true)
                AudioCueManager.Play(AudioCue.TileAlly);
            else
                AudioCueManager.Play(AudioCue.TileEnemy);

            string announcement;
            if (!string.IsNullOrEmpty(unitInfo))
                announcement = Loc.Get("map_cursor_unit", coord.x, coord.y, unitInfo);
            else
                announcement = Loc.Get("map_cursor", coord.x, coord.y);

            ScreenReaderOutput.Say(announcement);
            DebugHelper.Write($"TacticalMap: {coord.x},{coord.y} unit={unitInfo}");
        }

        /// <summary>
        /// Collect current map position info for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            if (_lastCoord.x == -999 && _lastCoord.y == -999) return;

            if (!string.IsNullOrEmpty(_lastUnitInfo))
                items.Add(Loc.Get("map_cursor_unit", _lastCoord.x, _lastCoord.y, _lastUnitInfo));
            else
                items.Add(Loc.Get("map_cursor", _lastCoord.x, _lastCoord.y));

            CollectUnitDetailItems(items);
        }

        /// <summary>
        /// Read detailed unit stats for screen review via FloatingCursor.GetPawnHere().
        /// Uses fresh cursor from MapManager (no cached ref).
        /// </summary>
        private void CollectUnitDetailItems(System.Collections.Generic.List<string> items)
        {
            var cursor = GetFreshCursor();
            if ((object)cursor == null) return;

            try
            {
                if (!SafeCall.ProbeObject(cursor.Pointer)) return;

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.IsAvailable) return;

                IntPtr puPtr = SafeCall.GetPawnHereSafe(cursor.Pointer);
                if (puPtr == IntPtr.Zero) return;
                if (!SafeCall.ProbeObject(puPtr)) return;
                var pawnUnit = new PawnUnit(puPtr);

                // CRITICAL: Wrap IL2CPP property access
                Il2CppCom.BBStudio.SRTeam.Data.Pawn pawn;
                Il2CppCom.BBStudio.SRTeam.Data.Robot robot;
                Il2CppCom.BBStudio.SRTeam.Data.Pilot pilot = null;
                try
                {
                    pawn = pawnUnit.PawnData;
                    if ((object)pawn == null) return;

                    robot = pawn.BelongRobot;
                    if ((object)robot == null) return;

                    // Extract pilot once, reuse across all blocks
                    try { pilot = robot.MainPilot; } catch { }
                }
                catch { return; }

                // Current HP/EN + Morale
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null && lastData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(lastData.Pointer))
                    {
                        int hpNow = lastData.HPNow;
                        int enNow = lastData.ENNow;
                        int hpMax = 0, enMax = 0;
                        try
                        {
                            var calcData = robot.calcData;
                            if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(calcData.Pointer))
                            {
                                var param = calcData.Parameters;
                                if ((object)param != null && param.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(param.Pointer))
                                {
                                    hpMax = param.hp;
                                    enMax = param.en;
                                }
                            }
                        }
                        catch { }

                        // Read morale from pilot
                        string moraleStr = "";
                        try
                        {
                            if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(pilot.Pointer))
                            {
                                var pld = pilot.GetLastData();
                                if ((object)pld != null && pld.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(pld.Pointer))
                                    moraleStr = string.Format(", {0} {1}/{2}",
                                        Loc.Get("stat_morale"), pld.MoraleNow, pld.MoraleMax);
                            }
                        }
                        catch { }

                        if (hpMax > 0)
                            items.Add(string.Format("HP {0}/{1}, EN {2}/{3}{4}",
                                hpNow, hpMax, enNow, enMax, moraleStr));
                        else
                            items.Add(string.Format("HP {0}, EN {1}{2}",
                                hpNow, enNow, moraleStr));
                    }
                }
                catch { }

                // Robot stats + Size + Sight + Weapon boost
                try
                {
                    var calcData = robot.calcData;
                    if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(calcData.Pointer))
                    {
                        var param = calcData.Parameters;
                        if ((object)param != null && param.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(param.Pointer))
                        {
                            string sizeStr = "";
                            try
                            {
                                var lastData = robot.lastData;
                                if ((object)lastData != null)
                                    sizeStr = string.Format(", {0} {1}",
                                        Loc.Get("stat_size"), GetSizeName(lastData.Size));
                            }
                            catch { }

                            items.Add(string.Format("{0} {1}, {2} {3}, {4} {5}, {6} {7}, {8}+{9}{10}",
                                Loc.Get("stat_armor"), param.armor,
                                Loc.Get("stat_mobility"), param.mobility,
                                Loc.Get("stat_move"), calcData.MovePower,
                                Loc.Get("stat_sight"), param.sight,
                                Loc.Get("stat_weapon_boost"), param.weapon,
                                sizeStr));
                        }
                    }
                }
                catch { }

                // Terrain aptitude
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null && lastData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(lastData.Pointer))
                    {
                        var adapt = lastData.LandAdaptation;
                        if ((object)adapt != null && adapt.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(adapt.Pointer))
                        {
                            items.Add(string.Format("{0}: {1}{2} {3}{4} {5}{6} {7}{8}",
                                Loc.Get("stat_terrain"),
                                Loc.Get("terrain_sky"), adapt.sky,
                                Loc.Get("terrain_ground"), adapt.ground,
                                Loc.Get("terrain_water"), adapt.water,
                                Loc.Get("terrain_space"), adapt.space));
                        }
                    }
                }
                catch { }

                // Pilot name, level, SP
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        int level = 0;
                        try
                        {
                            var cd = pilot.calcData;
                            if ((object)cd != null && cd.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(cd.Pointer))
                                level = cd.Level;
                        }
                        catch { }
                        int sp = pilot.GetSP();
                        int maxSp = pilot.GetMaxSP();
                        // CRITICAL: Use SafeCall to read pilot name - direct GetName() causes
                        // uncatchable AccessViolationException when pilot object is destroyed
                        string pilotName = "";
                        if (SafeCall.NameMethodsAvailable)
                        {
                            pilotName = SafeCall.ReadPilotNameSafe(pilot.Pointer) ?? "";
                        }
                        items.Add(string.Format("{0} Lv.{1} SP:{2}/{3}", pilotName, level, sp, maxSp));
                    }
                }
                catch { }

                // Pilot combat stats
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        var cd = pilot.calcData;
                        if ((object)cd != null && cd.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(cd.Pointer))
                        {
                            var ps = cd.Parameters;
                            if ((object)ps != null && ps.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(ps.Pointer))
                            {
                                items.Add(string.Format("{0} {1}, {2} {3}, {4} {5}, {6} {7}, {8} {9}, {10} {11}",
                                    Loc.Get("stat_melee"), ps.melee,
                                    Loc.Get("stat_ranged"), ps.range,
                                    Loc.Get("stat_defend"), ps.defend,
                                    Loc.Get("stat_hit"), ps.hit,
                                    Loc.Get("stat_evade"), ps.evade,
                                    Loc.Get("stat_skill"), ps.skill));
                            }
                        }
                    }
                }
                catch { }

                // Support attack/defense counts
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        var pld = pilot.GetLastData();
                        if ((object)pld != null && pld.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(pld.Pointer))
                        {
                            items.Add(string.Format("{0} {1}/{2}, {3} {4}/{5}",
                                Loc.Get("stat_support_attack"),
                                pld.SupportAttackNow, pld.SupportAttackMax,
                                Loc.Get("stat_support_defense"),
                                pld.SupportDefenseNow, pld.SupportDefenseMax));
                        }
                    }
                }
                catch { }

                // Pilot EXP and Ace Rank
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        var pld = pilot.GetLastData();
                        if ((object)pld != null && pld.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(pld.Pointer))
                        {
                            // EXP current / next level
                            items.Add(string.Format("{0} {1}/{2}, {3} {4}",
                                Loc.Get("stat_exp"), pld.Exp, pld.NextLevelExp,
                                Loc.Get("stat_ace_rank"), GetAceRankName(pld.Rank)));

                            // Ace bonus (if pilot has ace rank)
                            if (pld.Rank != Il2CppCom.BBStudio.SRTeam.Data.AceRank.None)
                            {
                                try
                                {
                                    var aceBonus = pld.AceBonus;
                                    if ((object)aceBonus != null && aceBonus.Pointer != IntPtr.Zero
                                        && SafeCall.ProbeObject(aceBonus.Pointer))
                                    {
                                        string bonusName = aceBonus.GetName();
                                        if (!string.IsNullOrEmpty(bonusName))
                                            items.Add(string.Format("{0}: {1}",
                                                Loc.Get("stat_ace_bonus"), bonusName));
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                // Pilot skills
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        var cd = pilot.calcData;
                        if ((object)cd != null && cd.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(cd.Pointer))
                        {
                            var skills = cd.Skills;
                            if ((object)skills != null && skills.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(skills.Pointer) && skills.Count > 0)
                            {
                                var names = new System.Collections.Generic.List<string>();
                                int limit = Math.Min(skills.Count, 20);
                                for (int i = 0; i < limit; i++)
                                {
                                    try
                                    {
                                        var skill = skills[i];
                                        if ((object)skill == null || skill.Pointer == IntPtr.Zero
                                            || !SafeCall.ProbeObject(skill.Pointer)) continue;
                                        string sName = skill.GetName();
                                        if (!string.IsNullOrEmpty(sName))
                                        {
                                            int lv = skill.GetLevel();
                                            if (lv > 1)
                                                names.Add(sName + " Lv." + lv);
                                            else
                                                names.Add(sName);
                                        }
                                    }
                                    catch { }
                                }
                                if (names.Count > 0)
                                    items.Add(Loc.Get("stat_pilot_skills") + ": " +
                                        string.Join(", ", names));
                            }
                        }
                    }
                }
                catch { }

                // Spirit commands
                try
                {
                    if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(pilot.Pointer))
                    {
                        var cd = pilot.calcData;
                        if ((object)cd != null && cd.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(cd.Pointer))
                        {
                            var spirits = cd.SpiritCommands;
                            if ((object)spirits != null && spirits.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(spirits.Pointer) && spirits.Count > 0)
                            {
                                var sNames = new System.Collections.Generic.List<string>();
                                int limit = Math.Min(spirits.Count, 10);
                                for (int i = 0; i < limit; i++)
                                {
                                    try
                                    {
                                        var sp = spirits[i];
                                        if ((object)sp == null || sp.Pointer == IntPtr.Zero
                                            || !SafeCall.ProbeObject(sp.Pointer)) continue;
                                        string spId = sp.id;
                                        int spCost = sp.cost;
                                        if (!string.IsNullOrEmpty(spId))
                                        {
                                            // Try to resolve name via game localization
                                            string spName = ResolveGameString("SpiritCommand", spId);
                                            if (string.IsNullOrEmpty(spName))
                                                spName = spId;
                                            sNames.Add(string.Format("{0}({1})", spName, spCost));
                                        }
                                    }
                                    catch { }
                                }
                                if (sNames.Count > 0)
                                    items.Add(Loc.Get("stat_spirit_commands") + ": " +
                                        string.Join(", ", sNames));
                            }
                        }
                    }
                }
                catch { }

                // Weapons (all) with expanded info
                try
                {
                    var weapons = robot.weapons;
                    if ((object)weapons != null && weapons.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(weapons.Pointer) && weapons.Count > 0)
                    {
                        int limit = weapons.Count;
                        for (int i = 0; i < limit; i++)
                        {
                            try
                            {
                                var w = weapons[i];
                                if ((object)w == null || w.Pointer == IntPtr.Zero
                                    || !SafeCall.ProbeObject(w.Pointer)) continue;
                                string wName = w.Name;
                                if (string.IsNullOrWhiteSpace(wName)) continue;

                                // Base info: name, power, range, EN cost
                                string line = string.Format("{0} P:{1} R:{2}-{3} EN:{4}",
                                    wName, w.Power, w.RangeMin, w.RangeMax, w.ENCost);

                                // Critical rate
                                try
                                {
                                    int crit = w.Critical;
                                    line += string.Format(" {0}:{1}", Loc.Get("weapon_crit"), crit);
                                }
                                catch { }

                                // Ammo (only if weapon uses bullets)
                                try
                                {
                                    int bulletMax = w.BulletMax;
                                    if (bulletMax > 0)
                                        line += string.Format(" {0}:{1}/{2}",
                                            Loc.Get("weapon_ammo"), w.BulletNow, bulletMax);
                                }
                                catch { }

                                // Morale requirement (only if > 0)
                                try
                                {
                                    int moraleReq = w.MoraleCondition;
                                    if (moraleReq > 0)
                                        line += string.Format(" {0}:{1}",
                                            Loc.Get("weapon_morale_req"), moraleReq);
                                }
                                catch { }

                                // Weapon attributes: handling type + power type
                                try
                                {
                                    var handling = w.HandlingType;
                                    if (handling == Il2CppCom.BBStudio.SRTeam.Data.WeaponHandlingType.Melee)
                                        line += " " + Loc.Get("weapon_melee");
                                    else if (handling == Il2CppCom.BBStudio.SRTeam.Data.WeaponHandlingType.Shooting)
                                        line += " " + Loc.Get("weapon_shooting");
                                }
                                catch { }

                                try
                                {
                                    var ptype = w.PowerType;
                                    if (ptype == Il2CppCom.BBStudio.SRTeam.Data.WeaponPowerType.Beam)
                                        line += " " + Loc.Get("weapon_beam");
                                    else if (ptype == Il2CppCom.BBStudio.SRTeam.Data.WeaponPowerType.Entity)
                                        line += " " + Loc.Get("weapon_entity");
                                }
                                catch { }

                                // Special flags
                                try { if (w.OnUseMoveAfter) line += " " + Loc.Get("weapon_post_move"); } catch { }
                                try { if (w.OnBarrierPenetration) line += " " + Loc.Get("weapon_barrier_pen"); } catch { }
                                try { if (w.OnIgnoreSize) line += " " + Loc.Get("weapon_ignore_size"); } catch { }

                                // MAP weapon (enriched: type, range, tiles, friendly fire)
                                try
                                {
                                    var mapType = w.MapwFiringType;
                                    if (mapType != Il2CppCom.BBStudio.SRTeam.Data.MAPWFiringType.None)
                                    {
                                        // MAP type
                                        switch (mapType)
                                        {
                                            case Il2CppCom.BBStudio.SRTeam.Data.MAPWFiringType.Straight:
                                                line += " " + Loc.Get("weapon_map_straight");
                                                break;
                                            case Il2CppCom.BBStudio.SRTeam.Data.MAPWFiringType.Landing:
                                                line += " " + Loc.Get("weapon_map_landing");
                                                break;
                                            case Il2CppCom.BBStudio.SRTeam.Data.MAPWFiringType.Center:
                                                line += " " + Loc.Get("weapon_map_center");
                                                break;
                                            default:
                                                line += " " + Loc.Get("weapon_map");
                                                break;
                                        }

                                        // MAP range
                                        try
                                        {
                                            int mapRange = w.MapwRange;
                                            if (mapRange > 0)
                                                line += " " + Loc.Get("weapon_map_range", mapRange);
                                        }
                                        catch { }

                                        // Affected tiles count from MapwMatrix
                                        try
                                        {
                                            var matrix = w.MapwMatrix;
                                            if ((object)matrix != null && matrix.Pointer != IntPtr.Zero
                                                && SafeCall.ProbeObject(matrix.Pointer) && matrix.Count > 0)
                                                line += " " + Loc.Get("weapon_map_tiles", matrix.Count);
                                        }
                                        catch { }

                                        // Friendly fire warning
                                        try
                                        {
                                            if (w.OnMapwFriendlyFire)
                                                line += " " + Loc.Get("weapon_map_friendly_fire");
                                        }
                                        catch { }
                                    }
                                }
                                catch { }

                                // Debuff
                                try
                                {
                                    var debuff = w.DebuffType;
                                    if (debuff != Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.None)
                                        line += " " + GetDebuffName(debuff);
                                }
                                catch { }

                                items.Add(line);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Robot skills
                try
                {
                    var calcData = robot.calcData;
                    if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(calcData.Pointer))
                    {
                        var rSkills = calcData.Skills;
                        if ((object)rSkills != null && rSkills.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(rSkills.Pointer) && rSkills.Count > 0)
                        {
                            var names = new System.Collections.Generic.List<string>();
                            int limit = Math.Min(rSkills.Count, 20);
                            for (int i = 0; i < limit; i++)
                            {
                                try
                                {
                                    var skill = rSkills[i];
                                    if ((object)skill == null || skill.Pointer == IntPtr.Zero
                                        || !SafeCall.ProbeObject(skill.Pointer)) continue;
                                    string sName = skill.GetName();
                                    if (!string.IsNullOrEmpty(sName))
                                        names.Add(sName);
                                }
                                catch { }
                            }
                            if (names.Count > 0)
                                items.Add(Loc.Get("stat_robot_skills") + ": " +
                                    string.Join(", ", names));
                        }
                    }
                }
                catch { }

                // Power parts
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null && lastData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(lastData.Pointer))
                    {
                        var slots = lastData.PowerPartsSlots;
                        if ((object)slots != null && slots.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(slots.Pointer) && slots.Count > 0)
                        {
                            var partNames = new System.Collections.Generic.List<string>();
                            int limit = Math.Min(slots.Count, 10);
                            for (int i = 0; i < limit; i++)
                            {
                                try
                                {
                                    var slot = slots[i];
                                    if ((object)slot == null || slot.Pointer == IntPtr.Zero
                                        || !SafeCall.ProbeObject(slot.Pointer)) continue;
                                    var part = slot.Interface;
                                    if ((object)part == null || part.Pointer == IntPtr.Zero
                                        || !SafeCall.ProbeObject(part.Pointer)) continue;
                                    string pName = part.GetName();
                                    if (!string.IsNullOrEmpty(pName))
                                        partNames.Add(pName);
                                }
                                catch { }
                            }
                            if (partNames.Count > 0)
                                items.Add(Loc.Get("stat_power_parts") + ": " +
                                    string.Join(", ", partNames));
                        }
                    }
                }
                catch { }

                // Custom bonus
                try
                {
                    var calcData = robot.calcData;
                    if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(calcData.Pointer))
                    {
                        var customBonus = calcData.CustomBonus;
                        if ((object)customBonus != null && customBonus.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(customBonus.Pointer))
                        {
                            string cbName = customBonus.GetName();
                            if (!string.IsNullOrEmpty(cbName))
                                items.Add(Loc.Get("stat_custom_bonus") + ": " + cbName);
                        }
                    }
                }
                catch { }

                // Upgrade levels
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null && lastData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(lastData.Pointer))
                    {
                        var upgrades = lastData.UpgradeLevels;
                        if ((object)upgrades != null && upgrades.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(upgrades.Pointer))
                        {
                            items.Add(string.Format("{0}: HP:{1} EN:{2} {3}:{4} {5}:{6} {7}:{8} {9}:{10}",
                                Loc.Get("stat_upgrade_levels"),
                                upgrades.hp, upgrades.en,
                                Loc.Get("stat_armor"), upgrades.armor,
                                Loc.Get("stat_mobility"), upgrades.mobility,
                                Loc.Get("stat_sight"), upgrades.sight,
                                Loc.Get("stat_weapon_boost"), upgrades.weapon));
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Convert RobotSize enum to localized display name.
        /// </summary>
        private static string GetSizeName(Il2CppCom.BBStudio.SRTeam.Data.RobotSize size)
        {
            switch (size)
            {
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.SS: return Loc.Get("size_SS");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.S: return Loc.Get("size_S");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.M: return Loc.Get("size_M");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.L: return Loc.Get("size_L");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.L2: return Loc.Get("size_L2");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.L3: return Loc.Get("size_L3");
                case Il2CppCom.BBStudio.SRTeam.Data.RobotSize.Infinity: return Loc.Get("size_Infinity");
                default: return size.ToString();
            }
        }

        /// <summary>
        /// Convert AceRank enum to localized display name.
        /// </summary>
        private static string GetAceRankName(Il2CppCom.BBStudio.SRTeam.Data.AceRank rank)
        {
            switch (rank)
            {
                case Il2CppCom.BBStudio.SRTeam.Data.AceRank.None: return Loc.Get("rank_none");
                case Il2CppCom.BBStudio.SRTeam.Data.AceRank.Ace: return Loc.Get("rank_ace");
                case Il2CppCom.BBStudio.SRTeam.Data.AceRank.SuperAce: return Loc.Get("rank_superace");
                case Il2CppCom.BBStudio.SRTeam.Data.AceRank.UltraAce: return Loc.Get("rank_ultraace");
                default: return rank.ToString();
            }
        }

        /// <summary>
        /// Convert WeaponDebuffType enum to localized display name.
        /// </summary>
        private static string GetDebuffName(Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType debuff)
        {
            switch (debuff)
            {
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.ENDown: return Loc.Get("weapon_debuff_en_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.ArmorDown: return Loc.Get("weapon_debuff_armor_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.MobilityDown: return Loc.Get("weapon_debuff_mobility_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.SightDown: return Loc.Get("weapon_debuff_sight_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.MoraleDown: return Loc.Get("weapon_debuff_morale_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.SPDown: return Loc.Get("weapon_debuff_sp_down");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.CutPilotParameterInHalf: return Loc.Get("weapon_debuff_param_half");
                case Il2CppCom.BBStudio.SRTeam.Data.WeaponDebuffType.ShutDown: return Loc.Get("weapon_debuff_shutdown");
                default: return debuff.ToString();
            }
        }

        /// <summary>
        /// Try to resolve a game localization string via LocalizationManager.
        /// Returns null on any failure (unavailable, crash, etc).
        /// </summary>
        private static string ResolveGameString(string table, string key)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key))
                return null;
            try
            {
                var locMgr = Il2CppCom.BBStudio.SRTeam.Manager.LocalizationManager.instance;
                if ((object)locMgr == null) return null;
                return locMgr.GetString(ref table, ref key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Announce movement range of unit at cursor or selected unit.
        /// Reports max MovePower and remaining movement (calculated from route length
        /// between moveStartPosition and current cursor position).
        /// Falls back to PawnController.SelectedPawnInfo when no unit is at cursor
        /// (e.g. after unit has moved away from its original tile).
        /// Called on = key press during tactical modes.
        /// </summary>
        public void AnnounceMovementRange()
        {
            try
            {
                if (!SafeCall.IsAvailable)
                {
                    ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                    return;
                }

                // Read current cursor coordinate
                Vector2Int cursorCoord = _lastCoord;
                var cursor = GetFreshCursor();
                if ((object)cursor != null && SafeCall.ProbeObject(cursor.Pointer))
                {
                    if (SafeCall.TacticalFieldsAvailable)
                    {
                        var (cOk, cx, cy) = SafeCall.ReadCurrentCoordSafe(cursor.Pointer);
                        if (cOk) cursorCoord = new Vector2Int(cx, cy);
                    }
                }

                // Check if PawnMoveCalculation is active (unit is in movement selection mode).
                // When active, ALWAYS use movePawnUnit for name/MovePower — NOT the unit
                // at cursor position (cursor passes over other units during movement).
                IntPtr puPtr = IntPtr.Zero;
                string name = null;
                int movePower = -1;
                int remaining = -1;

                try
                {
                    var mm = MapManager.Instance;
                    if ((object)mm != null && mm.Pointer != IntPtr.Zero && SafeCall.ProbeObject(mm.Pointer))
                    {
                        var board = mm.TacticalBoard;
                        if ((object)board != null && SafeCall.ProbeObject(board.Pointer))
                        {
                            var pmc = board.pawnMoveCalculation;
                            if ((object)pmc != null && SafeCall.ProbeObject(pmc.Pointer))
                            {
                                var movePawnUnit = pmc.movePawnUnit;
                                if ((object)movePawnUnit != null && movePawnUnit.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(movePawnUnit.Pointer))
                                {
                                    // Active movement: use the MOVING unit (not cursor unit)
                                    puPtr = movePawnUnit.Pointer;
                                    var pawnUnit = new PawnUnit(puPtr);
                                    name = ReadPawnUnitName(pawnUnit);

                                    // Read MovePower from moveStatus (movement-specific)
                                    int totalMove = -1;
                                    var moveStatus = pmc.moveStatus;
                                    if ((object)moveStatus != null && SafeCall.ProbeObject(moveStatus.Pointer))
                                        totalMove = moveStatus.MovePower;
                                    movePower = totalMove;

                                    // Read MovePower from calcData as fallback
                                    if (movePower < 0)
                                    {
                                        try
                                        {
                                            var pawn = pawnUnit.PawnData;
                                            if ((object)pawn != null)
                                            {
                                                var robot = pawn.BelongRobot;
                                                if ((object)robot != null)
                                                {
                                                    var calcData = robot.calcData;
                                                    if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                                                        && SafeCall.ProbeObject(calcData.Pointer))
                                                        movePower = calcData.MovePower;
                                                }
                                            }
                                        }
                                        catch { }
                                    }

                                    // Calculate remaining: route from unit position to cursor
                                    try
                                    {
                                        var unitPos = movePawnUnit.currentCoord;
                                        if (cursorCoord.x == unitPos.x && cursorCoord.y == unitPos.y)
                                        {
                                            remaining = movePower;
                                        }
                                        else
                                        {
                                            var route = pmc.GetMovementRoute(unitPos, cursorCoord);
                                            if ((object)route != null && route.Count > 0 && movePower >= 0)
                                                remaining = movePower - (route.Count - 1);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }

                // No active movement: fall back to unit at cursor or selected unit
                if (puPtr == IntPtr.Zero)
                {
                    if ((object)cursor != null && SafeCall.ProbeObject(cursor.Pointer))
                        puPtr = SafeCall.GetPawnHereSafe(cursor.Pointer);
                    if (puPtr == IntPtr.Zero)
                        puPtr = GetSelectedPawnUnitPtr();

                    if (puPtr == IntPtr.Zero || !SafeCall.ProbeObject(puPtr))
                    {
                        ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                        return;
                    }

                    var pawnUnit = new PawnUnit(puPtr);
                    name = ReadPawnUnitName(pawnUnit);

                    try
                    {
                        var pawn = pawnUnit.PawnData;
                        if ((object)pawn != null)
                        {
                            var robot = pawn.BelongRobot;
                            if ((object)robot != null)
                            {
                                var calcData = robot.calcData;
                                if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(calcData.Pointer))
                                    movePower = calcData.MovePower;
                            }
                        }
                    }
                    catch { }
                }

                string announcement;
                string unitName = !string.IsNullOrEmpty(name) ? name + ", " : "";

                if (movePower >= 0 && remaining >= 0)
                    announcement = string.Format("{0}{1} {2}/{3}",
                        unitName, Loc.Get("stat_move"), remaining, movePower);
                else if (movePower >= 0)
                    announcement = string.Format("{0}{1} {2}",
                        unitName, Loc.Get("stat_move"), movePower);
                else
                {
                    ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                    return;
                }

                ScreenReaderOutput.Say(announcement);
                DebugHelper.Write($"MovementRange: {announcement}");
            }
            catch
            {
                ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
            }
        }

        /// <summary>
        /// Announce attack range based on usable weapons.
        /// After movement, weapons with CanNotUseAfterMove are excluded.
        /// Called on - key press during tactical modes.
        /// </summary>
        public void AnnounceAttackRange()
        {
            try
            {
                if (!SafeCall.IsAvailable)
                {
                    ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                    return;
                }

                // Get the unit: PMC active → movePawnUnit; else → cursor/selected unit
                IntPtr puPtr = IntPtr.Zero;
                string name = null;

                try
                {
                    var mm = MapManager.Instance;
                    if ((object)mm != null && mm.Pointer != IntPtr.Zero && SafeCall.ProbeObject(mm.Pointer))
                    {
                        var board = mm.TacticalBoard;
                        if ((object)board != null && SafeCall.ProbeObject(board.Pointer))
                        {
                            var pmc = board.pawnMoveCalculation;
                            if ((object)pmc != null && SafeCall.ProbeObject(pmc.Pointer))
                            {
                                var movePawnUnit = pmc.movePawnUnit;
                                if ((object)movePawnUnit != null && movePawnUnit.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(movePawnUnit.Pointer))
                                {
                                    puPtr = movePawnUnit.Pointer;
                                    name = ReadPawnUnitName(new PawnUnit(puPtr));
                                }
                            }
                        }
                    }
                }
                catch { }

                // Fallback: cursor unit or selected unit
                if (puPtr == IntPtr.Zero)
                {
                    var cursor = GetFreshCursor();
                    if ((object)cursor != null && SafeCall.ProbeObject(cursor.Pointer))
                        puPtr = SafeCall.GetPawnHereSafe(cursor.Pointer);
                    if (puPtr == IntPtr.Zero)
                        puPtr = GetSelectedPawnUnitPtr();

                    if (puPtr == IntPtr.Zero || !SafeCall.ProbeObject(puPtr))
                    {
                        ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                        return;
                    }

                    name = ReadPawnUnitName(new PawnUnit(puPtr));
                }

                // Get robot via PawnUnit → PawnData → BelongRobot
                Il2CppCom.BBStudio.SRTeam.Data.Robot robot = null;
                try
                {
                    var pawnUnit = new PawnUnit(puPtr);
                    var pawn = pawnUnit.PawnData;
                    if ((object)pawn != null)
                        robot = pawn.BelongRobot;
                }
                catch { }

                if ((object)robot == null)
                {
                    ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
                    return;
                }

                // Use robot.calcData.Weapons → List<Weapon> for weapon data.
                // Each Weapon has BaseData (onUseMoveAfter, onHidden) and calcData (RangeMin, RangeMax).
                // This avoids the broken IsUsable() which depends on game-internal state.
                Il2CppSystem.Collections.Generic.List<Il2CppCom.BBStudio.SRTeam.Data.Weapon> weaponList = null;
                try
                {
                    var calcData = robot.calcData;
                    if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(calcData.Pointer))
                        weaponList = calcData.Weapons;
                }
                catch { }

                if ((object)weaponList == null || weaponList.Pointer == IntPtr.Zero
                    || !SafeCall.ProbeObject(weaponList.Pointer) || weaponList.Count == 0)
                {
                    string unitName0 = !string.IsNullOrEmpty(name) ? name + ", " : "";
                    ScreenReaderOutput.Say(unitName0 + Loc.Get("range_no_weapons"));
                    return;
                }

                // Track ranges: all weapons and post-move weapons separately
                int allMin = int.MaxValue, allMax = int.MinValue;
                int postMoveMin = int.MaxValue, postMoveMax = int.MinValue;
                int allCount = 0, postMoveCount = 0;

                int limit = weaponList.Count;
                for (int i = 0; i < limit; i++)
                {
                    try
                    {
                        var weapon = weaponList[i];
                        if ((object)weapon == null || weapon.Pointer == IntPtr.Zero
                            || !SafeCall.ProbeObject(weapon.Pointer)) continue;

                        // Read base data for hidden/post-move flags
                        Il2CppCom.BBStudio.SRTeam.Data.WeaponBaseData baseData = null;
                        try { baseData = weapon.BaseData; } catch { }
                        if ((object)baseData == null || baseData.Pointer == IntPtr.Zero) continue;
                        if (!SafeCall.ProbeObject(baseData.Pointer)) continue;

                        // Skip hidden weapons
                        try { if (baseData.onHidden) continue; } catch { }

                        // Read calculated range from Weapon.calcData
                        int rMin = -1, rMax = -1;
                        try
                        {
                            var wCalc = weapon.calcData;
                            if ((object)wCalc != null && wCalc.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(wCalc.Pointer))
                            {
                                rMin = wCalc.RangeMin;
                                rMax = wCalc.RangeMax;
                            }
                        }
                        catch { }

                        // Fallback: read base range
                        if (rMin <= 0 && rMax <= 0)
                        {
                            try
                            {
                                rMin = baseData.rangeMin;
                                rMax = baseData.rangeMax;
                            }
                            catch { }
                        }

                        if (rMin <= 0 && rMax <= 0) continue;

                        // All weapons range
                        allCount++;
                        if (rMin < allMin) allMin = rMin;
                        if (rMax > allMax) allMax = rMax;

                        // Post-move weapons (onUseMoveAfter = true means CAN use after move)
                        try
                        {
                            if (baseData.onUseMoveAfter)
                            {
                                postMoveCount++;
                                if (rMin < postMoveMin) postMoveMin = rMin;
                                if (rMax > postMoveMax) postMoveMax = rMax;
                            }
                        }
                        catch { }
                    }
                    catch { }
                }

                // Build announcement
                string unitNamePrefix = !string.IsNullOrEmpty(name) ? name + ", " : "";

                if (allCount == 0)
                {
                    ScreenReaderOutput.Say(unitNamePrefix + Loc.Get("range_no_weapons"));
                    return;
                }

                string allRangeStr = allMin == allMax
                    ? allMin.ToString()
                    : $"{allMin}-{allMax}";

                // If post-move range differs from all range, show both
                string announcement;
                if (postMoveCount > 0 && postMoveCount < allCount)
                {
                    string pmRangeStr = postMoveMin == postMoveMax
                        ? postMoveMin.ToString()
                        : $"{postMoveMin}-{postMoveMax}";

                    if (pmRangeStr != allRangeStr)
                        announcement = $"{unitNamePrefix}{Loc.Get("stat_attack_range")} {allRangeStr}, {Loc.Get("range_after_move")} {pmRangeStr}";
                    else
                        announcement = $"{unitNamePrefix}{Loc.Get("stat_attack_range")} {allRangeStr}";
                }
                else
                {
                    announcement = $"{unitNamePrefix}{Loc.Get("stat_attack_range")} {allRangeStr}";
                }

                ScreenReaderOutput.Say(announcement);
                DebugHelper.Write($"AttackRange: {announcement} (all:{allCount} pm:{postMoveCount})");
            }
            catch
            {
                ScreenReaderOutput.Say(Loc.Get("unit_no_unit"));
            }
        }

        /// <summary>
        /// Get the currently selected PawnUnit pointer via PawnController.SelectedPawnInfo.
        /// Used as fallback when no unit is at cursor (e.g. during/after movement).
        /// </summary>
        private IntPtr GetSelectedPawnUnitPtr()
        {
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return IntPtr.Zero;
                if (!SafeCall.ProbeObject(mm.Pointer)) return IntPtr.Zero;
                if (!SafeCall.TacticalFieldsAvailable || !SafeCall.PawnFieldsAvailable)
                    return IntPtr.Zero;

                IntPtr pcPtr = SafeCall.ReadPawnControllerPtrSafe(mm.Pointer);
                if (pcPtr == IntPtr.Zero || !SafeCall.ProbeObject(pcPtr)) return IntPtr.Zero;

                IntPtr infoPtr = SafeCall.ReadSelectedPawnInfoPtrSafe(pcPtr);
                if (infoPtr == IntPtr.Zero || !SafeCall.ProbeObject(infoPtr)) return IntPtr.Zero;

                IntPtr puPtr = SafeCall.ReadPawnUnitPtrSafe(infoPtr);
                return puPtr;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Read unit info at cursor with ally/enemy side detection.
        /// Returns (unitInfo, isPlayerSide) where isPlayerSide is null for empty tile.
        /// </summary>
        private (string info, bool? isPlayerSide) ReadUnitAtCursorWithSide(FloatingCursor cursor)
        {
            if ((object)cursor == null) return (null, null);

            try
            {
                if (!SafeCall.ProbeObject(cursor.Pointer)) return (null, null);

                // CRITICAL: Only use SafeCall protected path
                if (!SafeCall.IsAvailable) return (null, null);

                IntPtr pawnPtr = SafeCall.GetPawnHereSafe(cursor.Pointer);
                if (pawnPtr == IntPtr.Zero) return (null, null);
                if (!SafeCall.ProbeObject(pawnPtr)) return (null, null);

                var pawnUnit = new PawnUnit(pawnPtr);

                // Determine ally/enemy side via SafeCall (same pattern as UnitDistanceHandler)
                bool? side = null;
                if (SafeCall.PawnMethodsAvailable)
                {
                    var (ok, val) = SafeCall.ReadIsPlayerSideSafe(pawnPtr);
                    if (ok) side = val;
                }

                // Wrap all IL2CPP property access in try-catch (can catch NullRef, not AV)
                try
                {
                    var pawn = pawnUnit.PawnData;
                    if ((object)pawn == null) return (null, side);

                    var robot = pawn.BelongRobot;
                    if ((object)robot == null) return (null, side);

                    string nameInfo = ReadPawnUnitName(pawnUnit);
                    if (string.IsNullOrEmpty(nameInfo)) return (null, side);

                    // Read current HP/EN + Morale
                    try
                    {
                        var lastData = robot.lastData;
                    if ((object)lastData != null && lastData.Pointer != IntPtr.Zero
                        && SafeCall.ProbeObject(lastData.Pointer))
                    {
                        int hpNow = lastData.HPNow;
                        int enNow = lastData.ENNow;
                        int hpMax = 0, enMax = 0;
                        try
                        {
                            var calcData = robot.calcData;
                            if ((object)calcData != null && calcData.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(calcData.Pointer))
                            {
                                var param = calcData.Parameters;
                                if ((object)param != null && param.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(param.Pointer))
                                {
                                    hpMax = param.hp;
                                    enMax = param.en;
                                }
                            }
                        }
                        catch { }

                        // Read morale from pilot
                        string moraleStr = "";
                        try
                        {
                            var pilot = robot.MainPilot;
                            if ((object)pilot != null && pilot.Pointer != IntPtr.Zero
                                && SafeCall.ProbeObject(pilot.Pointer))
                            {
                                var pld = pilot.GetLastData();
                                if ((object)pld != null && pld.Pointer != IntPtr.Zero
                                    && SafeCall.ProbeObject(pld.Pointer))
                                    moraleStr = string.Format(" {0}:{1}", Loc.Get("stat_morale"), pld.MoraleNow);
                            }
                        }
                        catch { }

                        if (hpMax > 0)
                            return (string.Format("{0} HP:{1}/{2} EN:{3}/{4}{5}",
                                nameInfo, hpNow, hpMax, enNow, enMax, moraleStr), side);
                        else
                            return (string.Format("{0} HP:{1} EN:{2}{3}",
                                nameInfo, hpNow, enNow, moraleStr), side);
                    }
                    }
                    catch { }

                    return (nameInfo, side);
                }
                catch
                {
                    return (null, side);
                }
            }
            catch
            {
                return (null, null);
            }
        }
    }
}
