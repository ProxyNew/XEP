# XEnchantmentPatcher licensing

SPDX-License-Identifier: GPL-3.0-only

Copyright (C) 2026 XEnchantmentPatcher contributors.

XEnchantmentPatcher's project-owned software is distributed under the GNU General Public License, version 3 only. You may use, study, modify and redistribute it under that license. It is provided without warranty, including implied warranties of merchantability or fitness for a particular purpose. See [LICENSE](LICENSE) for the complete terms.

Third-party software remains under its own licenses and copyright notices; this declaration does not relicense it. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Game files, third-party mod content and generated patches are not automatically covered by XEP's license merely because XEP reads or writes them. Their contents and original rights remain relevant.

## Binary releases and corresponding source

Keep LICENSE, this document, THIRD-PARTY-NOTICES.md, the required license notices and the Champollion executable/notices with the binary release. The release packager initially produces a combined package with matching XEP source, project/build scripts, pinned restore metadata, and dependency source archives/notices. Third-party sources are unmodified upstream snapshots unless explicitly documented otherwise.

For Nexus 1.0.0, source is distributed separately as `XEnchantmentPatcher-1.0.0-corresponding-source.zip` on the [v1.0.0 GitHub release](https://github.com/ProxyNew/XEP/releases/tag/v1.0.0). This asset includes the complete source payload from the combined package; its `source/README.md` explains how to rebuild. End users do not need it to run XEP.

Under GPLv3 section 6(d), provide equivalent free access to the matching corresponding source, with clear directions beside the binary download, and keep it available. Upload and verify the source asset before distributing the slim binary ZIP. A moving development branch or GitHub's automatic repository ZIP alone is not a replacement for this complete release source asset.

## Preparing a release

Run `tools/package-release.ps1 -Destination <new-private-directory>` from PowerShell 7. It publishes a private build and writes a complete ZIP plus a SHA-256 inventory. It does not install into MO2 or generate patches. The package is built with the installed .NET 10 SDK; resolved package versions must match the reviewed dependency inventory. A dependency update requires updating the inventory, notices and matching source before packaging succeeds.

Packaging downloads pinned upstream source archives listed in `licenses/source-downloads.json` and verifies their SHA-256 hashes. An optional local `licenses/sources/` cache avoids repeat downloads; these large archives are excluded from Git but included in the release ZIP. Network access is needed when the cache is absent.

Only publish a package after the source/asset rights and the separate behavior-validation gates are satisfied. Licensing validation does not establish game compatibility.
