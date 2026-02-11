# SRWYAccess - Feature Plan (功能計畫)

## Overview

Features ordered by game flow - the order a player encounters them.

---

## Phase A: Foundation (基礎框架)

### A1. Basic Mod Framework
- Goal: Create mod that loads and announces "SRWYAccess loaded" via Tolk
- Classes: MelonMod base, Tolk integration
- Dependencies: None (first feature)
- Complexity: Simple

### A2. Accessibility Core Utilities
- Goal: Create shared utility classes for all features
- Components:
  - ScreenReaderOutput - Tolk wrapper for announcements
  - GameStateTracker - Track current game state via InputManager modes
  - LocalizationHelper - Get localized strings from game's system
- Dependencies: A1

---

## Phase B: Title & Main Menu (標題和主選單)

### B1. Title Screen
- Goal: Announce title screen, handle "Press Any Key" prompt
- Classes: TitleInputBehaviour, TitleState
- Harmony hook: TitleState initialization
- Keys: None needed (game handles input)
- Dependencies: A2
- Complexity: Simple

### B2. Main Menu Navigation
- Goal: Announce menu items when navigating (New Game, Continue, Options, etc.)
- Classes: MainMenuState, TitleUIButtonBehaviour
- Harmony hook: Menu cursor movement, ConfirmButton/CancelButton
- Keys: Game's own DPad/Confirm/Cancel
- Dependencies: B1
- Complexity: Medium

### B3. Options Menu
- Goal: Announce option categories and current values
- Classes: PageOptionScreen, OptionMenuUIInputBehaviour
- Harmony hook: Option selection, value changes
- Dependencies: B2
- Complexity: Medium

---

## Phase C: Adventure Mode (冒險模式 - 劇情對話)

### C1. Story Dialogue Reading
- Goal: Automatically read story dialogue text to screen reader
- Classes: MessageShowUnit, TalkStartUnit, EventDialogDataTable, DialogSystem
- Harmony hook: MessageShowUnit._Start, DialogSystem text updates
- Keys: None (auto-read on display)
- Dependencies: A2
- Challenge: Must intercept TextMeshPro text updates; async loading
- Complexity: Medium-High

### C2. Adventure Scene Announcements
- Goal: Announce scene transitions, character names, chapter titles
- Classes: AdventureState, Adv01State, AdventureInputBehaviour
- Harmony hook: State transitions
- Dependencies: C1
- Complexity: Medium

---

## Phase D: Strategy Part / Intermission (戰略部分 - 關卡間選單)

### D1. Strategy Top Menu
- Goal: Announce main intermission menu items (Units, Pilots, Shop, Save, etc.)
- Classes: StrategyTopUIHandler, StrategyTopUIBehaviour
- Harmony hook: Menu cursor movement
- Dependencies: A2
- Complexity: Medium

### D2. Unit Status Reading
- Goal: Read pilot/robot stats (name, level, HP, EN, stats, skills)
- Classes: PilotCalcData, RobotCalcData, StatusUIHandlers
- Harmony hook: Status screen open, tab switching
- Keys: F10 for quick status summary
- Dependencies: D1
- Complexity: Medium-High

### D3. Pilot Training
- Goal: Announce training options, stat values, costs
- Classes: PilotTrainingUIBehaviour, PilotTrainingStatusHandler
- Dependencies: D2
- Complexity: Medium

### D4. Robot Upgrade
- Goal: Announce upgrade options, current/max values, costs
- Classes: Robot upgrade UI handlers
- Dependencies: D2
- Complexity: Medium

### D5. Parts Equipment
- Goal: Navigate and equip power parts with announcements
- Classes: PartsEquipUIHandler, PartsEquipUIInputBehaviour, SelectPartsUIInputBehaviour
- Dependencies: D2
- Complexity: Medium-High

### D6. Shop
- Goal: Announce shop items, prices, purchase confirmations
- Classes: ShopUIHandler, ShopUIInputBehaviour
- Dependencies: D1
- Complexity: Medium

### D7. Save/Load
- Goal: Announce save slots, save/load confirmations
- Classes: SaveLoadManager, SaveLoadUIInputBehaviour, SaveConfirmDialogUIHandler
- Dependencies: D1
- Complexity: Simple-Medium

---

## Phase E: Tactical Part / Battle Map (戰術部分 - 戰鬥地圖)

### E1. Phase Announcements
- Goal: Announce turn start/end, player/enemy/ally phase
- Classes: TacticalPartState (PlayerPhase, EnemyPhase, ThirdParty)
- Harmony hook: Phase transitions
- Dependencies: A2
- Complexity: Simple

### E2. Unit Selection & Info
- Goal: Announce selected unit name, HP/EN, position when cursor moves on map
- Classes: PawnUnit, TacticalPartInputBehaviour, MapUnit
- Harmony hook: Cursor movement on tactical map
- Keys: F11 for detailed unit info
- Dependencies: E1
- Complexity: Medium-High

### E3. Unit Command Menu
- Goal: Announce unit commands (Move, Attack, Spirit, etc.)
- Classes: ButtonHandler (ButtonType enum), UnitCommandUIBehaviour
- Harmony hook: Command menu open, cursor movement
- Dependencies: E2
- Complexity: Medium

### E4. Movement Range & Targeting
- Goal: Announce movement range, terrain info, target selection
- Classes: Map system, PawnUnit.UnitLayer, GridsMaker
- Harmony hook: Movement mode, target selection
- Dependencies: E3
- Complexity: High

### E5. Weapon Selection
- Goal: Announce weapon name, power, range, ammo/EN cost, hit%
- Classes: WeaponCalcData, WeaponListItemHandler, TacticalPartWeaponListSelectedInputBehaviour
- Harmony hook: Weapon list open, cursor movement
- Dependencies: E3
- Complexity: Medium

### E6. Battle Preview
- Goal: Announce predicted damage, hit%, critical%, before confirming attack
- Classes: BattleSpecificSetting, BattlePreview.Uis, Calculate, Judgement
- Harmony hook: Battle preview display
- Dependencies: E5
- Complexity: Medium-High

### E7. Spirit Command Selection
- Goal: Navigate and use spirit commands with announcements
- Classes: SpiritCommandUtility, SpiritCommandData, TacticalPartSpiritButtonBehaviour
- Harmony hook: Spirit menu open, command selection
- Dependencies: E3
- Complexity: Medium

### E8. Battle Results
- Goal: Announce damage dealt/received, unit defeated, level up, morale changes
- Classes: ActionEndCheckUnit, ShottenDownCheckUnit, BattleAfterCheckUnit
- Harmony hook: Battle result events
- Dependencies: E1
- Complexity: Medium

### E9. Mission Objectives
- Goal: Announce victory/defeat conditions, mission progress
- Classes: MissionManager, MissionCheck, MissionPlayData
- Keys: F12 for mission objectives
- Dependencies: E1
- Complexity: Medium

---

## Phase F: Battle Animation (戰鬥動畫)

### F1. Battle Scene Narration
- Goal: Announce attacker/defender, weapon used, hit/miss/critical, damage
- Classes: BattleSceneInputBehaviour, BattleAnimState
- Harmony hook: Battle animation events
- Dependencies: E6
- Complexity: Medium

---

## Phase G: Tutorial System (教學系統)

### G1. Tutorial Reading
- Goal: Auto-read tutorial text when tutorials appear
- Classes: TutorialManager, TutorialWindow, TutorialUIHandler
- Harmony hook: TutorialWindow.SetInfoText, SetTitleText
- Dependencies: A2
- Complexity: Simple-Medium

---

## Phase H: Additional Features (附加功能)

### H1. Dialog/Confirmation Reading
- Goal: Auto-read all dialog boxes (yes/no, confirmations, etc.)
- Classes: DialogSystem
- Harmony hook: DialogSystem text updates
- Dependencies: A2
- Complexity: Simple

### H2. Encyclopedia/Library
- Goal: Browse library entries with screen reader
- Classes: EncyclopediaUIHandler, LibraryTopUIInputBehaviour
- Dependencies: D1
- Complexity: Medium

### H3. Mission Chart
- Goal: Navigate mission selection chart
- Classes: MissionChartUIInputBehaviour
- Dependencies: D1
- Complexity: Medium

### H4. Achievement Announcements
- Goal: Announce achievements when unlocked
- Classes: AchievementManager
- Dependencies: A2
- Complexity: Simple

---

## Recommended Build Order (建議順序)

1. **A1 + A2** - Foundation (must be first)
2. **B1 + B2** - Title & Main Menu (first contact)
3. **H1 + G1** - Dialogs & Tutorials (needed everywhere)
4. **C1** - Story Dialogue (adventure mode)
5. **D1 + D7** - Strategy Menu & Save/Load
6. **E1 + E3** - Phase Announcements & Unit Commands
7. **E2 + E5** - Unit Info & Weapon Selection
8. **E6 + E8 + F1** - Battle Preview & Results
9. **D2 + D3 + D4 + D5 + D6** - Full Strategy Features
10. **E4 + E7 + E9** - Advanced Tactical Features
11. **C2 + H2 + H3 + H4** - Polish & Extras

## Mod Keys Summary

- **F10** - Quick unit status summary
- **F11** - Detailed unit information
- **F12** - Mission objectives / victory conditions
