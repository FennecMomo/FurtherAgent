using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using FurtherAgent.Core;

namespace FurtherAgent.Server;

public sealed record MailboxOnlineDevice(string Code, string Name);

public static class MailboxEndpoints
{
    public static void MapMailboxes(this WebApplication app, MailboxStore store, Func<string, MailboxOnlineDevice> onlineDevice)
    {
        // Leave the established relay's 4 KiB handshake limit unchanged. A text
        // message can expand to six JSON bytes per UTF-16 character.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mailboxes") &&
                context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = 64 * 1024;
            await next();
        });
        app.MapPost("/mailboxes/pair", (MailboxPair input) => Run(() =>
        {
            var device = onlineDevice(input.Code);
            return Results.Ok(store.PairController(device.Code));
        }));
        app.MapGet("/mailboxes/{code}/status", (string code, HttpRequest request) =>
            Run(() => Results.Ok(store.Status(code, Bearer(request)))));
        app.MapGet("/mailboxes/{code}/messages", (string code, HttpRequest request) => Run(() =>
            Results.Ok(store.Page(code, Bearer(request), Number(request, "before"), Number(request, "after"), Limit(request)))));
        app.MapPost("/mailboxes/{code}/messages", (string code, MailboxSend input, HttpRequest request) => Run(() =>
        {
            var result = store.Send(code, Bearer(request), input);
            return Results.Json(result.Message, Json.Options, statusCode: result.Created ? 201 : 200);
        }));
        app.MapPost("/mailboxes/{code}/read", (string code, MailboxRead input, HttpRequest request) =>
            Run(() => Results.Ok(store.Read(code, Bearer(request), input.Through))));
    }

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (MailboxException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return Results.Json(new { error = "留言存储暂不可用，请稍后重试。" }, statusCode: 503); }
    }
    private static string Bearer(HttpRequest request)
    {
        string value = request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new MailboxException(401, "留言箱需要已配对的凭据。");
        string token = value[7..].Trim();
        if (token.Length == 0) throw new MailboxException(401, "留言箱需要已配对的凭据。");
        return token;
    }
    private static long? Number(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var text)) return null;
        if (!long.TryParse(text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new MailboxException(400, "留言分页参数无效。");
        return value;
    }
    private static int Limit(HttpRequest request)
    {
        long? limit = Number(request, "limit");
        if (limit is null) return 40;
        if (limit < 1 || limit > int.MaxValue) throw new MailboxException(400, "留言分页数量无效。");
        return (int)limit;
    }
}
