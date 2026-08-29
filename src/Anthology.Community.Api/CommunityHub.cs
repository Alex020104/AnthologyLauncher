using Anthology.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Anthology.Community.Api;

public sealed class CommunityHub(CommunityState state, DeveloperAccess developerAccess) : Hub
{
    public async Task JoinChannel(string channelId)
    {
        if (!state.ChannelExists(channelId))
        {
            throw new HubException("Канал не найден.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, channelId);
    }

    public Task LeaveChannel(string channelId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, channelId);

    public async Task SendMessage(string channelId, string authorId, string authorName, string text)
    {
        ChatMessage message;
        try
        {
            message = state.CreateMessage(
                channelId,
                new ChatMessageRequest(authorId, authorName, text),
                developerAccess.IsAuthorized(Context.GetHttpContext()));
        }
        catch (KeyNotFoundException)
        {
            throw new HubException("Канал не найден.");
        }
        catch (ArgumentException exception)
        {
            throw new HubException(exception.Message);
        }

        await Clients.Group(channelId).SendAsync("messageReceived", message);
    }
}
