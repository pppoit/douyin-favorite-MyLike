using System.IO;
using DouyinShuffle.Win.Capture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DouyinShuffle.Win.Storage;

/// <summary>同步状态:增量游标 + 数量统计 + 登录账号标识。</summary>
public sealed class SyncState
{
    public long MaxCursor { get; set; }
    public int CollectedCount { get; set; }
    public long LastSyncAt { get; set; }

    /// <summary>上次采集账号的 sec_uid(下次启动直接复用,不必重新探测)。</summary>
    public string SecUserId { get; set; } = "";

    /// <summary>上次采集是否未跑完全量(失败/中断/手动停止)。
    /// true → 下次点「采集」从 MaxCursor 断点续采(旧内容未采完);
    /// false → 从头增量(只补新喜欢)。防"中途失败后旧内容永远采不到"。
    /// </summary>
    public bool CollectIncomplete { get; set; }

    /// <summary>UI 偏好:自动连播开关(默认关)。与采集状态同文件存储,采集保存时保留不覆盖。</summary>
    public bool AutoNext { get; set; }
}

/// <summary>
/// JSON 文件存储(.dylist 播放列表 + state.json)。
/// format:2 = 只存元数据(链接类字段 [JsonIgnore] 不持久化),播放列表/导入导出/采集三者为同一数据。
/// </summary>
public sealed class LikeListStore
{
    private const int Format = 2;
    private const string AppTag = "DouyinShuffle";

    private readonly string _dir;

    /// <summary>state.json 内存缓存:Save 高频调用(采集每轮落盘),避免每次都读盘取旧值。</summary>
    private SyncState? _stateCache;

    public LikeListStore(string dataDir)
    {
        _dir = dataDir;
        Directory.CreateDirectory(_dir);
    }

    private string ItemsPath => Path.Combine(_dir, "items.dylist");
    private string StatePath => Path.Combine(_dir, "state.json");
    // 旧格式(含链接),迁移/兼容读取用
    private string LegacyItemsPath => Path.Combine(_dir, "items.json");

    public List<AwemeItem> LoadItems()
    {
        // 优先新格式
        if (File.Exists(ItemsPath))
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(ItemsPath));
                if (root["format"]?.Value<int>() == Format && root["items"] is JArray arr)
                    return arr.ToObject<List<AwemeItem>>() ?? new List<AwemeItem>();
            }
            catch { }
        }
        // 回退旧格式(迁移前):读取 items.json(带链接),返回后由调用方触发迁移
        if (File.Exists(LegacyItemsPath))
        {
            try
            {
                return JsonConvert.DeserializeObject<List<AwemeItem>>(File.ReadAllText(LegacyItemsPath)) ?? new();
            }
            catch { }
        }
        return new List<AwemeItem>();
    }

    /// <summary>检测是否存在旧格式数据(需要迁移)。</summary>
    public bool HasLegacyData()
    {
        return !File.Exists(ItemsPath) && File.Exists(LegacyItemsPath);
    }

    public SyncState LoadState()
    {
        if (_stateCache != null) return _stateCache;
        try
        {
            _stateCache = File.Exists(StatePath)
                ? JsonConvert.DeserializeObject<SyncState>(File.ReadAllText(StatePath)) ?? new SyncState()
                : new SyncState();
        }
        catch { _stateCache = new SyncState(); }
        return _stateCache;
    }

    public void Save(List<AwemeItem> items, long cursor, string? secUserId = null, bool? collectIncomplete = null)
    {
        // .dylist 格式:版本头 + 元数据数组(链接字段自动忽略)
        var root = new JObject
        {
            ["app"] = AppTag,
            ["format"] = Format,
            ["count"] = items.Count,
            ["saved_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["items"] = JArray.FromObject(items)
        };
        // 原子写:tmp + Replace,防写一半崩溃损坏列表(数万条数据是用户核心资产)
        AtomicFile.WriteAllText(ItemsPath, root.ToString(Formatting.None));
        var prev = _stateCache ?? LoadState();
        var state = new SyncState
        {
            MaxCursor = cursor,
            CollectedCount = items.Count,
            LastSyncAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SecUserId = secUserId?.Length > 0 ? secUserId : prev.SecUserId,
            CollectIncomplete = collectIncomplete ?? prev.CollectIncomplete,
            AutoNext = prev.AutoNext   // UI 偏好:采集保存时保留用户上次选择
        };
        AtomicFile.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
        _stateCache = state;
    }

    /// <summary>单独持久化 UI 偏好(自动连播开关),不触碰列表文件。</summary>
    public void SaveAutoNext(bool autoNext)
    {
        var state = _stateCache ?? LoadState();
        if (state.AutoNext == autoNext) return;
        state.AutoNext = autoNext;
        AtomicFile.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    /// <summary>
    /// 迁移旧格式(items.json 带链接)→ 新格式(items.dylist 仅元数据)。
    /// 链接字段被 [JsonIgnore] 自动剔除;完成即删除旧文件。
    /// </summary>
    public void MigrateLegacy()
    {
        if (!HasLegacyData()) return;
        var items = LoadItems();   // 从旧文件读取
        Save(items, LoadState().MaxCursor);   // 写新格式(自动剔链接)
        try { if (File.Exists(LegacyItemsPath)) File.Delete(LegacyItemsPath); } catch { }
    }

    /// <summary>清空全部数据(列表、增量状态、导出文件)。</summary>
    public void ClearAll()
    {
        _stateCache = null;   // 状态缓存随磁盘一起失效
        foreach (var f in new[] { ItemsPath, LegacyItemsPath, StatePath, ItemsPath + ".tmp", StatePath + ".tmp" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        var exportDir = Path.Combine(_dir, "export");
        try { if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true); } catch { }
    }
}
