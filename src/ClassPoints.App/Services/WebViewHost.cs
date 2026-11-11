using ClassPoints.Data;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ClassPoints.App.Services;

/// <summary>
/// WebView2 的唯一初始化入口。
///
/// 必须显式指定用户数据目录：不指定时 WebView2 会把它放在 <b>exe 旁边</b>
/// （<c>…\ClassPoints.exe.WebView2\EBWebView</c>）。装到 Program Files 之后那里没有写权限，
/// 用户看到的是一个「无法创建数据目录」的系统弹窗，打印页与预览一起失效。
/// 数据目录属于本机运行状态，和数据库一样放 %LOCALAPPDATA%，覆盖升级和卸载都不碰。
/// </summary>
public static class WebViewHost
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _environment;

    public static string UserDataFolder => Path.Combine(Database.RootDir, "webview2");

    public static async Task EnsureAsync(WebView2 view)
    {
        if (view.CoreWebView2 is not null) return;
        await view.EnsureCoreWebView2Async(await EnvironmentAsync());
    }

    /// <summary>整个进程只建一个环境：打印页和打印服务共用同一份浏览器缓存。</summary>
    private static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        lock (Gate)
        {
            return _environment ??= CreateAsync();
        }

        static async Task<CoreWebView2Environment> CreateAsync()
        {
            Directory.CreateDirectory(UserDataFolder);
            var options = new CoreWebView2EnvironmentOptions();

            // WinRT 投影里只暴露 CreateWithOptionsAsync；浏览器位置留空 = 用系统装的 Edge WebView2。
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, UserDataFolder, options);
        }
    }
}
