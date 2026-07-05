using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Alife.Platform;
using Microsoft.Extensions.Logging;

namespace Alife.Plugin.PixivSearcher;

public class PixivSearcherData
{
    [Description("Pixiv Refresh Token（必填）")]
    public string RefreshToken { get; set; } = "";

    [Description("HTTP 代理（如 http://127.0.0.1:7890）")]
    public string Proxy { get; set; } = "";

    [Description("请求超时（秒）")]
    public int Timeout { get; set; } = 90;

    [Description("最低收藏数阈值")]
    public int MinBookmarks { get; set; } = 100;

    [Description("排除标签（逗号分隔）")]
    public string ExcludeTags { get; set; } = "AI,R-18,裸足";

    [Description("图片缓存目录")]
    public string ImageStorageDir { get; set; } = "files/pixiv";

    [Description("最大搜索页数")]
    public int MaxSearchPages { get; set; } = 10;

    [Description("默认下载原图")]
    public bool DownloadOriginal { get; set; } = false;

    [Description("私聊允许 R-18（x_restrict=1）")]
    public bool AllowR18InPrivate { get; set; } = false;

    [Description("私聊允许 R-18G（x_restrict=2）")]
    public bool AllowR18GInPrivate { get; set; } = false;

    [Description("R-18 会话白名单（逗号分隔，如 qq:dm:123456,qq:gm:789012）")]
    public string R18Whitelist { get; set; } = string.Empty;

    [Description("R-18G 会话白名单（逗号分隔，如 qq:dm:123456,qq:gm:789012）")]
    public string R18GWhitelist { get; set; } = string.Empty;

    [Description("去重缓存时间（小时），0 表示永久")]
    public int DedupHours { get; set; } = 24;

    [Description("缓存文件数量上限")]
    public int MaxCacheSize { get; set; } = 500;
}

[Module("Pixiv图片搜索", "从 Pixiv 搜索高人气图片并发送到 QQ", defaultCategory: "智乃的肘击")]
public class PixivSearcherModule(
    XmlFunctionCaller functionService,
    ILogger<PixivSearcherModule> logger
) : InteractiveModule<PixivSearcherModule>, IConfigurable<PixivSearcherData>
{
    private static readonly string _baseUrl = "https://app-api.pixiv.net";
    private static readonly HashSet<string> _sentCache = new();
    private static DateTime _cacheLoadTime = DateTime.MinValue;
    private static readonly object _cacheLock = new();

    public PixivSearcherData? Configuration { get; set; } = new();

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        functionService.RegisterHandler(new XmlHandler(this));
        LoadCache();
        logger.LogInformation("[PixivSearcher] 初始化完成，已加载 {Count} 条去重记录", _sentCache.Count);
    }

    /// <summary>
    /// 加载去重缓存
    /// </summary>
    private void LoadCache()
    {
        lock (_cacheLock)
        {
            try
            {
                var cfg = Configuration ?? new PixivSearcherData();
                var cacheFile = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!, "sent_cache.json");
                if (File.Exists(cacheFile))
                {
                    var json = File.ReadAllText(cacheFile);
                    var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                    if (data != null)
                    {
                        var now = DateTime.Now;
                        var cutoff = cfg.DedupHours > 0 ? now.AddHours(-cfg.DedupHours) : DateTime.MinValue;
                        _sentCache.Clear();
                        foreach (var kv in data)
                        {
                            if (cfg.DedupHours == 0 || kv.Value > cutoff)
                            {
                                _sentCache.Add(kv.Key);
                            }
                        }
                        _cacheLoadTime = now;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "加载去重缓存失败");
            }
        }
    }

    /// <summary>
    /// 保存去重缓存
    /// </summary>
    private void SaveCache()
    {
        lock (_cacheLock)
        {
            try
            {
                var cfg = Configuration ?? new PixivSearcherData();
                var cacheFile = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!, "sent_cache.json");
                var dir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var data = _sentCache.ToDictionary(id => id, _ => DateTime.Now);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(cacheFile, json);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "保存去重缓存失败");
            }
        }
    }

    /// <summary>
    /// 检查是否已发送过
    /// </summary>
    private bool IsSent(string id)
    {
        lock (_cacheLock)
        {
            return _sentCache.Contains(id);
        }
    }

    /// <summary>
    /// 标记为已发送
    /// </summary>
    private void MarkSent(string id)
    {
        lock (_cacheLock)
        {
            _sentCache.Add(id);
            // 限制缓存大小
            var cfg = Configuration ?? new PixivSearcherData();
            if (_sentCache.Count > cfg.MaxCacheSize)
            {
                var toRemove = _sentCache.Count - cfg.MaxCacheSize;
                var removeList = _sentCache.Take(toRemove).ToList();
                foreach (var item in removeList)
                {
                    _sentCache.Remove(item);
                }
            }
            SaveCache();
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从 Pixiv 搜索高人气图片并发送到当前聊天")]
    public async Task Pixivsearch(
        [Description("搜索关键词")] string keyword,
        [Description("图片张数")] int count = 1,
        [Description("是否发送原图")] bool original = false)
    {
        var cfg = Configuration ?? new PixivSearcherData();
        try
        {
            if (string.IsNullOrEmpty(cfg.RefreshToken))
            { Poke("error: 未配置 Refresh Token"); return; }

            // 检查缓存是否过期，重新加载
            if (cfg.DedupHours > 0 && (DateTime.Now - _cacheLoadTime).TotalHours > cfg.DedupHours)
            {
                LoadCache();
            }

            var handler = string.IsNullOrEmpty(cfg.Proxy)
                ? new HttpClientHandler()
                : new HttpClientHandler { Proxy = new System.Net.WebProxy(cfg.Proxy) };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "PixivAndroidApp/5.0.234 (Android 11; Pixel 5)");
            client.DefaultRequestHeaders.Add("Referer", "https://app-api.pixiv.net/");

            var token = await AuthAsync(client, cfg.RefreshToken);
            if (token == null)
            { Poke("error: Token 认证失败"); return; }

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Add("User-Agent", "PixivAndroidApp/5.0.234 (Android 11; Pixel 5)");
            client.DefaultRequestHeaders.Add("Referer", "https://app-api.pixiv.net/");

            // 搜索时排除已发送的
            var illusts = await SearchAsync(client, keyword, count * 3, cfg, true);
            
            // 过滤掉已发送的
            var filteredIllusts = illusts.Where(i => !IsSent(i.GetProperty("id").GetInt32().ToString())).ToList();
            
            if (filteredIllusts.Count == 0)
            {
                Poke($"🔍 没有找到与「{keyword}」相关的新图片（已发送过的不会重复发）");
                return;
            }

            // 只取需要的数量
            var selectedIllusts = filteredIllusts.Take(count).ToList();

            var storageDir = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!);
            Directory.CreateDirectory(storageDir);
            var paths = new List<string>();
            var r18Blocked = 0;
            var r18gBlocked = 0;

            foreach (var illust in selectedIllusts)
            {
                int xRestrict = 0;
                if (illust.TryGetProperty("x_restrict", out var xRestrictElement))
                {
                    if (xRestrictElement.ValueKind == JsonValueKind.Number)
                        xRestrict = xRestrictElement.GetInt32();
                    else if (xRestrictElement.ValueKind == JsonValueKind.String)
                        int.TryParse(xRestrictElement.GetString(), out xRestrict);
                }

                var isR18 = xRestrict == 1;
                var isR18G = xRestrict == 2;

                if (isR18 && !cfg.AllowR18InPrivate) { r18Blocked++; continue; }
                if (isR18G && !cfg.AllowR18GInPrivate) { r18gBlocked++; continue; }

                var imgUrl = GetImageUrl(illust, original || cfg.DownloadOriginal);
                if (string.IsNullOrEmpty(imgUrl)) continue;
                var id = illust.GetProperty("id").GetInt32().ToString();
                var path = await DownloadAsync(client, imgUrl, id, storageDir);
                if (path != null)
                {
                    paths.Add(path);
                    MarkSent(id); // 标记已发送
                }
            }

            var totalFound = illusts.Count;
            var totalSent = paths.Count;
            var totalFiltered = filteredIllusts.Count;

            if (totalSent == 0)
            {
                var msg = $"🔍 找到 {totalFound} 张图片，其中 {totalFound - totalFiltered} 张已发送过";
                if (r18Blocked > 0) msg += $"，{r18Blocked} 张被 R-18 拦截";
                if (r18gBlocked > 0) msg += $"，{r18gBlocked} 张被 R-18G 拦截";
                msg += "，没有可发送的新图片";
                Poke(msg);
                return;
            }

            var successMsg = $"🖼️ 找到 {totalFound} 张图片，其中 {totalFound - totalFiltered} 张已发送过";
            if (r18Blocked > 0) successMsg += $"，{r18Blocked} 张被 R-18 拦截";
            if (r18gBlocked > 0) successMsg += $"，{r18gBlocked} 张被 R-18G 拦截";
            successMsg += $"，成功发送 {totalSent} 张新图";
            Poke($"{successMsg}：{string.Join("|", paths)}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PixivSearcher] 搜索失败");
            Poke($"error: {ex.Message}");
        }
    }

    private async Task<string?> AuthAsync(HttpClient client, string refreshToken)
    {
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", "MOBrBDS8blbauoSck0ZfDbtuzpyT"),
                new KeyValuePair<string, string>("client_secret", "lsACyCD94FhDUtGTXi3QzcFE2uU1hqtDaKeqrdwj"),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            });
            var resp = await client.PostAsync("https://oauth.secure.pixiv.net/auth/token", form);
            resp.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("access_token").GetString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token 认证失败");
            return null;
        }
    }

    private async Task<List<JsonElement>> SearchAsync(HttpClient client, string keyword, int count, PixivSearcherData cfg, bool skipSent = false)
    {
        var result = new List<JsonElement>();
        var excludeList = cfg.ExcludeTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

        for (int page = 0; page < cfg.MaxSearchPages && result.Count < count; page++)
        {
            try
            {
                var query = new Dictionary<string, string>
                {
                    ["word"] = keyword,
                    ["search_target"] = "partial_match_for_tags",
                    ["sort"] = "popular_desc",
                    ["filter"] = "for_ios",
                };
                if (cfg.MinBookmarks > 0) query["bookmark_num_min"] = cfg.MinBookmarks.ToString();
                if (page > 0) query["offset"] = (page * 30).ToString();

                var qs = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
                var resp = await client.GetAsync($"{_baseUrl}/v1/search/illust?{qs}");
                resp.EnsureSuccessStatusCode();
                var illusts = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("illusts").EnumerateArray().ToList();

                foreach (var illust in illusts)
                {
                    if (result.Count >= count) break;

                    // 检查排除标签
                    var tags = illust.GetProperty("tags").EnumerateArray()
                        .Select(t => t.GetProperty("name").GetString()).ToList();
                    if (excludeList.Any(e => tags.Contains(e))) continue;

                    // 跳过已发送的
                    if (skipSent)
                    {
                        var id = illust.GetProperty("id").GetInt32().ToString();
                        if (IsSent(id)) continue;
                    }

                    result.Add(illust);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "搜索第 {Page} 页失败", page);
                break;
            }
        }
        return result;
    }

    private string? GetImageUrl(JsonElement illust, bool original)
    {
        if (original)
            try { return illust.GetProperty("meta_single_page").GetProperty("original_image_url").GetString(); }
            catch { }
        try { return illust.GetProperty("image_urls").GetProperty("large").GetString(); }
        catch { return null; }
    }

    private async Task<string?> DownloadAsync(HttpClient client, string imgUrl, string id, string dir)
    {
        try
        {
            var resp = await client.GetAsync(imgUrl);
            resp.EnsureSuccessStatusCode();
            var ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var path = Path.Combine(dir, $"{id}{ext}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length > 0) { await File.WriteAllBytesAsync(path, bytes); return path; }
        }
        catch (Exception ex) { logger.LogWarning(ex, "下载图片 {Id} 失败", id); }
        return null;
    }
}