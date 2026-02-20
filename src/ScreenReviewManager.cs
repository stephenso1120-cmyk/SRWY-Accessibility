using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppTMPro;
using Il2CppInterop.Runtime;

namespace SRWYAccess
{
    /// <summary>
    /// Universal screen review coordinator.
    /// R key reads all visible info at once, [/] keys browse items one by one.
    /// Collects info from all active handlers based on current game state.
    ///
    /// Items are collected on-demand (on key press), not every poll cycle.
    /// Item list is cleared on state changes so the next key press re-collects.
    /// </summary>
    public class ScreenReviewManager
    {
        private readonly GenericMenuReader _menuReader;
        private readonly DialogHandler _dialogHandler;
        private readonly TutorialHandler _tutorialHandler;
        private readonly AdventureDialogueHandler _adventureHandler;
        private readonly BattleSubtitleHandler _battleSubtitleHandler;
        private readonly BattleResultHandler _battleResultHandler;
        private readonly TacticalMapHandler _tacticalMapHandler;

        private readonly List<string> _items = new List<string>();
        private int _browseIndex = -1;
        private IntPtr _lastReviewHandlerPtr = IntPtr.Zero;
        private InputManager.InputMode _lastReviewMode;

        public ScreenReviewManager(
            GenericMenuReader menuReader,
            DialogHandler dialogHandler,
            TutorialHandler tutorialHandler,
            AdventureDialogueHandler adventureHandler,
            BattleSubtitleHandler battleSubtitleHandler,
            BattleResultHandler battleResultHandler,
            TacticalMapHandler tacticalMapHandler)
        {
            _menuReader = menuReader;
            _dialogHandler = dialogHandler;
            _tutorialHandler = tutorialHandler;
            _adventureHandler = adventureHandler;
            _battleSubtitleHandler = battleSubtitleHandler;
            _battleResultHandler = battleResultHandler;
            _tacticalMapHandler = tacticalMapHandler;
        }

        /// <summary>
        /// Clear collected items. Call on state change / stabilizing.
        /// </summary>
        public void Clear()
        {
            _items.Clear();
            _browseIndex = -1;
            _lastReviewHandlerPtr = IntPtr.Zero;
        }

        /// <summary>
        /// Check if the game context changed (handler or mode) since last review.
        /// If so, clear cached items so [/] shows fresh data.
        /// </summary>
        private void CheckContextChanged(InputManager.InputMode currentMode, bool isBattle)
        {
            IntPtr currentHandlerPtr = _menuReader?.CurrentHandlerPtr ?? IntPtr.Zero;
            if (currentHandlerPtr != _lastReviewHandlerPtr || currentMode != _lastReviewMode)
            {
                if (_items.Count > 0)
                {
                    _items.Clear();
                    _browseIndex = -1;
                }
                _lastReviewHandlerPtr = currentHandlerPtr;
                _lastReviewMode = currentMode;
            }
        }

        /// <summary>
        /// R key: refresh items and read all at once.
        /// </summary>
        public void ReadAll(InputManager.InputMode currentMode, bool isBattle, bool isAdventure, bool postTactical = false)
        {
            RefreshItems(currentMode, isBattle, isAdventure, postTactical);

            if (_items.Count == 0)
            {
                ScreenReaderOutput.Say(Loc.Get("review_no_info"));
                return;
            }

            // Join all items and read as one announcement
            string all = string.Join(Loc.Get("review_separator"), _items);
            ScreenReaderOutput.Say(all);
            DebugHelper.Write($"ScreenReview: ReadAll {_items.Count} items");
        }

        /// <summary>
        /// ] key: browse to next item.
        /// </summary>
        public void BrowseNext(InputManager.InputMode currentMode, bool isBattle, bool isAdventure, bool postTactical = false)
        {
            CheckContextChanged(currentMode, isBattle);
            if (_items.Count == 0)
                RefreshItems(currentMode, isBattle, isAdventure, postTactical);

            if (_items.Count == 0)
            {
                ScreenReaderOutput.Say(Loc.Get("review_no_info"));
                return;
            }

            _browseIndex = (_browseIndex + 1) % _items.Count;
            AnnounceCurrentItem();
        }

        /// <summary>
        /// [ key: browse to previous item.
        /// </summary>
        public void BrowsePrev(InputManager.InputMode currentMode, bool isBattle, bool isAdventure, bool postTactical = false)
        {
            CheckContextChanged(currentMode, isBattle);
            if (_items.Count == 0)
                RefreshItems(currentMode, isBattle, isAdventure, postTactical);

            if (_items.Count == 0)
            {
                ScreenReaderOutput.Say(Loc.Get("review_no_info"));
                return;
            }

            _browseIndex = (_browseIndex - 1 + _items.Count) % _items.Count;
            AnnounceCurrentItem();
        }

        private void AnnounceCurrentItem()
        {
            string item = _items[_browseIndex];
            ScreenReaderOutput.Say(item);
        }

        private void RefreshItems(InputManager.InputMode currentMode, bool isBattle, bool isAdventure, bool postTactical = false)
        {
            _items.Clear();
            _browseIndex = -1;
            _lastReviewHandlerPtr = _menuReader?.CurrentHandlerPtr ?? IntPtr.Zero;
            _lastReviewMode = currentMode;

            try
            {
                if (isBattle)
                {
                    // Battle scene: unit info + subtitles
                    _battleSubtitleHandler?.CollectReviewItems(_items);
                }
                else if (isAdventure)
                {
                    // Adventure: dialogue + subtitles
                    _adventureHandler?.CollectReviewItems(_items);
                    // Tutorial overlay (can appear during adventure)
                    _tutorialHandler?.CollectReviewItems(_items);
                    // Dialog overlay
                    _dialogHandler?.CollectReviewItems(_items);
                }
                else
                {
                    // Tactical, strategy, menus, postTitle

                    // Map cursor (tactical map navigation)
                    if (currentMode == InputManager.InputMode.TACTICAL_PART)
                        _tacticalMapHandler?.CollectReviewItems(_items);

                    // Menu items (any UIHandlerBase)
                    _menuReader?.CollectReviewItems(_items);

                    // Tactical overlay info (mission objectives, situation)
                    // These are MonoBehaviours (not UIHandlerBase) so GenericMenuReader can't detect them.
                    // Search in tactical modes AND postTactical NONE mode (mission/situation screens
                    // opened from map menu run under NONE with postTactical=true).
                    // Safe: only called on user key press, not automatic polling.
                    if (GameStateTracker.IsTacticalMode(currentMode) || postTactical)
                        CollectTacticalOverlayItems(_items);

                    // Intermission menu (MonoBehaviour, not UIHandlerBase)
                    // Only visible during strategy/intermission. Safe: on key press only.
                    CollectIntermissionItems(_items);

                    // Tutorial overlay
                    _tutorialHandler?.CollectReviewItems(_items);

                    // Dialog overlay
                    _dialogHandler?.CollectReviewItems(_items);

                    // Battle result (if pending/recent)
                    _battleResultHandler?.CollectReviewItems(_items);
                }
            }
            catch (System.Exception ex)
            {
                DebugHelper.Write($"ScreenReview: RefreshItems error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ===== Tactical Overlay MonoBehaviours =====

        /// <summary>
        /// Find and read tactical overlay MonoBehaviours that are NOT UIHandlerBase:
        /// - TacticalPartMissionUIHandler: mission objectives (win/lose conditions)
        /// - TacticalSituationInfo: battlefield situation (unit counts, kills, credits)
        ///
        /// These only exist in the tactical battle scene and are only visible when
        /// the player opens the Mission or Situation screen from the map menu.
        /// FindObjectOfType is safe here because it only runs on user key press.
        /// </summary>
        private void CollectTacticalOverlayItems(List<string> items)
        {
            // Mission objectives
            try
            {
                var missionUI = UnityEngine.Object.FindObjectOfType<TacticalPartMissionUIHandler>();
                if ((object)missionUI != null && missionUI.Pointer != IntPtr.Zero)
                {
                    var go = missionUI.gameObject;
                    if ((object)go != null && go.activeInHierarchy)
                        CollectMissionItems(items, missionUI);
                }
            }
            catch { }

            // Tactical situation
            try
            {
                var sitInfo = UnityEngine.Object.FindObjectOfType<TacticalSituationInfo>();
                if ((object)sitInfo != null && sitInfo.Pointer != IntPtr.Zero)
                {
                    var go = sitInfo.gameObject;
                    if ((object)go != null && go.activeInHierarchy)
                        CollectSituationItems(items, sitInfo);
                }
            }
            catch { }

            // Phase info (wave number, enemy count)
            try
            {
                var phaseInfo = UnityEngine.Object.FindObjectOfType<TacticalPartPhaseInfo>();
                if ((object)phaseInfo != null && phaseInfo.Pointer != IntPtr.Zero)
                {
                    var go = phaseInfo.gameObject;
                    if ((object)go != null && go.activeInHierarchy)
                        CollectPhaseInfoItems(items, phaseInfo);
                }
            }
            catch { }
        }

        /// <summary>
        /// Read mission objectives: stage title, win conditions, lose conditions.
        /// </summary>
        private static void CollectMissionItems(List<string> items, TacticalPartMissionUIHandler missionUI)
        {
            try
            {
                // Mission stage title
                string title = ReadTmpSafe(missionUI.missionStageTitle);
                if (!string.IsNullOrWhiteSpace(title))
                    items.Add(Loc.Get("mission_title_label") + " " + title);

                // Win conditions
                try
                {
                    var winList = missionUI.missionStageWinList;
                    if (winList != null && winList.Length > 0)
                    {
                        bool hasWinItems = false;
                        for (int i = 0; i < winList.Length; i++)
                        {
                            try
                            {
                                string t = ReadTmpSafe(winList[i]);
                                if (!string.IsNullOrWhiteSpace(t))
                                {
                                    if (!hasWinItems)
                                    {
                                        items.Add(Loc.Get("mission_win"));
                                        hasWinItems = true;
                                    }
                                    items.Add(t);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Lose conditions
                try
                {
                    var loseList = missionUI.missionStageLoseList;
                    if (loseList != null && loseList.Length > 0)
                    {
                        bool hasLoseItems = false;
                        for (int i = 0; i < loseList.Length; i++)
                        {
                            try
                            {
                                string t = ReadTmpSafe(loseList[i]);
                                if (!string.IsNullOrWhiteSpace(t))
                                {
                                    if (!hasLoseItems)
                                    {
                                        items.Add(Loc.Get("mission_lose"));
                                        hasLoseItems = true;
                                    }
                                    items.Add(t);
                                }
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
        /// Read tactical situation: unit counts, kills, credits, items gained.
        /// </summary>
        private static void CollectSituationItems(List<string> items, TacticalSituationInfo sitInfo)
        {
            try
            {
                // Player/enemy unit counts
                string playerUnits = ReadTmpSafe(sitInfo.sortiePlayerNumText);
                string enemyUnits = ReadTmpSafe(sitInfo.sortieEnemyNumText);
                if (!string.IsNullOrWhiteSpace(playerUnits))
                    items.Add(Loc.Get("situation_player_units") + " " + playerUnits);
                if (!string.IsNullOrWhiteSpace(enemyUnits))
                    items.Add(Loc.Get("situation_enemy_units") + " " + enemyUnits);

                // Kill counts
                string playerKills = ReadTmpSafe(sitInfo.PlayerSideShotDownNumText);
                string enemyKills = ReadTmpSafe(sitInfo.EnemySideShotDownNumText);
                if (!string.IsNullOrWhiteSpace(playerKills))
                    items.Add(Loc.Get("situation_player_kills") + " " + playerKills);
                if (!string.IsNullOrWhiteSpace(enemyKills))
                    items.Add(Loc.Get("situation_enemy_kills") + " " + enemyKills);

                // Credits gained
                string credits = ReadTmpSafe(sitInfo.gainCreditText);
                if (!string.IsNullOrWhiteSpace(credits))
                    items.Add(Loc.Get("situation_credits") + " " + credits);

                // Power parts gained (only if visible)
                try
                {
                    var partsGo = sitInfo.gainPowerPartsGameObject;
                    if ((object)partsGo != null && partsGo.activeInHierarchy)
                    {
                        string parts = ReadTmpSafe(sitInfo.gainPowerPartsNum);
                        if (!string.IsNullOrWhiteSpace(parts))
                            items.Add(Loc.Get("situation_parts") + " " + parts);
                    }
                }
                catch { }

                // Skill programs gained (only if visible)
                try
                {
                    var skillGo = sitInfo.gainSkillProgramGameObject;
                    if ((object)skillGo != null && skillGo.activeInHierarchy)
                    {
                        string skills = ReadTmpSafe(sitInfo.gainSkillProgramNum);
                        if (!string.IsNullOrWhiteSpace(skills))
                            items.Add(Loc.Get("situation_skills") + " " + skills);
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read phase info: wave number and remaining enemy count.
        /// TacticalPartPhaseInfo is a MonoBehaviour shown during tactical battles.
        /// </summary>
        private static void CollectPhaseInfoItems(List<string> items, TacticalPartPhaseInfo phaseInfo)
        {
            try
            {
                // Wave number
                string wave = ReadTmpSafe(phaseInfo.waveNum);
                string maxWave = ReadTmpSafe(phaseInfo.maxWaveNum);
                if (!string.IsNullOrWhiteSpace(wave) || !string.IsNullOrWhiteSpace(maxWave))
                    items.Add(Loc.Get("phase_wave", wave ?? "?", maxWave ?? "?"));

                // Remaining enemies
                string enemies = ReadTmpSafe(phaseInfo.numOfEnemyText);
                if (!string.IsNullOrWhiteSpace(enemies))
                    items.Add(Loc.Get("phase_enemies", enemies));
            }
            catch { }
        }

        // ===== Intermission MonoBehaviour =====

        /// <summary>
        /// Find and read IntermissionUIHandler if active.
        /// This is a MonoBehaviour (not UIHandlerBase) with commandList and selectedCommandIdx.
        /// Only called on R key press for screen review, not during polling.
        /// </summary>
        private void CollectIntermissionItems(List<string> items)
        {
            try
            {
                var intermission = UnityEngine.Object.FindObjectOfType<IntermissionUIHandler>();
                if ((object)intermission == null || intermission.Pointer == IntPtr.Zero) return;

                var go = intermission.gameObject;
                if ((object)go == null || !go.activeInHierarchy) return;

                items.Add(Loc.Get("screen_intermissionuihandler"));

                var cmdList = intermission.commandList;
                if ((object)cmdList == null) return;

                int selectedIdx = -1;
                try { selectedIdx = intermission.selectedCommandIdx; } catch { }

                for (int i = 0; i < cmdList.Count; i++)
                {
                    try
                    {
                        var cmdGo = cmdList[i];
                        if ((object)cmdGo == null || !cmdGo.activeInHierarchy) continue;

                        // Read TMP text from command GameObject children
                        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                        try
                        {
                            tmps = cmdGo.GetComponentsInChildren<TextMeshProUGUI>(false);
                        }
                        catch (Exception ex)
                        {
                            DebugHelper.Write($"ScreenReview: IntermissionUIHandler GetComponentsInChildren error: {ex.GetType().Name}");
                            continue;
                        }
                        if (tmps == null || tmps.Count == 0) continue;

                        string bestText = null;
                        foreach (var tmp in tmps)
                        {
                            if ((object)tmp == null) continue;

                            // CRITICAL: Use SafeCall to read tmp.text
                            string t = null;
                            if (SafeCall.TmpTextMethodAvailable)
                            {
                                IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                                if (il2cppStrPtr != IntPtr.Zero)
                                {
                                    try
                                    {
                                        t = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                                        t = TextUtils.CleanRichText(t);
                                    }
                                    catch { }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(t) && (bestText == null || t.Length > bestText.Length))
                                bestText = t;
                        }

                        if (!string.IsNullOrWhiteSpace(bestText))
                        {
                            string prefix = (i == selectedIdx) ? "(*) " : "";
                            items.Add(prefix + bestText);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Safely read text from a TextMeshProUGUI, returning null on any error.
        /// </summary>
        private static string ReadTmpSafe(TextMeshProUGUI tmp)
        {
            if ((object)tmp == null) return null;

            // CRITICAL: Use SafeCall to read tmp.text - direct access causes
            // uncatchable AV when TMP object is destroyed during scene transitions
            if (SafeCall.TmpTextMethodAvailable)
            {
                IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                if (il2cppStrPtr != IntPtr.Zero)
                {
                    try
                    {
                        string t = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                        t = TextUtils.CleanRichText(t);
                        return string.IsNullOrWhiteSpace(t) ? null : t;
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
