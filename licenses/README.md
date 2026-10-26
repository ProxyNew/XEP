# Resolved third-party dependency inventory

`dependencies.json` records the reviewed runtime/library package graph: 31 package rows, excluding the build-only `Microsoft.NET.ILLink.Tasks` entry. The release packager compares every fresh restore against these exact IDs and versions and writes the accepted graph and runtime-pack identities to `licenses/resolved-components.json` in the release. Update the reviewed inventory, notices and pinned source before accepting dependency changes.

`source-downloads.json` is a top-level array recording each archive URL and SHA-256. Packaging must download and hash-check these pinned sources into release staging when they are absent; the local `sources/` cache is optional and ignored by Git. Each source URL pins an exact repository commit or release version. The two `*-LICENSE.md` files in the cache are extracted convenience copies from pinned submodule snapshots; the download manifest lists archive files only. `nuget/<id>/<version>/` contains upstream license files copied byte-for-byte from the corresponding repository source archive. Package rows share an archive when several package IDs come from one repository.

Source qualifications:

- The NuGet metadata for `ini-parser-netstandard/2.5.3` lacks a repository commit and the package has no SourceLink/PDB. Its redirected upstream owner fork exposes tag `2.5.3` at `b1beea584b23c65f7c9bcafd8ab9be62395f477a`; the tagged project declares package version 2.5.3 and assembly name `INIFileParser`, matching the resolved package metadata and DLL identity. The exact source snapshot and upstream MIT license are included.
- Both Microsoft.Extensions packages point to the full dotnet/runtime source archive at their nuspec-pinned commit, shared in `sources/dotnet-runtime-f57e6dc747158ab7ade4e62a75a6750d16b771e8.zip`.
- The NexusMods.Paths source archive's pinned `extern/meta` submodule is also included as `NexusMods.App.Meta-9c4844439c53f8b8b9f64fb707c91b469238b15e.zip`; the documentation theme submodule is not included. The main package project source and build props are in the primary archive.
- Reloaded.Memory imports the pinned `Reloaded.Project.Configurations` submodule for its build. Its exact pinned source ZIP is included separately. The docs theme submodule is not included. The main archive includes the library, generator, and package license.
- These codeload archives contain repository snapshots and do not preserve Git history or signatures. Submodule contents are separate archives at their recorded gitlink commits.

The resolved package set is an inventory aid, not a legal conclusion. Check the final redistributed files and any additional runtime/publish assets against the release-specific license obligations.
