using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HddBackup.App.Forms
{
    /// <summary>Small tree icons drawn in code (no embedded binary resources), scaled to the DPI.</summary>
    internal static class FilesTreeIcons
    {
        public const string Folder = "dir";
        public const string File = "file";
        public const string Link = "link";
        public const string Special = "special";
        public const string Unreadable = "bad";

        public static ImageList Create(float scale)
        {
            int size = (int)System.Math.Round(16 * scale);
            var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
            list.Images.Add(Folder, Draw(size, DrawFolder));
            list.Images.Add(File, Draw(size, DrawFile));
            list.Images.Add(Link, Draw(size, DrawLink));
            list.Images.Add(Special, Draw(size, DrawSpecial));
            list.Images.Add(Unreadable, Draw(size, DrawUnreadable));
            return list;
        }

        private static Bitmap Draw(int size, System.Action<Graphics, float> paint)
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                paint(g, size / 16f);
            }

            return bitmap;
        }

        private static void DrawFolder(Graphics g, float s)
        {
            using (var back = new SolidBrush(Color.FromArgb(232, 184, 64)))
            using (var front = new SolidBrush(Color.FromArgb(247, 214, 110)))
            using (var edge = new Pen(Color.FromArgb(176, 128, 24), Math1(s)))
            {
                g.FillRectangle(back, 1 * s, 3 * s, 6 * s, 3 * s);
                g.FillRectangle(front, 1 * s, 5 * s, 14 * s, 9 * s);
                g.DrawRectangle(edge, 1 * s, 5 * s, 14 * s, 9 * s);
            }
        }

        private static void DrawFile(Graphics g, float s)
        {
            using (var body = new SolidBrush(Color.White))
            using (var edge = new Pen(Color.FromArgb(110, 120, 140), Math1(s)))
            using (var line = new Pen(Color.FromArgb(170, 178, 192), Math1(s)))
            {
                g.FillRectangle(body, 3 * s, 1 * s, 10 * s, 14 * s);
                g.DrawRectangle(edge, 3 * s, 1 * s, 10 * s, 14 * s);
                for (int i = 0; i < 4; i++)
                {
                    g.DrawLine(line, 5 * s, (5 + (i * 2.5f)) * s, 11 * s, (5 + (i * 2.5f)) * s);
                }
            }
        }

        private static void DrawLink(Graphics g, float s)
        {
            DrawFile(g, s);
            using (var arrow = new Pen(Color.FromArgb(30, 110, 200), 2 * s))
            {
                arrow.EndCap = LineCap.ArrowAnchor;
                g.DrawLine(arrow, 5 * s, 12 * s, 12 * s, 6 * s);
            }
        }

        private static void DrawSpecial(Graphics g, float s)
        {
            using (var body = new SolidBrush(Color.FromArgb(220, 224, 232)))
            using (var edge = new Pen(Color.FromArgb(90, 98, 120), Math1(s)))
            {
                g.FillEllipse(body, 2 * s, 2 * s, 12 * s, 12 * s);
                g.DrawEllipse(edge, 2 * s, 2 * s, 12 * s, 12 * s);
                g.DrawLine(edge, 8 * s, 5 * s, 8 * s, 8.5f * s);
                g.DrawLine(edge, 8 * s, 8.5f * s, 10.5f * s, 10 * s);
            }
        }

        private static void DrawUnreadable(Graphics g, float s)
        {
            using (var body = new SolidBrush(Color.FromArgb(240, 200, 200)))
            using (var edge = new Pen(Color.FromArgb(176, 32, 32), 2 * s))
            {
                g.FillEllipse(body, 1 * s, 1 * s, 14 * s, 14 * s);
                g.DrawLine(edge, 5 * s, 5 * s, 11 * s, 11 * s);
                g.DrawLine(edge, 11 * s, 5 * s, 5 * s, 11 * s);
            }
        }

        private static float Math1(float s) => System.Math.Max(1f, s);
    }
}
