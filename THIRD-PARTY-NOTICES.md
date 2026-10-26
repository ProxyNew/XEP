# Third-party notices

XEnchantmentPatcher uses the following software. Each component retains its own copyright and license. Full notices and source provenance accompany the release in `licenses/`; the build-specific inventory is `licenses/resolved-components.json`. In the slim Nexus release, source archives mentioned below are supplied separately in `XEnchantmentPatcher-1.0.0-corresponding-source.zip` on the [v1.0.0 release](https://github.com/ProxyNew/XEP/releases/tag/v1.0.0). Paths to source archives refer to that companion download.

## Mutagen and other managed dependencies

Mutagen.Bethesda.Skyrim 0.54.4, Mutagen.Bethesda.Core and Mutagen.Bethesda.Kernel are by Noggog and contributors and are licensed GPL-3.0-only. Upstream: https://github.com/Mutagen-Modding/Mutagen . XEP directly uses these libraries. Matching upstream source is included under `licenses/sources/`, and GPLv3 is included as `LICENSE`.

Indirect libraries, including GameFinder and NexusMods.Paths, have their own notices and source archives. `licenses/dependencies.json` records the reviewed package IDs, versions, license files and pinned source identities; `licenses/nuget/` contains their upstream notices. Credit here does not replace those license terms.

## Microsoft .NET 10 and Windows Desktop

The Windows x64 release is self-contained and includes .NET runtime and Windows Desktop components. Microsoft and .NET Foundation contributors license the principal runtime/WPF/Windows Forms code under MIT; bundled third-party components retain their own terms. The packager copies the license and third-party notices from the exact resolved runtime packs, with pack versions and hashes in `licenses/resolved-components.json`.

Upstream: https://github.com/dotnet/runtime , https://github.com/dotnet/wpf and https://github.com/dotnet/winforms . See `licenses/dotnet/` in the release for the actual pack notices.

## Champollion

Champollion v1.3.2 is an unmodified, separately executed Papyrus decompiler licensed LGPL-3.0-only. Copyright (c) 2022 Nikita Lita; (c) 2015 Orvid King; (c) 2013 Paul-Henry Perrin. Upstream: https://github.com/Orvid/Champollion .

The binary's `Champollion/` folder contains the executable, LGPLv3 and GPLv3 texts, Boost license, and provenance/build notes. Keep these files intact. Its pinned source archive is included in the corresponding-source download. Boost.Program_options is used under the Boost Software License 1.0; the full Boost 1.80.0 source distribution accompanies that download under `licenses/sources/`, identified by `licenses/boost-source.json`. Users may replace Champollion with a compatible executable through `XEP_CHAMPOLLION_PATH`; XEP does not require the bundled executable's hash to run.

## Visual assets and fonts

The current busy animation and application icon are original project artwork, as confirmed by the project maintainer on 18 October 2026. Their asset bytes are unchanged in this release. The UI uses the system Segoe UI font; no Sovngarde font is bundled by the current project.
