# Icod.Tar build and packaging workflow

This repository follows the canonical `uniblab/.github` C#/.NET build and release pattern.

## Validation ladder

| Lifecycle | Configuration | Work |
| --- | --- | --- |
| local `build.cmd` / `build.sh` | `Debug` | clean, restore, build, test, pack, exact package validation |
| pull request | `Staging` | Windows/Linux/macOS build and test; Linux also validates generated NuGet artifacts |
| default branch | `Release` | six-runner Windows/Linux/macOS x64/ARM64 distribution validation plus Tar product smoke |
| `v<semver>` tag | `Release` | package/archive production and publication |

The shared scripts discover the root solution, projects, executable outputs, package identity, and package version from the repository/MSBuild instead of deriving them from the GitHub repository name.

`Get-RepositoryMetadata.ps1` exports a repository-relative solution path so metadata produced on Linux is safe to consume on Windows and macOS jobs.

`VerifyDistribution.ps1` is the common template validation: restore, build, test, pack, and exact package verification. `VerifyTarDistribution.ps1` is the only product-specific extension. It reuses those outputs to run `tar --version`, verify/install the generated `Icod.Tar` .NET tool package from a local source, and create/list a sample tar archive.

`BuildReleaseArchive.ps1` discovers the `tar` executable through MSBuild and creates framework-dependent single-file ZIPs for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`, including repository `LICENSE` and `README.md`.

Tagged releases require the tag to match `v<semver>` and the tagged commit to be contained in the default branch. Only NuGet packages whose actual nuspec version matches the tag version are selected. NuGet.org publication uses OIDC Trusted Publishing through the GitHub `Release` environment; GitHub Packages uses `GITHUB_TOKEN`. Both package publication paths use `--skip-duplicate`.
