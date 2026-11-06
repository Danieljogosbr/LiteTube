using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LiteTube
{
    /// <summary>
    /// Design system compartilhado: paleta preto puro (sem tom azulado), helpers gráficos e controles modernos.
    /// </summary>
    public static class Theme
    {
        // Paleta Preto Puro / Carvão Neutro Oficial (YouTube OLED Dark Mode)
        public static readonly Color Bg = Color.FromArgb(10, 10, 10);             // Preto profundo neutro (#0A0A0A)
        public static readonly Color Surface = Color.FromArgb(18, 18, 18);        // Superfície neutra (#121212)
        public static readonly Color SurfaceHigh = Color.FromArgb(28, 28, 28);    // Elementos elevados / cards (#1C1C1C)
        public static readonly Color Border = Color.FromArgb(44, 44, 44);         // Bordas sutis neutras (#2C2C2C)
        public static readonly Color Accent = Color.FromArgb(255, 30, 60);         // Vermelho YouTube Carmesim (#FF1E3C)
        public static readonly Color Blue = Color.FromArgb(62, 166, 255);          // Azul links YouTube (#3EA6FF)
        public static readonly Color TextMain = Color.FromArgb(245, 245, 245);    // Branco limpo de alto contraste
        public static readonly Color TextDim = Color.FromArgb(160, 160, 160);     // Cinza secundário neutro

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>
    /// Botão moderno: cantos arredondados (pílula), hover animado, efeito de clique,
    /// suporte a borda sutil, ribbon indicador de seleção ativa e texto sem cortes indesejados.
    /// </summary>
    public class ModernButton : Button
    {
        private float _hover = 0f;
        private bool _hovering = false;
        private bool _pressed = false;
        private readonly Timer _anim;

        public int CornerRadius { get; set; }
        public Color BorderColor { get; set; }
        public bool IsActive { get; set; }
        public Color ActiveColor { get; set; }
        public Color ActiveAccentColor { get; set; }
        public bool ShowActiveIndicator { get; set; }

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            CornerRadius = -1;
            BorderColor = Color.Empty;
            ActiveColor = Color.Empty;
            ActiveAccentColor = Color.Empty;
            ShowActiveIndicator = false;
            IsActive = false;
            Cursor = Cursors.Hand;
            _anim = new Timer { Interval = 15 };
            _anim.Tick += (s, e) =>
            {
                float target = _hovering ? 1f : 0f;
                _hover += (target - _hover) * 0.28f;
                if (Math.Abs(target - _hover) < 0.02f)
                {
                    _hover = target;
                    _anim.Stop();
                }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovering = true; _anim.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovering = false; _pressed = false; _anim.Start(); }
        protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color parentBg = Theme.Bg;
            Control p = Parent;
            while (p != null)
            {
                if (p.BackColor.A == 255) { parentBg = p.BackColor; break; }
                p = p.Parent;
            }
            g.Clear(parentBg);

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = CornerRadius >= 0 ? CornerRadius : Math.Min(Height / 2, 18);

            Color baseColor = (IsActive && ActiveColor != Color.Empty) ? ActiveColor : BackColor;
            bool transparent = baseColor.A < 255 || baseColor == Color.Transparent;
            Color fill;
            if (transparent)
            {
                fill = Color.FromArgb((int)(_hover * 40), 255, 255, 255);
            }
            else
            {
                fill = Theme.Blend(baseColor, Color.White, _hover * 0.14f);
                if (_pressed) fill = Theme.Blend(baseColor, Color.Black, 0.18f);
            }

            using (var path = Theme.RoundRect(rect, radius))
            {
                if (fill.A > 0)
                {
                    using (var br = new SolidBrush(fill))
                    {
                        g.FillPath(br, path);
                    }
                }

                // Borda suave se definida
                Color border = BorderColor;
                if (border != Color.Empty)
                {
                    Color borderRender = Theme.Blend(border, Color.White, _hover * 0.28f);
                    using (var pen = new Pen(borderRender, 1f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            // Ribbon indicador lateral (ex: item ativo da barra de navegação)
            if (IsActive && ShowActiveIndicator)
            {
                Color ribbonCol = (ActiveAccentColor != Color.Empty) ? ActiveAccentColor : Theme.Accent;
                int indH = Math.Min(22, Math.Max(12, Height - 14));
                int indY = (Height - indH) / 2;
                Rectangle indRect = new Rectangle(4, indY, 3, indH);
                using (var indPath = Theme.RoundRect(indRect, 2))
                using (var indBrush = new SolidBrush(ribbonCol))
                {
                    g.FillPath(indBrush, indPath);
                }
            }

            Color fore = Enabled ? (IsActive ? Theme.TextMain : ForeColor) : Theme.Blend(ForeColor, parentBg, 0.6f);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            switch (TextAlign)
            {
                case ContentAlignment.MiddleLeft:
                case ContentAlignment.TopLeft:
                case ContentAlignment.BottomLeft:
                    flags |= TextFormatFlags.Left | TextFormatFlags.EndEllipsis; break;
                case ContentAlignment.MiddleRight:
                case ContentAlignment.TopRight:
                case ContentAlignment.BottomRight:
                    flags |= TextFormatFlags.Right | TextFormatFlags.EndEllipsis; break;
                default:
                    flags |= TextFormatFlags.HorizontalCenter; break;
            }

            Rectangle textRect;
            if (IsActive && ShowActiveIndicator)
            {
                textRect = new Rectangle(14, 0, Math.Max(0, Width - 18), Height);
            }
            else if ((flags & TextFormatFlags.HorizontalCenter) != 0)
            {
                textRect = new Rectangle(1, 0, Math.Max(0, Width - 2), Height);
            }
            else
            {
                textRect = new Rectangle(6, 0, Math.Max(0, Width - 12), Height);
            }

            TextRenderer.DrawText(g, Text, Font, textRect, fore, flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null) { _anim.Stop(); _anim.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
