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
    public int DedupHours { get; set; } = 168;

    [Description("缓存文件数量上限")]
    public int MaxCacheSize { get; set; } = 500;
}

[Module("Pixiv图片搜索", "从 Pixiv 搜索高人气图片并发送到 QQ", 
    defaultCategory: "智乃的肘击",
    LaunchOrder = 20)]
public class PixivSearcherModule(
    XmlFunctionCaller functionService,
    ILogger<PixivSearcherModule> logger
) : InteractiveModule<PixivSearcherModule>, IConfigurable<PixivSearcherData>
{
    private static readonly string _baseUrl = "https://app-api.pixiv.net";
    private static readonly HashSet<string> _sentCache = new();
    private static DateTime _cacheLoadTime = DateTime.MinValue;
    private static readonly object _cacheLock = new();
    private static string _cacheFilePath = "";

    private class ImageInfo
    {
        public string Path { get; set; } = "";
        public int Bookmarks { get; set; }
        public int Id { get; set; }
    }

    public PixivSearcherData? Configuration { get; set; } = new();

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        functionService.RegisterHandler(new XmlHandler(this));
        
        var cfg = Configuration ?? new PixivSearcherData();
        _cacheFilePath = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!, "sent_cache.json");
        
        LoadCache();
        logger.LogInformation("[PixivSearcher] 初始化完成，缓存文件: {CacheFile}，已加载 {Count} 条记录", _cacheFilePath, _sentCache.Count);
    }

    private void LoadCache()
    {
        lock (_cacheLock)
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                    if (data != null)
                    {
                        var now = DateTime.Now;
                        var cfg = Configuration ?? new PixivSearcherData();
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
                        logger.LogInformation("[PixivSearcher] ✅ 成功加载 {Count} 条去重记录（{DedupHours}小时内）", _sentCache.Count, cfg.DedupHours);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[PixivSearcher] ❌ 加载去重缓存失败");
            }
        }
    }

    private void SaveCache()
    {
        lock (_cacheLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var data = _sentCache.ToDictionary(id => id, _ => DateTime.Now);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_cacheFilePath, json);
                logger.LogInformation("[PixivSearcher] ✅ 去重缓存已保存，共 {Count} 条记录", _sentCache.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[PixivSearcher] ❌ 保存去重缓存失败");
            }
        }
    }

    private bool IsSent(string id)
    {
        lock (_cacheLock)
        {
            return _sentCache.Contains(id);
        }
    }

    private void MarkSent(string id)
    {
        lock (_cacheLock)
        {
            _sentCache.Add(id);
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

    /// <summary>
    /// 搜索用户，返回用户 ID
    /// </summary>
    private async Task<int?> SearchUserAsync(HttpClient client, string userName, PixivSearcherData cfg)
    {
        try
        {
            var query = new Dictionary<string, string>
            {
                ["word"] = userName,
                ["sort"] = "popular_desc",
            };
            var qs = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
            // 🔑 使用 /v1/search/user 接口
            var resp = await client.GetAsync($"{_baseUrl}/v1/search/user?{qs}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            
            var users = doc.RootElement.GetProperty("user_previews").EnumerateArray().ToList();
            if (users.Count == 0)
            {
                logger.LogWarning("[PixivSearcher] 未找到用户: {UserName}", userName);
                return null;
            }

            var user = users[0].GetProperty("user");
            var userId = user.GetProperty("id").GetInt32();
            var name = user.GetProperty("name").GetString();
            logger.LogInformation("[PixivSearcher] ✅ 找到用户: {Name} (ID: {Id})", name, userId);
            return userId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PixivSearcher] 搜索用户失败: {UserName}", userName);
            return null;
        }
    }

    /// <summary>
    /// 根据用户 ID 获取作品
    /// </summary>
    private async Task<List<(JsonElement Illust, int Bookmarks)>> SearchArtistAsync(HttpClient client, int artistId, int count, PixivSearcherData cfg, bool skipSent = false)
    {
        var result = new List<(JsonElement, int)>();
        var excludeList = cfg.ExcludeTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

        for (int page = 0; page < cfg.MaxSearchPages && result.Count < count; page++)
        {
            try
            {
                var query = new Dictionary<string, string>
                {
                    ["user_id"] = artistId.ToString(),
                    ["type"] = "illust",
                    ["sort"] = "popular_desc",
                    ["filter"] = "for_ios",
                };
                if (page > 0) query["offset"] = (page * 30).ToString();

                var qs = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
                var resp = await client.GetAsync($"{_baseUrl}/v1/user/illusts?{qs}");
                resp.EnsureSuccessStatusCode();
                var illusts = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("illusts").EnumerateArray().ToList();

                foreach (var illust in illusts)
                {
                    if (result.Count >= count) break;

                    var tags = illust.GetProperty("tags").EnumerateArray()
                        .Select(t => t.GetProperty("name").GetString()).ToList();
                    if (excludeList.Any(e => tags.Contains(e))) continue;

                    if (skipSent)
                    {
                        var id = illust.GetProperty("id").GetInt32().ToString();
                        if (IsSent(id)) continue;
                    }

                    int bookmarks = 0;
                    if (illust.TryGetProperty("total_bookmarks", out var bookmarkElem))
                    {
                        if (bookmarkElem.ValueKind == JsonValueKind.Number)
                            bookmarks = bookmarkElem.GetInt32();
                        else if (bookmarkElem.ValueKind == JsonValueKind.String)
                            int.TryParse(bookmarkElem.GetString(), out bookmarks);
                    }

                    if (cfg.MinBookmarks > 0 && bookmarks < cfg.MinBookmarks)
                    {
                        continue;
                    }

                    result.Add((illust, bookmarks));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "搜索画师作品第 {Page} 页失败", page);
                break;
            }
        }
        return result;
    }

    private async Task<List<(JsonElement Illust, int Bookmarks)>> SearchAsync(HttpClient client, string keyword, int count, PixivSearcherData cfg, bool skipSent = false)
    {
        var result = new List<(JsonElement, int)>();
        var excludeList = cfg.ExcludeTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

        for (int page = 0; page < cfg.MaxSearchPages && result.Count < count; page++)
        {
            try
            {
                var searchKeyword = keyword;
                if (cfg.MinBookmarks > 0)
                {
                    searchKeyword = $"{keyword} {cfg.MinBookmarks}users入り";
                }

                var query = new Dictionary<string, string>
                {
                    ["word"] = searchKeyword,
                    ["search_target"] = "partial_match_for_tags",
                    ["sort"] = "popular_desc",
                    ["filter"] = "for_ios",
                };
                if (page > 0) query["offset"] = (page * 30).ToString();

                var qs = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
                var resp = await client.GetAsync($"{_baseUrl}/v1/search/illust?{qs}");
                resp.EnsureSuccessStatusCode();
                var illusts = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("illusts").EnumerateArray().ToList();

                foreach (var illust in illusts)
                {
                    if (result.Count >= count) break;

                    var tags = illust.GetProperty("tags").EnumerateArray()
                        .Select(t => t.GetProperty("name").GetString()).ToList();
                    if (excludeList.Any(e => tags.Contains(e))) continue;

                    if (skipSent)
                    {
                        var id = illust.GetProperty("id").GetInt32().ToString();
                        if (IsSent(id)) continue;
                    }

                    int bookmarks = 0;
                    if (illust.TryGetProperty("total_bookmarks", out var bookmarkElem))
                    {
                        if (bookmarkElem.ValueKind == JsonValueKind.Number)
                            bookmarks = bookmarkElem.GetInt32();
                        else if (bookmarkElem.ValueKind == JsonValueKind.String)
                            int.TryParse(bookmarkElem.GetString(), out bookmarks);
                    }

                    if (cfg.MinBookmarks > 0 && bookmarks < cfg.MinBookmarks)
                    {
                        continue;
                    }

                    result.Add((illust, bookmarks));
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

            var illustsWithBookmarks = await SearchAsync(client, keyword, count * 3, cfg, true);
            var filteredIllusts = illustsWithBookmarks
                .Where(item => !IsSent(item.Illust.GetProperty("id").GetInt32().ToString()))
                .ToList();

            if (filteredIllusts.Count == 0)
            {
                Poke($"🔍 没有找到与「{keyword}」相关的新图片（已发送过的不会重复发）");
                return;
            }

            var selectedIllusts = filteredIllusts.Take(count).ToList();

            var storageDir = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!);
            Directory.CreateDirectory(storageDir);
            var imageInfos = new List<ImageInfo>();
            var r18Blocked = 0;
            var r18gBlocked = 0;

            foreach (var (illust, bookmarks) in selectedIllusts)
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
                    imageInfos.Add(new ImageInfo
                    {
                        Path = path,
                        Bookmarks = bookmarks,
                        Id = illust.GetProperty("id").GetInt32()
                    });
                    MarkSent(id);
                }
            }

            var totalFound = illustsWithBookmarks.Count;
            var totalSent = imageInfos.Count;
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

            var msgLines = new List<string>();
            msgLines.Add($"🖼️ 找到 {totalFound} 张图片，其中 {totalFound - totalFiltered} 张已发送过");
            if (r18Blocked > 0) msgLines.Add($"🚫 {r18Blocked} 张被 R-18 拦截");
            if (r18gBlocked > 0) msgLines.Add($"🚫 {r18gBlocked} 张被 R-18G 拦截");
            msgLines.Add($"✅ 成功发送 {totalSent} 张新图：");
            msgLines.Add("");

            foreach (var info in imageInfos)
            {
                msgLines.Add($"📁 {info.Path}");
                msgLines.Add($"⭐ 收藏数：{info.Bookmarks}");
                msgLines.Add("");
            }

            Poke(string.Join("\n", msgLines));
            
            logger.LogInformation("[PixivSearcher] 搜索完成，共发送 {Count} 张图片", totalSent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PixivSearcher] 搜索失败");
            Poke($"error: {ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按画师名字搜索图片（如：画师 米山舞）")]
    public async Task PixivsearchArtist(
        [Description("画师名字（如：米山舞、Wlop）")] string artistName,
        [Description("图片张数")] int count = 1,
        [Description("是否发送原图")] bool original = false)
    {
        var cfg = Configuration ?? new PixivSearcherData();
        try
        {
            if (string.IsNullOrEmpty(cfg.RefreshToken))
            { Poke("error: 未配置 Refresh Token"); return; }

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

            // 🔑 第一步：搜索用户获取 ID
            var userId = await SearchUserAsync(client, artistName, cfg);
            if (userId == null)
            {
                Poke($"❌ 未找到画师「{artistName}」，请确认名字是否正确");
                return;
            }

            // 🔑 第二步：根据用户 ID 获取作品
            var illustsWithBookmarks = await SearchArtistAsync(client, userId.Value, count * 3, cfg, true);
            var filteredIllusts = illustsWithBookmarks
                .Where(item => !IsSent(item.Illust.GetProperty("id").GetInt32().ToString()))
                .ToList();

            if (filteredIllusts.Count == 0)
            {
                Poke($"🔍 画师「{artistName}」没有找到符合条件的新图片（已发送过的不会重复发）");
                return;
            }

            var selectedIllusts = filteredIllusts.Take(count).ToList();

            var storageDir = Path.Combine(AlifePath.StorageFolderPath, cfg.ImageStorageDir!);
            Directory.CreateDirectory(storageDir);
            var imageInfos = new List<ImageInfo>();
            var r18Blocked = 0;
            var r18gBlocked = 0;

            foreach (var (illust, bookmarks) in selectedIllusts)
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
                    imageInfos.Add(new ImageInfo
                    {
                        Path = path,
                        Bookmarks = bookmarks,
                        Id = illust.GetProperty("id").GetInt32()
                    });
                    MarkSent(id);
                }
            }

            var totalFound = illustsWithBookmarks.Count;
            var totalSent = imageInfos.Count;
            var totalFiltered = filteredIllusts.Count;

            if (totalSent == 0)
            {
                var msg = $"🔍 画师「{artistName}」找到 {totalFound} 张图片，其中 {totalFound - totalFiltered} 张已发送过";
                if (r18Blocked > 0) msg += $"，{r18Blocked} 张被 R-18 拦截";
                if (r18gBlocked > 0) msg += $"，{r18gBlocked} 张被 R-18G 拦截";
                msg += "，没有可发送的新图片";
                Poke(msg);
                return;
            }

            var msgLines = new List<string>();
            msgLines.Add($"🎨 画师「{artistName}」找到 {totalFound} 张图片，其中 {totalFound - totalFiltered} 张已发送过");
            if (r18Blocked > 0) msgLines.Add($"🚫 {r18Blocked} 张被 R-18 拦截");
            if (r18gBlocked > 0) msgLines.Add($"🚫 {r18gBlocked} 张被 R-18G 拦截");
            msgLines.Add($"✅ 成功发送 {totalSent} 张新图：");
            msgLines.Add("");

            foreach (var info in imageInfos)
            {
                msgLines.Add($"📁 {info.Path}");
                msgLines.Add($"⭐ 收藏数：{info.Bookmarks}");
                msgLines.Add("");
            }

            Poke(string.Join("\n", msgLines));
            
            logger.LogInformation("[PixivSearcher] 画师搜索完成，共发送 {Count} 张图片", totalSent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PixivSearcher] 画师搜索失败");
            Poke($"error: {ex.Message}");
        }
    }
}
