using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace DouyinShuffle.Win.Capture;

/// <summary>
/// 新增功能：批量取消抖音点赞。
/// 独立于原 LikeCollector / 本地删除逻辑，不修改原采集与删除行为。
/// 复用现有登录态的 WebView2，在页面上下文调用当前 Web 端的取消点赞接口。
/// </summary>
public sealed class UnlikeService
{
    private readonly CoreWebView2 _webView;
    private volatile bool _running;

    public bool IsRunning => _running;

    public event Action<int, int, string>? ProgressChanged;

    public UnlikeService(CoreWebView2 webView)
    {
        _webView = webView;
    }

    public async Task<UnlikeBatchResult> UnlikeBatchAsync(
        IReadOnlyCollection<string> awemeIds,
        CancellationToken cancellationToken = default)
    {
        if (_running)
            return new UnlikeBatchResult(0, awemeIds.Count, new[] { "已有取消点赞任务正在执行" });

        _running = true;
        var success = 0;
        var failures = new List<string>();

        try
        {
            var ids = awemeIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            for (var i = 0; i < ids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = ids[i];
                try
                {
                    var result = await UnlikeOneAsync(id, cancellationToken);
                    if (result.Success)
                    {
                        success++;
                        ProgressChanged?.Invoke(i + 1, ids.Count, $"已取消 {i + 1}/{ids.Count}");
                    }
                    else
                    {
                        failures.Add($"{id}: {result.Message}");
                        ProgressChanged?.Invoke(i + 1, ids.Count, $"失败 {i + 1}/{ids.Count}: {result.Message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{id}: {ex.Message}");
                    ProgressChanged?.Invoke(i + 1, ids.Count, $"失败 {i + 1}/{ids.Count}");
                }

                // 保守节奏：避免连续点击过快。不是并发请求。
                if (i + 1 < ids.Count)
                    await Task.Delay(Random.Shared.Next(900, 1500), cancellationToken);
            }
        }
        finally
        {
            _running = false;
        }

        return new UnlikeBatchResult(success, failures.Count, failures);
    }

    private async Task<UnlikeOneResult> UnlikeOneAsync(string awemeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(awemeId))
            return new UnlikeOneResult(false, "无效视频 ID");

        // WebView2 的 ExecuteScriptAsync 不会等待 Promise。
        // 因此不能直接 await 一个 async IIFE 的返回值；必须让页面通过
        // WebMessageReceived 把 fetch 结果回传给 C#。
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var jo = JObject.Parse(e.WebMessageAsJson);
                if (jo["type"]?.Value<string>() == "dsh_unlike_resp" &&
                    jo["id"]?.Value<string>() == requestId)
                {
                    tcs.TrySetResult(jo);
                }
            }
            catch { }
        }

        _webView.WebMessageReceived += Handler;
        try
        {
            var escapedId = Newtonsoft.Json.JsonConvert.ToString(awemeId);
            var escapedRequestId = Newtonsoft.Json.JsonConvert.ToString(requestId);

            var js = $$"""
            (function () {
              const id = {{escapedRequestId}};
              const awemeId = {{escapedId}};
              function post(obj) {
                try {
                  obj.type = 'dsh_unlike_resp';
                  obj.id = id;
                  window.chrome.webview.postMessage(obj);
                } catch (e) {}
              }
              (async function () {
                try {
                  const body = new URLSearchParams();
                  body.set('aweme_id', awemeId);
                  body.set('item_type', '0');
                  body.set('type', '0');

                  const resp = await fetch('/aweme/v1/web/commit/item/digg/?aid=6383', {
                    method: 'POST',
                    credentials: 'include',
                    headers: {
                      'accept': 'application/json, text/plain, */*',
                      'content-type': 'application/x-www-form-urlencoded;charset=UTF-8'
                    },
                    body: body.toString()
                  });

                  const text = await resp.text();
                  let data = null;
                  try { data = JSON.parse(text); } catch (_) {}

                  post({
                    ok: resp.ok,
                    http: resp.status,
                    status_code: data && data.status_code,
                    status_msg: data && (data.status_msg || data.msg),
                    raw: text.slice(0, 800)
                  });
                } catch (e) {
                  post({ error: String(e) });
                }
              })();
            })();
            """;

            await _webView.ExecuteScriptAsync(js);

            var completed = await Task.WhenAny(
                tcs.Task,
                Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));

            if (completed != tcs.Task)
                return new UnlikeOneResult(false, "接口请求超时（10秒）");

            var result = await tcs.Task;
            var error = result.Value<string>("error");
            if (!string.IsNullOrWhiteSpace(error))
                return new UnlikeOneResult(false, "浏览器请求失败: " + error);

            var http = result.Value<int?>("http") ?? 0;
            var statusCode = result.Value<int?>("status_code");
            var statusMsg = result.Value<string>("status_msg");
            var raw = result.Value<string>("raw") ?? "";

            // 必须同时满足 HTTP 2xx + status_code=0 才算成功。
            // 不再把“没有 status_code”误判成成功。
            if (http >= 200 && http < 300 && statusCode == 0)
                return new UnlikeOneResult(true, "ok");

            var detail = !string.IsNullOrWhiteSpace(statusMsg)
                ? statusMsg
                : (!string.IsNullOrWhiteSpace(raw) ? raw : "无返回内容");

            return new UnlikeOneResult(false,
                $"接口失败: {detail} (HTTP {http}, code {statusCode?.ToString() ?? "?"})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UnlikeOneResult(false, "请求异常: " + ex.Message);
        }
        finally
        {
            _webView.WebMessageReceived -= Handler;
        }
    }



public sealed record UnlikeOneResult(bool Success, string Message);

public sealed record UnlikeBatchResult(
    int Success,
    int Failed,
    IReadOnlyList<string> Failures);
