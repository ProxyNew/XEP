# XEnchantmentPatcher

**Version 1.0.0** · Windows x64 · Skyrim Special Edition

XEnchantmentPatcher (XEP) creates enchanted variants of selected weapons, armor and jewelry and places them in compatible distribution routes from your load order. The interface provides enchantment-origin priority, equipment review, specialized distribution choices, an exact output preview and validated ESPFE generation.

## Using XEP

1. Obtain the Windows x64 release package. Keep its Champollion folder and license notices with the executable. Source code is a separate download and is not needed to run XEP.
2. Add `XEnchantmentPatcher.exe` to Mod Organizer 2 and launch it through MO2 so it sees the active profile's plugins and scripts.
3. Select the Data and output folders, choose enchantment origins and review the equipment and distribution choices.
4. Calculate the output preview, inspect warnings, then generate and install the complete output folder through MO2, including any Scripts and SEQ companions.

See the [usage guide](docs/USAGE.md) for configuration, output and save considerations. Mod authors can supply [rules files](docs/MOD-AUTHOR-RULES-FILE.md), with [examples](docs/rules-file-samples/) and a [JSON Schema](docs/schemas/xep_pluginrules.schema.json).

**Default options:** Resolve leveled-list conflicts is off; Allow new material lists for specialized enchanted distribution is on. These choices can be changed in the interface, and existing saved profile choices are preserved.

## Building from source

Use Windows and the .NET 10 SDK selected by `global.json`. The app uses WPF and Windows Forms. NuGet restore requires network access. A separate .NET runtime installation is not required for the self-contained published app.

From the repository root:

```powershell
dotnet restore src/XEnchantmentPatcher.csproj --locked-mode
dotnet build src/XEnchantmentPatcher.csproj -c Release --no-restore
```

The committed Papyrus source/bytecode pairs and bundled Champollion files are required build inputs, not disposable build output.

## Creating a release package

Use PowerShell 7 from a Git checkout with at least one commit. If you obtained these files without Git history, initialize and commit the repository before using the release packager.

```powershell
pwsh -File tools/package-release.ps1 -Destination artifacts/release-1.0.0
```

The destination must be new. Packaging builds the application, verifies the restored dependency inventory, includes license notices and matching source, checks the executable's startup identity and verifies the resulting ZIP. Large upstream source archives are downloaded from pinned URLs and checked against SHA-256 hashes; they are excluded from Git.

The Nexus 1.0.0 download separates end-user files from source. Its matching `XEnchantmentPatcher-1.0.0-corresponding-source.zip` is provided as an asset on the [v1.0.0 release](https://github.com/ProxyNew/XEP/releases/tag/v1.0.0). It contains the exact XEP source/build inputs and third-party source archives, including Mutagen and Champollion. GitHub's automatically generated repository source ZIP alone does not contain those dependency archives.

For split distribution, keep the complete corresponding-source asset freely available and place a clear link beside the binary download. Retain the binary's licenses and notices. See [LICENSING.md](LICENSING.md).

Change `<Version>` in `src/XEnchantmentPatcher.csproj` and update [CHANGELOG.md](CHANGELOG.md) for later versions. GitHub release tags are separate from the project version; use a matching tag such as `v1.0.0` when publishing this version.

## Repository layout

- `src/`: application, engine, visual assets, runtime scripts and bundled Champollion.
- `tools/`: publication and release packaging.
- `docs/`: usage guide and mod-author rules contract/examples/schema.
- `licenses/`: third-party notices, pinned dependency inventory and source-download manifests.

## License

Project-owned software is licensed **GPL-3.0-only**. See [LICENSE](LICENSE), [licensing and corresponding source](LICENSING.md), and [third-party notices](THIRD-PARTY-NOTICES.md). Third-party software and artwork retain their own rights and notices.
