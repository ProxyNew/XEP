# XEnchantmentPatcher (XEP)

<img src="src/Assets/iron-helmet-fire.png" alt="XEnchantmentPatcher icon" width="128" align="right">

A patcher for **Skyrim Special Edition / Anniversary Edition** that gives the weapons, armor and jewelry from your
mods **enchanted versions** — and puts them only where the plain item already drops. The armor set from a mod you
installed turns up enchanted in the same lists, at the same levels and for the same enemies and merchants as the
original, instead of never appearing enchanted at all.

You review every choice before anything is written. The result is a validated ESPFE plugin that you install with
Mod Organizer 2 like any other mod. None of your existing mods are changed.

---

## Features

| Feature | What it does | Default |
|---|---|---|
| **Enchanted variants** | Creates enchanted versions of eligible weapons, armor, rings, amulets and circlets. Already-enchanted, non-playable and unsupported items are skipped. Clothing is not supported yet. | per plugin, you choose |
| **Enchantment origins** | Pick which plugins supply enchantments (the base game, enchantment overhauls, …) and drag them into priority order. Equivalent enchantments are merged by their actual effect and charge, not by name. | you choose |
| **Follows existing distribution** | Reads leveled lists and the configs of SPID, CID, SkyPatcher and LLOS to find where each plain item really goes, and places the enchanted version on those same routes. | always |
| **Availability kept** | Level windows, quest unlocks, global switches and chance controls of the original route are carried over, so enchanted gear does not appear earlier or in places the plain item cannot. Routes that cannot be proven safe are left out rather than guessed. | always |
| **Specialized distribution** | Items that need a separate decision get a per-item choice: off, enchanted lists only, the item's own lists, best available, general enchanted loot (expert) or merchant stock. Each row shows XEP's recommendation. | recommended per item |
| **New material lists** | Lets the enchanted-lists mode add a missing material family inside a compatible enchanted list. | ON |
| **Broader fallback** | Extra enchanted routes for items with no exact match, still within the same checks. | OFF |
| **Resolve leveled-list conflicts** | Restores entries that a later mod dropped from ordinary weapon/armor lists by accident. | OFF |
| **Keep undistributed variants** | Writes variants that found no safe loot route into the plugin anyway (they will not drop anywhere). | OFF |
| **Exact output preview** | Shows exactly what will be generated — new records, destinations, warnings and items left undistributed — before any file is written. | — |
| **Validated output** | Checks references, masters, record limits and list sizes, splits output over numbered ESPFE files of up to 4,096 records, backs up the previous output and writes a JSON report of every decision. | — |
| **Rules files for mod authors** | Mods can ship a small JSON file in `Data/XEnchantmentPatcher/` that sets XEP's defaults for their equipment. | — |
| **Command line** | Scan, preview and generate without the interface; `--help` lists the options. | — |

## Requirements

| Requirement | Needed | Notes |
|---|---|---|
| Windows 10 or 11, 64-bit | yes | Self-contained; no .NET installation needed. |
| Skyrim Special Edition / Anniversary Edition | yes | Skyrim VR and Legendary Edition are not supported. |
| [Mod Organizer 2](https://github.com/ModOrganizer2/modorganizer) | yes | Run XEP through MO2 so it sees your active profile's plugins, loose scripts and archives. |
| [SKSE64](https://skse.silverlock.org/) | when used | Only needed in game when the generated output includes XEP's script companions; most setups already have it. |
| Champollion (Papyrus decompiler) | included | Bundled in the `Champollion` folder; used to analyse scripts that distribute items. |

## Installation

1. Download `XEnchantmentPatcher-1.0.0-win-x64.zip` from [Releases](../../releases) and extract it to a folder outside
   Skyrim's `Data` folder. Keep the `Champollion` folder beside `XEnchantmentPatcher.exe`.
2. In MO2, add `XEnchantmentPatcher.exe` as an executable and always start it from MO2.
3. Check the **Data folder** and **Output folder**, tick the enchantment origins you want and put them in order.
4. Open **Patch review**: choose plugin rules, include the equipment you want and settle any specialized-distribution
   choices.
5. Calculate the **Output preview** and read its warnings.
6. Choose **Generate and validate ESPFE**, then install the **whole** output folder as a mod in MO2 (including any
   `Scripts` and `SEQ` folders). Load the generated plugins after the plugins they patch, numbered files in order.

Settings are saved in `%LOCALAPPDATA%\XEnchantmentPatcher\settings.json`.

**Saves:** generated records and scripts can persist in your save. Regenerating can change record identities, so keep a
save from before you install or replace the patch. After adding or removing mods, run XEP again.

## Documentation

- [Usage guide](docs/USAGE.md): every option, the distribution choices, output and command-line use.
- [Rules files for mod authors](docs/MOD-AUTHOR-RULES-FILE.md), with [examples](docs/rules-file-samples/) and a
  [JSON Schema](docs/schemas/xep_pluginrules.schema.json).

## Building from source

Requirements: Windows x64, the .NET SDK version named in `global.json` (10.0.400 or a later 10.0.4xx patch) and network
access for NuGet restore.

```powershell
git clone https://github.com/ProxyNew/XEP.git
cd XEP
dotnet publish src/XEnchantmentPatcher.csproj -c Release -o out
```

Result: `out\XEnchantmentPatcher.exe` (single file) and `out\Champollion`. Keep both together.

| Build dependency | Source |
|---|---|
| .NET 10 (WPF, Windows Forms) | .NET SDK, runtime bundled into the exe |
| Mutagen.Bethesda.Skyrim 0.54.4 and its dependencies | NuGet, versions pinned in `src/packages.lock.json` |
| Champollion v1.3.2 | bundled in `src/ThirdParty/Champollion`, with its source archive |

To produce the release downloads with all license notices and dependency sources, run
`pwsh -File tools/package-release.ps1 -Destination <new folder>` (PowerShell 7; downloads about 290 MB of pinned
upstream source archives on first run).

## License

[GPL-3.0-only](LICENSE). See [licensing and corresponding source](LICENSING.md) and the
[third-party notices](THIRD-PARTY-NOTICES.md). Each release on GitHub includes a complete corresponding-source download.

Thanks to Noggog and the Mutagen contributors, and to Orvid, Nikita Lita and Paul-Henry Perrin for Champollion.

If you redistribute or modify XEP, the GPL requires you to keep the copyright notices and publish your changes as
source code under the same license.
