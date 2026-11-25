#!/usr/bin/env bash
# Builds dist/Fm27Editor.app (a self-contained NativeAOT bundle, ad-hoc signed) and dist/Fm27Editor-mac-<arch>.dmg.
# One bundle serves every Mac game build with a profile in offsets/ (Steam and Touch).
# Usage: scripts/package-mac.sh   (RID=osx-x64 for Intel; VERSION=1.4.0 sets the version; DOTNET=path picks dotnet)
set -euo pipefail

# bash 3.2 mishandles the script's own stdin while dotnet runs; a scripted build is never interactive.
exec < /dev/null

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
version="${VERSION:-0.0.0}"
rid="${RID:-osx-arm64}"
app="$root/dist/Fm27Editor.app"
publish="$root/dist/publish-$rid"

rm -rf "$app" "$publish" "$root/dist/aot-roots.props"

# The AOT compiler crashes on a stale PDB ("Read out of bounds"), so drop the app's Release intermediates first.
rm -rf src/Fm27Editor.App/obj/Release src/Fm27Editor.App/bin/Release
mkdir -p "src/Fm27Editor.App/obj/Release/net10.0/$rid/native"

"$dotnet" publish src/Fm27Editor.App/Fm27Editor.App.csproj \
    -c Release \
    -r "$rid" \
    -p:PublishAot=true \
    -p:Version="$version" \
    -o "$publish"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

cp "$publish/Fm27Editor" "$app/Contents/MacOS/Fm27Editor"
chmod +x "$app/Contents/MacOS/Fm27Editor"

# The privileged on-demand helper is a tiny native binary; it is signed with the bundle below.
helper_arch=arm64
[ "$rid" = "osx-x64" ] && helper_arch=x86_64
clang -O2 -arch "$helper_arch" -Wall -Wextra \
    -o "$app/Contents/MacOS/Fm27EditorHelper" \
    src/Fm27Editor.Helper/fm27_helper.c
chmod +x "$app/Contents/MacOS/Fm27EditorHelper"
# The in-game plugin: the editor starts Football Manager 27 with it in DYLD_INSERT_LIBRARIES, so the Steam build
# (Hardened Runtime) is reachable without root or task_for_pid, with System Integrity Protection left on.
clang -O2 -arch "$helper_arch" -Wall -Wextra -dynamiclib -DFM27_PLUGIN \
    -o "$app/Contents/MacOS/libfm27editor_plugin.dylib" \
    src/Fm27Editor.Helper/fm27_helper.c
# Steam itself can start the game under Rosetta. An arm64-only plugin then makes dyld abort the x86_64 game at launch,
# so an empty x86_64 slice is added: the game starts, and the editor just cannot use the plugin in that process.
if [ "$helper_arch" = "arm64" ]; then
    stub=$(mktemp -d)
    printf 'void fm27editor_plugin_x86_64_stub(void) {}\n' > "$stub/stub.c"
    clang -O2 -arch x86_64 -dynamiclib -o "$stub/x86_64.dylib" "$stub/stub.c"
    lipo -create "$app/Contents/MacOS/libfm27editor_plugin.dylib" "$stub/x86_64.dylib" \
        -output "$stub/universal.dylib"
    mv "$stub/universal.dylib" "$app/Contents/MacOS/libfm27editor_plugin.dylib"
    rm -rf "$stub"
fi

# Build profiles next to the binary; packaged copies come from the publish output, the repo is the fallback.
if [ -d "$publish/offsets" ]; then
    cp -R "$publish/offsets" "$app/Contents/MacOS/offsets"
else
    cp -R "$root/offsets" "$app/Contents/MacOS/offsets"
fi

# Native dependencies of the bundled components (Avalonia, Skia, harfbuzz), signed with the bundle.
find "$publish" -maxdepth 1 -type f -name '*.dylib' -exec cp {} "$app/Contents/MacOS/" \;

for extra in LICENSE README.md; do
    [ -f "$root/$extra" ] && cp "$root/$extra" "$app/Contents/Resources/$extra"
done

icon_src="$root/src/Fm27Editor.App/Assets/icon.png"
# The rounded black tile needs Python with Pillow; without it the icon is made from the plain PNG.
if [ -f "$icon_src" ]; then
    icon_black="$root/dist/icon_black.png"
    if ! python3 -c "import PIL" 2>/dev/null; then cp "$icon_src" "$icon_black"; else
    python3 - "$icon_src" "$icon_black" <<'PY'
import sys
from PIL import Image, ImageDraw

src = Image.open(sys.argv[1]).convert("RGBA")
size = 1024
canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
mask = Image.new("L", (size, size), 0)
ImageDraw.Draw(mask).rounded_rectangle((40, 40, size - 40, size - 40), radius=210, fill=255)
bg = Image.new("RGBA", (size, size), (0, 0, 0, 255))
canvas.paste(bg, (0, 0), mask)

inner = 820
src_resized = src.resize((inner, inner), Image.Resampling.LANCZOS)
offset = (size - inner) // 2
canvas.alpha_composite(src_resized, (offset, offset))
canvas.save(sys.argv[2])
PY
    fi
    iconset="$root/dist/app.iconset"
    rm -rf "$iconset"
    mkdir -p "$iconset"
    for sz in 16 32 128 256 512; do
        sips -z "$sz" "$sz" "$icon_black" --out "$iconset/icon_${sz}x${sz}.png" >/dev/null
        dbl=$((sz * 2))
        sips -z "$dbl" "$dbl" "$icon_black" --out "$iconset/icon_${sz}x${sz}@2x.png" >/dev/null
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/app.icns"
    rm -rf "$iconset" "$icon_black"
fi

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Fm27Editor</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.fm27editor.app</string>
    <key>CFBundleName</key>
    <string>FM27 Editor</string>
    <key>CFBundleDisplayName</key>
    <string>FM27 Editor</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
rm -rf "$publish"

# Disk image: the app next to a link to /Applications.
arch=arm64
[ "$rid" = "osx-x64" ] && arch=x64
dmg="$root/dist/Fm27Editor-mac-$arch.dmg"
staging="$root/dist/dmg"
rm -rf "$staging" "$dmg"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "FM27 Editor" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
rm -rf "$staging"

echo "built $app"
echo "built $dmg"
