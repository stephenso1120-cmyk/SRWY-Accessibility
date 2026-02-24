========================================
SRWYAccess - Super Robot Wars Y Accessibility Mod
========================================

Version: 2.4
Game: Super Robot Wars Y
Supported Languages: English, Simplified Chinese, Traditional Chinese, Japanese, Korean


========================================
1. About the Game
========================================

Super Robot Wars Y is a tactical strategy RPG developed by Bandai Namco
Entertainment. The game features characters and mechs from multiple classic
mecha anime series. Players command units on a tactical grid map in
turn-based combat.

Engine: Unity 2022.3.44f1
Platform: Steam (Windows PC)
Architecture: 64-bit

The game has the following main scenes:
- Title Screen: Main menu after launching the game
- Adventure Mode (ADVENTURE): Story dialogue and cutscenes
- Tactical Mode (TACTICAL): Command units on the tactical grid map
- Battle Scene (BATTLE): Combat animations between units
- Strategy Mode (STRATEGY): Pre-battle preparation, upgrades, equipment, etc.
- Encyclopedia: View collected mech and character data


========================================
2. Prerequisites
========================================

Before using this mod, you need the following:

1. Super Robot Wars Y (Steam version)

2. MelonLoader v0.7.1 Open-Beta (mod loader)
   - Download: https://github.com/LavaGang/MelonLoader/releases
   - Download "MelonLoader.Installer.exe"
   - See installation instructions below

3. A screen reader (any of the following)
   - NVDA (free and open source, recommended): https://www.nvaccess.org/download/
   - JAWS
   - Any other screen reader supported by the Tolk library


========================================
3. Installation Guide
========================================

--- Step 1: Install MelonLoader ---

1. Download MelonLoader.Installer.exe
2. Run the installer
3. Click the "SELECT" button and locate the game executable:
   Default path: C:\Program Files (x86)\Steam\steamapps\common\SRWY\SUPER ROBOT WARS Y.exe
   (If you installed the game elsewhere, navigate to the correct path)
4. Make sure the version is set to v0.7.1
5. Check "Latest"
6. Click "INSTALL"
7. After installation, launch the game once and then close it
   (MelonLoader will automatically generate required folders on first run; this may take a while)
8. Verify that a "Mods" folder has appeared in the game directory

--- Step 2: Install the SRWYAccess Mod ---

Place the files from this archive as follows:

File -> Destination:

SRWYSafe.dll -> Game root directory
  e.g. C:\Program Files (x86)\Steam\steamapps\common\SRWY\SRWYSafe.dll

Tolk.dll -> Game root directory
  e.g. C:\Program Files (x86)\Steam\steamapps\common\SRWY\Tolk.dll

nvdaControllerClient64.dll -> Game root directory
  e.g. C:\Program Files (x86)\Steam\steamapps\common\SRWY\nvdaControllerClient64.dll

Mods\SRWYAccess.dll -> The Mods folder inside the game directory
  e.g. C:\Program Files (x86)\Steam\steamapps\common\SRWY\Mods\SRWYAccess.dll

After placement, the directory structure should look like this:

SRWY\  (game root directory)
  SUPER ROBOT WARS Y.exe  (game executable)
  GameAssembly.dll  (game core)
  SRWYSafe.dll  (mod crash protection) <- NEW
  Tolk.dll  (screen reader interface) <- NEW
  nvdaControllerClient64.dll  (NVDA bridge) <- NEW
  MelonLoader\  (MelonLoader folder, auto-generated)
  Mods\
    SRWYAccess.dll  (mod main file) <- NEW

--- Step 3: Launch the Game ---

1. Make sure your screen reader (e.g. NVDA) is running
2. Launch the game normally through Steam
3. Wait for the game to load (MelonLoader will display loading info in a console window)
4. The mod will automatically initialize after about 8 seconds
5. You should hear your screen reader start reading game UI content

--- Step 4: Recommended In-Game Settings ---

For the best experience, consider adjusting the following in game settings:
- Set battle animations to shortest or skip (speeds up combat)
- Adjust text speed to your preference


========================================
4. Game Keyboard Controls (Native)
========================================

These are the game's built-in keyboard controls (not added by the mod):

--- Basic Controls ---
W: Move up / Cursor up
S: Move down / Cursor down
A: Move left / Cursor left
D: Move right / Cursor right
Enter: Confirm / Select
Shift: Cancel / Back
Backspace: Cancel / Back (same as Shift)

--- Menu Controls ---
F: Open menu
G: Sort
Q: Switch tab left (L1)
E: Switch tab right (R1)
1: Switch tab left (L1, same as Q)
3: Switch tab right (R1, same as E)

--- Other ---
F1: Details / Help
Z: View story subtitle log (page up)
X: View story subtitle log (page down)


========================================
5. Mod Hotkeys
========================================

The following hotkeys are added by this mod. They do not conflict with any
native game controls.

--- Mod Controls ---
F2: Toggle mod on/off (enable/disable)
  - Announces "Mod enabled" or "Mod disabled"
  - F2 works even when the mod is disabled

F3: Reset mod state
  - Clears all internal state and re-detects the current game state
  - Press F3 if the mod behaves abnormally
  - Announces "Mod state reset"

F4: Toggle audio cues on/off
  - Enables/disables audio feedback when moving on the map
  - Announces "Audio cues on" or "Audio cues off"

--- Screen Review ---
R: Read current screen information
  - Collects and reads all visible information on the current screen
  - Automatically selects content based on the current scene
  - Tactical mode: reads mission objectives, battlefield overview, unit info
  - Menu mode: reads menu items and status info
  - Battle mode: reads both units' combat data

[ (Left bracket): Browse to previous item
  - Moves backward through the list of items collected by R
  - Reads one item at a time for careful review

] (Right bracket): Browse to next item
  - Moves forward through the list of items collected by R
  - Reads one item at a time

--- Tactical Map: Unit Stats ---
I: Read selected unit stats
  - Reads HP, EN, SP, Morale, armor, mobility, and weapons
  - Available in tactical modes when a unit is selected

--- Tactical Map: Enemy Unit Distance ---
(Only available in TACTICAL_PART mode)

; (Semicolon): Cycle to previous enemy unit
  - Sorted by distance from nearest to farthest
  - Announces: enemy name, direction, distance, HP

' (Apostrophe): Cycle to next enemy unit
  - Same as above, cycles in the opposite direction

Alt + Semicolon: Cycle to previous named/boss enemy unit
Alt + Apostrophe: Cycle to next named/boss enemy unit
  - Only cycles through special/named enemy units (bosses, etc.)

Ctrl + Semicolon: Cycle to previous enemy (sorted by lowest HP)
Ctrl + Apostrophe: Cycle to next enemy (sorted by lowest HP)
  - Helps find weakened enemy units to finish off

--- Tactical Map: Ally Unit Distance ---

. (Period): Cycle to previous ally unit
  - Sorted by distance from nearest to farthest
  - Announces: ally name, direction, distance, HP

/ (Slash): Cycle to next ally unit
  - Same as above, cycles in the opposite direction

--- Tactical Map: Unacted/Acted Units ---

Alt + Period: Cycle to previous unacted player unit
Alt + Slash: Cycle to next unacted player unit
  - Helps find units that can still act this turn

Ctrl + Period: Cycle to previous acted player unit
Ctrl + Slash: Cycle to next acted player unit
  - Helps check which units have already acted

--- Tactical Map: Mission Destination ---
Alt + Backslash: Announce mission destination point direction/distance
  - Shows direction, distance, and path to mission objective points
  - Useful for missions that require moving units to specific locations
  - Sorted by nearest destination first

Ctrl + Backslash: Announce enemy closest to mission destination point
  - Shows the enemy unit nearest to any mission objective
  - Announces enemy name, distance to point, direction/distance from cursor, HP

--- Distance General ---
\ (Backslash): Repeat last distance announcement
  - Re-announces the last unit you looked at
  - Works even after the cursor has moved; updates distance and direction

P: Path prediction to last queried target
  - Announces the path direction and distance to the last target you queried

--- Tactical Map: Range Query ---

= (Equals): Announce movement range
  - Reads the selected unit's movement range in tiles

- (Minus): Announce attack range
  - Reads the selected unit's weapon attack range


========================================
6. Automatic Features
========================================

The following features work automatically without any key presses:

--- Menu Reading ---
- Automatically reads the current menu item when the cursor moves
- Supports all game menus: tactical commands, shops, upgrades, equipment,
  spirit commands, etc.
- Re-reads the current item when switching tabs (Q/E/1/3)
- Automatically reads detailed character/mech info on status screens
- System settings: reads setting description and current value when navigating,
  announces new value when changed with left/right
- Parts equip: reads slot number and equipped part name, or "Equipable" if
  the slot is empty; reads part description after a brief delay
- Parts equip / Pilot training: announces the unit or pilot name when
  switching between units with Q/E

--- Story Dialogue ---
- Automatically reads character dialogue in adventure mode
- Announces the speaker's name and dialogue text
- Automatically reads chapter titles

--- Battle Subtitles ---
- Automatically reads voice line subtitles during battle scenes
- Includes character name and dialogue

--- Battle Results ---
- Automatically reads after battle ends:
  - Experience gained
  - Credits earned
  - Skill programs gained
  - Parts gained

--- Battle Animation Setting ---
- Announces when the battle animation setting changes
  (On / Face-in only / Off)

--- Tutorial Prompts ---
- Automatically reads tutorial window titles and descriptions
- Announces page numbers (e.g. "Page 1 of 3")

--- Dialog Boxes / Confirmation Windows ---
- Automatically reads confirmation dialog content when it appears
- Tracks Yes/No selection cursor and announces the current choice

--- Turn Announcements ---
- When the enemy turn ends and it returns to the player turn:
  - Automatically announces "Player turn, X/Y units can act"
  - Helps track the number of remaining actionable units

--- Cursor Position ---
- When the cursor moves on the tactical map:
  - Unit present: reads unit name, pilot, HP
  - No unit: reads terrain information

--- Audio Cues (toggle with F4) ---
- Plays audio tones when the cursor moves on the map:
  - Empty tile: low tone (330Hz) - soft short beep
  - Ally unit: medium tone (580Hz) - two ascending beeps
  - Enemy unit: high tone (880Hz) - sharp buzz
  - Different pitches allow instant spatial awareness
- Menu cursor movement: light tick (500Hz)
- Player turn: ascending tone sweep
- Enemy turn: descending tone sweep
- Scene change: notification tone (660Hz)

--- Window Focus ---
- The mod automatically pauses when the game window is not focused
- Prevents the mod from capturing keystrokes from other applications
- Resumes automatically when the game window is focused again


========================================
7. Frequently Asked Questions
========================================

Q: The mod is installed but the screen reader doesn't speak?
A: Please verify:
  1. NVDA or another screen reader is running
  2. Tolk.dll and nvdaControllerClient64.dll are in the game root directory
  3. SRWYSafe.dll is in the game root directory
  4. SRWYAccess.dll is in the Mods folder
  5. MelonLoader is correctly installed (a console window appears when the game starts)

Q: The mod suddenly stopped speaking?
A: Press F3 to reset the mod state. If that doesn't help, press F2 to
  disable and then re-enable the mod.

Q: The game crashed. What should I do?
A: This mod includes a crash protection mechanism (SRWYSafe.dll).
  Most crashes are automatically recovered. If the game closes completely:
  1. Restart the game
  2. The mod will automatically reload
  3. Your game progress is not lost (if you saved previously)

Q: Do the mod keys conflict with game controls?
A: The mod uses keys (F2-F4, R, I, brackets, semicolon, apostrophe, etc.)
  that do not conflict with any native game controls. If you experience
  any issues, press F3 to reset.

Q: What languages are supported?
A: The mod automatically follows the game's display language setting.
  Supported languages: English, Simplified Chinese, Traditional Chinese,
  Japanese, Korean. The mod re-checks the game locale every ~1 second to
  handle late locale changes during game startup.

Q: How do I uninstall the mod?
A: Delete the following files:
  - Mods\SRWYAccess.dll
  - SRWYSafe.dll
  For a complete removal, also delete Tolk.dll and nvdaControllerClient64.dll.
  MelonLoader can be uninstalled through its installer.


========================================
8. Quick Reference
========================================

--- Native Game Keys ---
WASD: Movement / Cursor
Enter: Confirm
Shift/Backspace: Cancel
F: Menu
G: Sort
Q/1: Tab left
E/3: Tab right
F1: Details
Z/X: Subtitle log

--- Mod Keys ---
F2: Toggle mod
F3: Reset mod
F4: Toggle audio cues

R: Read screen
[: Previous item
]: Next item
I: Unit stats

;: Previous enemy
': Next enemy
Alt+;: Previous named enemy
Alt+': Next named enemy
Ctrl+;: Previous enemy (by HP)
Ctrl+': Next enemy (by HP)

.: Previous ally
/: Next ally
Alt+.: Previous unacted
Alt+/: Next unacted
Ctrl+.: Previous acted
Ctrl+/: Next acted

\: Repeat last
Alt+\: Mission destination
Ctrl+\: Enemy nearest to dest
P: Path prediction

=: Movement range
-: Attack range


========================================
Thank you for using the SRWYAccess accessibility mod!
Enjoy the game!
========================================
