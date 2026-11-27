using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace YMM4Manager;

public partial class App : Application
{
    // インストーラーが「起動中かどうか」を調べるための印(アップデートの時に、閉じるよう案内される)
    static System.Threading.Mutex? running;

    protected override void OnStartup(StartupEventArgs e)
    {
        running = new System.Threading.Mutex(false, "YMM4ManagerRunning");
        // 予期しないエラーで落ちないように(内容を表示して続ける)
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("エラーが起きました:\n" + ex.Exception.Message, "YMM4 マネージャー", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        base.OnStartup(e);

        // タスクスケジューラーから起動された時: 画面を出さずに、専用ストレージのバックアップだけして終わる
        if (e.Args.Contains("--storage-backup"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBackupAndExit();
            return;
        }
        // マネージャーの画面は1つだけ(2つ開くと、閉じた時に古い設定で上書きしあうため)。もう開いていたら、そちらを前に出して終わる
        showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "YMM4ManagerShowWindow", out var first);
        if (!first)
        {
            try { showSignal.Set(); } catch { }
            Shutdown();
            return;
        }
        var win = new MainWindow();
        win.Show();
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try { showSignal.WaitOne(); } catch { return; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                    win.Show(); win.Activate(); win.Topmost = true; win.Topmost = false; win.Focus();
                }));
            }
        }) { IsBackground = true, Name = "YMM4Manager show signal" };
        t.Start();
    }
    static System.Threading.EventWaitHandle? showSignal;

    async Task RunBackupAndExit()
    {
        try
        {
            // 少し前に(マネージャーの画面などで)済ませていたら、しない
            if (StorageBackup.IsDue(0.8)) await StorageBackup.RunAsync(null);
        }
        catch { }
        Shutdown();
    }
}

/// <summary>文字が ConverterParameter と同じなら表示(ページの切り替え用)</summary>
public sealed class StringEqualsToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>文字が ConverterParameter と同じなら true(左のメニューの選択用)。true にされたら、その文字を返す</summary>
public sealed class StringEqualsToBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is true ? p?.ToString() ?? "" : Binding.DoNothing;
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>"RRGGBB" → ブラシ</summary>
public sealed class HexToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        try
        {
            var s = (value?.ToString() ?? "").TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            var col = (Color)ColorConverter.ConvertFromString("#" + s);
            var b = new SolidColorBrush(col);
            b.Freeze();
            return b;
        }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
