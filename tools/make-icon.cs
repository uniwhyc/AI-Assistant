// 生成 src/app.ico：深绿圆角底 + 暖橙四角星（与侧栏标识同造型）。
// 各尺寸以 PNG 写入 ICO 容器（Vista+ 支持）。图标缺失时由 build.ps1 编译运行本文件重新生成。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class MakeIcon
{
    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"src\app.ico";
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] blobs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) blobs[i] = Render(sizes[i]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int dim = sizes[i] == 256 ? 0 : sizes[i];
                writer.Write((byte)dim); writer.Write((byte)dim);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write((uint)blobs[i].Length); writer.Write((uint)offset);
                offset += blobs[i].Length;
            }
            foreach (byte[] blob in blobs) writer.Write(blob);
            writer.Flush();
            File.WriteAllBytes(outPath, ms.ToArray());
        }
        Console.WriteLine("已生成图标：" + Path.GetFullPath(outPath));
        return 0;
    }

    static byte[] Render(int size)
    {
        using (var bitmap = new Bitmap(size, size))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(ColorTranslator.FromHtml("#132920")))
                {
                    float radius = size * 0.20f, d = radius * 2;
                    using (var shape = new GraphicsPath())
                    {
                        shape.AddArc(0, 0, d, d, 180, 90);
                        shape.AddArc(size - d, 0, d, d, 270, 90);
                        shape.AddArc(size - d, size - d, d, d, 0, 90);
                        shape.AddArc(0, size - d, d, d, 90, 90);
                        shape.CloseFigure();
                        g.FillPath(bg, shape);
                    }
                }
                using (var fg = new SolidBrush(ColorTranslator.FromHtml("#EFB18A")))
                {
                    float c = size / 2f, outer = size * 0.40f, inner = size * 0.17f;
                    var points = new PointF[8];
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = Math.PI * i / 4 - Math.PI / 2;
                        float r = i % 2 == 0 ? outer : inner;
                        points[i] = new PointF(c + (float)Math.Cos(angle) * r, c + (float)Math.Sin(angle) * r);
                    }
                    g.FillPolygon(fg, points);
                }
            }
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
