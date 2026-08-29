using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DouyinShuffle.Win;

/// <summary>
/// 抖音引擎窗口(屏幕外常驻):承载登录态/签名/采集的抖音 WebView。
/// - 为什么独立窗口:WebView2 是 HwndHost(Win32 子窗口),不受 WPF Grid 的 z-order 裁剪,
///   放主窗口里一旦 Visible 就会盖住 UI 页 → 必须物理隔离;
/// - 为什么常显:Collapsed/隐藏窗口会让 Chromium 停止渲染,SPA 懒加载与页面自身的
///   签名请求(直连采集的模板来源)都不发生;
/// - 为什么屏幕外:用户不可见。Windows 对屏幕外窗口仍保持渲染(比最小化/隐藏强)。
/// 与主窗口共享同一 CoreWebView2Environment(profile) → cookie/登录态全局共享。
/// </summary>
public partial class DouyinEngineWindow : Window
{
    public CoreWebView2? Core => EngineWebView.CoreWebView2;

    public DouyinEngineWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionOffscreen();
    }

    /// <summary>
    /// 定位到主屏幕左侧屏幕外(不可见但持续渲染)。
    /// 注:不能用右侧 —— 多显示器用户右副屏起点通常正是主屏右缘,
    /// 引擎窗会完整暴露在副屏左上角(幽灵抖音页面)。
    /// </summary>
    private void PositionOffscreen()
    {
        try
        {
            var screen = SystemParameters.WorkArea;
            Left = screen.Left - Width - 50;   // 主屏幕左侧外 50px
            Top = screen.Top;
        }
        catch { }
    }

    /// <summary>暴露 WebView2 控件供宿主初始化(EnsureCoreWebView2Async)。</summary>
    public System.Threading.Tasks.Task EnsureAsync(CoreWebView2Environment env)
        => EngineWebView.EnsureCoreWebView2Async(env);
}
