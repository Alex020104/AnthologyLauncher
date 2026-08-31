using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Anthology.Launcher;

public sealed record RelayChatMessage(string AuthorName, string Faction, string Text, DateTimeOffset CreatedAt, bool IsSystem = false, bool IsOwn = false, string Role = "user", string Id = "");

public sealed record RelayChatParticipant(string Nick, string DisplayName, string Faction, bool IsOwn, string Role = "user");

public sealed class RelayChatClient : IAsyncDisposable, IDisposable
{
    private const int Port = 6667;
    private static readonly string[] Servers =
    [
        "irc.eu.gamesurge.net",
        "irc.gamesurge.net",
        "irc.us.gamesurge.net",
    ];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumSendInterval = TimeSpan.FromMilliseconds(400);
    private const string DefaultChannel = "#cocrc_slavik";
    private const string DefaultFaction = "actor_stalker";
    private const int MaximumMessages = 300;
    private static readonly Encoding GameEncoding = CreateGameEncoding();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly RelayChatAiHelper _aiHelper = new();
    private readonly List<RelayChatMessage> _messages = [];
    private readonly Dictionary<string, ParticipantState> _users = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _runTask;
    private StreamWriter? _writer;
    private string? _gameRoot;
    private string _displayName = "Stalker";
    private string _ownRole = "user";
    private string _nick = "Anthology";
    private string _channel = DefaultChannel;
    private string _faction = DefaultFaction;
    private string _configurationKey = string.Empty;
    private bool _autoFaction = true;
    private bool _sendDeaths = true;
    private bool _receiveDeaths = true;
    private int _deathInterval = 90;
    private int _newsDuration = 10;
    private string _chatKey = "RETURN";
    private bool _newsSound = true;
    private bool _closeAfterSend = true;
    private int _nextServerIndex;
    private DateTimeOffset _nextSendAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeathMessageAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public event Action<RelayChatMessage>? MessageReceived;
    public event Action? StatusChanged;
    public bool IsConnected { get; private set; }
    public string StatusText { get; private set; } = "Чат отключён";
    public bool IsAiHelperAvailable => _aiHelper.IsAvailable;
    public string AiHelperStatusText => _aiHelper.StatusText;

    public bool IsRunning
    {
        get { lock (_stateLock) { return _runTask is { IsCompleted: false }; } }
    }

    public int OnlineCount
    {
        get { lock (_stateLock) { return _users.Count; } }
    }

    public IReadOnlyList<RelayChatMessage> Messages
    {
        get { lock (_stateLock) { return _messages.ToArray(); } }
    }

    public IReadOnlyList<RelayChatParticipant> Participants
    {
        get
        {
            lock (_stateLock)
            {
                return _users.Select(pair => new RelayChatParticipant(
                        pair.Key,
                        pair.Value.DisplayName,
                        pair.Value.Faction,
                        string.Equals(pair.Key, _nick, StringComparison.OrdinalIgnoreCase),
                        pair.Value.Role))
                    .OrderByDescending(item => item.IsOwn)
                    .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }
    }

    public Task<LauncherActionResult> EnsureStartedAsync(string gameRoot, LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return Task.FromResult(new LauncherActionResult(false, "Сначала выберите корень игры"));
        }

        var fullRoot = Path.GetFullPath(gameRoot);
        var requestedKey = BuildConfigurationKey(fullRoot, settings);
        lock (_stateLock)
        {
            if (_runTask is { IsCompleted: false } && string.Equals(_configurationKey, requestedKey, StringComparison.Ordinal))
            {
                return Task.FromResult(new LauncherActionResult(true, "Реальный чат уже работает внутри лаунчера"));
            }
        }
        return RestartAsync(fullRoot, settings, requestedKey, cancellationToken);
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
            _users.Clear();
        }
        if (cancellation is null)
        {
            SetStatus(false, "Чат отключён");
            return;
        }
        cancellation.Cancel();
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
        cancellation.Dispose();
        await _aiHelper.StopAsync();
        SetStatus(false, "Чат отключён");
    }

    public async Task SendMessageAsync(string message, string? recipientNick = null, CancellationToken cancellationToken = default)
    {
        var text = SanitizeMessage(message);
        if (text.Length == 0) return;
        if (!IsConnected) throw new InvalidOperationException("Реальный чат ещё не подключён");

        if (TryGetAiQuestion(text, out var question))
        {
            await AskAiAsync(question, writeToGame: IsGameRunning(), cancellationToken);
            return;
        }

        if (string.Equals(text, "/whoami", StringComparison.OrdinalIgnoreCase))
        {
            var role = _ownRole;
            AddSystemMessage($"Вы — «{_displayName}». Роль: {RelayChatRoles.Label(role)}.");
            return;
        }

        var target = string.IsNullOrWhiteSpace(recipientNick) ? _channel : recipientNick.Trim();
        await SendRawAsync($"PRIVMSG {target} :{_faction}☺{_displayName}★{text}", cancellationToken);
        var isChannel = string.Equals(target, _channel, StringComparison.OrdinalIgnoreCase);
        AddMessage(new RelayChatMessage(_displayName, _faction, isChannel ? text : $"→ {DisplayForNick(target)}: {text}", DateTimeOffset.Now, IsOwn: true, Role: _ownRole));
        await WriteGameInputAsync(
            isChannel
                ? $"Message/{_faction}/{_displayName}/False/{text}"
                : $"Query/{_faction}/{_displayName}/{DisplayForNick(target)}/{text}",
            cancellationToken);
    }

    private async Task<LauncherActionResult> RestartAsync(string gameRoot, LauncherSettings settings, string configurationKey, CancellationToken cancellationToken)
    {
        await StopAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _gameRoot = gameRoot;
        _displayName = NormalizeDisplayName(settings.CommunityNickname);
        _ownRole = RelayChatRoles.Resolve(_displayName);
        _nick = CreateIrcNick(_displayName);
        _channel = NormalizeChannel(settings.RelayChatChannel);
        _faction = NormalizeFaction(settings.RelayChatFaction);
        _autoFaction = settings.RelayChatAutoFaction;
        _sendDeaths = settings.RelayChatSendDeaths;
        _receiveDeaths = settings.RelayChatReceiveDeaths;
        _deathInterval = Math.Clamp(settings.RelayChatDeathInterval, 0, 3600);
        _newsDuration = Math.Clamp(settings.RelayChatNewsDuration, 1, 60);
        _chatKey = NormalizeChatKey(settings.RelayChatKey);
        _newsSound = settings.RelayChatNewsSound;
        _closeAfterSend = settings.RelayChatCloseAfterSend;
        _configurationKey = configurationKey;
        _aiHelper.SetGameRoot(gameRoot);
        lock (_stateLock) { _users[_nick] = new ParticipantState(_displayName, _faction, _ownRole); }
        Directory.CreateDirectory(Path.Combine(gameRoot, "gamedata", "configs"));
        var cancellation = new CancellationTokenSource();
        lock (_stateLock)
        {
            _lifetimeCancellation = cancellation;
            _runTask = RunReconnectLoopAsync(cancellation.Token);
        }
        AddSystemMessage($"Подключение к {_channel}…");
        SetStatus(false, "Подключение к Реальному чату…");
        _ = WarmAiHelperAsync(cancellation.Token);
        return new LauncherActionResult(true, "Реальный чат запущен внутри лаунчера");
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var reconnectDelay = TimeSpan.FromSeconds(5);
            var server = Servers[Math.Abs(Interlocked.Increment(ref _nextServerIndex) - 1) % Servers.Length];
            SetStatus(false, $"Подключение к Реальному чату через {server}…");
            try { await RunConnectionAsync(server, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (RelayChatBackoffException exception)
            {
                reconnectDelay = exception.RetryAfter;
                var status = $"GameSurge временно ограничил подключения. Повтор через {Math.Ceiling(reconnectDelay.TotalMinutes):0} мин.";
                AddSystemMessage(status);
                SetStatus(false, status);
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
            {
                var status = $"Связь потеряна: {exception.Message}. Переподключение…";
                AddSystemMessage(status);
                SetStatus(false, status);
            }
            if (!cancellationToken.IsCancellationRequested) await Task.Delay(reconnectDelay, cancellationToken);
        }
    }

    private async Task RunConnectionAsync(string server, CancellationToken cancellationToken)
    {
        // Some GameSurge IPv6 endpoints accept TCP but never complete IRC registration.
        // The original Relay Chat uses the IPv4 network, so keep the embedded client on
        // that same path and rotate official round-robin hosts after a timed-out attempt.
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCancellation.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(server, Port, connectCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"{server} не ответил за {ConnectTimeout.TotalSeconds:0} секунд");
            }
        }
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        lock (_stateLock) { _writer = writer; }
        try
        {
            await SendRawAsync($"NICK {_nick}", cancellationToken);
            await SendRawAsync($"USER {_nick} 0 * :Anthology Launcher Next", cancellationToken);
            using var bridgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bridgeTask = RunGameBridgeAsync(bridgeCancellation.Token);
            try
            {
                var registrationCompleted = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line;
                    if (registrationCompleted)
                    {
                        line = await reader.ReadLineAsync(cancellationToken);
                    }
                    else
                    {
                        using var registrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        registrationCancellation.CancelAfter(RegistrationTimeout);
                        try
                        {
                            line = await reader.ReadLineAsync(registrationCancellation.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new IOException($"{server} не завершил вход в IRC за {RegistrationTimeout.TotalSeconds:0} секунд");
                        }
                    }
                    if (line is null)
                    {
                        throw new IOException("IRC-сервер закрыл соединение");
                    }
                    // Use the connection-scoped token here. Metadata requests started
                    // while processing JOIN/NAMES must stop with this socket instead of
                    // leaking into the next reconnect and multiplying IRC traffic.
                    await ProcessIrcLineAsync(line, bridgeCancellation.Token);
                    registrationCompleted = IsConnected;
                }
            }
            finally
            {
                bridgeCancellation.Cancel();
                try { await bridgeTask; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            lock (_stateLock) { if (ReferenceEquals(_writer, writer)) _writer = null; }
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
            case "001": await SendRawAsync($"JOIN {_channel}", cancellationToken); break;
            case "366":
                UpdateParticipant(_nick, _displayName, _faction);
                SetStatus(true, $"Подключён к {_channel}");
                AddSystemMessage($"Теперь Вы подключены к сети ({_displayName})");
                await WriteRuntimeSettingsAsync(cancellationToken);
                break;
            case "465":
                throw new RelayChatBackoffException(
                    string.IsNullOrWhiteSpace(message.Trailing) ? "GameSurge ограничил подключения" : message.Trailing,
                    TimeSpan.FromMinutes(10));
            case "ERROR" when message.Trailing.Contains("Excessive connections", StringComparison.OrdinalIgnoreCase)
                                  || message.Trailing.Contains("G-lined", StringComparison.OrdinalIgnoreCase):
                throw new RelayChatBackoffException(message.Trailing, TimeSpan.FromMinutes(10));
            case "ERROR":
                throw new IOException(string.IsNullOrWhiteSpace(message.Trailing)
                    ? "IRC-сервер закрыл соединение"
                    : message.Trailing);
            case "433":
                RemoveParticipant(_nick);
                _nick = CreateIrcNick(_displayName);
                UpdateParticipant(_nick, _displayName, _faction);
                await SendRawAsync($"NICK {_nick}", cancellationToken);
                break;
            case "353": await ProcessNamesAsync(message.Trailing, cancellationToken); break;
            case "JOIN": await ProcessJoinAsync(message, cancellationToken); break;
            case "PART": case "QUIT": await ProcessDepartureAsync(message, cancellationToken); break;
            case "NICK": await ProcessNickChangeAsync(message, cancellationToken); break;
            case "KICK": await ProcessKickAsync(message, cancellationToken); break;
            case "PRIVMSG": await ProcessPrivateMessageAsync(message, cancellationToken); break;
            case "NOTICE": await ProcessNoticeAsync(message, cancellationToken); break;
        }
    }

    private async Task ProcessNamesAsync(string names, CancellationToken cancellationToken)
    {
        foreach (var nick in names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimStart('@', '+', '%', '~', '&')).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var own = string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase);
            UpdateParticipant(nick, own ? _displayName : DisplayNameFromNick(nick), own ? _faction : DefaultFaction);
        }
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessJoinAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var nick = GetSenderNick(message);
        if (nick.Length == 0) return;
        var own = string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase);
        UpdateParticipant(nick, own ? _displayName : DisplayNameFromNick(nick), own ? _faction : DefaultFaction);
        if (!own)
        {
            AddSystemMessage($"{DisplayForNick(nick)} присоединился к каналу");
        }
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessDepartureAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var nick = GetSenderNick(message);
        var display = DisplayForNick(nick);
        RemoveParticipant(nick);
        if (!string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase)) AddSystemMessage($"{display} покинул канал");
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessNickChangeAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var oldNick = GetSenderNick(message);
        var newNick = message.Trailing.Length > 0 ? message.Trailing : message.Parameters.Count > 0 ? message.Parameters[0] : string.Empty;
        if (newNick.Length == 0) return;
        ParticipantState state;
        lock (_stateLock)
        {
            if (!_users.Remove(oldNick, out state!)) state = new ParticipantState(oldNick, DefaultFaction, RelayChatRoles.Resolve(oldNick));
            _users[newNick] = state;
            if (string.Equals(oldNick, _nick, StringComparison.OrdinalIgnoreCase)) _nick = newNick;
        }
        AddSystemMessage($"{state.DisplayName} изменил техническое имя");
        StatusChanged?.Invoke();
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessKickAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var target = message.Parameters.Count > 1 ? message.Parameters[1] : string.Empty;
        if (target.Length == 0) return;
        var display = DisplayForNick(target);
        RemoveParticipant(target);
        AddSystemMessage($"{display} отключён от канала: {message.Trailing}");
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessPrivateMessageAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var senderNick = GetSenderNick(message);
        var text = message.Trailing;
        if (text.StartsWith('\u0001') && text.EndsWith('\u0001'))
        {
            await ProcessCtcpRequestAsync(senderNick, text, cancellationToken);
            return;
        }
        if (string.Equals(senderNick, _nick, StringComparison.OrdinalIgnoreCase)) return;

        var faction = ParticipantFaction(senderNick);
        var author = DisplayForNick(senderNick);
        var body = text;
        var isDeath = false;
        var factionSeparator = text.IndexOf('☺');
        var nameSeparator = text.IndexOf('★');
        if (factionSeparator >= 0 && nameSeparator > factionSeparator)
        {
            faction = NormalizeFaction(text[..factionSeparator]);
            author = text[(factionSeparator + 1)..nameSeparator].Trim();
            body = text[(nameSeparator + 1)..];
        }
        else
        {
            var deathSeparator = text.IndexOf('☻');
            if (deathSeparator >= 0)
            {
                isDeath = true;
                author = text[..deathSeparator].Trim();
                var remainder = text[(deathSeparator + 1)..];
                factionSeparator = remainder.IndexOf('☺');
                if (factionSeparator >= 0)
                {
                    faction = NormalizeFaction(remainder[..factionSeparator]);
                    body = remainder[(factionSeparator + 1)..];
                }
            }
        }
        if (isDeath)
        {
            if (!_receiveDeaths || (DateTimeOffset.Now - _lastDeathMessageAt).TotalSeconds <= _deathInterval) return;
            _lastDeathMessageAt = DateTimeOffset.Now;
        }

        author = string.IsNullOrWhiteSpace(author) ? senderNick : author;
        UpdateParticipant(senderNick, author, faction);
        var isQuery = message.Parameters.Count > 0 && !string.Equals(message.Parameters[0], _channel, StringComparison.OrdinalIgnoreCase);
        AddMessage(new RelayChatMessage(author, faction, isQuery ? $"Личное сообщение: {body}" : body, DateTimeOffset.Now, Role: RelayChatRoles.Resolve(author)));
        await WriteGameInputAsync(isQuery
            ? $"Query/{faction}/{author}/{_displayName}/{body}"
            : $"Message/{faction}/{author}/False/{body}", cancellationToken);
        await WriteUsersAsync(cancellationToken);
    }

    private async Task ProcessCtcpRequestAsync(string senderNick, string text, CancellationToken cancellationToken)
    {
        var parts = text.Trim('\u0001').Split(' ', 2);
        var command = parts[0].ToUpperInvariant();
        var response = command switch
        {
            "CLIENTINFO" => "Supported CTCP commands: CLIENTINFO DISPLAY FACTION PING VERSION",
            "DISPLAY" => _displayName,
            "FACTION" => _faction,
            "VERSION" => "Anthology Launcher Next embedded relay 1.1",
            "PING" => parts.Length > 1 ? parts[1] : string.Empty,
            _ => string.Empty,
        };
        if (response.Length > 0) await SendRawAsync($"NOTICE {senderNick} :\u0001{command} {response}\u0001", cancellationToken);
    }

    private async Task ProcessNoticeAsync(IrcMessage message, CancellationToken cancellationToken)
    {
        var text = message.Trailing;
        if (!text.StartsWith('\u0001') || !text.EndsWith('\u0001')) return;
        var senderNick = GetSenderNick(message);
        var parts = text.Trim('\u0001').Split(' ', 2);
        var command = parts[0].ToUpperInvariant();
        var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        switch (command)
        {
            case "DISPLAY" when value.Length > 0:
                UpdateParticipant(senderNick, NormalizeDisplayName(value), null);
                await WriteUsersAsync(cancellationToken);
                break;
            case "FACTION" when value.Length > 0:
                UpdateParticipant(senderNick, null, NormalizeFaction(value));
                break;
            case "CLIENTINFO":
                if (value.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase)) await SendRawAsync($"PRIVMSG {senderNick} :\u0001DISPLAY\u0001", cancellationToken);
                if (value.Contains("FACTION", StringComparison.OrdinalIgnoreCase)) await SendRawAsync($"PRIVMSG {senderNick} :\u0001FACTION\u0001", cancellationToken);
                break;
        }
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
        if (outputPath is null || !File.Exists(outputPath) || !IsConnected) return;
        string[] lines;
        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, GameEncoding, false, 4096, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content)) return;
            stream.SetLength(0);
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException) { return; }

        foreach (var line in lines)
        {
            var parts = line.Split('/', 5);
            if (parts.Length < 2) continue;
            if (string.Equals(parts[0], "Handshake", StringComparison.OrdinalIgnoreCase))
            {
                await WriteRuntimeSettingsAsync(cancellationToken);
                continue;
            }
            if (string.Equals(parts[0], "Message", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                var faction = NormalizeFaction(parts[1]);
                if (_autoFaction) { _faction = faction; UpdateParticipant(_nick, _displayName, _faction); }
                var body = string.Join('/', parts.Skip(2));
                if (TryGetAiQuestion(body, out var question))
                {
                    await AskAiAsync(question, writeToGame: true, cancellationToken);
                    continue;
                }
                if (string.Equals(body.Trim(), "/whoami", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteGameInputAsync($"Message/system/ANTHOLOGY RELAY/False/Вы — {_displayName}. Роль: {RelayChatRoles.Label(_ownRole)}.", cancellationToken);
                    continue;
                }
                await SendRawAsync($"PRIVMSG {_channel} :{_faction}☺{_displayName}★{SanitizeMessage(body)}", cancellationToken);
                AddMessage(new RelayChatMessage(_displayName, _faction, body, DateTimeOffset.Now, IsOwn: true, Role: _ownRole));
            }
            else if (string.Equals(parts[0], "Death", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5 && _sendDeaths)
            {
                var faction = NormalizeFaction(parts[1]);
                if (_autoFaction) { _faction = faction; UpdateParticipant(_nick, _displayName, _faction); }
                var body = string.Join('/', parts.Skip(2));
                await SendRawAsync($"PRIVMSG {_channel} :{CreateDeathDisplayName(parts[2])}☻{_faction}☺{SanitizeMessage(body)}", cancellationToken);
            }
        }
    }

    private async Task WriteRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        if (!IsGameRunning()) return;
        await WriteGameInputAsync($"Setting/NewsDuration/{_newsDuration * 1000}", cancellationToken);
        await WriteGameInputAsync($"Setting/ChatKey/DIK_{_chatKey}", cancellationToken);
        await WriteGameInputAsync($"Setting/NewsSound/{_newsSound}", cancellationToken);
        await WriteGameInputAsync($"Setting/CloseChat/{_closeAfterSend}", cancellationToken);
        await WriteUsersAsync(cancellationToken);
    }

    private Task WriteUsersAsync(CancellationToken cancellationToken) => WriteGameInputAsync(
        $"Users/{string.Join('/', Participants.Select(item => item.DisplayName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase))}",
        cancellationToken);

    private async Task WriteGameInputAsync(string line, CancellationToken cancellationToken)
    {
        if (!IsGameRunning()) return;
        var inputPath = GetBridgePath("crc_input.txt");
        if (inputPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            var bytes = GameEncoding.GetBytes(line.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            await using var stream = new FileStream(inputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException) { }
    }

    private async Task SendRawAsync(string line, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextSendAtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            StreamWriter? writer;
            lock (_stateLock) { writer = _writer; }
            if (writer is null) throw new InvalidOperationException("IRC-соединение ещё не готово");
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            _nextSendAtUtc = DateTimeOffset.UtcNow + MinimumSendInterval;
        }
        finally { _sendGate.Release(); }
    }

    private void UpdateParticipant(string nick, string? displayName, string? faction)
    {
        if (string.IsNullOrWhiteSpace(nick)) return;
        lock (_stateLock)
        {
            _users.TryGetValue(nick, out var current);
            var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? current?.DisplayName ?? nick : displayName;
            _users[nick] = new ParticipantState(
                resolvedDisplayName,
                string.IsNullOrWhiteSpace(faction) ? current?.Faction ?? DefaultFaction : NormalizeFaction(faction),
                string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase)
                    ? _ownRole
                    : RelayChatRoles.Resolve(resolvedDisplayName));
        }
        StatusChanged?.Invoke();
    }

    private void RemoveParticipant(string nick)
    {
        lock (_stateLock) { _users.Remove(nick); }
        StatusChanged?.Invoke();
    }

    private string DisplayForNick(string nick)
    {
        lock (_stateLock) { return _users.TryGetValue(nick, out var participant) ? participant.DisplayName : nick; }
    }

    private string ParticipantFaction(string nick)
    {
        lock (_stateLock) { return _users.TryGetValue(nick, out var participant) ? participant.Faction : DefaultFaction; }
    }

    private async Task WarmAiHelperAsync(CancellationToken cancellationToken)
    {
        await _aiHelper.EnsureAvailableAsync(cancellationToken);
        StatusChanged?.Invoke();
    }

    private async Task AskAiAsync(string question, bool writeToGame, CancellationToken cancellationToken)
    {
        var cleanQuestion = SanitizeMessage(question);
        if (cleanQuestion.Length == 0)
        {
            AddSystemMessage("Использование: /ai вопрос");
            return;
        }

        AddMessage(new RelayChatMessage(_displayName, _faction, $"→ AI: {cleanQuestion}", DateTimeOffset.Now, IsOwn: true, Role: _ownRole));
        AddMessage(new RelayChatMessage("ANTHOLOGY AI", "ai", "Думаю…", DateTimeOffset.Now, Role: "ai"));
        var answer = await _aiHelper.AskAsync(cleanQuestion, cancellationToken);
        AddMessage(new RelayChatMessage("ANTHOLOGY AI", "ai", answer.Text, DateTimeOffset.Now, Role: "ai"));
        if (writeToGame)
        {
            await WriteGameInputAsync($"Message/actor_ecolog/ANTHOLOGY AI/False/{answer.Text}", cancellationToken);
        }
        StatusChanged?.Invoke();
    }

    private static bool TryGetAiQuestion(string text, out string question)
    {
        var clean = text.Trim();
        if (clean.Equals("/ai", StringComparison.OrdinalIgnoreCase))
        {
            question = string.Empty;
            return true;
        }
        if (clean.StartsWith("/ai ", StringComparison.OrdinalIgnoreCase))
        {
            question = clean[4..].Trim();
            return true;
        }
        question = string.Empty;
        return false;
    }

    private void AddSystemMessage(string text) => AddMessage(new RelayChatMessage("ANTHOLOGY RELAY", "system", text, DateTimeOffset.Now, IsSystem: true, Role: "system"));

    private void AddMessage(RelayChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            message = message with { Id = $"relay-{Guid.NewGuid():N}" };
        }
        lock (_stateLock)
        {
            _messages.Add(message);
            if (_messages.Count > MaximumMessages) _messages.RemoveRange(0, _messages.Count - MaximumMessages);
        }
        MessageReceived?.Invoke(message);
    }

    private void SetStatus(bool connected, string status)
    {
        IsConnected = connected;
        StatusText = status;
        StatusChanged?.Invoke();
    }

    private string? GetBridgePath(string fileName) => string.IsNullOrWhiteSpace(_gameRoot) ? null : Path.Combine(_gameRoot, "gamedata", "configs", fileName);
    private static string GetSenderNick(IrcMessage message) => message.Prefix.Split('!', 2)[0];
    private static string DisplayNameFromNick(string nick)
    {
        var display = nick.TrimStart('@', '+', '%', '~', '&');
        var suffixIndex = display.LastIndexOf('_');
        if (suffixIndex > 0)
        {
            var suffix = display[(suffixIndex + 1)..];
            if (suffix.Length is >= 6 and <= 8 && suffix.All(Uri.IsHexDigit))
            {
                display = display[..suffixIndex];
            }
        }
        return display.Replace('_', ' ').Trim();
    }
    private static string SanitizeMessage(string? message) { var clean = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim(); return clean.Length <= 380 ? clean : clean[..380]; }
    private static string NormalizeDisplayName(string? value) { var clean = SanitizeMessage(string.IsNullOrWhiteSpace(value) ? "Stalker" : value).Replace('★', '_'); return clean.Length <= 48 ? clean : clean[..48]; }
    private static string NormalizeChannel(string? value)
    {
        var channel = value?.Trim().ToLowerInvariant();
        return channel is "#cocrc_english" or "#cocrc_english_rp" or "#cocrc_slavik" ? channel : DefaultChannel;
    }

    private static string NormalizeFaction(string? value)
    {
        var faction = value?.Trim().ToLowerInvariant();
        return faction is "actor_bandit" or "actor_csky" or "actor_dolg" or "actor_ecolog" or "actor_freedom" or "actor_stalker" or "actor_killer" or "actor_army" or "actor_monolith" or "actor_renegade" or "actor_zombied" ? faction : DefaultFaction;
    }
    private static string NormalizeChatKey(string? value) { var key = string.IsNullOrWhiteSpace(value) ? "RETURN" : value.Trim().ToUpperInvariant(); return key.StartsWith("DIK_", StringComparison.Ordinal) ? key[4..] : key; }
    private static string CreateDeathDisplayName(string fallback) => string.IsNullOrWhiteSpace(fallback) ? "Stalker" : fallback.Trim();

    private static string BuildConfigurationKey(string gameRoot, LauncherSettings settings) => string.Join('|', gameRoot.ToUpperInvariant(), NormalizeDisplayName(settings.CommunityNickname), NormalizeChannel(settings.RelayChatChannel), settings.RelayChatAutoFaction, NormalizeFaction(settings.RelayChatFaction), settings.RelayChatSendDeaths, settings.RelayChatReceiveDeaths, Math.Clamp(settings.RelayChatDeathInterval, 0, 3600), Math.Clamp(settings.RelayChatNewsDuration, 1, 60), NormalizeChatKey(settings.RelayChatKey), settings.RelayChatNewsSound, settings.RelayChatCloseAfterSend);

    private static string CreateIrcNick(string displayName)
    {
        var safe = new string(displayName.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(20).ToArray());
        if (safe.Length == 0 || char.IsDigit(safe[0])) safe = "Anthology" + safe;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{safe}_{suffix}"[..Math.Min(30, safe.Length + suffix.Length + 1)];
    }

    private static bool IsGameRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.ProcessName.StartsWith("Anomaly", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private static Encoding CreateGameEncoding() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1251); }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetimeCancellation?.Cancel(); _aiHelper.Dispose(); }
    public async ValueTask DisposeAsync() { if (_disposed) return; await StopAsync(); _disposed = true; _aiHelper.Dispose(); _sendGate.Dispose(); }

    private sealed record ParticipantState(string DisplayName, string Faction, string Role);

    private sealed class RelayChatBackoffException(string message, TimeSpan retryAfter) : InvalidOperationException(message)
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
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
                if (prefixEnd > 0) { prefix = remaining[1..prefixEnd]; remaining = remaining[(prefixEnd + 1)..]; }
            }
            var trailing = string.Empty;
            var trailingIndex = remaining.IndexOf(" :", StringComparison.Ordinal);
            if (trailingIndex >= 0) { trailing = remaining[(trailingIndex + 2)..]; remaining = remaining[..trailingIndex]; }
            var tokens = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new IrcMessage(prefix, tokens.FirstOrDefault()?.ToUpperInvariant() ?? string.Empty, tokens.Skip(1).ToArray(), trailing);
        }
    }
}

internal static class RelayChatRoles
{
    public static string Resolve(string? displayName) => Anthology.Contracts.AnthologyRoles.Resolve(displayName);

    public static string Label(string role) => Anthology.Contracts.AnthologyRoles.Label(role);
}
