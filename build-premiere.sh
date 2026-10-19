#!/bin/bash
# Empaqueta el panel de Premiere (carpeta premiere/) en v2/dist:
#   - OrganizadorFootage-Premiere.zxp   (firmado; se instala con ZXP Installer, UPIA o Adobe Exchange)
#   - OrganizadorFootage-Premiere.zip   (el mismo panel firmado, para descomprimir en la carpeta de extensiones;
#                                         también se copia a windows/PanelPremiere.zip para la app de Windows)
#
# Firma con un certificado propio (gratis). Tiene que ser SIEMPRE el mismo para que las
# actualizaciones reemplacen al panel instalado: el .p12 queda en certificado/ (fuera del repo)
# y su contraseña en el Llavero de macOS. Hacé backup de los dos.
set -euo pipefail
cd "$(dirname "$0")"

DIST="dist"
NOMBRE="OrganizadorFootage-Premiere"
ID="com.betoviedo.organizadorfootage"
SIGNER="herramientas/ZXPSignCmd"
CERT="certificado/organizador-premiere.p12"
LLAVERO="organizador-premiere-zxp"
TSA="http://timestamp.digicert.com"
VERSION=$(sed -n 's/.*ExtensionBundleVersion="\([^"]*\)".*/\1/p' premiere/CSXS/manifest.xml)

# ---------- Herramienta de firma de Adobe ----------
if [ ! -x "$SIGNER" ]; then
  echo "▸ Descargando ZXPSignCmd (Adobe)…"
  mkdir -p herramientas
  curl -fsSL -o "$SIGNER" https://raw.githubusercontent.com/Adobe-CEP/CEP-Resources/master/ZXPSignCMD/4.1.3/macOS/ZXPSignCmd
  chmod +x "$SIGNER"
fi

# ---------- Certificado (se crea una sola vez) ----------
if ! CLAVE=$(security find-generic-password -s "$LLAVERO" -a "$USER" -w 2>/dev/null); then
  [ -f "$CERT" ] && { echo "❌ Existe $CERT pero falta su contraseña en el Llavero ($LLAVERO)."; exit 1; }
  echo "▸ Creando certificado propio (10 años)…"
  CLAVE=$(openssl rand -hex 24)
  security add-generic-password -s "$LLAVERO" -a "$USER" -w "$CLAVE" -l "Organizador de Footage - certificado ZXP"
fi
if [ ! -f "$CERT" ]; then
  mkdir -p certificado
  "$SIGNER" -selfSignedCert AR BA betoviedo "Organizador de Footage" "$CLAVE" "$CERT" -validityDays 3650 >/dev/null
fi

# ---------- Copia limpia del panel ----------
# Sin enlaces simbólicos ni .DS_Store: si no, el panel puede quedar en blanco al instalarlo (problema conocido de Adobe)
echo "▸ Preparando panel v${VERSION}…"
mkdir -p "$DIST"
TMP="$DIST/.premiere"
rm -rf "$TMP"; mkdir -p "$TMP"
rsync -a --copy-links --exclude '.DS_Store' --exclude '.debug' --exclude 'instalar-dev.*' premiere/ "$TMP/$ID/"
if [ -n "$(find "$TMP" -type l)" ]; then echo "❌ Quedaron enlaces simbólicos en el panel."; exit 1; fi

# ---------- Firma ----------
echo "▸ Firmando…"
rm -f "$DIST/$NOMBRE.zxp" "$DIST/$NOMBRE.zip"
"$SIGNER" -sign "$TMP/$ID" "$DIST/$NOMBRE.zxp" "$CERT" "$CLAVE" -tsa "$TSA" >/dev/null
"$SIGNER" -verify "$DIST/$NOMBRE.zxp" >/dev/null

# ---------- ZIP para instalar descomprimiendo ----------
# El .zxp es un ZIP con la firma adentro: se extrae en una carpeta con el ID del panel
rm -rf "$TMP"; mkdir -p "$TMP/$ID"
ditto -x -k "$DIST/$NOMBRE.zxp" "$TMP/$ID"
(cd "$TMP" && ditto -c -k --norsrc --noextattr --keepParent "$ID" "../$NOMBRE.zip")
rm -rf "$TMP"

# La app de Windows se compila en GitHub (sin el certificado): lleva adentro este ZIP ya firmado.
# Cuando cambia el panel, hay que volver a correr este script y subir windows/PanelPremiere.zip.
cp "$DIST/$NOMBRE.zip" windows/PanelPremiere.zip

# Si existe web/ (solo en la Mac de @betoviedo), deja los archivos listos para subir al servidor
if [ -d web ]; then cp "$DIST/$NOMBRE.zxp" "$DIST/$NOMBRE.zip" web/; fi

echo "✅ Panel de Premiere v${VERSION} listo en v2/$DIST"
ls -1 "$DIST" | grep Premiere
