#!/usr/bin/env bash
# Builds "AnyPS5 Library.app" (self-contained, Apple silicon) next to this script.
# Uses the project-local .NET SDK in ./.dotnet and builds pkg-to-anyps5 from ./pkg-to-anyps5 with cargo.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/AnyPS5 Library.app"
PUBLISH="$ROOT/.build/publish"
export DOTNET_ROOT="$ROOT/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "==> pkg-to-anyps5 (cargo build --release)"
(cd "$ROOT/pkg-to-anyps5" && cargo build --release --quiet)

echo "==> dotnet publish (osx-arm64, self-contained)"
rm -rf "$PUBLISH"
"$DOTNET_ROOT/dotnet" publish "$ROOT/src/AnyPS5Library/AnyPS5Library.csproj" -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=false -o "$PUBLISH" --nologo -v quiet

echo "==> assembling bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"
cp "$ROOT/pkg-to-anyps5/target/release/pkg-to-anyps5" "$APP/Contents/MacOS/pkg-to-anyps5"
cp "$ROOT/pkg-to-anyps5/LICENSE" "$APP/Contents/Resources/pkg-to-anyps5-LICENSE"
cp "$ROOT/upstream/LICENSE" "$APP/Contents/Resources/PS5PKGTool-LICENSE"
cp "$ROOT/upstream/THIRD_PARTY_NOTICES.md" "$APP/Contents/Resources/PS5PKGTool-THIRD_PARTY_NOTICES.md"
# Windows-only payloads have no use on macOS.
rm -rf "$APP/Contents/MacOS/LibProsperoPkg12" "$APP/Contents/MacOS/"*.pdb

ICONSET="$ROOT/.build/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for size in 16 32 64 128 256; do
  sips -z $size $size "$ROOT/src/AnyPS5Library/Assets/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  if [ $double -le 256 ]; then
    sips -z $double $double "$ROOT/src/AnyPS5Library/Assets/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  fi
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AnyPS5 Library</string>
  <key>CFBundleDisplayName</key><string>AnyPS5 Library</string>
  <key>CFBundleIdentifier</key><string>local.anyps5library</string>
  <key>CFBundleVersion</key><string>1.0.0</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleExecutable</key><string>AnyPS5Library</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

echo "==> ad-hoc signing"
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || codesign --force --deep --sign - "$APP"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
echo "Built: $APP"
