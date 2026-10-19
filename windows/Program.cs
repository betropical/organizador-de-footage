// Organizador de Footage (Windows) — @betoviedo
// Separa clips en Verticales / Horizontales (y SlowMo / RealTime).
// Misma lógica que la app de Mac: vista previa, nunca pisa archivos, sidecars incluidos.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OrganizadorFootage
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Para instalaciones automáticas: agrega / quita el acceso de "Enviar a" y sale
            if (args.Length > 0 && args[0] == "/agregar-enviar-a") { EnviarA.Agregar(); return; }
            if (args.Length > 0 && args[0] == "/quitar-enviar-a") { EnviarA.Quitar(); return; }
            if (args.Length > 0 && args[0] == "/instalar-panel-premiere") { PanelPremiere.Instalar(); return; }
            if (args.Length > 0 && args[0] == "/quitar-panel-premiere") { PanelPremiere.Quitar(); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new Ventana();
            // Desde "Enviar a" llega la carpeta elegida (si mandan un archivo, se usa su carpeta)
            string ruta = args.Length > 0 ? args[0] : null;
            if (ruta != null && File.Exists(ruta)) ruta = Path.GetDirectoryName(ruta);
            if (ruta != null && Directory.Exists(ruta)) form.Shown += (s, e) => form.Abrir(ruta);
            Application.Run(form);
        }
    }

    // ------------------------------------------------------------------ Menú "Enviar a"

    /// Acceso directo en la carpeta "Enviar a" de Windows: clic derecho sobre una carpeta →
    /// Enviar a → Organizador de Footage abre la app con esa carpeta cargada.
    static class EnviarA
    {
        static string Acceso
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Organizador de Footage.lnk"); }
        }

        public static bool Existe { get { return File.Exists(Acceso); } }

        /// El .exe se está ejecutando desde una carpeta temporal (por ejemplo, adentro del ZIP sin descomprimir)
        public static bool DesdeTemporal
        {
            get
            {
                string exe = Application.ExecutablePath, tmp = Path.GetTempPath();
                return exe.StartsWith(tmp, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Agregar()
        {
            var tipo = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(tipo);
            try
            {
                object lnk = tipo.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { Acceso });
                var t = lnk.GetType();
                Action<string, object> poner = (prop, val) => t.InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, lnk, new[] { val });
                poner("TargetPath", Application.ExecutablePath);
                poner("WorkingDirectory", Path.GetDirectoryName(Application.ExecutablePath));
                poner("IconLocation", Application.ExecutablePath + ",0");
                poner("Description", "Separar clips en verticales y horizontales");
                t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static void Quitar()
        {
            if (File.Exists(Acceso)) File.Delete(Acceso);
        }
    }

    // ------------------------------------------------------------------ Panel de Premiere

    /// El panel firmado viaja adentro del .exe (recurso PanelPremiere.zip) y se descomprime en la
    /// carpeta de extensiones de Adobe del usuario. No necesita permisos de administrador ni internet.
    static class PanelPremiere
    {
        public enum Estado { NoIncluido, NoInstalado, Desactualizado, Instalado }

        const string Id = "com.betoviedo.organizadorfootage";

        static string Extensiones
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Adobe", "CEP", "extensions"); }
        }

        static string Destino { get { return Path.Combine(Extensiones, Id); } }

        static Stream Zip() { return typeof(PanelPremiere).Assembly.GetManifestResourceStream("PanelPremiere.zip"); }

        /// Versión del panel según su manifest (ExtensionBundleVersion)
        static Version LeerVersion(string xml)
        {
            var m = Regex.Match(xml ?? "", "ExtensionBundleVersion=\"([^\"]+)\"");
            Version v;
            return m.Success && Version.TryParse(m.Groups[1].Value, out v) ? v : null;
        }

        static Version Incluida()
        {
            using (var s = Zip())
            {
                if (s == null) return null;
                using (var z = new ZipArchive(s))
                {
                    var e = z.GetEntry(Id + "/CSXS/manifest.xml");
                    if (e == null) return null;
                    using (var r = new StreamReader(e.Open())) return LeerVersion(r.ReadToEnd());
                }
            }
        }

        static Version Instalada()
        {
            string f = Path.Combine(Destino, "CSXS", "manifest.xml");
            return File.Exists(f) ? LeerVersion(File.ReadAllText(f)) : null;
        }

        public static Estado EstadoActual
        {
            get
            {
                var incluida = Incluida();
                if (incluida == null) return Estado.NoIncluido;
                var instalada = Instalada();
                if (instalada == null) return Estado.NoInstalado;
                return instalada < incluida ? Estado.Desactualizado : Estado.Instalado;
            }
        }

        public static void Instalar()
        {
            using (var s = Zip())
            {
                if (s == null) return;
                using (var z = new ZipArchive(s))
                {
                    Quitar();
                    Directory.CreateDirectory(Extensiones);
                    z.ExtractToDirectory(Extensiones);
                }
            }
        }

        public static void Quitar()
        {
            if (Directory.Exists(Destino)) Directory.Delete(Destino, true);
        }
    }

    // ------------------------------------------------------------------ Modelo

    sealed class Clip
    {
        public string Ruta;
        public List<string> Sidecars = new List<string>();
        public bool? Vertical;
        public bool Forzado;
        public double? Fps;
        public bool Leido;
        public ListViewItem Item;

        public string Nombre { get { return Path.GetFileName(Ruta); } }
        public bool EsVertical { get { return Vertical ?? false; } }
    }

    sealed class Movimiento
    {
        public string Origen, Destino;
    }

    static class Opciones
    {
        static readonly string Archivo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrganizadorFootage", "opciones.txt");

        public static Dictionary<string, string> Cargar()
        {
            var d = new Dictionary<string, string>();
            try
            {
                foreach (var l in File.ReadAllLines(Archivo, Encoding.UTF8))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
            return d;
        }

        public static void Guardar(Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Archivo));
                File.WriteAllLines(Archivo, d.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Ventana

    sealed class Ventana : Form
    {
        static readonly HashSet<string> ExtVideo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mov", ".m4v", ".crm", ".nev", ".mxf" };
        static readonly string[] Sidecars = { "M01.XML", ".XML", ".THM", ".XMP", ".LRF", ".SRT" };
        static readonly HashSet<string> CarpetasSalida = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Verticales", "Horizontales", "Verticales_SlowMo", "Verticales_RealTime", "Horizontales_SlowMo", "Horizontales_RealTime" };

        static readonly Color Acento = Color.FromArgb(76, 175, 80);
        static readonly Color ColorV = Color.FromArgb(144, 101, 176);
        static readonly Color ColorH = Color.FromArgb(51, 126, 169);
        static readonly Color Gris = Color.FromArgb(120, 119, 116);

        string carpeta, destinoElegido;
        List<Clip> clips = new List<Clip>();
        CancellationTokenSource cancelar;
        bool escaneando, procesando, terminado;

        // Controles
        Panel bienvenida, principal;
        ListView lista;
        Label lblCarpetaRuta, lblEjV, lblEjH;
        LinkLabel lnkEnviarA, lnkPremiere;
        Button btnDeshacer;
        List<Movimiento> hechos = new List<Movimiento>();
        List<string> carpetasCreadas = new List<string>();
        bool hechosCopiando;
        Label lblCarpeta, lblResumen, lblAccionInfo, lblDestinoNombre, lblDestinoRuta, lblDestinoInfo, lblOtroDisco, lblEstado, lblError;
        RadioButton rbMover, rbCopiar;
        CheckBox chkSlow;
        NumericUpDown numUmbral;
        TextBox txtV, txtH;
        LinkLabel lnkMisma, lnkCambiar, lnkCerrar, lnkElegirDestino;
        Button btnAccion, btnAbrir, btnOtra;
        ProgressBar barra;
        FlowLayoutPanel opciones;

        string Destino { get { return destinoElegido ?? carpeta; } }

        public Ventana()
        {
            Text = "Organizador de Footage";
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1040, 660);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Controls.Add(CrearPrincipal());
            Controls.Add(CrearBienvenida());
            Controls.Add(CrearEncabezado());

            HabilitarSoltar(this);
            CargarOpciones();
            MostrarBienvenida(true);
            FormClosing += (s, e) =>
            {
                if (procesando && MessageBox.Show(this, "Todavía se están procesando archivos. ¿Cerrar igual?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
                else GuardarOpciones();
            };
        }

        // ---------------------------------------------------------- Construcción de la interfaz

        Control CrearEncabezado()
        {
            var p = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.White, Padding = new Padding(16, 0, 16, 0) };
            var titulo = new Label
            {
                Text = "Organizador de Footage", Font = new Font("Segoe UI Semibold", 13f),
                AutoSize = true, Location = new Point(16, 12)
            };
            var links = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, Padding = new Padding(0, 14, 0, 0)
            };
            links.Controls.Add(Enlace("@betoviedo", "https://www.instagram.com/betoviedo/"));
            links.Controls.Add(Enlace("GitHub", "https://github.com/betropical/organizador-de-footage"));
            links.Controls.Add(Enlace("☕ Invitame un cafecito", "https://cafecito.app/beto_oviedo"));
            p.Controls.Add(links);
            p.Controls.Add(titulo);
            p.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(230, 230, 228) });
            return p;
        }

        Control CrearBienvenida()
        {
            bienvenida = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(250, 250, 249) };
            var centro = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
            centro.Controls.Add(new Label
            {
                Text = "⬇", Font = new Font("Segoe UI Symbol", 40f), ForeColor = Gris, AutoSize = true,
                Anchor = AnchorStyles.None
            });
            centro.Controls.Add(new Label
            {
                Text = "Arrastrá acá una carpeta o tarjeta", Font = new Font("Segoe UI Semibold", 16f),
                AutoSize = true, Anchor = AnchorStyles.None
            });
            centro.Controls.Add(new Label
            {
                Text = "Busca clips en todas las subcarpetas y los separa en verticales y horizontales.\n" +
                       "Antes de mover nada vas a ver cómo queda.",
                ForeColor = Gris, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.None, Margin = new Padding(0, 6, 0, 16)
            });
            var btn = BotonPrincipal("Elegir carpeta…");
            btn.Width = 200;
            btn.Anchor = AnchorStyles.None;
            btn.Click += (s, e) => ElegirCarpeta();
            centro.Controls.Add(btn);
            lnkEnviarA = new LinkLabel
            {
                AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 14, 0, 0),
                Font = new Font("Segoe UI Symbol", 9.75f), LinkColor = ColorH, ActiveLinkColor = ColorH, LinkBehavior = LinkBehavior.HoverUnderline
            };
            lnkEnviarA.LinkClicked += (s, e) => AlternarEnviarA();
            centro.Controls.Add(lnkEnviarA);
            ActualizarEnviarA();
            lnkPremiere = new LinkLabel
            {
                AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 6, 0, 0),
                Font = new Font("Segoe UI Symbol", 9.75f), LinkColor = ColorH, ActiveLinkColor = ColorH, LinkBehavior = LinkBehavior.HoverUnderline
            };
            lnkPremiere.LinkClicked += (s, e) => AlternarPremiere();
            centro.Controls.Add(lnkPremiere);
            ActualizarPremiere();
            lblError = new Label { ForeColor = Color.Firebrick, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 12, 0, 0) };
            centro.Controls.Add(lblError);

            bienvenida.Controls.Add(centro);
            EventHandler centrar = (s, e) => centro.Location = new Point(
                Math.Max(0, (bienvenida.ClientSize.Width - centro.Width) / 2),
                Math.Max(0, (bienvenida.ClientSize.Height - centro.Height) / 2));
            bienvenida.Resize += centrar;
            bienvenida.VisibleChanged += centrar;
            centro.SizeChanged += centrar;
            bienvenida.Paint += (s, e) =>
            {
                var r = bienvenida.ClientRectangle;
                r.Inflate(-20, -20);
                using (var pen = new Pen(Color.FromArgb(200, 200, 198), 2) { DashPattern = new[] { 4f, 3f } })
                    e.Graphics.DrawRectangle(pen, r);
            };
            return bienvenida;
        }

        Control CrearPrincipal()
        {
            principal = new Panel { Dock = DockStyle.Fill };

            // --- Lista de clips
            lista = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                BorderStyle = BorderStyle.None, ShowItemToolTips = true
            };
            lista.Font = new Font("Segoe UI", 9.75f);
            lista.Columns.Add("Clip", 170);
            lista.Columns.Add("Origen", 150);
            lista.Columns.Add("Orientación", 110);
            lista.Columns.Add("FPS", 60, HorizontalAlignment.Right);
            lista.Columns.Add("Va a", 260);
            lista.DoubleClick += (s, e) => AlternarOrientacion();
            lista.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { AlternarOrientacion(); e.Handled = true; } };

            var pie = new Label
            {
                Dock = DockStyle.Bottom, Height = 26, ForeColor = Gris, BackColor = Color.FromArgb(250, 250, 249),
                Text = "  Doble clic en un clip para cambiar su orientación a mano.", TextAlign = ContentAlignment.MiddleLeft
            };

            // --- Panel de opciones (izquierda)
            var izquierda = new Panel { Dock = DockStyle.Left, Width = 300, BackColor = Color.FromArgb(250, 250, 249) };
            var borde = new Label { Dock = DockStyle.Left, Width = 1, BackColor = Color.FromArgb(230, 230, 228) };

            opciones = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(16, 12, 12, 12)
            };
            int ancho = 260;

            lblCarpeta = new Label { Font = new Font("Segoe UI Semibold", 9.75f), AutoSize = false, Width = ancho, Height = 20, AutoEllipsis = true };
            lblCarpetaRuta = new Label { ForeColor = Gris, Font = new Font("Consolas", 8.5f), AutoSize = false, Width = ancho, Height = 32, AutoEllipsis = true };
            var filaCarpeta = Fila();
            lnkCambiar = Accion("Elegir otra carpeta…", (s, e) => ElegirCarpeta());
            lnkCerrar = Accion("Cerrar", (s, e) => Cerrar());
            filaCarpeta.Controls.Add(lnkCambiar);
            filaCarpeta.Controls.Add(lnkCerrar);
            lblResumen = new Label { AutoSize = false, Width = ancho, Height = 22, ForeColor = Color.FromArgb(55, 53, 47), Margin = new Padding(3, 6, 3, 0) };

            rbMover = new RadioButton { Text = "Mover", AutoSize = true, Checked = true };
            rbCopiar = new RadioButton { Text = "Copiar", AutoSize = true };
            var filaAccion = Fila();
            filaAccion.Controls.Add(rbMover);
            filaAccion.Controls.Add(rbCopiar);
            lblAccionInfo = Nota(ancho);
            rbMover.CheckedChanged += (s, e) => Refrescar();

            lblDestinoNombre = new Label { Font = new Font("Segoe UI Semibold", 9.75f), AutoSize = false, Width = ancho, Height = 20, AutoEllipsis = true };
            lblDestinoRuta = new Label { ForeColor = Gris, Font = new Font("Consolas", 8.5f), AutoSize = false, Width = ancho, Height = 32, AutoEllipsis = true };
            var filaDestino = Fila();
            lnkElegirDestino = Accion("Elegir otro destino…", (s, e) => ElegirDestino());
            lnkMisma = Accion("Usar la misma", (s, e) => { destinoElegido = null; Refrescar(); });
            filaDestino.Controls.Add(lnkElegirDestino);
            filaDestino.Controls.Add(lnkMisma);
            lblDestinoInfo = Nota(ancho);
            lblDestinoInfo.Margin = new Padding(3, 8, 3, 0);
            lblOtroDisco = Nota(ancho);
            lblOtroDisco.ForeColor = Color.DarkOrange;
            lblOtroDisco.Font = new Font("Segoe UI Symbol", 8.5f);
            lblOtroDisco.Text = "⚠ Es otro disco: mover tarda lo mismo que copiar.";

            chkSlow = new CheckBox { Text = "Separar SlowMo / RealTime", AutoSize = true };
            chkSlow.CheckedChanged += (s, e) => Refrescar();
            var filaUmbral = Fila();
            filaUmbral.Controls.Add(new Label { Text = "Más de", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            numUmbral = new NumericUpDown { Minimum = 24, Maximum = 240, Increment = 5, Value = 45, Width = 60 };
            numUmbral.ValueChanged += (s, e) => Refrescar();
            filaUmbral.Controls.Add(numUmbral);
            filaUmbral.Controls.Add(new Label { Text = "fps", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });

            txtV = new TextBox { Width = ancho, Font = new Font("Segoe UI", 10.5f) };
            txtH = new TextBox { Width = ancho, Font = new Font("Segoe UI", 10.5f) };
            Pista(txtV, "Escribí un prefijo, ej: Reels_");
            Pista(txtH, "Escribí un prefijo, ej: YouTube_");
            lblEjV = Nota(ancho); lblEjV.Height = 20; lblEjV.Font = new Font("Consolas", 8.5f);
            lblEjH = Nota(ancho); lblEjH.Height = 20; lblEjH.Font = new Font("Consolas", 8.5f);
            txtV.TextChanged += (s, e) => Refrescar();
            txtH.TextChanged += (s, e) => Refrescar();

            var tituloOrigen = Titulo("CLIPS DE");
            tituloOrigen.Margin = new Padding(3, 0, 3, 4);
            opciones.Controls.Add(tituloOrigen);
            opciones.Controls.Add(lblCarpeta);
            opciones.Controls.Add(lblCarpetaRuta);
            opciones.Controls.Add(filaCarpeta);
            opciones.Controls.Add(lblResumen);
            opciones.Controls.Add(Titulo("ACCIÓN"));
            opciones.Controls.Add(filaAccion);
            opciones.Controls.Add(lblAccionInfo);
            opciones.Controls.Add(Titulo("GUARDAR EN"));
            opciones.Controls.Add(lblDestinoNombre);
            opciones.Controls.Add(lblDestinoRuta);
            opciones.Controls.Add(filaDestino);
            opciones.Controls.Add(lblDestinoInfo);
            opciones.Controls.Add(lblOtroDisco);
            opciones.Controls.Add(Titulo("CÁMARA LENTA"));
            opciones.Controls.Add(chkSlow);
            opciones.Controls.Add(filaUmbral);
            opciones.Controls.Add(Titulo("PREFIJOS (OPCIONAL)"));
            var ayudaPref = Nota(ancho); ayudaPref.Height = 20;
            ayudaPref.Text = "Se agregan al principio del nombre de cada clip.";
            opciones.Controls.Add(ayudaPref);
            opciones.Controls.Add(new Label { Text = "▯ Verticales", ForeColor = ColorV, Font = new Font("Segoe UI Semibold", 9.75f), AutoSize = true, Margin = new Padding(3, 6, 3, 2) });
            opciones.Controls.Add(txtV);
            opciones.Controls.Add(lblEjV);
            opciones.Controls.Add(new Label { Text = "▭ Horizontales", ForeColor = ColorH, Font = new Font("Segoe UI Semibold", 9.75f), AutoSize = true, Margin = new Padding(3, 6, 3, 2) });
            opciones.Controls.Add(txtH);
            opciones.Controls.Add(lblEjH);

            // --- Zona inferior: botón / progreso / resultado
            var abajo = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, Padding = new Padding(16, 8, 12, 16)
            };
            lblEstado = new Label { AutoSize = false, Width = ancho, Height = 40, ForeColor = Gris, Font = new Font("Segoe UI Symbol", 9.75f) };
            barra = new ProgressBar { Width = ancho, Height = 8, Visible = false };
            btnAccion = BotonPrincipal("Mover");
            btnAccion.Width = ancho;
            btnAccion.Click += (s, e) => Ejecutar();
            var filaFin = Fila();
            btnAbrir = new Button { Text = "Abrir carpeta", AutoSize = true };
            btnAbrir.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + Destino + "\""); } catch { } };
            btnOtra = new Button { Text = "Otra carpeta", AutoSize = true };
            btnOtra.Click += (s, e) => Cerrar();
            btnDeshacer = new Button { Text = "↶ Deshacer", AutoSize = true, Font = new Font("Segoe UI Symbol", 9.75f) };
            btnDeshacer.Click += (s, e) => Deshacer();
            filaFin.Controls.Add(btnAbrir);
            filaFin.Controls.Add(btnDeshacer);
            filaFin.Controls.Add(btnOtra);
            abajo.Controls.Add(lblEstado);
            abajo.Controls.Add(barra);
            abajo.Controls.Add(btnAccion);
            abajo.Controls.Add(filaFin);

            izquierda.Controls.Add(opciones);
            izquierda.Controls.Add(abajo);

            principal.Controls.Add(lista);
            principal.Controls.Add(pie);
            principal.Controls.Add(borde);
            principal.Controls.Add(izquierda);
            return principal;
        }

        static FlowLayoutPanel Fila()
        {
            return new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        }

        static Label Titulo(string t)
        {
            return new Label
            {
                Text = t, AutoSize = true, ForeColor = Gris, Font = new Font("Segoe UI Semibold", 8.25f),
                Margin = new Padding(3, 16, 3, 4)
            };
        }

        static Label Nota(int ancho)
        {
            return new Label { AutoSize = false, Width = ancho, Height = 34, ForeColor = Gris, Font = new Font("Segoe UI", 8.5f) };
        }

        static LinkLabel Accion(string t, LinkLabelLinkClickedEventHandler h)
        {
            var l = new LinkLabel { Text = t, AutoSize = true, LinkColor = ColorH, ActiveLinkColor = ColorH, LinkBehavior = LinkBehavior.HoverUnderline };
            l.LinkClicked += h;
            return l;
        }

        static LinkLabel Enlace(string t, string url)
        {
            var l = Accion(t, (s, e) => { try { Process.Start(url); } catch { } });
            l.LinkColor = Gris;
            l.Margin = new Padding(10, 0, 0, 0);
            return l;
        }

        static Button BotonPrincipal(string t)
        {
            var b = new Button
            {
                Text = t, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = Acento, ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10.5f), Cursor = Cursors.Hand, Margin = new Padding(3, 6, 3, 6)
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void Pista(TextBox t, string texto)
        {
            t.HandleCreated += (s, e) => SendMessage(t.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, texto);
        }

        // ---------------------------------------------------------- Clic derecho ("Enviar a")

        void ActualizarEnviarA()
        {
            lnkEnviarA.Text = EnviarA.Existe
                ? "✓ Está en el clic derecho (Enviar a)  ·  Quitar"
                : "+ Agregar al clic derecho de las carpetas (Enviar a)";
        }

        void AlternarEnviarA()
        {
            try
            {
                if (EnviarA.Existe)
                {
                    EnviarA.Quitar();
                    MessageBox.Show(this, "Listo, se quitó del menú \"Enviar a\".", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    if (EnviarA.DesdeTemporal)
                    {
                        MessageBox.Show(this, "Parece que estás abriendo la app desde adentro del ZIP.\n\n" +
                            "Primero descomprimilo (clic derecho → Extraer todo), guardá OrganizadorFootage.exe " +
                            "en una carpeta fija (por ejemplo Documentos) y abrilo desde ahí.", Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    EnviarA.Agregar();
                    MessageBox.Show(this, "¡Listo! Ahora podés hacer clic derecho sobre una carpeta →\n" +
                        "Enviar a → Organizador de Footage.\n\n" +
                        "En Windows 11, \"Enviar a\" está dentro de \"Mostrar más opciones\".\n\n" +
                        "Si después movés OrganizadorFootage.exe a otra carpeta, volvé a agregarlo desde acá.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo cambiar el menú \"Enviar a\":\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            ActualizarEnviarA();
        }

        // ---------------------------------------------------------- Panel de Premiere

        void ActualizarPremiere()
        {
            var estado = PanelPremiere.EstadoActual;
            lnkPremiere.Visible = estado != PanelPremiere.Estado.NoIncluido;
            lnkPremiere.Text =
                estado == PanelPremiere.Estado.Instalado ? "✓ Panel de Premiere instalado  ·  Quitar" :
                estado == PanelPremiere.Estado.Desactualizado ? "↑ Actualizar el panel de Premiere Pro" :
                "+ Instalar panel para Premiere Pro";
        }

        void AlternarPremiere()
        {
            try
            {
                if (PanelPremiere.EstadoActual == PanelPremiere.Estado.Instalado)
                {
                    PanelPremiere.Quitar();
                    MessageBox.Show(this, "Se quitó el panel de Premiere. Desaparece la próxima vez que abras Premiere.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    PanelPremiere.Instalar();
                    MessageBox.Show(this, "¡Listo! Reiniciá Premiere Pro y abrí el panel desde\n" +
                        "Ventana → Extensiones → Organizador de Footage.\n\n" +
                        "Sirve para ordenar en bins los clips del proyecto, o importar una carpeta ya dividida en verticales y horizontales.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo instalar el panel de Premiere:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            ActualizarPremiere();
        }

        // ---------------------------------------------------------- Arrastrar y soltar

        void HabilitarSoltar(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = !procesando && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                var rutas = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (rutas != null && rutas.Length > 0) Abrir(rutas[0]);
            };
            foreach (Control hijo in c.Controls) HabilitarSoltar(hijo);
        }

        // ---------------------------------------------------------- Carpeta y escaneo

        void ElegirCarpeta()
        {
            using (var d = new FolderBrowserDialog { Description = "Elegí la carpeta o tarjeta con los clips", ShowNewFolderButton = false })
            {
                if (carpeta != null) d.SelectedPath = carpeta;
                if (d.ShowDialog(this) == DialogResult.OK) Abrir(d.SelectedPath);
            }
        }

        void ElegirDestino()
        {
            using (var d = new FolderBrowserDialog { Description = "Elegí dónde crear las carpetas Verticales y Horizontales", ShowNewFolderButton = true })
            {
                d.SelectedPath = Destino;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                destinoElegido = MismaRuta(d.SelectedPath, carpeta) ? null : d.SelectedPath;
                Refrescar();
            }
        }

        public void Abrir(string ruta) { Abrir(ruta, false); }

        void Abrir(string ruta, bool mantenerDestino)
        {
            if (procesando) return;
            if (!Directory.Exists(ruta))
            {
                lblError.Text = "Arrastrá una carpeta, no un archivo.";
                return;
            }
            if (cancelar != null) cancelar.Cancel();
            carpeta = Path.GetFullPath(ruta);
            if (!mantenerDestino) destinoElegido = null;
            terminado = false;
            hechos = new List<Movimiento>();
            lblError.Text = "";

            clips = BuscarClips(carpeta);
            if (clips.Count == 0)
            {
                carpeta = null;
                lblError.Text = "No encontré clips de video en esa carpeta.";
                MostrarBienvenida(true);
                return;
            }

            lista.BeginUpdate();
            lista.Items.Clear();
            foreach (var c in clips)
            {
                string origen = Path.GetDirectoryName(c.Ruta).Substring(carpeta.Length).TrimStart('\\', '/');
                c.Item = new ListViewItem(new[] { c.Nombre, origen, "…", "", "" }) { Tag = c, UseItemStyleForSubItems = false };
                c.Item.ToolTipText = c.Ruta;
                lista.Items.Add(c.Item);
            }
            lista.EndUpdate();
            MostrarBienvenida(false);
            LeerMetadatos();
        }

        void Cerrar()
        {
            if (procesando) return;
            if (cancelar != null) cancelar.Cancel();
            carpeta = destinoElegido = null;
            clips = new List<Clip>();
            lista.Items.Clear();
            terminado = false;
            lblError.Text = "";
            MostrarBienvenida(true);
        }

        void MostrarBienvenida(bool si)
        {
            bienvenida.Visible = si;
            principal.Visible = !si;
            if (!si) Refrescar();
        }

        static List<Clip> BuscarClips(string raiz)
        {
            var videos = new List<string>();
            var pendientes = new Stack<string>();
            pendientes.Push(raiz);
            while (pendientes.Count > 0)
            {
                string dir = pendientes.Pop();
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                    {
                        var info = new DirectoryInfo(sub);
                        if (CarpetasSalida.Contains(info.Name)) continue;
                        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        pendientes.Push(sub);
                    }
                    foreach (var f in Directory.GetFiles(dir))
                        if (ExtVideo.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith("._")) videos.Add(f);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            videos.Sort(StringComparer.OrdinalIgnoreCase);

            var contenido = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var lista = new List<Clip>();
            foreach (var v in videos)
            {
                string dir = Path.GetDirectoryName(v);
                Dictionary<string, string> archivos;
                if (!contenido.TryGetValue(dir, out archivos))
                {
                    archivos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try { foreach (var f in Directory.GetFiles(dir)) archivos[Path.GetFileName(f)] = f; } catch { }
                    contenido[dir] = archivos;
                }
                var clip = new Clip { Ruta = v };
                string baseNombre = Path.GetFileNameWithoutExtension(v);
                foreach (var s in Sidecars)
                {
                    string encontrado;
                    if (archivos.TryGetValue(baseNombre + s, out encontrado)) clip.Sidecars.Add(encontrado);
                }
                lista.Add(clip);
            }
            return lista;
        }

        void LeerMetadatos()
        {
            cancelar = new CancellationTokenSource();
            var token = cancelar.Token;
            var pendientes = clips.ToList();
            escaneando = true;
            Refrescar();

            Task.Run(() =>
            {
                Parallel.ForEach(pendientes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, c =>
                {
                    var info = Mp4Info.Leer(c.Ruta);
                    if (token.IsCancellationRequested) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        c.Vertical = info.Vertical;
                        c.Fps = info.Fps;
                        c.Leido = true;
                        PintarFila(c);
                        int leidos = clips.Count(x => x.Leido);
                        if (leidos == clips.Count) { escaneando = false; Refrescar(); }
                        else btnAccion.Text = "Leyendo clips… " + leidos + "/" + clips.Count;
                    }));
                });
            }, token).ContinueWith(t => { }, TaskScheduler.Default);
        }

        void AlternarOrientacion()
        {
            if (procesando || terminado) return;
            foreach (ListViewItem it in lista.SelectedItems)
            {
                var c = (Clip)it.Tag;
                if (!c.Leido) continue;
                c.Vertical = !c.EsVertical;
                c.Forzado = true;
                PintarFila(c);
            }
            Refrescar();
        }

        void PintarFila(Clip c)
        {
            var s = c.Item.SubItems;
            if (!c.Leido) { s[2].Text = "…"; return; }
            string t = c.EsVertical ? "Vertical" : "Horizontal";
            if (c.Vertical == null) t = "¿" + t + "?";
            else if (c.Forzado) t += " ✎";
            s[2].Text = t;
            s[2].ForeColor = c.Vertical == null ? Gris : (c.EsVertical ? ColorV : ColorH);
            s[3].Text = c.Fps.HasValue ? c.Fps.Value.ToString("0.##") : "";
            s[3].ForeColor = EsLento(c) && chkSlow.Checked ? Color.DarkOrange : lista.ForeColor;
        }

        // ---------------------------------------------------------- Plan y ejecución

        bool EsLento(Clip c) { return (c.Fps ?? 0) > (double)numUmbral.Value; }

        string CarpetaDestino(Clip c)
        {
            string n = c.EsVertical ? "Verticales" : "Horizontales";
            if (chkSlow.Checked) n += EsLento(c) ? "_SlowMo" : "_RealTime";
            return n;
        }

        // Destino de cada clip sin pisar nada: ni archivos existentes ni otros clips con el mismo nombre
        Dictionary<Clip, string> Plan(List<Movimiento> movimientos)
        {
            var destinos = new Dictionary<Clip, string>();
            var ocupados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string raiz = Destino;
            if (raiz == null) return destinos;

            foreach (var c in clips)
            {
                string dir = Path.Combine(raiz, CarpetaDestino(c));
                string baseOriginal = Path.GetFileNameWithoutExtension(c.Ruta);
                string ext = Path.GetExtension(c.Ruta);
                var sufijos = c.Sidecars.Select(s => Path.GetFileName(s).Substring(baseOriginal.Length)).ToList();
                string pref = c.EsVertical ? txtV.Text : txtH.Text;
                string baseNueva = pref + baseOriginal;

                string nombre = baseNueva;
                for (int n = 2; ; n++)
                {
                    var rutas = new List<string> { Path.Combine(dir, nombre + ext) };
                    rutas.AddRange(sufijos.Select(s => Path.Combine(dir, nombre + s)));
                    if (rutas.All(r => !ocupados.Contains(r) && !File.Exists(r) && !Directory.Exists(r)))
                    {
                        foreach (var r in rutas) ocupados.Add(r);
                        break;
                    }
                    nombre = baseNueva + "_" + n;
                }

                string destino = Path.Combine(dir, nombre + ext);
                destinos[c] = destino;
                if (movimientos != null)
                {
                    movimientos.Add(new Movimiento { Origen = c.Ruta, Destino = destino });
                    for (int i = 0; i < c.Sidecars.Count; i++)
                        movimientos.Add(new Movimiento { Origen = c.Sidecars[i], Destino = Path.Combine(dir, nombre + sufijos[i]) });
                }
            }
            return destinos;
        }

        void Ejecutar()
        {
            if (procesando || escaneando || clips.Count == 0) return;
            var movimientos = new List<Movimiento>();
            Plan(movimientos);
            bool copiar = rbCopiar.Checked;
            int v = clips.Count(c => c.EsVertical), h = clips.Count - v;
            string resumen = clips.Count + " clips: " + v + " verticales, " + h + " horizontales";

            procesando = true;
            barra.Value = 0;
            barra.Maximum = Math.Max(1, movimientos.Count);
            Refrescar();

            Task.Run(() =>
            {
                var fallas = new List<string>();
                var hechosAhora = new List<Movimiento>();
                var creadas = new List<string>();
                for (int i = 0; i < movimientos.Count; i++)
                {
                    var m = movimientos[i];
                    try
                    {
                        string dir = Path.GetDirectoryName(m.Destino);
                        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); creadas.Add(dir); }
                        if (copiar) File.Copy(m.Origen, m.Destino, false);
                        else File.Move(m.Origen, m.Destino);
                        hechosAhora.Add(m);
                    }
                    catch (Exception ex) { fallas.Add(Path.GetFileName(m.Origen) + ": " + ex.Message); }
                    int hecho = i + 1;
                    BeginInvoke((Action)(() => barra.Value = Math.Min(hecho, barra.Maximum)));
                }
                BeginInvoke((Action)(() =>
                {
                    hechos = hechosAhora;
                    hechosCopiando = copiar;
                    carpetasCreadas = creadas;
                    procesando = false;
                    terminado = true;
                    lblEstado.ForeColor = fallas.Count == 0 ? Acento : Color.Firebrick;
                    lblEstado.Text = fallas.Count == 0
                        ? "✔ Listo. " + resumen
                        : "Listo, pero " + fallas.Count + " archivo(s) no se pudieron procesar.";
                    if (fallas.Count > 0)
                        MessageBox.Show(this, string.Join("\n", fallas.Take(10)), "Algunos archivos fallaron", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    System.Media.SystemSounds.Asterisk.Play();
                    Refrescar();
                }));
            });
        }

        // Vuelve todo como estaba: devuelve los clips movidos o borra las copias,
        // y quita las carpetas que se crearon si quedaron vacías.
        void Deshacer()
        {
            if (procesando || hechos.Count == 0) return;
            var lista = hechos.ToList();
            bool copiando = hechosCopiando;
            var creadas = carpetasCreadas.ToList();
            string raiz = carpeta;
            procesando = true;
            barra.Value = 0;
            barra.Maximum = Math.Max(1, lista.Count);
            lblEstado.ForeColor = Gris;
            lblEstado.Text = "Deshaciendo…";
            Refrescar();

            Task.Run(() =>
            {
                var fallas = new List<string>();
                for (int i = lista.Count - 1, n = 1; i >= 0; i--, n++)
                {
                    var m = lista[i];
                    try
                    {
                        if (copiando) File.Delete(m.Destino);
                        else if (!File.Exists(m.Origen))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(m.Origen));
                            File.Move(m.Destino, m.Origen);
                        }
                    }
                    catch (Exception ex) { fallas.Add(Path.GetFileName(m.Destino) + ": " + ex.Message); }
                    int hecho = n;
                    BeginInvoke((Action)(() => barra.Value = Math.Min(hecho, barra.Maximum)));
                }
                foreach (var dir in creadas.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir)
                                .Any(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase)
                                       && !string.Equals(Path.GetFileName(f), "Thumbs.db", StringComparison.OrdinalIgnoreCase)))
                            Directory.Delete(dir, true);
                    }
                    catch { }
                }
                BeginInvoke((Action)(() =>
                {
                    procesando = false;
                    Abrir(raiz, true);
                    if (fallas.Count > 0)
                        MessageBox.Show(this, string.Join("\n", fallas.Take(10)), "No se pudieron deshacer algunos archivos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            });
        }

        // ---------------------------------------------------------- Refresco de la interfaz

        void Refrescar()
        {
            if (carpeta == null) return;
            bool libre = !procesando && !terminado;

            lblCarpeta.Text = new DirectoryInfo(carpeta).Name;
            lblCarpetaRuta.Text = carpeta;
            var cv = clips.FirstOrDefault(c => c.EsVertical);
            var ch = clips.FirstOrDefault(c => !c.EsVertical);
            string nv = cv != null ? cv.Nombre : "C0002.MP4", nh = ch != null ? ch.Nombre : "C0001.MP4";
            lblEjV.Text = txtV.Text.Length == 0 ? "Sin prefijo: " + nv : "Queda: " + txtV.Text + nv;
            lblEjH.Text = txtH.Text.Length == 0 ? "Sin prefijo: " + nh : "Queda: " + txtH.Text + nh;
            int v = clips.Count(c => c.EsVertical), h = clips.Count - v, lentos = clips.Count(EsLento);
            lblResumen.Text = v + " verticales  ·  " + h + " horizontales" + (chkSlow.Checked ? "  ·  " + lentos + " slow-mo" : "");

            lblAccionInfo.Text = rbCopiar.Checked ? "Deja los originales donde están (backup)." : "Rápido si es el mismo disco.";

            lblDestinoNombre.Text = destinoElegido == null ? "La misma carpeta" : new DirectoryInfo(Destino).Name;
            lblDestinoRuta.Text = Destino;
            lnkMisma.Visible = destinoElegido != null;
            lblDestinoInfo.Text = "Ahí adentro se crean las carpetas " + (chkSlow.Checked ? "Verticales_… y Horizontales_…" : "Verticales y Horizontales") + ".";
            lblOtroDisco.Visible = !rbCopiar.Checked && !string.Equals(Path.GetPathRoot(carpeta), Path.GetPathRoot(Destino), StringComparison.OrdinalIgnoreCase);

            numUmbral.Enabled = chkSlow.Checked && libre;
            foreach (Control c in opciones.Controls) if (c != numUmbral.Parent) c.Enabled = libre;
            numUmbral.Parent.Enabled = libre;

            // Al terminar, "Va a" queda mostrando adónde fue cada clip (no se recalcula)
            var destinos = terminado || procesando ? null : Plan(null);
            lista.BeginUpdate();
            foreach (var c in clips)
            {
                string d;
                if (destinos != null)
                    c.Item.SubItems[4].Text = destinos.TryGetValue(c, out d) ? CarpetaDestino(c) + "\\" + Path.GetFileName(d) : "";
                c.Item.SubItems[4].ForeColor = Gris;
                if (c.Leido) c.Item.SubItems[3].ForeColor = EsLento(c) && chkSlow.Checked ? Color.DarkOrange : lista.ForeColor;
            }
            lista.EndUpdate();

            barra.Visible = procesando;
            btnAccion.Visible = !terminado;
            btnAccion.Parent.PerformLayout();
            btnAbrir.Parent.Visible = terminado && !procesando;
            btnDeshacer.Visible = hechos.Count > 0;
            btnAccion.Enabled = !procesando && !escaneando && clips.Count > 0;
            int leidos = clips.Count(c => c.Leido);
            btnAccion.Text = escaneando ? "Leyendo clips… " + leidos + "/" + clips.Count
                           : procesando ? (rbCopiar.Checked ? "Copiando…" : "Moviendo…")
                           : (rbCopiar.Checked ? "Copiar " : "Mover ") + clips.Count + " clips";
            if (!terminado && !procesando) { lblEstado.Text = ""; }
        }

        static bool MismaRuta(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------- Opciones guardadas

        void CargarOpciones()
        {
            var o = Opciones.Cargar();
            string val;
            if (o.TryGetValue("copiar", out val)) rbCopiar.Checked = val == "1";
            if (o.TryGetValue("slowmo", out val)) chkSlow.Checked = val == "1";
            int umbral;
            if (o.TryGetValue("umbral", out val) && int.TryParse(val, out umbral)) numUmbral.Value = Math.Max(24, Math.Min(240, umbral));
            if (o.TryGetValue("prefijoV", out val)) txtV.Text = val;
            if (o.TryGetValue("prefijoH", out val)) txtH.Text = val;
        }

        void GuardarOpciones()
        {
            Opciones.Guardar(new Dictionary<string, string>
            {
                { "copiar", rbCopiar.Checked ? "1" : "0" },
                { "slowmo", chkSlow.Checked ? "1" : "0" },
                { "umbral", ((int)numUmbral.Value).ToString() },
                { "prefijoV", txtV.Text },
                { "prefijoH", txtH.Text },
            });
        }
    }
}
