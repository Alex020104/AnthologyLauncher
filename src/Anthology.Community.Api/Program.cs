using System.Net;
using System.Threading.RateLimiting;
using Anthology.Community.Api;
using Anthology.Contracts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "Anthology Community Server");

var configuredDataRoot = builder.Configuration["Community:DataRoot"];
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHOLOGY_DATA_ROOT"))
    && !string.IsNullOrWhiteSpace(configuredDataRoot))
{
    Environment.SetEnvironmentVariable("ANTHOLOGY_DATA_ROOT", configuredDataRoot);
}
var configuredTranslationUrl = builder.Configuration["Translation:ApiUrl"];
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API"))
    && !string.IsNullOrWhiteSpace(configuredTranslationUrl))
{
    Environment.SetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API", configuredTranslationUrl);
}
var configuredTranslationKey = builder.Configuration["Translation:ApiKey"];
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API_KEY"))
    && !string.IsNullOrWhiteSpace(configuredTranslationKey))
{
    Environment.SetEnvironmentVariable("ANTHOLOGY_TRANSLATION_API_KEY", configuredTranslationKey);
}

builder.Services.AddSingleton<DeveloperAccess>();
builder.Services.AddSingleton<CommunityState>();
builder.Services.AddHttpClient<TranslationGateway>(client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 16 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 180,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            }));
});

var app = builder.Build();
_ = app.Services.GetRequiredService<DeveloperAccess>();
_ = app.Services.GetRequiredService<CommunityState>();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data: https:; "
            + "media-src 'self' https:; style-src 'self'; script-src 'self'; base-uri 'self'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", (CommunityState state) =>
{
    var storage = state.GetStorageStatus();
    return Results.Ok(new
    {
        service = "anthology-community-server",
        status = "ok",
        storage = storage.Engine,
        reports = storage.Reports,
        messages = storage.Messages,
        time = DateTimeOffset.UtcNow,
    });
});

var api = app.MapGroup("/api/v1");
api.MapGet("/feed", (CommunityState state) => Results.Ok(state.GetFeed()));
api.MapGet("/polls/{pollId}", (string pollId, CommunityState state) =>
    state.GetPoll(pollId) is { } poll ? Results.Ok(poll) : Results.NotFound());
api.MapGet("/channels/{channelId}/messages", (string channelId, CommunityState state) =>
    state.ChannelExists(channelId)
        ? Results.Ok(state.GetMessages(channelId))
        : Results.NotFound());
api.MapPost("/channels/{channelId}/messages", async (
    string channelId,
    ChatMessageRequest message,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess,
    IHubContext<CommunityHub> hub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var created = state.CreateMessage(channelId, message, developerAccess.IsAuthorized(request));
        await hub.Clients.Group(channelId).SendAsync("messageReceived", created, cancellationToken);
        return Results.Ok(created);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
api.MapDelete("/channels/{channelId}/messages/{messageId}", (
    string channelId,
    string messageId,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    return state.DeleteMessage(channelId, messageId) ? Results.NoContent() : Results.NotFound();
});
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
api.MapGet("/bug-reports", (
    HttpRequest request,
    string? status,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    if (!string.IsNullOrWhiteSpace(status) && !BugReportStatuses.IsSupported(status))
    {
        return Results.BadRequest(new { error = "Неизвестный статус обращения." });
    }
    return Results.Ok(state.GetReports(status));
});
api.MapGet("/bug-reports/{reportId}", (
    string reportId,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!HasReportAccess(request, state, developerAccess, reportId))
    {
        return Results.Unauthorized();
    }
    return state.GetReport(reportId) is { } report ? Results.Ok(report) : Results.NotFound();
});
api.MapDelete("/bug-reports/{reportId}", (
    string reportId,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    return state.DeleteReport(reportId) ? Results.NoContent() : Results.NotFound();
});
api.MapPost("/bug-reports/{reportId}/messages", (
    string reportId,
    BugReportReplyRequest reply,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    var developer = developerAccess.IsAuthorized(request);
    if (!developer && !HasReportAccess(request, state, developerAccess, reportId))
    {
        return Results.Unauthorized();
    }
    try
    {
        return Results.Ok(state.AddReportMessage(reportId, reply, developer));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});
api.MapPatch("/bug-reports/{reportId}/status", (
    string reportId,
    BugReportStatusRequest status,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    try
    {
        return Results.Ok(state.SetReportStatus(reportId, status));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
api.MapGet("/bug-reports/{reportId}/attachments/{fileName}", (
    string reportId,
    string fileName,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!HasReportAccess(request, state, developerAccess, reportId))
    {
        return Results.Unauthorized();
    }
    return state.GetAttachmentPath(reportId, fileName) is { } path
        ? Results.File(path, "application/octet-stream", Path.GetFileName(path))
        : Results.NotFound();
});
api.MapPost("/bug-reports/{reportId}/attachments", async (
    string reportId,
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess,
    CancellationToken cancellationToken) =>
{
    if (!HasReportAccess(request, state, developerAccess, reportId))
    {
        return Results.Unauthorized();
    }
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
    }
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        return Results.Ok(await state.SaveAttachmentsAsync(reportId, form.Files, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapPost("/translate", async (
    TextTranslationRequest request,
    TranslationGateway translations,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await translations.TranslateAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

api.MapGet("/admin/status", (
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(state.GetStorageStatus());
});
api.MapPost("/admin/backups", (
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess) =>
{
    if (!developerAccess.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }
    return Results.Ok(state.CreateBackup());
});

app.MapHub<CommunityHub>("/hubs/community");
app.MapFallbackToFile("index.html");
app.Run();

static bool HasReportAccess(
    HttpRequest request,
    CommunityState state,
    DeveloperAccess developerAccess,
    string reportId) =>
    developerAccess.IsAuthorized(request)
    || state.ReportTokenMatches(reportId, request.Headers["X-Anthology-Report-Token"].FirstOrDefault());

public partial class Program;
