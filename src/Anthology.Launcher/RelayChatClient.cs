using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Anthology.Launcher;

public sealed record RelayChatMessage(
    string AuthorName,
    string Faction,
    string Text,
    DateTimeOffset CreatedAt,
    bool IsSystem = false,
    bool IsOwn = false);

public sealed class RelayChatClient : IAsyncDisposable, IDisposable
{
    private const string Server = "irc.gamesurge.net";
    private const int Port = 6667;
    private const string DefaultChannel = "#cocrc_slavik";
    private const string DefaultFaction = "stalker";
    private const int MaximumMessages = 300;
    private static readonly Encoding GameEncoding = CreateGameEncoding();

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly List<RelayChatMessage> _messages = [];
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _runTask;
    private StreamWriter? _writer;
    private string? _gameRoot;
    private string _displayName = "Stalker";
    private string _nick = "Anthology";
    private string _channel = DefaultChannel;
    private bool _disposed;

    public event Action<RelayChatMessage>? MessageReceived;

    public event Action? StatusChanged;

    public bool IsConnected { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public string StatusText { get; private set; } = "Чат отключён";

    public int OnlineCount { get; private set; }

    public IReadOnlyList<RelayChatMessage> Messages
    {
        get
        {
            lock (_stateLock)
            {
                return _messages.ToArray();
            }
        }
    }

    public Task<LauncherActionResult> EnsureStartedAsync(
        string gameRoot,
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return Task.FromResult(new LauncherActionResult(false, "Сначала выберите корень игры"));
        }

        var fullRoot = Path.GetFullPath(gameRoot);
        var requestedDisplayName = NormalizeDisplayName(settings.CommunityNickname);
        lock (_stateLock)
        {
            if (_runTask is { IsCompleted: false }
                && string.Equals(_gameRoot, fullRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_displayName, requestedDisplayName, StringComparison.Ordinal))
            {
                return Task.FromResult(new LauncherActionResult(true, "Реальный чат уже работает внутри лаунчера"));
            }
        }

        return RestartAsync(fullRoot, settings, cancellationToken);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_stateLock)
        {
            cancellation = _lifetimeCancellation;
            task = _runTask;
            _lifetimeCancellation = null;
            _runTask = null;
        }

        if (cancellation is null)
        {
            SetStatus(false, "Чат отключён");
            return;
        }

        cancellation.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
        SetStatus(false, "Чат отключён");
    }

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var text = SanitizeMessage(message);
        if (text.Length == 0)
        {
            return;
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException("Реальный чат ещё не подключён");
        }

        var payload = $"{DefaultFaction}☺{_displayName}★{text}";
        await SendRawAsync($"PRIVMSG {_channel} :{payload}", cancellationToken);
        AddMessage(new RelayChatMessage(_displayName, DefaultFaction, text, DateTimeOffset.Now, IsOwn: true));
        await WriteGameInputAsync($"Message/{DefaultFaction}/{_displayName}/False/{text}", cancellationToken);
    }

    private async Task<LauncherActionResult> RestartAsync(
        string gameRoot,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        await StopAsync();
        cancellationToken.ThrowIfCancellationRequested();

        _gameRoot = gameRoot;
        _displayName = NormalizeDisplayName(settings.CommunityNickname);
        _nick = CreateIrcNick(_displayName);
        _channel = DefaultChannel;
        Directory.CreateDirectory(Path.Combine(gameRoot, "gamedata", "configs"));

        var cancellation = new CancellationTokenSource();
        lock (_stateLock)
        {
            _lifetimeCancellation = cancellation;
            _runTask = RunReconnectLoopAsync(cancellation.Token);
        }

        SetStatus(false, "Подключение к Реальному чату…");
        return new LauncherActionResult(true, "Реальный чат запущен внутри лаунчера");
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException
                                               or SocketException
                                               or InvalidOperationException)
            {
                SetStatus(false, $"Связь потеряна: {exception.Message}. Переподключение…");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Server, Port, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };

        lock (_stateLock)
        {
            _writer = writer;
        }

        try
        {
            await SendRawAsync($"NICK {_nick}", cancellationToken);
            await SendRawAsync($"USER {_nick} 0 * :Anthology Launcher Next", cancellationToken);
            using var bridgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bridgeTask = RunGameBridgeAsync(bridgeCancellation.Token);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        throw new IOException("IRC-сервер закрыл соединение");
                    }

                    await ProcessIrcLineAsync(line, cancellationToken);
                }
            }
            finally
            {
                bridgeCancellation.Cancel();
                try
                {
                    await bridgeTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_writer, writer))
                {
                    _writer = null;
                }
            }
            SetStatus(false, cancellationToken.IsCancellationRequested ? "Чат отключён" : "Переподключение…");
        }
    }

    private async Task ProcessIrcLineAsync(string line, CancellationToken cancellationToken)
    {
        if (line.StartsWith("PING ", StringComparison.OrdinalIgnoreCase))
        {
            await SendRawAsync("PONG " + line[5..], cancellationToken);
            return;
        }

        var message = IrcMessage.Parse(line);
        switch (message.Command)
        {
            case "001":
                await SendRawAsync($"JOIN {_channel}", cancellationToken);
                break;
            case "366":
                SetStatus(true, $"Подключён к {_channel}");
                await WriteRuntimeSettingsAsync(cancellationToken);
                break;
            case "433":
                _nick = CreateIrcNick(_displayName);
                await SendRawAsync($"NICK {_nick}", cancellationToken);
                break;
            case "353":
                OnlineCount = Math.Max(0, message.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                StatusChanged?.Invoke();
                break;
            case "JOIN":
                OnlineCount++;
                StatusChanged?.Invoke();
                await WriteUsersAsync(cancellationToken);
                break;
            case "PART":
            case "QUIT":
                OnlineCount = Math.Max(0, OnlineCount - 1);
                StatusChanged?.Invoke();
                await WriteUsersAsync(cancellationToken);
                break;
            case "PRIVMSG":
                await ProcessPrivateMessageAsync(message, cancellationToken);
                break;
        }
    }

    private async Task ProcessPrivateMessageAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var senderNick = message.Prefix.Split('!', 2)[0];
        var text = message.Trailing;
        if (text.StartsWith('\u0001') && text.EndsWith('\u0001'))
        {
            var command = text.Trim('\u0001').Split(' ', 2)[0].ToUpperInvariant();
            var response = command switch
            {
                "CLIENTINFO" => "CLIENTINFO DISPLAY FACTION PING VERSION",
                "DISPLAY" => _displayName,
                "FACTION" => DefaultFaction,
                "VERSION" => "Anthology Launcher Next embedded relay 1.0",
                "PING" => text.Trim('\u0001')[4..].Trim(),
                _ => string.Empty,
            };
            if (response.Length > 0)
            {
                await SendRawAsync($"NOTICE {senderNick} :\u0001{command} {response}\u0001", cancellationToken);
            }
            return;
        }

        if (string.Equals(senderNick, _nick, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var faction = DefaultFaction;
        var author = senderNick;
        var body = text;
        var factionSeparator = text.IndexOf('☺');
        var nameSeparator = text.IndexOf('★');
        if (factionSeparator >= 0 && nameSeparator > factionSeparator)
        {
            faction = text[..factionSeparator];
            author = text[(factionSeparator + 1)..nameSeparator];
            body = text[(nameSeparator + 1)..];
        }
        else
        {
            var deathSeparator = text.IndexOf('☻');
            if (deathSeparator >= 0)
            {
                author = text[..deathSeparator];
                var remainder = text[(deathSeparator + 1)..];
                factionSeparator = remainder.IndexOf('☺');
                if (factionSeparator >= 0)
                {
                    faction = remainder[..factionSeparator];
                    body = remainder[(factionSeparator + 1)..];
                }
            }
        }

        var relayMessage = new RelayChatMessage(
            string.IsNullOrWhiteSpace(author) ? senderNick : author,
            string.IsNullOrWhiteSpace(faction) ? DefaultFaction : faction,
            body,
            DateTimeOffset.Now);
        AddMessage(relayMessage);
        await WriteGameInputAsync($"Message/{relayMessage.Faction}/{relayMessage.AuthorName}/False/{relayMessage.Text}", cancellationToken);
    }

    private async Task RunGameBridgeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ForwardGameOutputAsync(cancellationToken);
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task ForwardGameOutputAsync(CancellationToken cancellationToken)
    {
        var outputPath = GetBridgePath("crc_output.txt");
        if (outputPath is null || !File.Exists(outputPath) || !IsConnected)
        {
            return;
        }

        string[] lines;
        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, GameEncoding, false, 4096, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            stream.SetLength(0);
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException)
        {
            return;
        }

        foreach (var line in lines)
        {
            var parts = line.Split('/', 4);
            if (parts.Length < 2)
            {
                continue;
            }

            if (string.Equals(parts[0], "Handshake", StringComparison.OrdinalIgnoreCase))
            {
                await WriteRuntimeSettingsAsync(cancellationToken);
                continue;
            }

            if (string.Equals(parts[0], "Message", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                var body = parts.Length == 3 ? parts[2] : parts[3];
                var faction = parts[1];
                var payload = $"{faction}☺{_displayName}★{SanitizeMessage(body)}";
                await SendRawAsync($"PRIVMSG {_channel} :{payload}", cancellationToken);
                AddMessage(new RelayChatMessage(_displayName, faction, body, DateTimeOffset.Now, IsOwn: true));
            }
            else if (string.Equals(parts[0], "Death", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                var body = parts.Length == 3 ? parts[2] : parts[3];
                var payload = $"{_nick}☻{parts[1]}☺{SanitizeMessage(body)}";
                await SendRawAsync($"PRIVMSG {_channel} :{payload}", cancellationToken);
            }
        }
    }

    private async Task WriteRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        if (!IsGameRunning())
        {
            return;
        }

        await WriteGameInputAsync("Setting/NewsDuration/10000", cancellationToken);
        await WriteGameInputAsync("Setting/ChatKey/DIK_RETURN", cancellationToken);
        await WriteGameInputAsync("Setting/NewsSound/True", cancellationToken);
        await WriteGameInputAsync("Setting/CloseChat/True", cancellationToken);
        await WriteUsersAsync(cancellationToken);
    }

    private Task WriteUsersAsync(CancellationToken cancellationToken) =>
        WriteGameInputAsync($"Users/{_displayName}", cancellationToken);

    private async Task WriteGameInputAsync(string line, CancellationToken cancellationToken)
    {
        if (!IsGameRunning())
        {
            return;
        }

        var inputPath = GetBridgePath("crc_input.txt");
        if (inputPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            var bytes = GameEncoding.GetBytes(line.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            await using var stream = new FileStream(inputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The game may briefly lock the bridge file while consuming it.
        }
    }

    private async Task SendRawAsync(string line, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            StreamWriter? writer;
            lock (_stateLock)
            {
                writer = _writer;
            }
            if (writer is null)
            {
                throw new InvalidOperationException("IRC-соединение ещё не готово");
            }

            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void AddMessage(RelayChatMessage message)
    {
        lock (_stateLock)
        {
            _messages.Add(message);
            if (_messages.Count > MaximumMessages)
            {
                _messages.RemoveRange(0, _messages.Count - MaximumMessages);
            }
        }
        MessageReceived?.Invoke(message);
    }

    private void SetStatus(bool connected, string status)
    {
        IsConnected = connected;
        StatusText = status;
        StatusChanged?.Invoke();
    }

    private string? GetBridgePath(string fileName) => string.IsNullOrWhiteSpace(_gameRoot)
        ? null
        : Path.Combine(_gameRoot, "gamedata", "configs", fileName);

    private static string SanitizeMessage(string message) => message
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim()[..Math.Min(message.Replace('\r', ' ').Replace('\n', ' ').Trim().Length, 380)];

    private static string NormalizeDisplayName(string value)
    {
        var clean = SanitizeMessage(string.IsNullOrWhiteSpace(value) ? "Stalker" : value);
        return clean.Length <= 48 ? clean : clean[..48];
    }

    private static string CreateIrcNick(string displayName)
    {
        var safe = new string(displayName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(14)
            .ToArray());
        if (safe.Length == 0 || char.IsDigit(safe[0]))
        {
            safe = "Anthology" + safe;
        }
        return $"{safe}_{Random.Shared.Next(100000, 999999)}";
    }

    private static bool IsGameRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("Anomaly", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return false;
    }

    private static Encoding CreateGameEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetimeCancellation?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await StopAsync();
        _disposed = true;
        _sendGate.Dispose();
    }

    private sealed record IrcMessage(string Prefix, string Command, IReadOnlyList<string> Parameters, string Trailing)
    {
        public static IrcMessage Parse(string line)
        {
            var remaining = line;
            var prefix = string.Empty;
            if (remaining.StartsWith(':'))
            {
                var prefixEnd = remaining.IndexOf(' ');
                if (prefixEnd > 0)
                {
                    prefix = remaining[1..prefixEnd];
                    remaining = remaining[(prefixEnd + 1)..];
                }
            }

            var trailing = string.Empty;
            var trailingIndex = remaining.IndexOf(" :", StringComparison.Ordinal);
            if (trailingIndex >= 0)
            {
                trailing = remaining[(trailingIndex + 2)..];
                remaining = remaining[..trailingIndex];
            }

            var tokens = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new IrcMessage(
                prefix,
                tokens.FirstOrDefault()?.ToUpperInvariant() ?? string.Empty,
                tokens.Skip(1).ToArray(),
                trailing);
        }
    }
}
