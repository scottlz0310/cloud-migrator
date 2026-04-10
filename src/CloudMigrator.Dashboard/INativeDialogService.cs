namespace CloudMigrator.Dashboard;

/// <summary>
/// ネイティブダイアログサービスの契約。
/// Blazor コンポーネントが WPF の MessageBox 等を DI 経由で呼び出すためのインターフェース。
/// Blazor コンポーネントは WPF 型に直接依存しない。
/// </summary>
public interface INativeDialogService
{
    /// <summary>
    /// 確認ダイアログを表示する。ユーザーが「はい」を選択した場合は true を返す。
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// エラーダイアログを表示する。
    /// </summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>
    /// 情報ダイアログを表示する。
    /// </summary>
    Task ShowInfoAsync(string title, string message);
}
