#!/bin/bash
# ==========================================================
#  Organizador de Footage v2 — @betoviedo
#  Separa clips en Verticales / Horizontales (y SlowMo / RealTime)
#  https://cafecito.app/beto_oviedo
# ==========================================================
#
#  Uso:
#    organizar.sh [opciones] CARPETA [CARPETA...]
#
#  Opciones:
#    --copiar           Copia en vez de mover (backup)
#    --slowmo           Separa cámaras lentas (> UMBRAL fps)
#    --umbral N         Umbral de fps para SlowMo (default 45)
#    --pv PREFIJO       Prefijo para verticales   (ej: Reels_)
#    --ph PREFIJO       Prefijo para horizontales (ej: YouTube_)
#    --simular          No toca nada, solo muestra qué haría
#    --dialogos         Pide las opciones con ventanas de macOS (Automator)
#
#  Las opciones también se pueden fijar con variables antes de la
#  sección "LÓGICA" (así las genera la web).

# --- CONFIGURACIÓN (la web reemplaza este bloque) ---
COPIAR=${COPIAR:-0}
SLOWMO=${SLOWMO:-0}
UMBRAL=${UMBRAL:-45}
PREF_V=${PREF_V:-}
PREF_H=${PREF_H:-}
SIMULAR=${SIMULAR:-0}
DIALOGOS=${DIALOGOS:-0}
# --- FIN CONFIGURACIÓN ---

# ==================== LÓGICA ====================
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

EXTS="mp4 mov m4v crm nev mxf"
SIDECARS="M01.XML M01.xml .XML .xml .THM .thm .xmp .XMP .LRF .lrf .SRT .srt"
SALIDAS="Verticales Horizontales Verticales_SlowMo Verticales_RealTime Horizontales_SlowMo Horizontales_RealTime"

CARPETAS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --copiar)   COPIAR=1 ;;
    --slowmo)   SLOWMO=1 ;;
    --umbral)   UMBRAL="$2"; shift ;;
    --pv)       PREF_V="$2"; shift ;;
    --ph)       PREF_H="$2"; shift ;;
    --simular)  SIMULAR=1 ;;
    --dialogos) DIALOGOS=1 ;;
    -h|--help)  sed -n '2,20p' "$0"; exit 0 ;;
    *)          CARPETAS+=("$1") ;;
  esac
  shift
done
[ ${#CARPETAS[@]} -eq 0 ] && CARPETAS=(".")

dialogo() { osascript -e "$1" 2>/dev/null; }
avisar()  { if [ "$DIALOGOS" = 1 ]; then dialogo "display alert \"Organizador de Footage\" message \"$1\""; else echo "$1"; fi; }

# --- ExifTool (sistema, o junto a este script) ---
EXIFTOOL=$(command -v exiftool)
[ -z "$EXIFTOOL" ] && [ -x "$(dirname "$0")/exiftool" ] && EXIFTOOL="$(dirname "$0")/exiftool"
if [ -z "$EXIFTOOL" ]; then
  if [ "$DIALOGOS" = 1 ]; then
    R=$(dialogo 'display dialog "No se encontró ExifTool. Es necesario para leer la orientación de los clips." buttons {"Cancelar", "Descargar ExifTool"} default button 2 with icon stop with title "Falta Componente"')
    [[ "$R" == *Descargar* ]] && open "https://exiftool.org/install.html#MacOS"
  else
    echo "❌ No se encontró exiftool. Instalalo desde https://exiftool.org"
  fi
  exit 1
fi

# --- Opciones por ventanas (Automator) ---
if [ "$DIALOGOS" = 1 ]; then
  R=$(dialogo 'choose from list {"Mover (rápido)", "Copiar (backup)"} with title "Organizador de Footage" with prompt "¿Qué hacer con los clips?" default items "Mover (rápido)"')
  [ -z "$R" ] || [ "$R" = "false" ] && exit 0
  [[ "$R" == Copiar* ]] && COPIAR=1

  R=$(dialogo 'display dialog "¿Separar cámaras lentas (más de 45 fps)?" with title "Organizador de Footage" buttons {"No", "Sí"} default button "No"')
  [[ "$R" == *Sí* ]] && SLOWMO=1

  PREF_V=$(dialogo 'text returned of (display dialog "Prefijo para VERTICALES (opcional, ej: Reels_):" with title "Organizador de Footage" default answer "" buttons {"Siguiente"} default button 1)')
  PREF_H=$(dialogo 'text returned of (display dialog "Prefijo para HORIZONTALES (opcional, ej: YouTube_):" with title "Organizador de Footage" default answer "" buttons {"Siguiente"} default button 1)')
fi

# Devuelve una ruta libre: si "dir/nombre.ext" existe, prueba "nombre_2.ext", "nombre_3.ext"...
ruta_libre() {
  local dir="$1" base="$2" ext="$3" n=2 cand="$1/$2$3"
  while [ -e "$cand" ]; do cand="$dir/${base}_$n$ext"; n=$((n + 1)); done
  printf '%s' "$cand"
}

accion() {
  local src="$1" dst="$2"
  if [ "$SIMULAR" = 1 ]; then echo "  [simulación] ${src#$RAIZ/} → ${dst#$RAIZ/}"; return; fi
  mkdir -p "$(dirname "$dst")"
  if [ "$COPIAR" = 1 ]; then cp -p "$src" "$dst"; else mv "$src" "$dst"; fi
}

for RAIZ in "${CARPETAS[@]}"; do
  RAIZ=$(cd "$RAIZ" 2>/dev/null && pwd) || { avisar "No se pudo abrir la carpeta: $RAIZ"; continue; }

  # Una sola llamada a exiftool para toda la carpeta (incluye subcarpetas,
  # ignora las carpetas de salida para no reprocesar).
  ARGS=(-q -m -r -T -n -Directory -FileName -Rotation -VideoFrameRate)
  for e in $EXTS; do ARGS+=(-ext "$e"); done
  for s in $SALIDAS; do ARGS+=(-i "$s"); done
  LISTA=$("$EXIFTOOL" "${ARGS[@]}" "$RAIZ" | LC_ALL=C sort)

  if [ -z "$LISTA" ]; then avisar "No se encontraron clips de video en $(basename "$RAIZ")."; continue; fi

  NV=0; NH=0; NS=0
  while IFS=$'\t' read -r dir file rot fps; do
    case "$rot" in 90|270|-90) NV=$((NV + 1)) ;; *) NH=$((NH + 1)) ;; esac
    f=${fps%.*}; [[ "$f" =~ ^[0-9]+$ ]] && [ "$f" -gt "$UMBRAL" ] && NS=$((NS + 1))
  done <<< "$LISTA"
  TOTAL=$((NV + NH))

  RES="$TOTAL clips: $NV verticales, $NH horizontales"
  [ "$SLOWMO" = 1 ] && RES="$RES ($NS en slow motion)"
  if [ "$DIALOGOS" = 1 ]; then
    VERBO=$([ "$COPIAR" = 1 ] && echo Copiar || echo Mover)
    R=$(dialogo "display dialog \"$(basename "$RAIZ")\n\n$RES\" with title \"Organizador de Footage\" buttons {\"Cancelar\", \"$VERBO\"} default button 2")
    [[ "$R" == *"$VERBO"* ]] || continue
  else
    echo "📁 $RAIZ"
    echo "   $RES"
  fi

  while IFS=$'\t' read -r dir file rot fps; do
    src="$dir/$file"
    base="${file%.*}"; ext=".${file##*.}"
    case "$rot" in
      90|270|-90) carpeta="Verticales";   pref="$PREF_V" ;;
      *)          carpeta="Horizontales"; pref="$PREF_H" ;;
    esac
    if [ "$SLOWMO" = 1 ]; then
      f=${fps%.*}
      if [[ "$f" =~ ^[0-9]+$ ]] && [ "$f" -gt "$UMBRAL" ]; then carpeta="${carpeta}_SlowMo"; else carpeta="${carpeta}_RealTime"; fi
    fi

    destino=$(ruta_libre "$RAIZ/$carpeta" "$pref$base" "$ext")
    nuevo=$(basename "$destino"); nuevo="${nuevo%.*}"
    accion "$src" "$destino"

    # En discos que no distinguen mayúsculas, ".XML" y ".xml" son el mismo archivo
    hechos=""
    for s in $SIDECARS; do
      clave=$(printf '%s' "$s" | tr '[:upper:]' '[:lower:]')
      [[ "$hechos" == *"|$clave|"* ]] && continue
      if [ -f "$dir/$base$s" ]; then
        accion "$dir/$base$s" "$RAIZ/$carpeta/$nuevo$s"
        hechos="$hechos|$clave|"
      fi
    done
  done <<< "$LISTA"

  if [ "$SIMULAR" = 1 ]; then echo "✅ Simulación terminada. No se modificó nada."
  elif [ "$DIALOGOS" = 1 ]; then dialogo "display notification \"$RES\" with title \"Organizador de Footage\" subtitle \"Listo ✅\""
  else echo "✅ Listo."; fi
done

[ "$DIALOGOS" = 1 ] || echo "☕ Si te ahorró tiempo: https://cafecito.app/beto_oviedo"
