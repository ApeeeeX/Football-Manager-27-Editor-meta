#!/usr/bin/env bash
# Fails when product code changed without an update to the newest releases/<version>.md.
# Usage: scripts/check-release-log.sh <base> [head]
set -euo pipefail

base="${1:?usage: scripts/check-release-log.sh <base> [head]}"
head="${2:-HEAD}"

if [[ "$base" =~ ^0+$ ]] || ! git rev-parse -q --verify "$base^{commit}" > /dev/null; then
    echo "Release log check skipped: no base commit to compare."
    exit 0
fi

from="$(git merge-base "$base" "$head")"
changed="$(git diff --name-only "$from" "$head")"

if ! grep -Eq '^(src|offsets)/' <<< "$changed"; then
    echo "Release log check passed: no product code changed."
    exit 0
fi

latest="$(git ls-tree --name-only "$head" releases/ | sed -nE 's#^releases/([0-9]+\.[0-9]+\.[0-9]+)\.md$#\1#p' |
    sort -t. -k1,1n -k2,2n -k3,3n | tail -1)"
if [ -z "$latest" ]; then
    echo "Release log check failed: releases/ has no <version>.md file." >&2
    exit 1
fi

if ! grep -Fxq "releases/$latest.md" <<< "$changed"; then
    echo "Release log check failed: src/ or offsets/ changed, but releases/$latest.md did not." >&2
    exit 1
fi
echo "Release log check passed: releases/$latest.md updated."
