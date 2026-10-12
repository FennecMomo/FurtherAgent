using System.Net;
using System.Text;
using FurtherAgent.Core;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace FurtherAgent.Mobile;

public static class Transcript
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private static string E(string text) => WebUtility.HtmlEncode(text);
    private sealed class SafeLink : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            bool allowed = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "attachment";
            if (allowed) { renderer.Write("<a href=\""); renderer.WriteEscapeUrl(link.Url); renderer.Write("\">"); }
            renderer.WriteChildren(link);
            if (allowed) renderer.Write("</a>");
        }
    }
    private sealed class SafeAutoLink : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link)
        {
            bool allowed = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
            if (allowed) { renderer.Write("<a href=\""); renderer.WriteEscapeUrl(link.Url); renderer.Write("\">"); }
            renderer.WriteEscape(link.Url);
            if (allowed) renderer.Write("</a>");
        }
    }
    private static string MarkdownHtml(string text)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer); Pipeline.Setup(renderer);
        renderer.ObjectRenderers.RemoveAll(r => r is LinkInlineRenderer or AutolinkInlineRenderer);
        renderer.ObjectRenderers.Add(new SafeLink()); renderer.ObjectRenderers.Add(new SafeAutoLink());
        renderer.Render(Markdown.Parse(text, Pipeline));
        return writer.ToString();
    }
    public static string Render(SessionView view, bool hasOlder = false, bool readingOlder = false)
    {
        var html = new StringBuilder();
        if (hasOlder) html.Append("<p class='history'><a id='older' href='fa-history://older'>上滑或点此加载更早的记录</a></p>");
        else html.Append("<p class='history'>已到已加载历史的开头</p>");
        for (int i = 0; i < Math.Min(32, view.Messages.Count); i++)
        {
            var entry = ChatPresentation.Display(view.Messages[i]);
            if (!HistoryPaging.IsConversation(entry)) continue;
            string kind = ChatPresentation.Kind(entry);
            string text = entry.Text.Length <= 8000 ? entry.Text : entry.Text[..8000] + "\n[长内容显示已截断，可复制原文或在电脑查看]";
            // Large tool output and compaction summaries are plain text, not Markdown trees.
            string content;
            try { content = kind is "tool" or "reasoning" or "system" || entry.Text.Length > 8000 ? "<pre class='plain'>" + E(text) + "</pre>" : MarkdownHtml(text); }
            catch (Exception) { content = "<pre class='plain'>" + E(text) + "</pre>"; }
            html.Append($"<section id='m-{(view.History?.Start ?? 0) + i}'>");
            if (kind is "tool" or "reasoning" or "system")
                html.Append($"<details><summary>{E(entry.Role)}</summary><div class='body'>{content}<pre>{E(entry.Detail is { Length: > 2000 } d ? d[..2000] + "\n[详情截断]" : entry.Detail ?? "")}</pre></div></details>");
            else
                html.Append($"<article class='{kind}'><div class='who'>{(kind == "user" ? "你" : "FurtherAgent")}<a class='copy' href='fa-copy://{i}'>复制</a></div><div class='body'>{content}</div></article>");
            html.Append("</section>");
        }
        if (readingOlder) html.Append("<p class='history'><a href='fa-history://latest'>回到最新记录</a></p>");
        if (view.OutputFiles is { Count: > 0 })
        {
            html.Append("<aside class='files'><b>会话文件</b>");
            foreach (var file in view.OutputFiles)
                html.Append($"<p><a href='attachment:{Uri.EscapeDataString(file.Name)}'>{E(file.Name)} · {file.Size / 1024d:N0} KB · 下载</a></p>");
            html.Append("</aside>");
        }
        if (view.Messages.Count == 0) html.Append("<div class='empty'><b>从这里继续工作</b><p>发送一条消息，远端 Agent 会在原会话中回复。</p></div>");
        return html.ToString();
    }

    // Remote HTML/scripts/images cannot execute or load. Links are intercepted by the native page.
    public const string Shell = """
<!doctype html><html lang="zh"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
<style>
:root{color-scheme:light dark;--bg:#f6f8f5;--card:#fff;--text:#20372c;--muted:#708176;--line:#e0e7df;--user:#e4efe7}*{box-sizing:border-box}body{margin:0;padding:18px 16px 36px;background:var(--bg);color:var(--text);font:15px/1.75 system-ui,sans-serif;overflow-wrap:anywhere}article{margin:0 0 20px}.user{background:var(--user);border-radius:18px 18px 5px 18px;padding:12px 15px;margin-left:28px}.assistant{background:var(--card);border:1px solid var(--line);border-radius:5px 18px 18px 18px;padding:14px 16px;margin-right:8px}.who{font-size:11px;font-weight:650;color:var(--muted);margin-bottom:7px;letter-spacing:.3px}.copy{float:right;font-size:11px;text-decoration:none;color:var(--muted)}p{margin:8px 0}p:first-child{margin-top:0}p:last-child{margin-bottom:0}h1,h2,h3{font-size:18px;line-height:1.4}pre{overflow-x:auto;background:var(--bg);padding:12px;border-radius:9px;font:12px/1.65 monospace;white-space:pre}code{font:13px monospace;background:var(--bg)}table{display:block;overflow-x:auto;border-collapse:collapse;font-size:13px}td,th{border:1px solid var(--line);padding:7px 10px;min-width:70px}blockquote{margin:10px 0;border-left:3px solid #81ad93;padding-left:12px;color:var(--muted)}a{color:#428062}img{max-width:100%}details{border:1px solid var(--line);border-radius:10px;margin:10px 0;padding:9px 12px;font-size:13px;color:var(--muted)}summary{cursor:pointer}.empty{text-align:center;padding:64px 20px;color:var(--muted)}.empty b{color:var(--text);font-size:20px}ul,ol{padding-left:22px}@media(prefers-color-scheme:dark){:root{--bg:#101c17;--card:#182820;--text:#e4ece6;--muted:#a1b4a7;--line:#2b4034;--user:#264637}}
.history{text-align:center;font-size:12px;color:var(--muted)}pre.plain{white-space:pre-wrap;word-break:break-word}
</style></head><body><main id="chat"></main><script>
let armed=false,loading=false;
function older(){if(!loading&&document.getElementById('older')){loading=true;location.href='fa-history://older';}}
addEventListener('touchmove',()=>{armed=true;if(scrollY<30)older();},{passive:true});
addEventListener('wheel',e=>{armed=true;if(e.deltaY<0&&scrollY<30)older();},{passive:true});
addEventListener('scroll',()=>{if(armed&&scrollY<30){armed=false;older();}},{passive:true});
function historyLoadDone(){loading=false;}
function renderTranscript(html, reset,prepend){const bottom=innerHeight+scrollY>=document.body.scrollHeight-100;const y=scrollY;const anchor=[...document.querySelectorAll('section')].find(x=>x.getBoundingClientRect().bottom>0);const aid=anchor?.id;const ay=anchor?.getBoundingClientRect().top;const opened=new Set([...document.querySelectorAll('section details[open]')].map(x=>x.parentElement.id));document.getElementById('chat').innerHTML=html;if(!reset)document.querySelectorAll('section details').forEach(x=>x.open=opened.has(x.parentElement.id));armed=false;loading=false;requestAnimationFrame(()=>{const a=aid&&document.getElementById(aid);if(prepend&&a)scrollTo(0,scrollY+a.getBoundingClientRect().top-ay);else scrollTo(0,reset||bottom?document.body.scrollHeight:y);});}
</script></body></html>
""";

    public static SessionView Preview => new("preview", "随时接回你的工作", "", "idle", null,
    [new("user", "帮我梳理一下 Android 首版的重点。"),
     new("assistant", "## 工作继续，地点随你\n\n在手机上查看远端会话、继续对话，及时处理需要你确认的事项。\n\n- **会话同步**：接回电脑上的原会话\n- **文件交付**：上传资料，下载任务产物\n- **可靠连接**：断线恢复读取，不重复发送\n\n这是**离线界面预览**，不会连接设备或执行任务。"),
     new("工具", "已整理首版功能。", "此处展示工具过程。真实连接后会显示远端返回的详情。"),
     new("assistant", "你可以先打开右上角的 **连接**，填写远端授权码。\n\n```text\nAndroid → 中继 ← 电脑 / 服务器 → Agent\n```\n\n配置保存在手机，远端任务仍在原设备执行。")], [], CanSend: false, CanStop: false);
}
