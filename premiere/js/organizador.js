// Organizador de Footage (Premiere) — @betoviedo
// Lógica de disco: buscar clips, armar el plan, mover / copiar con sidecars y deshacer.
// Mismas reglas que la app de Mac (App/OrganizadorFootage.swift).

(function () {
    var req = (typeof window !== 'undefined' && window.cep_node) ? window.cep_node.require : require;
    var fs = req('fs'), path = req('path');

    var EXT_VIDEO = ['mp4', 'mov', 'm4v', 'crm', 'nev', 'mxf'];
    // En el modo Proyecto se aceptan más formatos: si el panel no los sabe leer, se usa lo que informa Premiere
    var EXT_PROYECTO = EXT_VIDEO.concat(['mts', 'm2ts', 'avi', 'mkv', 'braw', 'r3d', 'insv', 'mpg', 'mpeg', 'wmv', 'webm', '3gp', 'hevc', 'mxf', 'lrv']);
    var SUFIJOS_SIDECAR = ['M01.XML', 'M01.xml', '.XML', '.xml', '.THM', '.thm', '.xmp', '.XMP', '.LRF', '.lrf', '.SRT', '.srt'];
    var CARPETAS_SALIDA = ['Verticales', 'Horizontales', 'Verticales_SlowMo', 'Verticales_RealTime', 'Horizontales_SlowMo', 'Horizontales_RealTime'];

    function extension(ruta) { return path.extname(ruta).slice(1).toLowerCase(); }
    function esVideo(ruta) { return EXT_VIDEO.indexOf(extension(ruta)) >= 0; }
    function esVideoProyecto(ruta) { return EXT_PROYECTO.indexOf(extension(ruta)) >= 0; }

    function esLento(clip, opciones) { return (clip.fps || 0) > opciones.umbral; }

    function carpetaDestino(clip, opciones) {
        var nombre = clip.vertical ? 'Verticales' : 'Horizontales';
        if (opciones.slowmo) nombre += esLento(clip, opciones) ? '_SlowMo' : '_RealTime';
        return nombre;
    }

    // Busca clips en la carpeta y, si recursivo, en las subcarpetas (ignora ocultos y carpetas de salida)
    function escanear(raiz, recursivo) {
        var videos = [];
        (function recorrer(dir) {
            var nombres;
            try { nombres = fs.readdirSync(dir); } catch (e) { return; }
            nombres.sort();
            for (var i = 0; i < nombres.length; i++) {
                var n = nombres[i];
                if (n.charAt(0) === '.') continue;
                var ruta = path.join(dir, n), st;
                try { st = fs.statSync(ruta); } catch (e) { continue; }
                if (st.isDirectory()) {
                    if (recursivo !== false && CARPETAS_SALIDA.indexOf(n) < 0 && !/\.(app|fcpbundle|photoslibrary|prproj)$/i.test(n)) recorrer(ruta);
                } else if (esVideo(n)) videos.push(ruta);
            }
        })(raiz);

        // Contenido de cada carpeta, para encontrar sidecars sin importar mayúsculas
        var contenido = {};
        return videos.map(function (video) {
            var dir = path.dirname(video);
            if (!contenido[dir]) contenido[dir] = fs.readdirSync(dir);
            var base = path.basename(video, path.extname(video));
            var sidecars = [], vistos = {};
            SUFIJOS_SIDECAR.forEach(function (s) {
                var buscado = (base + s).toLowerCase();
                if (vistos[buscado]) return;
                for (var i = 0; i < contenido[dir].length; i++) {
                    if (contenido[dir][i].toLowerCase() === buscado) {
                        sidecars.push(path.join(dir, contenido[dir][i]));
                        vistos[buscado] = true;
                        break;
                    }
                }
            });
            return { ruta: video, nombre: path.basename(video), sidecars: sidecars, vertical: null, fps: null };
        });
    }

    // Plan: a dónde va cada clip y sus sidecars, sin pisar nada (ni en disco ni entre clips del mismo lote)
    function plan(clips, opciones, raizDestino) {
        var tomados = {};
        function libre(dir, base, ext, sufijos) {
            for (var n = 1; ; n++) {
                var nombre = n === 1 ? base : base + '_' + n;
                var rutas = [path.join(dir, nombre + ext)].concat(sufijos.map(function (s) { return path.join(dir, nombre + s); }));
                var ocupado = rutas.some(function (r) { return tomados[r.toLowerCase()] || fs.existsSync(r); });
                if (!ocupado) {
                    rutas.forEach(function (r) { tomados[r.toLowerCase()] = true; });
                    return nombre;
                }
            }
        }
        return clips.map(function (c) {
            var carpeta = carpetaDestino(c, opciones);
            var dir = path.join(raizDestino, carpeta);
            var ext = path.extname(c.ruta);
            var base = path.basename(c.ruta, ext);
            var sufijos = c.sidecars.map(function (s) { return path.basename(s).slice(base.length); });
            var nuevo = libre(dir, (c.vertical ? opciones.prefijoV : opciones.prefijoH) + base, ext, sufijos);
            return {
                clip: c,
                carpeta: carpeta,
                destino: path.join(dir, nuevo + ext),
                sidecars: c.sidecars.map(function (s, i) { return { de: s, a: path.join(dir, nuevo + sufijos[i]) }; })
            };
        });
    }

    // ---------- Operaciones de archivo (compatibles con el Node viejo de CEP 8) ----------

    function promesa(fn) {
        return new Promise(function (ok, mal) { fn(function (e, r) { if (e) mal(e); else ok(r); }); });
    }

    function crearCarpeta(dir, creadas) {
        if (fs.existsSync(dir)) return;
        crearCarpeta(path.dirname(dir), creadas);
        fs.mkdirSync(dir);
        creadas.push(dir);
    }

    function copiar(de, a) {
        return new Promise(function (ok, mal) {
            var fin = function (e) {
                if (e) return mal(e);
                // Conserva la fecha de modificación del original
                fs.stat(de, function (e2, st) {
                    if (e2) return ok();
                    fs.utimes(a, st.atime, st.mtime, function () { ok(); });
                });
            };
            if (fs.copyFile) return fs.copyFile(de, a, fs.constants.COPYFILE_EXCL, fin);
            var lector = fs.createReadStream(de), escritor = fs.createWriteStream(a, { flags: 'wx' });
            lector.on('error', fin);
            escritor.on('error', fin);
            escritor.on('close', function () { fin(); });
            lector.pipe(escritor);
        });
    }

    function mover(de, a) {
        if (fs.existsSync(a)) return Promise.reject(new Error('Ya existe: ' + a));
        return promesa(function (cb) { fs.rename(de, a, cb); }).catch(function (e) {
            // Entre discos distintos rename no funciona: copiar y borrar
            if (e.code !== 'EXDEV') throw e;
            return copiar(de, a).then(function () { return promesa(function (cb) { fs.unlink(de, cb); }); });
        });
    }

    // Ejecuta el plan. progreso(hechos, total, nombre). Devuelve el registro para deshacer.
    function ejecutar(pasos, copiarArchivos, progreso) {
        var registro = { copiar: copiarArchivos, ops: [], carpetas: [] };
        var cola = [];
        pasos.forEach(function (p) {
            cola.push({ de: p.clip.ruta, a: p.destino, nombre: p.clip.nombre });
            p.sidecars.forEach(function (s) { cola.push({ de: s.de, a: s.a, nombre: path.basename(s.de) }); });
        });
        var i = 0;
        function siguiente() {
            if (i >= cola.length) return Promise.resolve(registro);
            var op = cola[i++];
            if (progreso) progreso(i, cola.length, op.nombre);
            var hecho = new Promise(function (ok) {
                crearCarpeta(path.dirname(op.a), registro.carpetas);
                ok(copiarArchivos ? copiar(op.de, op.a) : mover(op.de, op.a));
            });
            return hecho.then(function () {
                registro.ops.push({ de: op.de, a: op.a });
                return siguiente();
            }, function (e) {
                e.archivo = op.nombre;   // para poder decir qué archivo falló
                throw e;
            });
        }
        return siguiente().catch(function (e) {
            e.registro = registro;   // lo hecho hasta el error, para poder deshacerlo
            throw e;
        });
    }

    // Vuelve todo a como estaba: borra copias o devuelve los archivos a su lugar
    function deshacer(registro) {
        var ops = registro.ops.slice().reverse(), errores = [];
        var cadena = Promise.resolve();
        ops.forEach(function (op) {
            cadena = cadena.then(function () {
                var hecho = registro.copiar
                    ? promesa(function (cb) { fs.unlink(op.a, cb); })
                    : (crearCarpeta(path.dirname(op.de), []), mover(op.a, op.de));
                return hecho.catch(function (e) { errores.push(path.basename(op.a) + ': ' + e.message); });
            });
        });
        return cadena.then(function () {
            // Borra las carpetas que creó el panel, si quedaron vacías (o solo con .DS_Store)
            registro.carpetas.slice().reverse().forEach(function (dir) {
                try {
                    var resto = fs.readdirSync(dir).filter(function (n) { return n !== '.DS_Store'; });
                    if (resto.length) return;
                    if (fs.existsSync(path.join(dir, '.DS_Store'))) fs.unlinkSync(path.join(dir, '.DS_Store'));
                    fs.rmdirSync(dir);
                } catch (e) { }
            });
            return errores;
        });
    }

    var api = {
        EXT_VIDEO: EXT_VIDEO, CARPETAS_SALIDA: CARPETAS_SALIDA,
        esVideo: esVideo, esVideoProyecto: esVideoProyecto, esLento: esLento, carpetaDestino: carpetaDestino,
        escanear: escanear, plan: plan, ejecutar: ejecutar, deshacer: deshacer
    };
    if (typeof window !== 'undefined') window.Organizador = api;
    else module.exports = api;
})();
