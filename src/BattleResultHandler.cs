using System;
using Il2CppCom.BBStudio.SRTeam.Data;
using Il2CppCom.BBStudio.SRTeam.Manager;
using Il2CppCom.BBStudio.SRTeam.State.GameState;

namespace SRWYAccess
{
    /// <summary>
    /// Reads battle result data from TacticalPartState.actionResultValue.
    ///
    /// Uses GameManager.Instance (static singleton) → gameStateHandler
    /// → currentState → TryCast&lt;TacticalPartState&gt;() → actionResultValue.
    ///
    /// This avoids FindObjectOfType entirely, eliminating the AV crash that
    /// occurred when cached MapUnit refs went stale during scene transitions.
    ///
    /// SAFETY: All IL2CPP null checks use (object)x != null.
    /// Only called when resultPending is true (after BATTLE_SCENE → NONE).
    /// </summary>
    public class BattleResultHandler
    {
        // Track last announced values to detect changes
        private int _lastGainExp = -1;
        private string _lastPilotId = "";

        /// <summary>
        /// Last full announcement for R key repeat.
        /// Persists across ReleaseHandler calls.
        /// </summary>
        public string LastAnnouncement { get; private set; } = "";

        public void ReleaseHandler()
        {
            _lastGainExp = -1;
            _lastPilotId = "";
        }

        public void Update()
        {
            try
            {
                UpdateInner();
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleResult: error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateInner()
        {
            // Access TacticalPartState via GameManager singleton chain.
            // GameManager.Instance is a static singleton (safe from background thread).
            // No FindObjectOfType needed - eliminates AV crash risk entirely.
            TacticalPartState tps = null;
            try
            {
                var gm = GameManager.Instance;
                if ((object)gm == null || gm.Pointer == IntPtr.Zero) return;

                var gsh = gm.gameStateHandler;
                if ((object)gsh == null || gsh.Pointer == IntPtr.Zero) return;

                var stateBase = gsh.currentState;
                if ((object)stateBase == null || stateBase.Pointer == IntPtr.Zero) return;

                tps = stateBase.TryCast<TacticalPartState>();
            }
            catch
            {
                return;
            }

            if ((object)tps == null) return;

            // Validate pointer before field access
            try
            {
                if (tps.Pointer == IntPtr.Zero) return;
            }
            catch { return; }

            // Read actionResultValue
            TacticalPartState.ActionResultUIValue resultVal;
            try
            {
                resultVal = tps.actionResultValue;
            }
            catch
            {
                return;
            }

            if ((object)resultVal == null) return;

            // Check for new result data
            int gainExp, gainScore, gainCapital;
            string pilotId;
            try
            {
                gainExp = resultVal.GainExp;
                gainScore = resultVal.GainScore;
                gainCapital = resultVal.GainCapital;
                pilotId = resultVal.PilotReferenceId ?? "";
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleResult: read fields error: {ex.GetType().Name}");
                return;
            }

            // Announce when result data changes (new battle result)
            if (gainExp > 0 && (gainExp != _lastGainExp || pilotId != _lastPilotId))
            {
                _lastGainExp = gainExp;
                _lastPilotId = pilotId;

                // Read level info
                int beforeLevel = 0;
                bool isLevelUp = false;
                try
                {
                    beforeLevel = resultVal.BeforeLevel;
                    isLevelUp = resultVal.IsUppedLevel;
                }
                catch { }

                // Resolve pilot reference ID to display name
                string pilotName = ResolvePilotName(pilotId);

                string announcement = Loc.Get("result_battle",
                    pilotName, beforeLevel.ToString(),
                    gainExp.ToString(), gainScore.ToString(), gainCapital.ToString());

                LastAnnouncement = announcement;
                ScreenReaderOutput.Say(announcement);
                DebugHelper.Write($"BattleResult: {announcement}");

                // Check level up
                if (isLevelUp)
                {
                    CheckLevelUp(resultVal);
                }
            }
        }

        /// <summary>
        /// Collect last battle result info for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            if (!string.IsNullOrWhiteSpace(LastAnnouncement))
                items.Add(LastAnnouncement);
        }

        /// <summary>
        /// Resolve a pilot reference ID to a display name via PRPManager.
        /// Falls back to the raw reference ID if resolution fails.
        /// </summary>
        private static string ResolvePilotName(string referenceId)
        {
            if (string.IsNullOrEmpty(referenceId)) return referenceId;

            try
            {
                var prpMgr = PRPManager.Instance;
                if ((object)prpMgr == null) return referenceId;

                var pilot = prpMgr.GetPilot(referenceId);
                if ((object)pilot == null) return referenceId;

                // CRITICAL: Use SafeCall to read pilot name - direct GetName() causes
                // uncatchable AccessViolationException when pilot object is destroyed
                string name = null;
                if (SafeCall.NameMethodsAvailable && pilot.Pointer != IntPtr.Zero)
                {
                    name = SafeCall.ReadPilotNameSafe(pilot.Pointer);
                }
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleResult: ResolvePilotName({referenceId}) failed: {ex.GetType().Name}");
            }

            return referenceId;
        }

        private void CheckLevelUp(TacticalPartState.ActionResultUIValue resultVal)
        {
            try
            {
                var lvUpList = resultVal.levelUpUIValues;
                if ((object)lvUpList == null) return;

                int count = lvUpList.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var lvUp = lvUpList[i];
                        if ((object)lvUp == null) continue;

                        string lvPilotId = lvUp.PilotReferenceId ?? "";
                        int beforeLv = lvUp.BeforeLevel;
                        int nowLv = lvUp.NowLevel;

                        string lvPilotName = ResolvePilotName(lvPilotId);
                        string announcement = Loc.Get("result_level_up",
                            lvPilotName, beforeLv.ToString(), nowLv.ToString());

                        LastAnnouncement = announcement;
                        ScreenReaderOutput.Say(announcement);
                        DebugHelper.Write($"BattleResult LvUp: {announcement}");
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
