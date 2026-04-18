using System;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard;

/// <summary>
/// WPF メインウィンドウ。BlazorWebView をホストし、初期化する。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<ILogger<MainWindow>>();
        InitializeComponent();
        ApplyWindowIcon();
        InitializeBlazor();
    }

    private void ApplyWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/CloudMigrator.png", UriKind.Absolute);
            Icon = BitmapFrame.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ウィンドウアイコンの設定に失敗しました。アプリの起動は継続します。");
        }
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
