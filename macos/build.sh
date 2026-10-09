#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_FILES=("$SCRIPT_DIR/Sources/AddressResolver.swift" "$SCRIPT_DIR/Sources/VeyraMacApp.swift")
LOGO_FILE="$ROOT_DIR/website/assets/VeyraIcon.png"
BUILD_DIR="$SCRIPT_DIR/build"
APP_BUNDLE="$BUILD_DIR/Veyra.app"
TEMP_DIR="$(mktemp -d /private/tmp/veyra-mac.XXXXXX)"
SDK_PATH="$(xcrun --sdk macosx --show-sdk-path)"
HOST_ARCH="$(uname -m)"

cleanup() {
    rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

for ARCH in arm64 x86_64; do
    MACOSX_DEPLOYMENT_TARGET=13.0 swiftc \
        -parse-as-library \
        -O \
        -sdk "$SDK_PATH" \
        -module-cache-path "$BUILD_DIR/ModuleCache-$ARCH" \
        -target "$ARCH-apple-macosx13.0" \
        -framework SwiftUI \
        -framework AppKit \
        -framework WebKit \
        "${SOURCE_FILES[@]}" \
        -o "$TEMP_DIR/Veyra-$ARCH"
done

lipo -create "$TEMP_DIR/Veyra-arm64" "$TEMP_DIR/Veyra-x86_64" \
    -output "$APP_BUNDLE/Contents/MacOS/Veyra"
cp "$SCRIPT_DIR/Info.plist" "$APP_BUNDLE/Contents/Info.plist"

cp -R "$ROOT_DIR/ui" "$APP_BUNDLE/Contents/Resources/ui"
cp "$LOGO_FILE" "$APP_BUNDLE/Contents/Resources/VeyraIcon.png"
cp "$SCRIPT_DIR/Assets/VeyraIcon.icns" "$APP_BUNDLE/Contents/Resources/VeyraIcon.icns"

swiftc \
    -module-cache-path "$BUILD_DIR/ModuleCache-x86_64" \
    "$SCRIPT_DIR/Sources/AddressResolver.swift" \
    "$SCRIPT_DIR/Tests/AddressResolverSmoke.swift" \
    -o "$TEMP_DIR/AddressResolverSmoke"
"$TEMP_DIR/AddressResolverSmoke"

swiftc \
    -parse-as-library \
    -sdk "$SDK_PATH" \
    -target "$HOST_ARCH-apple-macosx13.0" \
    -module-cache-path "$BUILD_DIR/ModuleCache-$HOST_ARCH" \
    -framework AppKit \
    -framework WebKit \
    "$SCRIPT_DIR/Tests/WebKitSmoke.swift" \
    -o "$TEMP_DIR/WebKitSmoke"
"$TEMP_DIR/WebKitSmoke"

swiftc \
    -parse-as-library \
    -sdk "$SDK_PATH" \
    -target "$HOST_ARCH-apple-macosx13.0" \
    -module-cache-path "$BUILD_DIR/ModuleCache-$HOST_ARCH" \
    -framework AppKit \
    -framework WebKit \
    "$SCRIPT_DIR/Tests/ShellSmoke.swift" \
    -o "$TEMP_DIR/ShellSmoke"
"$TEMP_DIR/ShellSmoke" "$APP_BUNDLE/Contents/Resources/ui/index.html" "$ROOT_DIR/tests/shell-smoke.js"

echo "Built universal macOS app: $APP_BUNDLE"
