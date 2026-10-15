// Organizador de Footage v2 — @betoviedo
// App nativa: separa clips en Verticales / Horizontales (y SlowMo / RealTime).
// Lee orientación y fps con AVFoundation, no necesita ExifTool.

import SwiftUI
import AVFoundation
import UniformTypeIdentifiers
import AppKit

// MARK: - Constantes

let extensionesVideo: Set<String> = ["mp4", "mov", "m4v", "crm", "nev", "mxf"]
let sufijosSidecar = ["M01.XML", "M01.xml", ".XML", ".xml", ".THM", ".thm", ".xmp", ".XMP", ".LRF", ".lrf", ".SRT", ".srt"]
let carpetasSalida: Set<String> = ["Verticales", "Horizontales", "Verticales_SlowMo", "Verticales_RealTime", "Horizontales_SlowMo", "Horizontales_RealTime"]
let linkCafecito = URL(string: "https://cafecito.app/beto_oviedo")!
let linkInstagram = URL(string: "https://www.instagram.com/betoviedo/")!
let linkGitHub = URL(string: "https://github.com/betropical/organizador-de-footage")!

// MARK: - Modelo

struct Clip: Identifiable {
    let id = UUID()
    let url: URL
    let sidecars: [URL]
    var vertical: Bool?          // nil = no se pudo leer
    var forzado = false          // el usuario cambió la orientación a mano
    var fps: Double?
    var miniatura: NSImage?
    var leido = false

    var nombre: String { url.lastPathComponent }
    var esVertical: Bool { vertical ?? false }
}

struct Movimiento {
    let origen: URL
    let destino: URL
}

struct Plan {
    var destinos: [UUID: URL] = [:]
    var movimientos: [Movimiento] = []
}

@MainActor
final class Organizador: ObservableObject {
    @Published var carpeta: URL?
    @Published var destinoElegido: URL?   // nil = la misma carpeta de los clips
    @Published var clips: [Clip] = []
    @Published var escaneando = false
    @Published var procesando = false
    @Published var progreso: Double = 0
    @Published var terminado: String?
    @Published var error: String?

    // Lo último que se hizo, para poder deshacerlo
    private var hechos: [Movimiento] = []
    /// Destinos con los que se procesó (para mostrarlos al terminar, sin recalcular)
    @Published var planUsado: Plan?
    private var hechosCopiando = false
    private var carpetasCreadas: [URL] = []

    // Opciones (se recuerdan entre usos)
    private static let prefs = UserDefaults.standard
    @Published var copiar = prefs.bool(forKey: "copiar") { didSet { Self.prefs.set(copiar, forKey: "copiar") } }
    @Published var slowmo = prefs.bool(forKey: "slowmo") { didSet { Self.prefs.set(slowmo, forKey: "slowmo") } }
    @Published var umbral = prefs.object(forKey: "umbral") as? Int ?? 45 { didSet { Self.prefs.set(umbral, forKey: "umbral") } }
    @Published var prefijoV = prefs.string(forKey: "prefijoV") ?? "" { didSet { Self.prefs.set(prefijoV, forKey: "prefijoV") } }
    @Published var prefijoH = prefs.string(forKey: "prefijoH") ?? "" { didSet { Self.prefs.set(prefijoH, forKey: "prefijoH") } }

    private var tareaEscaneo: Task<Void, Never>?

    var leidos: Int { clips.filter(\.leido).count }
    var verticales: Int { clips.filter(\.esVertical).count }
    var horizontales: Int { clips.count - verticales }
    var lentos: Int { clips.filter(esLento).count }

    /// Dónde se crean Verticales/Horizontales
    var destino: URL? { destinoElegido ?? carpeta }

    /// El destino está en otro disco: mover = copiar + borrar (tarda más)
    var destinoEnOtroDisco: Bool {
        guard let a = carpeta, let b = destino else { return false }
        let ida = try? a.resourceValues(forKeys: [.volumeIdentifierKey]).volumeIdentifier as? NSObject
        let idb = try? b.resourceValues(forKeys: [.volumeIdentifierKey]).volumeIdentifier as? NSObject
        return ida != nil && idb != nil && ida != idb
    }

    func esLento(_ c: Clip) -> Bool { (c.fps ?? 0) > Double(umbral) }

    func carpetaDestino(_ c: Clip) -> String {
        var nombre = c.esVertical ? "Verticales" : "Horizontales"
        if slowmo { nombre += esLento(c) ? "_SlowMo" : "_RealTime" }
        return nombre
    }

    // MARK: Escaneo

    func abrir(_ url: URL, mantenerDestino: Bool = false) {
        var esDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &esDir), esDir.boolValue else {
            error = "Arrastrá una carpeta, no un archivo."
            return
        }
        tareaEscaneo?.cancel()
        carpeta = url
        if !mantenerDestino { destinoElegido = nil }
        terminado = nil
        hechos = []
        error = nil
        clips = buscarClips(en: url)
        if clips.isEmpty { error = "No encontré clips de video en esta carpeta." }
        leerMetadatos()
    }

    func elegirCarpeta() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.prompt = "Elegir"
        panel.message = "Elegí la carpeta o tarjeta con los clips"
        if panel.runModal() == .OK, let url = panel.url { abrir(url) }
    }

    func elegirDestino() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = destino
        panel.prompt = "Guardar acá"
        panel.message = "Elegí dónde crear las carpetas Verticales y Horizontales"
        if panel.runModal() == .OK, let url = panel.url {
            destinoElegido = (url.standardizedFileURL == carpeta?.standardizedFileURL) ? nil : url
        }
    }

    func cerrar() {
        tareaEscaneo?.cancel()
        carpeta = nil
        destinoElegido = nil
        clips = []
        terminado = nil
        error = nil
    }

    private func buscarClips(en raiz: URL) -> [Clip] {
        let fm = FileManager.default
        guard let e = fm.enumerator(at: raiz, includingPropertiesForKeys: [.isDirectoryKey],
                                    options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { return [] }
        var videos: [URL] = []
        for case let url as URL in e {
            if (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
                if carpetasSalida.contains(url.lastPathComponent) { e.skipDescendants() }
                continue
            }
            if extensionesVideo.contains(url.pathExtension.lowercased()) { videos.append(url) }
        }
        videos.sort { $0.path.localizedStandardCompare($1.path) == .orderedAscending }

        // Contenido de cada carpeta, para encontrar sidecars sin importar mayúsculas
        var contenido: [URL: [String: URL]] = [:]
        return videos.map { video in
            let dir = video.deletingLastPathComponent()
            if contenido[dir] == nil {
                let archivos = (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
                contenido[dir] = Dictionary(archivos.map { ($0.lastPathComponent.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
            }
            let base = video.deletingPathExtension().lastPathComponent
            var vistos = Set<String>()
            var sidecars: [URL] = []
            for s in sufijosSidecar {
                let clave = (base + s).lowercased()
                if !vistos.contains(clave), let u = contenido[dir]?[clave] {
                    vistos.insert(clave)
                    sidecars.append(u)
                }
            }
            return Clip(url: video, sidecars: sidecars)
        }
    }

    private func leerMetadatos() {
        escaneando = true
        let pendientes = clips.map { ($0.id, $0.url) }
        tareaEscaneo = Task {
            // De a 6 clips en paralelo
            for lote in stride(from: 0, to: pendientes.count, by: 6) {
                if Task.isCancelled { return }
                let grupo = pendientes[lote..<min(lote + 6, pendientes.count)]
                await withTaskGroup(of: (UUID, Bool?, Double?, NSImage?).self) { g in
                    for (id, url) in grupo {
                        g.addTask {
                            let (v, f) = await leerOrientacion(url)
                            let m = await miniatura(url)
                            return (id, v, f, m)
                        }
                    }
                    for await (id, v, f, m) in g {
                        guard let i = clips.firstIndex(where: { $0.id == id }) else { continue }
                        clips[i].vertical = v
                        clips[i].fps = f
                        clips[i].miniatura = m
                        clips[i].leido = true
                    }
                }
            }
            escaneando = false
        }
    }

    func alternarOrientacion(_ id: UUID) {
        guard let i = clips.firstIndex(where: { $0.id == id }) else { return }
        clips[i].vertical = !clips[i].esVertical
        clips[i].forzado = true
    }

    // MARK: Plan y ejecución

    /// Calcula el destino de cada clip sin pisar nada: ni archivos existentes
    /// ni otros clips con el mismo nombre (ej: dos tarjetas con C0001.MP4).
    func plan() -> Plan {
        guard let raiz = destino else { return Plan() }
        let fm = FileManager.default
        var plan = Plan()
        var ocupados = Set<String>()

        func libre(_ dir: URL, _ base: String, _ ext: String, sidecars: [String]) -> String {
            var n = 1
            while true {
                let nombre = n == 1 ? base : "\(base)_\(n)"
                let rutas = [dir.appendingPathComponent(nombre + ext)] + sidecars.map { dir.appendingPathComponent(nombre + $0) }
                if rutas.allSatisfy({ !ocupados.contains($0.path.lowercased()) && !fm.fileExists(atPath: $0.path) }) {
                    rutas.forEach { ocupados.insert($0.path.lowercased()) }
                    return nombre
                }
                n += 1
            }
        }

        for c in clips {
            let dir = raiz.appendingPathComponent(carpetaDestino(c))
            let base = c.url.deletingPathExtension().lastPathComponent
            let viejaBase = base
            let ext = c.url.pathExtension.isEmpty ? "" : "." + c.url.pathExtension
            let sufijos = c.sidecars.map { String($0.lastPathComponent.dropFirst(viejaBase.count)) }
            let nuevo = libre(dir, (c.esVertical ? prefijoV : prefijoH) + base, ext, sidecars: sufijos)

            let destino = dir.appendingPathComponent(nuevo + ext)
            plan.destinos[c.id] = destino
            plan.movimientos.append(Movimiento(origen: c.url, destino: destino))
            for (s, suf) in zip(c.sidecars, sufijos) {
                plan.movimientos.append(Movimiento(origen: s, destino: dir.appendingPathComponent(nuevo + suf)))
            }
        }
        return plan
    }

    func ejecutar() {
        let p = plan()
        planUsado = p
        let movimientos = p.movimientos
        let copiar = self.copiar
        let resumen = "\(clips.count) clips: \(verticales) verticales, \(horizontales) horizontales"
        procesando = true
        progreso = 0
        error = nil

        Task.detached(priority: .userInitiated) {
            let fm = FileManager.default
            var fallas: [String] = []
            var hechos: [Movimiento] = []
            var creadas: [URL] = []
            for (i, m) in movimientos.enumerated() {
                do {
                    let dir = m.destino.deletingLastPathComponent()
                    if !fm.fileExists(atPath: dir.path) {
                        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
                        creadas.append(dir)
                    }
                    if copiar { try fm.copyItem(at: m.origen, to: m.destino) }
                    else { try fm.moveItem(at: m.origen, to: m.destino) }
                    hechos.append(m)
                } catch {
                    fallas.append("\(m.origen.lastPathComponent): \(error.localizedDescription)")
                }
                let p = Double(i + 1) / Double(movimientos.count)
                await MainActor.run { self.progreso = p }
            }
            let listaFallas = fallas
            let listaHechos = hechos
            let listaCreadas = creadas
            await MainActor.run {
                self.hechos = listaHechos
                self.hechosCopiando = copiar
                self.carpetasCreadas = listaCreadas
                self.procesando = false
                if listaFallas.isEmpty {
                    self.terminado = resumen
                } else {
                    self.terminado = resumen
                    self.error = "No se pudieron procesar \(listaFallas.count) archivo(s):\n" + listaFallas.prefix(5).joined(separator: "\n")
                }
                NSSound(named: "Glass")?.play()
            }
        }
    }

    var puedeDeshacer: Bool { !hechos.isEmpty }

    /// Vuelve todo como estaba: devuelve los clips movidos o borra las copias,
    /// y quita las carpetas que se crearon si quedaron vacías.
    func deshacer() {
        let hechos = self.hechos, copiando = hechosCopiando, creadas = carpetasCreadas
        guard !hechos.isEmpty, let raiz = carpeta else { return }
        procesando = true
        progreso = 0
        error = nil

        Task.detached(priority: .userInitiated) {
            let fm = FileManager.default
            var fallas: [String] = []
            for (i, m) in hechos.reversed().enumerated() {
                do {
                    if copiando {
                        try fm.removeItem(at: m.destino)
                    } else if !fm.fileExists(atPath: m.origen.path) {
                        try fm.createDirectory(at: m.origen.deletingLastPathComponent(), withIntermediateDirectories: true)
                        try fm.moveItem(at: m.destino, to: m.origen)
                    }
                } catch {
                    fallas.append("\(m.destino.lastPathComponent): \(error.localizedDescription)")
                }
                let p = Double(i + 1) / Double(hechos.count)
                await MainActor.run { self.progreso = p }
            }
            for dir in creadas.sorted(by: { $0.path.count > $1.path.count }) {
                let resto = (try? fm.contentsOfDirectory(atPath: dir.path)) ?? []
                if resto.allSatisfy({ $0 == ".DS_Store" }) { try? fm.removeItem(at: dir) }
            }
            let listaFallas = fallas
            await MainActor.run {
                self.procesando = false
                self.abrir(raiz, mantenerDestino: true)
                if !listaFallas.isEmpty {
                    self.error = "No se pudieron deshacer \(listaFallas.count) archivo(s):\n" + listaFallas.prefix(5).joined(separator: "\n")
                }
            }
        }
    }

    func mostrarEnFinder() {
        if let d = destino { NSWorkspace.shared.open(d) }
    }
}

// MARK: - Lectura de metadatos

/// Devuelve (vertical, fps). Primero AVFoundation; si el formato no es
/// compatible (ej: Canon CRM, Nikon NEV) intenta con ExifTool si está instalado.
func leerOrientacion(_ url: URL) async -> (Bool?, Double?) {
    let asset = AVURLAsset(url: url)
    if let pista = try? await asset.loadTracks(withMediaType: .video).first,
       let (tam, transformacion, fps) = try? await pista.load(.naturalSize, .preferredTransform, .nominalFrameRate),
       tam.width > 0 {
        let r = CGRect(origin: .zero, size: tam).applying(transformacion)
        return (abs(r.height) > abs(r.width), fps > 0 ? Double(fps) : nil)
    }
    return leerConExiftool(url)
}

func leerConExiftool(_ url: URL) -> (Bool?, Double?) {
    let rutas = ["/opt/homebrew/bin/exiftool", "/usr/local/bin/exiftool", "/usr/bin/exiftool"]
    guard let exiftool = rutas.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return (nil, nil) }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: exiftool)
    p.arguments = ["-q", "-m", "-T", "-n", "-Rotation", "-VideoFrameRate", url.path]
    let salida = Pipe()
    p.standardOutput = salida
    p.standardError = FileHandle.nullDevice
    guard (try? p.run()) != nil else { return (nil, nil) }
    p.waitUntilExit()
    let texto = String(data: salida.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    let campos = texto.trimmingCharacters(in: .whitespacesAndNewlines).components(separatedBy: "\t")
    guard campos.count >= 2 else { return (nil, nil) }
    let rot = Int(Double(campos[0]) ?? -1)
    return (rot == -1 ? nil : [90, 270, -90].contains(rot), Double(campos[1]))
}

func miniatura(_ url: URL) async -> NSImage? {
    let gen = AVAssetImageGenerator(asset: AVURLAsset(url: url))
    gen.appliesPreferredTrackTransform = true
    gen.maximumSize = CGSize(width: 160, height: 160)
    guard let (img, _) = try? await gen.image(at: CMTime(seconds: 0.5, preferredTimescale: 600)) else { return nil }
    return NSImage(cgImage: img, size: NSSize(width: img.width, height: img.height))
}

// MARK: - Vistas

struct VistaPrincipal: View {
    @EnvironmentObject var org: Organizador
    @State private var arrastrando = false

    var body: some View {
        VStack(spacing: 0) {
            Encabezado()
            Divider()
            if org.carpeta == nil {
                ZonaSoltar(arrastrando: arrastrando)
            } else {
                HStack(spacing: 0) {
                    PanelOpciones()
                        .frame(width: 260)
                    Divider()
                    ListaClips()
                }
            }
        }
        .frame(minWidth: 860, minHeight: 600)
        .dropDestination(for: URL.self) { urls, _ in
            guard let url = urls.first, !org.procesando else { return false }
            org.abrir(url)
            return true
        } isTargeted: { arrastrando = $0 }
    }
}

struct Encabezado: View {
    var body: some View {
        HStack {
            Image(systemName: "rectangle.portrait.on.rectangle.portrait.angled")
                .font(.title2)
                .foregroundStyle(.tint)
            Text("Organizador de Footage").font(.title3.bold())
            Spacer()
            Link("@betoviedo", destination: linkInstagram).foregroundStyle(.secondary)
            Link("GitHub", destination: linkGitHub).foregroundStyle(.secondary).padding(.horizontal, 6)
            Link(destination: linkCafecito) {
                Label("Invitame un cafecito", systemImage: "cup.and.saucer.fill")
            }
            .buttonStyle(.bordered)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}

struct ZonaSoltar: View {
    @EnvironmentObject var org: Organizador
    let arrastrando: Bool

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "arrow.down.doc")
                .font(.system(size: 46, weight: .light))
                .foregroundStyle(arrastrando ? Color.accentColor : .secondary)
            Text("Arrastrá acá una carpeta o tarjeta").font(.title2.weight(.semibold))
            Text("Busca clips en todas las subcarpetas y los separa en verticales y horizontales.\nAntes de mover nada vas a ver cómo queda.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
            Button("Elegir carpeta…") { org.elegirCarpeta() }
                .controlSize(.large)
                .keyboardShortcut("o")
            if let e = org.error { Text(e).foregroundStyle(.red) }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8, 6]))
                .foregroundStyle(arrastrando ? Color.accentColor : Color.secondary.opacity(0.4))
                .padding(20)
        )
    }
}

struct PanelOpciones: View {
    @EnvironmentObject var org: Organizador

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
          ScrollView {
          VStack(alignment: .leading, spacing: 18) {
            Grupo("Clips de") {
                CajaCarpeta(nombre: org.carpeta?.lastPathComponent ?? "", url: org.carpeta, icono: "film.stack")
                HStack(spacing: 10) {
                    Button("Elegir otra carpeta…") { org.elegirCarpeta() }
                    Button("Cerrar") { org.cerrar() }
                }
                .buttonStyle(.link)
            }

            Resumen()

            Grupo("Acción") {
                Picker("", selection: $org.copiar) {
                    Text("Mover").tag(false)
                    Text("Copiar").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                Text(org.copiar ? "Deja los originales donde están (backup)." : "Rápido si es el mismo disco.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            GuardarEn()

            Grupo("Cámara lenta") {
                Toggle("Separar SlowMo / RealTime", isOn: $org.slowmo)
                if org.slowmo {
                    Stepper("Más de \(org.umbral) fps", value: $org.umbral, in: 24...240, step: 5)
                }
            }

            Grupo("Prefijos (opcional)") {
                Text("Se agregan al principio del nombre de cada clip.")
                    .font(.caption).foregroundStyle(.secondary)
                CampoPrefijo(titulo: "Verticales", icono: "rectangle.portrait", color: .purple,
                             ejemploVacio: "Reels_", texto: $org.prefijoV,
                             clip: org.clips.first(where: \.esVertical)?.nombre ?? "C0002.MP4")
                CampoPrefijo(titulo: "Horizontales", icono: "rectangle", color: .blue,
                             ejemploVacio: "YouTube_", texto: $org.prefijoH,
                             clip: org.clips.first(where: { !$0.esVertical })?.nombre ?? "C0001.MP4")
            }
          }
          .padding(.trailing, 4)
          }
          .disabled(org.procesando || org.terminado != nil)

            if let e = org.error {
                Text(e).font(.caption).foregroundStyle(.red).textSelection(.enabled)
            }
            BotonAccion()
        }
        .textFieldStyle(.roundedBorder)
        .padding(16)
    }
}

/// Recuadro con el nombre y la ruta de una carpeta (origen o destino)
struct CajaCarpeta: View {
    let nombre: String
    let url: URL?
    let icono: String

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: icono).foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 2) {
                Text(nombre).font(.callout.weight(.medium)).lineLimit(1).truncationMode(.middle)
                Text(url.map { ($0.path as NSString).abbreviatingWithTildeInPath } ?? "")
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .truncationMode(.head)
                    .help(url?.path ?? "")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(8)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
    }
}

/// Campo de prefijo bien visible, con ejemplo en vivo del nombre resultante
struct CampoPrefijo: View {
    let titulo: String, icono: String, color: Color, ejemploVacio: String
    @Binding var texto: String
    let clip: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(titulo, systemImage: icono)
                .font(.callout.weight(.medium))
                .foregroundStyle(color)
            TextField("Escribí un prefijo, ej: \(ejemploVacio)", text: $texto)
                .textFieldStyle(.plain)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(color.opacity(texto.isEmpty ? 0.35 : 0.8), lineWidth: 1))
            Text(texto.isEmpty ? "Sin prefijo: \(clip)" : "Queda: \(texto)\(clip)")
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .lineLimit(1).truncationMode(.middle)
        }
        .padding(.top, 4)
    }
}

struct GuardarEn: View {
    @EnvironmentObject var org: Organizador

    var body: some View {
        Grupo("Guardar en") {
            CajaCarpeta(nombre: org.destinoElegido == nil ? "La misma carpeta" : (org.destino?.lastPathComponent ?? ""),
                        url: org.destino,
                        icono: org.destinoElegido == nil ? "folder" : "externaldrive")

            HStack(spacing: 10) {
                Button("Elegir otro destino…") { org.elegirDestino() }
                if org.destinoElegido != nil {
                    Button("Usar la misma") { org.destinoElegido = nil }
                }
            }
            .buttonStyle(.link)

            Text("Ahí adentro se crean las carpetas \(org.slowmo ? "Verticales_… y Horizontales_…" : "Verticales y Horizontales").")
                .font(.caption).foregroundStyle(.secondary)
            if org.destinoEnOtroDisco && !org.copiar {
                Label("Es otro disco: mover tarda lo mismo que copiar.", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
            }
        }
    }

}

struct Grupo<Contenido: View>: View {
    let titulo: String
    @ViewBuilder let contenido: Contenido
    init(_ titulo: String, @ViewBuilder contenido: () -> Contenido) {
        self.titulo = titulo
        self.contenido = contenido()
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(titulo.uppercased()).font(.caption.bold()).foregroundStyle(.secondary)
            contenido
        }
    }
}

struct Resumen: View {
    @EnvironmentObject var org: Organizador
    var body: some View {
        HStack(spacing: 8) {
            Ficha(valor: org.verticales, texto: "verticales", icono: "rectangle.portrait")
            Ficha(valor: org.horizontales, texto: "horizontales", icono: "rectangle")
            if org.slowmo { Ficha(valor: org.lentos, texto: "slow-mo", icono: "tortoise") }
        }
    }
}

struct Ficha: View {
    let valor: Int, texto: String, icono: String
    var body: some View {
        VStack(spacing: 2) {
            Image(systemName: icono).foregroundStyle(.secondary)
            Text("\(valor)").font(.title3.monospacedDigit().bold())
            Text(texto).font(.caption2).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 6)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
    }
}

struct BotonAccion: View {
    @EnvironmentObject var org: Organizador
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if org.procesando {
                ProgressView(value: org.progreso)
                Text(org.terminado != nil ? "Deshaciendo…" : (org.copiar ? "Copiando…" : "Moviendo…"))
                    .font(.caption).foregroundStyle(.secondary)
            } else if let t = org.terminado {
                Label("Listo: \(t)", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                    .font(.callout)
                Button { org.mostrarEnFinder() } label: {
                    Text("Mostrar en Finder").frame(maxWidth: .infinity)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                HStack {
                    if org.puedeDeshacer {
                        Button { org.deshacer() } label: {
                            Label("Deshacer", systemImage: "arrow.uturn.backward")
                        }
                        .help(org.copiar ? "Borra las copias y deja todo como estaba"
                                         : "Devuelve los clips a donde estaban, para cambiar algo y volver a hacerlo")
                    }
                    Button("Otra carpeta") { org.cerrar() }
                }
                Text(org.copiar ? "¿Te equivocaste? Deshacer borra las copias."
                                : "¿Te equivocaste de destino? Deshacer devuelve los clips a su lugar.")
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                Button {
                    org.ejecutar()
                } label: {
                    Text(org.escaneando
                         ? "Leyendo clips… \(org.leidos)/\(org.clips.count)"
                         : "\(org.copiar ? "Copiar" : "Mover") \(org.clips.count) clips")
                        .frame(maxWidth: .infinity)
                }
                .controlSize(.large)
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(org.escaneando || org.clips.isEmpty)
            }
        }
    }
}

struct ListaClips: View {
    @EnvironmentObject var org: Organizador

    var body: some View {
        let plan = (org.procesando || org.terminado != nil) ? (org.planUsado ?? org.plan()) : org.plan()
        let raiz = org.carpeta?.path ?? ""
        List(org.clips) { c in
            FilaClip(clip: c,
                     origen: String(c.url.deletingLastPathComponent().path.dropFirst(raiz.count)),
                     destino: plan.destinos[c.id].map { org.carpetaDestino(c) + "/" + $0.lastPathComponent } ?? "",
                     lento: org.slowmo && org.esLento(c))
        }
        .listStyle(.inset(alternatesRowBackgrounds: true))
        .disabled(org.terminado != nil)
        .opacity(org.terminado != nil ? 0.5 : 1)
    }
}

struct FilaClip: View {
    @EnvironmentObject var org: Organizador
    let clip: Clip, origen: String, destino: String, lento: Bool

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 4).fill(.quaternary)
                if let m = clip.miniatura {
                    Image(nsImage: m).resizable().scaledToFit()
                } else if !clip.leido {
                    ProgressView().controlSize(.small)
                }
            }
            .frame(width: 64, height: 40)
            .clipShape(RoundedRectangle(cornerRadius: 4))

            VStack(alignment: .leading, spacing: 2) {
                Text(clip.nombre).font(.body.weight(.medium))
                HStack(spacing: 6) {
                    if !origen.isEmpty { Text(origen).lineLimit(1).truncationMode(.head) }
                    if let f = clip.fps { Text(String(format: "%.2f fps", f)).fixedSize() }
                    if !clip.sidecars.isEmpty { Text("+\(clip.sidecars.count) sidecar").fixedSize() }
                }
                .lineLimit(1)
                .font(.caption).foregroundStyle(.secondary)
            }
            .frame(minWidth: 160, alignment: .leading)

            Spacer()

            Button { org.alternarOrientacion(clip.id) } label: {
                Label(etiqueta, systemImage: clip.esVertical ? "rectangle.portrait.fill" : "rectangle.fill")
                    .font(.caption.bold())
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .background(color.opacity(0.18), in: Capsule())
                    .foregroundStyle(color)
                    .fixedSize()
            }
            .buttonStyle(.plain)
            .help(clip.vertical == nil ? "No se pudo leer la orientación. Clic para cambiar."
                                       : "Clic para cambiar la orientación a mano")
            .disabled(!clip.leido)

            if lento { Image(systemName: "tortoise.fill").foregroundStyle(.orange).help("Slow motion") }

            Image(systemName: "arrow.right").foregroundStyle(.tertiary)
            Text(destino)
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(width: 230, alignment: .leading)
        }
        .padding(.vertical, 2)
    }

    var etiqueta: String {
        if !clip.leido { return "…" }
        let base = clip.esVertical ? "Vertical" : "Horizontal"
        if clip.vertical == nil { return "¿" + base + "?" }
        return clip.forzado ? base + " ✎" : base
    }
    var color: Color {
        if clip.vertical == nil && clip.leido { return .gray }
        return clip.esVertical ? .purple : .blue
    }
}

// MARK: - App

final class Delegado: NSObject, NSApplicationDelegate {
    var organizador: Organizador?
    var pendiente: URL?

    // Carpeta soltada sobre el ícono de la app (Dock / Finder)
    func application(_ application: NSApplication, open urls: [URL]) {
        guard let url = urls.first else { return }
        if let org = organizador { Task { @MainActor in org.abrir(url) } } else { pendiente = url }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

#if !PRUEBAS
@main
struct OrganizadorFootageApp: App {
    @NSApplicationDelegateAdaptor(Delegado.self) var delegado
    @StateObject private var org = Organizador()

    var body: some Scene {
        WindowGroup("Organizador de Footage") {
            VistaPrincipal()
                .environmentObject(org)
                .onAppear {
                    delegado.organizador = org
                    if let u = delegado.pendiente { org.abrir(u); delegado.pendiente = nil }
                }
        }
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Abrir carpeta…") { org.elegirCarpeta() }.keyboardShortcut("o")
            }
        }
    }
}
#endif
