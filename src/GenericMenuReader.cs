using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppCom.BBStudio.SRTeam.UIs.SelectParts;
using Il2CppCom.BBStudio.SRTeam.UIs.WeaponList;
using Il2CppCom.BBStudio.SRTeam.Manager;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

namespace SRWYAccess
{
    /// <summary>
    /// Universal menu reader for ANY UIHandlerBase subclass.
    /// Finds the active handler by matching controlBehaviour with
    /// InputManager's current input behaviour, then reads the current
    /// item text on-demand when cursor changes.
    ///
    /// Text reading strategies (in priority order):
    ///   1. Type-specific: direct field access for known handler types
    ///   2. List-based: ListHandlerBase item text extraction
    ///   3. Generic: GetComponentsInChildren&lt;Button&gt; at cursor index
    ///
    /// Does NOT track total option count. Focuses purely on reading
    /// what's at the current cursor position.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class GenericMenuReader
    {
        private UIHandlerBase _activeHandler;
        private string _activeHandlerType;
        private IntPtr _lastHandlerPtr = IntPtr.Zero;
        private int _lastCursorIndex = -1;

        // Cached InputManager behaviour pointer for the active handler.
        // Used for fast-path validation WITHOUT accessing handler IL2CPP fields.
        // Avoids AccessViolationException on freed native objects (uncatchable in .NET 6).
        private IntPtr _cachedBehaviourPtr = IntPtr.Zero;

        // ListHandlerBase: for list-based menus (SaveLoad, PilotList, Shop, etc.)
        private ListHandlerBase _listHandler;

        // Type-specific button count: used only for grid navigation suppression
        // and menu mode change detection (e.g. unit commands vs map menu).
        private int _typeSpecificCount = -1;

        // Cooldown for mode change re-announcement (prevents spam when
        // buttonList.Count flickers between values)
        private int _modeChangeCooldown;

        // Flag: set when a new handler is found, cleared after first cursor read
        private bool _newHandlerJustFound;

        private int _missCount;
        private int _faultCount;

        // Stale cursor detection: when cursor hasn't changed for many polls,
        // reduce IL2CPP access frequency. This minimizes AV exposure during
        // the dangerous window when a UI handler is being destroyed by Unity
        // but InputManager's behaviour pointer hasn't changed yet.
        private int _stalePollCount;

        // Handler types already covered by dedicated handlers
        private static readonly HashSet<string> _skipTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DialogSystem",
            "SimpleBattleHandler" // transient battle animation handler, freed during transitions → AV crash
        };

        // Info screen types: read all visible TMP text as fallback
        private static readonly HashSet<string> _infoScreenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StatusUIHandler",
            "TacticalPartStatusUIHandler",
            "CustomRobotUIHandler",
            "UpdateUIHandler",
            "RankUpAnimationHandler",
            "ConversionUIHandler",
            "PilotTrainingUIHandler",
            "HandoverUIHandler",
            "TransferUIHandler"
        };

        // Structured review types: read specific TMP fields for result/detail screens
        private static readonly HashSet<string> _structuredReviewTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ActionResultUIHandler",
            "LvUpUIHandler",
            "AceBonusUIHandler",
            "CharacterSelectionUIHandler",
            "BattleCheckMenuHandler",
            "BonusUIHandler",
            "CreditWindowUIHandler",
            "AssistLinkManager",
            "SurvivalMissionResultUIHandler",
            "MissionChartUIHandler"
        };

        // Diagnostic flag for command button dump
        private bool _cmdDiagDumped;

        // Spirit cursor tracking: TacticalPartSpiritUIHandler doesn't update
        // currentCursorIndex. Track CurrentSpiritBtnHandler pointer instead.
        private IntPtr _lastSpiritBtnPtr = IntPtr.Zero;
        private int _lastSpiritPilotIndex = -1;

        // OthersCommand cursor tracking: TacticalOthersCommandUIHandler uses
        // CurrentOtherCommandBtnHandler (same SpiritButtonHandler pattern as spirit).
        private IntPtr _lastOthersCmdBtnPtr = IntPtr.Zero;

        // BattleCheckMenuHandler cursor tracking: doesn't update currentCursorIndex.
        // Track curBattleCheckMenuButtonType changes instead.
        private int _lastBattleCheckBtnType = -1;

        // AssistLinkManager cursor tracking: uses CursorInfo.SelectNo instead of
        // currentCursorIndex. Track selection changes for grid-based navigation.
        private int _lastAssistSelectNo = -1;

        // AssistLink data cache: GetAssistLinkData returns mutable/reused object.
        // Second call returns empty fields. Cache all fields during cursor
        // navigation (ReadAssistLinkItemText) for use during screen review.
        private string _cachedALId;
        private string _cachedALPersonName;
        private string _cachedALCommandName;
        private string _cachedALCmdStr;
        private string _cachedALLvCmdStr;
        private string _cachedALPassiveStr;
        private string _cachedALLvPassiveStr;
        private string _cachedALTarget;
        private string _cachedALDuration;

        // Track extra command buttons that cursor doesn't navigate to
        // (e.g. LandformAction in TacticallPartCommandUIHandler)
        private string _lastExtraButtonText;

        // Track TacticallPartCommandUIHandler mode (map menu vs unit commands)
        private bool _isMapMenu;
        private int _lastCommandType = -1; // ShowCommandType enum value

        public bool HasHandler => (object)_activeHandler != null;
        public IntPtr CurrentHandlerPtr => _lastHandlerPtr;

        public void ReleaseHandler()
        {
            _activeHandler = null;
            _activeHandlerType = null;
            _lastHandlerPtr = IntPtr.Zero;
            _cachedBehaviourPtr = IntPtr.Zero;
            _lastCursorIndex = -1;
            _missCount = 0;
            _faultCount = 0;
            _listHandler = null;
            _typeSpecificCount = -1;
            _modeChangeCooldown = 0;
            _newHandlerJustFound = false;
            _cmdDiagDumped = false;
            _lastExtraButtonText = null;
            _isMapMenu = false;
            _lastCommandType = -1;
            _lastSpiritBtnPtr = IntPtr.Zero;
            _lastSpiritPilotIndex = -1;
            _lastOthersCmdBtnPtr = IntPtr.Zero;
            _lastBattleCheckBtnType = -1;
            _lastAssistSelectNo = -1;
            _stalePollCount = 0;
            _cachedALId = null;
            _cachedALPersonName = null;
            _cachedALCommandName = null;
            _cachedALCmdStr = null;
            _cachedALLvCmdStr = null;
            _cachedALPassiveStr = null;
            _cachedALLvPassiveStr = null;
            _cachedALTarget = null;
            _cachedALDuration = null;
        }

        /// <summary>
        /// Poll this reader.
        /// canSearch: if true, may call FindObjectsOfType this cycle.
        /// Returns true if a handler was lost (signals scene transition).
        /// </summary>
        public bool Update(bool canSearch)
        {
            if (_faultCount >= ModConfig.MenuMaxFaults) return false;

            try
            {
                return UpdateInner(canSearch);
            }
            catch (Exception ex)
            {
                _faultCount++;
                bool hadHandler = (object)_activeHandler != null;
                int savedFaults = _faultCount;
                ReleaseHandler();
                _faultCount = savedFaults;
                DebugHelper.Write($"GenericMenu: FAULT #{_faultCount}: {ex.GetType().Name}: {ex.Message}");
                if (_faultCount >= ModConfig.MenuMaxFaults)
                    DebugHelper.Write("GenericMenu: Too many faults, disabling.");
                return hadHandler;
            }
        }

        private bool UpdateInner(bool canSearch)
        {
            if (canSearch)
            {
                bool found = FindActiveHandler();

                if (!found && (object)_activeHandler != null)
                {
                    _missCount++;
                    if (_missCount >= ModConfig.MenuMissThreshold)
                    {
                        _missCount = 0;
                        string lostType = _activeHandlerType ?? "unknown";
                        ReleaseHandler();
                        DebugHelper.Write($"GenericMenu: Handler lost ({lostType})");
                        return true;
                    }
                    return false;
                }
                else if (found)
                {
                    _missCount = 0;
                }
            }

            if ((object)_activeHandler == null) return false;
            if (_missCount > 0) return false;

            // Validate handler is still alive by checking InputManager's
            // current behaviour pointer. This is a safe static field read
            // that doesn't touch the handler's native object.
            if (!IsHandlerStillActive())
            {
                // Behaviour pointer changed - handler is stale.
                // DON'T call FindObjectsOfType here: canSearch gating exists to
                // ensure FindObjectsOfType only runs when the scene is stable.
                // Calling it outside canSearch (during transitions) causes AV crashes.
                //
                // Instead, just skip this cycle. Keep the stale handler reference
                // (we won't access its IL2CPP fields). On the next canSearch=true
                // cycle, FindActiveHandler at the top of UpdateInner will detect
                // the behaviour pointer mismatch and safely do a full scan.
                return false;
            }

            // Stale cursor detection: when cursor hasn't changed for many polls,
            // reduce IL2CPP access frequency to minimize AV exposure.
            // The handler's native object can be destroyed by Unity before
            // InputManager's behaviour pointer changes (race condition).
            // By skipping most accesses when idle, we dramatically reduce the
            // chance of hitting this window.
            if (_stalePollCount >= ModConfig.MenuStalePollLimit)
            {
                if (_stalePollCount < 10000) _stalePollCount++; // Cap to prevent overflow
                if (_stalePollCount % ModConfig.MenuStaleProbeInterval != 0)
                    return false; // Skip this cycle
            }

            // Special: TacticalPartSpiritUIHandler doesn't use currentCursorIndex.
            // Track CurrentSpiritBtnHandler pointer changes instead.
            if (_activeHandlerType == "TacticalPartSpiritUIHandler")
                return UpdateSpiritHandler();

            // Special: TacticalOthersCommandUIHandler uses same SpiritButtonHandler
            // pattern. Track CurrentOtherCommandBtnHandler pointer changes.
            if (_activeHandlerType == "TacticalOthersCommandUIHandler")
                return UpdateOthersCommandHandler();

            // Special: BattleCheckMenuHandler doesn't use currentCursorIndex.
            // Track curBattleCheckMenuButtonType changes instead.
            if (_activeHandlerType == "BattleCheckMenuHandler")
                return UpdateBattleCheckHandler();

            // Special: AssistLinkManager uses CursorInfo.SelectNo for grid navigation,
            // not currentCursorIndex from UIHandlerBase.
            if (_activeHandlerType == "AssistLinkManager")
                return UpdateAssistLinkHandler();

            // Special: PartsEquipUIHandler doesn't update currentCursorIndex.
            // Track equipmentUIHandler.currentIndex instead.
            if (_activeHandlerType == "PartsEquipUIHandler")
                return UpdatePartsEquipHandler();

            // Read cursor index (IL2CPP access - but we just validated above)
            int currentIndex;
            try
            {
                currentIndex = _activeHandler.currentCursorIndex;
            }
            catch
            {
                ReleaseHandler();
                return true;
            }

            // Detect button list changes and command mode.
            // SAFETY: these require multiple IL2CPP accesses (TryCast + field reads).
            // Only run on cursor change or first detection to minimize AV exposure
            // during the window when Unity destroys native objects before
            // InputManager's behaviour pointer updates.
            bool cursorMoved = (currentIndex != _lastCursorIndex);
            if (cursorMoved || _newHandlerJustFound)
            {
                int typeCount = GetTypeSpecificCount(_activeHandlerType);
                if (typeCount > 0)
                {
                    if (_typeSpecificCount > 0 && typeCount != _typeSpecificCount)
                    {
                        // Only re-announce if cooldown expired (prevents flicker spam)
                        if (_modeChangeCooldown <= 0)
                        {
                            _lastCursorIndex = -1; // force re-announce
                            cursorMoved = true; // ensure item is announced
                            _modeChangeCooldown = ModConfig.MenuModeChangeCooldown;
                            DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] mode change {_typeSpecificCount} -> {typeCount}");
                        }
                    }
                    _typeSpecificCount = typeCount;
                }

                // Detect map menu vs unit command mode for TacticallPartCommandUIHandler
                if (_activeHandlerType == "TacticallPartCommandUIHandler")
                {
                    try
                    {
                        var cmdHandler = _activeHandler.TryCast<TacticallPartCommandUIHandler>();
                        if ((object)cmdHandler != null)
                        {
                            int cmdType = (int)cmdHandler.currentCommandType;
                            _isMapMenu = (cmdType == 1); // ShowCommandType.Main = 1
                            if (cmdType != _lastCommandType)
                            {
                                _lastCommandType = cmdType;
                                if (_isMapMenu)
                                {
                                    ScreenReaderOutput.Say(Loc.Get("map_menu"));
                                    _lastCursorIndex = -1; // force re-announce first item
                                    cursorMoved = true;
                                    _cmdDiagDumped = false; // re-dump for new mode
                                }
                                DebugHelper.Write($"GenericMenu: CommandType changed to {cmdType} (isMapMenu={_isMapMenu})");
                            }
                        }
                    }
                    catch { }
                }
            }
            if (_modeChangeCooldown > 0) _modeChangeCooldown--;

            // Handle newly found handler
            bool justFound = false;
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                justFound = true;
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] initial cursor={currentIndex}");

                // Announce screen name for known handler types
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);

                // If cursor starts at -1 for a type-specific handler, announce first item
                if (currentIndex < 0 && _typeSpecificCount > 0)
                {
                    AnnounceItem(0);
                    _lastCursorIndex = 0;
                }
            }

            if (cursorMoved)
            {
                _lastCursorIndex = currentIndex;
                _stalePollCount = 0; // Cursor changed → reset stale counter
                AnnounceItem(currentIndex);
            }
            else
            {
                _stalePollCount++;
            }

            // Announce extra buttons AFTER cursor (queued, so it doesn't get interrupted)
            if (justFound)
            {
                AnnounceExtraButtons();
            }

            // Monitor extra button text changes (e.g. LandformAction toggling 陸地↔空中).
            // SAFETY: Rate-limited to every 3rd cycle to reduce IL2CPP exposure.
            // MonitorExtraButtons does deep IL2CPP access (handler → buttonList →
            // button → TMP text). Double-check IsHandlerStillActive() to narrow
            // the TOCTOU window before accessing these objects.
            if (_stalePollCount % 3 == 0 && IsHandlerStillActive())
                MonitorExtraButtons();

            // Reset fault counter on successful poll
            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Special handler for BattleCheckMenuHandler.
        /// This handler doesn't update currentCursorIndex when navigating
        /// between button options (BATTLE/SELECT/SPIRIT/etc.).
        /// Track curBattleCheckMenuButtonType changes instead.
        /// </summary>
        private bool UpdateBattleCheckHandler()
        {
            try
            {
                var handler = _activeHandler.TryCast<BattleCheckMenuHandler>();
                if ((object)handler == null) return false;
                if (handler.Pointer == IntPtr.Zero) return false;

                int btnType = (int)handler.curBattleCheckMenuButtonType;

                if (btnType == _lastBattleCheckBtnType)
                    return false; // no change

                bool isFirst = _lastBattleCheckBtnType < 0;
                _lastBattleCheckBtnType = btnType;
                _lastCursorIndex = btnType;

                // Announce the button
                AnnounceItem(btnType);

                if (isFirst)
                {
                    DebugHelper.Write($"GenericMenu: [BattleCheckMenuHandler] initial btnType={btnType}");
                    // Auto-read combat predictions when battle check screen first opens.
                    // Gives the player critical info: unit names, hit%, damage, crit%.
                    AnnounceBattleCheckPredictions(handler);
                }
                else
                    DebugHelper.Write($"GenericMenu: [BattleCheckMenuHandler] btnType changed to {btnType}");
            }
            catch { }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Auto-read combat predictions when battle check screen first opens.
        /// Reads hit%, damage, crit% for both attacker and defender.
        /// </summary>
        private void AnnounceBattleCheckPredictions(BattleCheckMenuHandler handler)
        {
            try
            {
                string leftSummary = ReadUnitPrediction(handler.leftUnitInfo, Loc.Get("battle_left"));
                string rightSummary = ReadUnitPrediction(handler.rightUnitInfo, Loc.Get("battle_right"));

                if (!string.IsNullOrEmpty(leftSummary) && !string.IsNullOrEmpty(rightSummary))
                    ScreenReaderOutput.SayQueued(leftSummary + "  " + Loc.Get("battle_vs") + "  " + rightSummary);
                else if (!string.IsNullOrEmpty(leftSummary))
                    ScreenReaderOutput.SayQueued(leftSummary);
                else if (!string.IsNullOrEmpty(rightSummary))
                    ScreenReaderOutput.SayQueued(rightSummary);
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: BattleCheck predictions error: {ex.Message}");
            }
        }

        private static string ReadUnitPrediction(BattleCheckMenuUnitInfoHandler info, string side)
        {
            if ((object)info == null) return null;
            try
            {
                string robot = ReadTmpSafe(info.robotName);
                string weapon = ReadTmpSafe(info.weaponName);
                string hit = ReadTmpSafe(info.hitRate);
                string damage = ReadTmpSafe(info.weaponDamage);
                string crit = ReadTmpSafe(info.criticalRate);

                string name = robot ?? "";
                if (!string.IsNullOrWhiteSpace(weapon))
                    name += " " + weapon;

                if (string.IsNullOrWhiteSpace(name)) return null;

                return Loc.Get("battle_prediction",
                    side, name.Trim(),
                    hit ?? "-", damage ?? "-", crit ?? "-");
            }
            catch { return null; }
        }

        /// <summary>
        /// Auto-read weapon detail info (hit/crit corrections, morale) when cursor changes.
        /// Reads from WeaponListHandler.infoHandler detail panel.
        /// Queued so it doesn't interrupt the weapon name announcement.
        /// </summary>
        private void AnnounceWeaponDetail()
        {
            try
            {
                var handler = _activeHandler.TryCast<WeaponListHandler>();
                if ((object)handler == null) return;

                var info = handler.infoHandler;
                if ((object)info == null) return;

                var parts = new List<string>();

                // Hit/Crit rate corrections
                string hit = ReadTmpSafe(info.hitRateCorrection);
                if (!string.IsNullOrWhiteSpace(hit))
                    parts.Add(Loc.Get("battle_hit_rate") + " " + hit);

                string crit = ReadTmpSafe(info.crtRateCorrection);
                if (!string.IsNullOrWhiteSpace(crit))
                    parts.Add(Loc.Get("battle_critical") + " " + crit);

                // Morale requirement
                string morale = ReadTmpSafe(info.moraleNum);
                if (!string.IsNullOrWhiteSpace(morale) && morale != "0")
                    parts.Add(Loc.Get("weapon_morale_req") + " " + morale);

                // Required skill
                string skill = ReadTmpSafe(info.needSkill);
                if (!string.IsNullOrWhiteSpace(skill))
                    parts.Add(Loc.Get("weapon_required_skill") + " " + skill);

                if (parts.Count > 0)
                    ScreenReaderOutput.SayQueued(string.Join(", ", parts));
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: WeaponDetail error: {ex.Message}");
            }
        }

        /// <summary>
        /// Special update path for AssistLinkManager.
        /// This handler uses CursorInfo.SelectNo for grid-based navigation
        /// instead of UIHandlerBase.currentCursorIndex.
        /// Reads name via AssistLinkData.Name (reliable) and enriches with
        /// level/status from assist_link_work_copy data.
        /// </summary>
        private bool UpdateAssistLinkHandler()
        {
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] assist link handler init");
            }

            try
            {
                var alm = _activeHandler.TryCast<AssistLinkManager>();
                if ((object)alm == null) return false;
                if (alm.Pointer == IntPtr.Zero) return false;

                var curInfo = alm.curInfo;
                if ((object)curInfo == null) return false;

                int selectNo = curInfo.SelectNo;
                if (selectNo == _lastAssistSelectNo)
                    return false;

                _lastAssistSelectNo = selectNo;
                _lastCursorIndex = selectNo;

                // Include level/status in navigation announcement
                string text = ReadAssistLinkItemText(alm, selectNo);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ScreenReaderOutput.Say(text);
                    DebugHelper.Write($"GenericMenu: [AssistLink] select={selectNo} text={text}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: AssistLink error: {ex.GetType().Name}: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Read assist link item text at the given selection index.
        /// Strategy 1: Get work data (id/level/status) from assist_link_work_copy.
        /// Strategy 2: Look up display name via AssistLinkData.Name (most reliable).
        /// Strategy 3: Fallback to GetAssistName, ButtonItem TMP text, or raw id.
        /// Enriches with level/registered status (unless nameOnly).
        ///
        /// IMPORTANT: itemList contains only visible panel items (recycled pool).
        /// selectNo is the absolute logical index, NOT the itemList array index.
        /// Match by ButtonItem.no == selectNo (set during scroll updates).
        /// </summary>
        private string ReadAssistLinkItemText(AssistLinkManager alm, int selectNo, bool nameOnly = false)
        {
            if (selectNo < 0) return null;

            string itemName = null;
            string commandName = null; // link skill name (e.g. 加速, 鉄壁)
            string itemId = null;
            int level = -1;
            bool registered = false;

            // Step 1: Get work data (id, level, status) from assist_link_work_copy
            try
            {
                var workCopy = alm.assist_link_work_copy;
                if ((object)workCopy != null && selectNo < workCopy.Count)
                {
                    var wk = workCopy[selectNo];
                    if ((object)wk != null)
                    {
                        itemId = wk.id;
                        level = wk.level;
                        registered = wk.regist;
                    }
                }
            }
            catch { }

            // Step 2: Get display name + command name from AssistLinkData
            // Use alm.GetAssistLinkData (AssistLinkManager version) which populates
            // CommandName. The AssistManager version may not fill CommandName.
            if (!string.IsNullOrEmpty(itemId))
            {
                try
                {
                    // Primary: AssistLinkManager.GetAssistLinkData (UI handler version)
                    // IMPORTANT: Cache ALL fields from the data object here because
                    // GetAssistLinkData returns a mutable/reused object. A second call
                    // (e.g. from screen review) returns empty fields.
                    try
                    {
                        var data = alm.GetAssistLinkData(itemId);
                        if ((object)data != null)
                        {
                            string name = data.Name;
                            if (!string.IsNullOrWhiteSpace(name))
                                itemName = TextUtils.CleanRichText(name);

                            try
                            {
                                string cmd = data.CommandName;
                                if (!string.IsNullOrWhiteSpace(cmd))
                                    commandName = TextUtils.CleanRichText(cmd);
                            }
                            catch { }

                            // Cache all fields for screen review
                            _cachedALId = itemId;
                            _cachedALPersonName = itemName;
                            _cachedALCommandName = commandName;
                            try { _cachedALCmdStr = TextUtils.CleanRichText(data.CommandStr); } catch { _cachedALCmdStr = null; }
                            try { _cachedALLvCmdStr = TextUtils.CleanRichText(data.LvExCommandStr); } catch { _cachedALLvCmdStr = null; }
                            try { _cachedALPassiveStr = TextUtils.CleanRichText(data.PassiveStr); } catch { _cachedALPassiveStr = null; }
                            try { _cachedALLvPassiveStr = TextUtils.CleanRichText(data.LvExPassiveStr); } catch { _cachedALLvPassiveStr = null; }
                            try { _cachedALTarget = TextUtils.CleanRichText(data.Target); } catch { _cachedALTarget = null; }
                            try { _cachedALDuration = TextUtils.CleanRichText(data.Duration); } catch { _cachedALDuration = null; }
                        }
                    }
                    catch
                    {
                        // Clear stale cache on error to prevent screen review
                        // showing wrong data from the previous item
                        _cachedALId = null;
                        _cachedALPersonName = null;
                        _cachedALCommandName = null;
                        _cachedALCmdStr = null;
                        _cachedALLvCmdStr = null;
                        _cachedALPassiveStr = null;
                        _cachedALLvPassiveStr = null;
                        _cachedALTarget = null;
                        _cachedALDuration = null;
                    }

                    // Fallback for commandName: GetActivationCommandText
                    if (string.IsNullOrWhiteSpace(commandName))
                    {
                        try
                        {
                            string cmd = alm.GetActivationCommandText(itemId);
                            if (!string.IsNullOrWhiteSpace(cmd))
                                commandName = TextUtils.CleanRichText(cmd);
                        }
                        catch { }
                    }

                    // Diagnostic: log what we got
                    DebugHelper.Write($"AssistLink item: id={itemId} Name=[{itemName}] CmdName=[{commandName}]");

                    // Fallback for itemName: AssistManager.GetAssistName
                    if (string.IsNullOrWhiteSpace(itemName))
                    {
                        try
                        {
                            var gm = GameManager.Instance;
                            if ((object)gm != null && gm.Pointer != IntPtr.Zero)
                            {
                                var am = gm.assistManager;
                                if ((object)am != null && am.Pointer != IntPtr.Zero)
                                {
                                    string name = am.GetAssistName(itemId);
                                    if (!string.IsNullOrWhiteSpace(name))
                                        itemName = TextUtils.CleanRichText(name);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Step 3: Fallback to ButtonItem TMP text (match by no field, not array index)
            if (string.IsNullOrWhiteSpace(itemName))
            {
                try
                {
                    var items = alm.itemList;
                    if ((object)items != null && items.Count > 0)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            try
                            {
                                var btnItem = items[i];
                                if ((object)btnItem == null) continue;
                                if (btnItem.no != selectNo) continue;

                                var obj = btnItem.obj;
                                if ((object)obj != null && obj.Pointer != IntPtr.Zero)
                                {
                                    itemName = ReadBestTmpText(obj);
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // Step 4: Last resort - use raw id
            if (string.IsNullOrWhiteSpace(itemName) && !string.IsNullOrEmpty(itemId))
                itemName = itemId;

            if (string.IsNullOrWhiteSpace(itemName)) return null;

            // Name only: just return the name without enrichment
            if (nameOnly) return itemName;

            // Build announcement: commandName - personName + level + registered status
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(commandName))
            {
                sb.Append(commandName);
                sb.Append(" - ");
            }
            sb.Append(itemName);
            if (level >= 0)
            {
                sb.Append(" ");
                sb.Append(Loc.Get("assistlink_level", level + 1));
            }
            if (registered)
            {
                sb.Append(" [");
                sb.Append(Loc.Get("assistlink_registered"));
                sb.Append("]");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Special update path for PartsEquipUIHandler.
        /// This handler doesn't update currentCursorIndex from UIHandlerBase.
        /// Track equipmentUIHandler.currentIndex instead, and read part name
        /// from the list item's m_PartsName TMP field + remain/total counts.
        /// </summary>
        private bool UpdatePartsEquipHandler()
        {
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] parts equip handler init");
            }

            try
            {
                var peHandler = _activeHandler.TryCast<PartsEquipUIHandler>();
                if ((object)peHandler == null) return false;
                if (peHandler.Pointer == IntPtr.Zero) return false;

                var equipUI = peHandler.equipmentUIHandler;
                if ((object)equipUI == null || equipUI.Pointer == IntPtr.Zero) return false;

                int curIdx = equipUI.currentIndex;
                if (curIdx == _lastCursorIndex)
                    return false;

                _lastCursorIndex = curIdx;
                _stalePollCount = 0;

                // Read part name from the list item via ListHandlerBase
                string text = null;
                if ((object)_listHandler != null)
                    text = ReadPartsListItemText(curIdx);

                // Fallback: generic list reading
                if (string.IsNullOrWhiteSpace(text))
                    text = ReadListItemText(curIdx);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ScreenReaderOutput.Say(text);
                    DebugHelper.Write($"GenericMenu: [PartsEquip] cursor={curIdx} text={text}");
                }
                else
                {
                    DebugHelper.Write($"GenericMenu: [PartsEquip] cursor={curIdx} no text found");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: PartsEquip error: {ex.GetType().Name}: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Read part name and counts from a PartsEquipPartsPossessionColumn list item.
        /// Returns formatted text: "partName remainCount/totalCount"
        /// </summary>
        private string ReadPartsListItemText(int cursorIndex)
        {
            try
            {
                if ((object)_listHandler == null || _listHandler.Pointer == IntPtr.Zero)
                {
                    _listHandler = null;
                    return null;
                }

                var items = _listHandler.m_ListItem;
                if ((object)items == null) return null;
                int itemCount = items.Count;
                if (itemCount == 0) return null;

                ListItemHandler item = null;
                if (cursorIndex >= 0 && cursorIndex < itemCount)
                    item = items[cursorIndex];
                else if (itemCount > 0)
                    item = items[((cursorIndex % itemCount) + itemCount) % itemCount];

                if ((object)item == null || item.Pointer == IntPtr.Zero) return null;

                // Try to cast to PartsEquipPartsPossessionColumn for structured reading
                var partsCol = item.TryCast<PartsEquipPartsPossessionColumn>();
                if ((object)partsCol != null && partsCol.Pointer != IntPtr.Zero)
                {
                    string name = null;
                    string remain = null;
                    string total = null;

                    try
                    {
                        var nameTmp = partsCol.m_PartsName;
                        if ((object)nameTmp != null)
                            name = TextUtils.CleanRichText(nameTmp.text);
                    }
                    catch { }

                    try
                    {
                        var remainTmp = partsCol.m_PartsRemainCnt;
                        if ((object)remainTmp != null)
                            remain = remainTmp.text?.Trim();
                    }
                    catch { }

                    try
                    {
                        var totalTmp = partsCol.m_PartsTotalCnt;
                        if ((object)totalTmp != null)
                            total = totalTmp.text?.Trim();
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        if (!string.IsNullOrEmpty(remain) && !string.IsNullOrEmpty(total))
                            return $"{name}  {remain}/{total}";
                        return name;
                    }
                }

                // Fallback: generic TMP extraction
                return ExtractListItemText(item);
            }
            catch
            {
                _listHandler = null;
                return null;
            }
        }

        /// <summary>
        /// Special update path for TacticalPartSpiritUIHandler.
        /// This handler doesn't update currentCursorIndex from UIHandlerBase.
        /// Instead, track CurrentSpiritBtnHandler pointer to detect selection
        /// changes, and currentPilotIndex to detect pilot switching (L2/R2).
        /// </summary>
        private bool UpdateSpiritHandler()
        {
            // Screen name announcement for new handler
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] spirit handler init");
            }

            try
            {
                var spiritHandler = _activeHandler.TryCast<TacticalPartSpiritUIHandler>();
                if ((object)spiritHandler == null) return false;

                // Check pilot switch (L2/R2 changes which pilot's spirits are shown)
                int pilotIndex = spiritHandler.currentPilotIndex;
                if (pilotIndex != _lastSpiritPilotIndex && _lastSpiritPilotIndex >= 0)
                {
                    _lastSpiritBtnPtr = IntPtr.Zero; // force re-announce
                    DebugHelper.Write($"GenericMenu: Spirit pilot switched to {pilotIndex}");
                }
                _lastSpiritPilotIndex = pilotIndex;

                // Track CurrentSpiritBtnHandler pointer changes
                var current = spiritHandler.CurrentSpiritBtnHandler;
                if ((object)current == null || current.Pointer == IntPtr.Zero)
                    return false;

                IntPtr currentPtr = current.Pointer;
                if (currentPtr == _lastSpiritBtnPtr)
                    return false; // no change

                _lastSpiritBtnPtr = currentPtr;

                // Find index in spiritCommandsButtonHandlerList
                int index = -1;
                var btns = spiritHandler.spiritCommandsButtonHandlerList;
                if ((object)btns != null)
                {
                    for (int i = 0; i < btns.Count; i++)
                    {
                        try
                        {
                            var btn = btns[i];
                            if ((object)btn != null && btn.Pointer == currentPtr)
                            { index = i; break; }
                        }
                        catch { }
                    }
                }

                if (index >= 0)
                {
                    _lastCursorIndex = index;
                    AnnounceItem(index);
                }
            }
            catch { }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Track TacticalOthersCommandUIHandler cursor changes.
        /// Same SpiritButtonHandler pattern as spirit handler - tracks
        /// CurrentOtherCommandBtnHandler pointer instead of currentCursorIndex.
        /// </summary>
        private bool UpdateOthersCommandHandler()
        {
            // Screen name announcement for new handler
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] others command handler init");
            }

            try
            {
                var othersHandler = _activeHandler.TryCast<TacticalOthersCommandUIHandler>();
                if ((object)othersHandler == null) return false;

                // Track CurrentOtherCommandBtnHandler pointer changes
                var current = othersHandler.CurrentOtherCommandBtnHandler;
                if ((object)current == null || current.Pointer == IntPtr.Zero)
                    return false;

                IntPtr currentPtr = current.Pointer;
                if (currentPtr == _lastOthersCmdBtnPtr)
                    return false; // no change

                _lastOthersCmdBtnPtr = currentPtr;

                // Find index in othersCommandsButtonHandlerList
                int index = -1;
                var btns = othersHandler.othersCommandsButtonHandlerList;
                if ((object)btns != null)
                {
                    for (int i = 0; i < btns.Count; i++)
                    {
                        try
                        {
                            var btn = btns[i];
                            if ((object)btn != null && btn.Pointer == currentPtr)
                            { index = i; break; }
                        }
                        catch { }
                    }
                }

                if (index >= 0)
                {
                    _lastCursorIndex = index;
                    AnnounceItem(index);
                }
            }
            catch { }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Find the active UIHandlerBase by matching controlBehaviour
        /// with InputManager's current input behaviour.
        /// Returns true if a valid handler was found (new or same).
        /// </summary>
        private bool FindActiveHandler()
        {
            InputManager inputMgr;
            try
            {
                inputMgr = InputManager.instance;
            }
            catch { return false; }

            if ((object)inputMgr == null) return false;

            IInputBehaviour currentBehaviour;
            try
            {
                if (inputMgr.Pointer == IntPtr.Zero) return false;
                currentBehaviour = inputMgr.GetCurrentInputBehaviour();
            }
            catch { return false; }

            if ((object)currentBehaviour == null) return false;

            IntPtr currentBehaviourPtr;
            try
            {
                currentBehaviourPtr = currentBehaviour.Pointer;
                if (currentBehaviourPtr == IntPtr.Zero) return false;
            }
            catch { return false; }

            // Fast-path: verify cached handler still matches.
            // SAFETY: Compare stored behaviour pointer (managed field) instead
            // of accessing _activeHandler.controlBehaviour (IL2CPP field access).
            // If the native handler object was freed during a scene transition,
            // accessing IL2CPP fields causes AccessViolationException which is
            // uncatchable in .NET 6 and kills the entire process.
            if ((object)_activeHandler != null && _cachedBehaviourPtr != IntPtr.Zero)
            {
                if (_cachedBehaviourPtr == currentBehaviourPtr)
                    return true;

                // Behaviour pointer changed - handler may be stale.
                // Fall through to full scan to find the new handler.
            }

            // Find all UIHandlerBase instances in the scene
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<UIHandlerBase> handlers;
            try
            {
                handlers = UnityEngine.Object.FindObjectsOfType<UIHandlerBase>();
            }
            catch { return false; }

            if (handlers == null || handlers.Count == 0) return false;

            foreach (var handler in handlers)
            {
                if ((object)handler == null) continue;

                try
                {
                    if (handler.Pointer == IntPtr.Zero) continue;

                    var hGo = handler.gameObject;
                    if ((object)hGo == null || hGo.Pointer == IntPtr.Zero) continue;

                    var cb = handler.controlBehaviour;
                    if ((object)cb == null) continue;
                    if (cb.Pointer == IntPtr.Zero) continue;

                    if (cb.Pointer == currentBehaviourPtr)
                    {
                        string typeName;
                        try { typeName = handler.GetIl2CppType().Name; }
                        catch { typeName = "unknown"; }

                        if (_skipTypes.Contains(typeName)) continue;

                        IntPtr handlerPtr = handler.Pointer;
                        if (handlerPtr == _lastHandlerPtr)
                        {
                            _activeHandler = handler;
                            _cachedBehaviourPtr = currentBehaviourPtr;
                            return true;
                        }

                        // New handler found
                        _activeHandler = handler;
                        _lastHandlerPtr = handlerPtr;
                        _cachedBehaviourPtr = currentBehaviourPtr;
                        _lastCursorIndex = -1;
                        _activeHandlerType = typeName;
                        _typeSpecificCount = -1;
                        _newHandlerJustFound = true;
                        _cmdDiagDumped = false;

                        // Detect ListHandlerBase for list-based menus
                        _listHandler = null;
                        try
                        {
                            var lh = hGo.GetComponentInChildren<ListHandlerBase>(false);
                            if ((object)lh != null && lh.Pointer != IntPtr.Zero)
                                _listHandler = lh;
                        }
                        catch { }

                        DebugHelper.Write($"GenericMenu: Found {_activeHandlerType}{((object)_listHandler != null ? " [list]" : "")}");
                        return true;
                    }
                }
                catch { continue; }
            }

            return false;
        }

        /// <summary>
        /// Check if the cached handler is still the active one by reading
        /// InputManager's current behaviour pointer (safe static field read).
        /// Returns false if the behaviour has changed (handler may be stale).
        /// </summary>
        private bool IsHandlerStillActive()
        {
            if (_cachedBehaviourPtr == IntPtr.Zero) return false;

            try
            {
                var mgr = InputManager.instance;
                if ((object)mgr == null) return false;
                if (mgr.Pointer == IntPtr.Zero) return false;

                var beh = mgr.GetCurrentInputBehaviour();
                if ((object)beh == null) return false;

                return beh.Pointer == _cachedBehaviourPtr;
            }
            catch
            {
                return false;
            }
        }

        // ===== Announcement =====

        /// <summary>
        /// Read and announce the current menu item.
        /// Tries type-specific, list-based, and generic button strategies.
        /// Announces text only - no option count.
        /// </summary>
        private void AnnounceItem(int index)
        {
            // Strategy 1: Type-specific text
            string text = GetTypeSpecificText(_activeHandlerType, index);

            // Grid suppression: type-specific handlers with cursor beyond button count.
            // Skip for TacticallPartCommandUIHandler - cursor = btnType enum value,
            // so btnType matching in ReadCommandButtonText already handles suppression
            // (no match = null text = nothing announced).
            if (string.IsNullOrWhiteSpace(text) && (object)_listHandler == null
                && _typeSpecificCount > 0 && index >= _typeSpecificCount
                && _activeHandlerType != "TacticallPartCommandUIHandler")
            {
                return;
            }

            // Strategy 2: List-based reading
            if (string.IsNullOrWhiteSpace(text) && (object)_listHandler != null)
            {
                text = ReadListItemText(index);
            }

            // Strategy 3: Generic button reading (on-demand)
            if (string.IsNullOrWhiteSpace(text))
            {
                text = ReadGenericButtonText(index);
            }

            // Strategy 4: Read all visible TMP text (info/status screens only)
            if (string.IsNullOrWhiteSpace(text) && _infoScreenTypes.Contains(_activeHandlerType))
            {
                text = ReadAllVisibleText();
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                ScreenReaderOutput.Say(text);

                // Auto-read weapon detail info (hit/crit corrections, morale, skill)
                // after the weapon name. Queued so weapon name is heard first.
                if (_activeHandlerType == "WeaponListHandler")
                    AnnounceWeaponDetail();
            }
            else
            {
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] cursor={index} no text found");
            }
        }

        // ===== Extra button monitoring =====

        /// <summary>
        /// For TacticallPartCommandUIHandler, the last button (e.g. LandformAction)
        /// is not in the cursor navigation cycle. Announce it when the menu opens.
        /// </summary>
        private void AnnounceExtraButtons()
        {
            if (_activeHandlerType != "TacticallPartCommandUIHandler" || _isMapMenu) return;

            string text = GetExtraButtonText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _lastExtraButtonText = text;
                ScreenReaderOutput.SayQueued(text);
                DebugHelper.Write($"GenericMenu: Extra button queued: {text}");
            }
        }

        /// <summary>
        /// Monitor the extra (non-cursor) button text for changes.
        /// If the text changes (e.g. LandformAction toggles 陸地↔空中), announce it.
        /// </summary>
        private void MonitorExtraButtons()
        {
            if (_activeHandlerType != "TacticallPartCommandUIHandler" || _isMapMenu) return;
            if (_lastExtraButtonText == null) return;

            string text = GetExtraButtonText();
            if (!string.IsNullOrWhiteSpace(text) && text != _lastExtraButtonText)
            {
                _lastExtraButtonText = text;
                ScreenReaderOutput.Say(text);
                DebugHelper.Write($"GenericMenu: Extra button changed: {text}");
            }
        }

        /// <summary>
        /// Read text from the last (non-cursor-navigable) button in TacticallPartCommandUIHandler.
        /// </summary>
        private string GetExtraButtonText()
        {
            try
            {
                var cmdHandler = _activeHandler.TryCast<TacticallPartCommandUIHandler>();
                if ((object)cmdHandler == null) return null;

                var buttons = cmdHandler.buttonList;
                if ((object)buttons == null || buttons.Count < 2) return null;

                var lastBtn = buttons[buttons.Count - 1];
                if ((object)lastBtn == null) return null;

                string text = ReadButtonHandlerText(lastBtn);

                if (string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        string typeName = lastBtn.btnType.ToString();
                        string locKey = "cmd_" + typeName.ToLowerInvariant();
                        string locText = Loc.Get(locKey);
                        if (locText != locKey) text = locText;
                    }
                    catch { }
                }

                return text;
            }
            catch { return null; }
        }

        // ===== Generic button reading (on-demand, no caching) =====

        /// <summary>
        /// Read button text at the given cursor index directly from UI hierarchy.
        /// Finds active buttons via GetComponentsInChildren and reads text
        /// from the button at the specified index.
        /// Returns null if index is out of range or no text found.
        /// </summary>
        private string ReadGenericButtonText(int index)
        {
            if ((object)_activeHandler == null || index < 0) return null;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return null;

                var buttons = go.GetComponentsInChildren<Button>(false);
                if (buttons == null || buttons.Count == 0 || index >= buttons.Count) return null;

                var btn = buttons[index];
                if ((object)btn == null) return null;

                try
                {
                    var btnGo = btn.gameObject;
                    if ((object)btnGo == null || !btnGo.activeInHierarchy) return null;
                }
                catch { return null; }

                return ReadBestTmpText(btn.gameObject);
            }
            catch { return null; }
        }

        /// <summary>
        /// Extract the best (longest non-numeric) TextMeshProUGUI text
        /// from a GameObject's children.
        /// </summary>
        private static string ReadBestTmpText(GameObject go)
        {
            try
            {
                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                string bestText = null;
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;
                    try
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (!string.IsNullOrWhiteSpace(t) && !IsNumericOnly(t))
                        {
                            if (bestText == null || t.Length > bestText.Length)
                                bestText = t;
                        }
                    }
                    catch { }
                }

                return bestText;
            }
            catch { return null; }
        }

        // ===== List-based reading =====

        /// <summary>
        /// Read text from a ListHandlerBase item at the given cursor index.
        /// For list-based menus (SaveLoad, PilotList, Shop, etc.), this extracts
        /// text from ListItemHandler items in m_ListItem.
        /// Returns null if unavailable or reading fails.
        /// </summary>
        private string ReadListItemText(int cursorIndex)
        {
            try
            {
                if ((object)_listHandler == null || _listHandler.Pointer == IntPtr.Zero)
                {
                    _listHandler = null;
                    return null;
                }

                var items = _listHandler.m_ListItem;
                if ((object)items == null) return null;
                int itemCount = items.Count;
                if (itemCount == 0) return null;

                ListItemHandler item = null;

                // Direct index (works for non-virtualized lists)
                if (cursorIndex >= 0 && cursorIndex < itemCount)
                {
                    item = items[cursorIndex];
                }
                else if (itemCount > 0)
                {
                    // Virtualized list: items are recycled, use safe modulo
                    int localIndex = ((cursorIndex % itemCount) + itemCount) % itemCount;
                    item = items[localIndex];
                }

                if ((object)item == null || item.Pointer == IntPtr.Zero) return null;

                // Check if item is active
                try
                {
                    if (!item.GetActive()) return null;
                }
                catch { }

                return ExtractListItemText(item);
            }
            catch
            {
                _listHandler = null;
                return null;
            }
        }

        /// <summary>
        /// Extract all meaningful text from a ListItemHandler.
        /// Reads TextMeshProUGUI from the item's buttonGo (or go/gameObject fallback)
        /// and joins all non-empty texts.
        /// </summary>
        private string ExtractListItemText(ListItemHandler item)
        {
            try
            {
                GameObject targetGo = null;

                // Try buttonGo first (the clickable area with labels)
                try
                {
                    var bgo = item.buttonGo;
                    if ((object)bgo != null && bgo.Pointer != IntPtr.Zero)
                        targetGo = bgo;
                }
                catch { }

                // Fall back to go (the main item object)
                if ((object)targetGo == null)
                {
                    try
                    {
                        var go = item.go;
                        if ((object)go != null && go.Pointer != IntPtr.Zero)
                            targetGo = go;
                    }
                    catch { }
                }

                // Last resort: the MonoBehaviour's gameObject
                if ((object)targetGo == null)
                {
                    targetGo = item.gameObject;
                    if ((object)targetGo == null || targetGo.Pointer == IntPtr.Zero)
                        return null;
                }

                var tmps = targetGo.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                var texts = new System.Collections.Generic.List<string>();
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;
                    try
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (!string.IsNullOrWhiteSpace(t))
                            texts.Add(t);
                    }
                    catch { }
                }

                if (texts.Count == 0) return null;
                return string.Join("  ", texts);
            }
            catch { return null; }
        }

        // ===== Type-specific handlers =====

        /// <summary>
        /// Type-specific text override for handlers where generic button/list
        /// extraction doesn't work or gives wrong order.
        /// Returns null for unknown handler types or on failure.
        /// </summary>
        private string GetTypeSpecificText(string typeName, int index)
        {
            if (typeName == null || (object)_activeHandler == null) return null;

            try
            {
                if (typeName == "TacticallPartCommandUIHandler")
                    return ReadCommandButtonText(index);

                if (typeName == "TacticalPartSpiritUIHandler")
                    return ReadSpiritButtonText(index);

                if (typeName == "TitleUIButtonHandler")
                {
                    // ButtonType enum: CONTINUE=0, START=1, LOAD=2, OPTION=3, LANGUAGE=4, STORE=5, QUIT=6
                    switch (index)
                    {
                        case 0: return Loc.Get("menu_continue");
                        case 1: return Loc.Get("menu_start");
                        case 2: return Loc.Get("menu_load");
                        case 3: return Loc.Get("menu_option");
                        case 4: return Loc.Get("menu_language");
                        case 5: return Loc.Get("menu_store");
                        case 6: return Loc.Get("menu_quit");
                    }
                }

                if (typeName == "OptionMenuUIHandler")
                {
                    switch (index)
                    {
                        case 0: return Loc.Get("option_save");
                        case 1: return Loc.Get("option_load");
                        case 2: return Loc.Get("option_system");
                        case 3: return Loc.Get("option_return_title");
                        case 4: return Loc.Get("option_exit");
                    }
                }

                if (typeName == "StrategyTopUIHandler")
                {
                    switch (index)
                    {
                        case 0: return Loc.Get("strategy_mission");
                        case 1: return Loc.Get("strategy_unit");
                        case 2: return Loc.Get("strategy_update");
                        case 3: return Loc.Get("strategy_library");
                        case 4: return Loc.Get("strategy_option");
                    }
                }

                if (typeName == "DatabaseTopUIHandler")
                {
                    switch (index)
                    {
                        case 0: return Loc.Get("db_library");
                        case 1: return Loc.Get("db_sound");
                        case 2: return Loc.Get("db_movie");
                        case 3: return Loc.Get("db_record");
                        case 4: return Loc.Get("db_search");
                        case 5: return Loc.Get("db_tutorial");
                    }
                }

                if (typeName == "BattleCheckMenuHandler")
                    return ReadBattleCheckButtonText(index);

                if (typeName == "UnitCommandUIHandler")
                    return ReadUnitCommandText(index);

                if (typeName == "TacticalOthersCommandUIHandler")
                    return ReadOthersCommandButtonText(index);

                if (typeName == "DifficultyUIHandler")
                    return ReadDifficultyText();

                if (typeName == "SortiePreparationTopUIHandler")
                {
                    // ButtonType enum: PrepareFinish=0, SortieShipSelect=1,
                    // SortieUnitSelect=2, Unit=3, Save=4
                    switch (index)
                    {
                        case 0: return Loc.Get("sortie_finish");
                        case 1: return Loc.Get("sortie_ship");
                        case 2: return Loc.Get("sortie_unit");
                        case 3: return Loc.Get("sortie_unit_manage");
                        case 4: return Loc.Get("sortie_save");
                    }
                }

                if (typeName == "LibraryRobotDetailUIHandler")
                    return ReadLibraryRobotInfo();

                if (typeName == "LibraryCharDetailUIHandler")
                    return ReadLibraryCharInfo();
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Read battle check menu button text from BattleCheckMenuHandler.
        /// Cursor index maps to BattleCheckMenuButtonType enum:
        /// BATTLE=0, SELECT=1, SPIRIT=2, ASSISTLINK=3, SUPPORT=4,
        /// DETAIL=5, COUNTER=6, GUARD=7, AVOID=8.
        /// Also reads guideText for additional context.
        /// </summary>
        private string ReadBattleCheckButtonText(int index)
        {
            try
            {
                var handler = _activeHandler.TryCast<BattleCheckMenuHandler>();
                if ((object)handler == null) return null;
                if (handler.Pointer == IntPtr.Zero) return null;

                // Use index directly as BattleCheckMenuButtonType enum value
                BattleCheckMenuButtonType btnType = (BattleCheckMenuButtonType)index;

                string name = null;
                switch (btnType)
                {
                    case BattleCheckMenuButtonType.BATTLE: name = Loc.Get("battle_battle"); break;
                    case BattleCheckMenuButtonType.SELECT: name = Loc.Get("battle_select"); break;
                    case BattleCheckMenuButtonType.SPIRIT: name = Loc.Get("battle_spirit"); break;
                    case BattleCheckMenuButtonType.ASSISTLINK: name = Loc.Get("battle_assistlink"); break;
                    case BattleCheckMenuButtonType.SUPPORT: name = Loc.Get("battle_support"); break;
                    case BattleCheckMenuButtonType.DETAIL: name = Loc.Get("battle_detail"); break;
                    case BattleCheckMenuButtonType.COUNTER: name = Loc.Get("battle_counter"); break;
                    case BattleCheckMenuButtonType.GUARD: name = Loc.Get("battle_guard"); break;
                    case BattleCheckMenuButtonType.AVOID: name = Loc.Get("battle_avoid"); break;
                }

                return name;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Read command button text from TacticallPartCommandUIHandler.buttonList.
        /// This handler manages both unit commands (Move/Attack/Spirit/Wait) and
        /// map menu (End Turn/Unit List/System). Uses buttonList for correct order
        /// instead of GetComponentsInChildren which gives wrong DOM order.
        ///
        /// Cursor value = ButtonType enum value (e.g. Move=1, Attack=2, PhaseEnd=15).
        /// Finds the button by matching btnType to cursor value.
        /// </summary>
        private string ReadCommandButtonText(int index)
        {
            try
            {
                var cmdHandler = _activeHandler.TryCast<TacticallPartCommandUIHandler>();
                if ((object)cmdHandler == null) return null;

                var buttons = cmdHandler.buttonList;
                if ((object)buttons == null) return null;

                // Diagnostic: dump all buttons once per handler instance
                if (!_cmdDiagDumped)
                {
                    _cmdDiagDumped = true;
                    DumpCommandButtons(cmdHandler, buttons);
                }

                // Find button by matching btnType to cursor value
                ButtonHandler btn = null;
                for (int i = 0; i < buttons.Count; i++)
                {
                    var candidate = buttons[i];
                    if ((object)candidate == null) continue;
                    try
                    {
                        if ((int)candidate.btnType == index)
                        {
                            btn = candidate;
                            break;
                        }
                    }
                    catch { }
                }

                if ((object)btn == null) return null;

                string btnText = ReadButtonHandlerText(btn);

                // Fallback: use btnType for localized command name
                if (string.IsNullOrWhiteSpace(btnText))
                {
                    try
                    {
                        string typeName = btn.btnType.ToString();
                        string locKey = "cmd_" + typeName.ToLowerInvariant();
                        string locText = Loc.Get(locKey);
                        if (locText != locKey)
                            btnText = locText;
                    }
                    catch { }
                }

                return btnText;
            }
            catch { }
            return null;
        }

        private void DumpCommandButtons(TacticallPartCommandUIHandler cmdHandler, Il2CppSystem.Collections.Generic.List<ButtonHandler> buttons)
        {
            try
            {
                int count = buttons.Count;
                string cmdTypeName = "?";
                try { cmdTypeName = cmdHandler.currentCommandType.ToString(); } catch { }
                DebugHelper.Write($"=== CmdBtn DUMP: count={count} commandType={cmdTypeName} ===");
                for (int i = 0; i < count; i++)
                {
                    var btn = buttons[i];
                    if ((object)btn == null) continue;
                    string btnType = "?";
                    try { btnType = btn.btnType.ToString(); } catch { }
                    string text = ReadButtonHandlerText(btn);
                    DebugHelper.Write($"  [{i}] type={btnType} text='{text}'");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"CmdBtn DUMP error: {ex.Message}");
            }
        }

        private static string ReadButtonHandlerText(ButtonHandler btn)
        {
            // Read from btnTextList (localized UI text components)
            try
            {
                var textList = btn.btnTextList;
                if ((object)textList != null && textList.Count > 0)
                {
                    var tmp = textList[0];
                    if ((object)tmp != null)
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
            catch { }

            // Fallback: read from textDataList (data objects with CommandText)
            try
            {
                var dataList = btn.textDataList;
                if ((object)dataList != null && dataList.Count > 0)
                {
                    var data = dataList[0];
                    if ((object)data != null)
                    {
                        string t = data.CommandText;
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
            catch { }

            // Fallback: read from btnGuideText
            try
            {
                string guide = btn.btnGuideText;
                if (!string.IsNullOrWhiteSpace(guide)) return guide;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Read spirit button text from TacticalPartSpiritUIHandler.
        /// Reads spirit name from SpiritButtonHandler.BtnTextList and
        /// SP cost from costTextList. Returns "name SP:cost" format.
        /// </summary>
        private string ReadSpiritButtonText(int index)
        {
            try
            {
                var spiritHandler = _activeHandler.TryCast<TacticalPartSpiritUIHandler>();
                if ((object)spiritHandler == null) return null;

                var btns = spiritHandler.spiritCommandsButtonHandlerList;
                if ((object)btns == null) return null;
                if (index < 0 || index >= btns.Count) return null;

                var btn = btns[index];
                if ((object)btn == null) return null;

                var textArr = btn.BtnTextList;
                if (textArr == null || textArr.Length == 0) return null;

                var tmp = textArr[0];
                if ((object)tmp == null) return null;

                string name = TextUtils.CleanRichText(tmp.text);
                if (string.IsNullOrWhiteSpace(name)) return null;

                // Include SP cost if available
                string costSuffix = "";
                try
                {
                    var costArr = btn.costTextList;
                    if (costArr != null && costArr.Length > 0)
                    {
                        var costTmp = costArr[0];
                        if ((object)costTmp != null)
                        {
                            string cost = TextUtils.CleanRichText(costTmp.text);
                            if (!string.IsNullOrWhiteSpace(cost) && cost != "-1")
                                costSuffix = " SP:" + cost;
                        }
                    }
                }
                catch { }

                // Check if spirit is disabled/already used
                string statusSuffix = "";
                try
                {
                    var status = btn.CurrentBtnStatus;
                    if (status == SpiritButtonHandler.BtnStatus.Disactive)
                        statusSuffix = " " + Loc.Get("spirit_disabled");
                    else if (!btn.IsButtonEnable)
                        statusSuffix = " " + Loc.Get("spirit_disabled");
                }
                catch { }

                return name + costSuffix + statusSuffix;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Read others command button text from TacticalOthersCommandUIHandler.
        /// Same SpiritButtonHandler pattern as spirit - read BtnTextList[0] + costTextList.
        /// </summary>
        private string ReadOthersCommandButtonText(int index)
        {
            try
            {
                var othersHandler = _activeHandler.TryCast<TacticalOthersCommandUIHandler>();
                if ((object)othersHandler == null) return null;

                var btns = othersHandler.othersCommandsButtonHandlerList;
                if ((object)btns == null) return null;
                if (index < 0 || index >= btns.Count) return null;

                var btn = btns[index];
                if ((object)btn == null) return null;

                var textArr = btn.BtnTextList;
                if (textArr == null || textArr.Length == 0) return null;

                var tmp = textArr[0];
                if ((object)tmp == null) return null;

                string name = TextUtils.CleanRichText(tmp.text);
                if (string.IsNullOrWhiteSpace(name)) return null;

                // Include SP cost if available
                try
                {
                    var costArr = btn.costTextList;
                    if (costArr != null && costArr.Length > 0)
                    {
                        var costTmp = costArr[0];
                        if ((object)costTmp != null)
                        {
                            string cost = TextUtils.CleanRichText(costTmp.text);
                            if (!string.IsNullOrWhiteSpace(cost) && cost != "-1")
                                return name + " SP:" + cost;
                        }
                    }
                }
                catch { }

                return name;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Read difficulty text from DifficultyUIHandler.
        /// Uses modeTitleText and modeText TMP fields for rendered text.
        /// </summary>
        private string ReadDifficultyText()
        {
            try
            {
                var handler = _activeHandler.TryCast<DifficultyUIHandler>();
                if ((object)handler == null) return null;

                string title = null;
                string desc = null;

                try
                {
                    var titleTmp = handler.modeTitleText;
                    if ((object)titleTmp != null)
                        title = TextUtils.CleanRichText(titleTmp.text);
                }
                catch { }

                try
                {
                    var descTmp = handler.modeText;
                    if ((object)descTmp != null)
                        desc = TextUtils.CleanRichText(descTmp.text);
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(desc))
                    return Loc.Get("difficulty_description", title, desc);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Read unit command text from UnitCommandUIHandler.buttonGuideText.
        /// Falls back to ButtonType enum for localized command names.
        /// </summary>
        private string ReadUnitCommandText(int index)
        {
            try
            {
                var handler = _activeHandler.TryCast<UnitCommandUIHandler>();
                if ((object)handler != null)
                {
                    var guideText = handler.buttonGuideText;
                    if ((object)guideText != null && index >= 0 && index < guideText.Count)
                    {
                        string text = guideText[index];
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            catch { }

            // Fallback: ButtonType enum
            switch (index)
            {
                case 0: return Loc.Get("unit_robot_upgrade");
                case 1: return Loc.Get("unit_pilot_upgrade");
                case 2: return Loc.Get("unit_assist");
                case 3: return Loc.Get("unit_parts");
                case 4: return Loc.Get("unit_change");
                case 5: return Loc.Get("unit_shop");
            }
            return null;
        }

        /// <summary>
        /// Read robot detail info from LibraryRobotDetailUIHandler TMP fields.
        /// Returns official name + product name + description.
        /// </summary>
        private string ReadLibraryRobotInfo()
        {
            try
            {
                var handler = _activeHandler.TryCast<LibraryRobotDetailUIHandler>();
                if ((object)handler == null) return null;

                var parts = new System.Collections.Generic.List<string>();

                try
                {
                    var name = handler.dtOfficialName;
                    if ((object)name != null)
                    {
                        string t = TextUtils.CleanRichText(name.text);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var prod = handler.dtProductName;
                    if ((object)prod != null)
                    {
                        string t = TextUtils.CleanRichText(prod.text);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var desc = handler.dtDescription;
                    if ((object)desc != null)
                    {
                        string t = TextUtils.CleanRichText(desc.text);
                        if (!string.IsNullOrWhiteSpace(t) && t.Length > 2)
                        {
                            if (t.Length > 200) t = t.Substring(0, 200) + "...";
                            parts.Add(t);
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join("  ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read character detail info from LibraryCharDetailUIHandler TMP fields.
        /// Returns official name + nickname + voice actor + description.
        /// </summary>
        private string ReadLibraryCharInfo()
        {
            try
            {
                var handler = _activeHandler.TryCast<LibraryCharDetailUIHandler>();
                if ((object)handler == null) return null;

                var parts = new System.Collections.Generic.List<string>();

                try
                {
                    var name = handler.dtOfficialName;
                    if ((object)name != null)
                    {
                        string t = TextUtils.CleanRichText(name.text);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var nick = handler.dtNickName;
                    if ((object)nick != null)
                    {
                        string t = TextUtils.CleanRichText(nick.text);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var voice = handler.dtVoiceActor;
                    if ((object)voice != null)
                    {
                        string t = TextUtils.CleanRichText(voice.text);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add("CV:" + t);
                    }
                }
                catch { }

                try
                {
                    var desc = handler.dtDescription;
                    if ((object)desc != null)
                    {
                        string t = TextUtils.CleanRichText(desc.text);
                        if (!string.IsNullOrWhiteSpace(t) && t.Length > 2)
                        {
                            if (t.Length > 200) t = t.Substring(0, 200) + "...";
                            parts.Add(t);
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join("  ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Universal fallback: read all visible TextMeshProUGUI text from
        /// the handler's gameObject hierarchy. Used for info/status screens
        /// where buttons aren't the primary UI element.
        /// Limited to first 12 items or 300 chars to avoid overwhelming.
        /// </summary>
        private string ReadAllVisibleText()
        {
            if ((object)_activeHandler == null) return null;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return null;

                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                var texts = new System.Collections.Generic.List<string>();
                int charCount = 0;
                foreach (var tmp in tmps)
                {
                    if (texts.Count >= ModConfig.ReviewVisibleMaxItems || charCount >= ModConfig.ReviewVisibleMaxChars) break;
                    if ((object)tmp == null) continue;
                    try
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (!string.IsNullOrWhiteSpace(t) && t.Length > 1 && !IsNumericOnly(t))
                        {
                            texts.Add(t);
                            charCount += t.Length;
                        }
                    }
                    catch { }
                }

                return texts.Count > 0 ? string.Join("  ", texts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get the actual button count from handler-specific fields.
        /// Returns -1 for unknown handler types.
        /// </summary>
        private int GetTypeSpecificCount(string typeName)
        {
            if (typeName == null || (object)_activeHandler == null) return -1;

            try
            {
                if (typeName == "TacticallPartCommandUIHandler")
                {
                    var cmdHandler = _activeHandler.TryCast<TacticallPartCommandUIHandler>();
                    if ((object)cmdHandler != null)
                    {
                        var buttons = cmdHandler.buttonList;
                        // Cursor is 1-based but only cycles through Count-1 buttons;
                        // values >= Count are grid navigation (suppress)
                        if ((object)buttons != null) return buttons.Count;
                    }
                }
                else if (typeName == "TacticalPartSpiritUIHandler")
                {
                    var spiritHandler = _activeHandler.TryCast<TacticalPartSpiritUIHandler>();
                    if ((object)spiritHandler != null)
                    {
                        var btns = spiritHandler.spiritCommandsButtonHandlerList;
                        if ((object)btns != null) return btns.Count;
                    }
                }
                else if (typeName == "OptionMenuUIHandler")
                {
                    return 5;
                }
                else if (typeName == "TitleUIButtonHandler")
                {
                    return 7;
                }
                else if (typeName == "StrategyTopUIHandler")
                {
                    return 5;
                }
                else if (typeName == "DatabaseTopUIHandler")
                {
                    return 6;
                }
                else if (typeName == "UnitCommandUIHandler")
                {
                    return 6;
                }
            }
            catch { }

            return -1;
        }

        // ===== Screen Review =====

        /// <summary>
        /// Get the localized screen name for the current handler.
        /// Returns null if no handler active or no localization key found.
        /// </summary>
        public string GetScreenName()
        {
            if (_activeHandlerType == null) return null;
            string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
            string screenName = Loc.Get(screenKey);
            return screenName != screenKey ? screenName : null;
        }

        /// <summary>
        /// Collect supplementary (non-option) info for screen review.
        /// R/[/] keys read EXTRA information: descriptions, stats, hit rates,
        /// battle predictions, etc. NOT menu items (those are read by cursor nav).
        /// </summary>
        public void CollectReviewItems(List<string> items)
        {
            if ((object)_activeHandler == null) return;

            // Validate handler is still alive before accessing IL2CPP fields.
            if (!IsHandlerStillActive()) return;

            // Screen name
            string screenName = GetScreenName();
            if (screenName != null)
                items.Add(screenName);

            // Info screens: read all visible TMP text (stats, details)
            if (_infoScreenTypes.Contains(_activeHandlerType))
            {
                CollectInfoScreenItems(items);
                return;
            }

            // Structured review: specific TMP fields for result/battle screens
            if (_structuredReviewTypes.Contains(_activeHandlerType))
            {
                CollectStructuredReviewItems(items);
                return;
            }

            // Supplementary info for known handler types (shop prices, etc.)
            CollectSupplementaryItems(items);

            // General fallback: read TMP text that is NOT inside buttons.
            // Captures description labels, stat displays, and other non-option info.
            if (items.Count <= 1) // only screen name so far
                CollectNonButtonText(items);
        }

        private void CollectTypeSpecificItems(List<string> items, int count)
        {
            int cursorIndex = -1;
            try { cursorIndex = _activeHandler.currentCursorIndex; } catch { }

            // For TacticallPartCommandUIHandler, cursor = btnType enum value.
            // Iterate buttonList directly instead of 0..count-1.
            if (_activeHandlerType == "TacticallPartCommandUIHandler")
            {
                try
                {
                    var cmdHandler = _activeHandler.TryCast<TacticallPartCommandUIHandler>();
                    if ((object)cmdHandler != null)
                    {
                        var buttons = cmdHandler.buttonList;
                        if ((object)buttons != null)
                        {
                            for (int i = 0; i < buttons.Count; i++)
                            {
                                var btn = buttons[i];
                                if ((object)btn == null) continue;
                                try
                                {
                                    int btnType = (int)btn.btnType;
                                    string text = ReadButtonHandlerText(btn);
                                    if (string.IsNullOrWhiteSpace(text))
                                    {
                                        string typeName = btn.btnType.ToString();
                                        string locKey = "cmd_" + typeName.ToLowerInvariant();
                                        string locText = Loc.Get(locKey);
                                        if (locText != locKey) text = locText;
                                    }
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        string prefix = (btnType == cursorIndex) ? "(*) " : "";
                                        items.Add(prefix + text);
                                    }
                                }
                                catch { }
                            }
                            return;
                        }
                    }
                }
                catch { }
            }

            // Standard type-specific: 0..count-1
            for (int i = 0; i < count; i++)
            {
                string text = GetTypeSpecificText(_activeHandlerType, i);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string prefix = (i == cursorIndex) ? "(*) " : "";
                    items.Add(prefix + text);
                }
            }
        }

        private void CollectListItems(List<string> items)
        {
            try
            {
                if ((object)_listHandler == null || _listHandler.Pointer == IntPtr.Zero) return;

                var listItems = _listHandler.m_ListItem;
                if ((object)listItems == null) return;

                int itemCount = listItems.Count;
                if (itemCount == 0) return;

                int cursorIndex = -1;
                try { cursorIndex = _activeHandler.currentCursorIndex; } catch { }

                for (int i = 0; i < itemCount; i++)
                {
                    try
                    {
                        var item = listItems[i];
                        if ((object)item == null || item.Pointer == IntPtr.Zero) continue;
                        try { if (!item.GetActive()) continue; } catch { }

                        string text = ExtractListItemText(item);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            string prefix = (i == cursorIndex) ? "(*) " : "";
                            items.Add(prefix + text);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void CollectGenericButtonItems(List<string> items)
        {
            if ((object)_activeHandler == null) return;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return;

                var buttons = go.GetComponentsInChildren<Button>(false);
                if (buttons == null || buttons.Count == 0) return;

                int cursorIndex = -1;
                try { cursorIndex = _activeHandler.currentCursorIndex; } catch { }

                for (int i = 0; i < buttons.Count; i++)
                {
                    try
                    {
                        var btn = buttons[i];
                        if ((object)btn == null) continue;

                        var btnGo = btn.gameObject;
                        if ((object)btnGo == null || !btnGo.activeInHierarchy) continue;

                        string text = ReadBestTmpText(btn.gameObject);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            string prefix = (i == cursorIndex) ? "(*) " : "";
                            items.Add(prefix + text);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void CollectInfoScreenItems(List<string> items)
        {
            if ((object)_activeHandler == null) return;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return;

                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return;

                foreach (var tmp in tmps)
                {
                    if (items.Count >= ModConfig.ReviewInfoMaxItems) break;
                    if ((object)tmp == null) continue;
                    try
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (!string.IsNullOrWhiteSpace(t) && t.Length > 1)
                            items.Add(t);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== Structured Review (result/detail screens) =====

        /// <summary>
        /// Read specific TMP fields from known result/detail handler types.
        /// Returns structured, labeled information instead of raw TMP dump.
        /// </summary>
        private void CollectStructuredReviewItems(List<string> items)
        {
            if (_activeHandlerType == null || (object)_activeHandler == null) return;

            try
            {
                if (_activeHandlerType == "ActionResultUIHandler")
                    CollectActionResultItems(items);
                else if (_activeHandlerType == "LvUpUIHandler")
                    CollectLvUpItems(items);
                else if (_activeHandlerType == "AceBonusUIHandler")
                    CollectAceBonusItems(items);
                else if (_activeHandlerType == "BattleCheckMenuHandler")
                    CollectBattleCheckItems(items);
                else if (_activeHandlerType == "CharacterSelectionUIHandler")
                    CollectCharacterSelectionItems(items);
                else if (_activeHandlerType == "BonusUIHandler")
                    CollectBonusItems(items);
                else if (_activeHandlerType == "CreditWindowUIHandler")
                    CollectCreditItems(items);
                else if (_activeHandlerType == "AssistLinkManager")
                    CollectAssistLinkItems(items);
                else if (_activeHandlerType == "SurvivalMissionResultUIHandler"
                    || _activeHandlerType == "MissionChartUIHandler")
                    CollectInfoScreenItems(items);
            }
            catch { }
        }

        private void CollectActionResultItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<ActionResultUIHandler>();
            if ((object)handler == null) return;

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try { var tmp = handler.level; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_level", t)); } } catch { }
            try { var tmp = handler.gainExp; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add("EXP +" + t); } } catch { }
            try { var tmp = handler.gainScore; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_score", t)); } } catch { }
            try { var tmp = handler.gainCredit; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_credits", t)); } } catch { }

            // EXP progress (current/max)
            try
            {
                string expCur = ReadTmpSafe(handler.expTop);
                string expMax = ReadTmpSafe(handler.expBottom);
                if (!string.IsNullOrWhiteSpace(expCur) || !string.IsNullOrWhiteSpace(expMax))
                    items.Add("EXP " + (expCur ?? "") + "/" + (expMax ?? ""));
            }
            catch { }
        }

        private void CollectLvUpItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<LvUpUIHandler>();
            if ((object)handler == null) return;

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try
            {
                var bLv = handler.beforeLevel;
                var nLv = handler.nowLevel;
                string before = ((object)bLv != null) ? TextUtils.CleanRichText(bLv.text) : "";
                string now = ((object)nLv != null) ? TextUtils.CleanRichText(nLv.text) : "";
                if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(now))
                    items.Add(Loc.Get("review_lvup_level", before, now));
            }
            catch { }

            // Stat parameters: melee, ranged, defend, hit, evade, skill, sp
            CollectLvUpParam(items, handler.melee, Loc.Get("stat_melee"));
            CollectLvUpParam(items, handler.ranged, Loc.Get("stat_ranged"));
            CollectLvUpParam(items, handler.defend, Loc.Get("stat_defend"));
            CollectLvUpParam(items, handler.hit, Loc.Get("stat_hit"));
            CollectLvUpParam(items, handler.evade, Loc.Get("stat_evade"));
            CollectLvUpParam(items, handler.skill, Loc.Get("stat_skill"));
            CollectLvUpParam(items, handler.sp, "SP");

            // Newly acquired spirit commands
            try
            {
                var spirits = handler.spiritCommands;
                if ((object)spirits != null && spirits.Count > 0)
                {
                    for (int i = 0; i < spirits.Count; i++)
                    {
                        try
                        {
                            var data = spirits[i];
                            if ((object)data == null) continue;
                            string name = ReadTmpSafe(data.name);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                bool isNew = false;
                                try { var icon = data.newIcon; if ((object)icon != null) isNew = icon.activeInHierarchy; } catch { }
                                if (isNew)
                                    items.Add(Loc.Get("lvup_new_spirit") + " " + name);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Newly acquired skills
            try
            {
                var skillList = handler.skills;
                if ((object)skillList != null && skillList.Count > 0)
                {
                    for (int i = 0; i < skillList.Count; i++)
                    {
                        try
                        {
                            var data = skillList[i];
                            if ((object)data == null) continue;
                            string name = ReadTmpSafe(data.name);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                bool isNew = false;
                                try { var icon = data.newIcon; if ((object)icon != null) isNew = icon.activeInHierarchy; } catch { }
                                if (isNew)
                                    items.Add(Loc.Get("lvup_new_skill") + " " + name);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void CollectLvUpParam(List<string> items, LvUpUIHandler.Parameter param, string label)
        {
            if ((object)param == null) return;
            try
            {
                string before = "", now = "";
                try { var tmp = param.before; if ((object)tmp != null) before = TextUtils.CleanRichText(tmp.text) ?? ""; } catch { }
                try { var tmp = param.now; if ((object)tmp != null) now = TextUtils.CleanRichText(tmp.text) ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(now))
                {
                    if (before != now)
                        items.Add($"{label}: {before} → {now}");
                    else if (!string.IsNullOrWhiteSpace(before))
                        items.Add($"{label}: {before}");
                }
            }
            catch { }
        }

        private void CollectAceBonusItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<AceBonusUIHandler>();
            if ((object)handler == null) return;

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try { var tmp = handler.bonusDescription; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
        }

        private void CollectCharacterSelectionItems(List<string> items)
        {
            CollectInfoScreenItems(items);
        }

        /// <summary>
        /// Read battle check (preparation) screen data: both units' combat predictions.
        /// BattleCheckMenuHandler has leftUnitInfo/rightUnitInfo sub-handlers with
        /// detailed TMP fields for each combatant.
        /// </summary>
        private void CollectBattleCheckItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<BattleCheckMenuHandler>();
            if ((object)handler == null) return;

            // Left (attacker) unit info
            try
            {
                var left = handler.leftUnitInfo;
                if ((object)left != null)
                    CollectBattleCheckUnitInfo(items, left, Loc.Get("battle_left"));
            }
            catch { }

            // Right (defender) unit info
            try
            {
                var right = handler.rightUnitInfo;
                if ((object)right != null)
                    CollectBattleCheckUnitInfo(items, right, Loc.Get("battle_right"));
            }
            catch { }

            // Left support
            try
            {
                var leftSup = handler.leftSupportInfo;
                if ((object)leftSup != null)
                    CollectBattleCheckSupportInfo(items, leftSup, Loc.Get("battle_left"));
            }
            catch { }

            // Right support
            try
            {
                var rightSup = handler.rightSupportInfo;
                if ((object)rightSup != null)
                    CollectBattleCheckSupportInfo(items, rightSup, Loc.Get("battle_right"));
            }
            catch { }
        }

        private static void CollectBattleCheckUnitInfo(List<string> items, BattleCheckMenuUnitInfoHandler info, string side)
        {
            try
            {
                // Unit identity: robot name, pilot name, level
                string robot = ReadTmpSafe(info.robotName);
                string pilot = ReadTmpSafe(info.pilotName);
                string level = ReadTmpSafe(info.pilotLevel);
                if (!string.IsNullOrWhiteSpace(robot) || !string.IsNullOrWhiteSpace(pilot))
                {
                    string identity = robot ?? "";
                    if (!string.IsNullOrWhiteSpace(pilot))
                        identity += " (" + pilot + (string.IsNullOrWhiteSpace(level) ? "" : " Lv." + level) + ")";
                    items.Add(side + ": " + identity);
                }

                // HP/EN/SP
                string hp = ReadTmpSafe(info.hp);
                string en = ReadTmpSafe(info.en);
                string sp = ReadTmpSafe(info.sp);
                string maxSp = ReadTmpSafe(info.maxSp);
                var stats = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(hp)) stats.Add("HP " + hp);
                if (!string.IsNullOrWhiteSpace(en)) stats.Add("EN " + en);
                if (!string.IsNullOrWhiteSpace(sp))
                {
                    string spText = "SP " + sp;
                    if (!string.IsNullOrWhiteSpace(maxSp)) spText += "/" + maxSp;
                    stats.Add(spText);
                }
                if (stats.Count > 0)
                    items.Add(string.Join(", ", stats));

                // Weapon + action type
                string weapon = ReadTmpSafe(info.weaponName);
                string action = ReadTmpSafe(info.attackOrDefence);
                if (!string.IsNullOrWhiteSpace(weapon))
                {
                    string weaponLine = weapon;
                    if (!string.IsNullOrWhiteSpace(action)) weaponLine += " [" + action + "]";
                    // Weapon cost (EN/ammo)
                    string costType = ReadTmpSafe(info.ENBulletType);
                    string cost = ReadTmpSafe(info.weaponCost);
                    if (!string.IsNullOrWhiteSpace(cost) && !string.IsNullOrWhiteSpace(costType))
                        weaponLine += " " + costType + ":" + cost;
                    items.Add(weaponLine);
                }

                // Combat predictions: hit rate, damage, critical, attack power
                string hit = ReadTmpSafe(info.hitRate);
                string damage = ReadTmpSafe(info.weaponDamage);
                string crit = ReadTmpSafe(info.criticalRate);
                string atk = ReadTmpSafe(info.attackPower);
                var combat = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(hit)) combat.Add(Loc.Get("battle_hit_rate") + " " + hit);
                if (!string.IsNullOrWhiteSpace(damage)) combat.Add(Loc.Get("battle_damage") + " " + damage);
                if (!string.IsNullOrWhiteSpace(crit)) combat.Add(Loc.Get("battle_critical") + " " + crit);
                if (!string.IsNullOrWhiteSpace(atk)) combat.Add(Loc.Get("battle_attack_power") + " " + atk);
                if (combat.Count > 0)
                    items.Add(string.Join(", ", combat));

                // Terrain
                string terrain = ReadTmpSafe(info.terrainAttribute);
                if (!string.IsNullOrWhiteSpace(terrain))
                    items.Add(Loc.Get("battle_terrain") + " " + terrain);
            }
            catch { }
        }

        private static void CollectBattleCheckSupportInfo(List<string> items, BattleCheckMenuSupportInfoHandler sup, string side)
        {
            try
            {
                string weapon = ReadTmpSafe(sup.weaponName);
                string hit = ReadTmpSafe(sup.hitRate);
                if (!string.IsNullOrWhiteSpace(weapon) || !string.IsNullOrWhiteSpace(hit))
                {
                    string line = Loc.Get("battle_support") + " " + side;
                    if (!string.IsNullOrWhiteSpace(weapon)) line += ": " + weapon;
                    if (!string.IsNullOrWhiteSpace(hit)) line += " " + Loc.Get("battle_hit_rate") + " " + hit;
                    items.Add(line);
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
            try
            {
                string t = TextUtils.CleanRichText(tmp.text);
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch { return null; }
        }

        // ===== Supplementary Info =====

        /// <summary>
        /// Add extra context info for specific handler types.
        /// Called after menu items are collected, adds additional data
        /// like shop prices, item descriptions, etc.
        /// </summary>
        private void CollectSupplementaryItems(List<string> items)
        {
            if (_activeHandlerType == null || (object)_activeHandler == null) return;

            try
            {
                if (_activeHandlerType == "ShopUIHandler")
                {
                    var handler = _activeHandler.TryCast<ShopUIHandler>();
                    if ((object)handler == null) return;

                    try { var tmp = handler.explanationText; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
                    try { var tmp = handler.totalPriceText; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_total_price", t)); } } catch { }
                    try { var tmp = handler.remainCreditText; if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_remaining_credits", t)); } } catch { }
                }
                else if (_activeHandlerType == "WeaponListHandler")
                {
                    CollectWeaponDetailItems(items);
                }
                else if (_activeHandlerType == "TacticalPartSpiritUIHandler")
                {
                    CollectSpiritInfoItems(items);
                }
                else if (_activeHandlerType == "PartsEquipUIHandler")
                {
                    CollectPartsEquipItems(items);
                }
                else if (_activeHandlerType == "SelectPartsUIHandler")
                {
                    CollectSelectPartsItems(items);
                }
                else if (_activeHandlerType == "SaveLoadUIHandler")
                {
                    CollectSaveLoadInfo(items);
                }
            }
            catch { }
        }

        /// <summary>
        /// Read weapon detail info from WeaponListHandler.infoHandler.
        /// Shows hit/crit corrections, morale, required skill, weapon effects.
        /// </summary>
        private void CollectWeaponDetailItems(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<WeaponListHandler>();
                if ((object)handler == null) return;

                // Weapon info panel
                try
                {
                    var info = handler.infoHandler;
                    if ((object)info != null)
                    {
                        // Hit/Crit rate correction
                        string hit = ReadTmpSafe(info.hitRateCorrection);
                        string crit = ReadTmpSafe(info.crtRateCorrection);
                        var corrections = new List<string>();
                        if (!string.IsNullOrWhiteSpace(hit)) corrections.Add(Loc.Get("battle_hit_rate") + " " + hit);
                        if (!string.IsNullOrWhiteSpace(crit)) corrections.Add(Loc.Get("battle_critical") + " " + crit);
                        if (corrections.Count > 0)
                            items.Add(string.Join(", ", corrections));

                        // Morale
                        string currentMorale = ReadTmpSafe(info.currentMoraleNumText);
                        string maxMorale = ReadTmpSafe(info.maxMoraleNumText);
                        if (!string.IsNullOrWhiteSpace(currentMorale) || !string.IsNullOrWhiteSpace(maxMorale))
                            items.Add(Loc.Get("weapon_morale") + " " + (currentMorale ?? "") + "/" + (maxMorale ?? ""));
                        else
                        {
                            string morale = ReadTmpSafe(info.moraleNum);
                            if (!string.IsNullOrWhiteSpace(morale))
                                items.Add(Loc.Get("weapon_morale") + " " + morale);
                        }

                        // Required skill
                        string skill = ReadTmpSafe(info.needSkill);
                        if (!string.IsNullOrWhiteSpace(skill))
                            items.Add(Loc.Get("weapon_required_skill") + " " + skill);

                        // Not support attack warning
                        try
                        {
                            var nsaGo = info.notSupportAttack;
                            if ((object)nsaGo != null && nsaGo.activeInHierarchy)
                            {
                                string nsaText = ReadTmpSafe(info.notSupportAttackText);
                                if (!string.IsNullOrWhiteSpace(nsaText))
                                    items.Add(nsaText);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Weapon effects
                try
                {
                    var effectDetail = handler.weaponEffectDetail;
                    if ((object)effectDetail != null)
                    {
                        var effects = effectDetail.givenEffects;
                        if ((object)effects != null && effects.Count > 0)
                        {
                            for (int i = 0; i < effects.Count && i < 10; i++)
                            {
                                try
                                {
                                    var effect = effects[i];
                                    if ((object)effect == null) continue;
                                    string desc = ReadTmpSafe(effect.effectDescriptionText);
                                    if (!string.IsNullOrWhiteSpace(desc))
                                        items.Add(desc);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read spirit info page: pilot SP, spirit cost, morale, description.
        /// </summary>
        private void CollectSpiritInfoItems(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<TacticalPartSpiritUIHandler>();
                if ((object)handler == null) return;

                var infoPage = handler.spiritInfoPageHandler;
                if ((object)infoPage == null) return;

                // Pilot info
                string pilot = ReadTmpSafe(infoPage.pilotNameSpirit);
                string level = ReadTmpSafe(infoPage.levelSpirit);
                if (!string.IsNullOrWhiteSpace(pilot))
                {
                    string pilotInfo = pilot;
                    if (!string.IsNullOrWhiteSpace(level)) pilotInfo += " Lv." + level;
                    items.Add(pilotInfo);
                }

                // SP
                string sp = ReadTmpSafe(infoPage.spSpirit);
                string maxSp = ReadTmpSafe(infoPage.maxSpSpirit);
                if (!string.IsNullOrWhiteSpace(sp))
                {
                    string spText = "SP " + sp;
                    if (!string.IsNullOrWhiteSpace(maxSp)) spText += "/" + maxSp;
                    items.Add(spText);
                }

                // Spirit cost
                string cost = ReadTmpSafe(infoPage.spiritCost);
                if (!string.IsNullOrWhiteSpace(cost))
                    items.Add(Loc.Get("spirit_cost") + " " + cost);

                // Morale
                string morale = ReadTmpSafe(infoPage.moraleSpirit);
                if (!string.IsNullOrWhiteSpace(morale))
                    items.Add(Loc.Get("weapon_morale") + " " + morale);

                // HP/EN
                string hp = ReadTmpSafe(infoPage.hpSpirit);
                string en = ReadTmpSafe(infoPage.enSpirit);
                var stats = new List<string>();
                if (!string.IsNullOrWhiteSpace(hp)) stats.Add("HP " + hp);
                if (!string.IsNullOrWhiteSpace(en)) stats.Add("EN " + en);
                if (stats.Count > 0)
                    items.Add(string.Join(", ", stats));

                // Spirit text list (may contain description, target, etc.)
                try
                {
                    var textList = infoPage.spiritTextList;
                    if ((object)textList != null && textList.Count > 0)
                    {
                        for (int i = 0; i < textList.Count && i < 5; i++)
                        {
                            try
                            {
                                string t = ReadTmpSafe(textList[i]);
                                if (!string.IsNullOrWhiteSpace(t))
                                    items.Add(t);
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
        /// Read bonus display: pilot name and bonus description.
        /// </summary>
        private void CollectBonusItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<BonusUIHandler>();
            if ((object)handler == null) return;

            try { string t = ReadTmpSafe(handler.PilotName); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } catch { }
            try { string t = ReadTmpSafe(handler.BonusText); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } catch { }
        }

        /// <summary>
        /// Read game clear credits text.
        /// </summary>
        private void CollectCreditItems(List<string> items)
        {
            var handler = _activeHandler.TryCast<CreditWindowUIHandler>();
            if ((object)handler == null) return;

            try
            {
                string t = ReadTmpSafe(handler.creditText);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    if (t.Length > 500) t = t.Substring(0, 500) + "...";
                    items.Add(t);
                }
            }
            catch { }
        }

        /// <summary>
        /// Read assist link detail info for screen review.
        /// Reads directly from AssistLinkManager's detail panel TMP text fields
        /// (command_name_text, command_effect_text, passive_effect_text, etc.)
        /// which the game populates when the player selects an item.
        /// This avoids the GetAssistLinkData mutable/reused data issue entirely.
        /// </summary>
        private void CollectAssistLinkItems(List<string> items)
        {
            try
            {
                var alm = _activeHandler.TryCast<AssistLinkManager>();
                if ((object)alm == null) return;
                if (alm.Pointer == IntPtr.Zero) return;

                // Read detail panel TMP fields directly from AssistLinkManager.
                // These are populated by the game's setCommandEffectString() when
                // the user navigates to an item - always up to date, no mutable data issues.

                // Character name from detail panel
                string charaName = null;
                try { var tmp = alm.select_chara_name_text; if ((object)tmp != null) charaName = TextUtils.CleanRichText(tmp.text); } catch { }

                // Command skill name
                string cmdName = null;
                try { var tmp = alm.command_name_text; if ((object)tmp != null) cmdName = TextUtils.CleanRichText(tmp.text); } catch { }

                // Build title: cmdName - charaName Lv.X
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(cmdName))
                {
                    sb.Append(cmdName);
                    if (!string.IsNullOrWhiteSpace(charaName))
                    {
                        sb.Append(" - ");
                        sb.Append(charaName);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(charaName))
                {
                    sb.Append(charaName);
                }

                // Level from detail panel
                string levelText = null;
                try { var tmp = alm.select_chara_level_text; if ((object)tmp != null) levelText = TextUtils.CleanRichText(tmp.text); } catch { }
                if (!string.IsNullOrWhiteSpace(levelText))
                {
                    sb.Append(" ");
                    sb.Append(levelText);
                }

                if (sb.Length > 0)
                    items.Add(sb.ToString());

                // Command effect description
                string cmdEffect = null;
                try { var tmp = alm.command_effect_text; if ((object)tmp != null) cmdEffect = TextUtils.CleanRichText(tmp.text); } catch { }
                if (!string.IsNullOrWhiteSpace(cmdEffect))
                    items.Add(Loc.Get("assistlink_command_effect", cmdEffect));

                // Passive effect description
                string passiveEffect = null;
                try { var tmp = alm.passive_effect_text; if ((object)tmp != null) passiveEffect = TextUtils.CleanRichText(tmp.text); } catch { }
                if (!string.IsNullOrWhiteSpace(passiveEffect))
                    items.Add(Loc.Get("assistlink_passive_effect", passiveEffect));

                // Duration type
                string duration = null;
                try { var tmp = alm.duration_type_text; if ((object)tmp != null) duration = TextUtils.CleanRichText(tmp.text); } catch { }
                if (!string.IsNullOrWhiteSpace(duration))
                    items.Add(Loc.Get("assistlink_duration", duration));

                // Selection count (e.g. "2/3" equipped)
                string selCount = null;
                try { var tmp = alm.NowSelectionCount_text; if ((object)tmp != null) selCount = TextUtils.CleanRichText(tmp.text); } catch { }
                if (!string.IsNullOrWhiteSpace(selCount))
                    items.Add(Loc.Get("assistlink_selection_count", selCount));

                DebugHelper.Write($"AssistLink review (TMP): name=[{charaName}] cmd=[{cmdName}] effect=[{cmdEffect}] passive=[{passiveEffect}] dur=[{duration}]");
            }
            catch { }
        }

        /// <summary>
        /// Read save/load screen caption (Save/Load/Delete mode).
        /// </summary>
        private void CollectSaveLoadInfo(List<string> items)
        {
            var handler = _activeHandler.TryCast<SaveLoadUIHandler>();
            if ((object)handler == null) return;

            try { string t = ReadTmpSafe(handler.CaptionText); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } catch { }
        }

        /// <summary>
        /// Read parts equip description from PartsEquipUIHandler.
        /// Reads the explanation text from the equipment sub-handler panel,
        /// plus remaining/total count for the currently selected part.
        /// </summary>
        private void CollectPartsEquipItems(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<PartsEquipUIHandler>();
                if ((object)handler == null) return;

                // Equipment sub-handler has the explanation/description text
                try
                {
                    var equip = handler.equipmentUIHandler;
                    if ((object)equip != null && equip.Pointer != IntPtr.Zero)
                    {
                        // Part description
                        var explanTmp = equip.explanationText;
                        if ((object)explanTmp != null)
                        {
                            string desc = TextUtils.CleanRichText(explanTmp.text);
                            if (!string.IsNullOrWhiteSpace(desc))
                                items.Add(desc);
                        }

                        // Remaining / total count for current part
                        try
                        {
                            int idx = equip.currentIndex;
                            var filterList = equip.m_PartsDataFilterList;
                            if ((object)filterList != null && idx >= 0 && idx < filterList.Count)
                            {
                                var part = filterList[idx];
                                if ((object)part != null)
                                {
                                    int remain = part.remainCount;
                                    int total = part.count;
                                    items.Add(Loc.Get("parts_count", remain, total));
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read parts selection description from SelectPartsUIHandler (tactical prep).
        /// Finds the SelectPartsExplanation MonoBehaviour and reads its explanText.
        /// Safe: only called on R key press (user-initiated), not every poll.
        /// </summary>
        private void CollectSelectPartsItems(List<string> items)
        {
            try
            {
                // SelectPartsExplanation is a separate MonoBehaviour in the scene
                var expl = UnityEngine.Object.FindObjectOfType<SelectPartsExplanation>();
                if ((object)expl != null && expl.Pointer != IntPtr.Zero)
                {
                    var explanTmp = expl.explanText;
                    if ((object)explanTmp != null)
                    {
                        string desc = TextUtils.CleanRichText(explanTmp.text);
                        if (!string.IsNullOrWhiteSpace(desc))
                            items.Add(desc);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Read TMP text that is NOT inside any Button component.
        /// Captures description text, stat labels, and other non-option info
        /// that supplements the menu items navigated by cursor.
        /// </summary>
        private void CollectNonButtonText(List<string> items)
        {
            if ((object)_activeHandler == null) return;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return;

                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return;

                int charCount = 0;

                foreach (var tmp in tmps)
                {
                    if (items.Count >= ModConfig.ReviewNonButtonMaxItems || charCount >= ModConfig.ReviewNonButtonMaxChars) break;
                    if ((object)tmp == null) continue;
                    try
                    {
                        // Skip TMP components that are inside a Button
                        var parentBtn = tmp.GetComponentInParent<Button>();
                        if ((object)parentBtn != null) continue;

                        string t = TextUtils.CleanRichText(tmp.text);
                        if (string.IsNullOrWhiteSpace(t) || t.Length <= 1) continue;

                        items.Add(t);
                        charCount += t.Length;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== Utility =====

        /// <summary>
        /// Check if text is purely numeric (e.g. "-1", "10", "150").
        /// Used to filter out SP costs and other numeric displays from button text.
        /// </summary>
        private static bool IsNumericOnly(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return false;
            int start = 0;
            if (trimmed[0] == '-') start = 1;
            if (start >= trimmed.Length) return false;
            for (int i = start; i < trimmed.Length; i++)
            {
                if (trimmed[i] < '0' || trimmed[i] > '9') return false;
            }
            return true;
        }
    }
}
