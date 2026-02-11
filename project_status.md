# SRWYAccess - Project Status

## Game Info

- Game: Super Robot Wars Y (超級機器人大戰Y)
- Developer: Bandai Namco Entertainment
- Engine: Unity (IL2CPP backend)
- Architecture: 64-bit
- Game directory: C:\Program Files (x86)\Steam\steamapps\common\SRWY

## Mod Info

- Mod name: SRWYAccess
- Multilingual support: Yes

## User

- Experience level: Little/None
- Preferred language: Chinese (中文)

## Setup Checklist

- [x] Game engine detected: Unity (IL2CPP)
- [x] Architecture detected: 64-bit
- [x] .NET SDK installed: 8.0.417
- [x] ILSpy (ilspycmd) installed: 9.1.0.7988
- [x] MelonLoader installed: v0.7.1 Open-Beta
- [x] Tolk DLLs copied to game directory
- [x] Game launched once with MelonLoader
- [x] MelonLoader log values confirmed
- [x] Game source code decompiled (2736 C# files)

## MelonLoader Log Values

- Game Name: SUPER ROBOT WARS Y
- Game Developer: Bandai Namco Entertainment
- Runtime Type: net6
- Unity Version: 2022.3.44f1
- Game Version: 1.2.0
- MelonGame attribute: `[assembly: MelonGame("Bandai Namco Entertainment", "SUPER ROBOT WARS Y")]`
- TargetFramework: `net6.0`

## Important Notes

- IL2CPP game: MelonLoader generated proxy assemblies in `MelonLoader/Il2CppAssemblies/`.
- SceneManager override errors in log - known MelonLoader v0.7.1 issue with Unity 2022 IL2CPP games.

## Current Phase

Phase H1+G1 (Dialogs & Tutorials) COMPLETE. Ready for Phase C1 (Story Dialogue).

## Completed Features

- A1: Basic Mod Framework - MelonMod entry point, csproj, build pipeline
- A2: Core Utilities - ScreenReaderOutput (Tolk P/Invoke), GameStateTracker (InputMode tracking), Loc (5-language localization)
- B1: Title Screen - Announces "Title screen. Press any key" on title
- B2: Main Menu Navigation - Announces menu items with position (e.g. "Continue, 1 of 7")
- H1: Dialog/Confirmation Reading - Auto-reads dialog text, tracks Yes/No/Select cursor, custom button labels
- G1: Tutorial Reading - Auto-reads tutorial title/body, tracks page changes, button navigation

## Build

- Source: `src/` directory
- Build: `dotnet build src/SRWYAccess.csproj --configuration Release`
- Output: `src/bin/Release/SRWYAccess.dll` → copy to game `Mods/` folder
