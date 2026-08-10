#!/usr/bin/env bash
set -euo pipefail

linux_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
repo_root=$(cd "$linux_root/.." && pwd)
appimage_root="$linux_root/appimage"
build_root=${BUILD_ROOT:-"$repo_root/build/appimage"}
appdir="$build_root/AppDir"
tools_dir="$build_root/tools"
dist_dir=${DIST_DIR:-"$linux_root/dist"}
version=${VERSION:-0.0.2}
output_name=iPad-Tablet-Linux-x86_64.AppImage

for command_name in cmake ninja qmake6 python3 dotnet curl; do
  command -v "$command_name" >/dev/null || { echo "Missing build command: $command_name" >&2; exit 2; }
done

rm -rf "$build_root/AppDir" "$build_root/gui" "$build_root/pyinstaller"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/scalable/apps" "$appdir/usr/share/ipad-tablet/backend" \
  "$appdir/usr/share/ipad-tablet/install" "$appdir/usr/share/ipad-tablet/otd" \
  "$tools_dir" "$dist_dir"

python3 -m venv "$build_root/venv"
"$build_root/venv/bin/python" -m pip install --upgrade pip
"$build_root/venv/bin/pip" install "$linux_root" "pyinstaller>=6.10,<7"
"$build_root/venv/bin/python" -m PyInstaller --noconfirm --clean --onefile \
  --name ipad-tablet-backend --paths "$linux_root/src" \
  --distpath "$build_root/pyinstaller/dist" --workpath "$build_root/pyinstaller/work" \
  --specpath "$build_root/pyinstaller" "$appimage_root/backend_entry.py"

cmake -S "$linux_root/gui" -B "$build_root/gui" -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build "$build_root/gui" --parallel
dotnet build "$linux_root/opentabletdriver/Plugin/IPadPencilHub.csproj" -c Release

install -m0755 "$build_root/gui/ipad-tablet-backend-gui" "$appdir/usr/bin/"
install -m0755 "$build_root/pyinstaller/dist/ipad-tablet-backend" \
  "$appdir/usr/share/ipad-tablet/backend/"
install -m0755 "$appimage_root/ipad-tablet-install-helper" \
  "$appdir/usr/share/ipad-tablet/install/"
install -m0644 "$linux_root/opentabletdriver/Plugin/bin/Release/net8.0/IPadPencilHub.dll" \
  "$appdir/usr/share/ipad-tablet/otd/"
install -m0644 "$linux_root/opentabletdriver/Configurations/Apple-iPad-Pro.json" \
  "$appdir/usr/share/ipad-tablet/otd/"
install -m0644 "$appimage_root/dev.david.ipad-tablet-backend.desktop" \
  "$appdir/usr/share/applications/"
install -m0644 "$appimage_root/dev.david.ipad-tablet-backend.svg" \
  "$appdir/usr/share/icons/hicolor/scalable/apps/"

linuxdeploy="$tools_dir/linuxdeploy-x86_64.AppImage"
qt_plugin="$tools_dir/linuxdeploy-plugin-qt-x86_64.AppImage"
if [[ ! -x "$linuxdeploy" ]]; then
  curl --fail --location --retry 3 \
    https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage \
    --output "$linuxdeploy"
  chmod +x "$linuxdeploy"
fi
if [[ ! -x "$qt_plugin" ]]; then
  curl --fail --location --retry 3 \
    https://github.com/linuxdeploy/linuxdeploy-plugin-qt/releases/download/continuous/linuxdeploy-plugin-qt-x86_64.AppImage \
    --output "$qt_plugin"
  chmod +x "$qt_plugin"
fi

export APPIMAGE_EXTRACT_AND_RUN=1
# linuxdeploy's bundled strip can lag behind distributions that emit newer ELF
# sections (for example .relr.dyn). Stripping is optional for an AppImage and
# disabling it keeps local builds working without reducing compatibility.
export NO_STRIP=1
export QMAKE=${QMAKE:-$(command -v qmake6)}
export PATH="$tools_dir:$PATH"
export VERSION="$version"
export OUTPUT="$dist_dir/$output_name"
"$linuxdeploy" --appdir "$appdir" \
  --executable "$appdir/usr/bin/ipad-tablet-backend-gui" \
  --desktop-file "$appdir/usr/share/applications/dev.david.ipad-tablet-backend.desktop" \
  --icon-file "$appdir/usr/share/icons/hicolor/scalable/apps/dev.david.ipad-tablet-backend.svg" \
  --custom-apprun "$appimage_root/AppRun" --plugin qt --output appimage

(cd "$dist_dir" && sha256sum "$output_name" > "$output_name.sha256")
echo "Ready: $OUTPUT"
