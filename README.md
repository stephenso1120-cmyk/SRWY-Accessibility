# SRWYAccess - Super Robot Wars Y Accessibility Mod

Version: 2.5
Game: Super Robot Wars Y (Steam)
Supported Languages: English, Simplified Chinese, Traditional Chinese, Japanese, Korean

## About

SRWYAccess is an accessibility mod that makes Super Robot Wars Y playable with a screen reader. It provides automatic reading of menus, dialogue, battle information, and tactical map navigation through audio feedback.

## Prerequisites

1. **Super Robot Wars Y** (Steam version)
2. **MelonLoader v0.7.1 Open-Beta** - [Download](https://github.com/LavaGang/MelonLoader/releases)
3. **A screen reader** - NVDA (recommended), JAWS, or other Tolk-compatible reader

## Installation

### Step 1: Install MelonLoader

1. Download and run MelonLoader.Installer.exe
2. Click SELECT and locate the game executable:
   `C:\Program Files (x86)\Steam\steamapps\common\SRWY\SUPER ROBOT WARS Y.exe`
3. Set version to v0.7.1, check "Latest", click INSTALL
4. Launch the game once and close it (generates required folders)

### Step 2: Install the Mod

Place files from the release archive:

- `SRWYSafe.dll` -> Game root directory (e.g. `SRWY\SRWYSafe.dll`)
- `Tolk.dll` -> Game root directory (e.g. `SRWY\Tolk.dll`)
- `nvdaControllerClient64.dll` -> Game root directory (e.g. `SRWY\nvdaControllerClient64.dll`)
- `Mods\SRWYAccess.dll` -> Mods folder (e.g. `SRWY\Mods\SRWYAccess.dll`)

### Step 3: Launch

1. Start your screen reader (e.g. NVDA)
2. Launch the game through Steam
3. The mod initializes automatically after ~8 seconds

## Game Keyboard Controls (Native)

- W/A/S/D: Movement / Cursor
- Enter: Confirm
- Shift / Backspace: Cancel
- F: Open menu
- G: Sort
- Q / 1: Tab left (L1)
- E / 3: Tab right (R1)
- F1: Details
- Z / X: Story subtitle log (page up/down)

## Mod Hotkeys

### Mod Controls

- F2: Toggle mod on/off
- F3: Reset mod state (clears internal state, re-detects game state)
- F4: Toggle audio cues on/off

### Screen Review

- R: Read current screen information
- [: Browse to previous item
- ]: Browse to next item

### Tactical Map: Unit Info

- I: Read selected unit stats (HP/EN/SP/Morale/armor/mobility/weapons)

### Tactical Map: Enemy Distance

- ;: Cycle to previous enemy (sorted by distance)
- ': Cycle to next enemy
- Alt+;: Cycle to previous named/boss enemy
- Alt+': Cycle to next named/boss enemy
- Ctrl+;: Cycle to previous enemy (sorted by lowest HP)
- Ctrl+': Cycle to next enemy (sorted by lowest HP)

### Tactical Map: Ally Distance

- .: Cycle to previous ally
- /: Cycle to next ally

### Tactical Map: Unacted/Acted Units

- Alt+.: Previous unacted player unit
- Alt+/: Next unacted player unit
- Ctrl+.: Previous acted player unit
- Ctrl+/: Next acted player unit

### Tactical Map: Mission Destination

- Alt+\: Announce direction and distance to mission destination points
- Ctrl+\: Announce the enemy closest to mission destination point

### Tactical Map: Other

- \: Repeat last distance announcement
- P: Path prediction to last queried target
- =: Announce movement range
- -: Announce attack range

### Sortie Preparation

- \: Read selected unit/ship counts (updates dynamically as units are selected)

## Automatic Features

### Menu Reading

- Automatically reads menu items when cursor moves
- Supports all game menus: tactical commands, shops, upgrades, equipment, spirit commands, etc.
- Re-reads current item when switching tabs (Q/E)
- Reads character/mech info on status screens
- System settings: reads setting description and current value, announces changes on left/right
- Parts equip: reads slot name and equipped part (or "Equipable" if empty), with part description
- Parts equip / Pilot training: announces unit/pilot name on Q/E switch
- Tactical command menus: announces unit name when switching units with Q/E

### Story Dialogue

- Reads character dialogue in adventure mode (speaker name + text)
- Reads chapter titles

### Battle

- Reads voice line subtitles during battle scenes
- Reads battle results: experience, credits, skill programs, parts gained
- Announces battle animation setting changes

### Turn Announcements

- Announces "Player turn, X/Y units can act" on turn start

### Cursor Position

- Unit present: reads unit name, pilot, HP
- No unit: reads terrain information

### Dialog / Tutorial

- Reads confirmation dialog content and tracks Yes/No cursor
- Reads tutorial window titles, descriptions, and page numbers

### Audio Cues (toggle with F4)

- Map cursor: empty=low tone (330Hz), ally=medium (580Hz), enemy=high (880Hz)
- Menu: tick on cursor move (500Hz)
- Turns: ascending sweep (player), descending sweep (enemy)
- Scene change: notification tone (660Hz)

### Window Focus

- Mod pauses when the game window is not focused (Alt-Tab safe)

## Changelog

### v2.5 (2026-02-28)

- New: Q/E unit switching in tactical command menus now announces the new unit name together with the menu item
- Improved: All announcement timings optimized for faster response (~30-50% faster across all features)
- Improved: Adventure→next scene transition ~930ms faster (subtitle reading starts sooner)
- Improved: Guard mode and search cooldown now run concurrently, reducing transition delays
- Improved: Adventure blackout reduced from ~1s to ~400ms with SEH protection

### v2.45 (2026-02-27)

- New: Mission selection screen automatically reads mission details when cursor moves (description, location, recommended rank)
- New: Sortie preparation screen - press \ to read selected unit/ship counts (dynamically updates as units are selected/deselected)

### v2.4 (2026-02-25)

- New: Ctrl+\ announces the enemy unit closest to mission destination point (name, distance to point, direction/distance from cursor, HP)

### v2.3.5 (2026-02-24)

- New: Alt+\ announces direction and distance to mission destination points on tactical map
- New: Alt+;/' cycles through named/boss enemy units
- New: Ctrl+;/' cycles through enemies sorted by lowest HP
- Changed: Modifier key for unacted/acted units changed from Shift to Alt (Alt+.// and Ctrl+.//)

### v2.3 (2026-02-22)

- System options menu reading with setting descriptions and value changes
- Parts equip slot reading with part descriptions
- Q/E unit/pilot name switching announcements

## FAQ

**Q: Screen reader doesn't speak?**
Verify: screen reader is running, Tolk.dll + nvdaControllerClient64.dll + SRWYSafe.dll in game root, SRWYAccess.dll in Mods folder, MelonLoader installed correctly.

**Q: Mod stopped speaking?**
Press F3 to reset. If that doesn't help, F2 to toggle off/on.

**Q: Game crashed?**
SRWYSafe.dll provides crash protection. Most crashes auto-recover. If the game closes, restart it - progress is not lost if saved.

**Q: How to uninstall?**
Delete: Mods\SRWYAccess.dll, SRWYSafe.dll. Optionally delete Tolk.dll and nvdaControllerClient64.dll.

## Technical Details

- Engine: Unity 2022.3.44f1 (IL2CPP)
- Mod loader: MelonLoader v0.7.1 Open-Beta
- Runtime: .NET 6
- Hook: NativeDetour on InputManager.Update()
- Crash protection: VEH + setjmp/longjmp (SRWYSafe.dll)

## License

This mod is provided as-is for accessibility purposes.
