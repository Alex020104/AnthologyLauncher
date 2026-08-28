using Anthology.Community.Api;
using Anthology.Contracts;
using Microsoft.AspNetCore.Http;

namespace Anthology.Update.Core.Tests;

public sealed class CommunityStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-community-{Guid.NewGuid():N}");
    private readonly string? _previousDataRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_DATA_ROOT");

    [Fact]
    public void VotesMessagesAndReportsSurviveStateRecreation()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var state = new CommunityState();
        var beforeVotes = state.GetPoll("priority-01")!.Options[0].Votes;
        state.Vote("priority-01", new PollVoteRequest("persistence-test-user", ["updater"]));
        var message = new ChatMessage(
            "message-01",
            "general",
            "persistence-test-user",
            "Persistence Test",
            "Сообщение сохранено",
            DateTimeOffset.UtcNow);
        state.AppendMessage(message);
        var receipt = state.CreateReport(new BugReportRequest(
            "Persistent report",
            "Description",
            "Steps",
            "Expected",
            "Actual",
            "0.2.0-alpha.1",
            "sandbox",
            null,
            null));

        var reloaded = new CommunityState();

        Assert.Equal(beforeVotes + 1, reloaded.GetPoll("priority-01")!.Options[0].Votes);
        Assert.Contains(reloaded.GetMessages("general"), item => item.Id == message.Id);
        Assert.StartsWith("BUG-", receipt.Id, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Community", "state.json")));
    }

    [Fact]
    public async Task SmallConfigAttachmentIsStoredWithReport()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var state = new CommunityState();
        var receipt = state.CreateReport(new BugReportRequest(
            "Crash near depot",
            "Crash while looting bodies",
            "Load save and loot",
            "No crash",
            "Lua crash",
            "0.3.0-alpha.1",
            "Anthology 2.1",
            "stack trace",
            null,
            "Test PC",
            "https://disk.yandex.ru/example"));
        var bytes = "[anthology_test]\nenabled = true"u8.ToArray();
        await using var stream = new MemoryStream(bytes);
        var files = new FormFileCollection
        {
            new FormFile(stream, 0, bytes.Length, "files", "test-config.ltx"),
        };

        var attachments = await state.SaveAttachmentsAsync(receipt.Id, files);

        var attachment = Assert.Single(attachments);
        Assert.Equal("test-config.ltx", attachment.FileName);
        Assert.Equal(bytes.Length, attachment.Size);
        Assert.True(File.Exists(Path.Combine(
            _root,
            "Community",
            "attachments",
            receipt.Id,
            "test-config.ltx")));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _previousDataRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
