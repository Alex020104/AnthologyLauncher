using Anthology.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Anthology.Community.Api;

public sealed class CommunityHub(CommunityState state) : Hub
{
    public async Task JoinChannel(string channelId)
    {
        if (!state.ChannelExists(channelId))
        {
            throw new HubException("Канал не найден.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, channelId);
    }

    public async Task LeaveChannel(string channelId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channelId);

    public async Task SendMessage(string channelId, string authorId, string authorName, string text)
    {
        if (!state.ChannelExists(channelId))
        {
            throw new HubException("Канал не найден.");
        }

        authorId = RequireText(authorId, 96, "Не указан пользователь.");
        authorName = RequireText(authorName, 64, "Не указано имя.");
        text = RequireText(text, 2_000, "Сообщение пустое.");
        var message = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            channelId,
            authorId,
            authorName,
            text,
            DateTimeOffset.UtcNow);
        state.AppendMessage(message);
        await Clients.Group(channelId).SendAsync("messageReceived", message);
    }

    private static string RequireText(string value, int maximumLength, string error)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            throw new HubException(error);
        }

        return normalized;
    }
}
