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
        var receipt = state.CreateReport(CreateValidReport() with
        {
            Title = "Persistent report",
            Description = "Detailed persistent report description for storage test",
            ReproductionSteps = "1. Start game. 2. Reproduce the persistent issue.",
            GameVersion = "sandbox",
            EvidenceUrl = "https://disk.yandex.ru/d/test-persistence",
        });

        var reloaded = new CommunityState();

        Assert.Equal(beforeVotes + 1, reloaded.GetPoll("priority-01")!.Options[0].Votes);
        Assert.Contains(reloaded.GetMessages("general"), item => item.Id == message.Id);
        Assert.StartsWith("BUG-", receipt.Id, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Community", "community.db")));
    }

    [Fact]
    public async Task SmallConfigAttachmentIsStoredWithReport()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var state = new CommunityState();
        var receipt = state.CreateReport(CreateValidReport() with
        {
            EvidenceUrl = "https://disk.yandex.ru/d/test-attachment",
        });
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

    [Fact]
    public void DeveloperCanReplyAndCloseReportWhilePlayerKeepsPrivateAccess()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var state = new CommunityState();
        var receipt = state.CreateReport(CreateValidReport() with
        {
            EvidenceUrl = "https://disk.yandex.ru/d/test-developer-flow",
            ReporterId = "player-01",
            ReporterName = "Шура",
            InterfaceLanguage = "ru",
        });

        Assert.False(string.IsNullOrWhiteSpace(receipt.AccessToken));
        Assert.True(state.ReportTokenMatches(receipt.Id, receipt.AccessToken));
        Assert.False(state.ReportTokenMatches(receipt.Id, "wrong-token"));

        var answered = state.AddReportMessage(
            receipt.Id,
            new BugReportReplyRequest("developer", "Alex020104", "Нужен сейв.", "ru"),
            isDeveloper: true);
        Assert.Equal(BugReportStatuses.WaitingForPlayer, answered.Receipt.Status);
        Assert.Equal("developer", Assert.Single(answered.Messages).AuthorRole);
        Assert.Null(answered.Receipt.AccessToken);

        var closed = state.SetReportStatus(
            receipt.Id,
            new BugReportStatusRequest(BugReportStatuses.Closed, "Alex020104"));
        Assert.Equal(BugReportStatuses.Closed, closed.Receipt.Status);
        Assert.Equal(2, closed.Messages.Count);

        var reloaded = new CommunityState();
        Assert.Equal(BugReportStatuses.Closed, reloaded.GetReport(receipt.Id)!.Receipt.Status);
        Assert.True(reloaded.ReportTokenMatches(receipt.Id, receipt.AccessToken));
    }

    [Fact]
    public void BackupUsesConsistentSqliteSnapshotAndDeletedReportStaysDeleted()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var state = new CommunityState();
        var receipt = state.CreateReport(CreateValidReport() with
        {
            Title = "Backup test report",
            Description = "Detailed backup report description for database test",
            ReproductionSteps = "1. Create report. 2. Create backup. 3. Delete report.",
            EvidenceUrl = "https://disk.yandex.ru/d/test-backup",
        });

        var backup = state.CreateBackup();
        Assert.True(File.Exists(backup.Path));
        Assert.True(backup.Size > 0);
        Assert.True(state.DeleteReport(receipt.Id));
        Assert.Null(new CommunityState().GetReport(receipt.Id));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("steps")]
    [InlineData("expected")]
    [InlineData("actual")]
    [InlineData("log")]
    [InlineData("contact")]
    [InlineData("system")]
    [InlineData("evidence")]
    public void IncompleteBugReportIsRejected(string missingField)
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _root);
        var report = CreateValidReport();
        report = missingField switch
        {
            "title" => report with { Title = "" },
            "description" => report with { Description = "" },
            "steps" => report with { ReproductionSteps = "" },
            "expected" => report with { ExpectedResult = "" },
            "actual" => report with { ActualResult = "" },
            "log" => report with { LogExcerpt = "" },
            "contact" => report with { Contact = "" },
            "system" => report with { SystemSpecs = "" },
            "evidence" => report with { EvidenceUrl = "" },
            _ => report,
        };

        Assert.Throws<ArgumentException>(() => new CommunityState().CreateReport(report));
    }

    private static BugReportRequest CreateValidReport() => new(
        "Reproducible crash report",
        "Detailed description of the reproducible problem and location",
        "1. Load save. 2. Enter location. 3. Reproduce the crash.",
        "The game continues without a crash",
        "The game terminates with a Lua error",
        "0.7.0-alpha.1",
        "2.1.131 Standard",
        "Expression: test; stack trace follows",
        "discord-user",
        "CPU: Test CPU; GPU: Test GPU; RAM: 32 GB; Drive: A:",
        "https://disk.yandex.ru/d/test-evidence");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", _previousDataRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
