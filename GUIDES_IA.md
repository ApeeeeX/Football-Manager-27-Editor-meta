# GUIDES_IA.md — Guide de développement IA pour Fm27Toolkit

> Guide d’orientation fondé sur le dépôt `ApeeeeX/Fm27Toolkit`, branche par défaut observée lors de l’analyse (clone shallow). Le code et les profils du dépôt font foi. Ce fichier aide à naviguer et ne remplace ni les tests, ni l’inspection du code réel, ni les mesures sur le jeu.

## 1. But et limites du produit

Fm27Toolkit est un éditeur **en mémoire vive** pour Football Manager 27 et FM Touch. Il s’attache au processus du jeu avec une sauvegarde chargée, décode sa base de données mémoire et applique des modifications au processus; il n’édite pas les fichiers de jeu ni directement les fichiers de sauvegarde sur disque. Les changements se répercutent dans le jeu après **Continue**. Le cas d’usage est exclusivement le jeu solo hors ligne. Les sauvegardes doivent être copiées avant toute édition; les essais risqués se font sur une sauvegarde jetable. Voir `README.md`, `src/Fm27Editor.Core/Game/GameSession.cs` et `src/Fm27Editor.Core/Memory/`.

Les éditions/builds listés dans `README.md` sont Windows x64 Steam/Game Pass et macOS Apple Silicon Steam/Touch. La prise en charge du football féminin n’est pas validée; éviter d’y écrire. Un profil correspondant exactement au binaire est nécessaire avant attachement. Les hooks sont des modifications de code **en mémoire seulement**, avec des voies x64 Windows et ARM64 macOS pour les fonctionnalités concernées.

## 2. Architecture en bref

Flux conceptuel :

```text
Fm27Editor.App (Avalonia, écrans et orchestration UI)
        │  IEditorBackend / IEditService; pas d’écriture mémoire brute depuis les écrans
        ▼
Fm27Editor.Core (session, profils, lecture/écriture métier, sécurité mémoire, journal)
        │
        ├── WindowsProcessMemory (Windows) ou MacProcessMemory / MacHelperProcessMemory (macOS)
        └── processus FM27 / FM Touch (mémoire vive)

Fm27Editor.Tools ── mêmes Core readers/writers, CLI de diagnostic/calibration/validation
Fm27Editor.Helper ── helper privilégié macOS pour opérations Mach VM
```

`Fm27Editor.slnx` inclut App, Core, Tools, les deux projets de tests et `src/Fm27Toolkit.App`. L’ancienne `Fm27Toolkit.sln` liste App/Core/Tools, tests et l’outil `re/tools/memscan`; vérifiez la solution utilisée par les instructions actuelles, qui est `Fm27Editor.slnx`.

## 3. Arborescence et fichiers à connaître

### Racine et documentation

- `README.md` : plateforme, fonctionnalités, versions supportées, commandes, sécurité et runbook patch résumé. `README.vi.md` est sa version vietnamienne.
- `AGENTS.md` : règles applicables aux changements (en particulier sécurité mémoire, offsets par build, UI/tests et reverse engineering). À lire avant toute modification.
- `NOTES_IA.md` : notes historiques pour IA; plusieurs références d’architecture sont obsolètes (voir §13). Ne pas les prendre comme description du code actuel.
- `CHANGELOG.md` : historique fonctionnel/versionné, utile pour repérer les changements récents et leurs chemins concernés.
- `.github/workflows/build.yml` : CI Windows/macOS et publication de release.
- `Fm27Editor.slnx`, `Fm27Toolkit.sln` : solutions actuelle et ancienne.
- `docs/` : dans l’arbre observé, seulement images; les fichiers `docs/NEW-BUILD-PROFILE.md` et `docs/DESIGN-RULES.md` référencés par `README.md`/`AGENTS.md` **n’étaient pas présents dans ce clone**. Ne pas supposer leur contenu; les règles disponibles dans `AGENTS.md` restent applicables.

### `src/Fm27Editor.Core/` — modèle métier et accès mémoire

Le Core est le centre de gravité: il doit contenir lecture, interprétation et écriture métier plutôt que du code de mémoire dans l’UI.

- `Game/` : processus, session attachée, cycle/état de chargement. `GameSession.cs` choisit le profil, expose readers/writers et centralise les opérations d’édition structurées.
- `Memory/` : abstractions et implémentations d’accès (`IProcessMemory.cs`, `WindowsProcessMemory.cs`, `MacProcessMemory.cs`, `MacHelperProcessMemory.cs`), lectures groupées (`BulkReadScope.cs`), lots d’écritures (`MemoryWriteBatch.cs`), vérification (`VerifiedWriteMemory.cs`), allocateurs (`RemoteCrtAllocator.cs`) et patch de code.
- `Offsets/` : `OffsetProfile.cs` représente les données propres au build; `ProfileJsonResolver.cs` résout héritage et références symboliques; `NewBuildDiscoverer.cs` détecte les décalages; `RootFinder.cs`, `ModuleRtti.cs`, `ProfileHealth.cs`, `ProfileUpdater.cs` et `Gender.cs` contribuent à découverte/contrôle.
- `Players/`, `Staff/`, `Clubs/`, `Nations/`, `Stadiums/`, `Competitions/`, `Awards/`, `Officials/`, `Transfer/` : dossiers métier par domaine, avec records/snapshots, repositories/readers, writers et services. Exemples: `Players/PlayerReader.cs`, `PlayerWriter.cs`, `PlayerRepository.cs`, `Clubs/ClubFields.cs`, `Staff/StaffRepository.cs`, `Transfer/TransferService.cs`.
- `History/` : `ChangeJournal.cs` enregistre les octets changés et gère Undo; `ChangeContext.cs` fournit le contexte/nommage, `*ChangeLabels.cs` les libellés lisibles.
- `Verification/` : `BuildVerifier.cs`, `HeapGate.cs`, `WriteAudit.cs`, fixtures/vérifications de build, vtables, sémantique et écritures.
- `MassEdit/` : préparation, revue et application des éditions de masse (`MassPlanner`, `MassApplier`, `MassEditPlan`, etc.).
- `CodeGen/` : assembleur et primitives ARM64 de patch (`Arm64Assembler.cs`, `Arm64CodePatch.cs`, `Arm64SiteFinder.cs`).
- `Presets/` : fonctionnalités faisant des hooks (développement, génération de jeunes, offres de transfert).
- `Diagnostics/`, `Discovery/`, `Calibration/`, `ForceAccept/`, `Settings/` : diagnostic, découverte, outils/calibration interne, patchs de presets ciblés et réglages.
- `Fm27Editor.Core.csproj` cible `net10.0`, active `Nullable`, `AllowUnsafeBlocks` et `IsAotCompatible`; profils JSON, layouts et fixtures sont embarqués comme ressources.

### `src/Fm27Editor.App/` — application de bureau

Avalonia 11.3.10, principalement **UI construite en C#**, pas d’architecture XAML par écran. `MainWindow.cs` est une grande classe partielle dont les fonctions sont ventilées en fichiers `*View.cs`; `AppShell.cs`, `SectionHost.cs`, `SectionNav.cs`, `ScreenHost.cs`, `EntityScreen.cs` structurent shell/navigation/sous-écrans. `Backend.cs` expose `IEditorBackend`, établit la session et l’`EditService`, initialise l’historique et bloque l’écriture si la sauvegarde active change.

Réutiliser les briques présentes: `ToolGrid.cs` pour grilles, `UiKit.cs` pour composants/styles, `PickerDialog.cs`/`PickerSearch.cs` pour sélection, `PlayerDraft.cs`/`ClubDraft.cs` et drafts spécifiques pour staging, `Loc.cs` avec tables `Localization/Loc.Table.*.cs` pour textes. `PlayerInformationView.cs`, `PlayerInjuryView.cs`, `PlayerContractView.cs`, `ClubView.cs`, `StaffView.cs`, etc. sont des exemples réels; `PlayerActionsView.cs` et `MassEdit*.cs` couvrent actions/batch. `Ratings/rolesAndDuties.json` est la ressource des pondérations de rôles. `Assets/` regroupe icônes, drapeaux et ressources UI. Le csproj épingle Avalonia 11.3.10 et CommunityToolkit.Mvvm 8.4.0, mais l’application emploie largement une composition code-only.

### Outils, helper, profils et tests

- `src/Fm27Editor.Tools/` : CLI .NET 10. `Program.cs` est le routeur et l’index pratique des commandes; implémentations dédiées notamment `NewBuildCli.cs`, `VerifyBuildCli.cs`, `Calibration.cs`, `SteamCheck.cs`, `SteamWrites.cs`, `AllocCheck.cs`.
- `src/Fm27Editor.Helper/fm27_helper.c` : helper natif macOS à la demande. Il utilise Mach VM via socket Unix local pour lire/écrire, gérer protections, pause, allocations et opérations connexes; installation gérée depuis `src/Fm27Editor.App/MacElevation.cs`. Le helper s’exécute avec privilèges nécessaires et ne constitue pas un service réseau.
- `offsets/*.json` : profils propres à une variante/build (Steam, Game Pass, Mac, Touch). `offsets/layouts/windows.json`, `macos.json`, `touch-macos.json` : layouts de base communs. `offsets/fixtures/fm27-known-entities.json` : témoins/entités connus. Profils et layouts sont embarqués dans Core et copiés à côté des sorties; le dossier `offsets` voisin peut remplacer le profil embarqué correspondant.
- `calibration/manifest.json` : manifeste des valeurs/ancres de calibration et provenance. `calibration/DO-NOT-DELETE.txt` avertit de préserver le matériel de calibration. La sauvegarde de jeu citée par le manifeste n’est pas dans l’arbre listé; ne pas créer ni substituer une sauvegarde supposée.
- `tests/Fm27Editor.Core.Tests/` : tests unitaires Core, généralement doubles mémoire (`FakeMemory.cs`, `TestWorld.cs`) et tests spécialisés par feature. `tests/Fm27Editor.App.Tests/` : tests UI/logique App, Avalonia headless. `tests/golden/` contient notamment une fixture players JSON; `tests/mutation-check.ps1` vérifie les tests de mutation. `tests/Fm27Editor.Helper.Tests/read_many_test.c` est présent, mais ce répertoire n’est pas référencé comme projet dans la solution ou dans le workflow observé.
- `re/tools/` : scripts/outils d’investigation (PowerShell, Python, shell) et `re/tools/memscan/` scanner mémoire. Les notes et captures d’ingénierie inversée générées localement ne doivent pas être ajoutées au dépôt (règle `AGENTS.md`).
- `scripts/package-mac.sh` : publication NativeAOT, assemblage app `.app`, helper, profils, signature ad hoc et `.dmg`.
- `src/Fm27Toolkit.App/` : shell expérimental de branding/meta qui dérive de l’application Editor. Il est dans `Fm27Editor.slnx`, mais ce n’est pas l’exécutable publié par le workflow; voir §13.

## 4. Stack, versions et plateformes

- C# / .NET **10** (`net10.0`); SDK exigé par `README.md` et CI.
- UI principale: Avalonia **11.3.10**; CommunityToolkit.Mvvm **8.4.0**. Tests: xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1; UI utilise Avalonia.Headless 11.3.10.
- Windows 10/11 x64: API `ReadProcessMemory`/`WriteProcessMemory`; publication Windows autonome/single-file `win-x64`. Pas de runtime .NET requis chez l’utilisateur.
- macOS 13+ Apple Silicon: processus FM natif ARM64, application NativeAOT (`osx-arm64`) et helper C compilé avec Xcode Command Line Tools (`clang`). Le packaging prévoit aussi `RID=osx-x64` Intel, mais la matrice README/CI actuellement visée est Apple Silicon. Distinguer ce chemin de l’affirmation de prise en charge générale.
- Sur macOS, `MacMemory.Open` peut utiliser l’accès Mach direct; le fallback helper (`MacHelperProcessMemory`) et son installation permettent les opérations privilégiées. Pour ajouter une voie dans ce code, considérer le protocole client/serveur comme un contrat binaire strict et examiner les tests `MacHelper*Tests.cs`.
- Profils distinguent aussi architecture, édition/variant, taille/fingerprint de l’image et chaînes de build: lire `OffsetProfile.cs` et `GameSession.ProfileMatches`, ne pas identifier le build sur une supposition de version seule.

## 5. Concepts système à respecter

### Profils d’offsets et versions

Les RVA (adresses relatives au module) et offsets d’objet changent avec patch, édition et plateforme. Le processus n’est accepté que si chaîne de build et, si présent, image size/timestamp correspondent (`GameSession.ProfileMatches`). `OffsetProfile` contient ces mesures; `ProfileJsonResolver` fusionne `extends` avec un layout de base et résout les références `@...`. Un profil découvert par `newbuild` est toujours `readOnly: true` (voir `NewBuildCli.cs`). Ne jamais coder de RVA en dur ou de branchement sur le nom de build dans le Core. Garder profils Steam/Game Pass séparés si leurs RVAs diffèrent même avec la même chaîne de build. Un JSON propre à un build devrait hériter de `offsets/layouts/<windows|macos|touch-macos>.json` via `extends`; confirmer le mécanisme et le schéma dans `ProfileJsonResolver.cs`, `NewBuildDiscoverer.cs` et les profils voisins.

### Vtable, snapshots et lectures

Un pointeur de vtable sert à identifier/valider le type réel d’objet. Les readers produisent des records/snapshots contenant une adresse mémoire valable uniquement pour la session et le save actuellement chargés; les adresses ne survivent pas à un rechargement du jeu. Avant d’écrire, revalider vtable, identité et octets attendus. Les objets Player peuvent avoir des sous-types/insertion de champs; passer par les offsets profilés (`OffsetProfile.PlayerField`/`PlayerOffset`) plutôt que supposer un layout uniforme. Les opérations lisant de nombreuses adresses peuvent réutiliser `BulkReadScope`/scatter reads.

### Écritures gardées, lots, journal et Undo

Tous les writes métier sont gardés: lecture avant, vérification de snapshot/type/ID, écriture puis read-back; en cas d’écart, lever une erreur et arrêter plutôt que continuer avec un état ambigu. `MemoryWriteBatch` prépare les écritures, détecte les changements depuis le staging et applique via transaction avec garde/rollback; ce n’est pas un verrou sur les threads du jeu. UI utilise `IEditService` (`Backend.cs`) pour sérialiser les edits, associer une entité/contexte et enregistrer l’opération dans `ChangeJournal`. Les groupes peuvent fusionner de petites éditions rapprochées; Undo n’écrit les anciens octets que si la mémoire contient encore les octets post-edit attendus. Ne pas contourner cela par un `TryWrite` brut dans l’UI.

Pour les profils `readOnly`, `VerifiedWriteMemory` n’autorise qu’une liste de types/champs vérifiés, sur des cibles identifiées et revalidées (voir `OffsetProfile.VerifiedWrites`, `GameSession.CanEdit`, `Memory/VerifiedWriteMemory.cs`). Ne pas modifier `readOnly` pour faire disparaître un blocage.

### Listes du jeu et allocateur

Quand une feature ajoute/retire/agrandit un vecteur ou une liste possédée par FM (p. ex. langues, promesses, permis de travail, transferts futurs, affiliations), respecter ownership, begin/end/capacity, ordre d’initialisation et destruction du conteneur. Si le jeu peut ultérieurement libérer le bloc, l’allouer dans le heap/CRT du jeu avec `GameSession.AllocateGameBlock` et les implémentations correspondantes; `VirtualAllocEx` / `mach_vm_allocate` brut ne sont pas substituts sûrs pour des objets que FM va `free`. Le `HeapGate` et les round-trips/crash gate vérifient cela. Étudier la feature proche et ses tests avant de toucher au vecteur.

### Hooks x64 et ARM64

Les hooks modifient code/protections du processus vivant. Côté x64, lire les primitives d’allocation near, pause, patch et flush cache dans `WindowsProcessMemory.CodePatch.cs`, ainsi que les builders de routines des hooks sous `Presets/`. Sur ARM64, un branchement `B` doit être aligné sur 4 octets et tenir dans la portée ±128 MiB; `Arm64CodePatch.BuildPatch` remplit le site avec des NOP sans couper d’instruction, et des sauts absolus passent par x16. Les patterns/sites sont propres à l’architecture/build et doivent être mesurés/vérifiés par profil. Les patchs doivent valider les octets d’origine, arrêter les writes en cas de mismatch, protéger/pauser convenablement le processus et flusher le cache d’instructions; s’assurer que désactivation/Undo restaure un état valide. Voir `CodeGen/Arm64*`, `Presets/AcceleratedDevelopment.cs`, `GoldenGenerationHook.cs` et tests associés.

## 6. Ajouter une fonctionnalité d’édition: procédure

1. **Définir le champ et l’autorité de vérité.** Identifier l’objet/structure et établir valeur, représentation, largeur, plage, flags et comportement au Save/Continue depuis le jeu (UI FM ou autre preuve indépendante). Ne pas déduire une valeur inconnue. Pour un nouveau champ, déterminer si son offset existe déjà dans chaque profil/layout pertinent.
2. **Repérer une feature jumelle.** Chercher dans le domaine, par exemple `Players/PlayerWriter.cs`, `Players/Injuries.cs`, `Players/ContractWriter.cs`, `Clubs/ClubFields.cs`, `Staff/StaffContracts.cs`; inspecter leurs tests correspondants. Ne pas créer une seconde abstraction sans besoin.
3. **Implémenter en Core.** Ajouter/étendre un record ou reader si lecture, puis writer/service pour l’action. Exposer offsets variables au `OffsetProfile`; vérifier vtable, ID, snapshot et read-back; utiliser `MemoryWriteBatch` pour mutations composées et allouer via `GameSession.AllocateGameBlock` si le jeu doit posséder un nouveau bloc. Garder le métier indépendant des contrôles Avalonia.
4. **Écrire tests Core sur mémoire simulée.** Créer cas lecture, encoding/decoding, bornes, stale snapshot, mauvaise vtable, échec mémoire et Undo; tests ciblés dans `tests/Fm27Editor.Core.Tests/<Feature>Tests.cs`, doubles existants `FakeMemory.cs`/`TestWorld.cs`. Les expected doivent venir d’une source indépendante (pas du code testé). Ne jamais supprimer/affaiblir un test en échec.
5. **Exposer à l’UI.** Faire un petit écran/section ou champ dans le `MainWindow` partiel adapté (ex.: `PlayerInjuryView.cs`, `PlayerInformationView.cs`). Relire la snapshot au moment d’enregistrer, valider les entrées avant toute écriture, exécuter dans `backend.Edits.Execute(...)`/`ExecuteUnrecorded` approprié, puis rafraîchir l’entité/section et afficher erreurs via `UserMessage`/`Loc`. Pour champs éditables simples, préférer des descriptors/drafts et patterns existants (`PlayerFields`, `PlayerDraft`, `ClubDraft`); marquer l’état dirty et gérer abandon/reload selon l’écran.
6. **Libellés, formats, thèmes.** Ajouter les clés d’interface à `Loc.Table.*` (anglais clé canonique, vietnamien si prévu), appeler `Loc.S`/`Loc.F`; dates via `DateSetting.Format/TryParse/Hint`, machine/file export ISO. Couleurs d’interface via `UiKit`, pas de couleurs ad hoc. Réutiliser `ToolGrid`, dialogs et builders partagés.
7. **Profils et multi-plateforme.** Si un offset/structure varie, mesurer et ajouter aux profils concernés et comparer Game Pass/Steam et chaque OS/édition ciblé. Les champs non applicables doivent rester absents/explicitement indisponibles selon le modèle; ne pas inventer de fallback. Garder la parité des profils et pas de branche spécifique par build dans le métier.
8. **Revue réelle.** Tests unitaires puis, si l’action touche la mémoire, tests/gates live sur chaque variante prise en charge et sauvegarde jetable; confirmer comportement après Continue et Load Game pour allocations/structures. Mettre à jour `CHANGELOG.md` lorsque cela convient.

Exemple de référence vérifiable: blessures UI `src/Fm27Editor.App/PlayerInjuryView.cs` récupère `ReadInjuries`, valide les entrées, appelle `backend.Edits.Execute` et les writers `session.AddInjury`/`InjuryWriter`; la logique d’accès est dans `src/Fm27Editor.Core/Players/Injuries.cs` et `InjuryTypes.cs`. Les tests Core pertinents sont `InjuryReaderTests.cs` et `InjuryTypeAccessTests.cs`. Cet exemple montre le raccordement, pas une invitation à copier tous ses détails.

## 7. Prendre en charge un nouveau patch/build du jeu

Ne jamais déclarer le build supporté sur la seule base d’une détection ou de `gamecheck`. Utiliser un save de calibration jetable, FM ouvert et un build dont les données attendues peuvent être comparées à une source indépendante. Les commandes ci-dessous sont celles du README/CLI; adaptez `<build>`, valeurs d’effectif et chemin profil, ne les inventez pas.

1. Déterminer variante et layout réel (Windows, Mac FM27, Mac Touch); identifier binaire/module/architecture.
2. Construire le CLI si besoin, puis découvrir: `dotnet run --project src/Fm27Editor.Tools -c Release -- newbuild --layout windows --out offsets/<build>.json` (layout `macos` ou `touch-macos` si indiqué). Garder la sortie de découverte; résoudre chaque `MANUAL`, examiner chaque `WARN` et ses hints. Le résultat est read-only. Pour une répétition sur build déjà profilé, `newbuild --ignore-profiles` puis comparer avec la version existante; l’outil offre `profilediff`.
3. Confirmer `extends` sur layout de base, architecture/variant, build string/fingerprint, RTTI/vtables, roots, offsets dérivés et profils frères. Le profil JSON est un contrat de données: ne pas reporter aveuglément les anciennes mesures.
4. Vérifier read-only avec ground truth copiée de FM ou référence indépendante: `verify-build --profile offsets/<build>.json --expect-players N --expect-staff N --expect-humans N --negative-test`. Selon fonctionnalités, ajouter `--expect-clubs`/`--ground-truth`. Résoudre tous les FAIL et investiguer WARN/MANUAL.
5. Faire les round-trips d’écriture uniquement avec autorisation et save jetable: `verify-build --profile ... --writes`; test négatif `--negative-test`. Étudier `BuildVerifier`, `WriteAudit`, `HeapGate` et receipt avant d’interpréter un PASS.
6. Crash gate de session: `verify-build --profile ... --crash-gate state.json`; dans FM presser Continue puis Load Game; vérifier avec `verify-build --profile ... --crash-gate-verify state.json --release-gate --receipt receipt.json`. Seul verdict `VERDICT: PASS` avec receipt complet justifie, après revue humaine, d’envisager l’ouverture des écritures. Conserver le receipt comme preuve avec le changement.
7. Après déverrouillage éventuel, exécuter tests et comparer plusieurs champs/écrans réels; inclure profils et receipt. Aucun agent ne doit lever la politique read-only de sa propre initiative.

Sources disponibles dans ce clone: `src/Fm27Editor.Tools/NewBuildCli.cs`, `VerifyBuildCli.cs`, `src/Fm27Editor.Core/Offsets/NewBuildDiscoverer.cs`, `Verification/BuildVerifier.cs`, `HeapGate.cs`, `calibration/manifest.json`. Le runbook détaillé annoncé `docs/NEW-BUILD-PROFILE.md` était absent de l’arbre examiné; cette procédure reprend les consignes explicites `README.md`/`AGENTS.md` et implémentations CLI, sans prétendre reproduire le document manquant.

## 8. Build, tests et publication

Pré-requis: .NET 10 SDK. Pour l’app macOS NativeAOT/helper, Xcode Command Line Tools (`clang`) et chaîne Apple correspondante.

```bash
dotnet build Fm27Editor.slnx -c Release
dotnet test Fm27Editor.slnx -c Release
dotnet run --project src/Fm27Editor.App -c Release
```

Exemple CLI lecture seule (jeu avec save chargé):

```bash
dotnet run --project src/Fm27Editor.Tools -c Release -- players "Kobbie Mainoo"
```

Pour commande d’écriture, relire d’abord le code/usage précis: les commandes d’édition doivent explicitement recevoir `--write` ou autre opt-in documenté; par défaut, un plan/diagnostic ne doit pas modifier le jeu. `verify-build`/`newbuild` ne sont pas des tests unitaires: nécessitent le jeu et un profil/état correspondant.

Packaging macOS selon le README:

```bash
VERSION=1.5.1 scripts/package-mac.sh
```

`RID` par défaut est `osx-arm64`; `VERSION` modifie la version publiée. Le script produit `dist/Fm27Editor.app` et `dist/Fm27Editor-mac-arm64.dmg`; utilise NativeAOT, compile le helper C, embarque/copie les profils et signe ad hoc. Ne pas faire tourner un packaging destructif sur les sorties de travail sans vérifier d’abord le script: il supprime plusieurs chemins `dist/` et intermediates release.

## 9. CI et publication

`.github/workflows/build.yml` s’exécute sur push `main`, tag `v*` et pull request. Job Windows `windows-latest`: setup .NET 10, `dotnet test Fm27Editor.slnx -c Release`, publish self-contained single-file `win-x64` de `Fm27Editor.App` et `Fm27Editor.Tools`, artifacts. Job Mac `macos-latest` (commenté Apple Silicon): tests Core seuls puis `scripts/package-mac.sh` (NativeAOT/dmg), artifact. Sur tag, job release télécharge artifacts, calcule `SHA256SUMS.txt`, atteste provenance GitHub Actions puis crée release GitHub avec les assets.

Ne pas conclure que toute feature a été validée en live par CI: CI compile/teste, mais les gates mémoire (ground truth, round-trip et Continue/Load Game) exigent un jeu et une manipulation dédiée.

## 10. Conventions et règles de code

- Identifiants, code et messages de commit en anglais (`AGENTS.md`); guide présent en français.
- Types/méthodes/propriétés/enums en PascalCase, variables/paramètres en camelCase, conventions C# du code environnant. Nullable est activé, employer les gardes null explicites et `async` pour I/O UI comme le font les APIs concernées.
- Garder les couches: App orchestre et présente; Core lit/écrit et porte l’invariant métier; Tools réutilise Core.
- L’UI est dense et simple, pas d’ornement superflu. Utiliser navigation/onglets d’entité existants plutôt que fenêtres dupliquées. Liens d’entités ouvrent l’onglet réutilisable.
- Les couleurs UI sont exprimées au format sombre et résolues avec `UiKit.Brush(...)` afin que le changement clair/sombre repeigne correctement. Couleurs de données (kits/maillots) font exception.
- Date UI selon réglage global `DateSetting`, pas de format local codé en dur; exports/données machine ISO.
- Valeur indéterminée = inconnue; ne pas remplacer par `-1`, zéro arbitraire ou valeur supposée.
- Tests en échec: ne pas désactiver, supprimer, sauter ou diminuer assertions pour obtenir le vert. Identifier d’abord la cause; expected indépendant.
- Préserver fins de lignes mixtes lorsqu’on édite un fichier concerné; vérifier le diff réel.

## 11. Pièges et interdits critiques

1. **Aucun accès au jeu en ligne / shared save**; offline single-player seulement.
2. **Ne pas écrire via l’UI en accès mémoire brut**; passer par writer/service gardé et `IEditService`, avec confirmation/Undo/context.
3. **Ne jamais écrire avec un snapshot ancien**: les adresses changent après Load Game; relire/rebaser et bloquer si `SaveChanged`/session changée.
4. **Aucun RVA/offset spécifique build dans le code métier**. Les offsets vivent dans profils/layouts et doivent être mesurés pour chaque plateforme/édition pertinente. `NoSingleBuildCodeTests` et `ProfileParityTests` préviennent les régressions.
5. **Respecter bits de flags/encodages**: AGENTS signale notamment dates de contrat avec jour dans les 9 bits faibles (préserver flags hauts), attributs stockés ×5, valeurs tactiques `dword & 0x3FFF` (×100) et flags au-dessus. Vérifier les méthodes/helpers existants plutôt que refaire l’encodage.
6. **Structure native: chaque octet déplacé/écrit peut corrompre la sauvegarde ou faire crasher FM**. Vtable/type + ID + bytes snapshot + read-back obligatoires. Arrêter au premier mismatch.
7. **Pas de nouvelle allocation VM brute pour une structure que FM libèrera**; seule allocation appartenant à l’allocateur du jeu, et vérifier par HeapGate/crash gate.
8. **Vérifier les exceptions de fonctionnalité par build**: absence d’un champ/d’une liste n’est pas une valeur zéro. README note notamment absence de referee list sur Touch et limite Tactical Understanding équipe non pris en charge.
9. **Ne pas inclure de matériel inverse-engineering local/généré**; `AGENTS.md` précise que notes/captures/output issus du jeu restent hors dépôt, à l’exception des outils de `re/tools/memscan` explicitement versionnés.
10. **Pas d’assertion non prouvée sur FMRTE**: toute investigation suit le protocole AGENTS, capture une vraie opération et observe ses effets; navigation/chargement seuls ne prouvent rien.

## 12. Checklist avant commit

- [ ] Relire `AGENTS.md`, code voisin et tests; vérifier que description/objectif correspond à l’état courant.
- [ ] Aucune écriture directe UI, aucun offset codé en dur, aucune adresse gardée à travers reload.
- [ ] Snapshot/vtable/identité/ranges/flags/encodages et read-back vérifiés; transaction/ownership appropriés pour edits composites.
- [ ] Profil par build/variant actualisé seulement si mesuré; parité Steam/Game Pass et Mac/Touch pertinente vérifiée; un nouveau profil reste read-only sans preuve gate.
- [ ] Undo/journal correctement nommé et testés; plusieurs writes d’une action sont regroupés convenablement.
- [ ] Localisation, dates, thème, drafts/dirty state et abandon/reload conformes aux patterns UI.
- [ ] Tests ciblés et suite complète: `dotnet test Fm27Editor.slnx -c Release`; ne pas ignorer échec.
- [ ] Si mémoire/profils/hook: validation live appropriée sur sauvegarde jetable, source indépendante, Continue/Load Game et receipt si nouveau build; rapporter limites plutôt que surpromettre.
- [ ] Relire `git diff` (et fins de lignes), aucun secret, capture, save, binaire généré, receipt ou note locale non destinée à être commise.
- [ ] Mettre à jour le changelog si le changement mérite d’être visible; noms/commit en anglais.

## 13. `AGENTS.md` et `NOTES_IA.md`: résumé critique sans conflit

`AGENTS.md` prime pour les changements. Il décrit l’éditeur en mémoire hors ligne, exige backup/save jetable et .NET 10, donne build/test/run, séparation Core/App/Tools, lecture snapshot/écriture gardée, Undo via `IEditService`, formatages des données, règles UI, parité de profils et tests. Il impose un runbook `newbuild` → vérification read-only avec ground truth → écritures négatives/round-trip → Continue + Load Game → receipt PASS avant toute levée de `readOnly`. Il demande de tracer une vraie modification si l’on enquête sur le comportement FMRTE. Il mentionne aussi limites tactiques et tests `gamecheck` (lire `threw`, pas seulement `ok`). À traiter comme règles non négociables.

`NOTES_IA.md` est périmé sur un point majeur: il parle de SQLite/save files et liste `Database/SqliteConnection.cs`, `GameDataLoader.cs`, `EntitySaveHandler.cs`, `PlayerEditor.cs`, vues `ModuleViews/*.axaml`, `PlayersTab.axaml` et plusieurs fichiers `.axaml` qui n’existent pas dans l’arbre actuel. Ce dépôt attache la mémoire d’un processus; `MainWindow` et vues sont surtout C# en fichiers partiels, backend actuel `src/Fm27Editor.App/Backend.cs`, Core dans dossiers d’entités. Ne pas implémenter d’architecture SQL/Save-to-disk sur la foi de ces notes.

Les principes de `NOTES_IA.md` encore utiles seulement s’ils concordent avec le code/règles courantes: PascalCase types, camelCase variables, null checks, I/O asynchrone; thèmes/localisation centralisés; drafts et changements non sauvegardés à gérer. Cependant son conseil « couleurs UiKit » est plus précisément défini par `AGENTS.md`, et les raccourcis `Ctrl+S`/`Ctrl+K` cités dans les notes anciennes ne sont pas garantis par l’implémentation actuelle: confirmer dans le code avant de les annoncer. Le design de référence affiché comme `docs/DESIGN-RULES.md` n’était pas présent dans le clone; reprendre les règles UI explicitement copiées dans `AGENTS.md` et ne pas inventer son contenu.

Le fork Toolkit dispose de fichiers `src/Fm27Toolkit.App/MetaMode.cs` et `ToolkitApp.cs`; `README.md` décrit le CA META comme expérimental/en cours et précise qu’il reste à zéro jusqu’à définition/formule validée et vérification runtime. Le calcul actuellement présent dans `MetaMode.CalculateCaMeta` multiplie une propriété CA par 1,05, mais ne pas le traiter comme formule produit validée. Éviter d’étendre ces ajouts sans demander si la demande vise réellement le fork Toolkit ou l’éditeur upstream.

## 14. Glossaire

- **Adresse** : emplacement d’un objet/champ dans l’espace mémoire du processus. Temporaire, invalide après changement de save/session.
- **ARM64 / AArch64** : architecture native Apple Silicon utilisée par l’image FM macOS et hooks de cette plateforme.
- **Build / variant** : version de binaire FM, parfois différenciée par store (Steam/Game Pass) ou édition (Touch).
- **Crash gate** : séquence contrôlée d’écriture puis Continue/Load Game, vérifiée par receipt, pour déceler corruption/crash différé.
- **HeapGate** : barrière qui vérifie que toute allocation liée aux écritures appartient au heap du jeu et qu’aucune allocation VM brute n’est utilisée pour données dont FM devient propriétaire.
- **Hook / code patch** : branchement temporaire en mémoire vers une routine/cave; distinct d’une édition de valeur dans un objet.
- **Layout** : JSON de base partagé par une famille de builds (`offsets/layouts/*.json`).
- **Offset** : déplacement de champ dans un objet; **RVA** : déplacement d’une adresse dans l’image/module par rapport à sa base chargée.
- **Profil** : mesures + caractéristiques d’un build dans `offsets/*.json`; gouverne la compatibilité et les écritures.
- **Snapshot** : valeurs décodées d’un objet au moment d’une lecture; sert d’attendu et ne garantit pas que le jeu ne l’a pas changé depuis.
- **Vtable** : pointeur de table virtuelle C++ servant de marqueur de type pour les instances; le RVA de vtable est profilé par build.
- **Write guarded / stale** : écriture qui ne passe que si cible/type/identité/valeur attendue sont toujours ceux lus; stale signale une cible périmée.
- **Undo journal** : historique des octets avant/après; Undo refuse si le jeu a modifié les octets depuis l’édition.

## 15. Portée de cette analyse

L’analyse a couvert les documents prioritaires disponibles, workflow CI, solutions et métadonnées des projets, cartographie des dossiers/fichiers, profils/layouts/calibration, outils `newbuild`/`verify-build`, sessions et chemins d’accès mémoire, journal/écritures/allocations/hooks, exemples UI et feature Toolkit. Le dépôt cloné contenait environ **990 fichiers, 14 MiB**; l’arbre compte notamment 240 fichiers C# Core, 149 App, 252 fichiers C# dans tests. Vu la taille, il ne s’agit pas d’une lecture ligne par ligne de chaque fichier métier/test ni d’une validation live sur Windows/macOS et FM: le guide décrit les couches et exemples représentatifs et cite les preuves examinées.

Éléments manquants dans le clone, donc non couverts: `docs/NEW-BUILD-PROFILE.md`, `docs/DESIGN-RULES.md`, et les fichiers de sauvegarde/calibration annoncés à l’extérieur du dépôt. Aucun `dotnet build/test`, test sur jeu ou publication n’a été exécuté pendant cette analyse. Pour une modification particulière, inspecter en priorité la classe Core et les tests nommés dans ce guide; pour détailler un sous-système vaste, poursuivre par étapes ciblées.
