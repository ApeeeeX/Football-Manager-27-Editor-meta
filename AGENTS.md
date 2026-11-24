# AGENTS.md — Football Manager 27 Editor

Guidance for anyone (human or automated) changing this repository. Code, identifiers and commit messages are in English.

## 1. What this is

A live editor for **Football Manager 27**, for offline single-player saves. It attaches to the running game (`fm.exe` on Windows,
`Football Manager 27` or `Football Manager Touch` on macOS), reads and writes the memory of `game_plugin`, and never touches game files
on disk. Supported builds are listed by the JSON profiles in `offsets/`: Windows Steam `27.1.0+2505117` and `27.0.1+2503222`,
Windows Game Pass `27.1.0+2505117` and `27.0.1+2494687`, macOS Steam and Touch (`*-mac.json`).

FM Touch does not have staff allowances, team Tactical Understanding, referees, or the club relationship flags Can Name Stadium After Person and Clubs Don't Transfer Players. These are limits of that game, not missing work: do not try to add them or list them as open work. Club relationship reasons do exist on Touch.

Writing game memory is a real action: use a throwaway save, keep a backup of the save, and expect the game to redraw only after you press Continue.

## 2. Requirements

- Windows 10/11 or macOS 13+ (Apple silicon), .NET 10 SDK. On macOS a small privileged helper (`src/Fm27Editor.Helper`, installed on
  first connect) reads and writes the game; `scripts/package-mac.sh` builds the app bundle and the dmg (needs Xcode command-line tools).
- Football Manager 27 running with a save loaded, for anything that reads or writes the game. Build and unit tests need no game.

## 3. Layout

| Path | Contents |
|---|---|
| `src/Fm27Editor.Core` | Backend: memory access, game session, offset profiles, and one folder per entity (`Players`, `Staff`, `Clubs`, `Nations`, `Stadiums`, `Competitions`, `Awards`, `Officials`, `Transfer`), plus `History` (undo journal) and `Settings` |
| `src/Fm27Editor.App` | Avalonia 11 desktop app. UI is built in code (no XAML). `EntityScreen` is the shared base for entity screens, `ToolGrid` the shared table |
| `src/Fm27Editor.Tools` | Command-line tool: read, write and diagnose commands (the list is at the top of `Program.cs`) |
| `offsets/<build>.json` | Every build-dependent RVA and offset. Embedded into Core at build time; a file next to the exe overrides the embedded one for the same build |
| `tests/` | Unit tests on fake memory (`Fm27Editor.Core.Tests`) and view-model tests (`Fm27Editor.App.Tests`) |
| `re/tools/memscan` | Small memory-scanning helper used while measuring offsets |

## 4. Build, run, test

```bash
dotnet build Fm27Editor.slnx -c Release
```

```bash
dotnet test Fm27Editor.slnx -c Release
```

```bash
dotnet run --project src/Fm27Editor.App -c Release
```

CLI examples (read-only unless `--write` is given; without it write commands only print their plan):

```bash
dotnet run --project src/Fm27Editor.Tools -c Release -- players "Kobbie Mainoo"
```

`gamecheck` and `gamewrites` in the CLI exercise many readers and round-trip writers against a live game, on either build; they are the quickest smoke test after a change to offsets. (`steamcheck`/`steamwrites` remain aliases.)

## 5. How the core works

- **Attach:** `GameSession.Attach` finds `fm.exe` and `game_plugin.dll` and picks the profile whose build string matches. An unknown build throws `UnsupportedBuildException`; no game throws `GameNotRunningException`.
- **Reads** return immutable snapshots. Addresses change every time a save is loaded, so never store them between sessions.
- **Writes** always go through a guarded writer. Each write checks that the object still has the expected vtable, that memory still equals the snapshot read earlier, writes, then reads back. Any mismatch throws (`StaleObjectException`, `MemoryAccessException`) and writes nothing further. Do not call raw memory writes from UI code.
- **Adding and removing rows in game vectors** writes the new record and pointer; when the vector is full a new buffer is allocated outside the game's allocator and `begin/end/capacity` are rewritten. Removing shifts the tail up and moves `end` back.
- **Undo:** `IEditService` is the only write path from the UI. It serialises edits, records them, and supports undo.
- Encodings you will meet: contract dates use the low 9 bits as day of year with flags above (preserve the flags); attributes are stored ×5; tactical values are `dword & 0x3FFF` (×100) with flags above.

## 6. Rules for changes

- **Every feature must work on both Game Pass and Steam.** Anything touching game memory needs its offsets in both `offsets/*.json` profiles and must be measured on both builds. Shared code reads offsets from the profile only; no hard-coded RVAs or build-name checks (`NoSingleBuildCodeTests` and `ProfileParityTests` enforce this).
- **Dates** are shown and entered in the user's global date format (`DateSetting`, default DD-MM-YYYY). Use `DateSetting.Format`, `DateSetting.TryParse` and `DateSetting.Hint`; never hard-code a date pattern in UI text. File exports and machine data stay ISO.
- **Colours** in the UI are written as the dark-theme hex and fetched with `UiKit.Brush("#...")` so the light/dark switch repaints in place. Do not create `SolidColorBrush(Color.Parse(...))` for interface colours. Data colours (kits, club colours) are exempt.
- **UI style:** dense and plain. No decorative gradients, nested rounded cards, illustrative icons, emoji, taglines or helper text; if a screen needs explaining, fix the layout. Related entities are links that open their own tab. Do not open several windows for the same thing.
  Reuse what exists: every table is a `ToolGrid` (the Squads look), cards are `UiKit.Card`, forms `TwoColumnFormBuilder`,
  search/pickers the shared dialogs, and Core logic the existing readers/helpers. Never build a bespoke table or a second
  copy of a helper for one feature; if the shared piece lacks something, extend it.
- **Screens:** actions are words (Add, Remove, Save changes, Review changes), never glyph-only buttons. New rows and forms start empty, with nothing preselected. Named values are drop-downs, entities are type-then-pick boxes and links. Keep consistent padding, avoid dead space, and do not change window size or layout beyond the request.
- **Saving:** the app has one Save (top bar, Ctrl+S). Edits are drafts until Save. Every write message ends with "Continue in FM to see updates."
- **Bulk and long work:** bulk changes go through Review and are one undo group. Work over about 2 seconds runs in the background with progress and Cancel. Skipped records are counted and listed with a reason.
- **Tests:** do not skip, disable, weaken or delete a failing test to get green. Explain why it fails before touching it. Expected values must come from an independent source (the game's own UI, a reference editor, bytes captured from the game, or hand calculation), not from the code under test.
- **Unknown values** are reported as unknown; never substitute `-1` or an invented number.
- Game data may be tracked, including the calibration save. Do not commit generated build output or local notes. Reverse-engineering notes and tool output belong on your own machine (`re/` is ignored apart from `re/tools/memscan`).
- **macOS helper:** raise `FM27_HELPER_VERSION` in `src/Fm27Editor.Helper/fm27_helper.c` whenever the helper changes, and only then. The app reinstalls the helper, which asks for the administrator password, only when that number differs.
- **Shortcuts** are written with Ctrl and go through `Shortcuts`, which uses Command on macOS.
- When editing a file with mixed CRLF/LF endings, use an editor that preserves them and check the change landed.

## 7. Working on this repository

**Start of a session**

1. Read the newest `releases/<version>.md`. It says what is done, where it is, and what is open.
2. Check `git status` and `git log`. Other agents may be working in the same tree: never overwrite or revert their changes.
3. Do what was asked and nothing more. Commit and push only when the owner asks.

**Finding how the game works**

- Trust evidence in this order: the game's own UI and In-Game Editor, the game binary, live game memory, then a reference editor as a source of numbers. A reference editor's behaviour is not evidence of how the game works.
- Start from names the game ships: RTTI classes, editor field IDs, and strings. Follow them to the function or field, and read disassembly with the platform's command-line tools (`xcrun lldb --batch` on macOS).
- Read live memory read-only until the change is understood. Use `memdump`, `ptrdump`, `vtsurvey`, `slotsbyname`, `vectorroot`, `heapvectors`, `ptrpath`, and `heapfind`.
- Find entities by ID, resolve objects through their vtable and profile offsets, and never reuse an address after a save is loaded.
- On macOS the helper serves one client: close the editor before attaching a command-line tool.
- Never close the game or Steam. Prefer a throwaway save for writes.

**Before calling something done**

- Compare the result with an independent value from the game on each platform it claims to support. State plainly what was not checked.
- Run `dotnet test Fm27Editor.slnx -c Release`. Build and package only when needed: packaging replaces the running app and takes time and disk space.
- Keep internal notes and research in ignored folders (`docs/*.md`, `re/`). This is a public repository, so tracked files must not link to them.

## 8. Release log

Every change to `src/` or `offsets/` must update the newest `releases/<version>.md` in the same push. CI fails otherwise. Before pushing, run:

```bash
scripts/check-release-log.sh origin/main HEAD
```

- Keep one row per feature or fix: area, what changed, files, then one column each for Steam Windows, Game Pass, Steam macOS and Mac Touch. Each platform is **Checked**, **Reads**, **Not checked**, **Missing**, or **—** when that game does not have the feature.
- Update the same row when a feature changes. Move unfinished work to **Open work** and remove it when finished.
- Record only what was actually checked. Do not write RVAs, offsets or private notes here.
- After a version is tagged, create the next `releases/<version>.md`. `CHANGELOG.md` stays the user-facing summary.

## 9. Supporting a new game build

Every step ends in a PASS/WARN/MANUAL or PASS/FAIL line, never a judgement call.

1. Load a save in the new build (a throwaway calibration save).
2. Discover: `newbuild --out offsets/<build>.json` (add `--layout macos|touch-macos|windows` if the layout is not detected).
   It reads class names from the module's RTTI, finds every vtable and static root, probes the offsets that have drifted
   before, and writes a small profile that `extends` `offsets/layouts/<layout>.json`. About 30 seconds.
   - Every `MANUAL` line must be fixed with the hint printed under it. Treat each `WARN` as work: read its detail.
   - The written profile is always `"readOnly": true`.
3. Verify read-only: `verify-build --profile offsets/<build>.json --expect-players N --expect-staff N --expect-humans N --negative-test`.
   Take the counts from the game or a reference editor, never from this editor.
4. Verify writes and the crash gate: `verify-build ... --writes`, then `verify-build ... --crash-gate <state.json>`.
   Press Continue and Load Game in FM, then `verify-build ... --crash-gate-verify <state.json> --release-gate --receipt <out.json>`.
   Only a `VERDICT: PASS` receipt justifies removing `readOnly`.
5. Compare a few screens with the game UI, run `dotnet test`, and keep the receipt with the pull request.

Rehearse on a supported build with `newbuild --ignore-profiles`: the generated profile must match the committed one, or the
difference must be explained (the Steam macOS rehearsal of 19/11/2026 found a wrong Corporate Facilities offset this way).

The gate checks RTTI, completeness against independent counts, per-field semantics, write round-trips, the live crash gate
and a negative self-test. Exit codes: `0` PASS, `1` FAIL, `2` usage or attach error, `3` pending crash gate. Never report a build as
supported from `gamecheck` alone: a group there counts as filled when any one field reads.

Lower-level survey commands remain for the MANUAL cases: `vtsurvey`, `slotsbyname`, `vectorroot`, `heapvectors`, `ptrpath`,
`heapfind`. Find entities by ID, not name.

Known limit: a reader that reports success can still have thrown on some records; read the `threw` counts in `gamecheck`, not only the `ok` lines.
