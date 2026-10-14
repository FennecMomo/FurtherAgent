using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class MobileLayoutChecks
{
    public static void Preview(string directory)
    {
        Directory.CreateDirectory(directory);
        var view = new SessionView("preview", "手机端界面优化", "FurtherAgent", "idle", null,
            [new("user", "请整理手机端的界面优化方案。"),
             new("assistant", "我会先检查现有布局，再把阅读、连接切换和输入操作整理清楚。"),
             new("工具", "读取手机端布局", "MainPage.cs"), new("工具", "检查会话渲染", "Transcript.cs"), new("工具", "验证分页兼容", "检查通过"),
             new("assistant", "## 调整后的界面\n\n### 1. 更专注地阅读\n手机端以对话内容为中心，减少界面干扰。\n\n- 顶部仅保留必要信息。\n- 正文直接铺在页面上。\n\n### 2. 更方便地切换\n从侧栏按工作空间查找会话，顶部下拉切换连接端。\n\n### 3. 需要时再展开\n连续的工具记录合并展示，点击可查看各项结果。")], []);
        string html = Transcript.Render(view);
        File.WriteAllText(Path.Combine(directory, "index.html"), Transcript.Shell.Replace("<main id=\"chat\"></main>", "<main id=\"chat\">" + html + "</main>")
            .Replace("</body>", "<button onclick=\"renderTranscript(document.getElementById('chat').innerHTML,false,false)\">测试：模拟增量更新</button></body>"));
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), System.Text.Json.JsonSerializer.Serialize(html));
    }
    public static void Run(Action<bool, string> check)
    {
        var view = new SessionView("layout", "布局", "", "idle", null,
            [new("user", "开始"), new("工具", "读取文件", "<unsafe>"), new("推理摘要", "检查结果"),
             new("assistant", "第一段"), new("工具", "编译"), new("assistant", "第二段")], []);
        string html = Transcript.Render(view);
        check(html.Split("class='operations'").Length == 3 && html.Contains("2 项操作记录") && html.Contains("1 项操作记录"),
            "consecutive operations collapse without crossing assistant prose");
        check(html.IndexOf("第一段") > html.IndexOf("2 项操作记录") && html.IndexOf("第一段") < html.IndexOf("1 项操作记录"),
            "grouped operations retain chronological placement");
        check(!html.Contains("&lt;unsafe&gt;") && !html.Contains("已完成"), "tool detail is escaped and does not invent success");
        check(html.Contains("fa-copy://3") && html.Contains("fa-copy://5"), "copy links retain source message indices after grouping");
        var old = HistoryPaging.Page(view);
        var current = HistoryPaging.Page(view, includeOperations: true);
        check(old.Messages.Count == 3 && old.Messages.All(HistoryPaging.IsConversation), "v1 history remains conversation only");
        check(current.Messages.Count == 6 && current.Messages[1].Detail is null, "v2 history delivers ordered summaries without tool details");
        check(old.Revision != current.Revision, "operation history cannot reuse a conversation-only cache baseline");
        var big = view with { Messages = Enumerable.Range(0, 20).Select(i => new ChatEntry(i % 2 == 0 ? "工具" : "assistant", i.ToString())).ToList() };
        var last = HistoryPaging.Page(big, includeOperations: true);
        var before = HistoryPaging.Page(big, last.Start, last.Revision, includeOperations: true);
        check(before.End == last.Start && last.Start == 12 && last.Messages[0].Text == "执行了一项操作" && before.Messages[^1].Text == "11", "v2 pagination preserves operation boundaries without duplicate records");
        check(Transcript.Render(view with { Messages = [new("工具", "only")] }).EndsWith("</details></section>"), "trailing operation group is closed");
    }
}
