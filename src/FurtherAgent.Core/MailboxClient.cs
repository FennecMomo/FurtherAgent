using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FurtherAgent.Core;

// Independent HTTP mailbox. No Agent session or model call is made by this client.
public sealed class MailboxClient : IDisposable
{
    public const int MaxTextLength = 8000;
    private readonly HttpClient http;
    public string Server { get; }
    public MailboxBinding Binding { get; }
    private static Uri Address(string server)
    {
        var uri = new Uri(server.TrimEnd('/') + '/');
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("留言服务需要 HTTPS 或本机回环 HTTP 地址。");
        return uri;
    }
    public MailboxClient(string server, MailboxBinding binding)
    {
        Server = Address(server).AbsoluteUri; Binding = binding;
        if (string.IsNullOrWhiteSpace(binding.Code) || string.IsNullOrWhiteSpace(binding.Token)) throw new ArgumentException("留言箱尚未绑定。");
        http = DirectNetwork.CreateHttpClient(TimeSpan.FromSeconds(60)); http.BaseAddress = new Uri(Server);
        http.DefaultRequestHeaders.Authorization = new("Bearer", binding.Token);
    }
    public static async Task<MailboxBinding> PairAsync(string server, string code, CancellationToken ct = default)
    {
        using var http = DirectNetwork.CreateHttpClient(TimeSpan.FromSeconds(30)); http.BaseAddress = Address(server);
        using var response = await http.PostAsJsonAsync("mailboxes/pair", new MailboxPair(code), Json.Options, ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MailboxBinding>(Json.Options, ct) ?? throw new IOException("留言箱绑定返回为空。");
    }
    public Task<MailboxStatus> StatusAsync(CancellationToken ct = default) => GetAsync<MailboxStatus>("status", ct);
    public Task<MailboxPage> FetchAsync(long? before = null, long? after = null, int limit = 40, CancellationToken ct = default)
    {
        if (before is not null && after is not null || before < 0 || after < 0 || limit is < 1 or > 50) throw new ArgumentException("留言分页参数无效。");
        string query = "messages?limit=" + limit + (before is { } b ? "&before=" + b : "") + (after is { } a ? "&after=" + a : "");
        return GetAsync<MailboxPage>(query, ct);
    }
    public async Task<MailboxMessage> SendAsync(string text, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength || !Guid.TryParse(id, out var key)) throw new ArgumentException("留言不能为空、最多 8000 字符，并需要有效消息 ID。");
        using var response = await http.PostAsJsonAsync(PathFor("messages"), new MailboxSend(key.ToString("N"), text), Json.Options, ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MailboxMessage>(Json.Options, ct) ?? throw new IOException("留言保存结果为空，请使用原消息 ID 重试。");
    }
    public async Task MarkReadAsync(long through, CancellationToken ct = default)
    {
        if (through < 0) throw new ArgumentOutOfRangeException(nameof(through));
        using var response = await http.PostAsJsonAsync(PathFor("read"), new MailboxRead(through), Json.Options, ct);
        await CheckAsync(response, ct);
    }
    private string PathFor(string suffix) => "mailboxes/" + Uri.EscapeDataString(Binding.Code) + "/" + suffix;
    private async Task<T> GetAsync<T>(string suffix, CancellationToken ct)
    {
        using var response = await http.GetAsync(PathFor(suffix), ct); await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json.Options, ct) ?? throw new IOException("留言服务返回为空。");
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        // Deliberately do not print response bodies, bearer tokens or private mailbox identifiers.
        if (response.StatusCode == HttpStatusCode.NotFound) throw new NotSupportedException("中继尚未启用留言功能，或留言箱尚未建立；请先升级中继并在家里接入端在线时完成首次绑定。");
        if (response.StatusCode == HttpStatusCode.Conflict) throw new IOException("留言消息 ID 与已有内容冲突；请保留原草稿核对发送状态。");
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new HttpRequestException("留言绑定已失效，请在接入端在线时重新绑定。", null, response.StatusCode);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge) throw new IOException("留言内容或分页参数无效。");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) throw new IOException("首次绑定留言箱需要家里接入端在线；已绑定的留言箱可继续离线投递。");
        await Task.CompletedTask;
        throw new HttpRequestException("留言服务暂不可用：HTTP " + (int)response.StatusCode, null, response.StatusCode);
    }
    public void Dispose() => http.Dispose();
}
