using System;
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

            // Read current coord
            Vector2Int coord;
            try
            {
                coord = cursor.CurrentCoord;
            }
            catch
            {
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
        /// </summary>
        private FloatingCursor GetFreshCursor()
        {
            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return null;

                var cursor = mm.Cursor;
                if ((object)cursor == null || cursor.Pointer == IntPtr.Zero) return null;

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

                var pc = mm.PawnController;
                if ((object)pc == null || pc.Pointer == IntPtr.Zero) return;

                var info = pc.SelectedPawnInfo;
                if ((object)info != null)
                {
                    var pawnUnit = info.PawnUnit;
                    if ((object)pawnUnit != null && pawnUnit.Pointer != IntPtr.Zero)
                        currentPawnPtr = pawnUnit.Pointer;
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

                var pc = mm.PawnController;
                if ((object)pc == null || pc.Pointer == IntPtr.Zero) return null;

                var info = pc.SelectedPawnInfo;
                if ((object)info == null) return null;

                var pawnUnit = info.PawnUnit;
                if ((object)pawnUnit == null || pawnUnit.Pointer == IntPtr.Zero) return null;

                return ReadPawnUnitName(pawnUnit);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract pilot/robot name from a PawnUnit.
        /// </summary>
        private static string ReadPawnUnitName(PawnUnit pawnUnit)
        {
            var pawn = pawnUnit.PawnData;
            if ((object)pawn == null) return null;

            var robot = pawn.BelongRobot;
            if ((object)robot == null) return null;

            string robotName = robot.GetName();
            string pilotName = null;

            try
            {
                var pilot = robot.MainPilot;
                if ((object)pilot != null)
                    pilotName = pilot.GetName();
            }
            catch { }

            if (!string.IsNullOrEmpty(pilotName) && !string.IsNullOrEmpty(robotName))
                return pilotName + " / " + robotName;
            return robotName ?? pilotName;
        }

        private void AnnouncePosition(Vector2Int coord, FloatingCursor cursor)
        {
            string unitInfo = ReadUnitAtCursor(cursor);
            _lastUnitInfo = unitInfo;

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
                PawnUnit pawnUnit = cursor.GetPawnHere();
                if ((object)pawnUnit == null || pawnUnit.Pointer == IntPtr.Zero) return;

                var pawn = pawnUnit.PawnData;
                if ((object)pawn == null) return;

                var robot = pawn.BelongRobot;
                if ((object)robot == null) return;

                // Extract pilot once, reuse across all blocks
                Il2CppCom.BBStudio.SRTeam.Data.Pilot pilot = null;
                try { pilot = robot.MainPilot; } catch { }

                // Current HP/EN + Morale
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null)
                    {
                        int hpNow = lastData.HPNow;
                        int enNow = lastData.ENNow;
                        int hpMax = 0, enMax = 0;
                        try
                        {
                            var calcData = robot.calcData;
                            if ((object)calcData != null)
                            {
                                var param = calcData.Parameters;
                                if ((object)param != null)
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
                            if ((object)pilot != null)
                            {
                                var pld = pilot.GetLastData();
                                if ((object)pld != null)
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

                // Robot stats + Size
                try
                {
                    var calcData = robot.calcData;
                    if ((object)calcData != null)
                    {
                        var param = calcData.Parameters;
                        if ((object)param != null)
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

                            items.Add(string.Format("{0} {1}, {2} {3}, {4} {5}{6}",
                                Loc.Get("stat_armor"), param.armor,
                                Loc.Get("stat_mobility"), param.mobility,
                                Loc.Get("stat_move"), calcData.MovePower,
                                sizeStr));
                        }
                    }
                }
                catch { }

                // Terrain aptitude
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null)
                    {
                        var adapt = lastData.LandAdaptation;
                        if ((object)adapt != null)
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
                    if ((object)pilot != null)
                    {
                        int level = 0;
                        try { var cd = pilot.calcData; if ((object)cd != null) level = cd.Level; } catch { }
                        int sp = pilot.GetSP();
                        int maxSp = pilot.GetMaxSP();
                        string pilotName = pilot.GetName() ?? "";
                        items.Add(string.Format("{0} Lv.{1} SP:{2}/{3}", pilotName, level, sp, maxSp));
                    }
                }
                catch { }

                // Pilot combat stats
                try
                {
                    if ((object)pilot != null)
                    {
                        var cd = pilot.calcData;
                        if ((object)cd != null)
                        {
                            var ps = cd.Parameters;
                            if ((object)ps != null)
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
                    if ((object)pilot != null)
                    {
                        var pld = pilot.GetLastData();
                        if ((object)pld != null)
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

                // Weapons (up to 5) with expanded info
                try
                {
                    var weapons = robot.weapons;
                    if ((object)weapons != null && weapons.Count > 0)
                    {
                        int limit = Math.Min(weapons.Count, 5);
                        for (int i = 0; i < limit; i++)
                        {
                            try
                            {
                                var w = weapons[i];
                                if ((object)w == null) continue;
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

                                items.Add(line);
                            }
                            catch { }
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
        /// Read unit info at cursor position via FloatingCursor.GetPawnHere().
        /// Returns pilot/robot name with HP/EN/Morale stats.
        /// Uses fresh cursor ref passed from caller (never cached).
        /// </summary>
        private string ReadUnitAtCursor(FloatingCursor cursor)
        {
            if ((object)cursor == null) return null;

            try
            {
                PawnUnit pawnUnit = cursor.GetPawnHere();
                if ((object)pawnUnit == null || pawnUnit.Pointer == IntPtr.Zero) return null;

                var pawn = pawnUnit.PawnData;
                if ((object)pawn == null) return null;

                var robot = pawn.BelongRobot;
                if ((object)robot == null) return null;

                string nameInfo = ReadPawnUnitName(pawnUnit);
                if (string.IsNullOrEmpty(nameInfo)) return null;

                // Read current HP/EN + Morale
                try
                {
                    var lastData = robot.lastData;
                    if ((object)lastData != null)
                    {
                        int hpNow = lastData.HPNow;
                        int enNow = lastData.ENNow;
                        int hpMax = 0, enMax = 0;
                        try
                        {
                            var calcData = robot.calcData;
                            if ((object)calcData != null)
                            {
                                var param = calcData.Parameters;
                                if ((object)param != null)
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
                            if ((object)pilot != null)
                            {
                                var pld = pilot.GetLastData();
                                if ((object)pld != null)
                                    moraleStr = string.Format(" {0}:{1}", Loc.Get("stat_morale"), pld.MoraleNow);
                            }
                        }
                        catch { }

                        if (hpMax > 0)
                            return string.Format("{0} HP:{1}/{2} EN:{3}/{4}{5}",
                                nameInfo, hpNow, hpMax, enNow, enMax, moraleStr);
                        else
                            return string.Format("{0} HP:{1} EN:{2}{3}",
                                nameInfo, hpNow, enNow, moraleStr);
                    }
                }
                catch { }

                return nameInfo;
            }
            catch
            {
                return null;
            }
        }
    }
}
