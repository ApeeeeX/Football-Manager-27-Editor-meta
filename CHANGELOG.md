# Changelog

## 1.5.6

- **Football Manager 27.1.1:** supported on Steam Windows, Game Pass and Steam macOS. FM Touch has no 27.1.1 update yet.
- **Mac with System Integrity Protection on:** the editor plugin now works. Steam could not start the game with the old setup ("OS error 260"); Set up now uses a small launcher instead. If you set up the plugin before, the editor asks you to set it up once more.
- **Plugin setup window:** larger, step by step, in English or Vietnamese, and it tells you when Football Manager 27 or Steam is still open.

## 1.5.5

- **Advanced Search is back on the toolbar,** next to Your Manager, on Windows and Mac. 1.5.3 and 1.5.4 removed it by mistake.
- **New game builds:** `newbuild` now carries in-match editing, club swap, Force Board Request and the other recent features over to a new game version.

## 1.5.4

- **Club swap:** says why two clubs cannot be swapped (season started, other country, same division, no senior team, no table, points adjustment) as soon as a club is chosen, and a failed swap leaves only Close. A table row whose club name is still empty no longer blocks the swap.
- **In-match condition on Mac:** changing a player's condition during a match now takes effect on Steam macOS and FM Touch.
- **Destroy:** also drops Tactical Understanding to its minimum, for the whole team in a match and for a single player; it stays after the match and can be undone.
- **Club projects:** hidden on every platform for now. It returns when a new ground can be finished from Planning.
- **Steadier loading:** the memory clean-up after loading no longer fails.

## 1.5.3

- **Recommended Current Ability:** players and staff now use the game's own calculation. RCA sits left of CA and updates while you type.
- **Build a Backroom:** choose a team and the Coaching, Recruitment or Medical staff. Coaches are raised in the training areas that team lacks, a rated coach takes set pieces, and CA/PA rise to the new RCA without ever being lowered.
- **Staff:** Freeze for club staff, including CA, and extra staff allowances for human-managed clubs.
- **Clubs:** club swap before the first match, bring stadium and facility projects forward, Force Board Request, Team Tactical Understanding with per-bar sliders, and a clearer relationships screen.
- **Players and managers:** Legend Players, in-match ability editing, and manager unlockables including squad registration and squad numbers anytime.
- **Faster and steadier:** a reworked load path with lower memory use, and fixes for random Save As and holiday crashes after adding to full game lists.
- **Interface:** updated tables, section rail and Vietnamese translations.
- **Update window:** Update now shows a window with the download progress and Cancel, installs the new version and opens it again, on Windows and Mac.
- **Mac updates itself:** Update now downloads the new version, checks it, replaces the app and opens it again, with no Privacy & Security step. The helper asks for the administrator password only when the helper itself changes, not on every update.
- **Mac shortcuts use ⌘:** ⌘W closes a tab, ⌘S saves, ⌘R reloads, ⌘K searches and ⌘M opens Mass Edit.

Not every feature is available on every game build. `releases/1.5.3.md` lists each one by platform.

## 1.5.2

- **Move to club for staff (Windows, Steam and Game Pass):** a member of staff under contract could not join a club whose
  department was full ("Destination Coaching allocator spare is not empty"). Like the IGE and FMRTE, there is no limit
  now: a full department grows into a new list from the game's heap. Undo restores the old list.
- **Staff job code (macOS Touch):** read as one byte, so roles no longer show as "Code 41xx" and the move keeps the flag
  stored next to the job.

## 1.5.1

- **Windows `27.1.0+2505117` (Steam and Game Pass):** support players who are also staff (`db::ACTUAL_PLAYER_AND_NON_PLAYER` subtype insertion) across readers, writers, and transfer/release paths, and complete `newbuild` discovery on Windows.
- **macOS Football Manager Touch `27.1.0+2505719` and `27.0.1+2503222`:** verified read/write support with game-heap allocations, transfers, loans, future transfers, promises, `AWARD_MANAGER`, Corporate Facilities at `0x8AD`, and Continue + Load Game crash-gate verification.
- **Player Information UI (Windows and macOS):** moved the personality classification label from the `PERSONALITY` attribute card header to the top of `PERSONAL DETAILS` (right-aligned above `Born`), and left-aligned the lower checkbox and retirement labels.
- **New game builds & hardening:** `newbuild` discovers a profile from RTTI and the live save in about 30 seconds and `verify-build` enforces the release gate; hardened with Touch `AWARD_MANAGER`, subtype-insertion-aware promise discovery, `CorporateFacilities` duplicate-field filtering, and `futureTransfers.dateVector` / `proposals.changedFlag` vector-header checks.
- macOS Steam `27.1.0+2505117`: Corporate Facilities read 0 for every club (offset `0x8A9`); it is at `0x8AD`. Found by rehearsing `newbuild` with the profiles hidden.

## 1.5.0

Support for **Steam and Game Pass `27.1.0+2505117`** has been measured on the live game. Calibration passed 595 checks
with 0 failures and the existing `staff.CityOfBirth` skip; `gamecheck` passed all 72 features with no empty or failing
readers.

- **Move now with a sell-on** writes it in the completed offer's terms object at `+0xC4`, as FMRTE does. The previous
  club-clause record survived Save As but crashed FM when the save was loaded again.
- **Loan contracts** clear the template's owned records (`+0x30` unhappiness, `+0x68` clause list) and write `+0x54=0`;
  loading a loan created by the editor no longer exits FM.
- **Load Game** refuses every write while FM is loading a save or has no save loaded (the GAME_UI save count and the
  player list), on both Steam and Game Pass.
- **Role scores** ship inside the exe; there is no role catalog file to locate or import.


### macOS
- **Steam `27.1.0+2505117`** on Apple silicon, measured on the live game: `gamecheck` 72/72, round-trip writes 24/24,
  and every transfer path (Move now with sell-on, free agent, future transfer, loan, release) followed by Continue and
  Load Game without a crash.
- **Load Game no longer exits FM after an edit that adds a row** (languages, promises, work permits, future transfers,
  club vision, agreements). The new records are allocated by the game's own `calloc` instead of raw VM memory, which FM
  could not free.
- **Squad rules are editable**: maximum squad size and homegrown minimums write the runtime rule and every saved copy.
- **Move staff** to a club whose department list is full.
- Football Manager Touch remains experimental.

## 1.4.0

Checked on Steam `27.0.1+2503222` and Game Pass `27.0.1+2494687`.

### macOS
- **The editor runs on macOS** (Apple silicon) with Football Manager 27 Steam `27.0.1+2503222` and Football Manager Touch
  `27.0.0+2492889`, installed from a dmg. A small helper, installed once with your password, reads and writes the game;
  Development Speed and Wonderkid Academy are patched as ARM64 code.

### Transfers
- **Transfer a player on loan:** Move now removes the player from both old squads. A future transfer ends the loan immediately and returns the player to the parent club while the deal waits, as observed in IGE. Both paths accept an agreed purchase price without charging the borrowing club for an unconfirmed purchase. Buy-back, purchase-after-matches, other contract clauses and unmeasured squad layouts remain unavailable. The purchase-price change has been tested live on Steam; Game Pass has been tested with fake memory only.

- **Free agents** can be moved:
  - with Move now, the player joins at once on a copy of the destination squad's first contract, as FMRTE does;
  - as a future transfer, you enter the weekly wage.
- **Staff free agents** can be moved to a club: choose the role, wage and end date. Written the way FM's in-game editor does it.
- Steam `27.0.1+2503222` and Game Pass `27.0.1+2494687`: corrected the offer-list root used by future transfers and completed-transfer records.
- **Financial Fair Play:** an instant transfer now counts for FFP on both sides.
  - The selling club gets the fee as Transfer Income for the season.
  - FM keeps a finished-transfer record for the buying club. The buyer's FFP updates once game time moves on.
- Move to club: the team is chosen in a list next to the club, and the first team is picked by default.
- Move to club: selling to or from a club whose budget or balance is already at the maximum no longer fails with an overflow; the value stays at the maximum.
- Game Pass: transfer clauses are read from each club's own list, as on Steam, instead of a 5-second memory scan; Load Game drops from about 9 to 4 seconds. Club pages show the clauses on the club's players (they showed the ones owed to the club), and Move to club can add a sell-on on Game Pass too.

### App
- Kits: opening a club whose kit has Item Style 0 no longer marks the page unsaved.
- Vietnamese UI: aligned selected football terms with the supplied FM27 language file, filled missing form labels,
  and separated player Balance, club finances, training facilities and kit colours by context. Long labels now wrap
  in forms, section lists, table headers and preferred-move checkboxes. Global Save and pending-edit stars recognise
  translated child Save buttons, including after switching language.

- Load Game prepares entity lists, search and picker catalogues, retirement and future-transfer lists, and most shared lookups before reporting ready. Reloading from another module keeps its list populated.
- Transfer Deals and agreement regions are read from the game's own transfer manager and database instead of a multi-gigabyte heap scan; the scan remains the fallback when that lookup can't be validated.
- Bulk reads reuse nearby memory pages and shared names; work permits are discovered and fully validated inside the known transfer manager before falling back to a heap scan.
- Search catalogues stay available for the loaded save. Reload and window close release old search results and stop their timers; the loading animation stops when hidden.
- Wonderkid Academy has a Turn on / Turn off button that keeps its configuration and supports Undo.
- Unticking Move now defaults Transfer Date to the day after the current in-game date.

- Load Game removes stale Development Speed rows whose addresses now belong to different players, and reports the count.
- Load Game switches Wonderkid Academy off and says so; its settings are kept for when you switch it on again.

- **Update now**: when a new release is out, the editor downloads it, checks it against the release checksum, replaces itself
  and restarts. Builds before this one still need one manual download.
- **Vietnamese UI (`Tiếng Việt`)**: added global `Language` option in **Settings** (`English` default / `Tiếng Việt`) covering screens, menus, dialogs, columns, filters, Mass Edit, presets, status messages, and FM27 terms.
- **View Players Retirements** and **View Future Transfers** (toolbar, next to Mass Edit) open their own tab, as in FMRTE:
  - Retire Date lists everyone the game plans to retire, players and staff: name, age, nation, club, date.
  - Future Transfers lists every pending future transfer: player, from, to, transfer or loan, fee, date.
- **Multi-select in every list:** Shift+click selects a range, press-and-drag selects the rows the pointer passes, Ctrl adds or
  removes rows. Right-click gives bulk actions:
  - on player lists (Players, Squads, Retire Date, Future Transfers): Prevent retirement, Freeze/Unfreeze, State, Presets,
    Contract, Transfer/Loan Status, Development Speed, Mass Edit;
  - on Staff: Freeze/Unfreeze;
  - on every other list: Open and Copy name.

  Each bulk action is one undo entry.
- Fixed a crash at start in the Vietnamese UI build (the search panel's Group button).

### Players
- **Prevent retirement** now also clears the player's own retirement flags. Before, FM still showed the player as retiring.
  - The flags are +0x2A0 bit 0x01 and "Retiring after spell at current club"; FMRTE clears the first one too.
  - It still removes the planned retirement and sets the keep-playing trait, as before.
  - It no longer stops with an error when FMRTE has already cancelled someone's retirement in the same save.

### Calibration
- New anchors:
  - the offer list read straight from its root;
  - Man Utd's Players Sold and Transfer Expenditure (month and season);
  - the club staff department lists (all teams, first team, U21, U18);
  - staff team and contract end;
  - Sarr's loan;
  - eight planned retirements from FMRTE's list.
- `calibrate loanverify` is gone: its checks are part of `calibrate verify`.
- Game Pass 593 passed / 0 failed / 3 skipped. Steam 587 / 0 / 1 before the eight retirement anchors were added (not yet rerun).

## 1.3.0

### Game builds
- Supports the Steam hotfix `27.0.1+2503222`. Xbox / Game Pass stays on `27.0.1+2494687`; both run every reader and write, and pass
  the same calibration checks.

### Staff
- **Training Rating** tab: the eleven ratings as sliders (0–5 stars); saving sets the attributes behind a rating.
- Information shows and edits **Caps**, **Goals**, **License** and the fifteen **Roles** ratings.

### Players
- **Make Captain** / **Make Vice Captain** work in one click.
- Each child tab (Relationships, Contract, Languages…) shows a * while it holds unsaved edits.

### Clubs
- **Status** (Professional, Semi Professional, Amateur) and the full Ownership Type, Chairperson Title and Rich Benefactor lists.
- International pitch size reads as empty when the box is unticked.

### Mass Edit
- Rebuilt like FMRTE: coach General page (Age, Nationality, Caps, Goals, License, Training Rating), player Age and Nationality,
  club Status, Reputation, ticket price, Capacity and pitch sizes.
- No rows cut off at the bottom; opens without a pause.
- Fixed: a field set in one scope was also planned for another scope with the same field number (a club's Balance was
  offered as a nation's Women FIFA Ranking Points). Each scope now plans only its own fields. If you used Apply all with
  fields set in several scopes on 1.1.0–1.2.0, check the clubs and nations involved.
- Money boxes accept 1.000.000.000, 1,000,000,000 or plain digits; the review closes after Apply; Choose who to edit
  opens at once.

### Other
- Wonderkid Academy is marked Experimental and has a How to use guide. Force Accept Transfer is withdrawn until it is redone
  safely (shown as Coming Soon).
- Money on the club screen and in club Mass Edit is shown and typed in GBP, EUR, USD or VND: the game's own currency
  (detected) or one you pick in Settings. The game keeps pounds; limits follow the chosen currency.
- Open player tabs follow you to a newly loaded save (matched by player ID) instead of showing an empty page.
- Clearer tooltips; Freezer Manager layout fixed.

## 1.2.0

### Game builds
- Supports Football Manager 27 `27.0.1+2494687` on Xbox / Game Pass and Steam. Every reader and every kind of write was run against
  the live game on both.

### Players
- **Development speed** (Tools card of a player): makes the player improve faster from training while the game runs.
- **Transfer Proposals** shows loan, trial and B club move offers next to transfer offers, and their value can be edited.
- **Relationships** shows the name of every person a player likes or dislikes.

### Clubs and squads
- Squad presets: **Grant work permit** gives the ticked players a permit for the club's own country; **Golden Generation** sits on the
  first team and applies to every other team of the club. Every preset has a tooltip.
- Club Vision and Squads open without freezing the window.
- Stadium names and details are read correctly.
- Players currently out on loan are listed again.

### Nations
- National-team staff show their job and team for every member, not only those on a full contract.

### Other
- The editor notices a newly loaded save more reliably, and the status bar date follows the game.
- Work Permit opens at once (the permit table is found in the background after Load Game).
- Larger app icon.

## 1.1.0

### Mass edit
- **Mass edit** (toolbar button or Ctrl+M) changes many players, coaches, clubs or nations at once, in FMRTE's layout, with a Nation scope
  (youth rating and the other nation fields) that FMRTE does not have. Every change goes through the edit history, so it can be undone.

### Search
- **Advanced search** is rebuilt and filters players, coaches and clubs, with a results window.

### Players and staff
- **Relationships** lists every one of FMRTE's 21 relation types (Dislikes Team, Competition Unregistration, Invited to represent nation,
  Developed at Country, Other Nation Caps and Youth Caps with their Caps and Goals, and the rest), reads rows the game names "Unknown (N)",
  and adds and removes any of them.

## 1.0.1

### Staff
- **Move to club** can set the role and the weekly wage the staff member gets at the new club. A role from another department
  (coaching, medical, scouting) puts the member in that department of the club. The move takes effect on the game's current day.

### Players
- **Prevent retirement** (Tools card of a player) now applies at once: it removes the planned retirement, sets the keep-playing
  traits, reloads the screen and says what it did in the status bar. A player with nothing to change gets a message instead of an empty window.
- **Career Plans**: Add puts an empty row in the list, Remove takes a row out, and nothing is written until the one Save.
- **Clone Center** searches every player, not only the ones shown in the Players table.
- The Discard buttons are gone; Save is the global Save, Reload (Ctrl+R) drops unsaved edits.
- A message shown after a save stays in the status bar instead of being cleared when the screen redraws.

## 1.0.0

### Reporting problems
- A bug icon at the bottom right of the window creates a report zip on the Desktop: the player's description, the state of the
  editor and game, and the run logs. Every write the editor makes to the game is logged to disk as it happens, so a report can
  also be made after the editor or the game was closed or crashed. Nothing is uploaded.

### Players
- **Future transfer** from Move to club (untick Move now): a date and every transfer term in FMRTE's order (instalments,
  league / international / per-appearance and goal payments, sell-on with Of the profit, buy-back fee, wage contribution,
  friendly), all 0 by default. Game Pass only for now.
- **Remove future transfer**: a button on the Future Transfer tab takes the agreed transfer back out of the game (on both builds).
- **Swap Player**, **Promises** (all 68 types, add / edit / remove), **Tactical Understanding** preset.
- **Favourites**: star a player, then open the favourites table from the toolbar.
- **Reload** (Ctrl+R) rereads the current screen from the game and keeps unsaved edits.

### Stadiums and cities
- Stadium nearby stadium and coordinates; owner and location open the club and the city.
- City nation, region, language and weather.

### Other
- **Settings** (gear button in the toolbar): the date format for the whole editor, DD-MM-YYYY (default), MM-DD-YYYY or YYYY-MM-DD, remembered between runs. Exports keep ISO dates.
- **Light theme** (white and blue) next to the dark one: the sun / moon button in the toolbar switches in place and the choice is remembered.
- Notices: the game runs a supported build but no save is loaded; a newer release exists on GitHub (one check at start); the game loaded another save, which locks every tab and refuses writes until Load Game reads it.
- Much lower memory use for pictures (smaller decoded images, a capped cache, a compact index of the graphics pack).
- Smaller download: native libraries for other platforms are left out of the publish folder.
- New logo and icon.
- Removed Mass Edit and the club-level Tactical Understanding bars.

## 0.3.0 (5/11/2026)

### Game versions
- FM27 `27.0.0+2493426` on **Steam** (the November update).
- FM27 on **Xbox / Game Pass** (package `0.9.2409.0`). Steam and Game Pass share the object layout; the Game Pass
  profile maps every class by its RTTI name. `re/tools/gamepass-rva-map.ps1` rebuilds a profile for a new build in seconds.
- Steam and Game Pass report the same build string, so builds are told apart by image size and PE timestamp.

### Players
- Information: nationality, city of birth, nationality info, body, hair, skin, ethnicity, days in country, flags.
- Contract and future contract, bonuses and clauses, transfer clauses (sell-on, appearance fee, competition bonus).
- Career Plans, Relationships (with Other Nationality), Languages, Preferred Moves, Positions.
- Injuries, bans, unavailability, unhappiness, national call-ups (swap), transfer proposals, future transfers.
- Comparison, Clone Center.
- **Release Player**, with a preview and Undo. Clubs run by a human manager are refused.
- **Captain / vice captain**.
- **Move to club** now updates both team links of the contract.

### Staff
- New Staff screen: information, contract, Career Plans, Relationships, Languages, Knowledge (edit, add, remove),
  Formations, Tendencies.
- **Move staff to another club** (coaching, medical, scouting), with Undo.

### Clubs
- Relationships, Staff by club, Squads with presets, Transfer Center, Debt, Affiliations, Club Vision importance.

### Nations, stadiums, cities, competitions, awards
- New screens for each. Nation details, teams' reputation, relationships, transfer preferences; stadium details and
  flags; city attraction, location and altitude; competition and award reputation.

### Tools
- **Mass Edit** for players, staff and clubs: attributes, CA/PA, reputation, height, positions, personality, condition,
  wages and more. Every batch is previewed and undone as one.
- **Presets** for a whole team: Heal, Fitness, Morale, Lift bans, Remove Unhappiness, Max attributes,
  Prevent retirement.
- **Freezer** for player attributes and club balance. It is kept per save and re-applied when you connect again.
- **Export** the player list, with its filters, to CSV or JSON.
- Filters: attribute ranges for players; name, club and job for staff.

### Other
- Licensed under GPL-3.0-or-later.
- Support links: Ko-fi, and VietQR in the Vietnamese README.
