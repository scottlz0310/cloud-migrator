using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace CloudMigrator.Dashboard;

/// <summary>
/// WPF メインウィンドウ。BlazorWebView をホストし、初期化する。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        InitializeBlazor();
    }

    private void InitializeBlazor()
    {
        // DI コンテナを BlazorWebView と共有する
        BlazorWebView.Services = _services;

        // ルートコンポーネントをコンパイル時参照で登録する
        BlazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Components.DashboardApp),
        });

        // WebView2 初期化後に右クリックコンテキストメニュー（貼り付け等）を有効化する
        BlazorWebView.BlazorWebViewInitialized += (sender, args) =>
        {
            args.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        };
    }
}
