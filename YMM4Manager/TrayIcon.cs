using System.Windows;
using System.Windows.Input;
using WF = System.Windows.Forms;

namespace YMM4Manager;

/// <summary>
/// 通知領域(タスクバーの右下・「隠れているインジケーター」)の YMM4 マネージャーのアイコン。
/// 左クリックで画面を出し、右クリックでメニュー(開く・YMM4 を起動・今すぐバックアップ・終了)。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly WF.NotifyIcon icon;
    readonly MainWindow win;
    readonly WF.ToolStripMenuItem launchItem, backupItem;
    bool tipShown;

    public TrayIcon(MainWindow win, MainVm vm)
    {
        this.win = win;
        icon = new WF.NotifyIcon { Text = "YMM4 マネージャー", Icon = LoadIcon(), Visible = false };
        var menu = new WF.ContextMenuStrip();
        var open = new WF.ToolStripMenuItem("YMM4 マネージャーを開く", null, (_, _) => ShowWindow());
        open.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        launchItem = new WF.ToolStripMenuItem("YMM4 を起動", null, (_, _) => Run(vm.LaunchCommand, null));
        backupItem = new WF.ToolStripMenuItem("専用ストレージを今すぐバックアップ", null,
            (_, _) => Run(vm.Storage.BackupCommand, "専用ストレージのバックアップを始めました。終わると、マネージャーの「専用ストレージ」に結果が出ます"));
        menu.Items.Add(open);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(launchItem);
        menu.Items.Add(backupItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(new WF.ToolStripMenuItem("終了", null, (_, _) => win.ExitApp()));
        menu.Opening += (_, _) =>
        {
            launchItem.Enabled = vm.LaunchCommand.CanExecute(null);
            backupItem.Enabled = vm.Storage.BackupCommand.CanExecute(null);
        };
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
        icon.BalloonTipClicked += (_, _) => ShowWindow();
    }

    /// <summary>アイコンを出すか</summary>
    public bool Visible { get => icon.Visible; set => icon.Visible = value; }

    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } big)
                return new System.Drawing.Icon(big, WF.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowWindow()
    {
        win.Show();
        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
        win.Activate();
        win.Topmost = true; win.Topmost = false;
        win.Focus();
    }

    void Run(ICommand command, string? tip)
    {
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        if (tip != null && !win.IsVisible) icon.ShowBalloonTip(4000, "YMM4 マネージャー", tip, WF.ToolTipIcon.Info);
    }

    /// <summary>× で閉じて通知領域に残った時に、1回だけ知らせる</summary>
    public void ShowHiddenTip()
    {
        if (tipShown || !icon.Visible) return;
        tipShown = true;
        icon.ShowBalloonTip(5000, "YMM4 マネージャー", "通知領域で動いています(バックアップも続けます)。アイコンを押すと開き、右クリックの「終了」で閉じます。", WF.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
