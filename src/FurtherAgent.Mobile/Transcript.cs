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
    private sealed class SafeLink(SessionView view) : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            var file = RemoteFileReferences.Resolve(view.OutputFiles, link.Url ?? "");
            if (file is not null) { renderer.Write(AttachmentHtml(file)); return; }
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
    private static string MarkdownHtml(string text, SessionView view)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer); Pipeline.Setup(renderer);
        renderer.ObjectRenderers.RemoveAll(r => r is LinkInlineRenderer or AutolinkInlineRenderer);
        renderer.ObjectRenderers.Add(new SafeLink(view)); renderer.ObjectRenderers.Add(new SafeAutoLink());
        renderer.Render(Markdown.Parse(text, Pipeline));
        return writer.ToString();
    }
    private static string AttachmentHtml(RemoteFile file)
    {
        string link = "attachment:" + Uri.EscapeDataString(file.Id), label = E(file.Name);
        return ImageThumbnail.Supported(file.Name)
            ? $"<a class='attachment-image' href='{E(link)}'><img data-attachment='{E(file.Id)}' data-version='{E(file.Version)}' alt='{label}' width='320' height='240'><span>{label} · 点击打开原图</span></a>"
            : $"<a href='{E(link)}'>{label}</a>";
    }
    public static string Render(SessionView view, bool hasOlder = false, bool readingOlder = false, int conversationLimit = 32)
    {
        var html = new StringBuilder();
        if (hasOlder) html.Append("<p class='history'><a id='older' href='fa-history://older'>上滑或点此加载更早的记录</a></p>");
        else html.Append("<p class='history'>已到已加载历史的开头</p>");
        int renderCount = OperationGroups.TakeConversations(view.Messages, conversationLimit).Count;
        bool operationGroup = false;
        for (int i = 0; i < renderCount; i++)
        {
            var entry = ChatPresentation.Display(view.Messages[i]);
            if (entry.Operations is { } operations)
            {
                if (operationGroup) { html.Append("</details></section>"); operationGroup = false; }
                html.Append($"<section id='m-{(view.History?.Start ?? 0) + i}'><details class='operations' id='ops-{(view.History?.Start ?? 0) + i}'><summary>{E(entry.Text)}</summary>");
                for (int j = 0; j < operations.Count; j++)
                {
                    var op = OperationSummary.Display(operations[j]);
                    html.Append($"<p class='operation'>{E(op.Text)}</p>");
                }
                html.Append("</details></section>"); continue;
            }
            string kind = ChatPresentation.Kind(entry);
            string text = entry.Text.Length <= 8000 ? entry.Text : entry.Text[..8000] + "\n[长内容显示已截断，可复制原文或在电脑查看]";
            // Large tool output and compaction summaries are plain text, not Markdown trees.
            string content;
            try { content = kind is "tool" or "reasoning" or "system" || entry.Text.Length > 8000 ? "<pre class='plain'>" + E(text) + "</pre>" : MarkdownHtml(text, view); }
            catch (Exception) { content = "<pre class='plain'>" + E(text) + "</pre>"; }
            bool operation = kind is "tool" or "reasoning" or "system";
            if (operation && !operationGroup)
            {
                int count = 0;
                while (i + count < renderCount && !HistoryPaging.IsConversation(view.Messages[i + count])) count++;
                // A snapshot does not prove individual tools succeeded. Never invent a completion status.
                html.Append($"<section id='m-{(view.History?.Start ?? 0) + i}'><details class='operations' id='ops-{(view.History?.Start ?? 0) + i}'><summary>{count} 项操作记录</summary>");
                operationGroup = true;
            }
            if (!operation && operationGroup) { html.Append("</details></section>"); operationGroup = false; }
            if (operation)
                html.Append($"<p class='operation'>{E(OperationSummary.Display(entry).Text)}</p>");
            else
                html.Append($"<section id='m-{(view.History?.Start ?? 0) + i}'><article class='{kind}'><div class='body'>{content}</div><a class='copy' aria-label='复制消息' href='fa-copy://{i}'>复制</a></article></section>");
        }
        if (operationGroup) html.Append("</details></section>");
        if (readingOlder) html.Append("<p class='history'><a href='fa-history://latest'>回到最新记录</a></p>");
        if (view.OutputFiles is { Count: > 0 })
        {
            foreach (var picture in view.OutputFiles.Where(f => ImageThumbnail.Supported(f.Name)))
                if (!html.ToString().Contains("data-attachment='" + E(picture.Id) + "'", StringComparison.Ordinal))
                    html.Append(AttachmentHtml(picture));
            html.Append($"<aside class='files'><details id='session-files'><summary>会话文件 · {view.OutputFiles.Count} 个</summary><div class='file-list'>");
            foreach (var file in view.OutputFiles)
                html.Append($"<p><a href='attachment:{Uri.EscapeDataString(file.Name)}'>{E(file.Name)} · {file.Size / 1024d:N0} KB · 下载</a></p>");
            html.Append("</div></details></aside>");
        }
        if (view.Messages.Count == 0) html.Append("<div class='empty'><b>从这里继续工作</b><p>发送一条消息，远端 Agent 会在原会话中回复。</p></div>");
        return html.ToString();
    }

    // Remote HTML/scripts/images cannot execute or load. Links are intercepted by the native page.
    public const string Shell = """
<!doctype html><html lang="zh"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
<style>
:root{color-scheme:light dark;--bg:#fff;--card:#fff;--operations:#f5f6f7;--text:#202428;--muted:#708176;--line:#e0e7df;--user:#e4efe7}*{box-sizing:border-box}body{margin:0;padding:12px 18px 20px;background:var(--bg);color:var(--text);font:16px/1.65 system-ui,sans-serif;overflow-wrap:anywhere}article{margin:0 0 20px}.user{background:var(--user);border-radius:20px;padding:12px 15px;margin-left:28px}.assistant{padding:0;margin:0 0 24px}.copy{display:block;width:44px;padding:10px 0;font-size:12px;text-decoration:none;color:var(--muted)}.user .copy{padding-bottom:0}p{margin:8px 0}p:first-child{margin-top:0}p:last-child{margin-bottom:0}h1,h2,h3{font-size:18px;line-height:1.4}pre{overflow-x:auto;background:var(--bg);padding:12px;border-radius:9px;font:12px/1.65 monospace;white-space:pre}code{font:13px monospace;background:var(--bg)}table{display:block;overflow-x:auto;border-collapse:collapse;font-size:13px}td,th{border:1px solid var(--line);padding:7px 10px;min-width:70px}blockquote{margin:10px 0;border-left:3px solid #81ad93;padding-left:12px;color:var(--muted)}a{color:#428062}img{max-width:100%}details{margin:10px 0;font-size:13px;color:var(--muted)}.operations{background:var(--operations);border-radius:12px;padding:12px 14px}.operation{border-top:1px solid var(--line);padding:8px 0}summary{min-height:24px;line-height:24px}summary{cursor:pointer}.empty{text-align:center;padding:64px 20px;color:var(--muted)}.empty b{color:var(--text);font-size:20px}ul,ol{padding-left:22px}@media(prefers-color-scheme:dark){:root{--bg:#101c17;--card:#182820;--operations:#182820;--text:#e4ece6;--muted:#a1b4a7;--line:#2b4034;--user:#264637}}
.attachment-image{display:block;margin:12px 0;max-width:320px}.attachment-image img{display:block;object-fit:contain;border-radius:10px;background:var(--operations)}.attachment-image span{display:block;font-size:12px;color:var(--muted)}
.file-list{max-height:240px;overflow:auto}.files>details>summary{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.history{text-align:center;font-size:12px;color:var(--muted)}pre.plain{white-space:pre-wrap;word-break:break-word}
</style></head><body><main id="chat"></main><script>
let armed=false,loading=false;
const attachmentImages=new Map();
function setAttachmentImage(id,version,data){
 if(id)attachmentImages.set(JSON.stringify([id,version]),data);
 for(const image of document.querySelectorAll('img[data-attachment]')){
  const source=attachmentImages.get(JSON.stringify([image.dataset.attachment,image.dataset.version]));
  if(source)image.src=source;
 }
}
function older(){if(!loading&&document.getElementById('older')){loading=true;location.href='fa-history://older';}}
addEventListener('touchmove',()=>{armed=true;if(scrollY<30)older();},{passive:true});
addEventListener('wheel',e=>{armed=true;if(e.deltaY<0&&scrollY<30)older();},{passive:true});
addEventListener('scroll',()=>{if(armed&&scrollY<30){armed=false;older();}},{passive:true});
function historyLoadDone(){loading=false;}
// Reconcile keyed messages and their children in place, preserving selection and nested scroll positions.
function patchNode(current,next){
 if(current.nodeType!==next.nodeType||current.nodeName!==next.nodeName){const replacement=next.cloneNode(true);current.replaceWith(replacement);return replacement;}
 if(current.nodeType===Node.TEXT_NODE){if(current.nodeValue!==next.nodeValue)current.nodeValue=next.nodeValue;return current;}
 if(current.nodeType!==Node.ELEMENT_NODE)return current;
 for(const attr of [...current.attributes])if(!(current.tagName==='DETAILS'&&attr.name==='open')&&!next.hasAttribute(attr.name))current.removeAttribute(attr.name);
 for(const attr of next.attributes)if(!(current.tagName==='DETAILS'&&attr.name==='open')&&current.getAttribute(attr.name)!==attr.value)current.setAttribute(attr.name,attr.value);
 patchChildren(current,next);return current;
}
function patchChildren(current,next){
 const keyed=new Map([...current.children].filter(n=>n.id).map(n=>[n.id,n]));
 let cursor=current.firstChild;
 for(const desired of [...next.childNodes]){
  let node=desired.nodeType===Node.ELEMENT_NODE&&desired.id?keyed.get(desired.id):cursor;
  if(node&&node.nodeType===Node.ELEMENT_NODE&&node.id!==((desired.nodeType===Node.ELEMENT_NODE&&desired.id)||''))node=null;
  if(!node){node=desired.cloneNode(true);current.insertBefore(node,cursor);}
  else{if(node!==cursor)current.insertBefore(node,cursor);node=patchNode(node,desired);}
  cursor=node.nextSibling;
 }
 while(cursor){const nextNode=cursor.nextSibling;cursor.remove();cursor=nextNode;}
}
let renderVersion=0;
function renderTranscript(html,reset,prepend){
 const chat=document.getElementById('chat');
 const bottom=innerHeight+scrollY>=document.body.scrollHeight-100,y=scrollY;
 const anchor=[...chat.querySelectorAll('section')].find(x=>x.getBoundingClientRect().bottom>0);
 const aid=anchor?.id,ay=anchor?.getBoundingClientRect().top;
 const template=document.createElement('template');template.innerHTML=html;
 if(reset){attachmentImages.clear();chat.replaceChildren(template.content);}else patchChildren(chat,template.content);
 setAttachmentImage('', '', '');
 armed=false;loading=false;const version=++renderVersion;
 requestAnimationFrame(()=>{
  if(version!==renderVersion)return;
  const a=aid&&document.getElementById(aid);
  if(!reset&&(prepend||!bottom)&&a)scrollTo(0,scrollY+a.getBoundingClientRect().top-ay);
  else scrollTo(0,reset||bottom?document.body.scrollHeight:y);
 });
}
</script></body></html>
""";

    public static SessionView Preview => new("preview", "随时接回你的工作", "", "idle", null,
    [new("user", "帮我梳理一下 Android 首版的重点。"),
     new("assistant", "## 工作继续，地点随你\n\n在手机上查看远端会话、继续对话，及时处理需要你确认的事项。\n\n- **会话同步**：接回电脑上的原会话\n- **文件交付**：上传资料，下载任务产物\n- **可靠连接**：断线恢复读取，不重复发送\n\n这是**离线界面预览**，不会连接设备或执行任务。"),
     new("工具", "已整理首版功能。", "此处展示工具过程。真实连接后会显示远端返回的详情。"),
     new("assistant", "你可以先打开右上角的 **连接**，填写远端授权码。\n\n```text\nAndroid → 中继 ← 电脑 / 服务器 → Agent\n```\n\n配置保存在手机，远端任务仍在原设备执行。")], [], CanSend: false, CanStop: false);
}
