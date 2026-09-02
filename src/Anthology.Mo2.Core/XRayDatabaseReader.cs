using System.Buffers.Binary;
using System.Text;
using AuroraLib.Compression.Formats.Common;

namespace Anthology.Mo2.Core;

public sealed record XRayDatabaseEntry(
    string Name,
    long Offset,
    int CompressedSize,
    int Size,
    uint Crc)
{
    public bool IsCompressed => CompressedSize != Size;
}

/// <summary>
/// Read-only reader for the XDB archives used by Anomaly. Archive data is never
/// extracted into or written over the installed game.
/// </summary>
public sealed class XRayDatabaseReader : IDisposable
{
    private const uint CompressedChunk = 0x80000000;
    private const uint HeaderChunk = 1;
    private const int MaximumHeaderSize = 64 * 1024 * 1024;
    private const int MaximumTextAssetSize = 64 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly IReadOnlyList<XRayDatabaseEntry> _entries;

    public XRayDatabaseReader(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        _entries = ReadIndex();
    }

    public string Path { get; }

    public IReadOnlyList<XRayDatabaseEntry> Entries => _entries;

    public byte[] Read(XRayDatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size < 0 || entry.Size > MaximumTextAssetSize)
        {
            throw new InvalidDataException($"XDB asset '{entry.Name}' has an unsupported size: {entry.Size} bytes");
        }

        ValidateRange(entry.Offset, entry.CompressedSize);
        var raw = new byte[entry.CompressedSize];
        ReadExactly(entry.Offset, raw);
        if (!entry.IsCompressed)
        {
            return raw;
        }

        using var input = new MemoryStream(raw, writable: false);
        using var output = new MemoryStream(entry.Size);
        LZO.DecompressHeaderless(input, output);
        if (output.Length != entry.Size)
        {
            throw new InvalidDataException(
                $"XDB asset '{entry.Name}' has a declared size of {entry.Size} bytes, but decompressed to {output.Length} bytes");
        }

        return output.ToArray();
    }

    public byte[] Read(string name)
    {
        var normalized = NormalizeName(name);
        var entry = _entries.LastOrDefault(item => item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? throw new FileNotFoundException($"Asset '{normalized}' was not found in {System.IO.Path.GetFileName(Path)}")
            : Read(entry);
    }

    public void Dispose() => _stream.Dispose();

    private List<XRayDatabaseEntry> ReadIndex()
    {
        long position = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (position <= _stream.Length - 8)
        {
            ReadExactly(position, chunkHeader);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var dataOffset = checked(position + 8);
            ValidateRange(dataOffset, size);

            if ((type & ~CompressedChunk) == HeaderChunk)
            {
                if (size > MaximumHeaderSize)
                {
                    throw new InvalidDataException("XDB index is too large");
                }

                var rawIndex = new byte[size];
                ReadExactly(dataOffset, rawIndex);
                var index = (type & CompressedChunk) != 0
                    ? LzHufDecoder.Decode(rawIndex, MaximumHeaderSize)
                    : rawIndex;
                return ParseEntries(index);
            }

            position = checked(dataOffset + size);
        }

        throw new InvalidDataException($"XDB header chunk was not found in {System.IO.Path.GetFileName(Path)}");
    }

    private List<XRayDatabaseEntry> ParseEntries(ReadOnlySpan<byte> index)
    {
        var entries = new List<XRayDatabaseEntry>();
        var position = 0;
        while (position < index.Length)
        {
            if (index.Length - position < 16)
            {
                throw new InvalidDataException("Truncated XDB entry");
            }

            var recordSize = BinaryPrimitives.ReadUInt16LittleEndian(index[position..]);
            var totalRecordSize = recordSize + 2;
            if (recordSize < 16 || totalRecordSize > index.Length - position)
            {
                throw new InvalidDataException($"Invalid XDB entry size {recordSize} at index offset {position} of {index.Length}");
            }

            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(index[(position + 2)..]));
            var compressedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(index[(position + 6)..]));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(index[(position + 10)..]);
            var nameLength = recordSize - 16;
            var name = DecodeName(index.Slice(position + 14, nameLength));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(index[(position + 14 + nameLength)..]);
            if (offset != 0)
            {
                ValidateRange(offset, compressedSize);
                entries.Add(new XRayDatabaseEntry(NormalizeName(name), offset, compressedSize, size, crc));
            }

            position += totalRecordSize;
        }

        return entries;
    }

    private void ValidateRange(long offset, long size)
    {
        if (offset < 0 || size < 0 || offset > _stream.Length || size > _stream.Length - offset)
        {
            throw new InvalidDataException("XDB entry points outside the archive");
        }
    }

    private void ReadExactly(long offset, Span<byte> destination)
    {
        var position = 0;
        while (position < destination.Length)
        {
            var read = RandomAccess.Read(_stream.SafeFileHandle, destination[position..], offset + position);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            position += read;
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static string NormalizeName(string name) =>
        name.Replace('/', '\\').TrimStart('\\');

    private sealed class LzHufDecoder
    {
        private const int N = 4096;
        private const int F = 60;
        private const int Threshold = 2;
        private const int MaxFrequency = 0x4000;
        private const int CharacterCount = 256 - Threshold + F;
        private const int TreeSize = CharacterCount * 2 - 1;
        private const int Root = TreeSize - 1;

        private readonly int[] _frequency = new int[TreeSize + 1];
        private readonly int[] _child = new int[TreeSize];
        private readonly int[] _parent = new int[TreeSize + CharacterCount];
        private readonly byte[] _window = new byte[N + F - 1];
        private readonly byte[] _distanceCode = BuildDistanceCodes();
        private readonly byte[] _distanceLength = BuildDistanceLengths();
        private byte[] _source = [];
        private int _sourcePosition;
        private uint _bitBuffer;
        private int _bitCount;

        public static byte[] Decode(byte[] source, int maximumOutputSize)
        {
            if (source.Length < 4)
            {
                throw new InvalidDataException("Truncated LZ-Huffman stream");
            }

            var outputSize = BinaryPrimitives.ReadInt32LittleEndian(source);
            if (outputSize < 0 || outputSize > maximumOutputSize)
            {
                throw new InvalidDataException($"Invalid LZ-Huffman output size: {outputSize}");
            }

            return new LzHufDecoder().DecodeCore(source, outputSize);
        }

        private byte[] DecodeCore(byte[] source, int outputSize)
        {
            _source = source;
            _sourcePosition = 4;
            StartHuffman();
            Array.Fill(_window, (byte)' ', 0, N - F);

            var output = new byte[outputSize];
            var outputPosition = 0;
            var windowPosition = N - F;
            while (outputPosition < output.Length)
            {
                var character = DecodeCharacter();
                if (character < 256)
                {
                    output[outputPosition++] = (byte)character;
                    _window[windowPosition] = (byte)character;
                    windowPosition = (windowPosition + 1) & (N - 1);
                    continue;
                }

                var readPosition = (windowPosition - DecodePosition() - 1) & (N - 1);
                var count = character - 255 + Threshold;
                for (var index = 0; index < count && outputPosition < output.Length; index++)
                {
                    var value = _window[(readPosition + index) & (N - 1)];
                    output[outputPosition++] = value;
                    _window[windowPosition] = value;
                    windowPosition = (windowPosition + 1) & (N - 1);
                }
            }

            return output;
        }

        private void StartHuffman()
        {
            for (var index = 0; index < CharacterCount; index++)
            {
                _frequency[index] = 1;
                _child[index] = index + TreeSize;
                _parent[index + TreeSize] = index;
            }

            var source = 0;
            for (var node = CharacterCount; node <= Root; node++)
            {
                _frequency[node] = _frequency[source] + _frequency[source + 1];
                _child[node] = source;
                _parent[source] = node;
                _parent[source + 1] = node;
                source += 2;
            }

            _frequency[TreeSize] = 0xffff;
            _parent[Root] = 0;
        }

        private int DecodeCharacter()
        {
            var character = _child[Root];
            while (character < TreeSize)
            {
                character += ReadBit();
                character = _child[character];
            }

            character -= TreeSize;
            Update(character);
            return character;
        }

        private int DecodePosition()
        {
            var value = ReadByte();
            var position = _distanceCode[value] << 6;
            var remainingBits = _distanceLength[value] - 2;
            while (remainingBits-- > 0)
            {
                value = (value << 1) + ReadBit();
            }

            return position | (value & 0x3f);
        }

        private int ReadBit()
        {
            FillBitBuffer();
            var result = (_bitBuffer >> 15) & 1;
            _bitBuffer <<= 1;
            _bitCount--;
            return (int)result;
        }

        private int ReadByte()
        {
            FillBitBuffer();
            var result = (_bitBuffer >> 8) & 0xff;
            _bitBuffer <<= 8;
            _bitCount -= 8;
            return (int)result;
        }

        private void FillBitBuffer()
        {
            while (_bitCount <= 8)
            {
                var value = _sourcePosition < _source.Length ? _source[_sourcePosition++] : 0;
                _bitBuffer |= (uint)value << (8 - _bitCount);
                _bitCount += 8;
            }
        }

        private void Reconstruct()
        {
            var destination = 0;
            for (var index = 0; index < TreeSize; index++)
            {
                if (_child[index] < TreeSize)
                {
                    continue;
                }

                _frequency[destination] = (_frequency[index] + 1) / 2;
                _child[destination++] = _child[index];
            }

            var source = 0;
            for (var node = CharacterCount; node < TreeSize; node++)
            {
                var second = source + 1;
                var frequency = _frequency[source] + _frequency[second];
                _frequency[node] = frequency;
                var insertion = node - 1;
                while (insertion >= 0 && frequency < _frequency[insertion])
                {
                    insertion--;
                }

                insertion++;
                for (var move = node; move > insertion; move--)
                {
                    _frequency[move] = _frequency[move - 1];
                    _child[move] = _child[move - 1];
                }

                _frequency[insertion] = frequency;
                _child[insertion] = source;
                source += 2;
            }

            for (var index = 0; index < TreeSize; index++)
            {
                var child = _child[index];
                if (child >= TreeSize)
                {
                    _parent[child] = index;
                }
                else
                {
                    _parent[child] = index;
                    _parent[child + 1] = index;
                }
            }
        }

        private void Update(int character)
        {
            if (_frequency[Root] == MaxFrequency)
            {
                Reconstruct();
            }

            var node = _parent[character + TreeSize];
            while (true)
            {
                var frequency = ++_frequency[node];
                var swap = node + 1;
                if (frequency > _frequency[swap])
                {
                    while (frequency > _frequency[swap + 1])
                    {
                        swap++;
                    }

                    _frequency[node] = _frequency[swap];
                    _frequency[swap] = frequency;

                    var firstChild = _child[node];
                    _parent[firstChild] = swap;
                    if (firstChild < TreeSize)
                    {
                        _parent[firstChild + 1] = swap;
                    }

                    var secondChild = _child[swap];
                    _child[swap] = firstChild;
                    _parent[secondChild] = node;
                    if (secondChild < TreeSize)
                    {
                        _parent[secondChild + 1] = node;
                    }

                    _child[node] = secondChild;
                    node = swap;
                }

                node = _parent[node];
                if (node == 0)
                {
                    break;
                }
            }
        }

        private static byte[] BuildDistanceCodes()
        {
            var result = new List<byte>(256);
            AddRange(result, 0, 0, 32);
            AddRange(result, 1, 3, 16);
            AddRange(result, 4, 11, 8);
            AddRange(result, 12, 23, 4);
            AddRange(result, 24, 47, 2);
            AddRange(result, 48, 63, 1);
            return result.ToArray();
        }

        private static byte[] BuildDistanceLengths()
        {
            var result = new List<byte>(256);
            foreach (var (value, count) in new[] { (3, 32), (4, 48), (5, 64), (6, 48), (7, 48), (8, 16) })
            {
                result.AddRange(Enumerable.Repeat((byte)value, count));
            }

            return result.ToArray();
        }

        private static void AddRange(List<byte> target, int start, int end, int repetitions)
        {
            for (var value = start; value <= end; value++)
            {
                target.AddRange(Enumerable.Repeat((byte)value, repetitions));
            }
        }
    }
}
