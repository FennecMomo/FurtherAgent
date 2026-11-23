using System.Windows;

namespace FurtherAgent.Client;

internal sealed class SubmissionRecovery
{
    private readonly HashSet<(string Device, string Session)> uncertain = [];

    public event Action<(string Device, string Session)>? Changed;

    public bool IsUncertain((string Device, string Session) key) => uncertain.Contains(key);

    public void MarkUncertain((string Device, string Session) key)
    {
        if (uncertain.Add(key)) Changed?.Invoke(key);
    }

    public bool PromptIfUncertain(Window owner, (string Device, string Session) key)
    {
        if (!uncertain.Contains(key)) return false;
        var choice = MessageBox.Show(owner,
            "上次提交可能已经进入远端队列或开始执行。请先在原会话核对。选择「是」仅解除发送保护。已核对吗？",
            "核对远端消息", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes && uncertain.Remove(key)) Changed?.Invoke(key);
        return true;
    }
}
