using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Anthology.Contracts;

namespace Anthology.Releaser.App;

public sealed class BugReportDeveloperClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<BugReportDetails>> GetReportsAsync(
        string apiUrl,
        string developerToken,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var relative = "api/v1/bug-reports";
        if (!string.IsNullOrWhiteSpace(status))
        {
            relative += $"?status={Uri.EscapeDataString(status)}";
        }
        using var request = CreateRequest(HttpMethod.Get, apiUrl, relative, developerToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<BugReportDetails>>(
                   ManifestJson.Options,
                   cancellationToken) ?? [];
    }

    public async Task<BugReportDetails> ReplyAsync(
        string apiUrl,
        string developerToken,
        string reportId,
        string developerName,
        string text,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            apiUrl,
            $"api/v1/bug-reports/{Uri.EscapeDataString(reportId)}/messages",
            developerToken);
        request.Content = JsonContent.Create(
            new BugReportReplyRequest(
                $"developer:{developerName}",
                developerName,
                text,
                "ru"),
            options: ManifestJson.Options);
        return await SendForReportAsync(request, cancellationToken);
    }

    public async Task<BugReportDetails> SetStatusAsync(
        string apiUrl,
        string developerToken,
        string reportId,
        string developerName,
        string status,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            apiUrl,
            $"api/v1/bug-reports/{Uri.EscapeDataString(reportId)}/status",
            developerToken);
        request.Content = JsonContent.Create(
            new BugReportStatusRequest(status, developerName),
            options: ManifestJson.Options);
        return await SendForReportAsync(request, cancellationToken);
    }

    public async Task<string> DownloadAttachmentsAsync(
        string apiUrl,
        string developerToken,
        BugReportDetails report,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        var reportRoot = Path.Combine(Path.GetFullPath(destinationRoot), report.Receipt.Id);
        Directory.CreateDirectory(reportRoot);
        foreach (var attachment in report.Attachments)
        {
            var safeName = Path.GetFileName(attachment.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                continue;
            }
            using var request = CreateRequest(
                HttpMethod.Get,
                apiUrl,
                $"api/v1/bug-reports/{Uri.EscapeDataString(report.Receipt.Id)}/attachments/{Uri.EscapeDataString(safeName)}",
                developerToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var destination = Path.Combine(reportRoot, safeName);
            var temporary = destination + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        return reportRoot;
    }

    private async Task<BugReportDetails> SendForReportAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BugReportDetails>(ManifestJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Community API вернул пустое обращение.");
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string apiUrl,
        string relativeUrl,
        string developerToken)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Укажите корректный HTTP/HTTPS-адрес Community API.", nameof(apiUrl));
        }
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativeUrl));
        if (!string.IsNullOrWhiteSpace(developerToken))
        {
            request.Headers.TryAddWithoutValidation("X-Anthology-Developer-Token", developerToken.Trim());
        }
        return request;
    }
}
