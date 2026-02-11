# Accessibility Mod Template

## User

- Blind, uses screen reader
- Programming experience: Little/None - explain concepts contextually
- Preferred language: Chinese (中文)
- User provides direction, Claude Code writes code independently and explains
- For uncertainties: Ask briefly, then act
- Screen reader-friendly output: NO tables with `|`, use lists instead

## Project Start

Setup completed. See `project_status.md` for details.

## Environment

- **OS:** Windows (Bash/Git Bash)
- **Game:** Super Robot Wars Y (超級機器人大戰Y)
- **Mod name:** SRWYAccess
- **Game directory:** C:\Program Files (x86)\Steam\steamapps\common\SRWY
- **Architecture:** 64-bit
- **Game engine:** Unity (IL2CPP)
- **Developer:** Bandai Namco Entertainment
- **Multilingual:** Yes

## Coding Rules

- **Handler classes:** `[Feature]Handler`
- **Private fields:** `_camelCase`
- **Logs/Comments:** English
- **Build:** `dotnet build SRWYAccess.csproj`

## Coding Principles

- **Playability, not simplification** - Make game playable as sighted players play it; only suggest cheats when unavoidable
- **Modular** - Separate input handling, UI extraction, announcements, game state
- **Maintainable** - Consistent patterns, easily extensible
- **Efficient** - Cache objects, avoid unnecessary processing
- **Robust** - Use utility classes, handle edge cases, announce state changes
- **Respect game controls** - Never override game keys, handle rapid key presses

Patterns: `docs/ACCESSIBILITY_MODDING_GUIDE.md`

## Before Implementation

**ALWAYS:**
1. Search `decompiled/` for actual class/method names - NEVER guess
2. Check `docs/game-api.md` for keys, methods, patterns
3. Use only "Safe Keys for Mod" (see game-api.md → "Game Key Bindings")

## References

- `docs/setup-guide.md` - Project setup interview
- `docs/ACCESSIBILITY_MODDING_GUIDE.md` - Code patterns and architecture
- `docs/localization-guide.md` - Text and announcement localization
- `docs/menu-accessibility-checklist.md` - Menu implementation checklist
- `docs/game-api.md` - Keys, methods, documented patterns
- `templates/` - Code templates
- `scripts/` - PowerShell helper scripts
