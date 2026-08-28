using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Anthology.Contracts;

namespace Anthology.Launcher;

public sealed class CommunityClient(HttpClient httpClient)
{
    private readonly Uri _baseUri = GetBaseUri();
    private readonly string _userId = $"local-{Guid.NewGuid():N}";

    public bool IsOffline { get; private set; }

    public async Task<CommunityFeed> GetFeedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var feed = await httpClient.GetFromJsonAsync<CommunityFeed>(
                new Uri(_baseUri, "api/v1/feed"),
                ManifestJson.Options,
                timeout.Token);
            IsOffline = false;
            return feed ?? DemoContent.CreateFeed();
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or TaskCanceledException
                                           or NotSupportedException)
        {
            IsOffline = true;
            return DemoContent.CreateFeed();
        }
    }

    public async Task<PollItem> VoteAsync(
        string pollId,
        string optionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(_baseUri, $"api/v1/polls/{Uri.EscapeDataString(pollId)}/votes"),
            new PollVoteRequest(_userId, [optionId]),
            ManifestJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PollItem>(ManifestJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Сервер вернул пустой результат голосования.");
    }

    public async Task<BugReportReceipt> SubmitBugReportAsync(
        BugReportRequest report,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(_baseUri, "api/v1/bug-reports"),
            report,
            ManifestJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BugReportReceipt>(ManifestJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Сервер не вернул номер обращения.");
    }

    private static Uri GetBaseUri()
    {
        var configured = Environment.GetEnvironmentVariable("ANTHOLOGY_COMMUNITY_API");
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("http://localhost:5249/");
    }
}
