#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <publish-dir> <output-zip> <version>" >&2
  exit 1
fi

publish_dir="$1"
output_zip="$2"
version="$3"
app_name="LiveHelper"
bundle="$app_name.app"
contents="$bundle/Contents"
macos="$contents/MacOS"
resources="$contents/Resources"

rm -rf "$bundle"
mkdir -p "$macos" "$resources"

cp "$publish_dir/LiveHelper" "$macos/$app_name"
chmod 755 "$macos/$app_name"

native_libraries=(
  libAvaloniaNative.dylib
  libHarfBuzzSharp.dylib
  libSkiaSharp.dylib
  libonigwrap.dylib
)
for library in "${native_libraries[@]}"; do
  if [[ ! -f "$publish_dir/$library" ]]; then
    echo "macOS publish output is missing required native library: $library" >&2
    exit 1
  fi
  cp "$publish_dir/$library" "$macos/$library"
  chmod 755 "$macos/$library"
done

for tool in yt-dlp ffmpeg; do
  if [[ ! -x "$publish_dir/tools/$tool" ]]; then
    echo "macOS publish output is missing executable recording tool: tools/$tool" >&2
    exit 1
  fi
done
mkdir -p "$macos/tools"
ditto "$publish_dir/tools" "$macos/tools"

cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleDisplayName</key>
  <string>LiveHelper</string>
  <key>CFBundleExecutable</key>
  <string>LiveHelper</string>
  <key>CFBundleIconFile</key>
  <string>livehelper.icns</string>
  <key>CFBundleIdentifier</key>
  <string>io.github.hinatayoki.livehelper</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>LiveHelper</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSSupportsAutomaticTermination</key>
  <false/>
  <key>NSSupportsSuddenTermination</key>
  <false/>
</dict>
</plist>
PLIST

icon_work=$(mktemp -d)
cleanup() {
  rm -rf "$icon_work"
  if [[ "${KEEP_BUNDLE:-0}" != "1" ]]; then
    rm -rf "$bundle"
  fi
}
trap cleanup EXIT

node - "$icon_work/livehelper.icns" <<'JS'
const fs = require("fs");

const data = fs.readFileSync("src/LiveHelper.Desktop/Assets/livehelper.ico");
const count = data.readUInt16LE(4);
const images = new Map();

for (let index = 0; index < count; index++) {
  const offset = 6 + index * 16;
  const size = data[offset] || 256;
  const length = data.readUInt32LE(offset + 8);
  const position = data.readUInt32LE(offset + 12);
  images.set(size, data.subarray(position, position + length));
}

const required = [16, 32, 128, 256];
const missing = required.filter((size) => !images.has(size));
if (missing.length > 0) {
  throw new Error(`icon is missing PNG sizes: ${missing.join(", ")}`);
}

if (!images.has(64)) {
  images.set(64, images.get(128));
}

const chunks = [
  [16, "icp4"],
  [32, "icp5"],
  [64, "icp6"],
  [128, "ic07"],
  [256, "ic08"],
].map(([size, kind]) => {
  const payload = images.get(size);
  const header = Buffer.alloc(8);
  header.write(kind, 0, "ascii");
  header.writeUInt32BE(payload.length + 8, 4);
  return Buffer.concat([header, payload]);
});

const body = Buffer.concat(chunks);
const header = Buffer.alloc(8);
header.write("icns", 0, "ascii");
header.writeUInt32BE(8 + body.length, 4);
fs.writeFileSync(process.argv[2], Buffer.concat([header, body]));
JS

cp "$icon_work/livehelper.icns" "$resources/livehelper.icns"

signing_values=(
  "${MACOS_CERTIFICATE:-}"
  "${MACOS_CERTIFICATE_PASSWORD:-}"
  "${MACOS_SIGNING_IDENTITY:-}"
)
notary_values=(
  "${MACOS_NOTARY_APPLE_ID:-}"
  "${MACOS_NOTARY_PASSWORD:-}"
  "${MACOS_NOTARY_TEAM_ID:-}"
)

if printf '%s\n' "${signing_values[@]}" | grep -q .; then
  for value in "${signing_values[@]}"; do
    if [[ -z "$value" ]]; then
      echo "macOS signing requires MACOS_CERTIFICATE, MACOS_CERTIFICATE_PASSWORD and MACOS_SIGNING_IDENTITY" >&2
      exit 1
    fi
  done
fi

if printf '%s\n' "${notary_values[@]}" | grep -q .; then
  for value in "${notary_values[@]}"; do
    if [[ -z "$value" ]]; then
      echo "macOS notarization requires MACOS_NOTARY_APPLE_ID, MACOS_NOTARY_PASSWORD and MACOS_NOTARY_TEAM_ID" >&2
      exit 1
    fi
  done
  if [[ -z "${MACOS_CERTIFICATE:-}" ]]; then
    echo "macOS notarization requires a Developer ID signing certificate" >&2
    exit 1
  fi
fi

if [[ -n "${MACOS_CERTIFICATE:-}" ]]; then
  keychain="$RUNNER_TEMP/livehelper-signing.keychain-db"
  keychain_password=$(openssl rand -base64 32)
  certificate="$RUNNER_TEMP/livehelper-signing.p12"
  printf '%s' "$MACOS_CERTIFICATE" | openssl base64 -d -A -out "$certificate"

  security create-keychain -p "$keychain_password" "$keychain"
  security set-keychain-settings -lut 21600 "$keychain"
  security unlock-keychain -p "$keychain_password" "$keychain"
  security import "$certificate" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" \
    -T /usr/bin/codesign
  security set-key-partition-list -S apple-tool:,apple: -s -k "$keychain_password" "$keychain" >/dev/null
  security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')

  codesign --force --deep --options runtime --timestamp \
    --sign "$MACOS_SIGNING_IDENTITY" "$bundle"
  codesign --verify --deep --strict --verbose=2 "$bundle"
fi

if [[ -n "${MACOS_NOTARY_APPLE_ID:-}" ]]; then
  notarize_zip="$RUNNER_TEMP/livehelper-notarize.zip"
  ditto -c -k --keepParent "$bundle" "$notarize_zip"
  xcrun notarytool submit "$notarize_zip" \
    --apple-id "$MACOS_NOTARY_APPLE_ID" \
    --password "$MACOS_NOTARY_PASSWORD" \
    --team-id "$MACOS_NOTARY_TEAM_ID" \
    --wait
  xcrun stapler staple "$bundle"
  xcrun stapler validate "$bundle"
fi

mkdir -p "$(dirname "$output_zip")"
ditto -c -k --keepParent "$bundle" "$output_zip"
echo "Created $output_zip"
