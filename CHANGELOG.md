# Changelog

## 1.0.1 — 2026-11-21

- Accept exported lines containing `(SET)` codes and collector numbers, including PLST codes such as `CM2-235`.
- Accept known double-faced and split card pairs written with a single slash.
- Normalize Unicode accents while preserving Forge's canonical card names.
- Preserve exact card titles containing parentheses or literal slash characters, and merge printed and plain entries for the same card.

## 1.0.0 — 2026-11-21

Initial public release.

- Paste and save 100-card Commander decks directly into Forge's configured deck folder.
- Accept ordinary quantities, `1x` quantities, tabs, chat backslashes, and double-faced card names.
- Check card names against the installed Forge database.
- Select a default commander and mark alternate choices for Forge's pre-game Commander selector.
- Preserve existing decks with confirmation and replacement backups.
- Include a custom icon, portable Windows executable, source code, and local build/test scripts.
