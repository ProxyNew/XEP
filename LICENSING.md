# XEnchantmentPatcher licensing

SPDX-License-Identifier: GPL-3.0-only

Copyright (C) 2026 XEnchantmentPatcher contributors.

XEnchantmentPatcher's project-owned software is distributed under the GNU General Public License, version 3 only. You may use, study, modify and redistribute it under that license. It is provided without warranty, including implied warranties of merchantability or fitness for a particular purpose. See [LICENSE](LICENSE) for the complete terms.

Third-party software remains under its own licenses and copyright notices; this declaration does not relicense it. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Game files, third-party mod content and generated patches are not automatically covered by XEP's license merely because XEP reads or writes them. Their contents and original rights remain relevant.

## Binary releases and corresponding source

The release packager produces two downloads: `XEnchantmentPatcher.zip` for the
application and `XEnchantmentPatcher-source.zip` for its matching corresponding
source. Keep the application download's XEnchantmentPatcher-app/licenses
folder, which holds LICENSE, THIRD-PARTY-NOTICES.md, the notice texts and
SOURCE.md, with the application; the published developer build keeps LICENSE,
THIRD-PARTY-NOTICES.md and the licenses directory in XEnchantmentPatcher-docs
instead. The licenses/README.md index maps each component's original notice to
its retained file; only byte-identical notices are consolidated.

Offer both ZIPs together on the release page, with equivalent free access and
clear source-download directions beside the application download. Keep matching
source available. A moving development branch is not a replacement. SOURCE.md in
the application records the companion source ZIP's SHA-256 and build fingerprint.

The source ZIP contains the exact XEP snapshot, build scripts, pinned restore
metadata, original dependency notices and matching upstream source archives.
Its BUILDING.md explains how to rebuild independently of the application ZIP.
Third-party sources are unmodified upstream snapshots unless documented otherwise.

## Preparing a release

Run `tools/package-release.ps1 -Destination <new-private-directory>` from PowerShell 7. It publishes a private build and writes both ZIPs plus separate SHA-256 inventories. It does not install into MO2 or generate patches. The package is built with the installed .NET 10 SDK; resolved package versions must match the reviewed dependency inventory. A dependency update requires updating the inventory, notices and matching source before packaging succeeds.

Packaging downloads pinned upstream source archives listed in `licenses/source-downloads.json` and verifies their SHA-256 hashes. An optional local `licenses/sources/` cache avoids repeat downloads; these large archives are excluded from Git but included in the matching source ZIP. Network access is needed when the cache is absent.

The upload steps for Nexus and GitHub are in [docs/RELEASING.md](docs/RELEASING.md).

Only publish a package after the source/asset rights and the separate behavior-validation gates are satisfied. Licensing validation does not establish game compatibility.
