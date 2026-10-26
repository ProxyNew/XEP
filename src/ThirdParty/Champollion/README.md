# Champollion v1.3.2

XEnchantmentPatcher bundles the unmodified Windows executable from
[Orvid/Champollion v1.3.2](https://github.com/Orvid/Champollion/releases/tag/v1.3.2).
It runs as a separate command-line process for PEX script analysis. Skyrim does
not need this tool. Keep this folder beside XEnchantmentPatcher.exe, including
the source archive and notices, when copying or distributing a published build.

## Pinned upstream identity

- Repository: <https://github.com/Orvid/Champollion>
- Tag: `v1.3.2`
- Commit: `108bb84fb960884639560c04cd67143ba0a9608f`
- Release asset: `Champollion.v1.3.2.zip`
- Asset SHA256: `EA53054276AC8006CCD3B323286BFBC6E34A454FA419D08DA9BD440CBD31B383`
- Executable SHA256: `450D9C74DADE66331D573CFAD881EF3E55384B5478E019AF6D90A82378D1A1EC`
- Included source: `Champollion-v1.3.2-source.zip`, produced with `git archive --format=zip --prefix=Champollion-v1.3.2/` at that commit.
- Source archive SHA256: `3DF1B12A58138AA4C8FE75C9467BD05C4FA2EFFDEA30564C6FDFF925F68C3688`

The source archive includes the upstream CMake files, vcpkg dependency manifest
and Windows build workflow. Upstream builds use a static runtime and static
Boost.Program_options. No upstream source changes were made. The executable
was downloaded from the release asset; it was not rebuilt locally.

## Notices and replacement

Copyright (c) 2022 Nikita Lita; (c) 2015 Orvid King; (c) 2013 Paul-Henry Perrin.
Champollion is licensed under LGPL v3. See [LGPL](LICENSE-LGPL-3.0.txt) and
[GPL](LICENSE-GPL-3.0.txt). The [Boost license](LICENSE-Boost-1.0.txt) is included
for its dependency.

Set `XEP_CHAMPOLLION_PATH` to an alternative compatible executable to override
the bundled copy. If the bundle is absent, the legacy search for Champollion.exe
beside the app or in a Champollion folder at an ancestor directory remains.
Missing tools retain the existing warning and conservative script protection.
Cache identities include the actual executable's SHA256, so replacement tools
do not reuse the old tool's decompilation results.

Builds copy this folder locally and publishing keeps it outside the single-file
XEP executable. Builds and the app do not download dependencies from GitHub.
To update it, replace the executable and source from a reviewed upstream release,
update these pins/notices and validate real PEX decompilation, affected script
consumers and published-folder contents.
