using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMModelHealthDashboard.Services
{
    // RGB colour for the PDF (0–255).
    public readonly struct PdfColor
    {
        public PdfColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static PdfColor FromHex(string hex)
        {
            hex = hex.TrimStart('#');
            return new PdfColor(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }

        internal string Operands =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", R / 255.0, G / 255.0, B / 255.0);
    }

    // A small, dependency-free PDF writer: A4 pages, Helvetica / Helvetica-Bold
    // text, filled rectangles, lines and circles. Coordinates are in points
    // measured from the TOP-left corner of the page.
    public sealed class SimplePdf
    {
        public const double PageWidth = 595.28;
        public const double PageHeight = 841.89;

        private readonly List<StringBuilder> _pages = new List<StringBuilder>();
        private StringBuilder? _current;

        public int PageCount => _pages.Count;

        public void NewPage()
        {
            _current = new StringBuilder();
            _pages.Add(_current);
        }

        private StringBuilder Page => _current ?? throw new InvalidOperationException("Call NewPage first.");

        public void Text(double x, double y, string text, double size, bool bold, PdfColor color)
        {
            double baseline = PageHeight - y - size;

            Page.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size)).Append(" Tf ")
                .Append(color.Operands).Append(" rg ")
                .Append(N(x)).Append(' ').Append(N(baseline)).Append(" Td (")
                .Append(Escape(text)).Append(") Tj ET\n");
        }

        public void TextRight(double rightX, double y, string text, double size, bool bold, PdfColor color) =>
            Text(rightX - TextWidth(text, size, bold), y, text, size, bold, color);

        public void Rect(double x, double y, double width, double height, PdfColor fill)
        {
            Page.Append(fill.Operands).Append(" rg ")
                .Append(N(x)).Append(' ').Append(N(PageHeight - y - height)).Append(' ')
                .Append(N(width)).Append(' ').Append(N(height)).Append(" re f\n");
        }

        public void Line(double x1, double y1, double x2, double y2, PdfColor color, double width)
        {
            Page.Append(color.Operands).Append(" RG ").Append(N(width)).Append(" w ")
                .Append(N(x1)).Append(' ').Append(N(PageHeight - y1)).Append(" m ")
                .Append(N(x2)).Append(' ').Append(N(PageHeight - y2)).Append(" l S\n");
        }

        // Filled circle (status dot), centre (cx, cy), radius r.
        public void Dot(double cx, double cy, double r, PdfColor fill)
        {
            const double k = 0.5523;
            double y = PageHeight - cy;

            Page.Append(fill.Operands).Append(" rg ")
                .Append(N(cx + r)).Append(' ').Append(N(y)).Append(" m ")
                .Append(N(cx + r)).Append(' ').Append(N(y + k * r)).Append(' ')
                .Append(N(cx + k * r)).Append(' ').Append(N(y + r)).Append(' ')
                .Append(N(cx)).Append(' ').Append(N(y + r)).Append(" c ")
                .Append(N(cx - k * r)).Append(' ').Append(N(y + r)).Append(' ')
                .Append(N(cx - r)).Append(' ').Append(N(y + k * r)).Append(' ')
                .Append(N(cx - r)).Append(' ').Append(N(y)).Append(" c ")
                .Append(N(cx - r)).Append(' ').Append(N(y - k * r)).Append(' ')
                .Append(N(cx - k * r)).Append(' ').Append(N(y - r)).Append(' ')
                .Append(N(cx)).Append(' ').Append(N(y - r)).Append(" c ")
                .Append(N(cx + k * r)).Append(' ').Append(N(y - r)).Append(' ')
                .Append(N(cx + r)).Append(' ').Append(N(y - k * r)).Append(' ')
                .Append(N(cx + r)).Append(' ').Append(N(y)).Append(" c f\n");
        }

        // Splits text into lines that fit the width.
        public static List<string> Wrap(string text, double size, bool bold, double maxWidth)
        {
            var lines = new List<string>();
            var current = new StringBuilder();

            foreach (string word in (text ?? string.Empty).Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;

                if (TextWidth(candidate, size, bold) <= maxWidth || current.Length == 0)
                {
                    current.Clear().Append(candidate);
                }
                else
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }
            }

            if (current.Length > 0 || lines.Count == 0)
                lines.Add(current.ToString());

            // Hard-cut single words that are still too long.
            return lines.Select(l => Fit(l, size, bold, maxWidth)).ToList();
        }

        // Shortens text with "..." so it fits the width.
        public static string Fit(string text, double size, bool bold, double maxWidth)
        {
            text ??= string.Empty;

            if (TextWidth(text, size, bold) <= maxWidth)
                return text;

            while (text.Length > 1 && TextWidth(text + "...", size, bold) > maxWidth)
                text = text.Substring(0, text.Length - 1);

            return text + "...";
        }

        public static double TextWidth(string text, double size, bool bold)
        {
            int[] widths = bold ? HelveticaBoldWidths : HelveticaWidths;
            double total = 0;

            foreach (byte b in ToWinAnsi(text ?? string.Empty))
            {
                total += b >= 32 && b <= 126 ? widths[b - 32] : (b == 0x95 ? 350 : 556);
            }

            return total * size / 1000.0;
        }

        public byte[] Build()
        {
            using var stream = new MemoryStream();
            var offsets = new List<long>();

            void Write(string s)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(s);
                stream.Write(bytes, 0, bytes.Length);
            }

            void BeginObject()
            {
                offsets.Add(stream.Position);
                Write($"{offsets.Count} 0 obj\n");
            }

            Write("%PDF-1.4\n");

            int pageCount = _pages.Count;
            int firstPageObject = 5;

            // 1 catalog, 2 page tree, 3 Helvetica, 4 Helvetica-Bold
            BeginObject();
            Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            BeginObject();
            string kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObject + i * 2} 0 R"));
            Write($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

            BeginObject();
            Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

            BeginObject();
            Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

            for (int i = 0; i < pageCount; i++)
            {
                int contentObject = firstPageObject + i * 2 + 1;

                BeginObject();
                Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                      $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObject} 0 R >>\nendobj\n");

                string content = _pages[i].ToString();
                BeginObject();
                Write($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n");
                Write(content);
                Write("\nendstream\nendobj\n");
            }

            long xref = stream.Position;
            Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");

            foreach (long offset in offsets)
                Write($"{offset:D10} 00000 n \n");

            Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

            return stream.ToArray();
        }

        // ------------------------------------------------------------------

        private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        // PDF string with WinAnsi bytes; non-ASCII bytes as octal escapes.
        private static string Escape(string text)
        {
            var sb = new StringBuilder();

            foreach (byte b in ToWinAnsi(text))
            {
                if (b == '(' || b == ')' || b == '\\')
                    sb.Append('\\').Append((char)b);
                else if (b < 32 || b > 126)
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else
                    sb.Append((char)b);
            }

            return sb.ToString();
        }

        private static IEnumerable<byte> ToWinAnsi(string text)
        {
            foreach (char c in text)
            {
                if (c >= 32 && c <= 126)
                {
                    yield return (byte)c;
                    continue;
                }

                switch (c)
                {
                    case '\u2013': yield return 0x96; break;              // –
                    case '\u2014': yield return 0x97; break;              // —
                    case '\u2022': yield return 0x95; break;              // •
                    case '\u00B7': yield return 0xB7; break;              // ·
                    case '\u2019': yield return 0x92; break;              // ’
                    case '\u2018': yield return 0x91; break;              // ‘
                    case '\u201C': yield return 0x93; break;              // “
                    case '\u201D': yield return 0x94; break;              // ”
                    case '\u2026': yield return 0x85; break;              // …
                    case '\u2192': yield return (byte)'-'; yield return (byte)'>'; break; // →
                    case '\u2713': yield return (byte)'+'; break;         // ✓
                    case '\u2717': yield return (byte)'x'; break;         // ✗
                    default:
                        yield return c >= 0xA0 && c <= 0xFF ? (byte)c : (byte)'?';
                        break;
                }
            }
        }

        // Standard Helvetica character widths for ASCII 32–126 (per 1000 units).
        private static readonly int[] HelveticaWidths =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
        };

        private static readonly int[] HelveticaBoldWidths =
        {
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
            975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
            333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
            611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
        };
    }
}
