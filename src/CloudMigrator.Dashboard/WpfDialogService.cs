using System.Windows;

namespace CloudMigrator.Dashboard;

/// <summary>
/// <see cref="INativeDialogService"/> の WPF 実装。
/// Dispatcher 経由で UI スレッドに切り替えてから MessageBox を表示する。
/// </summary>
internal sealed class WpfDialogService : INativeDialogService
{
    public Task<bool> ConfirmAsync(string title, string message)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes);
        return Task.FromResult(result);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));
        return Task.CompletedTask;
    }

    public Task ShowInfoAsync(string title, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        return Task.CompletedTask;
    }
}
