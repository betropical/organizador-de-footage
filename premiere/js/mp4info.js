// Organizador de Footage (Premiere) — @betoviedo
// Lee orientación y fps de archivos MP4 / MOV (ISO base media) sin dependencias.
// Port de windows/Mp4Info.cs: la orientación sale de la matriz del track de video (tkhd).

(function () {
    var nodo = (typeof window !== 'undefined' && window.cep_node) ? window.cep_node : null;
    var fs = nodo ? nodo.require('fs') : require('fs');
    var Buffer = nodo && nodo.Buffer ? nodo.Buffer : nodo ? nodo.require('buffer').Buffer : global.Buffer;

    // Devuelve { vertical: true|false|null, fps: número|null }  (null = no se pudo leer)
    function leer(ruta) {
        var info = { vertical: null, fps: null };
        var fd;
        try {
            fd = fs.openSync(ruta, 'r');
            var largo = fs.fstatSync(fd).size;
            var moov = buscarCaja(fd, 0, largo, 'moov');
            if (!moov) return info;

            // Recorre los trak dentro de moov hasta encontrar el de video
            var pos = moov.cuerpo;
            while (pos < moov.fin) {
                var c = leerEncabezado(fd, pos, moov.fin);
                if (!c) break;
                if (c.tipo === 'trak' && leerTrak(fd, c.cuerpo, c.fin, info)) return info;
                pos = c.fin;
            }
        } catch (e) {
            // archivo ilegible o formato raro: se devuelve lo que haya
        } finally {
            if (fd !== undefined) try { fs.closeSync(fd); } catch (e) { }
        }
        return info;
    }

    // Devuelve true si el trak es de video (y completa info)
    function leerTrak(fd, ini, fin, info) {
        var mdia = buscarCaja(fd, ini, fin, 'mdia');
        if (!mdia) return false;
        var hdlr = buscarCaja(fd, mdia.cuerpo, mdia.fin, 'hdlr');
        if (!hdlr || ascii(leerBytes(fd, hdlr.cuerpo + 8, 4)) !== 'vide') return false;

        // tkhd: matriz de transformación y tamaño
        var tkhd = buscarCaja(fd, ini, fin, 'tkhd');
        if (tkhd) {
            var version = leerBytes(fd, tkhd.cuerpo, 1)[0];
            var m = tkhd.cuerpo + (version === 1 ? 4 + 8 + 8 + 4 + 4 + 8 : 4 + 4 + 4 + 4 + 4 + 4) + 8 + 2 + 2 + 2 + 2;
            var b = leerBytes(fd, m, 36 + 8);
            if (b.length >= 44) {
                var a = fijo1616(b, 0), bb = fijo1616(b, 4), c = fijo1616(b, 12), d = fijo1616(b, 16);
                var w = fijo1616(b, 36), h = fijo1616(b, 40);
                if (w > 0 && h > 0) {
                    var ancho = Math.abs(a * w + c * h);
                    var alto = Math.abs(bb * w + d * h);
                    info.vertical = alto > ancho;
                }
            }
        }

        // fps = cantidad de cuadros / duración (mdhd + stts)
        var mdhd = buscarCaja(fd, mdia.cuerpo, mdia.fin, 'mdhd');
        if (mdhd) {
            var vMdhd = leerBytes(fd, mdhd.cuerpo, 1)[0];
            var escala = u32(leerBytes(fd, mdhd.cuerpo + 4 + (vMdhd === 1 ? 16 : 8), 4), 0);
            var minf = escala > 0 && buscarCaja(fd, mdia.cuerpo, mdia.fin, 'minf');
            var stbl = minf && buscarCaja(fd, minf.cuerpo, minf.fin, 'stbl');
            var stts = stbl && buscarCaja(fd, stbl.cuerpo, stbl.fin, 'stts');
            if (stts) {
                var entradas = u32(leerBytes(fd, stts.cuerpo + 4, 4), 0);
                entradas = Math.min(entradas, Math.floor((stts.fin - stts.cuerpo - 8) / 8));
                var t = leerBytes(fd, stts.cuerpo + 8, Math.min(entradas * 8, 8 * 100000));
                var cuadros = 0, duracion = 0;
                for (var i = 0; i + 8 <= t.length; i += 8) {
                    var n = u32(t, i), delta = u32(t, i + 4);
                    cuadros += n;
                    duracion += n * delta;
                }
                if (cuadros > 0 && duracion > 0) info.fps = Math.round(cuadros / (duracion / escala) * 100) / 100;
            }
        }
        return true;
    }

    // Busca una caja hija directa por tipo dentro de [ini, fin)
    function buscarCaja(fd, ini, fin, buscado) {
        var pos = ini;
        while (pos < fin) {
            var c = leerEncabezado(fd, pos, fin);
            if (!c) break;
            if (c.tipo === buscado) return c;
            pos = c.fin;
        }
        return null;
    }

    function leerEncabezado(fd, pos, limite) {
        if (pos + 8 > limite) return null;
        var h = leerBytes(fd, pos, 8);
        if (h.length < 8) return null;
        var tam = u32(h, 0);
        var tipo = ascii(h.slice(4, 8));
        var cuerpo = pos + 8;
        if (tam === 1) {
            var g = leerBytes(fd, pos + 8, 8);
            if (g.length < 8) return null;
            tam = u32(g, 0) * 4294967296 + u32(g, 4);
            cuerpo = pos + 16;
        } else if (tam === 0) tam = limite - pos;
        if (tam < cuerpo - pos) return null;
        return { tipo: tipo, cuerpo: cuerpo, fin: Math.min(pos + tam, limite) };
    }

    function leerBytes(fd, pos, n) {
        if (n <= 0) return Buffer.alloc(0);
        var buf = Buffer.alloc(n);
        var leidos = 0;
        while (leidos < n) {
            var r = fs.readSync(fd, buf, leidos, n - leidos, pos + leidos);
            if (r <= 0) break;
            leidos += r;
        }
        return leidos < n ? buf.slice(0, leidos) : buf;
    }

    function u32(b, i) { return b[i] * 16777216 + (b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]); }
    function fijo1616(b, i) { return (b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]) / 65536; }
    function ascii(b) { return b.toString('latin1'); }

    var api = { leer: leer };
    if (typeof window !== 'undefined') window.Mp4Info = api;
    else module.exports = api;
})();
