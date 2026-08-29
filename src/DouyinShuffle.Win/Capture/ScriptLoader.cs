using System.IO;
using System.Reflection;

namespace DouyinShuffle.Win.Capture;

/// <summary>
/// 采集 JS 脚本加载器:Capture/Scripts/*.js 以嵌入资源打进程序集,
/// 运行时读出并缓存(替代此前内嵌在 C# 类里的 ~570 行 JS 字符串常量)。
/// 注意:这些脚本经 ExecuteScriptAsync 瞬时注入(用后即弃),
/// 任何文档创建时注入的 fetch/XHR 包装都会干扰 webmssdk 签名层(历史教训)。
/// </summary>
internal static class ScriptLoader
{
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly object Sync = new();

    /// <summary>读脚本(带缓存)。name 为文件名,如 "paged-loop.js"。</summary>
    public static string Get(string name)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
        }
        var asm = Assembly.GetExecutingAssembly();
        // 资源名 = 命名空间.目录.文件名(默认 EmbeddedResource 命名规则)
        var resName = $"{typeof(ScriptLoader).Namespace}.Scripts.{name}";
        using var s = asm.GetManifestResourceStream(resName)
            ?? throw new FileNotFoundException($"embedded script not found: {resName}");
        using var r = new StreamReader(s);
        var content = r.ReadToEnd();
        lock (Sync) { Cache[name] = content; }
        return content;
    }
}
