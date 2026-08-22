# Articy VoiceOver Tools

An Articy:draft X macro plugin for managing voice-over asset naming, cleanup, and translation stripping across DialogueFragments. Internal plugin id: `Kurisu.VoiceOverTools`.

## Features

### Rename Voice-Overs (All / Selected)

Scans DialogueFragments and proposes per-asset renames so each VO file follows a `{hexId}_{culture}` convention. A preview window shows the plan:

- Each VO reference is categorized: **Already correct**, **Will rename**, **Display name update**, **Target file exists (skip)**, **Source file missing (skip)**
- Click the category badges above the list to filter by category
- Each row shows the file-name and display-name *before → after*
- Click any row and then **Show Fragment** or **Show VO Asset** to jump to it in Articy's main view (window stays open)
- Choose **Dry run** to preview only, or **Execute rename** to apply

### Audit Voice-Overs

Read-only diagnostic that scans every DialogueFragment and flags VO issues:

- **Missing**: a text property has voice-over enabled but no asset assigned for one or more languages
- **Corrupted**: the assigned asset is broken — invalid proxy, missing `AbsoluteFilePath`, file missing on disk, or 0-byte file
- **Overlapping**: a single audio asset is referenced by multiple distinct fragments (sometimes intentional, sometimes a bug — the audit just surfaces it)

Same window pattern as the others — category filter toggles, click-to-navigate (**Show Fragment** / **Show VO Asset**).

### Find Duplicate Translations

Scans every DialogueFragment's localized text properties and groups together fragments whose **non-reference-language** text is byte-identical. Useful for spotting copy-pasted translations or accidentally-shared lines that should differ.

- One group per `(language, shared-text)` pair. Each member row shows its own reference-language text alongside the shared offending text, so it's easy to see when the duplicate is suspicious (different reference texts → probable bug) versus expected (all members say the same thing in the reference language too).
- **Reference language** picker — defaults to the project's primary language. Changing it re-runs the scan with the new language excluded from the offending side.
- **Comparison rules** (re-runs the scan): case-insensitive, collapse whitespace, include groups where every member shares the same reference text (off by default — those are usually justified duplicates).
- **View filters** (no rescan): offending language, character (speaker), navigator path.
- Per-row buttons: **Clear** wipes just this fragment's translation in the offending language; **Clear from others** wipes everyone *except* this row.
- Footer **Clear all filtered offending texts** — bulk-clears every visible row's translation, with a confirmation showing the breakdown by language. Reference-language text is never touched.

### Find Multi-bound Voice-Overs

Inverts the audit's *Overlapping* view into a per-asset listing: one entry per audio asset that's referenced by more than one DialogueFragment, with a sub-row for each binding.

- Per-asset header shows display name, file name, on-disk size, full path, plus **▶ Play** (uses the system audio decoder) and **Show VO Asset** (jump to the asset in Articy).
- Per-binding sub-rows show speaker, language, property, fragment ID, path, line text, plus **Clear** (unbind just this slot) and **Clear from others** (keep this binding, unbind all others on this asset).
- Filters: language, character, navigator path. Optional toggle to also flag assets bound to the same fragment via multiple property/language slots (off by default — only inter-fragment overlaps are surfaced).
- Clearing uses the API-supported null assignment (`text.VoiceOverReferences[culture] = null`), confirmed against the live `LocalizationHelper.SetAudioSample` chain.

### Clean Up Orphaned Voice-Overs

Finds audio assets that no DialogueFragment references via its `VoiceOverReferences` and offers three actions:

- **Dry run** — list only, no changes
- **Delete from Articy only** (recommended) — removes the asset entries; Articy cleans up the on-disk files on session close, and the action stays reversible via Undo until then
- **Delete from Articy AND disk immediately** — permanent, guarded behind a confirmation dialog

### Remove Translations and Voice-Overs

Right-click a selection (a Flow, Dialogue, or DialogueFragment) and strip all non-primary-language translations and voice-over references. VO references are pointed at a silent-placeholder asset that's cleaned up at session close.

## Installation

1. Download the latest `.mdk` from the [`releases/`](./releases) folder.
2. Install it via Articy:draft X's **Package Manager** (the standard plugin install path for the app — see Articy's [Packaging a plugin](https://www.articy.com/adxdevkit/html/packaging_a_plugin.htm) docs).
3. Commands appear in the ribbon (global scope, e.g. "Rename All Voice-Overs", "Clean Up Orphaned Voice-Overs", "Audit Voice-Overs", "Find Duplicate Translations", "Find Multi-bound Voice-Overs") and in right-click context menus for selections (e.g. "Rename Selected Voice-Overs", "Remove Translations and Voice-Overs").

## Build from source

Requirements:

- .NET 8 SDK
- Windows (WPF)
- Articy:draft X installed — the `Articy.MDK` NuGet package's reference assemblies ship alongside the app

```bash
dotnet build -c Release
```

Output lands in `bin/Release/`. To produce a distributable `.mdk`, either:

- Use Articy's built-in **DevKit Tools** plugin to package the build folder (canonical path), or
- Zip the contents of `bin/Release/` and rename the archive to `.mdk` (the `.mdk` format is a plain zip of the built plugin files)

## License

MIT — see [LICENSE](./LICENSE).
