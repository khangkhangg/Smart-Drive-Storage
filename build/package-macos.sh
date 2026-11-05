#!/usr/bin/env bash
# Builds "Miu Smart Storage.app" for macOS (Apple silicon and Intel) as .dmg and .zip. Runs on macOS.
# The app is ad-hoc signed; notarization needs an Apple Developer ID (see docs).
# Usage: build/package-macos.sh <version> [output-dir]
set -euo pipefail
VERSION="${1:?version, e.g. 0.8.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mkdir -p "${2:-$ROOT/artifacts}" && cd "${2:-$ROOT/artifacts}" && pwd)"
APP_NAME="Miu Smart Storage"
SHORT_VERSION="${VERSION%%-*}" # CFBundleShortVersionString must be numbers only

echo "== icon"
ICONSET="$OUT/miu.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
SRC_ICON="$ROOT/docs/brand/miu-logo-512.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); [ $d -le 512 ] && sips -z $d $d "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
cp "$SRC_ICON" "$ICONSET/icon_256x256@2x.png"
iconutil -c icns "$ICONSET" -o "$OUT/miu.icns"
rm -rf "$ICONSET"

for RID in osx-arm64 osx-x64; do
  ARCH=$([ "$RID" = osx-arm64 ] && echo arm64 || echo x64)
  WORK="$OUT/work-$RID"
  rm -rf "$WORK" && mkdir -p "$WORK"
  echo "== publish $RID"
  dotnet publish "$ROOT/src/Miu.App" -c Release -r "$RID" --self-contained \
    -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -p:UseAppHost=true -o "$WORK/publish"

  APP="$WORK/$APP_NAME.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -a "$WORK/publish/." "$APP/Contents/MacOS/"
  cp "$OUT/miu.icns" "$APP/Contents/Resources/miu.icns"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.duykhang.miu</string>
  <key>CFBundleVersion</key><string>$SHORT_VERSION</string>
  <key>CFBundleShortVersionString</key><string>$SHORT_VERSION</string>
  <key>CFBundleExecutable</key><string>Miu</string>
  <key>CFBundleIconFile</key><string>miu.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHumanReadableCopyright</key><string>Made with love by Khang · vietnamcos.com · duykhang.com</string>
</dict>
</plist>
PLIST
  chmod +x "$APP/Contents/MacOS/Miu"
  # Ad-hoc signature: required for Apple silicon to run it at all.
  codesign --force --deep --sign - "$APP"

  echo "== zip + dmg ($ARCH)"
  ditto -c -k --keepParent "$APP" "$OUT/Miu-Smart-Storage-$VERSION-macos-$ARCH.zip"
  STAGE="$WORK/dmg"
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$OUT/Miu-Smart-Storage-$VERSION-macos-$ARCH.dmg" >/dev/null
  rm -rf "$WORK"
done
rm -f "$OUT/miu.icns"
echo "== done"; ls -la "$OUT"
