# libs 说明

本目录下的 WebView2 SDK 程序集为**离线本地引用**(构建环境无法访问 nuget.org):

| 文件 | 来源 |
|---|---|
| Microsoft.Web.WebView2.Core.dll | 本机 Visual Studio 2022 Community `Common7\IDE\PrivateAssemblies` |
| Microsoft.Web.WebView2.Wpf.dll | 同上 |
| WebView2Loader.dll | 同上(x64) |

- 运行依赖系统已安装的 WebView2 运行时(Edge WebView2 Runtime)。
- 版本较 NuGet 包略旧,但 API 完全够用;联网环境下建议换回
  `Microsoft.Web.WebView2` 官方 NuGet 包并删除本地引用。
- 注意:WebView2Loader.dll 目前只复制了 x64,多架构发布时需补 x86/ARM64。
