#!/bin/bash
# Compila todo en v2/dist:
#   - Organizador de Footage.app   (app nativa, no necesita ExifTool)
#   - Organizar Footage.workflow   (Acción Rápida de Finder, usa organizar.sh)
#   - ZIPs listos para subir a la web
set -euo pipefail
cd "$(dirname "$0")"

VERSION="2.1.1"
DIST="dist"
APP="$DIST/Organizador de Footage.app"
WF="$DIST/Organizar Footage.workflow"
rm -rf "$DIST"; mkdir -p "$DIST"

# ---------- App ----------
echo "▸ Compilando app (Apple Silicon + Intel)…"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$DIST/.build"
for arch in arm64 x86_64; do
  swiftc -parse-as-library -O -swift-version 5 \
    -target "$arch-apple-macos13.0" \
    App/OrganizadorFootage.swift -o "$DIST/.build/app-$arch"
done
lipo -create "$DIST/.build/app-arm64" "$DIST/.build/app-x86_64" -output "$APP/Contents/MacOS/OrganizadorFootage"
rm -rf "$DIST/.build"
cp App/AppIcon.icns "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Organizador de Footage</string>
  <key>CFBundleDisplayName</key><string>Organizador de Footage</string>
  <key>CFBundleIdentifier</key><string>com.betoviedo.organizadorfootage</string>
  <key>CFBundleExecutable</key><string>OrganizadorFootage</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.video</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>@betoviedo</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Carpeta</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>public.folder</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST
codesign --force --deep --sign - "$APP"

# ---------- Acción Rápida (Automator) ----------
echo "▸ Generando Acción Rápida…"
cp -R ../OrganizadorFootageVideo.workflow "$WF"
/usr/bin/python3 - "$WF" organizar.sh <<'PY'
import plistlib, sys
wf, script = sys.argv[1], sys.argv[2]
cuerpo = open(script, encoding="utf-8").read().replace("DIALOGOS=${DIALOGOS:-0}", "DIALOGOS=1", 1)

doc = f"{wf}/Contents/document.wflow"
d = plistlib.load(open(doc, "rb"))
for a in d["actions"]:
    p = a["action"]["ActionParameters"]
    if "COMMAND_STRING" in p:
        p["COMMAND_STRING"] = cuerpo
        p["shell"] = "/bin/bash"
        p["inputMethod"] = 1  # carpetas como argumentos
plistlib.dump(d, open(doc, "wb"))

info = f"{wf}/Contents/Info.plist"
i = plistlib.load(open(info, "rb"))
i["NSServices"][0]["NSMenuItem"]["default"] = "Organizar Footage"
plistlib.dump(i, open(info, "wb"))
PY

# ---------- ZIPs ----------
echo "▸ Empaquetando…"
# ZIP de la app: la app + el LEEME en el primer nivel
mkdir -p "$DIST/.zip"
cp -R "$APP" "$DIST/.zip/"
cp "App/LEEME - Cómo abrir la app.txt" "$DIST/.zip/"
ditto -c -k --norsrc --noextattr "$DIST/.zip" "$DIST/OrganizadorFootage-App.zip"
rm -rf "$DIST/.zip"
(cd "$DIST" && ditto -c -k --keepParent "Organizar Footage.workflow" "OrganizarFootage-AccionRapida.zip")
cp organizar.sh "$DIST/"
# web/ queda listo para subir tal cual al servidor
cp "$DIST/OrganizadorFootage-App.zip" web/

echo "✅ Listo en v2/$DIST"
ls -1 "$DIST"
