#!/bin/bash
# Instala el panel en modo desarrollo (Mac): enlaza esta carpeta en las extensiones de Adobe
# y habilita extensiones sin firmar (PlayerDebugMode). Para desinstalar: ./instalar-dev.sh --quitar
DIR="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/Library/Application Support/Adobe/CEP/extensions/com.betoviedo.organizadorfootage"

if [ "$1" = "--quitar" ]; then
  rm -f "$DEST"
  echo "Panel quitado. (PlayerDebugMode sigue activo; para apagarlo: defaults delete com.adobe.CSXS.12 PlayerDebugMode)"
  exit 0
fi

mkdir -p "$(dirname "$DEST")"
rm -rf "$DEST"
ln -s "$DIR" "$DEST"
for v in 8 9 10 11 12 13; do defaults write "com.adobe.CSXS.$v" PlayerDebugMode 1; done
echo "Listo. Reiniciá Premiere y abrí Ventana → Extensiones → Organizador de Footage."
