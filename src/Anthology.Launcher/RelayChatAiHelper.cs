using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Anthology.Launcher;

internal sealed record RelayAiResult(bool Success, string Text);

internal sealed class RelayChatAiHelper : IAsyncDisposable, IDisposable
{
    private static readonly Uri LocalHealthUri = new("http://127.0.0.1:8787/health");
    private static readonly Uri LocalAskUri = new("http://127.0.0.1:8787/ask");
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(50),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _ownedProcess;
    private string? _gameRoot;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public string StatusText { get; private set; } = "AI Helper не проверен";

    public void SetGameRoot(string gameRoot) => _gameRoot = Path.GetFullPath(gameRoot);

    public async Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (await CheckHealthAsync(cancellationToken))
        {
            IsAvailable = true;
            StatusText = "AI Helper подключён";
            return true;
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (await CheckHealthAsync(cancellationToken))
            {
                IsAvailable = true;
                StatusText = "AI Helper подключён";
                return true;
            }

            var scriptPath = FindHelperScript();
            if (scriptPath is null)
            {
                IsAvailable = false;
                StatusText = "Не найден anthology-ai-helper\\start_helper.ps1";
                return false;
            }

            if (_ownedProcess is null or { HasExited: true })
            {
                _ownedProcess?.Dispose();
                _ownedProcess = Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }

            for (var attempt = 0; attempt < 24; attempt++)
            {
                await Task.Delay(250, cancellationToken);
                if (await CheckHealthAsync(cancellationToken))
                {
                    IsAvailable = true;
                    StatusText = "AI Helper подключён";
                    return true;
                }
            }

            IsAvailable = false;
            StatusText = "AI Helper не ответил на порту 8787";
            return false;
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or HttpRequestException)
        {
            IsAvailable = false;
            StatusText = $"AI Helper недоступен: {exception.Message}";
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<RelayAiResult> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var cleanQuestion = Sanitize(question, 700);
        if (cleanQuestion.Length == 0)
        {
            return new RelayAiResult(false, "Напишите вопрос после /ai.");
        }

        if (await EnsureAvailableAsync(cancellationToken))
        {
            var local = await PostAsync(LocalAskUri, cleanQuestion, null, cancellationToken);
            if (local.Success || local.Text.Contains("Подожди", StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }
        }

        var cloud = ReadCloudEndpoint();
        if (cloud is not null)
        {
            var result = await PostAsync(cloud.Value.Uri, cleanQuestion, cloud.Value.Token, cancellationToken);
            if (result.Success)
            {
                IsAvailable = true;
                StatusText = "AI Helper подключён через облако";
            }
            return result;
        }

        return new RelayAiResult(false, StatusText);
    }

    private async Task<RelayAiResult> PostAsync(Uri uri, string question, string? bridgeToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(question, Encoding.UTF8, "text/plain"),
            };
            if (!string.IsNullOrWhiteSpace(bridgeToken))
            {
                request.Headers.TryAddWithoutValidation("X-Anthology-Bridge-Token", bridgeToken);
            }
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var answer = Sanitize(await response.Content.ReadAsStringAsync(cancellationToken), 1800);
            if (answer.Length == 0)
            {
                answer = $"AI Helper вернул пустой ответ ({(int)response.StatusCode}).";
            }
            return new RelayAiResult(response.IsSuccessStatusCode, answer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RelayAiResult(false, "AI Helper не ответил вовремя.");
        }
        catch (HttpRequestException exception)
        {
            return new RelayAiResult(false, $"AI Helper недоступен: {exception.Message}");
        }
    }

    private async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var response = await _httpClient.GetAsync(LocalHealthUri, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private string? FindHelperScript()
    {
        if (string.IsNullOrWhiteSpace(_gameRoot))
        {
            return null;
        }

        var parent = Directory.GetParent(_gameRoot)?.FullName;
        var candidates = new[]
        {
            Path.Combine(_gameRoot, "anthology-ai-helper", "start_helper.ps1"),
            Path.Combine(_gameRoot, "ai-helper", "start_helper.ps1"),
            parent is null ? string.Empty : Path.Combine(parent, "anthology-ai-helper", "start_helper.ps1"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private (Uri Uri, string? Token)? ReadCloudEndpoint()
    {
        if (string.IsNullOrWhiteSpace(_gameRoot))
        {
            return null;
        }

        var path = Path.Combine(_gameRoot, "anthology-ai-helper", "cloud_config.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;
            var token = root.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString() : null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? (uri, token)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        var clean = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    public async Task StopAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        IsAvailable = false;
        StatusText = "AI Helper отключён";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownedProcess is { HasExited: false })
        {
            try { _ownedProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _ownedProcess?.Dispose();
        _httpClient.Dispose();
        _startGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
        _httpClient.Dispose();
        _startGate.Dispose();
    }
}
