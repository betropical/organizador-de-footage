// Organizador de Footage (Premiere) — @betoviedo
// Interfaz del panel. Habla con Premiere por evalScript (jsx/host.jsx) y con el disco por Node (organizador.js).

(function () {
    var path = (window.cep_node ? window.cep_node.require : window.require)('path');
    var O = window.Organizador, M = window.Mp4Info;
    var $ = function (id) { return document.getElementById(id); };

    var estado = { modo: 'proyecto', clips: [], raiz: null, destino: null, deshacer: null, ocupado: false };

    // ---------- Comunicación con Premiere ----------

    function aLiteral(s) {
        // String JS seguro para ExtendScript: sin caracteres fuera de ASCII
        return JSON.stringify(s).replace(/[\u007f-\uffff]/g, function (c) {
            return '\\u' + ('000' + c.charCodeAt(0).toString(16)).slice(-4);
        });
    }

    function llamar(fn, arg) {
        return new Promise(function (ok, mal) {
            var script = fn + '(' + (arg === undefined ? '' : aLiteral(JSON.stringify(arg))) + ')';
            window.__adobe_cep__.evalScript(script, function (r) {
                if (r === 'EvalScript error.') return mal(new Error('Premiere no pudo ejecutar ' + fn + '.'));
                var o;
                try { o = JSON.parse(r); } catch (e) { return mal(new Error(String(r))); }
                if (o && o.error) mal(new Error(o.error)); else ok(o);
            });
        });
    }

    // ---------- Preferencias ----------

    var prefs = {
        leer: function (k, def) { try { var v = localStorage.getItem('of.' + k); return v === null ? def : JSON.parse(v); } catch (e) { return def; } },
        guardar: function (k, v) { try { localStorage.setItem('of.' + k, JSON.stringify(v)); } catch (e) { } }
    };

    function opciones() {
        return {
            slowmo: $('slowmo').checked,
            umbral: parseInt($('umbral').value, 10) || 45,
            prefijoV: $('prefijoV').value,
            prefijoH: $('prefijoH').value
        };
    }

    function cargarPrefs() {
        $('slowmo').checked = prefs.leer('slowmo', false);
        $('umbral').value = prefs.leer('umbral', 45);
        $('prefijoV').value = prefs.leer('prefijoV', '');
        $('prefijoH').value = prefs.leer('prefijoH', '');
        $('secuencias').checked = prefs.leer('secuencias', false);
        $('importar').checked = prefs.leer('importar', true);
        $('subcarpetas').checked = prefs.leer('subcarpetas', true);
        $('subcarpetas').addEventListener('change', function () {
            prefs.guardar('subcarpetas', $('subcarpetas').checked);
            if (estado.raiz && !estado.ocupado) abrirCarpeta(estado.raiz);
        });
        marcarRadio('accion', prefs.leer('accion', 'importar'));
        ['slowmo', 'secuencias', 'importar'].forEach(function (id) {
            $(id).addEventListener('change', function () { prefs.guardar(id, $(id).checked); actualizar(); });
        });
        ['umbral', 'prefijoV', 'prefijoH'].forEach(function (id) {
            $(id).addEventListener('input', function () { prefs.guardar(id, id === 'umbral' ? opciones().umbral : $(id).value); actualizar(); });
        });
    }

    function marcarRadio(nombre, valor) {
        var r = document.querySelector('input[name=' + nombre + '][value=' + valor + ']');
        if (r) r.checked = true;
    }
    function valorRadio(nombre) { return document.querySelector('input[name=' + nombre + ']:checked').value; }

    // ---------- Estado visible ----------

    function mostrarEstado(texto, tipo, conDeshacer) {
        var e = $('estado');
        e.className = 'estado' + (tipo ? ' ' + tipo : '');
        $('estadoTexto').textContent = texto;
        $('deshacer').classList.toggle('oculto', !conDeshacer);
    }
    function ocultarEstado() { $('estado').classList.add('oculto'); }

    function ocupar(si) {
        estado.ocupado = si;
        Array.prototype.forEach.call(document.querySelectorAll('button'), function (b) { b.disabled = si; });
    }

    // ---------- Lista y resumen ----------

    function actualizar() {
        $('filaUmbral').classList.toggle('oculto', !$('slowmo').checked);
        $('opcionesDisco').classList.toggle('oculto', valorRadio('accion') === 'importar');

        // Aviso si ya hay un bin con el nombre de la carpeta (solo si se va a importar algo)
        var vaAImportar = valorRadio('accion') === 'importar' || $('importar').checked;
        var repetido = estado.modo === 'carpeta' && estado.clips.length > 0 && vaAImportar && hayBinRepetido();
        $('avisoBin').classList.toggle('oculto', !repetido);
        if (repetido) {
            $('avisoBinTexto').textContent = '⚠ Ya hay un bin "' + path.basename(estado.raiz) + '" en el proyecto.';
            $('binNuevoTexto').textContent = 'Crear uno nuevo: "' + binLibre() + '"';
        }
        var op = opciones(), clips = estado.clips;
        var hayClips = clips.length > 0;
        $('resultado').classList.toggle('oculto', !hayClips && !estado.analizado);
        actualizarBoton();
        if (!hayClips) {
            $('fichas').innerHTML = '';
            $('lista').innerHTML = '';
            $('nota').textContent = estado.analizado ? 'No se encontraron clips de video.' : '';
            return;
        }

        var v = 0, lentos = 0, dudosos = 0, importados = 0;
        clips.forEach(function (c) {
            if (c.vertical) v++;
            if (O.esLento(c, op)) lentos++;
            if (c.dudoso) dudosos++;
            if (c.yaImportado) importados++;
        });
        var fichas = [ficha(clips.length, 'clips'), ficha(v, 'verticales'), ficha(clips.length - v, 'horizontales')];
        if (op.slowmo) fichas.push(ficha(lentos, 'slow-mo'));
        if (importados && estado.modo === 'carpeta') fichas.push(ficha(importados, 'ya en el proyecto'));
        $('fichas').innerHTML = fichas.join('');

        var notas = [];
        if (dudosos) notas.push(dudosos + ' sin datos de orientación (marcados en rojo, van a Horizontales).');
        if (importados && estado.modo === 'carpeta') {
            var accion = valorRadio('accion');
            if (accion === 'importar') notas.push(importados + ' ya están en el proyecto y se van a saltear.');
            else if (accion === 'mover' && enWindows) notas.push('⚠ ' + importados + ' ya están en el proyecto y Windows no permite mover archivos que Premiere tiene abiertos. Usá Copiar o Solo importar.');
            else if (accion === 'mover') notas.push('⚠ ' + importados + ' ya están en el proyecto: si los movés, Premiere los va a mostrar como offline. Usá Copiar o Solo importar.');
        }
        notas.push('Hacé clic en un clip para cambiar su orientación.');
        $('nota').textContent = notas.join(' ');

        var lista = $('lista');
        lista.innerHTML = '';
        clips.forEach(function (c, i) {
            var li = document.createElement('li');
            li.className = (c.dudoso ? 'dudoso ' : '') + (c.yaImportado && estado.modo === 'carpeta' ? 'importado' : '');
            var lento = op.slowmo && O.esLento(c, op);
            li.innerHTML = '<span class="icono ' + (c.vertical ? 'v' : 'h') + '"></span>' +
                '<span class="nombre"></span>' +
                '<span class="destino' + (lento ? ' lento' : '') + '"></span>';
            li.querySelector('.nombre').textContent = c.nombre;
            li.querySelector('.destino').textContent = (c.fps ? c.fps + ' fps · ' : '') +
                (c.yaImportado && estado.modo === 'carpeta' && valorRadio('accion') === 'importar' ? 'ya importado' : O.carpetaDestino(c, op));
            li.title = c.ruta;
            li.addEventListener('click', function () {
                if (estado.ocupado) return;
                clips[i].vertical = !clips[i].vertical;
                clips[i].dudoso = false;
                actualizar();
            });
            lista.appendChild(li);
        });
    }

    // Botón Procesar: se habilita cuando hay clips y dice qué va a hacer
    function actualizarBoton() {
        var n = estado.clips.length, texto;
        var soloImportar = estado.modo === 'carpeta' && valorRadio('accion') === 'importar';
        if (soloImportar && n) n = clipsAImportar().length;
        $('ejecutar').disabled = estado.ocupado || n === 0 || bloqueaMoverEnWindows();
        if (bloqueaMoverEnWindows()) texto = 'Windows no permite mover clips abiertos en Premiere: elegí Copiar o Solo importar.';
        else if (!n && estado.clips.length) texto = 'Todos los clips ya están en el proyecto.';
        else if (!n) texto = estado.modo === 'proyecto' ? 'Primero analizá el proyecto.' : 'Primero elegí una carpeta.';
        else if (estado.modo === 'proyecto') texto = 'Ordena ' + n + ' clips en bins';
        else texto = {
            importar: 'Importa ' + n + ' clips divididos en bins',
            mover: 'Mueve ' + n + ' clips' + ($('importar').checked ? ' y los importa' : ''),
            copiar: 'Copia ' + n + ' clips' + ($('importar').checked ? ' y los importa' : '')
        }[valorRadio('accion')];
        $('resumenAccion').textContent = texto;
    }

    function ficha(n, texto) { return '<span class="ficha"><b>' + n + '</b> ' + texto + '</span>'; }

    function pausa() { return new Promise(function (ok) { setTimeout(ok, 0); }); }

    // Orientación y fps de cada clip: primero el archivo; si no se puede, lo que diga Premiere
    async function leerMetadatos(clips, progreso) {
        for (var i = 0; i < clips.length; i++) {
            var c = clips[i];
            var info = O.esVideo(c.ruta) ? M.leer(c.ruta) : { vertical: null, fps: null };
            c.vertical = info.vertical;
            c.fps = info.fps || c.fps || null;
            if (c.vertical === null && c.ancho && c.alto) c.vertical = c.alto > c.ancho;
            c.dudoso = c.vertical === null;
            c.vertical = !!c.vertical;
            if (i % 10 === 0) { progreso(i + 1, clips.length); await pausa(); }
        }
    }

    // ---------- Modo Proyecto ----------

    async function analizarProyecto() {
        ocupar(true);
        estado.deshacer = null;
        mostrarEstado('Leyendo el proyecto…', 'trabajando');
        try {
            var clips = (await llamar('of_listarClips')).filter(function (c) { return O.esVideoProyecto(c.ruta); });
            await leerMetadatos(clips, function (i, n) { mostrarEstado('Leyendo clips ' + i + ' de ' + n + '…', 'trabajando'); });
            estado.clips = clips;
            estado.analizado = true;
            ocultarEstado();
        } catch (e) {
            mostrarEstado(e.message, 'error');
        }
        ocupar(false);
        actualizar();
    }

    async function ordenarProyecto() {
        var op = opciones(), grupos = {};
        estado.clips.forEach(function (c) {
            var bin = O.carpetaDestino(c, op);
            if (!grupos[bin]) grupos[bin] = { bin: bin, ids: [], prefijo: c.vertical ? op.prefijoV : op.prefijoH };
            grupos[bin].ids.push(c.id);
        });
        ocupar(true);
        mostrarEstado('Ordenando…', 'trabajando');
        try {
            var r = await llamar('of_ordenar', {
                grupos: Object.keys(grupos).map(function (k) { return grupos[k]; }),
                secuencias: $('secuencias').checked
            });
            estado.deshacer = { tipo: 'proyecto', premiere: r };
            estado.clips = [];
            estado.analizado = false;
            var texto = r.movidos + ' clips ordenados en bins.';
            if (r.secuencias.length) texto += ' ' + r.secuencias.length + ' secuencias creadas.';
            mostrarEstado(texto, 'ok', true);
        } catch (e) {
            mostrarEstado(e.message, 'error');
        }
        ocupar(false);
        actualizar();
    }

    // ---------- Modo Carpeta ----------

    // Misma ruta aunque cambien barras, mayúsculas o la forma de escribir los acentos
    function claveRuta(r) { return String(r).normalize('NFC').replace(/\\/g, '/').toLowerCase(); }

    // Marca los clips que ya están en el proyecto abierto
    async function marcarImportados(clips) {
        var enProyecto = {};
        try {
            (await llamar('of_rutasProyecto')).forEach(function (r) { enProyecto[claveRuta(r)] = true; });
        } catch (e) { }   // sin proyecto abierto: no hay nada importado
        clips.forEach(function (c) { c.yaImportado = !!enProyecto[claveRuta(c.ruta)]; });
    }

    // Bin donde se importa la carpeta: su nombre, o "Nombre 2", "Nombre 3"... si ya existe y se eligió crear uno nuevo
    function binLibre() {
        var base = path.basename(estado.raiz), bins = estado.binsRaiz || [];
        for (var n = 2; bins.indexOf(base + ' ' + n) >= 0; n++) { }
        return base + ' ' + n;
    }
    function hayBinRepetido() { return !!estado.raiz && (estado.binsRaiz || []).indexOf(path.basename(estado.raiz)) >= 0; }
    function nombreBinPadre() {
        return hayBinRepetido() && valorRadio('binExistente') === 'nuevo' ? binLibre() : path.basename(estado.raiz);
    }

    var enWindows = navigator.platform.indexOf('Win') === 0;

    // Windows no deja mover archivos que Premiere tiene abiertos
    function bloqueaMoverEnWindows() {
        return enWindows && estado.modo === 'carpeta' && valorRadio('accion') === 'mover' &&
            estado.clips.some(function (c) { return c.yaImportado; });
    }

    // Traduce los errores de archivo más comunes a algo entendible
    function mensajeError(e) {
        var cual = e.archivo ? ' (' + e.archivo + ')' : '';
        if (e.code === 'EBUSY' || (enWindows && (e.code === 'EPERM' || e.code === 'EACCES')))
            return 'Windows no permite mover un archivo que está abierto en Premiere u otro programa' + cual +
                '. Usá Copiar o Solo importar, o cerrá el programa que lo usa.';
        if (e.code === 'EACCES' || e.code === 'EPERM') return 'No hay permiso para escribir en esa carpeta' + cual + '.';
        if (e.code === 'ENOSPC') return 'No hay espacio suficiente en el disco de destino' + cual + '.';
        return e.message + cual;
    }

    function clipsAImportar() { return estado.clips.filter(function (c) { return !c.yaImportado; }); }

    function elegirCarpetaDialogo(titulo) {
        var fsCep = window.cep.fs;
        var r = fsCep.showOpenDialogEx ? fsCep.showOpenDialogEx(false, true, titulo, '') : fsCep.showOpenDialog(false, true, titulo, '');
        if (!r || r.err !== 0 || !r.data || !r.data.length) return null;
        // Normalmente es una ruta común (C:\... o /Users/...). Solo se decodifica si viene como file://,
        // así una carpeta llamada "100%" no se rompe.
        var ruta = String(r.data[0]);
        if (ruta.indexOf('file://') === 0) {
            ruta = decodeURIComponent(ruta.replace(/^file:\/\/(localhost)?/, ''));
            if (/^\/[A-Za-z]:\//.test(ruta)) ruta = ruta.slice(1);   // Windows: /C:/... -> C:/...
        }
        return ruta;
    }

    async function abrirCarpeta(ruta) {
        if (!ruta) return;
        estado.raiz = ruta;
        estado.deshacer = null;
        $('rutaCarpeta').textContent = ruta;
        ocupar(true);
        mostrarEstado('Buscando clips…', 'trabajando');
        await pausa();
        try {
            var clips = O.escanear(ruta, $('subcarpetas').checked);
            await leerMetadatos(clips, function (i, n) { mostrarEstado('Leyendo clips ' + i + ' de ' + n + '…', 'trabajando'); });
            await marcarImportados(clips);
            try { estado.binsRaiz = await llamar('of_binsRaiz'); } catch (e) { estado.binsRaiz = []; }
            // Si algún clip ya está importado, seguramente es la misma carpeta: se propone sumar al bin existente
            marcarRadio('binExistente', clips.some(function (c) { return c.yaImportado; }) ? 'agregar' : 'nuevo');
            estado.clips = clips;
            estado.analizado = true;
            ocultarEstado();
        } catch (e) {
            mostrarEstado(e.message, 'error');
        }
        ocupar(false);
        actualizar();
    }

    // Importa la carpeta tal como está y la divide en bins (no toca el disco)
    async function importarCarpeta() {
        var op = opciones(), grupos = {}, nuevos = clipsAImportar();
        nuevos.forEach(function (c) {
            var bin = O.carpetaDestino(c, op);
            if (!grupos[bin]) grupos[bin] = { bin: bin, rutas: [], prefijo: c.vertical ? op.prefijoV : op.prefijoH };
            grupos[bin].rutas.push(c.ruta);
        });
        ocupar(true);
        mostrarEstado('Importando ' + nuevos.length + ' clips…', 'trabajando');
        try {
            var r = await llamar('of_importar', {
                padre: nombreBinPadre(),
                grupos: Object.keys(grupos).map(function (k) { return grupos[k]; }),
                secuencias: $('secuencias').checked
            });
            estado.deshacer = { tipo: 'carpeta', registro: null, premiere: r };
            estado.clips = [];
            estado.analizado = false;
            var texto = r.importados.length + ' clips importados y divididos en bins.';
            var salteados = estado.clips.length - nuevos.length;
            if (salteados) texto += ' ' + salteados + ' ya estaban en el proyecto.';
            if (r.secuencias.length) texto += ' ' + r.secuencias.length + ' secuencias creadas.';
            mostrarEstado(texto, 'ok', true);
        } catch (e) {
            mostrarEstado(e.message, 'error');
        }
        ocupar(false);
        actualizar();
    }

    async function organizarCarpeta() {
        if (valorRadio('accion') === 'importar') return importarCarpeta();
        var op = opciones();
        var copiar = valorRadio('accion') === 'copiar';
        var raizDestino = valorRadio('destino') === 'otra' ? estado.destino : estado.raiz;
        if (!raizDestino) return mostrarEstado('Elegí la carpeta de destino.', 'error');

        var pasos = O.plan(estado.clips, op, raizDestino);
        var verbo = copiar ? 'Copiando' : 'Moviendo';
        ocupar(true);
        var registro = null, importado = null;
        try {
            registro = await O.ejecutar(pasos, copiar, function (i, n, nombre) {
                mostrarEstado(verbo + ' ' + i + ' de ' + n + ': ' + nombre, 'trabajando');
            });
            if ($('importar').checked) {
                mostrarEstado('Importando a Premiere…', 'trabajando');
                var grupos = {};
                pasos.forEach(function (p) {
                    if (!grupos[p.carpeta]) grupos[p.carpeta] = { bin: p.carpeta, rutas: [] };
                    grupos[p.carpeta].rutas.push(p.destino);
                });
                importado = await llamar('of_importar', {
                    padre: nombreBinPadre(),
                    grupos: Object.keys(grupos).map(function (k) { return grupos[k]; }),
                    secuencias: $('secuencias').checked
                });
            }
            estado.deshacer = { tipo: 'carpeta', registro: registro, premiere: importado };
            estado.clips = [];
            estado.analizado = false;
            var texto = pasos.length + ' clips ' + (copiar ? 'copiados' : 'movidos') + (importado ? ' e importados' : '') + '.';
            if (importado && importado.secuencias.length) texto += ' ' + importado.secuencias.length + ' secuencias creadas.';
            mostrarEstado(texto, 'ok', true);
        } catch (e) {
            // Si falló a mitad de camino, se ofrece deshacer lo que sí se hizo
            registro = registro || e.registro;
            if (registro && registro.ops.length) estado.deshacer = { tipo: 'carpeta', registro: registro, premiere: importado };
            mostrarEstado(mensajeError(e) + (estado.deshacer ? ' Podés deshacer lo que alcanzó a hacerse.' : ''), 'error', !!estado.deshacer);
        }
        ocupar(false);
        actualizar();
    }

    // ---------- Deshacer ----------

    async function deshacer() {
        var d = estado.deshacer;
        if (!d) return;
        ocupar(true);
        mostrarEstado('Deshaciendo…', 'trabajando');
        try {
            if (d.premiere) await llamar('of_deshacer', d.premiere);
            var errores = d.registro ? await O.deshacer(d.registro) : [];
            estado.deshacer = null;
            if (errores.length) mostrarEstado('Deshecho con errores: ' + errores.join(' · '), 'error');
            else mostrarEstado('Listo, quedó todo como estaba.', 'ok');
        } catch (e) {
            mostrarEstado(e.message, 'error', true);
        }
        ocupar(false);
        if (d.tipo === 'carpeta' && estado.raiz) abrirCarpeta(estado.raiz).then(function () { mostrarEstado('Listo, quedó todo como estaba.', 'ok'); });
    }

    // ---------- Arranque ----------

    function cambiarModo(modo) {
        estado.modo = modo;
        estado.clips = [];
        estado.analizado = false;
        Array.prototype.forEach.call(document.querySelectorAll('.pestanas button'), function (b) {
            b.classList.toggle('activa', b.getAttribute('data-modo') === modo);
        });
        $('modo-proyecto').classList.toggle('oculto', modo !== 'proyecto');
        $('modo-carpeta').classList.toggle('oculto', modo !== 'carpeta');
        if (!estado.deshacer) ocultarEstado();
        prefs.guardar('modo', modo);
        if (modo === 'carpeta' && estado.raiz) abrirCarpeta(estado.raiz);
        actualizar();
    }

    function aplicarTema() {
        try {
            var c = JSON.parse(window.__adobe_cep__.getHostEnvironment()).appSkinInfo.panelBackgroundColor.color;
            document.documentElement.style.setProperty('--fondo', 'rgb(' + Math.round(c.red) + ',' + Math.round(c.green) + ',' + Math.round(c.blue) + ')');
        } catch (e) { }
    }

    function rutaSoltada(ev) {
        var dt = ev.dataTransfer;
        if (dt.files && dt.files.length && dt.files[0].path) return dt.files[0].path;
        var uri = (dt.getData('text/uri-list') || '').split('\n')[0].trim();
        if (uri.indexOf('file://') === 0) {
            var p = decodeURIComponent(uri.replace(/^file:\/\/(localhost)?/, ''));
            return /^\/[A-Za-z]:\//.test(p) ? p.slice(1) : p;   // Windows: /C:/... -> C:/...
        }
        return null;
    }

    function iniciar() {
        aplicarTema();
        cargarPrefs();

        Array.prototype.forEach.call(document.querySelectorAll('.pestanas button'), function (b) {
            b.addEventListener('click', function () { if (!estado.ocupado) cambiarModo(b.getAttribute('data-modo')); });
        });
        $('analizarProyecto').addEventListener('click', analizarProyecto);
        $('elegirCarpeta').addEventListener('click', function () { abrirCarpeta(elegirCarpetaDialogo('Elegí la carpeta o tarjeta con los clips')); });
        $('ejecutar').addEventListener('click', function () { if (estado.modo === 'proyecto') ordenarProyecto(); else organizarCarpeta(); });
        $('deshacer').addEventListener('click', deshacer);

        Array.prototype.forEach.call(document.querySelectorAll('input[name=accion]'), function (r) {
            r.addEventListener('change', function () { prefs.guardar('accion', valorRadio('accion')); actualizar(); });
        });
        Array.prototype.forEach.call(document.querySelectorAll('input[name=destino]'), function (r) {
            r.addEventListener('change', function () {
                if (valorRadio('destino') === 'otra') {
                    var d = elegirCarpetaDialogo('Elegí dónde crear las carpetas Verticales y Horizontales');
                    if (d) estado.destino = d;
                    else marcarRadio('destino', 'misma');
                }
                $('rutaDestino').textContent = valorRadio('destino') === 'otra' ? estado.destino : '';
            });
        });

        var zona = $('soltar');
        zona.addEventListener('dragover', function (e) { e.preventDefault(); zona.classList.add('encima'); });
        zona.addEventListener('dragleave', function () { zona.classList.remove('encima'); });
        zona.addEventListener('drop', function (e) {
            e.preventDefault();
            zona.classList.remove('encima');
            var ruta = rutaSoltada(e);
            if (ruta) abrirCarpeta(ruta);
            else mostrarEstado('No se pudo leer la carpeta soltada. Usá "Elegir carpeta…".', 'error');
        });
        // Evita que Premiere abra el archivo si se suelta fuera de la zona
        document.addEventListener('dragover', function (e) { e.preventDefault(); });
        document.addEventListener('drop', function (e) { e.preventDefault(); });

        Array.prototype.forEach.call(document.querySelectorAll('footer a'), function (a) {
            a.addEventListener('click', function (e) { e.preventDefault(); window.cep.util.openURLInDefaultBrowser(a.getAttribute('data-url')); });
        });

        cambiarModo(prefs.leer('modo', 'proyecto'));
    }

    iniciar();
})();
