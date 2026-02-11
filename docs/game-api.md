# Super Robot Wars Y - Game API Documentation

## Overview

- Game: Super Robot Wars Y (超級機器人大戰Y)
- Developer: Bandai Namco Entertainment
- Engine: Unity 2022.3.44f1 (IL2CPP)
- Architecture: 64-bit
- MelonLoader: v0.7.1 Open-Beta
- Runtime: net6
- Game Version: 1.2.0
- Main Namespace: Com.BBStudio.SRTeam

## Singleton Access Points

### MonoBehaviour Singletons (SingletonMonoBehaviour<T>)

- **GameManager** - Main game state, soft resets, game flow
- **InputManager** - Player input handling, 17 input modes (ENTRY, LOGO, TITLE, MAIN_MENU, ADVENTURE, TACTICAL_PART, BATTLE_SCENE, STRATEGY_PART, GAME_CLEAR, etc.)
- **UIManager** - UI generation, unit info positioning, sortie preparation
- **SaveLoadManager** - Save/load, save point management
- **EventManager** - In-game events, event sequencing, BGM suspension
- **MissionManager** - Mission data and progression
- **SoundManagerADX** - Sound/BGM playback via CRI ADX
- **EffectManager** - Visual effects, sprite studio animations
- **CriAdxManager** - Low-level CRI ADX audio driver
- **TutorialManager** - Tutorial data loading and activation
- **AssistManager** - Assist crew system
- **DebugManager** - Debug features

### Non-MonoBehaviour Singletons (Singleton<T>)

- **DataManager** - Core game data loading, asset management
- **LocalizationManager** - Localization strings, language selection (6 languages)
- **GamePlayDataManager** - Gameplay-specific data
- **LibraryEntryManager** - Library/encyclopedia entries
- **AchievementManager** - Achievement tracking
- **PRPManager** - Pilot Ranking Points
- **DlcManager** - DLC content
- **MissionLayerManager** - Mission layer/difficulty
- **EndMessageManager** - End-game messages
- **BattleManager** - Battle state and progression

### Other Important Singletons

- **SteamManager** - Steamworks integration
- **TimerHandler** - Game timer, frame counting
- **PanelObjectPool** - UI panel object pooling
- **DicDataHandler** - Dictionary-based data access
- **RobotImageHandler** - Robot artwork caching

## Game Key Bindings

Game uses **Unity New Input System** (action-based, NOT legacy KeyCode).

### Primary Actions

- **Confirm** - Cross/Enter - Select/execute
- **Cancel** - Circle/ESC - Close/back
- **Select** - Square - Secondary selection/status
- **Detail** - Triangle - Show detail/help
- **Menu** - Options button - System menu
- **View** - Share/View button - Alternative view

### Navigation

- **DPad** - Up/Down/Left/Right menu navigation (with continuous hold)
- **Left Stick** - Directional input, cursor movement
- **Right Stick** - Camera control/secondary

### Shoulder Buttons

- **L1/R1** - Tab switching, shortcuts
- **L2/R2** - Special actions
- **L3/R3** - Stick click, toggles

### Mouse

- **MousePosition** - Cursor tracking
- **MouseDelta** - Mouse movement delta

### Special

- **PressAnyKey** - Skip/confirm prompts
- **OpenOptionMenu** - System menu access

### Safe Keys for Mod

NOT used by the game, safe for accessibility mod:

- **F1-F12** (especially F10, F11, F12)
- **Tab** (when not in text input)
- **Grave/Tilde** (~)
- **Insert, Delete**
- **ScrollLock, Pause/Break, PrintScreen**
- **Mouse middle button** (Button 3)
- **Mouse scroll wheel**
- **Numpad keys** (if separate from main numbers)

## UI System

### Architecture

Handler-based with separation of concerns:

- **UIHandlerBase** (MonoBehaviour) - Primary base for all UI handlers
  - Fields: controlBehaviour, currentCursorIndex, isPointer, canHoldInput
  - Input methods: ConfirmButton(), CancelButton(), MenuButton(), DetailButton(), SelectButton(), ViewButton()
  - Navigation: MoveCursor(Vector2), MoveCursorUp/Down/Left/Right()
  - Lifecycle: Initialize(), BindEvent(), UnbindEvent()

- **DefaultUIHandler** - Simple handler with button object list
- **ListHandlerBase** - List-based UI with scrolling, visibility culling
- **ListItemHandler** - Individual list items with highlight/visibility

### Dialog System (DialogSystem.cs)

Central dialog management:

- Dialog types: CommonBlue, CommonRed, CommonYellow, CommonLong1, CommonLong2
- Yes/No dialogs: YesNoBlue, YesNoRed, YesNoYellow, YesNoLong
- Selection: Select2, Select3
- Status: None → Busy → Finish → End
- Returns: Decision, Cancel

### Text Display

- Uses **TextMeshPro** (Il2CppTMPro)
- btnTextData structure: CommandText (direct), CommandTextKey (localization key), GuideText
- SetText() and .text = patterns

### Button Types (ButtonHandler.ButtonType)

Game actions: Persuade, Move, Attack, Split, AssistLink, LandformAction, Deformation, Collect, Departure, Special, Special2, Fix, Supply, Parts, Rest, PhaseEnd, Mission, TacticalSituation, UnitList, Auto, Search, System, Save

### Grid Navigation

- GridNavigationHelper - Cell-based grid navigation
- Row/column selection for status screens and equipment grids

### Key UI Handlers (100+ total)

- DialogSystem - Dialog/message windows
- ButtonHandler - Button components
- ShopUIHandler - Shop interface
- MissionUIHandler - Mission selection
- StatusUIHandlers (Pilot/Robot/Weapon)
- PartsEquipUIHandler - Equipment management
- StrategyTopUIHandler - Strategy screen
- NormalBattleUIHandler - Battle UI
- EncyclopediaUIHandler - Encyclopedia/library
- TutorialWindow - Tutorial display
- SaveConfirmDialogUIHandler - Save confirmation

## Game Mechanics

### Pilot System (Pilot.cs)

- Level/Experience: LevelMax, DefaultLevelUpExp, ExpMax
- Morale: MoraleMax, DefaultMoraleMin, DefaultMoraleMax
- Ace system: AceScore, SuperAce scoring
- Skills, SpiritCommands
- Support: SupportAttackMax, SupportDefenseMax
- BelongRobot reference

### Robot System (Robot.cs)

- Parameters: HP, armor, mobility (via RobotValueSet)
- MovePower, LandAdaptation (terrain)
- PowerPartsSlotMax equipment slots
- CustomBonus, UpgradeBonus
- Skills, Weapons arrays
- Size system (SizeText class)

### Weapon System (WeaponCalcData)

- Power, RangeMin, RangeMax, Sight
- Critical rate, BulletMax (ammo), ENCost
- LandAdaptation, MoraleCondition
- Map weapons: MapwFiringType, MapwMatrix, MapwRange
- Flags: BarrierPenetration, IgnoreSize, Counter, UseMoveAfter, NotConsumeResources

### Battle System

- PawnUnit.BattleActionType: Attack, Counter, Guard, Avoid, UnableAction, UnableCounter
- PawnUnit.UnitLayer: Standing, Flying, Sinking, Space, None
- TacticalPartState phases: StageStart, MapStart, PlayerPhase, EnemyPhase, GameOver
- GamePhase: Player, Enemy, ThirdParty
- Calculator: Calculate.cs (damage), Judgement.cs (hit/crit), WeaponCheck.cs (validity)
- BattleSpecificSetting: damage/hit/critical modifications per battle

### Morale System (MoraleAddedValueData)

- shotDown: +morale on enemy defeat
- allyShotDownAnEnemy: +morale when ally defeats enemy
- allysShotDown: -morale when ally is defeated
- avoid: +morale on successful evasion
- hit: +morale on successful hit
- attacked: +morale when attacked
- miss: -morale on miss

### Spirit Command System

- SpiritCommandUtility: GetTargetableList(), GetSpiritCommandOwnerList()
- SpiritCommandUsableType for usage restrictions
- SpiritCommandData for command definitions
- TacticalPartState.SpiritCommandStatus: None, Menu, ListSelect, MapSelect

### Deformation System

- Trasformation (mecha transformation)
- Retrofit, ArmorPurge (unit modifications)
- United, Separated (combine/separate units)

### Data Architecture Pattern

- [Type]BaseData - Static definition data
- [Type]CalcData - Runtime calculated stats
- [Type]UserData - Player-modified state
- [Type]LastData - Snapshot for comparison

## Status Systems

### Pilot Stats (PilotCalcData)

- Name, FullName, Level
- Parameters (PilotValueSet) - hit, evasion, critical, defense
- SPSortie (Spirit Points at sortie)
- MoraleSortie, MoraleMax
- Skills, SpiritCommands
- AceScore, Rank, AceBonus
- SupportAttackMax, SupportDefenseMax
- ForceValue

### Robot Stats (RobotCalcData)

- Name, FormalName, MovePower
- Parameters (RobotValueSet) - HP, armor, mobility
- LandAdaptation (terrain compatibility)
- PowerParts array, PowerPartsSlotNumber
- CustomBonus, UpgradeBonus
- Skills, Weapons
- ForceValue, CostToMoveOne

### Status Effects (TurnEffect)

- Key (identifier), Value (data)
- SetupPhase, SetupTurn, SetupCount (duration)
- IsOverTurn() for expiration check
- WeaponDebuffType tracking on Pawn

### StatusAttachDatas

- Pilot skills, Robot skills, Power parts
- Ace/Custom/FullCustom bonuses
- Ally effects, Assist passive/active abilities
- Spirit command information

## Event Hooks for Harmony

### Game State Transitions (GameStateHandler)

- EntryState → LogoState → TitleState → MainMenuState
- MainMenuState → AdventureState / StrategyPartState
- StrategyPartState → TacticalPartState (battle)
- TacticalPartState → BattleAnimState (combat animation)
- GameClearState (mission complete)

### Critical Patchable Event Commands

- **MessageShowUnit** - Message display (hookable for screen reader)
- **BattleBeforeCheckUnit** - Battle pre-checks
- **BattleAfterCheckUnit** - Battle post-actions
- **ActionEndCheckUnit** - Action completion
- **GameOverCheckUnit** - Game over detection
- **ShottenDownCheckUnit** - Unit defeated
- **TalkStartUnit**, **TalkStartIfFlagUnit** - Dialogue triggers
- **PawnParameterCheckUnit** - Unit stat checks
- **PawnParameterSettingUnit** - Unit stat modifications
- **MissionResultStartUnit** - Mission completion

### UI Handler Lifecycle Hooks

- Initialize() - Setup
- BindEvent() / UnbindEvent() - Input registration
- ConfirmButton() / CancelButton() - Input handling
- SetActive(true/false) - Show/hide
- OnEnable() / OnDisable() - Unity lifecycle

### Input System Hooks

- InputManager mode switching (17 modes)
- InputBehaviourBase virtual Handle* methods
- UIInputBehaviourBase HoldType tracking

## Localization System

### Supported Languages (LocaleID)

- zh_CN - Simplified Chinese (簡體中文)
- zh_TW - Traditional Chinese (繁體中文)
- zh_HK - Hong Kong Chinese (香港中文)
- en_US - English (英文)
- ja_JP - Japanese (日文)
- ko_KR - Korean (韓文)

### Key Methods (LocalizationManager)

- GetLocaleID() - Current language
- GetString(tableName, key) - Get localized string (sync)
- GetStringAsync(table, key, callback) - Get localized string (async)
- GetStringTable(tableName) - Get string table
- SelectedLocale(LocaleID) - Change language
- IsJapanese() - Check if Japanese
- IsExistsStringTable(tableName) - Check if table exists

### Font Handling

- LocalizeTmpFontEvent - Font switching per language
- Dynamic CJK font support

### For Mod Integration

- Use LocalizationManager.Instance to access
- Get current language: GetLocaleID()
- Can create custom string tables or use direct strings
- Font switching handled automatically by game

## Tutorial System

### Tutorial Types (21 total)

- BaseBattleRule - Basic combat rules
- GameSequence - Game flow
- Mission - Mission system
- SurvivalMission - Survival mode
- UnitMenu - Unit command menu
- SpiritCommand - Spirit commands
- Morale - Morale mechanics
- Auto - Auto-battle
- RobotUpgrade - Mech upgrades
- PilotTraining - Pilot development
- PowerParts - Power parts
- AssistLink - Assist link system
- AssistCrew - Crew assist
- TransferAndConversion - Unit transfer
- Shop - Shop system
- AllyRank - Ally ranking
- DataBase - Database
- STGMemory - STG memory
- STGMemoryExpansion - Expanded STG memory
- AfterClear - Post-clear content
- Replay - Replay

### Key Methods (TutorialManager)

- IsTutorialShowing() - Check if tutorial active
- CheckTutorialState(Type) - Check if shown before
- ActiveTutorialEvent(Type, callback) - Show tutorial
- ForceCloseTutorial() - Close immediately

### Tutorial UI (TutorialWindow)

- titleText (TextMeshProUGUI) - Title
- infoText (TextMeshProUGUI) - Content
- tutorialImage (Image) - Illustration
- pageText / totalPageText - Page indicators
- SetActive(bool) - Show/hide

### Tutorial Data Structure

- TutorialData → TutorialInfo → TutorialPageData
- Each page: SpriteID (image), TextDataId (localized text)
- History tracking prevents repeat display
