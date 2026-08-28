namespace Anthology.Update.Core.Tests;

public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("../file.txt")]
    [InlineData("mods/../../file.txt")]
    [InlineData("C:/Windows/file.txt")]
    [InlineData("/rooted/file.txt")]
    public void UnsafeRelativePathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => PathSafety.NormalizeRelativePath(path));
    }

    [Fact]
    public void SafeRelativePathIsNormalized()
    {
        Assert.Equal("mods/example/file.txt", PathSafety.NormalizeRelativePath("mods\\example/file.txt"));
    }
}
