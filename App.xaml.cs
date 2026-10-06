using System.Diagnostics;
using System.Windows;

namespace Voca;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Started by an update: let the previous version exit and release the single-instance lock first.
        Services.Updater.WaitForPreviousProcess(e.Args);
        _singleInstanceMutex = new System.Threading.Mutex(true, @"Local\Voca2.SingleInstance", out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance || TryReplaceRunningInstance();
        if (!_ownsSingleInstanceMutex)
        {
            Shutdown();
            return;
        }
        var store = new Services.Store();
        if (!store.Data.Seeded) store.Update(Services.SeedData.EnsureSeeded);
        // First run after Voca 1: bring over its plans (e.g. ones created on the old web page) and progress.
        if (!store.Data.ImportedFromV1 && Services.VocaV1Import.Available())
        {
            try { store.Update(d => Services.VocaV1Import.Import(d, includeSettings: true)); }
            catch (Exception) { /* an unreadable old file must not stop the app; the manual button reports details */ }
        }
        // After an update (or a fresh start): remove the previous exe and leftover downloads.
        if (Services.Updater.Enabled()) Services.Updater.CleanUp();
        try { Services.WindowsStartupService.FollowCurrentExe(); }
        catch (Exception) { /* start-up entry not writable: the setting in Cài đặt still works */ }
        _window = new MainWindow(store);
        _window.Show();
    }

    /// <summary>
    /// Another copy is already running (often an older build started with Windows). Instead of
    /// exiting silently, offer to close it and continue with this one.
    /// </summary>
    private bool TryReplaceRunningInstance()
    {
        var answer = System.Windows.MessageBox.Show(
            "Voca đang chạy (có thể là phiên bản cũ).\n\nĐóng bản đang chạy và mở bản này?",
            "Voca", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;

        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("Voca").Where(p => p.Id != current))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already exited, or not ours to close; the mutex wait below decides.
                }
            }
        }

        try
        {
            if (_singleInstanceMutex!.WaitOne(TimeSpan.FromSeconds(5))) return true;
        }
        catch (System.Threading.AbandonedMutexException)
        {
            return true; // The killed process held the mutex; ownership passes to us.
        }

        System.Windows.MessageBox.Show("Không đóng được bản đang chạy. Hãy thoát nó từ khay hệ thống rồi mở lại.",
            "Voca", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
