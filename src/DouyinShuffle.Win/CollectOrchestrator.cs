using DouyinShuffle.Win.Capture;
using Microsoft.Web.WebView2.Core;

namespace DouyinShuffle.Win;

/// <summary>
/// 采集编排器(单一通道 + 收藏接口单一真相源):
///
/// 链路:登录检查 → sec_uid(self 探测,只管登录态) → 引擎页预热 →
///   ★采集前预检(favorite 探测,18s 内定位接口状态)
///     OK  → 翻页采集(单一直连通道;单轮 200 页上限,分轮自动续采,最多 30 轮)
///     不通 → 短重试 2 次 → 仍不通分流:
///       已有数据 = 限流 → 弹验证窗(轮询 favorite 恢复,恢复即自动断点续采)
///       无数据   = 网络/环境 → 报错退出
///   翻页中失败 → favorite 探测分流(同上)
///
/// 探测信号原则:要采什么就探什么 —— 一切"接口可用性"判断都用收藏接口本身
/// (favorite-probe);profile/self 只用于登录态与 sec_uid。
/// 历史教训:风控是分接口的,收藏接口被限时 self 仍正常返回(实测 548ms Ok),
/// 用 self 复核风控必然误判;同理"主动签名"二级通道依赖的全局签名函数在新版
/// 抖音已收进 webmssdk 内部,实测 10 次调用零成功,两条歧路均已删除。
/// </summary>
internal sealed class CollectOrchestrator
{
    private readonly MainWindow _host;

    private CancellationTokenSource? _collectCts;
    private bool _collecting;
    private string _lastSecUid = "";

    /// <summary>上次采集未跑完全量(失败/中断/停止)→ 下次点「采集」从断点续采而非从头增量。</summary>
    private bool _lastCollectIncomplete;

    private bool _pendingCollectResume;
    private int _autoRecoverCount;   // 采集波动静默恢复次数(防死循环,超过 3 次转人工/终止)

    public bool IsCollecting => _collecting;
    public string SecUid => _lastSecUid;

    public CollectOrchestrator(MainWindow host) => _host = host;

    /// <summary>启动时回填持久化状态(sec_uid 缓存 + 断点续采标志)。</summary>
    public void SeedState(string secUid, bool collectIncomplete)
    {
        _lastSecUid = secUid;
        _lastCollectIncomplete = collectIncomplete;
    }

    public void ClearSecUid() => _lastSecUid = "";

    /// <summary>
    /// 开始采集(命令泵入口):上次未跑完 → 从 MaxCursor 断点续采;
    /// 否则从头(cursor 0)增量(喜欢列表时间倒序,翻到已知断点即停,只补新喜欢)。
    /// </summary>
    public string Start()
    {
        if (_collecting) return "busy";
        // 验证窗口开着(等接口恢复)期间拒绝新采集:此时启动只会立刻再黑洞,反复刺激接口
        if (_host.IsVerifyWindowOpen)
        {
            AppLog.Write("COLLECT rejected: verify window open");
            return "err:验证窗口等待接口恢复中,请完成滑块或稍候;关闭验证窗后再点采集";
        }
        var collector = _host.Collector;
        var cursor = _lastCollectIncomplete && collector is { Count: > 0 } ? collector.MaxCursor : 0;
        AppLog.Write($"COLLECT start cursor={cursor} incomplete={_lastCollectIncomplete}");
        _ = RunCollectAsync(cursor);
        return "started";
    }

    /// <summary>停止采集(用户点「停止」):不再自动续采。</summary>
    public void Stop()
    {
        if (_collectCts != null) { try { _collectCts.Cancel(); } catch { } }
        _pendingCollectResume = false;
    }

    /// <summary>认证成功后自动续采(若采集被登录/验证打断)。</summary>
    public async Task ResumeAfterAuthAsync()
    {
        if (!_pendingCollectResume) return;
        _pendingCollectResume = false;
        // 等旧采集循环完全退出再重启(风控取消有延迟,避免双循环)
        for (var i = 0; i < 20 && _collecting; i++) await Task.Delay(250);
        if (_host.IsShuttingDown) return;
        var resumeCursor = _host.Collector is { Count: > 0 } ? _host.Collector.MaxCursor : 0;
        _ = RunCollectAsync(resumeCursor);
    }

    /// <summary>
    /// 采集中途疑似风控(JS 循环连续退避后仍黑洞,blocked)→ favorite 探测分流:
    /// 已恢复 = 真波动 → 静默续采;不通 → reload 重建 SDK 再探 → 仍不通 = 限流弹验证窗。
    /// 不用 profile/self 复核(风控分接口,self 正常说明不了收藏接口状态)。
    /// </summary>
    public async void OnCollectRisk()
    {
        if (!_host.Dispatcher.CheckAccess()) { _ = _host.Dispatcher.BeginInvoke(OnCollectRisk); return; }
        if (_host.Collector == null || _lastSecUid.Length == 0) return;

        var uid = _lastSecUid;
        AppLog.Write($"RISK-RECHECK fav auto={_autoRecoverCount}");
        var favOk = await DouyinProbe.CheckFavoriteApiAsync(_host.DouyinCoreInternal, uid);

        // 已恢复 = 真波动(JS 退避期间接口回来了)→ 静默续采
        if (favOk && _autoRecoverCount < 3)
        {
            _autoRecoverCount++;
            _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("采集波动,自动恢复中…") + ",false)");
            await ResumeAsync();
            return;
        }

        // 不通 → reload 重建 SDK 拦截器(登录/风控后旧页面持过期状态是黑洞常见原因)再探一次
        if (!favOk)
        {
            await _host.ReloadDouyinPageAsync();
            favOk = await DouyinProbe.CheckFavoriteApiAsync(_host.DouyinCoreInternal, uid);
            if (favOk && _autoRecoverCount < 3)
            {
                _autoRecoverCount++;
                _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("已恢复页面状态,自动继续采集…") + ",false)");
                await ResumeAsync();
                return;
            }
        }

        // 仍不通 = 限流,弹人工验证窗(以收藏接口恢复为完成信号,通过后自动断点续采)
        _autoRecoverCount = 0;
        _pendingCollectResume = true;
        Stop();
        _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("接口被限,请在弹出的页面完成滑块验证") + ",true)");
        _host.OpenAuthWindow(DouyinAuthWindow.AuthMode.Verify, uid);
    }

    /// <summary>等旧循环退出后从断点重启采集。</summary>
    private async Task ResumeAsync()
    {
        for (var i = 0; i < 20 && _collecting; i++) await Task.Delay(250);
        if (_host.IsShuttingDown) return;
        var resumeCursor = _host.Collector is { Count: > 0 } ? _host.Collector.MaxCursor : 0;
        _ = RunCollectAsync(resumeCursor);
    }

    // ---------- 主流程 ----------

    private async Task RunCollectAsync(long startCursor = 0)
    {
        var collector = _host.Collector;
        if (collector == null || _collecting) return;
        _collecting = true;
        _collectCts = new CancellationTokenSource();
        var ct = _collectCts.Token;
        // 退出路径统一收口:中途 return 的分支若已让 UI 进入"采集中"状态(发过 collectStatus)
        // 且没有挂起等待(登录/验证后自动续采),必须在 finally 补发 collectDone,
        // 否则 UI 进度条永久卡住、采集按钮永久灰(新用户关掉登录窗即触发的经典死锁)。
        var uiCollecting = false;
        var sentDone = false;
        void MarkUiCollecting() => uiCollecting = true;
        try
        {
            // 1. 登录检查
            if (!await _host.IsLoggedInAsync())
            {
                _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("请先登录") + ",true)");
                _pendingCollectResume = true;   // 登录成功后自动开始采集
                _host.OpenAuthWindow(DouyinAuthWindow.AuthMode.Login);
                return;
            }

            // 2. sec_uid:优先缓存,否则经 self 探测状态机(只管登录态,不管收藏接口状态)
            var uid = _lastSecUid;
            if (uid.Length == 0 || !uid.StartsWith("MS4wLjAB"))
            {
                var (health, probedUid) = await ProbeAccountAsync(ct, MarkUiCollecting);
                switch (health)
                {
                    case ApiHealth.Ok when probedUid.Length > 0:
                        uid = probedUid;
                        break;
                    case ApiHealth.Blocked:
                        _pendingCollectResume = true;
                        _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("接口受限,请在弹出的页面完成验证") + ",true)");
                        _host.OpenAuthWindow(DouyinAuthWindow.AuthMode.Verify);
                        return;
                    default:
                        AppLog.Write("PROBE not-ready give up");
                        _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("接口持续无响应(网络或临时限制),请稍后重新采集") + ",true)");
                        _host.DispatchUi($"window.__dsh_collectDone && window.__dsh_collectDone({MainWindow.JsonText(collector.Count.ToString())},false)");
                        return;
                }
                _lastSecUid = uid;
            }

            // 3. 引擎页预热
            await _host.EnsureLikePageAsync();

            // 4. ★采集前预检:favorite 探测(18s 定位,不再黑洞 2×15s 才发现问题)
            if (!ct.IsCancellationRequested)
            {
                _host.DispatchUi("window.__dsh_collectStatus && window.__dsh_collectStatus(" + MainWindow.JsonText("正在检查接口状态…") + ")");
                MarkUiCollecting();
                var favOk = await DouyinProbe.CheckFavoriteApiAsync(_host.DouyinCoreInternal, uid);
                if (!favOk && !ct.IsCancellationRequested)
                {
                    // 短重试一次(3s,滤掉瞬时抖动);仍不通则分流
                    await Task.Delay(3000, ct);
                    favOk = await DouyinProbe.CheckFavoriteApiAsync(_host.DouyinCoreInternal, uid);
                }
                if (!favOk && !ct.IsCancellationRequested)
                {
                    await HandleInterfaceDownAsync(uid, collector.Count, ct);
                    if (!ct.IsCancellationRequested)
                        return;   // 已分流(弹验证窗或报错),本轮结束
                }
            }
            if (ct.IsCancellationRequested) return;

            // 5. 翻页采集(单一通道;分轮续采:单轮 200 页上限到达后自动从 MaxCursor 继续,
            //    否则超长列表再点采集会立即命中"整页已知"增量停止,尾部永远采不到。最多 30 轮 ≈ 10万条)
            var knownIds = collector.Items.Select(i => i.AwemeId).ToList();
            var reason = "";
            for (var round = 0; round < 30; round++)
            {
                collector.Round = round + 1;   // 进度文本显示轮次(用户看到"第1页重新计数"时知道是续轮)
                reason = await collector.StartDirectAsync(uid, startCursor, knownIds, ct);
                AppLog.Write($"COLLECT direct#{round} {reason}");
                // 每轮落盘一次(≈3600 条粒度):防进程崩溃/断电丢整轮数据(异常分支虽有兜底,硬崩溃无解)
                try { _host.SaveNow(true); } catch { }
                var rk = reason.Split(':')[0];
                if (rk != "safety" || ct.IsCancellationRequested) break;
                startCursor = collector.MaxCursor;
            }
            var reasonKey = reason.Split(':')[0];

            // 6. 翻页中失败(0 页黑洞/blocked)→ favorite 探测分流:波动自愈已由 JS 退避处理,
            //    到这里还不通就是限流;已有数据弹验证窗,无数据普通报错
            if (reasonKey is "notready" or "blocked" && !ct.IsCancellationRequested)
            {
                var fetchedPages = 0;
                var colon = reason.IndexOf(':');
                if (colon > 0) int.TryParse(reason[(colon + 1)..], out fetchedPages);
                if (fetchedPages == 0)
                {
                    await HandleInterfaceDownAsync(uid, collector.Count, ct);
                    return;
                }
                // 中途失败(>0 页):数据已落库,断点续采标志由下方统一处理
            }

            // 7. 收尾:结束原因 → UI 反馈(complete/incremental/stalled/safety=正常收尾)
            var ok = reasonKey is "complete" or "stalled" or "incremental" or "safety";
            _lastCollectIncomplete = !ok;   // 未跑完(失败/停止)→ 下次点采集从断点续采
            if (ok) _autoRecoverCount = 0;   // 采集正常完成,重置自愈计数
            _host.SaveNow(_lastCollectIncomplete);
            await _host.PushStateAsync();
            _host.DispatchUi("window.__dsh_refresh && window.__dsh_refresh()");
            if (reasonKey == "busy")
                _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("已有采集在进行") + ",true)");
            else
            {
                sentDone = true;
                _host.DispatchUi($"window.__dsh_collectDone && window.__dsh_collectDone({MainWindow.JsonText(collector.Count.ToString())},{(ok ? "true" : "false")})");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("COLLECT ERR " + ex);
            try
            {
                _lastCollectIncomplete = true;   // 异常中断 → 下次断点续采(保住已采数据)
                _host.SaveNow(true);
            }
            catch { }
            sentDone = true;
            _host.DispatchUi($"window.__dsh_collectDone && window.__dsh_collectDone({MainWindow.JsonText((_host.Collector?.Count ?? 0).ToString())},false)");
        }
        finally
        {
            _collecting = false;
            _collectCts = null;
            // 统一收口:UI 还在"采集中"且本轮没发过 collectDone、也没挂起等待自动续采 → 补发。
            // 覆盖:未登录弹窗后 return、探测 Blocked return、取消停止、HandleInterfaceDown 的弹验证窗
            // 分支(它故意不发 collectDone 保持进度条显示,但有 _pendingCollectResume 挂起)等所有路径。
            if (uiCollecting && !sentDone && !_pendingCollectResume && !_host.IsShuttingDown)
            {
                AppLog.Write("COLLECT done via finally-fallback");
                _host.DispatchUi($"window.__dsh_collectDone && window.__dsh_collectDone({MainWindow.JsonText((_host.Collector?.Count ?? 0).ToString())},false)");
            }
        }
    }

    /// <summary>
    /// 收藏接口不可用时的统一分流:
    /// 已有数据 = 限流(网络抖动已被调用方的重试排除)→ 弹验证窗,轮询 favorite 恢复,
    ///   恢复即自动关窗并从断点续采;
    /// 无数据 = 网络/环境问题 → 报错退出(弹验证窗无意义,页面里没有滑块可滑)。
    /// </summary>
    private async Task HandleInterfaceDownAsync(string uid, int collectedCount, CancellationToken ct)
    {
        AppLog.Write($"COLLECT interface down (stored={collectedCount})");
        if (collectedCount > 0)
        {
            // 限流:先 reload 重建 SDK(旧页面拦截器持过期状态也会黑洞),再确认一次
            await _host.ReloadDouyinPageAsync();
            if (ct.IsCancellationRequested) return;
            var favOk = await DouyinProbe.CheckFavoriteApiAsync(_host.DouyinCoreInternal, uid);
            if (favOk)
            {
                AppLog.Write("COLLECT recovered after reload");
                _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("已恢复页面状态,自动继续采集…") + ",false)");
                await ResumeAsync();
                return;
            }
            // 真限流 → 验证窗(轮询 favorite 恢复;传 sec_uid 作为完成信号)
            _pendingCollectResume = true;   // 验证通过后自动从断点续采
            _autoRecoverCount = 0;
            _host.DispatchUi("window.__dsh_collectStatus && window.__dsh_collectStatus(" + MainWindow.JsonText("接口被限,已暂停采集;请在验证窗口完成滑块,或等其自动恢复") + ")");
            _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("接口被限,请在弹出的页面完成滑块验证") + ",true)");
            _host.OpenAuthWindow(DouyinAuthWindow.AuthMode.Verify, uid);
        }
        else
        {
            // 无数据:网络不通或环境未就绪,普通报错
            _lastCollectIncomplete = true;
            _host.SaveNow(true);
            _host.DispatchUi("window.__dsh_toast && window.__dsh_toast(" + MainWindow.JsonText("接口持续无响应(网络或临时限制),请稍后重新采集") + ",true)");
            _host.DispatchUi($"window.__dsh_collectDone && window.__dsh_collectDone({MainWindow.JsonText(collectedCount.ToString())},false)");
        }
    }

    /// <summary>
    /// 账号探测状态机(采集前,只管登录态/sec_uid):
    /// 第一轮原地退避重试(瞬时波动);第二轮重载隐藏页重建 securitySDK 再试。
    /// </summary>
    private async Task<(ApiHealth health, string uid)> ProbeAccountAsync(CancellationToken ct, Action? markUiCollecting = null)
    {
        _host.DispatchUi("window.__dsh_collectStatus && window.__dsh_collectStatus(" + MainWindow.JsonText("正在获取账号信息…") + ")");
        markUiCollecting?.Invoke();

        // 第一轮:原地退避重试(2s/2s)
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            var (h, u) = await DouyinProbe.CheckHealthAsync(_host.DouyinCoreInternal);
            AppLog.Write($"PROBE r1#{i + 1} {h}");
            if (h != ApiHealth.NotReady) return (h, u);
            if (i < 2) await Task.Delay(2000, ct);
        }

        // 第二轮:重载隐藏页(重建 SDK 拦截器状态),等 2 秒初始化后重试
        _host.DispatchUi("window.__dsh_collectStatus && window.__dsh_collectStatus(" + MainWindow.JsonText("正在刷新页面状态…") + ")");
        await _host.ReloadDouyinPageAsync();
        for (var i = 0; i < 4 && !ct.IsCancellationRequested; i++)
        {
            var (h, u) = await DouyinProbe.CheckHealthAsync(_host.DouyinCoreInternal);
            AppLog.Write($"PROBE r2#{i + 1} {h}");
            if (h != ApiHealth.NotReady) return (h, u);
            await Task.Delay(3000, ct);
        }

        return (ApiHealth.NotReady, "");
    }
}
