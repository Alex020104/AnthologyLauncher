[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [string]$OutputPng = (Join-Path (Split-Path -Parent $PSScriptRoot) "assets\anthology-launcher-icon.png"),

    [string]$OutputIco = (Join-Path (Split-Path -Parent $PSScriptRoot) "assets\launcher_radioactive_icon_round.ico")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$iconBuilderSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class AnthologyIconBuilder
{
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Build(string sourcePath, string outputPng, string outputIco)
    {
        var frames = new List<byte[]>();
        using (var source = new Bitmap(sourcePath))
        using (var cutout = ExtractEllipse(source, FindEmblemBounds(source)))
        using (var master = ResizeWithPadding(cutout, 1024, 64))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPng)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputIco)));
            master.Save(outputPng, ImageFormat.Png);

            foreach (var size in IconSizes)
            {
                using (var frame = Resize(master, size, size))
                {
                    frames.Add(EncodeIconBitmap(frame));
                }
            }
        }

        using (var file = new FileStream(outputIco, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Count);

            var offset = 6 + frames.Count * 16;
            for (var index = 0; index < frames.Count; index++)
            {
                var size = IconSizes[index];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frames[index].Length);
                writer.Write((uint)offset);
                offset += frames[index].Length;
            }

            foreach (var frame in frames)
            {
                writer.Write(frame);
            }
        }
    }

    private static Rectangle FindEmblemBounds(Bitmap bitmap)
    {
        var centerX = bitmap.Width / 2;
        var centerY = bitmap.Height / 2;
        var left = FindFirstDark(bitmap, true, centerY);
        var right = FindLastDark(bitmap, true, centerY);
        var top = FindFirstDark(bitmap, false, centerX);
        var bottom = FindLastDark(bitmap, false, centerX);
        const int margin = 4;
        return Rectangle.FromLTRB(
            Math.Max(0, left - margin),
            Math.Max(0, top - margin),
            Math.Min(bitmap.Width, right + margin + 1),
            Math.Min(bitmap.Height, bottom + margin + 1));
    }

    private static int FindFirstDark(Bitmap bitmap, bool horizontal, int fixedAxis)
    {
        var length = horizontal ? bitmap.Width : bitmap.Height;
        for (var index = 0; index < length; index++)
        {
            var color = horizontal ? bitmap.GetPixel(index, fixedAxis) : bitmap.GetPixel(fixedAxis, index);
            if (Luminance(color) < 185)
            {
                return index;
            }
        }

        return 0;
    }

    private static int FindLastDark(Bitmap bitmap, bool horizontal, int fixedAxis)
    {
        var length = horizontal ? bitmap.Width : bitmap.Height;
        for (var index = length - 1; index >= 0; index--)
        {
            var color = horizontal ? bitmap.GetPixel(index, fixedAxis) : bitmap.GetPixel(fixedAxis, index);
            if (Luminance(color) < 185)
            {
                return index;
            }
        }

        return length - 1;
    }

    private static double Luminance(Color color)
    {
        return 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
    }

    private static Bitmap ExtractEllipse(Bitmap source, Rectangle bounds)
    {
        var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        }

        var centerX = (bounds.Width - 1) / 2d;
        var centerY = (bounds.Height - 1) / 2d;
        var radiusX = Math.Max(1d, centerX - 2d);
        var radiusY = Math.Max(1d, centerY - 2d);
        const double feather = 0.006;
        for (var y = 0; y < result.Height; y++)
        {
            var dy = (y - centerY) / radiusY;
            for (var x = 0; x < result.Width; x++)
            {
                var dx = (x - centerX) / radiusX;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= 1d - feather)
                {
                    continue;
                }

                var color = result.GetPixel(x, y);
                var alpha = distance >= 1d
                    ? 0
                    : (int)Math.Round(color.A * (1d - distance) / feather);
                result.SetPixel(x, y, Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color.R, color.G, color.B));
            }
        }

        return result;
    }

    private static Bitmap ResizeWithPadding(Bitmap source, int canvasSize, int padding)
    {
        var target = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            Configure(graphics);
            graphics.Clear(Color.Transparent);
            var available = canvasSize - padding * 2;
            var scale = Math.Min(available / (double)source.Width, available / (double)source.Height);
            var width = (int)Math.Round(source.Width * scale);
            var height = (int)Math.Round(source.Height * scale);
            var x = (canvasSize - width) / 2;
            var y = (canvasSize - height) / 2;
            graphics.DrawImage(source, new Rectangle(x, y, width, height));
        }

        return target;
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            Configure(graphics);
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        return target;
    }

    private static byte[] EncodeIconBitmap(Bitmap bitmap)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            var pixelBytes = bitmap.Width * bitmap.Height * 4;
            writer.Write((uint)40);
            writer.Write(bitmap.Width);
            writer.Write(bitmap.Height * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)0);
            writer.Write((uint)pixelBytes);
            writer.Write(0);
            writer.Write(0);
            writer.Write((uint)0);
            writer.Write((uint)0);

            for (var y = bitmap.Height - 1; y >= 0; y--)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    writer.Write(color.B);
                    writer.Write(color.G);
                    writer.Write(color.R);
                    writer.Write(color.A);
                }
            }

            var maskStride = ((bitmap.Width + 31) / 32) * 4;
            var mask = new byte[maskStride];
            for (var y = bitmap.Height - 1; y >= 0; y--)
            {
                Array.Clear(mask, 0, mask.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A == 0)
                    {
                        mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }

                writer.Write(mask);
            }

            return stream.ToArray();
        }
    }

    private static void Configure(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
    }
}
'@

Add-Type -TypeDefinition $iconBuilderSource -ReferencedAssemblies System.Drawing
[AnthologyIconBuilder]::Build(
    [System.IO.Path]::GetFullPath($SourcePng),
    [System.IO.Path]::GetFullPath($OutputPng),
    [System.IO.Path]::GetFullPath($OutputIco))

Write-Host "PNG: $OutputPng"
Write-Host "ICO: $OutputIco"
