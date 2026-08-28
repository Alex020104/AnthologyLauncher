using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Anthology.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Anthology.Launcher;

public sealed class CommunityClient(
    HttpClient httpClient,
    LauncherSettingsStore settingsStore) : IDisposable
{
    private readonly Uri _baseUri = GetBaseUri();
    private HubConnection? _hubConnection;
    private string? _joinedChannel;

    public event Action<ChatMessage>? MessageReceived;

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
            new PollVoteRequest(settingsStore.Current.UserId, [optionId]),
            ManifestJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PollItem>(ManifestJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Сервер вернул пустой результат голосования.");
    }

    public async Task<IReadOnlyList<ChatMessage>> JoinChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        await EnsureHubConnectedAsync(cancellationToken);
        if (_joinedChannel is not null
            && !string.Equals(_joinedChannel, channelId, StringComparison.OrdinalIgnoreCase))
        {
            await _hubConnection!.InvokeAsync("LeaveChannel", _joinedChannel, cancellationToken);
        }

        if (!string.Equals(_joinedChannel, channelId, StringComparison.OrdinalIgnoreCase))
        {
            await _hubConnection!.InvokeAsync("JoinChannel", channelId, cancellationToken);
            _joinedChannel = channelId;
        }

        return await httpClient.GetFromJsonAsync<IReadOnlyList<ChatMessage>>(
                   new Uri(_baseUri, $"api/v1/channels/{Uri.EscapeDataString(channelId)}/messages"),
                   ManifestJson.Options,
                   cancellationToken) ?? [];
    }

    public async Task SendMessageAsync(
        string channelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        await EnsureHubConnectedAsync(cancellationToken);
        await _hubConnection!.InvokeAsync(
            "SendMessage",
            channelId,
            settingsStore.Current.UserId,
            settingsStore.Current.CommunityNickname,
            text,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_hubConnection is not null)
        {
            _hubConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task EnsureHubConnectedAsync(CancellationToken cancellationToken)
    {
        if (_hubConnection is null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(new Uri(_baseUri, "hubs/community"))
                .WithAutomaticReconnect()
                .Build();
            _hubConnection.On<ChatMessage>("messageReceived", message => MessageReceived?.Invoke(message));
        }

        if (_hubConnection.State == HubConnectionState.Disconnected)
        {
            await _hubConnection.StartAsync(cancellationToken);
        }
    }

    public async Task<BugReportReceipt> SubmitBugReportAsync(
        BugReportRequest report,
        IReadOnlyList<string>? attachmentPaths = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(_baseUri, "api/v1/bug-reports"),
            report,
            ManifestJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<BugReportReceipt>(ManifestJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Сервер не вернул номер обращения.");
        if (attachmentPaths is { Count: > 0 })
        {
            await UploadBugReportAttachmentsAsync(receipt.Id, attachmentPaths, cancellationToken);
        }

        return receipt;
    }

    private async Task UploadBugReportAttachmentsAsync(
        string reportId,
        IReadOnlyList<string> attachmentPaths,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var streams = new List<Stream>();
        try
        {
            foreach (var path in attachmentPaths)
            {
                var fullPath = Path.GetFullPath(path);
                var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream);
                content.Add(new StreamContent(stream), "files", Path.GetFileName(fullPath));
            }

            using var response = await httpClient.PostAsync(
                new Uri(_baseUri, $"api/v1/bug-reports/{Uri.EscapeDataString(reportId)}/attachments"),
                content,
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static Uri GetBaseUri()
    {
        var configured = Environment.GetEnvironmentVariable("ANTHOLOGY_COMMUNITY_API");
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("http://localhost:5249/");
    }
}
