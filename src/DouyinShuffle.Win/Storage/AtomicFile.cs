using System.IO;

namespace DouyinShuffle.Win.Storage;

/// <summary>
/// 原子文件写入:先写同目录临时文件,再 Replace/Move 到目标路径。
/// 防止写入中途崩溃/断电导致数据文件(items.dylist/state.json/导出文件)半写损坏——
/// 目标文件要么是旧内容、要么是完整新内容,不存在中间态。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
