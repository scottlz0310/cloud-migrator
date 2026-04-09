using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace CloudMigrator.Dashboard;

/// <summary>
/// WPF メインウィンドウ。BlazorWebView をホストし、WebView2 の NavigationStarting ハンドラを管理する。
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

        // ルートコンポーネントを登録
        // Razor コンポーネントは同アセンブリ内に生成されるため、リフレクションで取得する
        var appComponentType = GetType().Assembly.GetType("CloudMigrator.Dashboard.Components.DashboardApp")
            ?? throw new InvalidOperationException("DashboardApp コンポーネントが見つかりません。ビルドを確認してください。");

        BlazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = appComponentType,
        });
    }
}
