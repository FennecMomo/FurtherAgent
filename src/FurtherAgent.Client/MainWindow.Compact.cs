using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private readonly Dictionary<(DeviceContext Device, string Session), MiniWindow> miniWindows = [];

    private void InitializeCompact()
    {
        Composer.TextChanged += (_, _) =>
        {
            if (activeDevice is null || selectedId is null) return;
            drafts[selectedId] = Composer.Text;
            if (miniWindows.TryGetValue((activeDevice, selectedId), out var mini)) mini.SetDraft(Composer.Text);
        };
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (activeDevice is not { } device || selectedId is null || current is not { } view || view.Id != selectedId || !device.Remote.IsConnected)
        {
            StatusBar.Text = "请先连接设备并打开一个会话，再创建迷你窗口。";
            return;
        }
        var key = (device, selectedId);
        if (miniWindows.TryGetValue(key, out var existing)) { existing.RestoreAndActivate(); return; }

        string identity = device.Server + "|" + device.Code + "|" + selectedId;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        string positionPath = Path.Combine(Path.GetDirectoryName(SettingsFile)!, "compact-window-" + suffix + ".json");
        string session = selectedId;
        var mini = new MiniWindow(device.Server, device.Code, session, view.Title, view, positionPath,
            () => device.Drafts.GetValueOrDefault(session, ""),
            text =>
            {
                device.Drafts[session] = text;
                if (activeDevice == device && selectedId == session && Composer.Text != text) Composer.Text = text;
            },
            RestoreFromTray, (miniWindows.Count % 8) * 28);
        miniWindows.Add(key, mini);
        mini.Closed += (_, _) => miniWindows.Remove(key);
        mini.Show();
        _ = mini.StartAsync();
    }

    private void CloseMinisForDevice(DeviceContext device)
    {
        foreach (var mini in miniWindows.Where(pair => pair.Key.Device == device).Select(pair => pair.Value).ToArray()) mini.Close();
    }

    private void CloseAllMinis()
    {
        foreach (var mini in miniWindows.Values.ToArray()) mini.Close();
    }
}
