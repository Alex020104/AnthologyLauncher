using Anthology.Community.Api;
using Anthology.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CommunityState>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
            builder.Environment.IsDevelopment()
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.IsLoopback)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new
{
    service = "anthology-community-api",
    status = "ok",
    time = DateTimeOffset.UtcNow,
}));

var api = app.MapGroup("/api/v1");
api.MapGet("/feed", (CommunityState state) => Results.Ok(state.GetFeed()));
api.MapGet("/polls/{pollId}", (string pollId, CommunityState state) =>
    state.GetPoll(pollId) is { } poll ? Results.Ok(poll) : Results.NotFound());
api.MapPost("/polls/{pollId}/votes", (string pollId, PollVoteRequest vote, CommunityState state) =>
{
    try
    {
        return state.Vote(pollId, vote) is { } poll ? Results.Ok(poll) : Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
api.MapPost("/bug-reports", (BugReportRequest report, CommunityState state) =>
{
    try
    {
        var receipt = state.CreateReport(report);
        return Results.Created($"/api/v1/bug-reports/{receipt.Id}", receipt);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapHub<CommunityHub>("/hubs/community");
app.Run();

public partial class Program;
