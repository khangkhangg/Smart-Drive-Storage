#!/usr/bin/env bash
# Builds Miu Smart Storage for Linux: a .tar.gz, a .deb and (when appimagetool is available) an AppImage.
# Usage: build/package-linux.sh <version> [output-dir]
set -euo pipefail
VERSION="${1:?version, e.g. 0.8.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mkdir -p "${2:-$ROOT/artifacts}" && cd "${2:-$ROOT/artifacts}" && pwd)"
RID=linux-x64
NAME=miu-smart-storage
WORK="$OUT/work-$RID"
rm -rf "$WORK" && mkdir -p "$WORK"

echo "== publish $RID $VERSION"
dotnet publish "$ROOT/src/Miu.App" -c Release -r "$RID" --self-contained \
  -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -o "$WORK/publish"
chmod -R a+rX,u+w "$WORK/publish" && chmod 755 "$WORK/publish/Miu"

ICON="$ROOT/docs/brand/miu-logo-256.png"
DESKTOP="$ROOT/build/$NAME.desktop"

echo "== tar.gz"
TAR_DIR="$WORK/$NAME-$VERSION"
mkdir -p "$TAR_DIR"
cp -a "$WORK/publish/." "$TAR_DIR/"
cp "$ICON" "$TAR_DIR/$NAME.png"
sed "s|^Exec=.*|Exec=$NAME-path/Miu %F|" "$DESKTOP" > "$TAR_DIR/$NAME.desktop"
cat > "$TAR_DIR/README.txt" <<TXT
Miu Smart Storage $VERSION for Linux (x64)

Run ./Miu to start. To add it to your app menu, edit $NAME.desktop
(replace $NAME-path with this folder) and copy it to ~/.local/share/applications/.
Made with love by Khang - vietnamcos.com - duykhang.com
TXT
tar -C "$WORK" -czf "$OUT/$NAME-$VERSION-linux-x64.tar.gz" "$NAME-$VERSION"

echo "== deb"
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/$NAME" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/256x256/apps"
cp -a "$WORK/publish/." "$DEB/opt/$NAME/"
ln -s "/opt/$NAME/Miu" "$DEB/usr/bin/miu"
cp "$DESKTOP" "$DEB/usr/share/applications/$NAME.desktop"
cp "$ICON" "$DEB/usr/share/icons/hicolor/256x256/apps/$NAME.png"
SIZE_KB=$(du -sk "$DEB/opt" | cut -f1)
DEB_VERSION="${VERSION%%+*}"; DEB_VERSION="${DEB_VERSION//-/\~}" # 0.8.0-beta.1 -> 0.8.0~beta.1 sorts before 0.8.0
cat > "$DEB/DEBIAN/control" <<CTRL
Package: $NAME
Version: $DEB_VERSION
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $SIZE_KB
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66, libssl3 | libssl3t64 | libssl1.1
Maintainer: Khang <https://duykhang.com>
Homepage: https://github.com/khangkhangg/Smart-Drive-Storage
Description: See what fills your drive and clean it up safely
 Miu Smart Storage shows what takes up space, explains every item,
 and moves only what you choose to the Trash, with undo. It also finds
 developer junk (node_modules, build output, Docker) and backs up to the cloud.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/${NAME}_${DEB_VERSION}_amd64.deb"

echo "== AppImage"
APPDIR="$WORK/AppDir"
mkdir -p "$APPDIR/usr/lib/$NAME"
cp -a "$WORK/publish/." "$APPDIR/usr/lib/$NAME/"
cp "$ICON" "$APPDIR/$NAME.png"
sed "s|^Exec=.*|Exec=AppRun %F|" "$DESKTOP" > "$APPDIR/$NAME.desktop"
cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/miu-smart-storage/Miu" "$@"
RUN
chmod +x "$APPDIR/AppRun"
TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [ -n "$TOOL" ]; then
  ARCH=x86_64 "$TOOL" --no-appstream "$APPDIR" "$OUT/Miu-Smart-Storage-$VERSION-x86_64.AppImage"
else
  echo "appimagetool not found: AppImage skipped (set APPIMAGETOOL to build it)"
fi

rm -rf "$WORK"
echo "== done"; ls -la "$OUT"
