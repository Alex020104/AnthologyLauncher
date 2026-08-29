using Anthology.Community.Api;
using Anthology.Contracts;
using System.Net;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CommunityState>();
builder.Services.AddHttpClient<TranslationGateway>(client => client.Timeout = TimeSpan.FromSeconds(90));
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
api.MapGet("/channels/{channelId}/messages", (string channelId, CommunityState state) =>
    state.ChannelExists(channelId)
        ? Results.Ok(state.GetMessages(channelId))
        : Results.NotFound());
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
api.MapGet("/bug-reports", (HttpRequest request, string? status, CommunityState state) =>
{
    if (!IsDeveloperAuthorized(request))
    {
        return Results.Unauthorized();
    }
    if (!string.IsNullOrWhiteSpace(status) && !BugReportStatuses.IsSupported(status))
    {
        return Results.BadRequest(new { error = "Неизвестный статус обращения." });
    }
    return Results.Ok(state.GetReports(status));
});
api.MapGet("/bug-reports/{reportId}", (string reportId, HttpRequest request, CommunityState state) =>
{
    if (!HasReportAccess(request, state, reportId))
    {
        return Results.Unauthorized();
    }
    return state.GetReport(reportId) is { } report ? Results.Ok(report) : Results.NotFound();
});
api.MapPost("/bug-reports/{reportId}/messages", (
    string reportId,
    BugReportReplyRequest reply,
    HttpRequest request,
    CommunityState state) =>
{
    var developer = IsDeveloperAuthorized(request);
    if (!developer && !HasReportAccess(request, state, reportId))
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
    CommunityState state) =>
{
    if (!IsDeveloperAuthorized(request))
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
    CommunityState state) =>
{
    if (!HasReportAccess(request, state, reportId))
    {
        return Results.Unauthorized();
    }
    return state.GetAttachmentPath(reportId, fileName) is { } path
        ? Results.File(path, "application/octet-stream", Path.GetFileName(path))
        : Results.NotFound();
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
api.MapPost("/bug-reports/{reportId}/attachments", async (
    string reportId,
    HttpRequest request,
    CommunityState state,
    CancellationToken cancellationToken) =>
{
    if (!HasReportAccess(request, state, reportId))
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
        var attachments = await state.SaveAttachmentsAsync(reportId, form.Files, cancellationToken);
        return Results.Ok(attachments);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapHub<CommunityHub>("/hubs/community");
app.Run();

static bool HasReportAccess(HttpRequest request, CommunityState state, string reportId) =>
    IsDeveloperAuthorized(request)
    || state.ReportTokenMatches(reportId, request.Headers["X-Anthology-Report-Token"].FirstOrDefault());

static bool IsDeveloperAuthorized(HttpRequest request)
{
    var configured = Environment.GetEnvironmentVariable("ANTHOLOGY_DEVELOPER_TOKEN");
    if (string.IsNullOrWhiteSpace(configured))
    {
        var remoteAddress = request.HttpContext.Connection.RemoteIpAddress;
        return remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
    }

    var supplied = request.Headers["X-Anthology-Developer-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(supplied))
    {
        return false;
    }
    var expectedBytes = Encoding.UTF8.GetBytes(configured.Trim());
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
    return expectedBytes.Length == suppliedBytes.Length
           && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

public partial class Program;
