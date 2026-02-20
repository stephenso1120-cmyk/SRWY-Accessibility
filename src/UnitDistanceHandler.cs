using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.Map;
using Il2CppCom.BBStudio.SRTeam.Data;

namespace SRWYAccess
{
    /// <summary>
    /// Allows cycling through enemy/ally units sorted by distance from cursor.
    /// Keys: ;/' for enemies, .// for allies, \ to re-announce current selection.
    /// Distance = Manhattan distance (|dx| + |dy|), matching SRW weapon/movement range.
    /// Direction = 8 cardinal/intercardinal bearings from cursor to unit.
    ///
    /// On key press, builds a sorted unit list from TacticalBoard.GetAllPawns(),
    /// cycles the index, and returns an announcement string with name + direction + distance + HP.
    /// Indices reset when cursor moves to a new tile (nearest-first on next press).
    ///
    /// SAFETY: All IL2CPP calls wrapped in try/catch. Uses SafeCall for coord reads
    /// on PawnUnit (MapUnit.currentCoord offset valid for subclasses).
    /// </summary>
    public class UnitDistanceHandler
    {
        private enum LastListType { Enemy, Ally, Unacted, Acted }

        private int _enemyIndex = -1;
        private int _allyIndex = -1;
        private int _unactedIndex = -1;
        private int _actedIndex = -1;
        private LastListType _lastList = LastListType.Enemy; // tracks which list \ key re-announces
        private Vector2Int _lastCursorCoord = new Vector2Int(-999, -999);

        // Cached lists to avoid rebuilding on same-tile repeated presses
        private List<UnitInfo> _cachedEnemies;
        private List<UnitInfo> _cachedAllies;
        private List<UnitInfo> _cachedUnacted;
        private List<UnitInfo> _cachedActed;
        private Vector2Int _cachedEnemyCoord = new Vector2Int(-999, -999);
        private Vector2Int _cachedAllyCoord = new Vector2Int(-999, -999);
        private Vector2Int _cachedUnactedCoord = new Vector2Int(-999, -999);
        private Vector2Int _cachedActedCoord = new Vector2Int(-999, -999);

        // Track last-announced unit pointers for RepeatLast unit tracking
        private IntPtr _lastEnemyPtr = IntPtr.Zero;
        private IntPtr _lastAllyPtr = IntPtr.Zero;
        private IntPtr _lastUnactedPtr = IntPtr.Zero;
        private IntPtr _lastActedPtr = IntPtr.Zero;

        private struct UnitInfo
        {
            public IntPtr Pointer;
            public string Name;
            public int Distance;
            public int Dx; // unit.x - cursor.x
            public int Dy; // unit.y - cursor.y
            public int HpNow;
            public int HpMax;
            public bool HasHp;
        }

        public void ReleaseHandler()
        {
            _enemyIndex = -1;
            _allyIndex = -1;
            _unactedIndex = -1;
            _actedIndex = -1;
            _lastList = LastListType.Enemy;
            _lastCursorCoord = new Vector2Int(-999, -999);
            _cachedEnemies = null;
            _cachedAllies = null;
            _cachedUnacted = null;
            _cachedActed = null;
            _cachedEnemyCoord = new Vector2Int(-999, -999);
            _cachedAllyCoord = new Vector2Int(-999, -999);
            _cachedUnactedCoord = new Vector2Int(-999, -999);
            _cachedActedCoord = new Vector2Int(-999, -999);
            _lastEnemyPtr = IntPtr.Zero;
            _lastAllyPtr = IntPtr.Zero;
            _lastUnactedPtr = IntPtr.Zero;
            _lastActedPtr = IntPtr.Zero;
        }

        /// <summary>
        /// Cycle to next/previous enemy unit and return announcement string.
        /// direction: +1 = next (farther), -1 = previous (closer)
        /// </summary>
        public string CycleEnemy(int direction, Vector2Int cursorCoord)
        {
            CheckCursorMoved(cursorCoord);

            // Build/refresh enemy list if needed
            if (_cachedEnemies == null || cursorCoord.x != _cachedEnemyCoord.x || cursorCoord.y != _cachedEnemyCoord.y)
            {
                _cachedEnemies = BuildUnitList(cursorCoord, isEnemy: true);
                _cachedEnemyCoord = cursorCoord;
            }

            if (_cachedEnemies == null || _cachedEnemies.Count == 0)
                return Loc.Get("dist_no_enemies");

            // First press: always start at nearest (index 0)
            if (_enemyIndex < 0)
                _enemyIndex = 0;
            else
            {
                _enemyIndex += direction;
                if (_enemyIndex >= _cachedEnemies.Count) _enemyIndex = 0;
                if (_enemyIndex < 0) _enemyIndex = _cachedEnemies.Count - 1;
            }

            _lastList = LastListType.Enemy;
            _lastEnemyPtr = _cachedEnemies[_enemyIndex].Pointer;
            return FormatAnnouncement(_cachedEnemies[_enemyIndex], _enemyIndex, _cachedEnemies.Count, isEnemy: true);
        }

        /// <summary>
        /// Cycle to next/previous ally unit and return announcement string.
        /// direction: +1 = next (farther), -1 = previous (closer)
        /// </summary>
        public string CycleAlly(int direction, Vector2Int cursorCoord)
        {
            CheckCursorMoved(cursorCoord);

            // Build/refresh ally list if needed
            if (_cachedAllies == null || cursorCoord.x != _cachedAllyCoord.x || cursorCoord.y != _cachedAllyCoord.y)
            {
                _cachedAllies = BuildUnitList(cursorCoord, isEnemy: false);
                _cachedAllyCoord = cursorCoord;
            }

            if (_cachedAllies == null || _cachedAllies.Count == 0)
                return Loc.Get("dist_no_allies");

            // First press: always start at nearest (index 0)
            if (_allyIndex < 0)
                _allyIndex = 0;
            else
            {
                _allyIndex += direction;
                if (_allyIndex >= _cachedAllies.Count) _allyIndex = 0;
                if (_allyIndex < 0) _allyIndex = _cachedAllies.Count - 1;
            }

            _lastList = LastListType.Ally;
            _lastAllyPtr = _cachedAllies[_allyIndex].Pointer;
            return FormatAnnouncement(_cachedAllies[_allyIndex], _allyIndex, _cachedAllies.Count, isEnemy: false);
        }

        /// <summary>
        /// Cycle to next/previous unacted player unit and return announcement string.
        /// direction: +1 = next (farther), -1 = previous (closer)
        /// </summary>
        public string CycleUnacted(int direction, Vector2Int cursorCoord)
        {
            CheckCursorMoved(cursorCoord);

            if (_cachedUnacted == null || cursorCoord.x != _cachedUnactedCoord.x || cursorCoord.y != _cachedUnactedCoord.y)
            {
                _cachedUnacted = BuildActionFilteredList(cursorCoord, canAct: true);
                _cachedUnactedCoord = cursorCoord;
            }

            if (_cachedUnacted == null || _cachedUnacted.Count == 0)
                return Loc.Get("dist_no_unacted");

            if (_unactedIndex < 0)
                _unactedIndex = 0;
            else
            {
                _unactedIndex += direction;
                if (_unactedIndex >= _cachedUnacted.Count) _unactedIndex = 0;
                if (_unactedIndex < 0) _unactedIndex = _cachedUnacted.Count - 1;
            }

            _lastList = LastListType.Unacted;
            _lastUnactedPtr = _cachedUnacted[_unactedIndex].Pointer;
            return FormatActionAnnouncement(_cachedUnacted[_unactedIndex], _unactedIndex, _cachedUnacted.Count, isUnacted: true);
        }

        /// <summary>
        /// Cycle to next/previous acted player unit and return announcement string.
        /// direction: +1 = next (farther), -1 = previous (closer)
        /// </summary>
        public string CycleActed(int direction, Vector2Int cursorCoord)
        {
            CheckCursorMoved(cursorCoord);

            if (_cachedActed == null || cursorCoord.x != _cachedActedCoord.x || cursorCoord.y != _cachedActedCoord.y)
            {
                _cachedActed = BuildActionFilteredList(cursorCoord, canAct: false);
                _cachedActedCoord = cursorCoord;
            }

            if (_cachedActed == null || _cachedActed.Count == 0)
                return Loc.Get("dist_no_acted");

            if (_actedIndex < 0)
                _actedIndex = 0;
            else
            {
                _actedIndex += direction;
                if (_actedIndex >= _cachedActed.Count) _actedIndex = 0;
                if (_actedIndex < 0) _actedIndex = _cachedActed.Count - 1;
            }

            _lastList = LastListType.Acted;
            _lastActedPtr = _cachedActed[_actedIndex].Pointer;
            return FormatActionAnnouncement(_cachedActed[_actedIndex], _actedIndex, _cachedActed.Count, isUnacted: false);
        }

        /// <summary>
        /// Re-announce the currently selected unit.
        /// If cursor hasn't moved, re-announce from cache (no rebuild).
        /// If cursor has moved, rebuild the relevant list, find the same unit
        /// by pointer, and announce with updated distance + direction.
        /// If no unit was previously selected, announces the nearest enemy.
        /// </summary>
        public string RepeatLast(Vector2Int cursorCoord)
        {
            bool cursorMoved = cursorCoord.x != _lastCursorCoord.x
                || cursorCoord.y != _lastCursorCoord.y;

            if (cursorMoved)
            {
                _lastCursorCoord = cursorCoord;
                _enemyIndex = -1;
                _allyIndex = -1;
                _unactedIndex = -1;
                _actedIndex = -1;
                _cachedEnemies = null;
                _cachedAllies = null;
                _cachedUnacted = null;
                _cachedActed = null;
            }

            switch (_lastList)
            {
                case LastListType.Enemy:
                    return RepeatFromList(
                        ref _cachedEnemies, ref _cachedEnemyCoord, ref _enemyIndex, ref _lastEnemyPtr,
                        cursorCoord, cursorMoved,
                        () => BuildUnitList(cursorCoord, isEnemy: true),
                        "dist_no_enemies",
                        (u, i, t) => FormatAnnouncement(u, i, t, isEnemy: true));

                case LastListType.Ally:
                    return RepeatFromList(
                        ref _cachedAllies, ref _cachedAllyCoord, ref _allyIndex, ref _lastAllyPtr,
                        cursorCoord, cursorMoved,
                        () => BuildUnitList(cursorCoord, isEnemy: false),
                        "dist_no_allies",
                        (u, i, t) => FormatAnnouncement(u, i, t, isEnemy: false));

                case LastListType.Unacted:
                    return RepeatFromList(
                        ref _cachedUnacted, ref _cachedUnactedCoord, ref _unactedIndex, ref _lastUnactedPtr,
                        cursorCoord, cursorMoved,
                        () => BuildActionFilteredList(cursorCoord, canAct: true),
                        "dist_no_unacted",
                        (u, i, t) => FormatActionAnnouncement(u, i, t, isUnacted: true));

                case LastListType.Acted:
                    return RepeatFromList(
                        ref _cachedActed, ref _cachedActedCoord, ref _actedIndex, ref _lastActedPtr,
                        cursorCoord, cursorMoved,
                        () => BuildActionFilteredList(cursorCoord, canAct: false),
                        "dist_no_acted",
                        (u, i, t) => FormatActionAnnouncement(u, i, t, isUnacted: false));

                default:
                    return Loc.Get("dist_no_enemies");
            }
        }

        private string RepeatFromList(
            ref List<UnitInfo> cache, ref Vector2Int cacheCoord,
            ref int index, ref IntPtr lastPtr,
            Vector2Int cursorCoord, bool cursorMoved,
            Func<List<UnitInfo>> buildList, string emptyKey,
            Func<UnitInfo, int, int, string> format)
        {
            if (cache == null || cursorCoord.x != cacheCoord.x || cursorCoord.y != cacheCoord.y)
            {
                cache = buildList();
                cacheCoord = cursorCoord;
            }

            if (cache == null || cache.Count == 0)
                return Loc.Get(emptyKey);

            if (cursorMoved && lastPtr != IntPtr.Zero)
            {
                int found = FindUnitByPointer(cache, lastPtr);
                index = found >= 0 ? found : 0;
            }
            else if (index < 0 || index >= cache.Count)
            {
                index = 0;
            }

            lastPtr = cache[index].Pointer;
            return format(cache[index], index, cache.Count);
        }

        private void CheckCursorMoved(Vector2Int cursorCoord)
        {
            if (cursorCoord.x != _lastCursorCoord.x || cursorCoord.y != _lastCursorCoord.y)
            {
                _enemyIndex = -1;
                _allyIndex = -1;
                _unactedIndex = -1;
                _actedIndex = -1;
                _lastCursorCoord = cursorCoord;
                _cachedEnemies = null;
                _cachedAllies = null;
                _cachedUnacted = null;
                _cachedActed = null;
            }
        }

        /// <summary>
        /// Find a unit in the list by its native pointer.
        /// Returns the index if found, -1 if not found.
        /// </summary>
        private static int FindUnitByPointer(List<UnitInfo> list, IntPtr ptr)
        {
            if (list == null || ptr == IntPtr.Zero) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pointer == ptr) return i;
            }
            return -1;
        }

        /// <summary>
        /// Get compass direction string from cursor to unit.
        /// Uses dx/dy to determine one of 8 cardinal/intercardinal directions,
        /// or "same position" if dx==0 && dy==0.
        /// </summary>
        private static string GetDirection(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return Loc.Get("dir_same");

            // Determine primary direction based on angle
            // In tactical grid: +X = right (east), +Y = down (south)
            bool hasX = dx != 0;
            bool hasY = dy != 0;

            if (hasX && hasY)
            {
                // Intercardinal
                if (dx > 0 && dy < 0) return Loc.Get("dir_ne");
                if (dx > 0 && dy > 0) return Loc.Get("dir_se");
                if (dx < 0 && dy < 0) return Loc.Get("dir_nw");
                return Loc.Get("dir_sw"); // dx < 0 && dy > 0
            }
            else if (hasX)
            {
                return dx > 0 ? Loc.Get("dir_e") : Loc.Get("dir_w");
            }
            else
            {
                return dy < 0 ? Loc.Get("dir_n") : Loc.Get("dir_s");
            }
        }

        private string FormatAnnouncement(UnitInfo unit, int index, int total, bool isEnemy)
        {
            string direction = GetDirection(unit.Dx, unit.Dy);

            string key = isEnemy
                ? (unit.HasHp ? "dist_enemy" : "dist_enemy_simple")
                : (unit.HasHp ? "dist_ally" : "dist_ally_simple");

            if (unit.HasHp)
                return Loc.Get(key, index + 1, total, unit.Name, direction, unit.Distance, unit.HpNow, unit.HpMax);
            else
                return Loc.Get(key, index + 1, total, unit.Name, direction, unit.Distance);
        }

        /// <summary>
        /// Build a sorted list of units (enemies or allies) with distance from cursor.
        /// </summary>
        private List<UnitInfo> BuildUnitList(Vector2Int cursorCoord, bool isEnemy)
        {
            var result = new List<UnitInfo>();

            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return result;
                if (!SafeCall.ProbeObject(mm.Pointer)) return result;

                TacticalBoard board;
                try
                {
                    board = mm.TacticalBoard;
                }
                catch { return result; }
                if ((object)board == null) return result;
                if (!SafeCall.ProbeObject(board.Pointer)) return result;

                Il2CppSystem.Collections.Generic.List<PawnUnit> allPawns;
                try
                {
                    allPawns = board.GetAllPawns();
                }
                catch { return result; }
                if ((object)allPawns == null) return result;

                int count;
                try { count = allPawns.Count; }
                catch { return result; }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var pawn = allPawns[i];
                        if ((object)pawn == null) continue;
                        if (pawn.Pointer == IntPtr.Zero) continue;
                        if (!SafeCall.ProbeObject(pawn.Pointer)) continue;

                        // Filter by side (SEH-protected: computed property, no backing field)
                        bool playerSide;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsPlayerSideSafe(pawn.Pointer);
                            if (!ok) continue; // AV - pawn freed
                            playerSide = val;
                        }
                        else
                        {
                            try { playerSide = pawn.IsPlayerSide; }
                            catch { continue; }
                        }

                        // isEnemy=true means we want non-player units
                        if (isEnemy == playerSide) continue;

                        // Filter dead units (SEH-protected: computed property, no backing field)
                        bool alive;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsAliveSafe(pawn.Pointer);
                            if (!ok) continue; // AV - pawn freed
                            alive = val;
                        }
                        else
                        {
                            try { alive = pawn.IsAlive; }
                            catch { continue; }
                        }
                        if (!alive) continue;

                        // Read unit position
                        int ux, uy;
                        if (SafeCall.TacticalFieldsAvailable)
                        {
                            var (ok, cx, cy) = SafeCall.ReadCurrentCoordSafe(pawn.Pointer);
                            if (!ok) continue;
                            ux = cx;
                            uy = cy;
                        }
                        else
                        {
                            try
                            {
                                var coord = pawn.CurrentCoord;
                                ux = coord.x;
                                uy = coord.y;
                            }
                            catch { continue; }
                        }

                        // Calculate Manhattan distance and direction vector
                        int dx = ux - cursorCoord.x;
                        int dy = uy - cursorCoord.y;
                        int distance = Math.Abs(dx) + Math.Abs(dy);

                        // Read unit name
                        string name = ReadPawnUnitName(pawn);
                        if (string.IsNullOrEmpty(name)) continue;

                        // Read HP (protect PawnData with SafeCall)
                        int hpNow = 0, hpMax = 0;
                        bool hasHp = false;
                        try
                        {
                            Pawn pawnData = null;
                            if (SafeCall.PawnMethodsAvailable)
                            {
                                IntPtr pdPtr = SafeCall.ReadPawnDataSafe(pawn.Pointer);
                                if (pdPtr != IntPtr.Zero && SafeCall.ProbeObject(pdPtr))
                                    pawnData = new Pawn(pdPtr);
                            }
                            else
                            {
                                // SAFETY: If SafeCall unavailable, skip this unit.
                                // DO NOT access pawn.PawnData - try/catch cannot catch AV.
                                continue;
                            }
                            if ((object)pawnData != null)
                            {
                                var robot = pawnData.BelongRobot;
                                if ((object)robot != null)
                                {
                                    var lastData = robot.lastData;
                                    if ((object)lastData != null)
                                    {
                                        hpNow = lastData.HPNow;
                                        hasHp = true;
                                        try
                                        {
                                            var calcData = robot.calcData;
                                            if ((object)calcData != null)
                                            {
                                                var param = calcData.Parameters;
                                                if ((object)param != null)
                                                    hpMax = param.hp;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }

                        result.Add(new UnitInfo
                        {
                            Pointer = pawn.Pointer,
                            Name = name,
                            Distance = distance,
                            Dx = dx,
                            Dy = dy,
                            HpNow = hpNow,
                            HpMax = hpMax,
                            HasHp = hasHp
                        });
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"UnitDistance: BuildUnitList error: {ex.GetType().Name}: {ex.Message}");
            }

            // Sort by distance (nearest first), then by name for stable ordering
            result.Sort((a, b) =>
            {
                int cmp = a.Distance.CompareTo(b.Distance);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            return result;
        }

        private string FormatActionAnnouncement(UnitInfo unit, int index, int total, bool isUnacted)
        {
            string direction = GetDirection(unit.Dx, unit.Dy);

            string key = isUnacted
                ? (unit.HasHp ? "dist_unacted" : "dist_unacted_simple")
                : (unit.HasHp ? "dist_acted" : "dist_acted_simple");

            if (unit.HasHp)
                return Loc.Get(key, index + 1, total, unit.Name, direction, unit.Distance, unit.HpNow, unit.HpMax);
            else
                return Loc.Get(key, index + 1, total, unit.Name, direction, unit.Distance);
        }

        /// <summary>
        /// Build a sorted list of player-side units filtered by action status.
        /// canAct=true: units that can still act (IsPossibleToAction)
        /// canAct=false: units that have already acted
        /// </summary>
        private List<UnitInfo> BuildActionFilteredList(Vector2Int cursorCoord, bool canAct)
        {
            var result = new List<UnitInfo>();

            try
            {
                var mm = MapManager.Instance;
                if ((object)mm == null || mm.Pointer == IntPtr.Zero) return result;
                if (!SafeCall.ProbeObject(mm.Pointer)) return result;

                TacticalBoard board;
                try { board = mm.TacticalBoard; }
                catch { return result; }
                if ((object)board == null) return result;
                if (!SafeCall.ProbeObject(board.Pointer)) return result;

                Il2CppSystem.Collections.Generic.List<PawnUnit> allPawns;
                try { allPawns = board.GetAllPawns(); }
                catch { return result; }
                if ((object)allPawns == null) return result;

                int count;
                try { count = allPawns.Count; }
                catch { return result; }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var pawn = allPawns[i];
                        if ((object)pawn == null) continue;
                        if (pawn.Pointer == IntPtr.Zero) continue;
                        if (!SafeCall.ProbeObject(pawn.Pointer)) continue;

                        // Only player-side units
                        bool playerSide;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsPlayerSideSafe(pawn.Pointer);
                            if (!ok) continue;
                            playerSide = val;
                        }
                        else
                        {
                            try { playerSide = pawn.IsPlayerSide; }
                            catch { continue; }
                        }
                        if (!playerSide) continue;

                        // Only alive units
                        bool alive;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            var (ok, val) = SafeCall.ReadIsAliveSafe(pawn.Pointer);
                            if (!ok) continue;
                            alive = val;
                        }
                        else
                        {
                            try { alive = pawn.IsAlive; }
                            catch { continue; }
                        }
                        if (!alive) continue;

                        // Check action status via PawnData.IsPossibleToAction()
                        Pawn pawnData = null;
                        if (SafeCall.PawnMethodsAvailable)
                        {
                            IntPtr pdPtr = SafeCall.ReadPawnDataSafe(pawn.Pointer);
                            if (pdPtr != IntPtr.Zero && SafeCall.ProbeObject(pdPtr))
                                pawnData = new Pawn(pdPtr);
                        }
                        else
                        {
                            continue; // SAFETY: skip if SafeCall unavailable
                        }
                        if ((object)pawnData == null) continue;

                        bool possibleToAction;
                        try { possibleToAction = pawnData.IsPossibleToAction(); }
                        catch { continue; }

                        // Filter: canAct=true wants actionable, canAct=false wants already-acted
                        if (possibleToAction != canAct) continue;

                        // Read unit position
                        int ux, uy;
                        if (SafeCall.TacticalFieldsAvailable)
                        {
                            var (ok, cx, cy) = SafeCall.ReadCurrentCoordSafe(pawn.Pointer);
                            if (!ok) continue;
                            ux = cx;
                            uy = cy;
                        }
                        else
                        {
                            try
                            {
                                var coord = pawn.CurrentCoord;
                                ux = coord.x;
                                uy = coord.y;
                            }
                            catch { continue; }
                        }

                        int dx = ux - cursorCoord.x;
                        int dy = uy - cursorCoord.y;
                        int distance = Math.Abs(dx) + Math.Abs(dy);

                        string name = ReadPawnUnitName(pawn);
                        if (string.IsNullOrEmpty(name)) continue;

                        // Read HP
                        int hpNow = 0, hpMax = 0;
                        bool hasHp = false;
                        try
                        {
                            var robot = pawnData.BelongRobot;
                            if ((object)robot != null)
                            {
                                var lastData = robot.lastData;
                                if ((object)lastData != null)
                                {
                                    hpNow = lastData.HPNow;
                                    hasHp = true;
                                    try
                                    {
                                        var calcData = robot.calcData;
                                        if ((object)calcData != null)
                                        {
                                            var param = calcData.Parameters;
                                            if ((object)param != null)
                                                hpMax = param.hp;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        result.Add(new UnitInfo
                        {
                            Pointer = pawn.Pointer,
                            Name = name,
                            Distance = distance,
                            Dx = dx,
                            Dy = dy,
                            HpNow = hpNow,
                            HpMax = hpMax,
                            HasHp = hasHp
                        });
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"UnitDistance: BuildActionFilteredList error: {ex.GetType().Name}: {ex.Message}");
            }

            result.Sort((a, b) =>
            {
                int cmp = a.Distance.CompareTo(b.Distance);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            return result;
        }

        /// <summary>
        /// Extract pilot/robot name from a PawnUnit.
        /// Same pattern as TacticalMapHandler.ReadPawnUnitName.
        /// </summary>
        private static string ReadPawnUnitName(PawnUnit pawnUnit)
        {
            if (!SafeCall.ProbeObject(pawnUnit.Pointer)) return null;

            Pawn pawn;
            if (SafeCall.PawnMethodsAvailable)
            {
                IntPtr pawnPtr = SafeCall.ReadPawnDataSafe(pawnUnit.Pointer);
                if (pawnPtr == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(pawnPtr)) return null;
                pawn = new Pawn(pawnPtr);
            }
            else
            {
                // SAFETY: If SafeCall unavailable, skip functionality.
                // DO NOT access pawnUnit.PawnData - try/catch cannot catch AV.
                return null;
            }

            Robot robot;
            try { robot = pawn.BelongRobot; }
            catch { return null; }
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
    }
}
