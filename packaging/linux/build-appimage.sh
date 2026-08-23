#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <publish-directory> <output-directory> <version> [appimagetool] [x86_64|aarch64]" >&2
  exit 2
fi

publish_dir="$(realpath "$1")"
output_dir="$(mkdir -p "$2" && realpath "$2")"
version="$3"
appimagetool="${4:-appimagetool}"
appimage_arch="${5:-x86_64}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(realpath "$script_dir/../..")"
app_dir="$(mktemp -d)"
trap 'rm -rf "$app_dir"' EXIT

mkdir -p "$app_dir/usr/bin" "$app_dir/usr/share/applications" \
  "$app_dir/usr/share/icons/hicolor/512x512/apps" "$app_dir/usr/share/pixmaps" \
  "$app_dir/usr/share/metainfo"
cp -a "$publish_dir/." "$app_dir/usr/bin/"
cp "$script_dir/AppRun" "$app_dir/AppRun"
cp "$script_dir/com.wuild.TSMDesktop.desktop" "$app_dir/com.wuild.TSMDesktop.desktop"
cp "$script_dir/com.wuild.TSMDesktop.desktop" \
  "$app_dir/usr/share/applications/com.wuild.TSMDesktop.desktop"
cp "$script_dir/com.wuild.TSMDesktop.metainfo.xml" \
  "$app_dir/usr/share/metainfo/com.wuild.TSMDesktop.appdata.xml"
cp "$project_dir/TSM/Assets/tsm-app.png" "$app_dir/com.wuild.TSMDesktop.png"
cp "$project_dir/TSM/Assets/tsm-app.png" \
  "$app_dir/usr/share/icons/hicolor/512x512/apps/com.wuild.TSMDesktop.png"
cp "$project_dir/TSM/Assets/tsm-app.png" "$app_dir/usr/share/pixmaps/com.wuild.TSMDesktop.png"
ln -s com.wuild.TSMDesktop.png "$app_dir/tsm-app.png"
chmod +x "$app_dir/AppRun" "$app_dir/usr/bin/TSM"
ln -s com.wuild.TSMDesktop.png "$app_dir/.DirIcon"
if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$app_dir/com.wuild.TSMDesktop.desktop"
fi
if command -v appstreamcli >/dev/null 2>&1; then
  appstreamcli validate --no-net \
    "$app_dir/usr/share/metainfo/com.wuild.TSMDesktop.appdata.xml"
fi
test -s "$app_dir/com.wuild.TSMDesktop.png"
test -L "$app_dir/.DirIcon"

ARCH="$appimage_arch" VERSION="$version" APPIMAGE_EXTRACT_AND_RUN=1 \
  "$appimagetool" "$app_dir" "$output_dir/TSM-${version}-${appimage_arch}.AppImage"
chmod +x "$output_dir/TSM-${version}-${appimage_arch}.AppImage"
