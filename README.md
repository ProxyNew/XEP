# XEnchantmentPatcher

Source of XEnchantmentPatcher (XEP), a Windows tool for Skyrim Special Edition
that creates enchanted variants of mod equipment and places them where the
plain items already drop. This page is for building it. The download and the
user guide are on the mod page.

## Requirements

- Windows x64
- .NET SDK 10.0.400 or a later 10.0.4xx patch (`global.json`)
- Network access for the NuGet restore
- PowerShell 7, for the release package only

## Build

```powershell
dotnet publish src/XEnchantmentPatcher.csproj -c Release -o out
```

`out\XEnchantmentPatcher.exe` is the program. It is self-contained and needs
the files beside it, including the `Champollion` folder. Start it through Mod
Organizer 2 so that it sees the active profile's Data folder;
`XEnchantmentPatcher.exe --help` lists the command-line options.

## Release package

```powershell
pwsh -File tools/package-release.ps1 -Destination <new folder>
```

This writes `XEnchantmentPatcher.zip` (the download: the executable at the
root, the program files in `XEnchantmentPatcher-app`) and
`XEnchantmentPatcher-source.zip` (this source with the dependency sources).
The first run downloads about 290 MB of pinned dependency source archives
into `licenses/sources`.

## Layout

| Path | Holds |
|---|---|
| `src/` | The program: C#, WPF, .NET 10. |
| `src/Runtime/` | Papyrus scripts that generated output can include, source and compiled. |
| `src/ThirdParty/Champollion/` | The bundled Papyrus decompiler v1.3.2 with its source archive. |
| `tools/` | The publish and packaging scripts. |
| `licenses/` | Dependency notices and the manifests that pin their sources. |

Dependencies come from NuGet (Mutagen.Bethesda.Skyrim 0.54.4 and what it
needs); `src/packages.lock.json` pins the versions.

## License

GPL-3.0-only: [LICENSE](LICENSE), [licensing and corresponding source](LICENSING.md),
[third-party notices](THIRD-PARTY-NOTICES.md).
