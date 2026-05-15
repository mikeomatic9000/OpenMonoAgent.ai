using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Tools;

namespace OpenMono.Acp;

public static class AcpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/sessions", PostSession);
        app.MapGet("/api/v1/sessions/{id}", GetSession);
        app.MapPost("/api/v1/sessions/{id}/turn", PostTurn);
    }

    private static async Task<IResult> PostSession(HttpContext ctx, AcpSessionStore store, AppConfig config)
    {
        CreateSessionBody? body = null;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(raw))
                body = JsonSerializer.Deserialize<CreateSessionBody>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }

        var session = store.Create(body?.Model, body?.ClientTools, config);
        return Results.Ok(new
        {
            session_id = session.Id,
            model = session.Model,
            client_tools = session.ClientTools,
        });
    }

    private static IResult GetSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        return session is null ? Results.NotFound() : Results.Ok(new { session_id = session.Id, model = session.Model });
    }

    private static async Task PostTurn(
        HttpContext ctx,
        string id,
        AcpSessionStore store,
        AcpServerSettings settings,
        AppConfig config,
        ILlmClient llm,
        ToolRegistry toolRegistry)
    {
        var session = store.TryGet(id);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await session.TurnLock.WaitAsync(0, ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            await ctx.Response.WriteAsJsonAsync(new { error = "session_busy" }, ctx.RequestAborted);
            return;
        }

        try
        {
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json", detail = ex.Message }, ctx.RequestAborted);
                return;
            }

            using (body)
            {
                var root = body.RootElement;
                var hasMessage = root.TryGetProperty("message", out var msgEl);
                var hasResults = root.TryGetProperty("tool_results", out var resultsEl);

                if (!hasMessage && !hasResults)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(
                        new { error = "missing_body_key", detail = "body must contain `message` or `tool_results`" },
                        ctx.RequestAborted);
                    return;
                }

                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                var writer = new SseWriter(ctx.Response.Body, ctx.RequestAborted);
                var runner = new AcpTurnRunner(session, writer, store, llm, toolRegistry, config, settings);

                try
                {
                    if (hasMessage)
                        await runner.RunUserMessageAsync(msgEl.GetString() ?? "", ctx.RequestAborted);
                    else
                        await runner.ResumeWithToolResultsAsync(resultsEl, ctx.RequestAborted);
                }
                catch (InvalidOperationException ex)
                {
                    await writer.WriteEventAsync("error", new { message = ex.Message });
                }
            }
        }
        finally
        {
            session.LastActivityAt = DateTime.UtcNow;
            store.Save(session);
            session.TurnLock.Release();
        }
    }

    private sealed class CreateSessionBody
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("client_tools")]
        public List<string>? ClientTools { get; set; }
    }
}
