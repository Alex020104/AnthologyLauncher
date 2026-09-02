using System.Buffers.Binary;
using System.Text;
using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class XRayDatabaseReaderTests
{
    [Fact]
    public void ReadsUncompressedEntriesWithoutExtractingArchive()
    {
        using var environment = TestDatabase.Create();
        var archivePath = environment.WriteArchive(
            ("scripts/test_mcm.script", "script contents"),
            ("configs/text/rus/test.xml", "<string_table />"));

        using var reader = new XRayDatabaseReader(archivePath);

        Assert.Equal(2, reader.Entries.Count);
        Assert.Equal("script contents", Encoding.UTF8.GetString(reader.Read("scripts/test_mcm.script")));
        Assert.Equal("<string_table />", Encoding.UTF8.GetString(reader.Read("configs\\text\\rus\\test.xml")));
        Assert.Empty(Directory.EnumerateFiles(environment.Root, "*.script", SearchOption.AllDirectories));
    }

    [Fact]
    public void McmCatalogReadsDefinitionsAndRussianLabelsFromGameDatabase()
    {
        using var environment = TestDatabase.Create();
        environment.WriteGameFile(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\ndbtest/enabled = true\r\n");
        environment.WriteArchive(
            ("scripts/dbtest_mcm.script",
                "function on_mcm_load() op = { id='dbtest', gr={ { id='title', type='slide', text='ui_mcm_dbtest_title' }, { id='enabled', type='check', text='ui_mcm_dbtest_enabled', def=false } } } return op end"),
            ("configs/text/rus/st_dbtest.xml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><string_table><string id=\"ui_mcm_dbtest_title\"><text>Архивный модуль</text></string><string id=\"ui_mcm_dbtest_enabled\"><text>Архивный параметр</text></string></string_table>"));

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, null);
        var entry = Assert.Single(snapshot.McmSettings);

        Assert.Equal("Архивный модуль", entry.CategoryDisplayName);
        Assert.Equal("Архивный параметр", entry.DisplayName);
        Assert.Equal("check", entry.ControlType);
        Assert.Equal("false", entry.DefaultValue);
        Assert.Contains("DB: 2 файлов из 1 архивов", snapshot.StatusText);
    }

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string root)
        {
            Root = root;
            GameRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(GameRoot, "db", "configs"));
        }

        public string Root { get; }

        public string GameRoot { get; }

        public static TestDatabase Create() => new(Path.Combine(
            Path.GetTempPath(),
            "AnthologyXdbTests",
            Guid.NewGuid().ToString("N")));

        public void WriteGameFile(string relativePath, string contents)
        {
            var path = Path.Combine(GameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }

        public string WriteArchive(params (string Name, string Contents)[] files)
        {
            var archivePath = Path.Combine(GameRoot, "db", "configs", "metadata.xdb0");
            using var stream = File.Create(archivePath);
            var payloads = files.Select(file => Encoding.UTF8.GetBytes(file.Contents)).ToArray();
            var dataSize = payloads.Sum(item => item.Length);
            WriteUInt32(stream, 0);
            WriteUInt32(stream, checked((uint)dataSize));

            var offsets = new uint[payloads.Length];
            for (var index = 0; index < payloads.Length; index++)
            {
                offsets[index] = checked((uint)stream.Position);
                stream.Write(payloads[index]);
            }

            using var indexStream = new MemoryStream();
            for (var index = 0; index < files.Length; index++)
            {
                var name = Encoding.UTF8.GetBytes(files[index].Name.Replace('/', '\\'));
                WriteUInt16(indexStream, checked((ushort)(name.Length + 16)));
                WriteUInt32(indexStream, checked((uint)payloads[index].Length));
                WriteUInt32(indexStream, checked((uint)payloads[index].Length));
                WriteUInt32(indexStream, 0);
                indexStream.Write(name);
                WriteUInt32(indexStream, offsets[index]);
            }

            WriteUInt32(stream, 1);
            WriteUInt32(stream, checked((uint)indexStream.Length));
            indexStream.Position = 0;
            indexStream.CopyTo(stream);
            return archivePath;
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
