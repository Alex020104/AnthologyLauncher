using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class AnomalyLaunchArgumentsTests
{
    [Fact]
    public void AppendStartSaveUsesTheColdStartServerCommandAndKeepsItLast()
    {
        var result = AnomalyLaunchArguments.AppendStartSave(
            "-smap1536 -dbg -prefetch_sounds",
            "chenc - quicksave_2");

        Assert.Equal(
            "-smap1536 -dbg -prefetch_sounds -start server(chenc - quicksave_2/single/alife/load)",
            result);
        Assert.DoesNotContain('"', result);
        Assert.DoesNotContain("-load ", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendStartSaveSupportsLocalizedSaveNames()
    {
        var result = AnomalyLaunchArguments.AppendStartSave(
            null,
            "chenc - «Атрибут». Битва у станции");

        Assert.Equal(
            "-start server(chenc - «Атрибут». Битва у станции/single/alife/load)",
            result);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData(":")]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("\"")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("%")]
    public void AppendStartSaveRejectsEveryCharacterRejectedByXRay(string invalidCharacter)
    {
        var saveName = $"save{invalidCharacter}name";

        Assert.Throws<ArgumentException>(() =>
            AnomalyLaunchArguments.AppendStartSave("-smap1536", saveName));
    }

    [Fact]
    public void AppendStartSaveRejectsNullSaveName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AnomalyLaunchArguments.AppendStartSave("-smap1536", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void AppendStartSaveRejectsEmptyOrWhitespaceSaveName(string saveName)
    {
        Assert.Throws<ArgumentException>(() =>
            AnomalyLaunchArguments.AppendStartSave("-smap1536", saveName));
    }
}
