# TAR(1)

![Icod.DiffUtils](https://raw.githubusercontent.com/uniblab/Icod.Tar/main/Icod.Tar.banner.png)

[![PR Staging build](https://github.com/uniblab/Icod.Tar/actions/workflows/pull-request.yaml/badge.svg?event=pull_request)](https://github.com/uniblab/Icod.Tar/actions/workflows/pull-request.yaml)
[![Main Release validation](https://github.com/uniblab/Icod.Tar/actions/workflows/main.yaml/badge.svg?branch=main)](https://github.com/uniblab/Icod.Tar/actions/workflows/main.yaml)

## NAME

**tar** — create, list, extract, and manipulate tar archives

## SYNOPSIS

```text
tar [OPTION...] [FILE]...
```

## DESCRIPTION

`Icod.Tar` is a managed .NET implementation of GNU `tar(1)`, currently modeled on GNU tar 1.35.

The command creates and reads tar archives using the .NET `System.Formats.Tar` record codec while retaining GNU-compatible command policy in the `Icod.Tar` archive engine. It supports archive creation, extraction, listing, append, update, member deletion, archive concatenation, and comparison with the local filesystem.

The implementation supports GNU, ustar, and pax output formats; gzip compression in-process; bzip2, xz, zstd, and custom external compression programs; recursive filesystem traversal; exclusions; hard links; symbolic links; GNU/PAX sparse 0.1 files; metadata restoration; overwrite policies; and bounded archive/extraction resource controls.

Neutral cross-platform infrastructure comes from `Icod.CommandFramework`, including filesystem traversal, filesystem mutation, transactional replacement, process launching, and secure temporary workspaces. `Icod.Tar` has no dependency on `Icod.CoreUtils.Shared`.

## RELEASE 1.0.2

Version `1.0.2` is a maintenance release following normalization of the repository build, validation, packaging, and release workflows to the shared `uniblab/.github` C#/.NET pattern. The release also refreshes the repository and package documentation. No change to Tar command behavior is intended relative to `1.0.1`.

## INSTALLATION AND DISTRIBUTION

Install the .NET tool from NuGet.org:

```text
dotnet tool install --global Icod.Tar --version 1.0.2
```

The installed command is `tar`. If the host already provides a native `tar`, normal `PATH` ordering determines which command is selected.

Runtime-specific ZIP archives are also produced for Windows, Linux, and macOS on x64 and ARM64. The default ZIPs are framework-dependent and require the .NET 10 runtime. Each archive contains `tar` (or `tar.exe` on Windows), `README.md`, and `LICENSE`.

The repository root `README.md` is also the NuGet package README and is embedded at the package root. The same README is included in runtime-specific release archives.

See [`packaging/README.md`](packaging/README.md) for distribution validation, release mechanics, and release prerequisites.

## RUNTIME AND DEPENDENCIES

`Icod.Tar` targets .NET 10 and is built with C# 13. The production project references `Icod.CommandFramework` 1.1.0 for shared command, filesystem, process, temporary-workspace, and transactional-replacement infrastructure.

The implementation does not depend on `Icod.CoreUtils.Shared`, a native `tar` executable, or an external archive library for its core tar record codec. External executables are required only when the corresponding external compression modes are selected.

## OPERATION MODES

Exactly one archive operation is required, except for `--help` and `--version`.

```text
-c, --create
    Create a new archive.

-x, --extract, --get
    Extract selected archive members.

-t, --list
    List selected archive members.

-r, --append
    Append files to an existing uncompressed archive.

-u, --update
    Append files that are newer than the corresponding archived members.

--delete
    Delete selected members from an existing uncompressed archive.

-A, --concatenate, --catenate
    Append the members of one or more tar archives to an existing archive.

-d, --compare, --diff
    Compare selected archive members with the filesystem.
```

## ARCHIVE CONTROL

```text
-f, --file=ARCHIVE
    Use ARCHIVE. A value of - denotes standard input or standard output for
    operations that support streaming.

-C, --directory=DIR
    Resolve following operands relative to DIR.

--format=gnu|ustar|pax|posix
    Select the output archive format. posix is an alias for pax.

-v, --verbose
    Report processed member names. Listing mode displays a long-form entry
    description when verbose output is requested.
```

Old-style option words such as `cvf archive.tar ...` are accepted for the implemented operation and value-taking options.

## COMPRESSION

```text
-z, --gzip, --ungzip
    Read or write gzip-compressed archives.

-j, --bzip2
    Read or write bzip2-compressed archives through an external compressor.

-J, --xz
    Read or write xz-compressed archives through an external compressor.

--zstd
    Read or write zstd-compressed archives through an external compressor.

--use-compress-program=PROGRAM
    Use PROGRAM as an external compression filter. The command is parsed into
    an executable and arguments and is launched directly, without a shell.

-a, --auto-compress
    Select the compressor for archive creation from the archive filename.
```

Named compressed archives are recognized on read from conventional suffixes including `.tar.gz`, `.tgz`, `.tar.bz2`, `.tbz`, `.tbz2`, `.tar.xz`, `.txz`, `.tar.zst`, and `.tzst`.

Append, update, delete, and concatenate require a named, uncompressed archive.

## SELECTION AND TRAVERSAL

```text
--exclude=PATTERN
    Exclude archive members matching PATTERN.

-h, --dereference
    Follow filesystem symbolic links while creating an archive.

--no-recursion
    Do not recurse below directory operands.

-P, --absolute-names
    Preserve leading filesystem roots when constructing archive member names.

--strip-components=N
    Remove N leading pathname components while extracting or comparing.
```

Archive member operands used for list, extract, delete, and compare select the named member and its descendants.

## EXTRACTION AND OVERWRITE POLICY

```text
-p, --preserve-permissions, --same-permissions
    Restore archived Unix mode bits where the host supports them.

--same-owner
    Restore numeric user and group ownership on non-Windows hosts when the
    underlying filesystem/provider permits it.

-m, --touch
    Do not restore archived modification times.

--keep-old-files
    Fail rather than replace an existing regular-file destination.

--skip-old-files
    Leave an existing regular-file destination unchanged.

--overwrite
    Replace an existing ordinary file according to the archive engine's safe
    replacement rules.
```

Extraction is treated as a trust boundary. Rooted archive names, `..` traversal, platform-root-like pathnames, escaping symbolic-link targets, unsafe hard-link targets, pathname-indirection parents, and case-folding collisions on Windows are rejected. Special device and FIFO members are not materialized.

Regular-file extraction and archive rewrites use the transactional replacement facilities from `Icod.CommandFramework` so destination identity is revalidated at publication time.

## SPARSE FILES

```text
-S, --sparse
    When creating an archive, detect sufficiently sparse regular files and
    encode them with GNU sparse 0.1 metadata in a pax archive.

--sparse-version=VERSION
    Select the sparse metadata version. This implementation currently accepts
    0.1 for sparse creation.
```

Sparse maps are validated before extraction. Invalid, overlapping, overflowing, or out-of-range sparse extents are rejected rather than published.

## RESOURCE LIMITS

`Icod.Tar` adds explicit safety limits for archive processing:

```text
--max-entries=N
    Maximum number of archive or traversal entries. Default: 1,000,000.

--max-extract-bytes=N
    Maximum total logical bytes extracted. Default: 1 TiB.

--max-archive-bytes=N
    Maximum archive or decompressed archive bytes. Default: 1 TiB.
```

Size values accept byte counts and the implemented decimal or binary suffixes such as `K`, `KiB`, `KB`, `M`, `MiB`, `MB`, `G`, `GiB`, `GB`, `T`, `TiB`, and `TB`.

## BUILDING AND CONTINUOUS INTEGRATION

A .NET 10 SDK is required to build the repository.

On Windows:

```text
build.cmd
```

On Unix-like hosts:

```text
./build.sh
```

With no argument, both wrappers use the `Debug` configuration and run the canonical sequence:

```text
clean → restore → build → test → pack → validate
```

Individual stages `clean`, `restore`, `build`, `test`, `pack`, and `validate` may also be requested.

Pull requests use the `Staging` configuration on `windows-latest`, `ubuntu-latest`, and `macos-latest`; Linux additionally packs and verifies the exact NuGet artifact. Pushes to `main` use `Release` distribution validation across Windows, Linux, and macOS on both x64 and ARM64. Main-branch validation also runs the Tar-specific product smoke, including standalone `tar --version`, local installation of the generated .NET tool package, and sample archive creation/listing.

A `v<semver>` tag on a commit contained in the default branch starts the `Release` publication workflow. The tag version must match the actual NuGet package version. Successful tagged releases publish the `Icod.Tar` NuGet package, create the six RID archives, publish configured package registries, generate SHA-256 checksums, and create a GitHub Release after all applicable jobs succeed.

## EXIT STATUS

```text
0    Operation completed successfully, or no difference was found by compare.
1    Compare found one or more differences.
2    Usage, archive, filesystem, compression, or other controlled operation error.
130  Operation was cancelled through the supplied cancellation token.
```

## PLATFORM NOTES

The project targets .NET 10 and is intended to run on Windows, Linux, and macOS.

Unix ownership and mode restoration are performed only where those semantics are representable. Symbolic-link and hard-link creation use the cross-platform mutation contracts supplied by `Icod.CommandFramework`. GNU/PAX sparse archive content is portable, although the degree to which the extracted file remains physically sparse depends on the host filesystem and staging behavior.

Gzip support is built in. bzip2, xz, zstd, and custom compressor modes require the corresponding external executable to be available when used.

## VERSIONING

The repository version is currently declared by the `Version`, `AssemblyVersion`, and `PackageVersion` properties in `Icod.Tar.csproj`. For a tagged release, the `v<semver>` tag and the generated package's nuspec version must agree; package selection rejects nonmatching versions rather than publishing them accidentally.

`tar --version`, assembly/package metadata, the documented installation command, and release notes are maintained together for each release.

## AUTHORS

Inspired by original work from John Gilmore, who originally wrote GNU `tar`; Jay Fenlason and Joy Kendall, who wrote the early GNU enhancements; and Thomas Bushnell, n/BSG, François Pinard, Paul Eggert, Sergey Poznyakoff, and the many contributors whose work developed and maintained GNU tar.

Migrated to .Net by Timothy J. Bruce <uniblab@hotmail.com>.

## COPYRIGHT

Copyright (c) 2026 Timothy J. Bruce

The repository `LICENSE` contains the applicable GPL licensing terms. Built executable output also carries the license as `tar.LICENSE.txt`, while NuGet and release ZIP distributions include the repository license material documented by the packaging workflow.

## SEE ALSO

`tar(1)`, `gzip(1)`, `bzip2(1)`, `xz(1)`, `zstd(1)`
