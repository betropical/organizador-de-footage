// Organizador de Footage (Windows) — @betoviedo
// Lee orientación y fps de archivos MP4 / MOV (ISO base media) sin dependencias.
// La orientación sale de la matriz del track de video (tkhd), igual que en la app de Mac.

using System;
using System.IO;
using System.Text;

namespace OrganizadorFootage
{
    public sealed class InfoVideo
    {
        public bool? Vertical;   // null = no se pudo leer
        public double? Fps;
    }

    public static class Mp4Info
    {
        public static InfoVideo Leer(string ruta)
        {
            var info = new InfoVideo();
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
                {
                    long moov, moovFin;
                    if (!BuscarCaja(fs, 0, fs.Length, "moov", out moov, out moovFin)) return info;

                    // Recorre los trak dentro de moov hasta encontrar el de video
                    long pos = moov;
                    while (pos < moovFin)
                    {
                        string tipo; long cuerpo, fin;
                        if (!LeerEncabezado(fs, pos, moovFin, out tipo, out cuerpo, out fin)) break;
                        if (tipo == "trak" && LeerTrak(fs, cuerpo, fin, info)) return info;
                        pos = fin;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return info;
        }

        // Devuelve true si el trak es de video (y completa info)
        static bool LeerTrak(Stream fs, long ini, long fin, InfoVideo info)
        {
            long mdia, mdiaFin;
            if (!BuscarCaja(fs, ini, fin, "mdia", out mdia, out mdiaFin)) return false;

            long hdlr, hdlrFin;
            if (!BuscarCaja(fs, mdia, mdiaFin, "hdlr", out hdlr, out hdlrFin)) return false;
            if (Ascii(LeerBytes(fs, hdlr + 8, 4)) != "vide") return false;

            // tkhd: matriz de transformación y tamaño
            long tkhd, tkhdFin;
            if (BuscarCaja(fs, ini, fin, "tkhd", out tkhd, out tkhdFin))
            {
                int version = LeerBytes(fs, tkhd, 1)[0];
                long m = tkhd + (version == 1 ? 4 + 8 + 8 + 4 + 4 + 8 : 4 + 4 + 4 + 4 + 4 + 4) + 8 + 2 + 2 + 2 + 2;
                byte[] b = LeerBytes(fs, m, 36 + 8);
                double a = Fijo1616(b, 0), bb = Fijo1616(b, 4), c = Fijo1616(b, 12), d = Fijo1616(b, 16);
                double w = Fijo1616(b, 36), h = Fijo1616(b, 40);
                if (w > 0 && h > 0)
                {
                    double ancho = Math.Abs(a * w + c * h);
                    double alto = Math.Abs(bb * w + d * h);
                    info.Vertical = alto > ancho;
                }
            }

            // fps = cantidad de cuadros / duración (mdhd + stts)
            long mdhd, mdhdFin;
            if (BuscarCaja(fs, mdia, mdiaFin, "mdhd", out mdhd, out mdhdFin))
            {
                int version = LeerBytes(fs, mdhd, 1)[0];
                byte[] b = LeerBytes(fs, mdhd + 4 + (version == 1 ? 16 : 8), 4);
                long escala = U32(b, 0);

                long minf, minfFin, stbl, stblFin, stts, sttsFin;
                if (escala > 0 &&
                    BuscarCaja(fs, mdia, mdiaFin, "minf", out minf, out minfFin) &&
                    BuscarCaja(fs, minf, minfFin, "stbl", out stbl, out stblFin) &&
                    BuscarCaja(fs, stbl, stblFin, "stts", out stts, out sttsFin))
                {
                    long entradas = U32(LeerBytes(fs, stts + 4, 4), 0);
                    entradas = Math.Min(entradas, (sttsFin - stts - 8) / 8);
                    byte[] t = LeerBytes(fs, stts + 8, (int)Math.Min(entradas * 8, 8 * 100000));
                    double cuadros = 0, duracion = 0;
                    for (int i = 0; i + 8 <= t.Length; i += 8)
                    {
                        long n = U32(t, i), delta = U32(t, i + 4);
                        cuadros += n;
                        duracion += (double)n * delta;
                    }
                    if (cuadros > 0 && duracion > 0) info.Fps = Math.Round(cuadros / (duracion / escala), 2);
                }
            }
            return true;
        }

        // Busca una caja hija directa por tipo dentro de [ini, fin)
        static bool BuscarCaja(Stream fs, long ini, long fin, string buscado, out long cuerpo, out long finCaja)
        {
            long pos = ini;
            while (pos < fin)
            {
                string tipo;
                if (!LeerEncabezado(fs, pos, fin, out tipo, out cuerpo, out finCaja)) break;
                if (tipo == buscado) return true;
                pos = finCaja;
            }
            cuerpo = finCaja = 0;
            return false;
        }

        static bool LeerEncabezado(Stream fs, long pos, long limite, out string tipo, out long cuerpo, out long fin)
        {
            tipo = null; cuerpo = fin = 0;
            if (pos + 8 > limite) return false;
            byte[] h = LeerBytes(fs, pos, 8);
            if (h.Length < 8) return false;
            long tam = U32(h, 0);
            tipo = Ascii(h, 4, 4);
            cuerpo = pos + 8;
            if (tam == 1)
            {
                byte[] g = LeerBytes(fs, pos + 8, 8);
                if (g.Length < 8) return false;
                tam = (long)((ulong)U32(g, 0) << 32 | (ulong)U32(g, 4));
                cuerpo = pos + 16;
            }
            else if (tam == 0) tam = limite - pos;
            if (tam < cuerpo - pos) return false;
            fin = Math.Min(pos + tam, limite);
            return true;
        }

        static byte[] LeerBytes(Stream fs, long pos, int n)
        {
            if (n <= 0) return new byte[0];
            fs.Position = pos;
            var buf = new byte[n];
            int leidos = 0;
            while (leidos < n)
            {
                int r = fs.Read(buf, leidos, n - leidos);
                if (r <= 0) break;
                leidos += r;
            }
            if (leidos < n) Array.Resize(ref buf, leidos);
            return buf;
        }

        static long U32(byte[] b, int i) { return (long)b[i] << 24 | (long)b[i + 1] << 16 | (long)b[i + 2] << 8 | b[i + 3]; }
        static double Fijo1616(byte[] b, int i) { return unchecked((int)U32(b, i)) / 65536.0; }
        static string Ascii(byte[] b, int i = 0, int n = -1) { return Encoding.ASCII.GetString(b, i, n < 0 ? b.Length - i : n); }
    }
}
