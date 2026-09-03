# Icod.Tar build and packaging workflow

This repository follows the canonical `uniblab/.github` C#/.NET build and release pattern, with one Tar-specific distribution smoke layered on top of the shared mechanics.

## Validation ladder

| Lifecycle | Configuration | Work |
| --- | --- | --- |
| local `build.cmd` / `build.sh` | `Debug` | clean, restore, build, test, pack, exact package validation |
| pull request | `Staging` | Windows/Linux/macOS build and test; Linux also validates generated NuGet artifacts |
| default branch | `Release` | six-runner Windows/Linux/macOS x64/ARM64 distribution validation plus Tar product smoke |
| `v<semver>` tag | `Release` | package/archive production and publication |

## Repository and package contract

The shared scripts discover the root solution, projects, executable outputs, package identity, and package version from the repository and MSBuild rather than deriving them from the GitHub repository name.

The production package is `Icod.Tar`, packaged as a .NET tool whose installed command is `tar`. The repository root `README.md` is declared as `PackageReadmeFile`, packed at the package root, and also included with `LICENSE` in each runtime-specific ZIP archive.

`Get-RepositoryMetadata.ps1` exports a repository-relative solution path so metadata produced on Linux is safe to consume on Windows and macOS jobs.

## Scripts

### `RepositoryTools.psm1`

Provides the common helpers for locating the root solution, enumerating solution projects, reading MSBuild properties, discovering executable projects, and inspecting generated NuGet package metadata.

### `Get-RepositoryMetadata.ps1`

Reports whether the repository has a root solution, its portable repository-relative path, and whether that solution contains executable projects. PR, main, manual distribution, and release workflows consume these outputs.

### `Invoke-Build.ps1`

Implements the local build contract used by `build.cmd` and `build.sh`. The default `Debug` invocation runs:

```text
clean → restore → build → test → pack → validate
```

Individual stages may be selected independently.

### `VerifyPackageArtifact.ps1`

Validates the exact generated `.nupkg` files supplied by the caller. It verifies package identity/version metadata, confirms any declared package README exists, and checks .NET tool metadata shape where applicable.

### `VerifyDistribution.ps1`

Performs the common authoritative source-tree validation:

1. restore;
2. build;
3. test;
4. pack without rebuilding; and
5. exact NuGet package validation.

The six-platform `main` and manually dispatched distribution-validation workflows use this script.

### `VerifyTarDistribution.ps1`

Provides the product-specific validation retained during normalization. It reuses the common distribution outputs to:

- execute the standalone `tar --version`;
- inspect `Icod.Tar` tool metadata and require `tar.dll`;
- install the generated package from an isolated local NuGet source;
- execute the installed `tar --version`; and
- create and list a sample tar archive.

This script is intentionally an extension to the shared build mechanics rather than a second build system.

### `SelectReleasePackages.ps1`

Selects only packages whose actual nuspec version matches the requested `v<semver>` release version. A mismatched project/package version therefore cannot be published merely because the package was built.

### `BuildReleaseArchive.ps1`

Discovers the `tar` executable through MSBuild and creates framework-dependent single-file ZIPs for:

```text
win-x64
win-arm64
linux-x64
linux-arm64
osx-x64
osx-arm64
```

Each archive includes the executable plus repository `LICENSE` and `README.md`.

## Tagged release graph

After validating the tag and confirming that the tagged commit is contained in the default branch, package production and executable archive production proceed independently:

```text
metadata
  ├── package
  │     ├── publish-nuget
  │     └── publish-github-packages
  └── archives (6 RIDs)

publish-nuget ────────────────┐
publish-github-packages ──────┼── github-release
archives ─────────────────────┘
```

NuGet.org and GitHub Packages publish in parallel from the same validated package artifact. Both use `--skip-duplicate`, making a retried release safe after a partial registry publication. GitHub Release creation still waits for every applicable package-publication and archive job to succeed.

## Release prerequisites

For NuGet.org publication the repository requires:

- a GitHub environment named `Release`;
- an Actions secret named `NUGET_USER`; and
- a NuGet.org Trusted Publishing policy authorizing repository `uniblab/Icod.Tar`, workflow `release.yaml`, and environment `Release`.

The Trusted Publishing package scope must authorize `Icod.Tar`. GitHub Packages and GitHub Release use the job-scoped `GITHUB_TOKEN` permissions declared by the release workflow.

## Version contract

A release tag must use one of these forms:

```text
vMAJOR.MINOR.PATCH
vMAJOR.MINOR.PATCH-prerelease
```

`Icod.Tar.csproj` currently declares `Version`, `AssemblyVersion`, and `PackageVersion`. For a release, the generated package's nuspec version must equal the tag version. `SelectReleasePackages.ps1` enforces that equality before any package is uploaded.

For version `1.0.2`, maintain `tar --version`, the installation example in the root README, package metadata, and release notes in sync with the project version.
