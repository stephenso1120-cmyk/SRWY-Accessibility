using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppCom.BBStudio.SRTeam.UIs.SelectParts;
using Il2CppCom.BBStudio.SRTeam.UIs.SelectSpecialCommand;
using Il2CppCom.BBStudio.SRTeam.UIs.WeaponList;
using Il2CppCom.BBStudio.SRTeam.UIs.StatusUI;
using Il2CppCom.BBStudio.SRTeam.UIs.StatusUI.PilotStatus;
using Il2CppCom.BBStudio.SRTeam.UIs.StatusUI.RobotStatus;
using Il2CppCom.BBStudio.SRTeam.UIs.StatusUI.WeaponStatus;
using Il2CppCom.BBStudio.SRTeam.Manager;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine.EventSystems;

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

        // Init skip: after finding a new handler, skip N cycles before
        // accessing IL2CPP fields. The UI may not be fully initialized
        // on the first few frames (TMP objects being created/populated).
        // The initial announcement happens in the same cycle as finding,
        // but subsequent reads are delayed.
        private int _initSkipCount;

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
            "SimpleBattleHandler", // transient battle animation handler, freed during transitions → AV crash
            "SingleSimpleBattleHandler", // same as SimpleBattleHandler: transient, freed mid-transition → AV crash
            "SubtitleUIHandler", // handled by AdventureDialogueHandler, freed during ADVENTURE->NONE transitions → AV crash
            "OptionUIHandler",  // handled by SystemOptionHandler (column-based cursor + value tracking)
            "OptionUIHandlerV"  // variant of OptionUIHandler
        };

        // Info screen types: read all visible TMP text as fallback
        private static readonly HashSet<string> _infoScreenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StatusUIHandler",
            "TacticalPartStatusUIHandler",
            // CustomRobotUIHandler: now has dedicated handler (UpdateCustomRobotHandler)
            "UpdateUIHandler",
            "RankUpAnimationHandler",
            "ConversionUIHandler",
            "HandoverUIHandler",
            "TransferUIHandler",
            // Newly covered info screens:
            "SaveConfirmDialogUIHandler",  // save confirm dialog with messageText
            "PrologueSystem",              // prologue narrative text
            "FullCustomBonusUIHandler",    // custom bonus choice/description panel
            "CustomBonusEffectUIHandler",  // custom bonus effect display
            // OptionUIHandler/OptionUIHandlerV: now handled by SystemOptionHandler
            "LibraryPlayerRecordUIHandler", // player records display
            "LicenceWindowUIHandler",      // license/legal text scroll
            "DesignWorkUIHandler",         // design work viewer
            "ResultDisplay",               // post-battle result display (war report, bonus)
            "ResultDisplay2",              // post-battle result display (parts, skills)
            "BattleDetailsUIHandler"       // battle detail panel (skills, abilities)
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
            "AssistLinkSystem",
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
        // BattleCheckMenuHandler demoText: shows battle animation on/off status.
        private string _lastDemoText = null;

        // AssistLinkManager cursor tracking: uses CursorInfo.SelectNo instead of
        // currentCursorIndex. Track selection changes for grid-based navigation.
        private int _lastAssistSelectNo = -1;

        // AssistLink data cache: names from TMP (deferred read).
        // Effect text from TMP fields (deferred read).
        private string _cachedALId;
        private string _cachedALPersonName;
        private string _cachedALCommandName;
        private string _cachedALCmdStr;     // populated from TMP in deferred read
        private string _cachedALPassiveStr; // populated from TMP in deferred read
        private string _cachedALLevelText;  // from TMP select_chara_level_text
        private int _cachedALLevel = -1;    // from workCopy (fallback if TMP empty)
        private bool _cachedALRegistered;   // from workCopy

        // Track extra command buttons that cursor doesn't navigate to
        // (e.g. LandformAction in TacticallPartCommandUIHandler)
        private string _lastExtraButtonText;

        // Track TacticallPartCommandUIHandler mode (map menu vs unit commands)
        private bool _isMapMenu;
        private int _lastCommandType = -1; // ShowCommandType enum value

        // PilotTrainingUIHandler tracking: uses sub-handler cursors, not
        // UIHandlerBase.currentCursorIndex. Track tab (menuType), list type
        // (currentSelectType), and cursor (m_CurrentIndex) separately.
        private int _lastTrainingMenuType = -2; // -2 = unset (MenuType.Default = -1)
        private int _lastTrainingSelectType = -1;
        private int _lastTrainingCursorIndex = -1;
        private IntPtr _lastTrainingPilotPtr = IntPtr.Zero; // Q/E pilot switch detection

        // PartsEquip slot tracking: track slot index and select type separately
        // from the parts list cursor (currentIndex / _lastCursorIndex).
        private int _lastPartsSlotIndex = -1;
        private int _lastPartsSelectType = -1; // 0=Equiped, 1=Select
        private IntPtr _lastPartsRobotPtr = IntPtr.Zero; // Q/E robot switch detection

        // StatusUIHandler tab tracking: PILOT=0, ROBOT=1, WEAPON=2
        private int _lastStatusUIType = -2; // -2 = unset

        // CharacterSelectionUIHandler tracking: selectCharacter (BOY=0/GIRL=1) + stateType
        private int _lastCharSelectCharacter = -1;
        private int _lastCharSelectState = -1;
        private int _charSelectReadDelay;  // deferred read: wait for animation to update TMP

        // CustomRobotUIHandler tracking: robot index for Q/E switch detection
        private int _lastCustomRobotIndex = -1;
        private IntPtr _lastSelectedButtonPtr = IntPtr.Zero;

        // SearchUnitTopUIHandler tracking: mode + indices for Category/Item/Result
        private int _lastSearchSelectMode = -1;  // SelectMode enum
        private int _lastSearchCategory = -1;    // searchCategory enum
        private int _lastSearchItemIndex = -1;   // CurrentItemIndex[currentCategory]
        private int _lastSearchResult = -1;      // SearchResult enum (Pilot/Robot)
        private int _lastSearchResultIndex = -1; // CurrentResultIndex

        // Deferred description read: the game updates TMP description text
        // (explanationText, descriptionText) AFTER our hook runs in
        // InputManager.Update(). Reading immediately on cursor change gets
        // stale text. Use a countdown timer so the game has time to update.
        // Also handles rapid navigation: repeated cursor changes reset the
        // timer, so only the final position's description is read.
        private int _descriptionDelay;       // countdown: >0 = waiting, fires at 0
        private int _descriptionRetries;     // retry count if read returns empty
        private int _descriptionCursorIndex; // cursor index for shop item detail

        // Sort/filter text tracking: monitor TMP text from list handlers
        // and announce when the sort/filter type changes (e.g., G key press).
        private TextMeshProUGUI _sortTmp;
        private TextMeshProUGUI _filterTmp;
        private string _lastSortText = "";
        private string _lastFilterText = "";

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
            _initSkipCount = 0;
            _cmdDiagDumped = false;
            _lastExtraButtonText = null;
            _isMapMenu = false;
            _lastCommandType = -1;
            _lastSpiritBtnPtr = IntPtr.Zero;
            _lastSpiritPilotIndex = -1;
            _lastOthersCmdBtnPtr = IntPtr.Zero;
            _lastBattleCheckBtnType = -1;
            _lastDemoText = null;
            _lastAssistSelectNo = -1;
            _stalePollCount = 0;
            _cachedALId = null;
            _cachedALPersonName = null;
            _cachedALCommandName = null;
            _cachedALCmdStr = null;
            _cachedALPassiveStr = null;
            _cachedALLevelText = null;
            _cachedALLevel = -1;
            _cachedALRegistered = false;
            _lastTrainingMenuType = -2;
            _lastTrainingSelectType = -1;
            _lastTrainingCursorIndex = -1;
            _lastTrainingPilotPtr = IntPtr.Zero;
            _lastPartsSlotIndex = -1;
            _lastPartsSelectType = -1;
            _lastPartsRobotPtr = IntPtr.Zero;
            _lastStatusUIType = -2;
            _lastCharSelectCharacter = -1;
            _lastCharSelectState = -1;
            _charSelectReadDelay = 0;
            _lastCustomRobotIndex = -1;
            _lastSelectedButtonPtr = IntPtr.Zero;
            _descriptionDelay = 0;
            _descriptionRetries = 0;
            _sortTmp = null;
            _filterTmp = null;
            _lastSortText = "";
            _lastFilterText = "";
        }

        /// <summary>
        /// Force re-announcement on next poll cycle. Resets cursor tracking
        /// but keeps the active handler cached. Used when Q/E (L1/R1) tab
        /// switching changes content without moving the cursor.
        /// </summary>
        public void ForceReannounce()
        {
            _lastCursorIndex = -1;
            _stalePollCount = 0;
            _lastBattleCheckBtnType = -1;
            _lastDemoText = null;
            _lastSpiritBtnPtr = IntPtr.Zero;
            _lastOthersCmdBtnPtr = IntPtr.Zero;
            _lastAssistSelectNo = -1;
            _lastCommandType = -1;
            _typeSpecificCount = -1;
            _lastTrainingMenuType = -2;
            _lastTrainingSelectType = -1;
            _lastTrainingCursorIndex = -1;
            _lastPartsSlotIndex = -1;
            _lastPartsSelectType = -1;
            // Keep _lastPartsRobotPtr: don't reset so Q/E robot switch is detected
            _lastStatusUIType = -2;
            _lastCharSelectCharacter = -1;
            _lastCharSelectState = -1;
            _charSelectReadDelay = 0;
            _lastCustomRobotIndex = -1;
            _lastSelectedButtonPtr = IntPtr.Zero;
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

            // SEH: Verify handler's native object is still accessible before any
            // IL2CPP access. IsHandlerStillActive only checks InputManager's behaviour
            // pointer, not the handler object itself. During transitions, the handler's
            // native memory can be freed before the behaviour pointer updates.
            if (!SafeCall.ProbeObject(_activeHandler.Pointer))
            {
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] ProbeObject failed - handler freed");
                ReleaseHandler();
                return true;
            }

            // Init skip: after finding a new handler, skip a few cycles before
            // doing further IL2CPP reads. The initial announcement already happened
            // (in the same cycle as finding via _newHandlerJustFound), but the UI
            // may still be initializing TMP objects on subsequent frames.
            // ResultDisplay2 and other info screens crash on the 2nd cycle.
            if (_initSkipCount > 0)
            {
                _initSkipCount--;
                return false;
            }

            // Deferred description read: countdown timer gives the game multiple
            // frames to update TMP description text after cursor change.
            // Rapid navigation resets the timer, reading only the final position.
            if (_descriptionDelay > 0)
            {
                _descriptionDelay--;
                if (_descriptionDelay == 0)
                {
                    bool ok = ReadDeferredDescription();
                    // Retry if the read returned empty (game may need more time).
                    // AssistLink uses multi-phase deferred reads, needs more retries.
                    if (!ok && _descriptionRetries < 5)
                    {
                        _descriptionDelay = 1; // retry next cycle
                        _descriptionRetries++;
                    }
                }
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

            // Special: SearchUnitTopUIHandler has three modes (Category/Item/Result).
            // Each mode uses different indices and handlers.
            if (_activeHandlerType == "SearchUnitTopUIHandler")
                return UpdateSearchHandler();

            // Special: TacticalOthersCommandUIHandler uses same SpiritButtonHandler
            // pattern. Track CurrentOtherCommandBtnHandler pointer changes.
            if (_activeHandlerType == "TacticalOthersCommandUIHandler")
                return UpdateOthersCommandHandler();

            // Special: BattleCheckMenuHandler doesn't use currentCursorIndex.
            // Track curBattleCheckMenuButtonType changes instead.
            if (_activeHandlerType == "BattleCheckMenuHandler")
                return UpdateBattleCheckHandler();

            // Special: AssistLinkManager/AssistLinkSystem uses CursorInfo.SelectNo
            // for grid navigation, not currentCursorIndex from UIHandlerBase.
            // AssistLinkSystem is the battle-time wrapper around AssistLinkManager.
            if (_activeHandlerType == "AssistLinkManager" || _activeHandlerType == "AssistLinkSystem")
                return UpdateAssistLinkHandler();

            // Special: PartsEquipUIHandler doesn't update currentCursorIndex.
            // Track equipmentUIHandler.currentIndex instead.
            if (_activeHandlerType == "PartsEquipUIHandler")
                return UpdatePartsEquipHandler();

            // Special: CharacterSelectionUIHandler (角色選擇) has two states:
            // CHARACTERSELECTION (boy/girl) and CHARACTERSETTINGS (name/birthday/blood/confirm).
            // Track EventSystem selection changes to read focused button text.
            if (_activeHandlerType == "CharacterSelectionUIHandler")
                return UpdateCharacterSelectionHandler();

            // Special: CustomRobotUIHandler (機體改造) has robot switching (Q/E)
            // and stat modification options. Track robot index changes and
            // read CustomButton stat labels + values.
            if (_activeHandlerType == "CustomRobotUIHandler")
                return UpdateCustomRobotHandler();

            // Special: StatusUIHandler has tabs (Pilot/Robot/Weapon).
            // On the Weapon tab, track cursor changes and read weapon details
            // instead of dumping all TMP text.
            // TacticalPartStatusUIHandler extends StatusUIHandler (tactical map F key).
            if (_activeHandlerType == "StatusUIHandler" || _activeHandlerType == "TacticalPartStatusUIHandler")
                return UpdateStatusUIHandler();

            // Special: PilotTrainingUIHandler uses sub-handler cursors
            // (statusHandler.m_CurrentIndex / paramHandler.m_CurrentIndex),
            // not UIHandlerBase.currentCursorIndex. Multiple list handlers
            // exist; we switch between them based on currentSelectType.
            if (_activeHandlerType == "PilotTrainingUIHandler")
                return UpdatePilotTrainingHandler();

            // Read cursor index (IL2CPP access - but we just validated above)
            int currentIndex;
            if (SafeCall.FieldsAvailable)
            {
                var (ok, idx) = SafeCall.ReadCursorIndexSafe(_activeHandler.Pointer);
                if (!ok) { ReleaseHandler(); return true; }
                currentIndex = idx;
            }
            else
            {
                try { currentIndex = _activeHandler.currentCursorIndex; }
                catch { ReleaseHandler(); return true; }
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
            if (_stalePollCount % 3 == 0 && IsHandlerStillActive()
                && SafeCall.ProbeObject(_activeHandler.Pointer))
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

                // SEH-protected read: the IL2CPP property getter for
                // curBattleCheckMenuButtonType dereferences native pointers
                // that can be invalid on partially-freed objects during scene
                // transitions, causing uncatchable AccessViolationException.
                int btnType;
                if (SafeCall.BattleCheckFieldAvailable)
                {
                    var (ok, val) = SafeCall.ReadBattleCheckBtnTypeSafe(handler.Pointer);
                    if (!ok)
                    {
                        DebugHelper.Write("GenericMenu: [BattleCheckMenuHandler] SEH read failed - handler freed");
                        ReleaseHandler();
                        return true;
                    }
                    btnType = val;
                }
                else
                {
                    btnType = (int)handler.curBattleCheckMenuButtonType;
                }

                // Check demoText (battle animation on/off) every poll
                try
                {
                    var demoTmp = handler.demoText;
                    if ((object)demoTmp != null && demoTmp.Pointer != IntPtr.Zero)
                    {
                        var demoStrPtr = SafeCall.ReadTmpTextSafe(demoTmp.Pointer);
                        string demoVal = (demoStrPtr != IntPtr.Zero) ? SafeCall.SafeIl2CppStringToManaged(demoStrPtr) : null;
                        if (!string.IsNullOrEmpty(demoVal) && demoVal != _lastDemoText)
                        {
                            bool isFirstDemo = _lastDemoText == null;
                            _lastDemoText = demoVal;
                            if (!isFirstDemo)
                            {
                                // Announce battle animation toggle change
                                string announcement = Loc.Get("battle_anim_label") + demoVal;
                                ScreenReaderOutput.Say(announcement);
                                DebugHelper.Write($"GenericMenu: demoText changed to '{demoVal}'");
                            }
                            else
                                DebugHelper.Write($"GenericMenu: demoText initial='{demoVal}'");
                        }
                    }
                }
                catch { }

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
                string leftSummary = ReadParamPrediction(handler.baseLeftMainParam, Loc.Get("battle_left"));
                string rightSummary = ReadParamPrediction(handler.baseRightMainParam, Loc.Get("battle_right"));

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

        private static string ReadParamPrediction(BattleCheckMenuParametor param, string side)
        {
            if ((object)param == null) return null;
            try
            {
                string robot = param.robotName ?? "";
                string weapon = param.weaponName;
                if (!string.IsNullOrWhiteSpace(weapon))
                    robot += " " + weapon;

                if (string.IsNullOrWhiteSpace(robot)) return null;

                return Loc.Get("battle_prediction",
                    side, robot.Trim(),
                    param.hitRate.ToString(),
                    param.predictDamage.ToString(),
                    param.criticalRate.ToString());
            }
            catch { return null; }
        }

        /// <summary>
        /// Read deferred description text. Called after countdown timer reaches 0,
        /// giving the game time to update TMP description fields.
        /// Tries handler-specific readers first, then falls back to generic
        /// GuideUIHandler.m_GuideText (universal help text shown in most menus).
        /// Returns true if text was successfully read and announced, false if empty.
        /// </summary>
        private bool ReadDeferredDescription()
        {
            try
            {
                if ((object)_activeHandler == null) return true; // no handler, don't retry

                string descText = null;

                // Handler-specific description readers only.
                // Only handlers with known description TMP fields are supported.
                if (_activeHandlerType == "MissionUIHandler")
                    descText = ReadMissionDetailInfoText();
                else if (_activeHandlerType == "PilotTrainingUIHandler")
                    descText = ReadPilotTrainingDescriptionText();
                else if (_activeHandlerType == "ShopUIHandler")
                    descText = ReadShopDescriptionText();
                else if (_activeHandlerType == "SelectDogmaUIHandler"
                    || _activeHandlerType == "SelectTacticalCommandUIHandler"
                    || _activeHandlerType == "SelectSpecialCommandUIHandler")
                    descText = ReadSpecialCommandDescriptionText();
                else if (_activeHandlerType == "PartsEquipUIHandler")
                    descText = ReadPartsEquipDescriptionText();
                else if (_activeHandlerType == "TacticalPartSpiritUIHandler")
                    descText = ReadSpiritDescriptionText();
                else if (_activeHandlerType == "AssistLinkManager" || _activeHandlerType == "AssistLinkSystem")
                {
                    // Two-phase deferred read: names first, then effects.
                    // Phase 1: names haven't been read yet (null).
                    // Phase 2: names read, now read effects.
                    if (_cachedALCommandName == null)
                    {
                        // Phase 1: read names from TMP (game has updated after delay)
                        descText = ReadAssistLinkNamesFromTmp();
                        if (!string.IsNullOrWhiteSpace(descText))
                        {
                            ScreenReaderOutput.Say(descText);
                            // Schedule phase 2 for effects
                            _descriptionDelay = 2;
                            _descriptionRetries = 0;
                            return true;
                        }
                        return false; // retry
                    }
                    else
                    {
                        // Phase 2: read effects from TMP
                        descText = ReadAssistLinkDescriptionText();
                    }
                }
                else
                    return true; // no reader for this handler type, don't retry

                if (!string.IsNullOrWhiteSpace(descText))
                {
                    ScreenReaderOutput.SayQueued(descText);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Read spirit description/target text from SpiritInfoPageHandler.spiritTextList.</summary>
        private string ReadSpiritDescriptionText()
        {
            try
            {
                var handler = _activeHandler.TryCast<TacticalPartSpiritUIHandler>();
                if ((object)handler == null) return null;

                var infoPage = handler.spiritInfoPageHandler;
                if ((object)infoPage == null) return null;

                var parts = new List<string>();

                // spiritTextList contains rendered TMP elements for
                // spirit description, target, and other detail text.
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
                                    parts.Add(t);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Spirit cost
                try
                {
                    string cost = ReadTmpSafe(infoPage.spiritCost);
                    if (!string.IsNullOrWhiteSpace(cost))
                        parts.Add(Loc.Get("spirit_cost") + " " + cost);
                }
                catch { }

                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>Get the AssistLinkManager from the active handler.
        /// Works for both AssistLinkManager (direct) and AssistLinkSystem (via managerScript).</summary>
        private AssistLinkManager GetActiveAssistLinkManager()
        {
            try
            {
                if (_activeHandlerType == "AssistLinkSystem")
                {
                    var als = _activeHandler.TryCast<AssistLinkSystem>();
                    if ((object)als != null && als.Pointer != IntPtr.Zero)
                        return als.managerScript;
                }
                else
                {
                    return _activeHandler.TryCast<AssistLinkManager>();
                }
            }
            catch { }
            return null;
        }

        /// <summary>Read assist link command/passive effect description.
        /// ALWAYS reads from TMP fields (deferred 3 frames after cursor change).
        /// <summary>
        /// Phase 1 deferred read: read link name and character name from TMP fields.
        /// Called 3 frames after cursor change, when game has updated TMP.
        /// During battle, assist_link_work_copy order doesn't match visual display,
        /// so GetAssistLinkData returns wrong names. TMP is the source of truth.
        /// </summary>
        private string ReadAssistLinkNamesFromTmp()
        {
            try
            {
                var alm = GetActiveAssistLinkManager();
                if ((object)alm == null || alm.Pointer == IntPtr.Zero) return null;

                string cmdName = null, personName = null, levelText = null;
                try { var t = alm.command_name_text; if ((object)t != null) cmdName = ReadTmpSafe(t); } catch { }
                try { var t = alm.select_chara_name_text; if ((object)t != null) personName = ReadTmpSafe(t); } catch { }
                try { var t = alm.select_chara_level_text; if ((object)t != null) levelText = ReadTmpSafe(t); } catch { }
                // Also try select_chara_level_text_in (alternative TMP for level)
                string levelTextIn = null;
                if (string.IsNullOrWhiteSpace(levelText))
                {
                    try { var t = alm.select_chara_level_text_in; if ((object)t != null) { levelTextIn = ReadTmpSafe(t); if (!string.IsNullOrWhiteSpace(levelTextIn)) levelText = levelTextIn; } } catch { }
                }

                DebugHelper.Write($"GenericMenu: [AssistLink TMP] cmd=[{cmdName}] name=[{personName}] lv=[{levelText}] lvIn=[{levelTextIn}]");

                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(cmdName))
                    sb.Append(TextUtils.CleanRichText(cmdName));
                if (!string.IsNullOrWhiteSpace(personName))
                {
                    if (sb.Length > 0) sb.Append(" - ");
                    sb.Append(TextUtils.CleanRichText(personName));
                }

                if (sb.Length == 0) return null; // TMP not ready yet, retry

                // Append level (0-based enum: Lv1=0, Lv2=1, ...; display = level+1)
                // Always use Loc.Get format (TMP lv is bare number without prefix)
                if (_cachedALLevel >= 0)
                {
                    _cachedALLevelText = Loc.Get("assistlink_level", _cachedALLevel + 1);
                    sb.Append(" ");
                    sb.Append(_cachedALLevelText);
                }
                if (_cachedALRegistered)
                {
                    sb.Append(" [");
                    sb.Append(Loc.Get("assistlink_registered"));
                    sb.Append("]");
                }

                // Only cache when we have actual data (keeps _cachedALCommandName
                // null so retries stay in phase 1 until data arrives)
                _cachedALCommandName = !string.IsNullOrWhiteSpace(cmdName) ? TextUtils.CleanRichText(cmdName) : "";
                _cachedALPersonName = !string.IsNullOrWhiteSpace(personName) ? TextUtils.CleanRichText(personName) : "";

                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>Phase 2 deferred read: effects from TMP.
        /// GetAssistLinkData effect fields (CommandStr, PassiveStr) are STALE during
        /// battle - they contain effects from a previous/different item.
        /// TMP fields are updated by the game's own UI update cycle and are reliable.</summary>
        private string ReadAssistLinkDescriptionText()
        {
            try
            {
                var parts = new List<string>();
                var alm = GetActiveAssistLinkManager();
                if ((object)alm == null || alm.Pointer == IntPtr.Zero) return null;

                // Command effect: ALWAYS read from TMP (reliable after deferred delay)
                string cmdEffect = null;
                try
                {
                    var tmp = alm.command_effect_text;
                    if ((object)tmp != null)
                        cmdEffect = ReadTmpSafe(tmp);
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(cmdEffect))
                {
                    _cachedALCmdStr = cmdEffect; // update cache for screen review
                    parts.Add(Loc.Get("assistlink_command_effect", cmdEffect));
                }

                // Passive effect: ALWAYS read from TMP
                string passiveEffect = null;
                try
                {
                    var tmp = alm.passive_effect_text;
                    if ((object)tmp != null)
                        passiveEffect = ReadTmpSafe(tmp);
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(passiveEffect))
                {
                    _cachedALPassiveStr = passiveEffect; // update cache for screen review
                    parts.Add(Loc.Get("assistlink_passive_effect", passiveEffect));
                }

                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>Read description text from PilotTrainingUIHandler without announcing.</summary>
        /// <summary>
        /// Read mission detail info (description, location, rank) from MissionDetailInfo.
        /// This runs 3 cycles after cursor change, giving the game time to update the TMP text.
        /// </summary>
        private string ReadMissionDetailInfoText()
        {
            try
            {
                // Find MissionDetailInfo object
                var detailInfo = UnityEngine.Object.FindObjectOfType<MissionDetailInfo>();
                if ((object)detailInfo == null)
                    return null;

                if (!SafeCall.ProbeObject(detailInfo.Pointer))
                    return null;

                // Check if data is loading
                bool isLoading = false;
                try { isLoading = detailInfo.MissionDetailLoading; } catch { }
                if (isLoading)
                    return null;

                var parts = new List<string>();

                // Description (main content)
                string desc = ReadTmpSafe(detailInfo.dtDescription);
                if (!string.IsNullOrWhiteSpace(desc))
                    parts.Add(desc);

                // Location
                string loc = ReadTmpSafe(detailInfo.dtPointName);
                if (!string.IsNullOrWhiteSpace(loc))
                    parts.Add(Loc.Get("mission_location") + ": " + loc);

                // Recommended rank
                string rank = ReadTmpSafe(detailInfo.dtRecommendRank);
                if (!string.IsNullOrWhiteSpace(rank))
                    parts.Add(Loc.Get("mission_recommend_rank") + ": " + rank);

                if (parts.Count > 0)
                    return string.Join(". ", parts);

                return null;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: ReadMissionDetailInfoText error: {ex.Message}");
                return null;
            }
        }

        private string ReadPilotTrainingDescriptionText()
        {
            try
            {
                var handler = _activeHandler.TryCast<PilotTrainingUIHandler>();
                if ((object)handler == null) return null;

                var parts = new List<string>();

                try
                {
                    var sh = handler.statusHandler;
                    if ((object)sh != null)
                    {
                        var tmp = sh.explanationText;
                        if ((object)tmp != null)
                        {
                            string t = ReadTmpSafe(tmp);
                            if (!string.IsNullOrWhiteSpace(t))
                                parts.Add(t);
                        }
                    }
                }
                catch { }

                // Learn skill dialog info if showing
                try
                {
                    var dialog = handler.LearnSkillDialog;
                    if ((object)dialog != null)
                    {
                        var dialogGo = dialog.go;
                        if ((object)dialogGo != null && dialogGo.activeInHierarchy)
                        {
                            string skillName = ReadTmpSafe(dialog.learningSkillProgramText);
                            if (!string.IsNullOrWhiteSpace(skillName))
                                parts.Add(Loc.Get("training_learning") + " " + skillName);

                            string needBuy = ReadTmpSafe(dialog.needToBuyText);
                            if (!string.IsNullOrWhiteSpace(needBuy))
                                parts.Add(needBuy);

                            string cost = ReadTmpSafe(dialog.costCreditText);
                            if (!string.IsNullOrWhiteSpace(cost))
                                parts.Add(Loc.Get("training_cost") + " " + cost);
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>Read description text from ShopUIHandler without announcing.</summary>
        private string ReadShopDescriptionText()
        {
            try
            {
                var handler = _activeHandler.TryCast<ShopUIHandler>();
                if ((object)handler == null) return null;

                var parts = new List<string>();

                // Per-item data from ShopListPanel
                try
                {
                    var shopList = handler.shopList;
                    if ((object)shopList != null)
                    {
                        var panels = shopList.GetShopListPanelInstances();
                        if (panels != null)
                        {
                            ShopListPanel panel = null;
                            for (int i = 0; i < panels.Count; i++)
                            {
                                try
                                {
                                    var p = panels[i];
                                    if ((object)p != null && p.currentIndex == _descriptionCursorIndex)
                                    { panel = p; break; }
                                }
                                catch { }
                            }

                            if ((object)panel != null)
                            {
                                string cost = ReadTmpSafe(panel.itemCost);
                                if (!string.IsNullOrWhiteSpace(cost))
                                    parts.Add(Loc.Get("shop_price") + " " + cost);

                                string owned = ReadTmpSafe(panel.itemOwnCnt);
                                if (!string.IsNullOrWhiteSpace(owned))
                                    parts.Add(Loc.Get("shop_owned") + " " + owned);

                                string buyCnt = ReadTmpSafe(panel.itemBuyCnt);
                                if (!string.IsNullOrWhiteSpace(buyCnt) && buyCnt != "0")
                                    parts.Add(Loc.Get("shop_buy_count") + " " + buyCnt);
                            }
                        }
                    }
                }
                catch { }

                // Explanation text
                try
                {
                    string desc = ReadTmpSafe(handler.explanationText);
                    if (!string.IsNullOrWhiteSpace(desc))
                        parts.Add(desc);
                }
                catch { }

                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>Read description text from SelectSpecialCommandUIHandler without announcing.</summary>
        private string ReadSpecialCommandDescriptionText()
        {
            try
            {
                var handler = _activeHandler.TryCast<SelectSpecialCommandUIHandler>();
                if ((object)handler == null) return null;

                var tmp = handler.descriptionText;
                if ((object)tmp == null) return null;

                return ReadTmpSafe(tmp);
            }
            catch { return null; }
        }

        /// <summary>Read description text from PartsEquipUIHandler without announcing.</summary>
        private string ReadPartsEquipDescriptionText()
        {
            try
            {
                var handler = _activeHandler.TryCast<PartsEquipUIHandler>();
                if ((object)handler == null) return null;

                var equipHandler = handler.equipmentUIHandler;
                if ((object)equipHandler == null) return null;

                var tmp = equipHandler.explanationText;
                if ((object)tmp == null) return null;

                return ReadTmpSafe(tmp);
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
        /// Special update path for AssistLinkManager and AssistLinkSystem.
        /// This handler uses CursorInfo.SelectNo for grid-based navigation
        /// instead of UIHandlerBase.currentCursorIndex.
        /// AssistLinkSystem is the battle-time wrapper; its managerScript field
        /// provides access to the underlying AssistLinkManager.
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
                else if (_activeHandlerType == "AssistLinkSystem")
                {
                    // Fall back to AssistLinkManager screen name for battle context
                    screenName = Loc.Get("screen_assistlinkmanager");
                    if (screenName != "screen_assistlinkmanager")
                        ScreenReaderOutput.Say(screenName);
                }
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] assist link handler init");
            }

            try
            {
                var alm = GetActiveAssistLinkManager();
                if ((object)alm == null) return false;
                if (alm.Pointer == IntPtr.Zero) return false;

                var curInfo = alm.curInfo;
                if ((object)curInfo == null) return false;

                int selectNo = curInfo.SelectNo;

                // Detect item change even when cursor position stays the same
                // (e.g. after sorting reorders the list)
                bool positionChanged = selectNo != _lastAssistSelectNo;
                bool itemChanged = false;
                if (!positionChanged && selectNo >= 0)
                {
                    try
                    {
                        var wc = alm.assist_link_work_copy;
                        if ((object)wc != null && selectNo < wc.Count)
                        {
                            var wk = wc[selectNo];
                            if ((object)wk != null)
                            {
                                string currentId = wk.id;
                                if (!string.IsNullOrEmpty(currentId) && currentId != _cachedALId)
                                    itemChanged = true;
                            }
                        }
                    }
                    catch { }
                }

                if (!positionChanged && !itemChanged)
                    return false;

                _lastAssistSelectNo = selectNo;
                _lastCursorIndex = selectNo;

                // Try to get correct level via itemList (visual buttons).
                // workCopy order may NOT match visual display order.
                // itemList is a recycled pool; match by ButtonItem.no == selectNo.
                int correctLevel = -1;
                bool correctRegistered = false;
                string correctId = null;
                try
                {
                    var items = alm.itemList;
                    if ((object)items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            var btn = items[i];
                            if ((object)btn != null && btn.no == selectNo)
                            {
                                correctId = btn.id;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(correctId))
                    {
                        correctLevel = alm.GetLevel(correctId);
                        // Check equipped status via wk.selected (regist/isRegistered are always true)
                        var wkList = alm.assist_link_work_copy;
                        if ((object)wkList != null)
                        {
                            for (int i = 0; i < wkList.Count; i++)
                            {
                                var w = wkList[i];
                                if ((object)w != null && w.id == correctId)
                                {
                                    correctRegistered = w.selected;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GenericMenu: [AL] itemList lookup error: {ex.Message}");
                }

                // Fallback: workCopy[selectNo] (may be wrong order during battle)
                try
                {
                    var workCopy = alm.assist_link_work_copy;
                    if ((object)workCopy != null && selectNo < workCopy.Count)
                    {
                        var wk = workCopy[selectNo];
                        if ((object)wk != null)
                        {
                            _cachedALId = wk.id;
                            if (correctLevel >= 0)
                            {
                                _cachedALLevel = correctLevel;
                                _cachedALRegistered = correctRegistered;
                                DebugHelper.Write($"GenericMenu: [AL] id={correctId} lv={correctLevel} sel={correctRegistered}");
                            }
                            else
                            {
                                _cachedALLevel = wk.level;
                                _cachedALRegistered = wk.selected;
                                DebugHelper.Write($"GenericMenu: [AL] workCopy fallback: wk[{selectNo}].id={wk.id} lv={wk.level} sel={wk.selected}");
                            }
                        }
                    }
                }
                catch { }

                // Defer ALL reading by 2 frames so the game updates TMP first.
                _cachedALCommandName = null;
                _cachedALPersonName = null;
                _cachedALCmdStr = null;
                _cachedALPassiveStr = null;
                _descriptionDelay = 2;
                _descriptionRetries = 0;
                DebugHelper.Write($"GenericMenu: [AssistLink] select={selectNo} deferred to TMP");
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

            // Step 2: Get display name + command name from GetAssistLinkData.
            // Name and CommandName fields are reliable. Effect fields (CommandStr,
            // PassiveStr etc.) are NOT cached here - they are stale during battle.
            // Effects are read from TMP in the deferred description reader.
            if (!string.IsNullOrEmpty(itemId))
            {
                try
                {
                    AssistManager.AssistLinkData data = null;

                    // Primary: AssistManager (data manager) - pure dictionary lookup
                    try
                    {
                        var gm = GameManager.Instance;
                        if ((object)gm != null && gm.Pointer != IntPtr.Zero)
                        {
                            var am = gm.assistManager;
                            if ((object)am != null && am.Pointer != IntPtr.Zero)
                                data = am.GetAssistLinkData(itemId);
                        }
                    }
                    catch { }

                    // Fallback: AssistLinkManager version
                    if ((object)data == null)
                    {
                        try { data = alm.GetAssistLinkData(itemId); }
                        catch { }
                    }

                    if ((object)data != null)
                    {
                        try
                        {
                            string name = data.Name;
                            if (!string.IsNullOrWhiteSpace(name))
                                itemName = TextUtils.CleanRichText(name);
                        }
                        catch { }

                        try
                        {
                            string cmd = data.CommandName;
                            if (!string.IsNullOrWhiteSpace(cmd))
                                commandName = TextUtils.CleanRichText(cmd);
                        }
                        catch { }

                        // Cache name fields only. Effect fields (CommandStr, PassiveStr etc.)
                        // are STALE during battle - they contain data from a previous item.
                        // Effects will be read from TMP in the deferred description reader.
                        _cachedALId = itemId;
                        _cachedALPersonName = itemName;
                        _cachedALCommandName = commandName;
                        // Clear effect caches - will be populated from TMP later
                        _cachedALCmdStr = null;
                        _cachedALPassiveStr = null;
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

                    // Update cache with fallback results
                    _cachedALId = itemId;
                    if (!string.IsNullOrWhiteSpace(itemName))
                        _cachedALPersonName = itemName;
                    if (!string.IsNullOrWhiteSpace(commandName))
                        _cachedALCommandName = commandName;

                    DebugHelper.Write($"AssistLink item: sel={selectNo} id={itemId} Name=[{itemName}] CmdName=[{commandName}] Lv={level} reg={registered}");
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
        /// Dual-mode handling:
        ///   SelectType.Equiped (0): slot view — track selectSlotIndex, read slot name + description
        ///   SelectType.Select (1): parts list — track currentIndex, read part name + count
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

                // Q/E robot switch detection: track currentRobot pointer
                IntPtr robotPtr = IntPtr.Zero;
                try
                {
                    var robot = equipUI.currentRobot;
                    if ((object)robot != null)
                        robotPtr = robot.Pointer;
                }
                catch { }

                if (robotPtr != _lastPartsRobotPtr && _lastPartsRobotPtr != IntPtr.Zero)
                {
                    // Robot changed → announce new robot name, reset slot tracking
                    _lastPartsSlotIndex = -1;
                    _lastCursorIndex = -1;
                    _stalePollCount = 0;

                    string robotName = null;
                    try
                    {
                        var unitBase = equipUI.unitBaseData;
                        if ((object)unitBase != null && unitBase.Pointer != IntPtr.Zero
                            && SafeCall.ProbeObject(unitBase.Pointer))
                        {
                            var nameTmp = unitBase.m_RobotNameText;
                            if ((object)nameTmp != null)
                                robotName = ReadTmpSafe(nameTmp);
                        }
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(robotName))
                    {
                        ScreenReaderOutput.Say(robotName);
                        DebugHelper.Write($"GenericMenu: [PartsEquip] robot switch: {robotName}");
                    }
                }
                _lastPartsRobotPtr = robotPtr;

                int selectType = (int)equipUI.currentSelectType;

                // Mode changed (Equiped ↔ Select): reset cursors
                if (selectType != _lastPartsSelectType)
                {
                    _lastPartsSelectType = selectType;
                    _lastPartsSlotIndex = -1;
                    _lastCursorIndex = -1;
                    _stalePollCount = 0;
                    DebugHelper.Write($"GenericMenu: [PartsEquip] selectType={selectType}");
                }

                if (selectType == 0) // Equiped: slot navigation
                {
                    int slotIndex = equipUI.selectSlotIndex;
                    if (slotIndex != _lastPartsSlotIndex)
                    {
                        _lastPartsSlotIndex = slotIndex;
                        _stalePollCount = 0;

                        string text = ReadPartsSlotText(equipUI, slotIndex);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ScreenReaderOutput.Say(text);
                            _descriptionDelay = 3;
                            _descriptionRetries = 0;
                            _descriptionCursorIndex = slotIndex;
                            DebugHelper.Write($"GenericMenu: [PartsEquip] slot={slotIndex} text={text}");
                        }
                        else
                        {
                            DebugHelper.Write($"GenericMenu: [PartsEquip] slot={slotIndex} no text");
                        }
                    }
                }
                else // Select: parts list navigation (existing behavior)
                {
                    int curIdx = equipUI.currentIndex;
                    if (curIdx != _lastCursorIndex)
                    {
                        _lastCursorIndex = curIdx;
                        _stalePollCount = 0;

                        string text = null;
                        if ((object)_listHandler != null)
                            text = ReadPartsListItemText(curIdx);

                        if (string.IsNullOrWhiteSpace(text))
                            text = ReadListItemText(curIdx);

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ScreenReaderOutput.Say(text);
                            _descriptionDelay = 3;
                            _descriptionRetries = 0;
                            DebugHelper.Write($"GenericMenu: [PartsEquip] list cursor={curIdx} text={text}");
                        }
                        else
                        {
                            DebugHelper.Write($"GenericMenu: [PartsEquip] list cursor={curIdx} no text");
                        }
                    }
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
        /// Special update path for CharacterSelectionUIHandler (角色選擇).
        /// Two states tracked via stateType field:
        ///   CHARACTERSELECTION (0): boy/girl selection via selectCharacter field
        ///   CHARACTERSETTINGS (1): name/birthday/blood/confirm via EventSystem
        ///
        /// CHARACTERSELECTION: selectCharacter (BOY=0, GIRL=1) changes on L1/R1.
        /// Track this field directly; ReadAllVisibleText shows character profile.
        ///
        /// CHARACTERSETTINGS: EventSystem tracks focused setting button.
        /// </summary>
        private bool UpdateCharacterSelectionHandler()
        {
            bool firstEntry = _newHandlerJustFound;
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                _lastSelectedButtonPtr = IntPtr.Zero;
                _lastCharSelectCharacter = -1;
                _lastCharSelectState = -1;
                string screenKey = "screen_characterselectionuihandler";
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write("GenericMenu: [CharacterSelectionUIHandler] init");
            }

            try
            {
                // TryCast to read selectCharacter and stateType fields
                var handler = _activeHandler.TryCast<CharacterSelectionUIHandler>();
                if ((object)handler == null)
                {
                    DebugHelper.Write("GenericMenu: [CharacterSelection] TryCast failed");
                    return false;
                }

                int stateType = (int)handler.stateType;
                int selectChar = (int)handler.selectCharacter;

                // Detect state change (CHARACTERSELECTION ↔ CHARACTERSETTINGS)
                bool stateChanged = (stateType != _lastCharSelectState);
                if (stateChanged)
                {
                    _lastCharSelectState = stateType;
                    _lastSelectedButtonPtr = IntPtr.Zero; // reset EventSystem tracking
                    DebugHelper.Write($"GenericMenu: [CharacterSelection] state changed to {stateType}");
                }

                if (stateType == 0) // CHARACTERSELECTION (boy/girl)
                {
                    bool charChanged = (selectChar != _lastCharSelectCharacter);
                    if (charChanged || firstEntry || stateChanged)
                    {
                        _lastCharSelectCharacter = selectChar;
                        _stalePollCount = 0;

                        // Deferred read: the game updates TMP text via animation
                        // after selectCharacter changes. Reading immediately gets
                        // stale text from the previous character. Wait 10 frames
                        // (~330ms) for animation + TMP update to complete.
                        _charSelectReadDelay = firstEntry ? 3 : 10;
                        DebugHelper.Write($"GenericMenu: [CharacterSelection] char={selectChar} deferred read in {_charSelectReadDelay} frames");
                    }

                    // Deferred read countdown
                    if (_charSelectReadDelay > 0)
                    {
                        _charSelectReadDelay--;
                        if (_charSelectReadDelay == 0)
                        {
                            string profileText = ReadCharacterProfile(handler, selectChar);
                            if (!string.IsNullOrWhiteSpace(profileText))
                            {
                                ScreenReaderOutput.Say(profileText);
                                DebugHelper.Write($"GenericMenu: [CharacterSelection] char={selectChar} profile announced");
                            }
                            else
                            {
                                DebugHelper.Write($"GenericMenu: [CharacterSelection] char={selectChar} profile empty");
                            }
                        }
                    }
                    else
                    {
                        _stalePollCount++;
                    }
                }
                else // CHARACTERSETTINGS (name/birthday/blood/confirm)
                {
                    var es = EventSystem.current;
                    GameObject selectedGO = null;
                    if ((object)es != null)
                        selectedGO = es.currentSelectedGameObject;

                    if ((object)selectedGO != null && selectedGO.Pointer != IntPtr.Zero)
                    {
                        IntPtr selectedPtr = selectedGO.Pointer;
                        if (selectedPtr == _lastSelectedButtonPtr && !firstEntry && !stateChanged)
                            return false;

                        _lastSelectedButtonPtr = selectedPtr;
                        _stalePollCount = 0;

                        string text = ReadBestTmpText(selectedGO);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ScreenReaderOutput.Say(text);
                            DebugHelper.Write($"GenericMenu: [CharacterSelection] setting button: {text}");
                        }
                        else
                        {
                            string allText = ReadAllVisibleText();
                            if (!string.IsNullOrWhiteSpace(allText))
                                ScreenReaderOutput.Say(allText);
                        }
                    }
                    else if (firstEntry || stateChanged)
                    {
                        // No EventSystem focus yet on state change → read all text
                        string allText = ReadAllVisibleText();
                        if (!string.IsNullOrWhiteSpace(allText))
                            ScreenReaderOutput.SayQueued(allText);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: [CharacterSelection] error: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Read profile text for the selected character.
        /// Both character panels are always active (side by side). The TMP array
        /// from GetComponentsInChildren contains BOTH profiles sequentially:
        ///   Section 1: GIRL (selectChar=1) profile data
        ///   Section 2: BOY (selectChar=0) profile data
        /// Each section starts with a "配音" label followed by the voice actor name.
        /// We find the two sections by locating duplicate "配音" labels, then
        /// pick the correct section based on selectCharacter.
        /// </summary>
        private string ReadCharacterProfile(CharacterSelectionUIHandler handler, int selectChar)
        {
            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return null;

                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                // Read all TMP texts with their indices
                var allTexts = new System.Collections.Generic.List<(int idx, string text)>();
                for (int i = 0; i < tmps.Count; i++)
                {
                    var tmp = tmps[i];
                    if ((object)tmp == null) continue;

                    string t = null;
                    if (SafeCall.TmpTextMethodAvailable)
                    {
                        IntPtr strPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                        if (strPtr != IntPtr.Zero)
                        {
                            try
                            {
                                t = IL2CPP.Il2CppStringToManaged(strPtr);
                                t = TextUtils.CleanRichText(t);
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(t) && t.Length > 1)
                        allTexts.Add((i, t));
                }

                // Find the two profile sections by locating duplicate label
                // "配音" (Voice Actor) appears at the start of each profile
                int firstVoiceIdx = -1;
                int secondVoiceIdx = -1;
                string voiceLabel = null;

                for (int i = 0; i < allTexts.Count; i++)
                {
                    string t = allTexts[i].text;
                    // The first non-header label that appears twice marks the profile start
                    if (t == "CHARACTER SELECT" || t == "CHARACTER SETTING") continue;
                    if (t.StartsWith("[") && t.EndsWith("]")) continue; // skip [Q], [E]

                    // Look for this same text later in the list
                    for (int j = i + 1; j < allTexts.Count; j++)
                    {
                        if (allTexts[j].text == t)
                        {
                            firstVoiceIdx = i;
                            secondVoiceIdx = j;
                            voiceLabel = t;
                            break;
                        }
                    }
                    if (firstVoiceIdx >= 0) break;
                }

                if (firstVoiceIdx < 0 || secondVoiceIdx < 0)
                {
                    DebugHelper.Write($"[CharSel] could not find two profile sections");
                    return ReadAllVisibleText();
                }

                DebugHelper.Write($"[CharSel] sections at {firstVoiceIdx},{secondVoiceIdx} label='{voiceLabel}'");

                // Section 1 (firstVoiceIdx..secondVoiceIdx-1) = GIRL (selectChar=1)
                // Section 2 (secondVoiceIdx..end) = BOY (selectChar=0)
                int startIdx, endIdx;
                if (selectChar == 1) // GIRL
                {
                    startIdx = firstVoiceIdx;
                    endIdx = secondVoiceIdx;
                }
                else // BOY
                {
                    startIdx = secondVoiceIdx;
                    endIdx = allTexts.Count;
                }

                var profileTexts = new System.Collections.Generic.List<string>();
                int charCount = 0;
                for (int i = startIdx; i < endIdx && profileTexts.Count < 20 && charCount < 500; i++)
                {
                    string t = allTexts[i].text;
                    if (t.StartsWith("[") && t.EndsWith("]")) continue; // skip [Q], [E]
                    if (t == "HOLD") continue;
                    if (!IsNumericOnly(t))
                    {
                        profileTexts.Add(t);
                        charCount += t.Length;
                    }
                }

                DebugHelper.Write($"[CharSel] char={selectChar} read {profileTexts.Count} items");
                return profileTexts.Count > 0 ? string.Join("  ", profileTexts) : null;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"[CharSel] ReadCharacterProfile error: {ex.Message}");
                return ReadAllVisibleText();
            }
        }

        /// <summary>
        /// Special update path for CustomRobotUIHandler (機體改造).
        /// Tracks robot switching (Q/E via robotIndex) and selected button
        /// via EventSystem.current.currentSelectedGameObject (this handler
        /// does NOT update UIHandlerBase.currentCursorIndex).
        /// </summary>
        private bool UpdateCustomRobotHandler()
        {
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                _lastCustomRobotIndex = -1;
                _lastSelectedButtonPtr = IntPtr.Zero;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] custom robot handler init");
            }

            if (!SafeCall.CustomRobotFieldsAvailable)
                return UpdateCustomRobotFallback();

            try
            {
                IntPtr handlerPtr = _activeHandler.Pointer;
                if (handlerPtr == IntPtr.Zero) return false;

                // Read robotIndex pointer
                IntPtr riPtr = SafeCall.ReadCustomRobotIndexPtr(handlerPtr);
                if (riPtr == IntPtr.Zero)
                    return UpdateCustomRobotFallback();

                // Check robot index for Q/E switch
                int robotIdx = SafeCall.ReadRobotIndexValue(riPtr);
                bool robotChanged = (robotIdx != _lastCustomRobotIndex && _lastCustomRobotIndex != -1);
                bool firstEntry = (_lastCustomRobotIndex == -1);

                if (robotIdx != _lastCustomRobotIndex)
                {
                    _lastCustomRobotIndex = robotIdx;

                    // Read current robot name
                    IntPtr robotPtr = SafeCall.ReadRobotIndexCurrentPtr(riPtr);
                    string robotName = null;
                    if (robotPtr != IntPtr.Zero)
                        robotName = SafeCall.ReadRobotNameSafe(robotPtr);

                    int count = SafeCall.ReadRobotIndexCount(riPtr);

                    if (!string.IsNullOrEmpty(robotName))
                    {
                        if (count > 0)
                        {
                            string msg = string.Format(Loc.Get("custom_robot_switch"),
                                robotName, robotIdx + 1, count);
                            ScreenReaderOutput.Say(msg);
                        }
                        else
                        {
                            ScreenReaderOutput.Say(robotName);
                        }
                        DebugHelper.Write($"GenericMenu: [CustomRobot] robot={robotName} idx={robotIdx} count={count}");
                    }

                    // On robot switch, force re-announce current button
                    if (robotChanged)
                        _lastSelectedButtonPtr = IntPtr.Zero;
                }

                // Detect selected button via EventSystem (currentCursorIndex not updated by this handler)
                var es = EventSystem.current;
                if ((object)es == null) return false;
                var selectedGO = es.currentSelectedGameObject;
                if ((object)selectedGO == null || selectedGO.Pointer == IntPtr.Zero)
                    return false;

                IntPtr selectedPtr = selectedGO.Pointer;
                if (selectedPtr == _lastSelectedButtonPtr && !firstEntry)
                    return false;

                _lastSelectedButtonPtr = selectedPtr;
                _stalePollCount = 0;

                // Get buttons list and find which button matches the selected gameobject
                IntPtr customPtr = SafeCall.ReadCustomCustomPtr(handlerPtr);
                if (customPtr == IntPtr.Zero)
                    return false;

                IntPtr buttonsPtr = SafeCall.ReadCustomButtonsPtr(customPtr);
                if (buttonsPtr == IntPtr.Zero)
                    return false;

                var buttons = new Il2CppSystem.Collections.Generic.List<
                    Il2CppCom.BBStudio.SRTeam.UI.StrategyPart.Custom.CustomButton>(buttonsPtr);
                int btnCount = buttons.Count;
                int matchedIndex = -1;

                for (int i = 0; i < btnCount; i++)
                {
                    var btn = buttons[i];
                    if ((object)btn == null || btn.Pointer == IntPtr.Zero) continue;
                    try
                    {
                        var btnGO = btn.gameObject;
                        if ((object)btnGO != null && btnGO.Pointer == selectedPtr)
                        {
                            matchedIndex = i;
                            break;
                        }
                    }
                    catch { }
                }

                if (matchedIndex < 0)
                {
                    DebugHelper.Write($"GenericMenu: [CustomRobot] selected GO not in button list");
                    return false;
                }

                // Read stat text for matched button
                string statText = ReadCustomButtonText(buttonsPtr, matchedIndex);
                if (!string.IsNullOrWhiteSpace(statText))
                {
                    ScreenReaderOutput.Say(statText);
                    DebugHelper.Write($"GenericMenu: [CustomRobot] btn={matchedIndex} stat={statText}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: CustomRobot error: {ex.GetType().Name}: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        // EButtonIndex stat name keys (HP=0, EN=1, AR=2, MO=3, SI=4, WP=5)
        private static readonly string[] _customStatKeys = {
            "custom_stat_hp", "custom_stat_en", "custom_stat_ar",
            "custom_stat_mo", "custom_stat_si", "custom_stat_wp"
        };

        /// <summary>
        /// Read stat label and values from a CustomButton at the given index.
        /// Stat label comes from EButtonIndex enum mapping, not from TMP text
        /// (textBefore/textAfter show numeric values, not labels).
        /// </summary>
        private string ReadCustomButtonText(IntPtr buttonsListPtr, int index)
        {
            try
            {
                var buttons = new Il2CppSystem.Collections.Generic.List<Il2CppCom.BBStudio.SRTeam.UI.StrategyPart.Custom.CustomButton>(buttonsListPtr);
                if (index < 0 || index >= buttons.Count)
                    return null;

                var btn = buttons[index];
                if ((object)btn == null || btn.Pointer == IntPtr.Zero)
                    return null;
                if (!SafeCall.ProbeObject(btn.Pointer))
                    return null;

                // Get stat name from EButtonIndex mapping
                string label = index < _customStatKeys.Length
                    ? Loc.Get(_customStatKeys[index])
                    : $"Stat {index}";

                // Read valueBefore and valueAfter
                int valBefore = btn.valueBefore;
                int valAfter = btn.valueAfter;

                if (valBefore != valAfter && valAfter != 0)
                {
                    return string.Format(Loc.Get("custom_stat_change"),
                        label, valBefore, valAfter);
                }
                else
                {
                    return string.Format(Loc.Get("custom_stat"),
                        label, valBefore);
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: ReadCustomButtonText error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fallback for CustomRobotUIHandler when SafeCall fields are unavailable.
        /// Tracks selected button via EventSystem and reads all visible TMP text.
        /// </summary>
        private bool UpdateCustomRobotFallback()
        {
            var es = EventSystem.current;
            if ((object)es == null) return false;
            var selectedGO = es.currentSelectedGameObject;
            if ((object)selectedGO == null || selectedGO.Pointer == IntPtr.Zero)
                return false;

            IntPtr selectedPtr = selectedGO.Pointer;
            if (selectedPtr == _lastSelectedButtonPtr)
                return false;

            _lastSelectedButtonPtr = selectedPtr;
            _stalePollCount = 0;

            string text = ReadAllVisibleText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                ScreenReaderOutput.Say(text);
                DebugHelper.Write($"GenericMenu: [CustomRobot] fallback");
            }

            return false;
        }

        /// <summary>
        /// Special update path for PilotTrainingUIHandler.
        /// This handler uses sub-handler cursors (statusHandler.m_CurrentIndex for
        /// Skill tab, paramHandler.m_CurrentIndex for Param tab) instead of
        /// UIHandlerBase.currentCursorIndex. Multiple PilotTrainingListUIHandler
        /// children exist; we switch _listHandler based on currentSelectType.
        /// </summary>
        private bool UpdatePilotTrainingHandler()
        {
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                _lastTrainingMenuType = -2;
                _lastTrainingSelectType = -1;
                _lastTrainingCursorIndex = -1;
                string screenKey = "screen_" + _activeHandlerType.ToLowerInvariant();
                string screenName = Loc.Get(screenKey);
                if (screenName != screenKey)
                    ScreenReaderOutput.Say(screenName);
                DebugHelper.Write($"GenericMenu: [{_activeHandlerType}] pilot training init");
            }

            try
            {
                var handler = _activeHandler.TryCast<PilotTrainingUIHandler>();
                if ((object)handler == null) return false;

                // Q/E pilot switch detection
                IntPtr pilotPtr = IntPtr.Zero;
                try
                {
                    var pilot = handler.currentPilot;
                    if ((object)pilot != null)
                        pilotPtr = pilot.Pointer;
                }
                catch { }

                if (pilotPtr != _lastTrainingPilotPtr && _lastTrainingPilotPtr != IntPtr.Zero)
                {
                    _lastTrainingCursorIndex = -1;
                    _stalePollCount = 0;

                    string pilotName = null;
                    if (pilotPtr != IntPtr.Zero)
                        pilotName = SafeCall.ReadPilotNameSafe(pilotPtr);

                    if (!string.IsNullOrWhiteSpace(pilotName))
                    {
                        ScreenReaderOutput.Say(pilotName);
                        DebugHelper.Write($"GenericMenu: [PilotTraining] pilot switch: {pilotName}");
                    }
                }
                _lastTrainingPilotPtr = pilotPtr;

                // Detect tab change (Skill=0 vs Param=1)
                int menuType = (int)handler.currentMenuType;
                bool tabChanged = (menuType != _lastTrainingMenuType);

                if (tabChanged)
                {
                    _lastTrainingMenuType = menuType;
                    _lastTrainingCursorIndex = -1;
                    _lastTrainingSelectType = -1;
                    _listHandler = null;

                    string tabName = menuType switch
                    {
                        0 => Loc.Get("training_tab_skill"),
                        1 => Loc.Get("training_tab_param"),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(tabName))
                        ScreenReaderOutput.Say(tabName);

                    DebugHelper.Write($"GenericMenu: PilotTraining tab changed to {menuType}");
                }

                int cursor;

                if (menuType == 0) // Skill tab
                {
                    var sh = handler.statusHandler;
                    if ((object)sh == null) return false;

                    int selectType = (int)sh.currentSelectType;
                    if (selectType != _lastTrainingSelectType || tabChanged)
                    {
                        _lastTrainingSelectType = selectType;
                        _lastTrainingCursorIndex = -1;
                        // Switch to the correct list handler for this sub-list
                        UpdateSkillTabListHandler(sh, selectType);
                    }

                    cursor = sh.m_CurrentIndex;
                }
                else if (menuType == 1) // Param tab
                {
                    var ph = handler.paramHandler;
                    if ((object)ph == null) return false;
                    cursor = ph.m_CurrentIndex;
                }
                else
                {
                    return false; // Unknown tab
                }

                if (cursor == _lastTrainingCursorIndex)
                    return false;

                _lastTrainingCursorIndex = cursor;
                _stalePollCount = 0;

                // Read item text
                string text = null;

                if ((object)_listHandler != null)
                    text = ReadListItemText(cursor);

                if (string.IsNullOrWhiteSpace(text))
                    text = ReadGenericButtonText(cursor);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ScreenReaderOutput.Say(text);
                    _descriptionDelay = 3;
                    _descriptionRetries = 0;
                    DebugHelper.Write($"GenericMenu: [PilotTraining] tab={menuType} cursor={cursor} text={text}");
                }
                else
                {
                    DebugHelper.Write($"GenericMenu: [PilotTraining] tab={menuType} cursor={cursor} no text found");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: PilotTraining error: {ex.GetType().Name}: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Special update handler for StatusUIHandler.
        /// Tracks tab changes (Pilot/Robot/Weapon) and reads weapon list items
        /// when on the Weapon tab. For Pilot/Robot tabs, reads all visible TMP.
        /// </summary>
        private bool UpdateStatusUIHandler()
        {
            bool justFound = false;
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                justFound = true;
                string screenName = Loc.Get("screen_statusuihandler");
                if (screenName != "screen_statusuihandler")
                    ScreenReaderOutput.Say(screenName);
            }

            try
            {
                var statusHandler = _activeHandler.TryCast<StatusUIHandler>();
                if ((object)statusHandler == null) return false;

                int uiType = (int)statusHandler.currentUIType;

                // Detect tab change
                bool tabChanged = (uiType != _lastStatusUIType);
                if (tabChanged)
                {
                    _lastStatusUIType = uiType;
                    _lastCursorIndex = -1;
                    _stalePollCount = 0;

                    // Announce tab name
                    string tabKey = uiType switch
                    {
                        0 => "status_tab_pilot",
                        1 => "status_tab_robot",
                        2 => "status_tab_weapon",
                        _ => null
                    };
                    if (tabKey != null)
                    {
                        string tabName = Loc.Get(tabKey);
                        if (tabName != tabKey)
                            ScreenReaderOutput.Say(tabName);
                    }

                    DebugHelper.Write($"GenericMenu: [StatusUIHandler] tab={uiType}");

                    // For non-weapon tabs, read structured info
                    if (uiType == 0)
                    {
                        string pilotInfo = ReadStatusPilotInfo(statusHandler);
                        if (!string.IsNullOrWhiteSpace(pilotInfo))
                            ScreenReaderOutput.SayQueued(pilotInfo);
                        return false;
                    }
                    else if (uiType == 1)
                    {
                        string robotInfo = ReadStatusRobotInfo(statusHandler);
                        if (!string.IsNullOrWhiteSpace(robotInfo))
                            ScreenReaderOutput.SayQueued(robotInfo);
                        return false;
                    }
                }

                // Weapon tab: track cursor and read weapon item details
                if (uiType == 2)
                {
                    int currentIndex;
                    if (SafeCall.FieldsAvailable)
                    {
                        var (ok, idx) = SafeCall.ReadCursorIndexSafe(_activeHandler.Pointer);
                        if (!ok) { ReleaseHandler(); return true; }
                        currentIndex = idx;
                    }
                    else
                    {
                        try { currentIndex = _activeHandler.currentCursorIndex; }
                        catch { ReleaseHandler(); return true; }
                    }

                    if (currentIndex != _lastCursorIndex || justFound)
                    {
                        _lastCursorIndex = currentIndex;
                        _stalePollCount = 0;

                        string weaponText = ReadStatusWeaponItem(statusHandler, currentIndex);
                        if (!string.IsNullOrWhiteSpace(weaponText))
                            ScreenReaderOutput.Say(weaponText);
                        else
                            DebugHelper.Write($"GenericMenu: [StatusUIHandler] weapon cursor={currentIndex} no text");
                    }
                    else
                    {
                        _stalePollCount++;
                    }
                }
                else
                {
                    // PILOT/ROBOT: no continuous cursor tracking needed
                    _stalePollCount++;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: [StatusUIHandler] error: {ex.Message}");
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Read weapon details from StatusUIWeaponListItemHandler at the given index.
        /// Returns formatted text with name, attack, range, EN, ammo, crit, hit.
        /// </summary>
        private string ReadStatusWeaponItem(StatusUIHandler statusHandler, int index)
        {
            try
            {
                var weaponUI = statusHandler.weaponUIHandler;
                if ((object)weaponUI == null) return null;

                var listUI = weaponUI.listUIHandler;
                if ((object)listUI == null || listUI.Pointer == IntPtr.Zero) return null;

                if (!SafeCall.ProbeObject(listUI.Pointer)) return null;

                var items = listUI.m_ListItem;
                if ((object)items == null || items.Count == 0) return null;

                ListItemHandler rawItem = null;
                if (index >= 0 && index < items.Count)
                    rawItem = items[index];
                else if (items.Count > 0)
                    rawItem = items[((index % items.Count) + items.Count) % items.Count];

                if ((object)rawItem == null || rawItem.Pointer == IntPtr.Zero) return null;

                var weaponItem = rawItem.TryCast<StatusUIWeaponListItemHandler>();
                if ((object)weaponItem == null) return null;

                var parts = new System.Collections.Generic.List<string>();

                // Weapon name
                try
                {
                    string name = ReadTmpSafe(weaponItem.m_NameText);
                    if (!string.IsNullOrWhiteSpace(name))
                        parts.Add(TextUtils.CleanRichText(name));
                }
                catch { }

                // Attack power
                try
                {
                    string attack = ReadTmpSafe(weaponItem.m_AttackText);
                    if (!string.IsNullOrWhiteSpace(attack))
                        parts.Add(Loc.Get("weapon_power") + " " + attack);
                }
                catch { }

                // Range (min~max)
                try
                {
                    var rt = weaponItem.rangeText;
                    if ((object)rt != null && rt.Pointer != IntPtr.Zero)
                    {
                        string rMin = ReadTmpSafe(rt.rangeMinText);
                        string rMax = ReadTmpSafe(rt.rangeMaxText);
                        if (!string.IsNullOrWhiteSpace(rMin))
                        {
                            string rangeStr = rMin;
                            if (!string.IsNullOrWhiteSpace(rMax) && rMax != rMin)
                                rangeStr += "~" + rMax;
                            parts.Add(Loc.Get("weapon_range") + " " + rangeStr);
                        }
                    }
                }
                catch { }

                // EN cost
                try
                {
                    string en = ReadTmpSafe(weaponItem.m_ENText);
                    if (!string.IsNullOrWhiteSpace(en))
                        parts.Add(Loc.Get("weapon_en_cost") + " " + en);
                }
                catch { }

                // Ammo
                try
                {
                    string ammo = ReadTmpSafe(weaponItem.m_AmmoText);
                    if (!string.IsNullOrWhiteSpace(ammo) && ammo != "-")
                        parts.Add(Loc.Get("weapon_ammo") + " " + ammo);
                }
                catch { }

                // Critical rate
                try
                {
                    string crit = ReadTmpSafe(weaponItem.m_CriticalText);
                    if (!string.IsNullOrWhiteSpace(crit))
                        parts.Add(Loc.Get("weapon_crit") + " " + crit);
                }
                catch { }

                // Hit rate
                try
                {
                    string hit = ReadTmpSafe(weaponItem.m_HitText);
                    if (!string.IsNullOrWhiteSpace(hit))
                        parts.Add(Loc.Get("battle_hit_rate") + " " + hit);
                }
                catch { }

                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read structured pilot info from StatusUIHandler's pilot tab.
        /// Returns formatted text with name, level, SP, morale, stats, skills, spirits.
        /// </summary>
        private string ReadStatusPilotInfo(StatusUIHandler statusHandler)
        {
            try
            {
                var pilotUI = statusHandler.pilotUIHandler;
                if ((object)pilotUI == null) return null;

                var parts = new System.Collections.Generic.List<string>();

                // Base info
                try
                {
                    var baseInfo = pilotUI.baseInfoUIHandler;
                    if ((object)baseInfo != null)
                    {
                        string name = ReadTmpSafe(baseInfo.m_NameText);
                        if (!string.IsNullOrWhiteSpace(name))
                            parts.Add(TextUtils.CleanRichText(name));

                        string level = ReadTmpSafe(baseInfo.m_LevelText);
                        if (!string.IsNullOrWhiteSpace(level))
                            parts.Add("Lv " + level);

                        string sp = ReadTmpSafe(baseInfo.m_SPText);
                        string maxSp = ReadTmpSafe(baseInfo.m_MaxSPText);
                        if (!string.IsNullOrWhiteSpace(sp))
                            parts.Add("SP " + sp + (string.IsNullOrWhiteSpace(maxSp) ? "" : "/" + maxSp));

                        string morale = ReadTmpSafe(baseInfo.m_MoraleText);
                        string maxMorale = ReadTmpSafe(baseInfo.m_MaxMoraleText);
                        if (!string.IsNullOrWhiteSpace(morale))
                            parts.Add(Loc.Get("stat_morale") + " " + morale + (string.IsNullOrWhiteSpace(maxMorale) ? "" : "/" + maxMorale));

                        string score = ReadTmpSafe(baseInfo.m_ScoreText);
                        if (!string.IsNullOrWhiteSpace(score))
                            parts.Add(Loc.Get("stat_score") + " " + score);
                    }
                }
                catch { }

                // Pilot stats (melee, defend, evade, skill, hit, sight)
                try
                {
                    var statUI = pilotUI.statusUIHandelr; // typo in game code
                    if ((object)statUI != null)
                    {
                        ReadParamGroupText(parts, statUI.meleeParamGroup, "stat_melee");
                        ReadParamGroupText(parts, statUI.defendParamGroup, "stat_defend");
                        ReadParamGroupText(parts, statUI.evadeParamGroup, "stat_evade");
                        ReadParamGroupText(parts, statUI.skillParamGroup, "stat_skill");
                        ReadParamGroupText(parts, statUI.hitParamGroup, "stat_hit");
                        ReadParamGroupText(parts, statUI.rangeParamGroup, "stat_sight");
                    }
                }
                catch { }

                // Ace bonus
                try
                {
                    var aceUI = pilotUI.aceBonusUIHandler;
                    if ((object)aceUI != null)
                    {
                        string bonus = ReadTmpSafe(aceUI.m_BonusText);
                        if (!string.IsNullOrWhiteSpace(bonus))
                            parts.Add(Loc.Get("stat_ace_bonus") + ": " + TextUtils.CleanRichText(bonus));
                    }
                }
                catch { }

                // Special skills (names from UI, descriptions from SAInterface data)
                try
                {
                    var skillUI = pilotUI.specialSkillUIHandler;
                    if ((object)skillUI != null)
                    {
                        var skillItems = skillUI.m_ItemList;
                        if ((object)skillItems != null && skillItems.Count > 0)
                        {
                            // Try to get SAInterface skill list for descriptions
                            Il2CppSystem.Collections.Generic.List<Il2CppCom.BBStudio.SRTeam.Data.SAInterface> saSkills = null;
                            try
                            {
                                var unitData = statusHandler.unit;
                                if ((object)unitData != null)
                                {
                                    var pilot = unitData.pilot;
                                    if ((object)pilot != null)
                                        saSkills = pilot.GetSkills();
                                }
                            }
                            catch { }

                            var skillEntries = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < skillItems.Count && i < 20; i++)
                            {
                                try
                                {
                                    var item = skillItems[i];
                                    if ((object)item == null) continue;
                                    string sn = ReadTmpSafe(item.m_NameText);
                                    if (string.IsNullOrWhiteSpace(sn)) continue;
                                    sn = TextUtils.CleanRichText(sn);

                                    // Try to get description from SAInterface
                                    string desc = null;
                                    if ((object)saSkills != null && i < saSkills.Count)
                                    {
                                        try
                                        {
                                            var sa = saSkills[i];
                                            if ((object)sa != null)
                                                desc = sa.GetDescription();
                                        }
                                        catch { }
                                    }

                                    if (!string.IsNullOrWhiteSpace(desc))
                                        skillEntries.Add(sn + ": " + desc);
                                    else
                                        skillEntries.Add(sn);
                                }
                                catch { }
                            }
                            if (skillEntries.Count > 0)
                                parts.Add(Loc.Get("stat_pilot_skills") + ": " + string.Join(". ", skillEntries));
                        }
                    }
                }
                catch { }

                // Spirit commands
                try
                {
                    var spiritUI = pilotUI.spiritCommandUIHandler;
                    if ((object)spiritUI != null)
                    {
                        var spiritItems = spiritUI.m_ItemList;
                        if ((object)spiritItems != null && spiritItems.Count > 0)
                        {
                            var spiritNames = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < spiritItems.Count && i < 10; i++)
                            {
                                try
                                {
                                    var item = spiritItems[i];
                                    if ((object)item == null) continue;
                                    string sn = ReadTmpSafe(item.m_NameText);
                                    string cost = ReadTmpSafe(item.m_CostText);
                                    if (!string.IsNullOrWhiteSpace(sn))
                                    {
                                        string entry = TextUtils.CleanRichText(sn);
                                        if (!string.IsNullOrWhiteSpace(cost))
                                            entry += "(" + cost + ")";
                                        spiritNames.Add(entry);
                                    }
                                }
                                catch { }
                            }
                            if (spiritNames.Count > 0)
                                parts.Add(Loc.Get("stat_spirit_commands") + ": " + string.Join(", ", spiritNames));
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read structured robot info from StatusUIHandler's robot tab.
        /// Returns formatted text with name, size, HP, EN, move, stats, terrain, abilities, parts.
        /// </summary>
        private string ReadStatusRobotInfo(StatusUIHandler statusHandler)
        {
            try
            {
                var robotUI = statusHandler.robotUIHandler;
                if ((object)robotUI == null) return null;

                var parts = new System.Collections.Generic.List<string>();

                // Base info
                try
                {
                    var baseInfo = robotUI.baseInfoUIHandler;
                    if ((object)baseInfo != null)
                    {
                        string name = ReadTmpSafe(baseInfo.nameText);
                        if (!string.IsNullOrWhiteSpace(name))
                            parts.Add(TextUtils.CleanRichText(name));

                        string size = ReadTmpSafe(baseInfo.sizeText);
                        if (!string.IsNullOrWhiteSpace(size))
                            parts.Add(Loc.Get("stat_size") + " " + size);

                        string move = ReadTmpSafe(baseInfo.moveRangeText);
                        if (!string.IsNullOrWhiteSpace(move))
                            parts.Add(Loc.Get("stat_move") + " " + move);

                        // HP gauge
                        try
                        {
                            var hpGauge = baseInfo.hpGauge;
                            if ((object)hpGauge != null)
                            {
                                string hp = ReadTmpSafe(hpGauge.m_CurrentParamText);
                                string maxHp = ReadTmpSafe(hpGauge.m_MaxParamText);
                                if (!string.IsNullOrWhiteSpace(hp))
                                    parts.Add("HP " + hp + (string.IsNullOrWhiteSpace(maxHp) ? "" : "/" + maxHp));
                            }
                        }
                        catch { }

                        // EN gauge
                        try
                        {
                            var enGauge = baseInfo.enGauge;
                            if ((object)enGauge != null)
                            {
                                string en = ReadTmpSafe(enGauge.m_CurrentParamText);
                                string maxEn = ReadTmpSafe(enGauge.m_MaxParamText);
                                if (!string.IsNullOrWhiteSpace(en))
                                    parts.Add("EN " + en + (string.IsNullOrWhiteSpace(maxEn) ? "" : "/" + maxEn));
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Robot stats (armor, mobility, accuracy) + remodel
                try
                {
                    var statUI = robotUI.statusUIHandelr; // typo in game code
                    if ((object)statUI != null)
                    {
                        ReadRobotParamGroupText(parts, statUI.armorHardnessParamGroup, "stat_armor");
                        ReadRobotParamGroupText(parts, statUI.quickMovementParamGroup, "stat_mobility");
                        ReadRobotParamGroupText(parts, statUI.aimingAbilityParamGroup, "stat_accuracy");

                        string remodel = ReadTmpSafe(statUI.remodelingText);
                        if (!string.IsNullOrWhiteSpace(remodel))
                            parts.Add(Loc.Get("stat_upgrade_levels") + " " + remodel);

                        // Terrain adaptation
                        try
                        {
                            var adaptUI = statUI.adaptationUIHandler;
                            if ((object)adaptUI != null)
                            {
                                var terrainParts = new System.Collections.Generic.List<string>();
                                AppendTerrainRank(terrainParts, adaptUI.airLevelTextGroup, "terrain_sky");
                                AppendTerrainRank(terrainParts, adaptUI.landLevelTextGroup, "terrain_ground");
                                AppendTerrainRank(terrainParts, adaptUI.waterLevelTextGroup, "terrain_water");
                                AppendTerrainRank(terrainParts, adaptUI.spaceLevelTextGroup, "terrain_space");
                                if (terrainParts.Count > 0)
                                    parts.Add(Loc.Get("stat_terrain") + " " + string.Join(" ", terrainParts));
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Robot abilities (names from UI, descriptions from SAInterface data)
                try
                {
                    var skillUI = robotUI.skillUIHandler;
                    if ((object)skillUI != null)
                    {
                        var abilityItems = skillUI.m_ItemList;
                        if ((object)abilityItems != null && abilityItems.Count > 0)
                        {
                            // Try to get SAInterface skill list for descriptions
                            Il2CppSystem.Collections.Generic.List<Il2CppCom.BBStudio.SRTeam.Data.SAInterface> saSkills = null;
                            try
                            {
                                var unitData = statusHandler.unit;
                                if ((object)unitData != null)
                                {
                                    var robot = unitData.robot;
                                    if ((object)robot != null)
                                        saSkills = robot.GetSkills();
                                }
                            }
                            catch { }

                            var abilityEntries = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < abilityItems.Count && i < 20; i++)
                            {
                                try
                                {
                                    var item = abilityItems[i];
                                    if ((object)item == null) continue;
                                    string an = ReadTmpSafe(item.m_NameText);
                                    if (string.IsNullOrWhiteSpace(an)) continue;
                                    an = TextUtils.CleanRichText(an);

                                    // Try to get description from SAInterface
                                    string desc = null;
                                    if ((object)saSkills != null && i < saSkills.Count)
                                    {
                                        try
                                        {
                                            var sa = saSkills[i];
                                            if ((object)sa != null)
                                                desc = sa.GetDescription();
                                        }
                                        catch { }
                                    }

                                    if (!string.IsNullOrWhiteSpace(desc))
                                        abilityEntries.Add(an + ": " + desc);
                                    else
                                        abilityEntries.Add(an);
                                }
                                catch { }
                            }
                            if (abilityEntries.Count > 0)
                                parts.Add(Loc.Get("stat_robot_skills") + ": " + string.Join(". ", abilityEntries));
                        }
                    }
                }
                catch { }

                // Equipped parts (names from UI, descriptions from SAInterface data)
                try
                {
                    var partsUI = robotUI.partsUIHandler;
                    if ((object)partsUI != null)
                    {
                        var partsList = partsUI.m_PartsList;
                        if ((object)partsList != null && partsList.Count > 0)
                        {
                            // Try to get SAInterface power parts list for descriptions
                            Il2CppSystem.Collections.Generic.List<Il2CppCom.BBStudio.SRTeam.Data.SAInterface> saParts = null;
                            try
                            {
                                var unitData = statusHandler.unit;
                                if ((object)unitData != null)
                                {
                                    var robot = unitData.robot;
                                    if ((object)robot != null)
                                        saParts = robot.GetPowerParts();
                                }
                            }
                            catch { }

                            var partEntries = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < partsList.Count && i < 10; i++)
                            {
                                try
                                {
                                    var item = partsList[i];
                                    if ((object)item == null) continue;
                                    string pn = ReadTmpSafe(item.m_PartsName);
                                    if (string.IsNullOrWhiteSpace(pn)) continue;
                                    pn = TextUtils.CleanRichText(pn);

                                    // Try to get description from SAInterface
                                    string desc = null;
                                    if ((object)saParts != null && i < saParts.Count)
                                    {
                                        try
                                        {
                                            var sa = saParts[i];
                                            if ((object)sa != null)
                                                desc = sa.GetDescription();
                                        }
                                        catch { }
                                    }

                                    if (!string.IsNullOrWhiteSpace(desc))
                                        partEntries.Add(pn + ": " + desc);
                                    else
                                        partEntries.Add(pn);
                                }
                                catch { }
                            }
                            if (partEntries.Count > 0)
                                parts.Add(Loc.Get("stat_power_parts") + ": " + string.Join(". ", partEntries));
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read a pilot ParamTextGroup's ParamText TMP value and append to parts list.
        /// Pilot version uses uppercase ParamText.
        /// </summary>
        private void ReadParamGroupText(System.Collections.Generic.List<string> parts,
            StatusUIPilotStatusUIHandler.ParamTextGroup group, string locKey)
        {
            try
            {
                var tmp = group.ParamText;
                if ((object)tmp == null) return;
                string val = ReadTmpSafe(tmp);
                if (!string.IsNullOrWhiteSpace(val))
                    parts.Add(Loc.Get(locKey) + " " + val);
            }
            catch { }
        }

        /// <summary>
        /// Read a robot ParamTextGroup's paramText TMP value and append to parts list.
        /// Robot version uses lowercase paramText.
        /// </summary>
        private void ReadRobotParamGroupText(System.Collections.Generic.List<string> parts,
            StatusUIRobotStatusUIHandler.ParamTextGroup group, string locKey)
        {
            try
            {
                var tmp = group.paramText;
                if ((object)tmp == null) return;
                string val = ReadTmpSafe(tmp);
                if (!string.IsNullOrWhiteSpace(val))
                    parts.Add(Loc.Get(locKey) + " " + val);
            }
            catch { }
        }

        /// <summary>
        /// Read terrain adaptation rank from AdaptationUIHandler ParamTextGroup
        /// and append "label:rank" to the list.
        /// </summary>
        private void AppendTerrainRank(System.Collections.Generic.List<string> parts,
            AdaptationUIHandler.ParamTextGroup group, string locKey)
        {
            try
            {
                var tmp = group.ParamText;
                if ((object)tmp == null) return;
                string rank = ReadTmpSafe(tmp);
                if (!string.IsNullOrWhiteSpace(rank))
                    parts.Add(Loc.Get(locKey) + rank);
            }
            catch { }
        }

        /// <summary>
        /// Switch _listHandler to the correct PilotTrainingListUIHandler for the
        /// current skill sub-list type (SkillList, SkillProgramList, SkillProgramLevelList).
        /// </summary>
        private void UpdateSkillTabListHandler(PilotTrainingStatusHandler sh, int selectType)
        {
            _listHandler = null;
            try
            {
                PilotTrainingListUIHandler targetList = null;
                switch (selectType)
                {
                    case 0: // SkillList
                        targetList = sh.skillListHandler;
                        break;
                    case 1: // SkillProgramList
                        targetList = sh.skillProgramListHandler;
                        break;
                    case 2: // SkillProgramLevelList
                        try
                        {
                            var levelList = sh.pilotSkillLevelList;
                            if ((object)levelList != null)
                                targetList = levelList.skillProgramLevelList;
                        }
                        catch { }
                        break;
                }

                if ((object)targetList != null && targetList.Pointer != IntPtr.Zero
                    && SafeCall.ProbeObject(targetList.Pointer))
                {
                    _listHandler = targetList;
                    DebugHelper.Write($"GenericMenu: PilotTraining list handler set: selectType={selectType}");
                }
            }
            catch { }
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

                if (!SafeCall.ProbeObject(_listHandler.Pointer))
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
                            name = ReadTmpSafe(nameTmp);
                    }
                    catch { }

                    try
                    {
                        var remainTmp = partsCol.m_PartsRemainCnt;
                        if ((object)remainTmp != null)
                        {
                            string t = ReadTmpSafe(remainTmp);
                            if (t != null) remain = t.Trim();
                        }
                    }
                    catch { }

                    try
                    {
                        var totalTmp = partsCol.m_PartsTotalCnt;
                        if ((object)totalTmp != null)
                        {
                            string t = ReadTmpSafe(totalTmp);
                            if (t != null) total = t.Trim();
                        }
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
        /// Read equipment slot info from PartsEquipEquipmentUIHandler.
        /// Accesses unitBaseData.m_PartsList[slotIndex].m_PartsName TMP.
        /// Returns "Slot N, partName" or "Slot N, Equipable" if empty.
        /// </summary>
        private string ReadPartsSlotText(PartsEquipEquipmentUIHandler equipUI, int slotIndex)
        {
            try
            {
                var unitBase = equipUI.unitBaseData;
                if ((object)unitBase == null || unitBase.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(unitBase.Pointer)) return null;

                var partsList = unitBase.m_PartsList;
                if ((object)partsList == null) return null;

                int count;
                try { count = partsList.Count; }
                catch { return null; }
                if (slotIndex < 0 || slotIndex >= count) return null;

                var slotCol = partsList[slotIndex];
                if ((object)slotCol == null || slotCol.Pointer == IntPtr.Zero) return null;
                if (!SafeCall.ProbeObject(slotCol.Pointer)) return null;

                string partName = null;
                try
                {
                    var nameTmp = slotCol.m_PartsName;
                    if ((object)nameTmp != null)
                        partName = ReadTmpSafe(nameTmp);
                }
                catch { }

                string slotLabel = Loc.Get("parts_slot", slotIndex + 1);

                if (string.IsNullOrWhiteSpace(partName))
                    return slotLabel + ", " + Loc.Get("parts_slot_empty");
                else
                    return slotLabel + ", " + partName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read the explanation text from PartsEquipEquipmentUIHandler.
        /// Used for deferred description reading in slot mode.
        /// </summary>
        private string ReadPartsEquipExplanation(PartsEquipUIHandler peHandler)
        {
            try
            {
                var equipUI = peHandler.equipmentUIHandler;
                if ((object)equipUI == null || equipUI.Pointer == IntPtr.Zero) return null;

                var expTmp = equipUI.explanationText;
                if ((object)expTmp == null) return null;

                string text = ReadTmpSafe(expTmp);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return text;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read save/load slot info from SaveLoadListItemHandler.
        /// Reads structured fields: nameText (slot name), allText.AutoManual,
        /// allText.ChapterNo, allText.PlayTime, allText.TurnCount, allText.SubtitleCaption.
        /// Returns formatted: "DATA 01: Auto Save, Chapter 5, Turn 23, Playtime 12h34m"
        /// </summary>
        private string ReadSaveLoadItemText(int cursorIndex)
        {
            try
            {
                if ((object)_listHandler == null || _listHandler.Pointer == IntPtr.Zero)
                {
                    _listHandler = null;
                    return null;
                }

                if (!SafeCall.ProbeObject(_listHandler.Pointer))
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

                var saveItem = item.TryCast<SaveLoadListItemHandler>();
                if ((object)saveItem == null || saveItem.Pointer == IntPtr.Zero)
                    return null;

                var parts = new System.Collections.Generic.List<string>();

                // Slot name (e.g. "DATA 01")
                try
                {
                    var nameTmp = saveItem.nameText;
                    if ((object)nameTmp != null)
                    {
                        string name = ReadTmpSafe(nameTmp);
                        if (!string.IsNullOrWhiteSpace(name))
                            parts.Add(name);
                    }
                }
                catch { }

                // Access DisplayStrings struct for structured data
                try
                {
                    var ds = saveItem.allText;
                    if ((object)ds != null)
                    {
                        // Auto/Manual indicator
                        try
                        {
                            var autoTmp = ds.AutoManual;
                            if ((object)autoTmp != null)
                            {
                                string auto = ReadTmpSafe(autoTmp);
                                if (!string.IsNullOrWhiteSpace(auto))
                                    parts.Add(auto);
                            }
                        }
                        catch { }

                        // Chapter number
                        try
                        {
                            var chapterTmp = ds.ChapterNo;
                            if ((object)chapterTmp != null)
                            {
                                string chapter = ReadTmpSafe(chapterTmp);
                                if (!string.IsNullOrWhiteSpace(chapter))
                                    parts.Add(Loc.Get("save_chapter", chapter));
                            }
                        }
                        catch { }

                        // Subtitle/mission name
                        try
                        {
                            var subTmp = ds.SubtitleCaption;
                            if ((object)subTmp != null)
                            {
                                string sub = ReadTmpSafe(subTmp);
                                if (!string.IsNullOrWhiteSpace(sub))
                                    parts.Add(sub);
                            }
                        }
                        catch { }

                        // Turn count
                        try
                        {
                            var turnTmp = ds.TurnCount;
                            if ((object)turnTmp != null)
                            {
                                string turn = ReadTmpSafe(turnTmp);
                                if (!string.IsNullOrWhiteSpace(turn))
                                    parts.Add(Loc.Get("save_turn", turn));
                            }
                        }
                        catch { }

                        // Playtime
                        try
                        {
                            var playTmp = ds.PlayTime;
                            if ((object)playTmp != null)
                            {
                                string play = ReadTmpSafe(playTmp);
                                if (!string.IsNullOrWhiteSpace(play))
                                    parts.Add(Loc.Get("save_playtime", play));
                            }
                        }
                        catch { }

                        // Save date
                        try
                        {
                            var monthTmp = ds.SaveDateMonth;
                            var timeTmp = ds.SaveDateTime;
                            string month = null, time = null;
                            if ((object)monthTmp != null)
                                month = ReadTmpSafe(monthTmp);
                            if ((object)timeTmp != null)
                                time = ReadTmpSafe(timeTmp);
                            if (!string.IsNullOrWhiteSpace(month))
                            {
                                string dateStr = month;
                                if (!string.IsNullOrWhiteSpace(time))
                                    dateStr += " " + time;
                                parts.Add(dateStr);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (parts.Count == 0)
                {
                    // Empty slot
                    return Loc.Get("save_new_slot");
                }

                return string.Join(", ", parts);
            }
            catch
            {
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
        /// Track SearchUnitTopUIHandler mode and cursor changes.
        /// Three modes: Category (select Spirit/Skill/Ability), Item (select specific item),
        /// Result (select Pilot/Robot from search results).
        /// Each mode uses different indices and handlers.
        /// </summary>
        private bool UpdateSearchHandler()
        {
            // Screen name announcement for new handler
            if (_newHandlerJustFound)
            {
                _newHandlerJustFound = false;
                string screenName = Loc.Get("search_unit_screen");
                ScreenReaderOutput.Say(screenName);
            }

            try
            {
                var searchHandler = _activeHandler.TryCast<SearchUnitTopUIHandler>();
                if ((object)searchHandler == null) return false;

                // Probe handler before accessing IL2CPP fields
                if (!SafeCall.ProbeObject(searchHandler.Pointer)) return false;

                // Read current mode and state
                int selectMode = (int)searchHandler.currentSelectMode;

                // Mode change detection
                if (selectMode != _lastSearchSelectMode)
                {
                    _lastSearchSelectMode = selectMode;
                    _lastSearchCategory = -1;
                    _lastSearchItemIndex = -1;
                    _lastSearchResult = -1;
                    _lastSearchResultIndex = -1;
                    _stalePollCount = 0;

                    // Announce mode change
                    string modeName = selectMode switch
                    {
                        0 => Loc.Get("search_mode_category"),  // Category
                        1 => Loc.Get("search_mode_item"),      // Item
                        2 => Loc.Get("search_mode_result"),    // Result
                        _ => Loc.Get("search_mode_unknown")
                    };
                    ScreenReaderOutput.Say(modeName);
                }

                // Handle each mode
                switch (selectMode)
                {
                    case 0: // Category mode
                        return UpdateSearchCategoryMode(searchHandler);
                    case 1: // Item mode
                        return UpdateSearchItemMode(searchHandler);
                    case 2: // Result mode
                        return UpdateSearchResultMode(searchHandler);
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"UpdateSearchHandler fault: {ex.Message}");
                _faultCount++;
                if (_faultCount >= ModConfig.MenuMaxFaults)
                {
                    ReleaseHandler();
                    return false;
                }
            }

            if (_faultCount > 0) _faultCount = 0;
            return false;
        }

        /// <summary>
        /// Category mode: selecting Spirit/Skill/Ability category
        /// </summary>
        private bool UpdateSearchCategoryMode(SearchUnitTopUIHandler handler)
        {
            int category = (int)handler.currentCategory;

            if (category != _lastSearchCategory)
            {
                _lastSearchCategory = category;
                _stalePollCount = 0;

                string categoryName = category switch
                {
                    0 => Loc.Get("search_category_spirit"),   // Spirit
                    1 => Loc.Get("search_category_skill"),    // Skill
                    2 => Loc.Get("search_category_ability"),  // Ability
                    _ => Loc.Get("search_category_unknown")
                };

                ScreenReaderOutput.Say(categoryName);
            }
            else
            {
                _stalePollCount++;
            }

            return false;
        }

        /// <summary>
        /// Item mode: selecting specific Spirit/Skill/Ability from list
        /// Uses ItemListHandler[currentCategory] and CurrentItemIndex[currentCategory]
        /// </summary>
        private bool UpdateSearchItemMode(SearchUnitTopUIHandler handler)
        {
            int category = (int)handler.currentCategory;
            var itemIndexArray = handler.CurrentItemIndex;

            if ((object)itemIndexArray == null || category < 0 || category >= itemIndexArray.Length)
                return false;

            int itemIndex = itemIndexArray[category];

            if (itemIndex != _lastSearchItemIndex || category != _lastSearchCategory)
            {
                _lastSearchItemIndex = itemIndex;
                _lastSearchCategory = category;
                _stalePollCount = 0;

                // Get the list handler for this category
                var itemHandlers = handler.ItemListHandler;
                if ((object)itemHandlers == null || category >= itemHandlers.Count)
                    return false;

                var listHandler = itemHandlers[category];
                if ((object)listHandler == null || !SafeCall.ProbeObject(listHandler.Pointer))
                    return false;

                // Read item text using ListHandlerBase
                _listHandler = listHandler;
                string text = ReadListItemText(itemIndex);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ScreenReaderOutput.Say(text);
                }
            }
            else
            {
                _stalePollCount++;
            }

            return false;
        }

        /// <summary>
        /// Result mode: selecting Pilot/Robot from search results
        /// Uses ResultListHandler[currentResult] and CurrentResultIndex
        /// </summary>
        private bool UpdateSearchResultMode(SearchUnitTopUIHandler handler)
        {
            int result = (int)handler.currentResult;
            int resultIndex = handler.CurrentResultIndex;

            if (resultIndex != _lastSearchResultIndex || result != _lastSearchResult)
            {
                _lastSearchResultIndex = resultIndex;
                _lastSearchResult = result;
                _stalePollCount = 0;

                // Get the list handler for this result type
                var resultHandlers = handler.ResultListHandler;
                if ((object)resultHandlers == null || result < 0 || result >= resultHandlers.Count)
                    return false;

                var listHandler = resultHandlers[result];
                if ((object)listHandler == null || !SafeCall.ProbeObject(listHandler.Pointer))
                    return false;

                // Read result text using UnitListHandler (inherits from ListHandlerBase)
                _listHandler = listHandler;
                string text = ReadListItemText(resultIndex);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ScreenReaderOutput.Say(text);
                }
            }
            else
            {
                _stalePollCount++;
            }

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

            IntPtr currentBehaviourPtr;
            if (SafeCall.IsAvailable)
            {
                try
                {
                    if (inputMgr.Pointer == IntPtr.Zero) return false;
                }
                catch { return false; }

                currentBehaviourPtr = SafeCall.GetCurrentInputBehaviourSafe(inputMgr.Pointer);
                if (currentBehaviourPtr == IntPtr.Zero) return false;
            }
            else
            {
                IInputBehaviour currentBehaviour;
                try
                {
                    if (inputMgr.Pointer == IntPtr.Zero) return false;
                    currentBehaviour = inputMgr.GetCurrentInputBehaviour();
                }
                catch { return false; }

                if ((object)currentBehaviour == null) return false;
                try
                {
                    currentBehaviourPtr = currentBehaviour.Pointer;
                    if (currentBehaviourPtr == IntPtr.Zero) return false;
                }
                catch { return false; }
            }

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

                    // SEH probe: verify handler's native object is still alive
                    // before calling any IL2CPP property.
                    // During scene transitions (e.g. battle → adventure), handlers
                    // returned by FindObjectsOfType may have freed native memory
                    // → uncatchable AccessViolationException on property access.
                    if (!SafeCall.ProbeObject(handler.Pointer)) continue;

                    // SAFETY: Read controlBehaviour via SEH-protected field read.
                    // Do NOT access handler.gameObject here - it's an unprotected
                    // IL2CPP method call that can AV on partially-destroyed objects.
                    // Defer gameObject access until after match is confirmed.
                    IntPtr cbPtr;
                    if (SafeCall.FieldsAvailable)
                    {
                        cbPtr = SafeCall.ReadControlBehaviourPtrSafe(handler.Pointer);
                        if (cbPtr == IntPtr.Zero) continue;
                    }
                    else
                    {
                        var cb = handler.controlBehaviour;
                        if ((object)cb == null) continue;
                        if (cb.Pointer == IntPtr.Zero) continue;
                        cbPtr = cb.Pointer;
                    }

                    if (cbPtr == currentBehaviourPtr)
                    {
                        string typeName;
                        if (SafeCall.IsAvailable)
                        {
                            typeName = SafeCall.ReadObjectTypeName(handler.Pointer);
                            if (typeName == null) continue; // handler freed between probe and read
                        }
                        else
                        {
                            try { typeName = handler.GetIl2CppType().Name; }
                            catch { typeName = "unknown"; }
                        }

                        if (_skipTypes.Contains(typeName)) continue;

                        IntPtr handlerPtr = handler.Pointer;
                        if (handlerPtr == _lastHandlerPtr)
                        {
                            _activeHandler = handler;
                            _cachedBehaviourPtr = currentBehaviourPtr;
                            return true;
                        }

                        // New handler found - controlBehaviour matches, so
                        // the handler is confirmed alive. Safe to access
                        // gameObject now for ListHandlerBase detection.
                        _activeHandler = handler;
                        _lastHandlerPtr = handlerPtr;
                        _cachedBehaviourPtr = currentBehaviourPtr;
                        _lastCursorIndex = -1;
                        _activeHandlerType = typeName;
                        _typeSpecificCount = -1;
                        _newHandlerJustFound = true;
                        _initSkipCount = 3; // let UI settle before reading
                        _cmdDiagDumped = false;

                        // Detect ListHandlerBase for menus
                        _listHandler = null;

                        // SAFETY: ProbeObject before accessing .gameObject to prevent AV
                        // on partially-destroyed handlers during scene transitions.
                        // If probe fails, skip detection (graceful degradation).
                        if (SafeCall.ProbeObject(handler.Pointer))
                        {
                            try
                            {
                                var hGo = handler.gameObject;
                                if ((object)hGo != null && hGo.Pointer != IntPtr.Zero)
                                {
                                    var lh = hGo.GetComponentInChildren<ListHandlerBase>(false);
                                    if ((object)lh != null && lh.Pointer != IntPtr.Zero)
                                        _listHandler = lh;
                                }
                            }
                            catch { }
                        }

                        // Detect sort/filter TMP fields from list handler subtypes
                        _sortTmp = null;
                        _filterTmp = null;
                        _lastSortText = "";
                        _lastFilterText = "";
                        if ((object)_listHandler != null)
                            DetectSortFilterTmp();

                        DebugHelper.Write($"GenericMenu: Found {_activeHandlerType}{((object)_listHandler != null ? " [list]" : "")}{((object)_sortTmp != null ? " [sort]" : "")}");
                        DebugHelper.Flush();
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

                if (SafeCall.IsAvailable)
                {
                    IntPtr behPtr = SafeCall.GetCurrentInputBehaviourSafe(mgr.Pointer);
                    return behPtr != IntPtr.Zero && behPtr == _cachedBehaviourPtr;
                }

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

            // Strategy 2a: Type-specific list reading (structured data)
            if (string.IsNullOrWhiteSpace(text) && (object)_listHandler != null
                && _activeHandlerType == "SaveLoadUIHandler")
            {
                text = ReadSaveLoadItemText(index);
            }

            // Strategy 2b: Generic list-based reading
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
                // Weapon data comes from pre-populated list item fields (not updated
                // on cursor change), so immediate read is safe.
                if (_activeHandlerType == "WeaponListHandler")
                    AnnounceWeaponDetail();

                // Defer description/detail reads by multiple poll cycles.
                // The game updates TMP description text (explanationText,
                // descriptionText, GuideUIHandler.m_GuideText) in UI handler
                // Update/LateUpdate, which runs AFTER our hook in
                // InputManager.Update(). Reading immediately gets stale text.
                // Delay of 3 cycles (~100ms) gives the game reliable time.
                // Rapid navigation resets the timer, reading only the final item.
                // Applied to ALL handlers: specific readers run first, then
                // generic GuideUIHandler.m_GuideText as universal fallback.
                _descriptionDelay = 3;
                _descriptionRetries = 0;
                _descriptionCursorIndex = index;
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

            // Verify handler is still alive before unprotected .gameObject call
            if (!SafeCall.ProbeObject(_activeHandler.Pointer)) return null;

            try
            {
                var go = _activeHandler.gameObject;
                if ((object)go == null || go.Pointer == IntPtr.Zero) return null;

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons = null;
                try
                {
                    buttons = go.GetComponentsInChildren<Button>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: GetComponentsInChildren<Button> error: {ex.GetType().Name}");
                    return null;
                }
                if (buttons == null || buttons.Count == 0 || index >= buttons.Count) return null;

                var btn = buttons[index];
                if ((object)btn == null) return null;

                GameObject btnGo = null;
                try
                {
                    btnGo = btn.gameObject;
                    if ((object)btnGo == null || !btnGo.activeInHierarchy) return null;
                }
                catch { return null; }

                return ReadBestTmpText(btnGo);
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
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: GetComponentsInChildren<TMP> error: {ex.GetType().Name}");
                    return null;
                }
                if (tmps == null || tmps.Count == 0) return null;

                string bestText = null;
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;

                    // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                    // uncatchable AV when TMP object is destroyed during scene transitions
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

                    if (!string.IsNullOrWhiteSpace(t) && !IsNumericOnly(t))
                    {
                        if (bestText == null || t.Length > bestText.Length)
                            bestText = t;
                    }
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

                // SEH probe: verify ListHandlerBase native object is still alive
                // before accessing m_ListItem. This IL2CPP field access dereferences
                // native pointers and causes uncatchable AV if object was freed.
                if (!SafeCall.ProbeObject(_listHandler.Pointer))
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

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = targetGo.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: ListItem GetComponentsInChildren error: {ex.GetType().Name}");
                    return null;
                }
                if (tmps == null || tmps.Count == 0) return null;

                var texts = new System.Collections.Generic.List<string>();
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;

                    // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                    // uncatchable AV when TMP object is destroyed during scene transitions
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

                    if (!string.IsNullOrWhiteSpace(t))
                        texts.Add(t);
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

                if (typeName == "DifficultyUIHandler" || typeName == "DifficultyUIHandler_dlc")
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

                if (typeName == "GameOverUIHandler")
                {
                    // SelectMenu enum: Retry=0, MainMenu=1
                    switch (index)
                    {
                        case 0: return Loc.Get("gameover_retry");
                        case 1: return Loc.Get("gameover_mainmenu");
                    }
                }
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

                string name = ReadTmpSafe(tmp);
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
                            string cost = ReadTmpSafe(costTmp);
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

                string name = ReadTmpSafe(tmp);
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
                            string cost = ReadTmpSafe(costTmp);
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
        /// Read difficulty text from DifficultyUIHandler or DifficultyUIHandler_dlc.
        /// Uses modeTitleText and modeText TMP fields for rendered text.
        /// Both classes have the same fields but are separate types.
        /// </summary>
        private string ReadDifficultyText()
        {
            string title = null;
            string desc = null;

            // Try DifficultyUIHandler first
            try
            {
                var handler = _activeHandler.TryCast<DifficultyUIHandler>();
                if ((object)handler != null)
                {
                    try { var tmp = handler.modeTitleText; if ((object)tmp != null) title = ReadTmpSafe(tmp); } catch { }
                    try { var tmp = handler.modeText; if ((object)tmp != null) desc = ReadTmpSafe(tmp); } catch { }
                }
            }
            catch { }

            // Try DifficultyUIHandler_dlc if base type didn't work
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc))
            {
                try
                {
                    var dlcHandler = _activeHandler.TryCast<DifficultyUIHandler_dlc>();
                    if ((object)dlcHandler != null)
                    {
                        try { var tmp = dlcHandler.modeTitleText; if ((object)tmp != null) title = ReadTmpSafe(tmp); } catch { }
                        try { var tmp = dlcHandler.modeText; if ((object)tmp != null) desc = ReadTmpSafe(tmp); } catch { }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(desc))
                return Loc.Get("difficulty_description", title, desc);
            if (!string.IsNullOrWhiteSpace(title))
                return title;
            if (!string.IsNullOrWhiteSpace(desc))
                return desc;
            return null;
        }

        /// <summary>
        /// Read unit command text from ButtonType enum via Loc.
        /// Uses mod localization which tracks game language via LocalizationManager.GetLocaleID().
        /// </summary>
        private string ReadUnitCommandText(int index)
        {
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
                        string t = ReadTmpSafe(name);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var prod = handler.dtProductName;
                    if ((object)prod != null)
                    {
                        string t = ReadTmpSafe(prod);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var desc = handler.dtDescription;
                    if ((object)desc != null)
                    {
                        string t = ReadTmpSafe(desc);
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
                        string t = ReadTmpSafe(name);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var nick = handler.dtNickName;
                    if ((object)nick != null)
                    {
                        string t = ReadTmpSafe(nick);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
                    }
                }
                catch { }

                try
                {
                    var voice = handler.dtVoiceActor;
                    if ((object)voice != null)
                    {
                        string t = ReadTmpSafe(voice);
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add("CV:" + t);
                    }
                }
                catch { }

                try
                {
                    var desc = handler.dtDescription;
                    if ((object)desc != null)
                    {
                        string t = ReadTmpSafe(desc);
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

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: ReadAllVisibleText GetComponentsInChildren error: {ex.GetType().Name}");
                    return null;
                }
                if (tmps == null || tmps.Count == 0) return null;

                var texts = new System.Collections.Generic.List<string>();
                int charCount = 0;
                foreach (var tmp in tmps)
                {
                    if (texts.Count >= ModConfig.ReviewVisibleMaxItems || charCount >= ModConfig.ReviewVisibleMaxChars) break;
                    if ((object)tmp == null) continue;

                    // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                    // uncatchable AV when TMP object is destroyed during scene transitions
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

                    if (!string.IsNullOrWhiteSpace(t) && t.Length > 1 && !IsNumericOnly(t))
                    {
                        texts.Add(t);
                        charCount += t.Length;
                    }
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
                else if (typeName == "GameOverUIHandler")
                {
                    return 2;
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

            // StatusUIHandler: structured reading per tab
            // TacticalPartStatusUIHandler extends StatusUIHandler (tactical map F key)
            if (_activeHandlerType == "StatusUIHandler" || _activeHandlerType == "TacticalPartStatusUIHandler")
            {
                CollectStatusScreenItems(items);
                return;
            }

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

                if (!SafeCall.ProbeObject(_listHandler.Pointer)) { _listHandler = null; return; }

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

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons = null;
                try
                {
                    buttons = go.GetComponentsInChildren<Button>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: CollectGenericButtonItems GetComponentsInChildren error: {ex.GetType().Name}");
                    return;
                }
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

        /// <summary>
        /// Collect structured status screen items for R key review.
        /// Routes to pilot/robot/weapon structured readers based on current tab.
        /// </summary>
        private void CollectStatusScreenItems(List<string> items)
        {
            try
            {
                var statusHandler = _activeHandler.TryCast<StatusUIHandler>();
                if ((object)statusHandler == null)
                {
                    CollectInfoScreenItems(items);
                    return;
                }

                int uiType = (int)statusHandler.currentUIType;
                if (uiType == 0) // PILOT
                {
                    string pilotInfo = ReadStatusPilotInfo(statusHandler);
                    if (!string.IsNullOrWhiteSpace(pilotInfo))
                    {
                        foreach (var part in pilotInfo.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
                            items.Add(part);
                    }
                }
                else if (uiType == 1) // ROBOT
                {
                    string robotInfo = ReadStatusRobotInfo(statusHandler);
                    if (!string.IsNullOrWhiteSpace(robotInfo))
                    {
                        foreach (var part in robotInfo.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
                            items.Add(part);
                    }
                }
                else if (uiType == 2) // WEAPON
                {
                    // Collect all weapons in the list
                    CollectStatusWeaponListItems(items, statusHandler);
                }
                else
                {
                    CollectInfoScreenItems(items);
                }
            }
            catch
            {
                CollectInfoScreenItems(items);
            }
        }

        /// <summary>
        /// Collect all weapon items from the status weapon list for R key review.
        /// </summary>
        private void CollectStatusWeaponListItems(List<string> items, StatusUIHandler statusHandler)
        {
            try
            {
                var weaponUI = statusHandler.weaponUIHandler;
                if ((object)weaponUI == null) return;

                var listUI = weaponUI.listUIHandler;
                if ((object)listUI == null || listUI.Pointer == IntPtr.Zero) return;
                if (!SafeCall.ProbeObject(listUI.Pointer)) return;

                int listCount = listUI.GetListCount();
                int cursorIndex = -1;
                try { cursorIndex = _activeHandler.currentCursorIndex; } catch { }

                var listItems = listUI.m_ListItem;
                if ((object)listItems == null || listItems.Count == 0) return;

                for (int i = 0; i < listCount && i < 30; i++)
                {
                    string weaponText = ReadStatusWeaponItem(statusHandler, i);
                    if (!string.IsNullOrWhiteSpace(weaponText))
                    {
                        string prefix = (i == cursorIndex) ? "(*) " : "";
                        items.Add(prefix + weaponText);
                    }
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

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: CollectNonButtonItems GetComponentsInChildren error: {ex.GetType().Name}");
                    return;
                }
                if (tmps == null || tmps.Count == 0) return;

                foreach (var tmp in tmps)
                {
                    if (items.Count >= ModConfig.ReviewInfoMaxItems) break;
                    if ((object)tmp == null) continue;

                    // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                    // uncatchable AV when TMP object is destroyed during scene transitions
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

                    if (!string.IsNullOrWhiteSpace(t) && t.Length > 1)
                        items.Add(t);
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
                else if (_activeHandlerType == "AssistLinkManager" || _activeHandlerType == "AssistLinkSystem")
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

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try { var tmp = handler.level; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_level", t)); } } catch { }
            try { var tmp = handler.gainExp; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add("EXP +" + t); } } catch { }
            try { var tmp = handler.gainScore; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_score", t)); } } catch { }
            try { var tmp = handler.gainCredit; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_credits", t)); } } catch { }

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

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try
            {
                var bLv = handler.beforeLevel;
                var nLv = handler.nowLevel;
                string before = ((object)bLv != null) ? ReadTmpSafe(bLv) : "";
                string now = ((object)nLv != null) ? ReadTmpSafe(nLv) : "";
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
                try { var tmp = param.before; if ((object)tmp != null) before = ReadTmpSafe(tmp) ?? ""; } catch { }
                try { var tmp = param.now; if ((object)tmp != null) now = ReadTmpSafe(tmp) ?? ""; } catch { }
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

            try { var tmp = handler.characterName; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
            try { var tmp = handler.bonusDescription; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
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

            // Left (attacker) unit info + parametor data
            try
            {
                var left = handler.leftUnitInfo;
                BattleCheckMenuParametor leftParam = null;
                try { leftParam = handler.baseLeftMainParam; } catch { }
                if ((object)left != null)
                    CollectBattleCheckUnitInfo(items, left, leftParam, Loc.Get("battle_left"));
            }
            catch { }

            // Right (defender) unit info + parametor data
            try
            {
                var right = handler.rightUnitInfo;
                BattleCheckMenuParametor rightParam = null;
                try { rightParam = handler.baseRightMainParam; } catch { }
                if ((object)right != null)
                    CollectBattleCheckUnitInfo(items, right, rightParam, Loc.Get("battle_right"));
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

        private static void CollectBattleCheckUnitInfo(List<string> items, BattleCheckMenuUnitInfoHandler info, BattleCheckMenuParametor param, string side)
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

                // Combat predictions: prefer parametor int values (sprite damage shows "-" in TMP)
                var combat = new System.Collections.Generic.List<string>();
                if ((object)param != null)
                {
                    try
                    {
                        combat.Add(Loc.Get("battle_hit_rate") + " " + param.hitRate + "%");
                        combat.Add(Loc.Get("battle_damage") + " " + param.predictDamage);
                        combat.Add(Loc.Get("battle_critical") + " " + param.criticalRate + "%");
                        combat.Add(Loc.Get("battle_attack_power") + " " + param.attackPower);
                    }
                    catch { }
                }
                if (combat.Count == 0)
                {
                    // Fallback to TMP text if parametor unavailable
                    string hit = ReadTmpSafe(info.hitRate);
                    string damage = ReadTmpSafe(info.weaponDamage);
                    string crit = ReadTmpSafe(info.criticalRate);
                    string atk = ReadTmpSafe(info.attackPower);
                    if (!string.IsNullOrWhiteSpace(hit)) combat.Add(Loc.Get("battle_hit_rate") + " " + hit);
                    if (!string.IsNullOrWhiteSpace(damage)) combat.Add(Loc.Get("battle_damage") + " " + damage);
                    if (!string.IsNullOrWhiteSpace(crit)) combat.Add(Loc.Get("battle_critical") + " " + crit);
                    if (!string.IsNullOrWhiteSpace(atk)) combat.Add(Loc.Get("battle_attack_power") + " " + atk);
                }
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

                    try { var tmp = handler.explanationText; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); } } catch { }
                    try { var tmp = handler.totalPriceText; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_total_price", t)); } } catch { }
                    try { var tmp = handler.remainCreditText; if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(Loc.Get("review_remaining_credits", t)); } } catch { }
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
                else if (_activeHandlerType == "MissionUIHandler")
                {
                    CollectMissionInfo(items);
                }
                else if (_activeHandlerType == "SelectDogmaUIHandler"
                    || _activeHandlerType == "SelectTacticalCommandUIHandler"
                    || _activeHandlerType == "SelectSpecialCommandUIHandler")
                {
                    CollectSpecialCommandDescription(items);
                }
                else if (_activeHandlerType == "BackLogUIHandler")
                {
                    CollectBackLogContent(items);
                }
                else if (_activeHandlerType == "PilotTrainingUIHandler")
                {
                    CollectPilotTrainingInfo(items);
                }
            }
            catch { }
        }

        /// <summary>
        /// Read pilot training info: skill description, current tab, dialog info.
        /// </summary>
        private void CollectPilotTrainingInfo(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<PilotTrainingUIHandler>();
                if ((object)handler == null) return;

                // Current tab
                try
                {
                    var menuType = handler.currentMenuType;
                    string tabName = menuType switch
                    {
                        PilotTrainingUIHandler.MenuType.Skill => Loc.Get("training_tab_skill"),
                        PilotTrainingUIHandler.MenuType.Param => Loc.Get("training_tab_param"),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(tabName))
                        items.Add(tabName);
                }
                catch { }

                // Skill description from statusHandler
                try
                {
                    var sh = handler.statusHandler;
                    if ((object)sh != null)
                    {
                        var tmp = sh.explanationText;
                        if ((object)tmp != null)
                        {
                            string t = ReadTmpSafe(tmp);
                            if (!string.IsNullOrWhiteSpace(t))
                                items.Add(t);
                        }
                    }
                }
                catch { }

                // Learn dialog info
                try
                {
                    var dialog = handler.LearnSkillDialog;
                    if ((object)dialog != null)
                    {
                        var dialogGo = dialog.go;
                        if ((object)dialogGo != null && dialogGo.activeInHierarchy)
                        {
                            string skillName = ReadTmpSafe(dialog.learningSkillProgramText);
                            if (!string.IsNullOrWhiteSpace(skillName))
                                items.Add(Loc.Get("training_learning") + " " + skillName);

                            string needBuy = ReadTmpSafe(dialog.needToBuyText);
                            if (!string.IsNullOrWhiteSpace(needBuy))
                                items.Add(needBuy);

                            string cost = ReadTmpSafe(dialog.costCreditText);
                            if (!string.IsNullOrWhiteSpace(cost))
                                items.Add(Loc.Get("training_cost") + " " + cost);
                        }
                    }
                }
                catch { }

                // Upgrade param dialog info
                try
                {
                    var dialog = handler.UpgradeParamDialog;
                    if ((object)dialog != null)
                    {
                        var dialogGo = dialog.go;
                        if ((object)dialogGo != null && dialogGo.activeInHierarchy)
                        {
                            string skillName = ReadTmpSafe(dialog.learningSkillProgramText);
                            if (!string.IsNullOrWhiteSpace(skillName))
                                items.Add(Loc.Get("training_upgrading") + " " + skillName);

                            string cost = ReadTmpSafe(dialog.costCreditText);
                            if (!string.IsNullOrWhiteSpace(cost))
                                items.Add(Loc.Get("training_cost") + " " + cost);
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read mission area name from MissionUIHandler.areaName TMP
        /// AND mission detail info from MissionDetailInfo if available.
        /// </summary>
        private void CollectMissionInfo(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<MissionUIHandler>();
                if ((object)handler == null) return;

                // Area/layer name
                try
                {
                    var tmp = handler.areaName;
                    if ((object)tmp != null)
                    {
                        string t = ReadTmpSafe(tmp);
                        if (!string.IsNullOrWhiteSpace(t))
                            items.Add(t);
                    }
                }
                catch { }

                // Mission detail info (description, location, rank)
                try
                {
                    var detailInfo = UnityEngine.Object.FindObjectOfType<MissionDetailInfo>();
                    if ((object)detailInfo != null && SafeCall.ProbeObject(detailInfo.Pointer))
                    {
                        // Skip if data is loading
                        bool isLoading = false;
                        try { isLoading = detailInfo.MissionDetailLoading; } catch { }
                        if (!isLoading)
                        {
                            // Description
                            string desc = ReadTmpSafe(detailInfo.dtDescription);
                            if (!string.IsNullOrWhiteSpace(desc))
                                items.Add(desc);

                            // Location
                            string loc = ReadTmpSafe(detailInfo.dtPointName);
                            if (!string.IsNullOrWhiteSpace(loc))
                                items.Add(Loc.Get("mission_location") + ": " + loc);

                            // Recommended rank
                            string rank = ReadTmpSafe(detailInfo.dtRecommendRank);
                            if (!string.IsNullOrWhiteSpace(rank))
                                items.Add(Loc.Get("mission_recommend_rank") + ": " + rank);
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read description text from SelectSpecialCommandUIHandler.descriptionText TMP.
        /// Works for SelectDogmaUIHandler and SelectTacticalCommandUIHandler too
        /// since they inherit from SelectSpecialCommandUIHandler.
        /// </summary>
        private void CollectSpecialCommandDescription(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<SelectSpecialCommandUIHandler>();
                if ((object)handler == null) return;

                try
                {
                    var tmp = handler.descriptionText;
                    if ((object)tmp != null)
                    {
                        string t = ReadTmpSafe(tmp);
                        if (!string.IsNullOrWhiteSpace(t))
                            items.Add(t);
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Read visible text from BackLogUIHandler.objectContent children.
        /// The backlog is a scroll view of dialogue history text.
        /// </summary>
        private void CollectBackLogContent(List<string> items)
        {
            try
            {
                var handler = _activeHandler.TryCast<BackLogUIHandler>();
                if ((object)handler == null) return;

                // Read TMP text from objectContent children
                try
                {
                    var content = handler.objectContent;
                    if ((object)content == null) return;

                    var tmps = content.GetComponentsInChildren<TextMeshProUGUI>(false);
                    if (tmps == null || tmps.Count == 0) return;

                    for (int i = 0; i < tmps.Count && i < 20; i++)
                    {
                        try
                        {
                            var tmp = tmps[i];
                            if ((object)tmp == null) continue;
                            string t = ReadTmpSafe(tmp);
                            if (!string.IsNullOrWhiteSpace(t))
                                items.Add(t);
                        }
                        catch { }
                    }
                }
                catch { }
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
        /// Uses cached data from navigation (ReadAssistLinkItemText) for main fields,
        /// supplemented by TMP fields for additional info (SP cost, EXP, etc.).
        /// TMP detail panel is safe to read during R key review because:
        /// - Our hook runs inside InputManager.Update()
        /// - AssistLinkManager.Update() ran in the previous frame
        /// - No cursor change during R key press → TMP shows correct data
        /// </summary>
        private void CollectAssistLinkItems(List<string> items)
        {
            try
            {
                var alm = GetActiveAssistLinkManager();
                if ((object)alm == null) return;
                if (alm.Pointer == IntPtr.Zero) return;

                // Effects are now read directly from TMP below (not from GetAssistLinkData
                // which returns stale effect fields during battle).

                // === Title: cmdName - personName + level ===
                string cmdName = _cachedALCommandName;
                string personName = _cachedALPersonName;

                // Read from TMP if cached is empty (TMP is current after previous frame's Update)
                try
                {
                    if (string.IsNullOrWhiteSpace(cmdName))
                    {
                        var tmp = alm.command_name_text;
                        if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) cmdName = t; }
                    }
                }
                catch { }
                try
                {
                    if (string.IsNullOrWhiteSpace(personName))
                    {
                        var tmp = alm.select_chara_name_text;
                        if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) personName = t; }
                    }
                }
                catch { }

                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(cmdName))
                {
                    sb.Append(cmdName);
                    if (!string.IsNullOrWhiteSpace(personName))
                    {
                        sb.Append(" - ");
                        sb.Append(personName);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(personName))
                {
                    sb.Append(personName);
                }

                // Add level: always use Loc format (TMP is bare number without prefix)
                string levelText = null;
                if (_cachedALLevel >= 0)
                {
                    levelText = Loc.Get("assistlink_level", _cachedALLevel + 1);
                }
                if (!string.IsNullOrWhiteSpace(levelText))
                {
                    sb.Append(" ");
                    sb.Append(levelText);
                }
                if (_cachedALRegistered)
                {
                    sb.Append(" [");
                    sb.Append(Loc.Get("assistlink_registered"));
                    sb.Append("]");
                }

                if (sb.Length > 0)
                    items.Add(sb.ToString());

                // === Command effect ===
                // ALWAYS read from TMP (GetAssistLinkData effects are stale during battle)
                string cmdEffect = null;
                try { var tmp = alm.command_effect_text; if ((object)tmp != null) cmdEffect = ReadTmpSafe(tmp); } catch { }
                if (!string.IsNullOrWhiteSpace(cmdEffect))
                    items.Add(Loc.Get("assistlink_command_effect", cmdEffect));

                // === Passive effect ===
                // ALWAYS read from TMP
                string passiveEffect = null;
                try { var tmp = alm.passive_effect_text; if ((object)tmp != null) passiveEffect = ReadTmpSafe(tmp); } catch { }
                if (!string.IsNullOrWhiteSpace(passiveEffect))
                    items.Add(Loc.Get("assistlink_passive_effect", passiveEffect));

                // Duration type (from TMP, supplementary)
                try
                {
                    var tmp = alm.duration_type_text;
                    if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add(t); }
                }
                catch { }

                // === SP cost ===
                try
                {
                    var tmp = alm.DoucmentSpiritText;
                    if ((object)tmp != null) { string t = ReadTmpSafe(tmp); if (!string.IsNullOrWhiteSpace(t)) items.Add("SP: " + t); }
                }
                catch { }

                // === EXP info ===
                try
                {
                    var tmpNow = alm.now_exp_text;
                    var tmpNext = alm.next_exp_text;
                    string nowExp = null, nextExp = null;
                    if ((object)tmpNow != null) nowExp = ReadTmpSafe(tmpNow);
                    if ((object)tmpNext != null) nextExp = ReadTmpSafe(tmpNext);
                    if (!string.IsNullOrWhiteSpace(nowExp) || !string.IsNullOrWhiteSpace(nextExp))
                        items.Add("EXP: " + (nowExp ?? "0") + " / " + (nextExp ?? "?"));
                }
                catch { }

                // Selection count (global counter, not item-specific)
                string selCount = null;
                try { var tmp = alm.NowSelectionCount_text; if ((object)tmp != null) selCount = ReadTmpSafe(tmp); } catch { }
                if (!string.IsNullOrWhiteSpace(selCount))
                    items.Add(Loc.Get("assistlink_selection_count", selCount));

                DebugHelper.Write($"AssistLink review: cmd=[{cmdName}] person=[{personName}] effect=[{cmdEffect}] passive=[{passiveEffect}]");
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
                            string desc = ReadTmpSafe(explanTmp);
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
                        string desc = ReadTmpSafe(explanTmp);
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

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"GMR: CollectNonButtonText GetComponentsInChildren error: {ex.GetType().Name}");
                    return;
                }
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

                        string t = ReadTmpSafe(tmp);
                        if (string.IsNullOrWhiteSpace(t) || t.Length <= 1) continue;

                        items.Add(t);
                        charCount += t.Length;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== Sort/Filter Text Monitoring =====

        /// <summary>
        /// Detect sort/filter TMP components from the current list handler.
        /// Called once when a new handler with a ListHandlerBase is found.
        /// Caches the TMP references for efficient per-poll monitoring.
        /// </summary>
        private void DetectSortFilterTmp()
        {
            try
            {
                if ((object)_listHandler == null || _listHandler.Pointer == IntPtr.Zero)
                    return;
                if (!SafeCall.ProbeObject(_listHandler.Pointer))
                    return;

                // Try each known list handler subtype that has sort/filter TMP
                var unitList = _listHandler.TryCast<UnitListHandler>();
                if ((object)unitList != null)
                {
                    _sortTmp = unitList.m_SortTypeText;
                    return;
                }

                var missionList = _listHandler.TryCast<MissionListHandler>();
                if ((object)missionList != null)
                {
                    _sortTmp = missionList.m_SortTypeText;
                    _filterTmp = missionList.m_FilterTypeText;
                    return;
                }

                var partsList = _listHandler.TryCast<PartsEquipSelectPartsListUIHandler>();
                if ((object)partsList != null)
                {
                    _sortTmp = partsList.m_SortTypeText;
                    _filterTmp = partsList.m_FilterTypeText;
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"GenericMenu: DetectSortFilterTmp error: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Check sort/filter TMP text for changes and announce.
        /// Called from main update loop each poll cycle.
        /// </summary>
        public void CheckSortFilterText()
        {
            if ((object)_sortTmp == null && (object)_filterTmp == null)
                return;

            try
            {
                // Read sort text
                if ((object)_sortTmp != null)
                {
                    string sortText = ReadTmpSafe(_sortTmp);
                    if (sortText != null && sortText != _lastSortText)
                    {
                        _lastSortText = sortText;
                        if (!string.IsNullOrWhiteSpace(sortText))
                        {
                            ScreenReaderOutput.Say(Loc.Get("sort_type", sortText));
                            DebugHelper.Write($"GenericMenu: Sort changed: {sortText}");
                        }
                    }
                }

                // Read filter text
                if ((object)_filterTmp != null)
                {
                    string filterText = ReadTmpSafe(_filterTmp);
                    if (filterText != null && filterText != _lastFilterText)
                    {
                        _lastFilterText = filterText;
                        if (!string.IsNullOrWhiteSpace(filterText))
                        {
                            ScreenReaderOutput.Say(Loc.Get("filter_type", filterText));
                            DebugHelper.Write($"GenericMenu: Filter changed: {filterText}");
                        }
                    }
                }
            }
            catch
            {
                // TMP destroyed - clear references
                _sortTmp = null;
                _filterTmp = null;
            }
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
