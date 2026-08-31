using System.Buffers.Binary;

namespace Anthology.Mo2.Core;

public sealed record DdsPreviewImage(int Width, int Height, byte[] Bgra32);

public static class DdsPreviewDecoder
{
    private const int DdsHeaderLength = 128;
    private const uint DdsMagic = 0x20534444;
    private const uint Dxt1FourCc = 0x31545844;

    public static DdsPreviewImage DecodeDxt1(ReadOnlySpan<byte> data)
    {
        if (data.Length < DdsHeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != DdsMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124)
        {
            throw new InvalidDataException("Файл не является поддерживаемым DDS-превью");
        }

        var height = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || fourCc != Dxt1FourCc)
        {
            throw new InvalidDataException("Поддерживаются только DDS-превью формата DXT1");
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var requiredLength = checked(DdsHeaderLength + blocksWide * blocksHigh * 8);
        if (data.Length < requiredLength)
        {
            throw new InvalidDataException("DDS-превью повреждено или загружено не полностью");
        }

        var pixels = new byte[checked(width * height * 4)];
        var offset = DdsHeaderLength;
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                DecodeBlock(data.Slice(offset, 8), pixels, width, height, blockX * 4, blockY * 4);
                offset += 8;
            }
        }

        return new DdsPreviewImage(width, height, pixels);
    }

    private static void DecodeBlock(
        ReadOnlySpan<byte> block,
        Span<byte> pixels,
        int width,
        int height,
        int originX,
        int originY)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<RgbaColor> colors = stackalloc RgbaColor[4];
        colors[0] = ExpandRgb565(color0);
        colors[1] = ExpandRgb565(color1);
        if (color0 > color1)
        {
            colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
            colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
        }
        else
        {
            colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
            colors[3] = new RgbaColor(0, 0, 0, 0);
        }

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (var localY = 0; localY < 4; localY++)
        {
            var targetY = originY + localY;
            for (var localX = 0; localX < 4; localX++)
            {
                var color = colors[(int)(indices & 0b11)];
                indices >>= 2;
                var targetX = originX + localX;
                if (targetY >= height || targetX >= width)
                {
                    continue;
                }

                var target = (targetY * width + targetX) * 4;
                pixels[target] = color.Blue;
                pixels[target + 1] = color.Green;
                pixels[target + 2] = color.Red;
                pixels[target + 3] = color.Alpha;
            }
        }
    }

    private static RgbaColor ExpandRgb565(ushort value)
    {
        var red = (byte)(((value >> 11) & 0x1f) * 255 / 31);
        var green = (byte)(((value >> 5) & 0x3f) * 255 / 63);
        var blue = (byte)((value & 0x1f) * 255 / 31);
        return new RgbaColor(red, green, blue, 255);
    }

    private static RgbaColor Mix(RgbaColor first, RgbaColor second, int firstWeight, int secondWeight, int divisor) =>
        new(
            (byte)((first.Red * firstWeight + second.Red * secondWeight) / divisor),
            (byte)((first.Green * firstWeight + second.Green * secondWeight) / divisor),
            (byte)((first.Blue * firstWeight + second.Blue * secondWeight) / divisor),
            255);

    private readonly record struct RgbaColor(byte Red, byte Green, byte Blue, byte Alpha);
}
