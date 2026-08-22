#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <item-icon-cache-directory> <output-file>" >&2
  exit 2
fi

cache_dir="$(realpath "$1")"
output_file="$2"
temporary_file="$(mktemp)"
trap 'rm -f "$temporary_file"' EXIT

while IFS= read -r -d '' metadata_file; do
  flavor="$(basename "$(dirname "$metadata_file")")"
  item_id="$(basename "$metadata_file" .json)"
  icon="$(jq -r '.icon // empty' "$metadata_file")"
  if [[ "$item_id" =~ ^[0-9]+$ && "$icon" =~ ^[a-z0-9_-]+$ ]]; then
    printf '%s\t%s\t%s\n' "$flavor" "$item_id" "$icon"
  fi
done < <(find "$cache_dir" -mindepth 2 -maxdepth 2 -type f -name '*.json' -print0) |
  sort -t $'\t' -k1,1 -k2,2n > "$temporary_file"

mkdir -p "$(dirname "$output_file")"
jq -Rnc '[inputs | split("\t") | {flavor: .[0], id: .[1], icon: .[2]}]
  | group_by(.flavor)
  | map({key: .[0].flavor, value: (map({key: .id, value: .icon}) | from_entries)})
  | from_entries' "$temporary_file" |
  brotli --stdout --quality=9 > "$output_file"

test -s "$output_file"
