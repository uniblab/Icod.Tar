#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ]; then
    printf 'Usage: %s <artifact-directory> <Debug|Staging|Release>\n' "$0" >&2
    exit 1
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
cd "$repository_root"

pwsh -NoLogo -NoProfile -File ./packaging/VerifyPackageArtifact.ps1 \
    -ArtifactDirectory "$1" \
    -Configuration "$2" \
    -AllowNoPackages
