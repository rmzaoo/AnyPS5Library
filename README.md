# AnyPS5 Library (macOS)

A macOS app (Apple silicon) for browsing a game catalog, managing a PS5 dump/image/FPKG collection and preparing
games for [AnyPS5](https://github.com/boykopovar/AnyPS5). Unofficial; not affiliated with Sony or the AnyPS5 project.

Run `AnyPS5 Library.app` from this folder. It is self-contained (no .NET install needed).

## What's inside

- `upstream/` — git submodule: [pearlxcore/PS5PkgTool](https://github.com/pearlxcore/PS5PkgTool) v1.3.0, unmodified.
  Its engine (exFAT, UFS2/FFPKG, PFSC, PFS, ProsperoPkgTool) is portable .NET and is used as-is. Its
  Windows Forms UI is not used.
- `pkg-to-anyps5/` — git submodule: [awake-devel/pkg-to-anyps5](https://github.com/awake-devel/pkg-to-anyps5) v0.2.5
  (GPL-3.0-or-later), built from source and bundled as a separate executable inside the app.
- `src/AnyPS5Library/` — the Avalonia UI and glue code.
  - `Metadata/catalog.json` — factual game info (title, IDs, developer, publisher, date, genres, artwork links)
    shown in the Catalog and details panels, produced by `tools/import_metadata.py`. An optional local
    `Metadata/pages.json` adds descriptions and screenshots; it is copyrighted store text, so it is git-ignored
    and not part of the repository.

## Tabs

| Tab | What it does |
| --- | --- |
| **Catalog** | Every game in `catalog.json`: search, *AnyPS5* stage column, details (cover, hero art, genres, facts, description, screenshots), **Show in My Library** when you own the title. |
| **My Library** | *AnyPS5* stage column; your scanned dumps, `.pkg`, `.exfat`, `.ffpkg`, `.ffpfsc`: search, details (info, overview, artwork, files with extraction, trophies, executable info, raw `param.json`), **Export for AnyPS5**, **Open in Tools**, **Show in Finder**. |
| **Compatibility** | How far each game gets on AnyPS5, from the community [AnyPS5 blocker map](https://alxcbtw.github.io/anyps5-roadmap/) (see below). |
| **AnyPS5 Export** | Debug package, dump folder or image → `source/` + `app0/` via pkg-to-anyps5 (see below). |
| **Image Tools** | Convert between dump/exFAT/FFPKG/FFPFSC, build a debug FPKG, extract, verify, repair exFAT, refresh AMPR, rebuild FFPKG. |
| **Tasks** | Queued, cancellable, retryable jobs with progress; history persists between launches. |
| **Log** | Live log; **Open Log File** shows it in Finder. |
| **Settings** | Library folders, debug passcode, workspace folder, reveal-output option. |

## Compatibility

The **Compatibility** tab shows the data of the [AnyPS5 blocker map](https://alxcbtw.github.io/anyps5-roadmap/)
([repo](https://github.com/AlxcBTW/anyps5-roadmap), MIT, community-made, not official). The app reads its
`roadmap.json` on launch and on **Refresh**, and keeps the last copy so the tab works offline.

- **List:** game, title IDs, verified **Stage**, unverified **Claim** (shown with `?`), confidence, where it
  **Stops at** (first open blocker), last activity, and whether it's in **My Library**. Sorted by stage, highest
  first. Filters: search (name, alias, title ID, tag, engine, blocker), stage, **In my library**.
- **Details:** cover, stage with its meaning, OS / GPU / build / date of the report, the quote it rests on and a
  link to its source; the unverified claim and the stage on PR branches (kept separate, as the roadmap asks);
  every open blocker with a link to its GitHub issue; each title ID with region and stage.
- **Buttons:** **Open on Roadmap** (`#/games/<id>` on the site), **Store Page**, **Show in My Library**.
- **AnyPS5 column** in Catalog and My Library: the verified stage for that title ID ("Not reported" when the game is
  on the map without a stage; empty when it isn't on the map). Sorting uses the stage order.

Stages, lowest to highest: Converts, Loads, Starts, First draw, Presents, Menus, Ingame, Playable.

## AnyPS5 Export

- Images are extracted to a staging folder first.
- Passcode-encrypted debug packages (which pkg-to-anyps5 refuses) are decrypted with the PS5 PKG Tool engine
  automatically, then converted as a folder.
- Optional: runs the AnyPS5 relinker on `source/eboot.bin` → `app.elf` and copies the AnyPS5 system libraries
  (`*.prx`) into `libs/`. Set the relinker and libs paths on the AnyPS5 Export tab.

## Not available on macOS

- The LibProsperoPkg builder: it wraps Sony's `libScePubTools.dll`, which is mixed-mode C++/CLI
  (native Win32 code), so it cannot run on macOS. All package work uses ProsperoPkgTool.
- Windows Explorer integration, the raw-drive (`\\.\PhysicalDrive`) UFS2 writer, and some niche dialogs
  of the Windows app (saved views, bulk rename/move, in-place image file editors).

## Legal

AnyPS5 Library is not affiliated with, endorsed by or sponsored by Sony Interactive Entertainment, the AnyPS5
project, or any game publisher. PlayStation, PS4 and PS5 are trademarks of Sony Interactive Entertainment Inc.,
named here only to describe the formats the app works with. Game titles and artwork belong to their owners;
artwork is loaded from the publishers' own CDNs and never bundled.

The app contains no Sony code, keys or game content. Use it only with packages, dumps and images you made yourself
or are otherwise entitled to use, and follow the laws and licence agreements that apply to you.

## License

AnyPS5 Library is free software under the **GNU General Public License v3.0** (see [`LICENSE`](LICENSE)), because it
links the GPL-3.0 PS5 PKG Tool engine.

| Component | License | How it is used |
| --- | --- | --- |
| [PS5 PKG Tool](https://github.com/pearlxcore/PS5PkgTool) engine (`upstream/`) | GPL-3.0 | Linked (Core, Ffpfsc, Ufs2). See `upstream/THIRD_PARTY_NOTICES.md`. |
| ProsperoPkgTool (`upstream/PS5PKGTool/ThirdParty/ProsperoPkgTool/`) | MIT | Linked. |
| [pkg-to-anyps5](https://github.com/awake-devel/pkg-to-anyps5) | GPL-3.0-or-later | Separate executable inside the app; its license ships in `Contents/Resources/`. |
| [Avalonia](https://avaloniaui.net/), .NET runtime | MIT | UI framework and runtime. |
| [AnyPS5 blocker map](https://github.com/AlxcBTW/anyps5-roadmap) data | MIT | Downloaded at runtime by the Compatibility tab, credited in the tab. |

`upstream/` and `pkg-to-anyps5/` are submodules: this repository only records which commit of each original
project to use, it does not copy their files. The upstream project's Windows-only `ThirdParty/LibProsperoPkg12/`
(Sony's proprietary `libScePubTools.dll`) and `ThirdParty/Oodle/` (RAD/Epic's proprietary Oodle) are not used by this
app and are left out of the built `.app`.

## Data and settings

Everything lives in `~/Library/Application Support/AnyPS5Library/`:

| Path | Contents |
| --- | --- |
| `settings.json` | Settings. |
| `library.json` | Cached library scan. |
| `tasks.json` | Task history. |
| `anyps5library.log` | Log file. |
| `art-cache/` | Catalog artwork (only from `image.api.playstation.com` and `steamstatic.com`). |
| `roadmap.json` | Last downloaded AnyPS5 compatibility data. |

## Rebuild

```sh
git clone --recurse-submodules https://github.com/rmzaoo/AnyPS5Library.git
cd AnyPS5Library
bash dotnet-install.sh --channel 10.0 --install-dir .dotnet   # first time only
./build-app.sh
```

In an existing clone without the submodules, run `git submodule update --init` first.

This uses the project-local .NET SDK in `.dotnet/`; reinstall it with
`bash dotnet-install.sh --channel 10.0 --install-dir .dotnet` if needed. It also needs `cargo`.

### Source layout

| File | Role |
| --- | --- |
| `src/AnyPS5Library/Views/MainWindow.axaml(.cs)` | Main window: all tabs and their wiring. |
| `src/AnyPS5Library/Views/Dialog.cs` | Alert / confirm dialog. |
| `src/AnyPS5Library/Infrastructure/Compatibility.cs` | AnyPS5 blocker map: download, cache, lookup by title ID. |
| `src/AnyPS5Library/Infrastructure/GameMetadata.cs` | Catalog lookup and artwork cache. |
| `src/AnyPS5Library/Infrastructure/Rows.cs` | Grid row models (catalog, library, files, tasks). |
| `src/AnyPS5Library/Infrastructure/AppState.cs` | Settings, manifest, log. |
| `src/AnyPS5Library/Infrastructure/Operations.cs` | Scan, export and image-tool operations. |
| `tools/import_metadata.py` | Builds `Metadata/catalog.json` from a local catalog data folder (factual metadata only). |
