// Organizador de Footage (Premiere) — @betoviedo
// Lado ExtendScript: lee el panel Proyecto, crea bins, mueve clips, importa y arma secuencias.
// ExtendScript es ES3: sin JSON, sin Array.indexOf, sin let/const.
// Cada función recibe un string JSON y devuelve un string JSON ({ error: "..." } si falla).

var OF_TIPO_CLIP = 1, OF_TIPO_BIN = 2;

// ---------- JSON mínimo ----------

function of_json(v) {
    if (v === null || v === undefined) return 'null';
    var t = typeof v;
    if (t === 'number') return isFinite(v) ? String(v) : 'null';
    if (t === 'boolean') return v ? 'true' : 'false';
    if (t === 'string') {
        var s = '"';
        for (var i = 0; i < v.length; i++) {
            var c = v.charAt(i), n = v.charCodeAt(i);
            if (c === '"') s += '\\"';
            else if (c === '\\') s += '\\\\';
            else if (n < 32 || n > 126) s += '\\u' + ('000' + n.toString(16)).slice(-4);
            else s += c;
        }
        return s + '"';
    }
    if (v instanceof Array) {
        var a = [];
        for (var j = 0; j < v.length; j++) a.push(of_json(v[j]));
        return '[' + a.join(',') + ']';
    }
    var o = [];
    for (var k in v) if (v.hasOwnProperty(k)) o.push(of_json(k) + ':' + of_json(v[k]));
    return '{' + o.join(',') + '}';
}

function of_leerArg(s) { return eval('(' + s + ')'); }

function of_ejecutar(fn, arg) {
    try { return of_json(fn(arg === undefined ? null : of_leerArg(arg))); }
    catch (e) { return of_json({ error: String(e) + (e.line ? ' (línea ' + e.line + ')' : '') }); }
}

// ---------- Helpers del proyecto ----------

function of_proyecto() {
    if (!app.project || !app.project.rootItem) throw new Error('No hay un proyecto abierto.');
    return app.project;
}

// Recorre todo el proyecto. fn(item, padre) para cada ítem.
function of_recorrer(bin, fn) {
    for (var i = 0; i < bin.children.numItems; i++) {
        var it = bin.children[i];
        fn(it, bin);
        if (it.type === OF_TIPO_BIN) of_recorrer(it, fn);
    }
}

// nodeId -> ítem (incluye la raíz). Si se pasa padres, también nodeId -> nodeId del bin que lo contiene.
function of_mapa(padres) {
    var raiz = of_proyecto().rootItem, m = {};
    m[raiz.nodeId] = raiz;
    of_recorrer(raiz, function (it, padre) {
        m[it.nodeId] = it;
        if (padres) padres[it.nodeId] = padre.nodeId;
    });
    return m;
}

// Bin hijo con ese nombre; lo crea si no existe. creados acumula los nodeId nuevos.
function of_binHijo(padre, nombre, creados) {
    for (var i = 0; i < padre.children.numItems; i++) {
        var it = padre.children[i];
        if (it.type === OF_TIPO_BIN && it.name === nombre) return it;
    }
    var b = padre.createBin(nombre);
    // En algunas versiones createBin no devuelve el bin: se busca de nuevo
    if (!b || !b.nodeId) {
        for (var j = 0; j < padre.children.numItems; j++) {
            var c = padre.children[j];
            if (c.type === OF_TIPO_BIN && c.name === nombre) { b = c; break; }
        }
    }
    if (!b) throw new Error('No se pudo crear el bin "' + nombre + '".');
    if (creados) creados.push(b.nodeId);
    return b;
}

// Ancho, alto y fps según Premiere (respaldo para formatos que el panel no sabe leer)
function of_infoPremiere(it) {
    var r = { ancho: null, alto: null, fps: null };
    try {
        var xml = it.getProjectMetadata();
        var m = /Column\.Intrinsic\.VideoInfo>\s*(\d+)\s*x\s*(\d+)/.exec(xml);
        if (m) { r.ancho = parseInt(m[1], 10); r.alto = parseInt(m[2], 10); }
    } catch (e) { }
    try {
        var interp = it.getFootageInterpretation();
        if (interp && interp.frameRate > 0) r.fps = Math.round(interp.frameRate * 100) / 100;
    } catch (e2) { }
    return r;
}

// Arma una secuencia con los clips (si la versión de Premiere lo permite)
function of_secuencia(nombre, items, bin) {
    if (!items.length || typeof app.project.createNewSequenceFromClips !== 'function') return null;
    var seq = app.project.createNewSequenceFromClips(nombre, items, bin);
    return seq && seq.projectItem ? seq.projectItem.nodeId : null;
}

// ---------- Funciones que llama el panel ----------

function of_info() {
    return of_ejecutar(function () {
        return {
            version: app.version,
            proyecto: app.project && app.project.name ? app.project.name : null,
            secuencias: typeof app.project.createNewSequenceFromClips === 'function'
        };
    });
}

// Lista los clips de video del proyecto (sin secuencias, sin offline)
function of_listarClips() {
    return of_ejecutar(function () {
        var clips = [];
        of_recorrer(of_proyecto().rootItem, function (it, padre) {
            if (it.type !== OF_TIPO_CLIP) return;
            try { if (it.isSequence()) return; } catch (e) { }
            try { if (it.isOffline()) return; } catch (e2) { }
            var ruta = '';
            try { ruta = it.getMediaPath(); } catch (e3) { }
            if (!ruta) return;
            var p = of_infoPremiere(it);
            clips.push({ id: it.nodeId, nombre: it.name, ruta: ruta, bin: padre.name, binId: padre.nodeId,
                         ancho: p.ancho, alto: p.alto, fps: p.fps });
        });
        return clips;
    });
}

// Nombres de los bins en la raíz del proyecto (para avisar si ya existe uno con el nombre de la carpeta)
function of_binsRaiz() {
    return of_ejecutar(function () {
        var raiz = of_proyecto().rootItem, nombres = [];
        for (var i = 0; i < raiz.children.numItems; i++) {
            if (raiz.children[i].type === OF_TIPO_BIN) nombres.push(raiz.children[i].name);
        }
        return nombres;
    });
}

// Rutas de todos los archivos del proyecto (para no importar dos veces lo mismo)
function of_rutasProyecto() {
    return of_ejecutar(function () {
        var rutas = [];
        of_recorrer(of_proyecto().rootItem, function (it) {
            if (it.type !== OF_TIPO_CLIP) return;
            try { var r = it.getMediaPath(); if (r) rutas.push(r); } catch (e) { }
        });
        return rutas;
    });
}

// arg: { grupos: [{ bin: "Verticales", ids: [...], prefijo: "Reels_" }], secuencias: bool }
// Devuelve lo necesario para deshacer.
function of_ordenar(arg) {
    return of_ejecutar(function (a) {
        var raiz = of_proyecto().rootItem, padres = {}, mapa = of_mapa(padres);
        var r = { movidos: 0, cambios: [], binsCreados: [], secuencias: [] };
        for (var g = 0; g < a.grupos.length; g++) {
            var grupo = a.grupos[g];
            if (!grupo.ids.length) continue;
            var bin = of_binHijo(raiz, grupo.bin, r.binsCreados);
            var items = [];
            for (var i = 0; i < grupo.ids.length; i++) {
                var it = mapa[grupo.ids[i]];
                if (!it) continue;
                var nombreViejo = it.name, padreViejo = padres[it.nodeId] || raiz.nodeId;
                if (padreViejo !== bin.nodeId) it.moveBin(bin);
                if (grupo.prefijo && nombreViejo.indexOf(grupo.prefijo) !== 0) it.name = grupo.prefijo + nombreViejo;
                r.cambios.push({ id: it.nodeId, binId: padreViejo, nombre: nombreViejo });
                items.push(it);
                r.movidos++;
            }
            if (a.secuencias) {
                var s = of_secuencia(grupo.bin, items, bin);
                if (s) r.secuencias.push(s);
            }
        }
        return r;
    }, arg);
}

// arg: lo que devolvió of_ordenar o of_importar
function of_deshacer(arg) {
    return of_ejecutar(function (a) {
        var mapa = of_mapa(), r = { restaurados: 0 };
        var cambios = a.cambios || [];
        for (var i = 0; i < cambios.length; i++) {
            var c = cambios[i], it = mapa[c.id], destino = mapa[c.binId];
            if (!it) continue;
            if (destino) it.moveBin(destino);
            if (c.nombre && it.name !== c.nombre) it.name = c.nombre;
            r.restaurados++;
        }
        // Secuencias creadas por el panel
        var secs = a.secuencias || [];
        for (var s = 0; s < secs.length; s++) {
            for (var k = app.project.sequences.numSequences - 1; k >= 0; k--) {
                var seq = app.project.sequences[k];
                if (seq.projectItem && seq.projectItem.nodeId === secs[s]) { app.project.deleteSequence(seq); break; }
            }
        }
        // Ítems importados por el panel (modo carpeta)
        mapa = of_mapa();
        var imp = a.importados || [];
        for (var m = 0; m < imp.length; m++) {
            var x = mapa[imp[m]];
            if (x) try { x.deleteBin(); } catch (e) { }
        }
        // Bins creados por el panel, solo si quedaron vacíos
        mapa = of_mapa();
        var bins = a.binsCreados || [];
        for (var b = bins.length - 1; b >= 0; b--) {
            var bin = mapa[bins[b]];
            if (bin && bin.children.numItems === 0) bin.deleteBin();
        }
        return r;
    }, arg);
}

// arg: { padre: "Footage" | "", grupos: [{ bin: "Verticales", rutas: [...], prefijo: "" }], secuencias: bool }
// El prefijo se aplica solo al nombre en el proyecto (en "Solo importar" los archivos no se renombran).
function of_importar(arg) {
    return of_ejecutar(function (a) {
        var proyecto = of_proyecto(), r = { importados: [], binsCreados: [], secuencias: [], cambios: [] };
        var base = a.padre ? of_binHijo(proyecto.rootItem, a.padre, r.binsCreados) : proyecto.rootItem;
        for (var g = 0; g < a.grupos.length; g++) {
            var grupo = a.grupos[g];
            if (!grupo.rutas.length) continue;
            var bin = of_binHijo(base, grupo.bin, r.binsCreados);
            var antes = {};
            for (var i = 0; i < bin.children.numItems; i++) antes[bin.children[i].nodeId] = true;
            proyecto.importFiles(grupo.rutas, true, bin, false);
            var nuevos = [];
            for (var j = 0; j < bin.children.numItems; j++) {
                var it = bin.children[j];
                if (!antes[it.nodeId] && it.type === OF_TIPO_CLIP) {
                    if (grupo.prefijo && it.name.indexOf(grupo.prefijo) !== 0) it.name = grupo.prefijo + it.name;
                    nuevos.push(it);
                    r.importados.push(it.nodeId);
                }
            }
            if (a.secuencias) {
                var s = of_secuencia(grupo.bin, nuevos, bin);
                if (s) r.secuencias.push(s);
            }
        }
        return r;
    }, arg);
}
