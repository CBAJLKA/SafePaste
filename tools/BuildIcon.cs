using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SafePaste.Ui;

internal static class BuildIcon
{
    private static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        byte[][] frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap bitmap = IconArtwork.Render(sizes[i]))
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                frames[i] = stream.ToArray();
            }
        }
        Directory.CreateDirectory(args[0]);
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(args[0], "SafePaste.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
        using (Bitmap preview = IconArtwork.Render(256))
            preview.Save(Path.Combine(args[0], "SafePaste.png"), ImageFormat.Png);
    }
}
