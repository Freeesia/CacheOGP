using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using CacheOGP.ApiService;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.IO;
using OpenGraphNet;
using PuppeteerSharp;
using SkiaSharp;
using UUIDNext;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = WebApplication.CreateBuilder(args);

var browserFetcher = new BrowserFetcher(new BrowserFetcherOptions
{
    Browser = SupportedBrowser.Chromium,
    Path = Path.Combine(Path.GetTempPath(), "cache-ogp", "browsers"),
});
var browser = await browserFetcher.DownloadAsync();

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();

builder.AddRedisOutputCache("cache");
builder.AddNpgsqlDbContext<OgpDbContext>("ogp");
builder.Services.AddResponseCaching();
builder.Services.AddResponseCompression(op =>
{
    op.EnableForHttps = true;
});
builder.Services.AddSingleton(sp
    => Puppeteer.LaunchAsync(
        new()
        {
            Args = [
                "--no-sandbox",
            ],
            UserDataDir = Path.Combine(Path.GetTempPath(), "cache-ogp", "userdata"),
            Browser = browser.Browser,
            ExecutablePath = browser.GetExecutablePath(),
        },
        sp.GetService<ILoggerFactory>()).Result);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await using var dbContext = scope.ServiceProvider.GetRequiredService<OgpDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseResponseCaching();
app.UseOutputCache();
app.UseResponseCompression();


const string Index = """
<!DOCTYPE HTML>
<meta charset="utf-8">
<title>CacheOGP</title>
<style>
iframe {
    width: 860px;
    height: 800px;
    border: 1px solid #ccc;
    margin-top: 20px;
}
pre {
    background-color: #f5f5f5;
    border: 1px solid #ddd;
    border-radius: 4px;
    padding: 15px;
    font-family: 'Courier New', Courier, monospace;
    font-size: 14px;
    line-height: 1.5;
    white-space: pre-wrap; /* Enable line wrap */
    word-wrap: break-word; /* Prevent overflow */
    overflow: auto; /* Add scrollbars if needed */
}
</style>
<h1>CacheOGP</h1>
<p>Cache Open Graph Protocol</p>
<div>
<input type="number" id="scale-input" placeholder="スケール値" step="1" min="1">
<select id="style-select">
    <option value="Portrait">Portrait</option>
    <option value="Landscape">Landscape</option>
    <option value="Overlay">Overlay</option>
</select>
</div>
<input type="text" id="url-input" placeholder="URLを入力してください">
<button id="load-button">表示</button>
<pre id="ogp-json"></pre>
<iframe id="embed-display" src=""></iframe>
<pre id="embed-html"></pre>
<img id="image-display" src="" alt="OGP Image">
<pre id="image-md"></pre>

<script>
document.getElementById('load-button').addEventListener('click', async function() {
    var url = document.getElementById('url-input').value;
    var scale = document.getElementById('scale-input').value || 1.0;
    var mode = document.getElementById('style-select').value;
    if (url) {
        if (!url.startsWith('http://') && !url.startsWith('https://')) {
            url = 'http://' + url;
        }
        document.getElementById('embed-display').src = `/embed?style=${mode}&url=${encodeURIComponent(url)}`;
        document.getElementById('embed-html').innerText = `<iframe src="${location}embed?style=${mode}&url=${encodeURIComponent(url)}"></iframe>`;
        document.getElementById('image-display').src = `/image?scale=${scale}&style=${mode}&url=${encodeURIComponent(url)}`;
        document.getElementById('image-md').innerText = `[![Alt](${location}image?scale=${scale}&style=${mode}&url=${url})](${url})`;
        document.getElementById('ogp-json').innerText = JSON.stringify(await (await fetch('/ogp?url=' + encodeURIComponent(url))).json(), null, 2);
    } else {
        alert('URLを入力してください。');
    }
});
</script>
""";

app.MapGet("/", () => Results.Text(Index, MediaTypeNames.Text.Html, Encoding.UTF8));
app.MapGet("/info", GetOgpInfo);
app.MapGet("/ogp", GetOgp);
app.MapGet("/embed", GetOgpEmbed);
app.MapGet("/thumb/{id:guid}", GetThumb);
app.MapGet("/image", GetImage);

app.MapDefaultEndpoints();

app.Run();

#if !DEBUG
[OutputCache(Duration = 60 * 60), ResponseCache(Duration = 60 * 60)]
#endif
static async Task<Ogp> GetOgp([FromQuery] Uri url, OgpDbContext db, HttpClient client)
    => await GetOgpInfo(url, db, client);

#if !DEBUG
[OutputCache(Duration = 60 * 60), ResponseCache(Duration = 60 * 60)]
#endif
static async Task<OgpInfo> GetOgpInfo([FromQuery] Uri url, OgpDbContext db, HttpClient client)
{
    var info = await db.FindAsync<OgpInfo>(url);
    if (info?.ExpiresAt > DateTime.UtcNow)
    {
        return info;
    }
    var req = new HttpRequestMessage(HttpMethod.Get, url);
    if (!string.IsNullOrEmpty(info?.Etag))
    {
        req.Headers.IfNoneMatch.Add(new(info.Etag));
    }
    if (info?.LastModified is not null)
    {
        req.Headers.IfModifiedSince = info.LastModified;
    }
    var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    var age = res.Headers.CacheControl?.MaxAge ?? default;
    age = age < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : age;
    if (res.StatusCode == HttpStatusCode.NotModified)
    {
        _ = info ?? throw new InvalidOperationException();
        info = info with { ExpiresAt = DateTime.UtcNow + age };
    }
    else
    {
        var isa = res.Headers.Date?.UtcDateTime ?? DateTime.UtcNow;
        var exp = isa + age;
        var last = res.Content.Headers.LastModified?.UtcDateTime;
        var etag = res.Headers.ETag?.Tag;
        var ogp = OpenGraph.ParseHtml(await res.Content.ReadContentAsHtmlString());
        var id = await db.SetOgpThumb(client, ogp.Image ?? throw new InvalidOperationException());
        info = new(
            url,
            ogp.Url ?? throw new InvalidOperationException(),
            ogp.Title,
            ogp.Type,
            new($"thumb/{id}", UriKind.Relative),
            isa,
            exp,
            etag,
            last,
            ogp.Metadata.TryGetValue("og:site_name", out var s) ? s[0].Value : null,
            ogp.Metadata.TryGetValue("og:description", out var d) ? d[0].Value : null,
            ogp.Metadata.TryGetValue("og:locale", out var l) ? l[0].Value : null);
    }
    await db.Upsert(info).RunAsync();
    return info;
}


#if !DEBUG
[OutputCache(Duration = 60 * 60), ResponseCache(Duration = 60 * 60)]
#endif
static async Task<IResult> GetOgpEmbed(OgpDbContext db, HttpClient client, [FromQuery] Uri url, [FromQuery] StyleType style = StyleType.Portrait, [FromQuery] string? css = null)
{
    var ogp = await GetOgp(url, db, client);
    var styleTag = GetStyle(style, css);
    return Results.Text(GenHtmlContent(styleTag, ogp.Title, ogp.Url, "../" + ogp.Image.ToString(), ogp.Description, ogp.SiteName), MediaTypeNames.Text.Html, Encoding.UTF8);
}

#if !DEBUG
[OutputCache(Duration = 60 * 60), ResponseCache(Duration = 60 * 60)]
#endif
static async Task<IResult> GetThumb(Guid id, OgpDbContext db)
{
    var image = await db.FindAsync<OgpImage>(id);
    if (image is null)
    {
        return Results.NotFound();
    }
    return Results.Bytes(image.Image, MediaTypeNames.Image.Webp, lastModified: image.LastModified, entityTag: image.Etag is { } e ? new(e, true) : null);
}


#if !DEBUG
[OutputCache(Duration = 60 * 60), ResponseCache(Duration = 60 * 60)]
#endif
static async Task<IResult> GetImage(OgpDbContext db, HttpClient client, IBrowser browser, [FromQuery] Uri url, [FromQuery] int scale = 1, [FromQuery] StyleType style = StyleType.Portrait, [FromQuery] string? css = null)
{
    var ogp = await GetOgpInfo(url, db, client);

    var ns = new Guid("95DDE42A-79E7-46C6-A9DC-25C8ED7CFF73");
    var id = Uuid.NewNameBased(ns, ogp.Url.ToString());
    id = Uuid.NewNameBased(id, style.ToString());
    id = Uuid.NewNameBased(id, scale.ToString());
    if (css is not null)
    {
        id = Uuid.NewNameBased(id, css);
    }
#if DEBUG
    OgpImage? image = null;
#else
    var image = await db.Images.FindAsync(id);
#endif
    if (image is null || image.ExpiresAt < DateTime.UtcNow || image.Etag != ogp.Etag || image.LastModified != ogp.LastModified)
    {
        using var page = await browser.NewPageAsync();
        await page.SetViewportAsync(ViewPortOptions.Default with { DeviceScaleFactor = scale, Width = 860, Height = 800 });
        var thumbId = new Guid(ogp.Image.OriginalString["thumb/".Length..]);
        var thumb = await db.Images.FindAsync(thumbId) ?? throw new InvalidOperationException();
        var styleTag = GetStyle(style, css);
        await page.SetContentAsync(GenHtmlContent(styleTag, ogp.Title, ogp.Url, thumb.GetBase64Image(), ogp.Description, ogp.SiteName));
        var element = await page.QuerySelectorAsync(".ogp-card") ?? throw new InvalidOperationException();
        // CaptureBeyondViewportをtrueにしないと画像が欠ける
        var sc = await element.ScreenshotDataAsync(new() { Type = ScreenshotType.Png, OmitBackground = true, CaptureBeyondViewport = false });
        using var bitmap = SKBitmap.Decode(sc);
        using var ski = SKImage.FromBitmap(bitmap);
        using var data = ski.Encode(SKEncodedImageFormat.Webp, 100);
        using var output = data.AsStream(true);
        var bytes = new byte[output.Length];
        await output.ReadAsync(bytes.AsMemory(0, bytes.Length));
        image = new(id, url, bytes, ogp.IssuedAt, ogp.ExpiresAt, ogp.Etag, ogp.LastModified);
        await db.Upsert(image).RunAsync();
    }
    return Results.Bytes(image.Image, MediaTypeNames.Image.Webp, lastModified: image.LastModified, entityTag: image.Etag is { } e ? new(e, true) : null);
}

static string GenHtmlContent(string style, string title, Uri url, string image, string? desc, string? site)
    => $$"""
    <!DOCTYPE HTML>
    <meta chartset="utf-8">
    <title>{{title}}</title>
    <style>
    body {
        display: flex;
        justify-content: center;
        align-items: center;
        height: 100vh;
        margin: 0;
    }
    </style>
    {{style}}
    <div class="ogp-card">
        <a href="{{url}}" target="_blank">
            <img src="{{image}}" alt="OGP Image" class="ogp-image">
            <div class="ogp-content">
                <h1 class="ogp-title">{{title}}</h1>
                <p class="ogp-url">{{url}}</p>
                <p class="ogp-description">{{desc}}</p>
                <p class="ogp-site-name">{{site}}</p>
            </div>
        </a>
    </div>
    """;

static string GetStyle(StyleType type, string? css)
    => type switch
    {
        StyleType.Portrait => """
        <style>
        .ogp-card {
            border: 1px solid #ddd;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
            width: 600px;
            background-color: #fff;
            overflow: hidden;
            text-decoration: none;
        }
        .ogp-card a {
            text-decoration: none;
            color: inherit;
        }
        .ogp-image {
            width: 100%;
            height: 380px;
            object-fit: cover;
        }
        .ogp-content {
            padding: 1em 1em 0.5em 1em;
        }
        .ogp-title {
            font-size: 1.5em;
            margin: 0;
            overflow: hidden;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
        }
        .ogp-url {
            font-size: 0.8em;
            color: #888;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        .ogp-description {
            color: #555;
            margin: 0.6em 0;
            line-height: 1.4em;
            overflow: hidden;
            display: -webkit-box;
            -webkit-line-clamp: 3;
            -webkit-box-orient: vertical;
        }
        .ogp-site-name {
            font-size: 0.9em;
            color: #888;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        </style>
        """,
        StyleType.Landscape => """
        <style>
        .ogp-card {
            border: 1px solid #ddd;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
            width: 840px;
            background-color: #fff;
            overflow: hidden;
            text-decoration: none;
        }
        .ogp-card a {
            display: flex;
            text-decoration: none;
            color: inherit;
        }
        .ogp-image {
            width: 30%;
            object-fit: cover;
        }
        .ogp-content {
            padding: 10px;
            display: flex;
            flex-direction: column;
        }
        .ogp-title {
            font-size: 1.2em;
            margin: 0;
            text-overflow: ellipsis;
            overflow: hidden;
            -webkit-line-clamp: 2;
        }
        .ogp-url {
            font-size: 0.8em;
            color: #888;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        .ogp-description {
            font-size: 0.9em;
            color: #555;
            margin: 0.4em 0;
            line-height: 1.4em;
            overflow: hidden;
            display: -webkit-box;
            -webkit-line-clamp: 4;
            -webkit-box-orient: vertical;
        }
        .ogp-site-name {
            text-align: right;
            font-size: 0.8em;
            color: #888;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        </style>
        """,
        StyleType.Overlay => """
        <style>
        .ogp-card {
            border: 1px solid #ddd;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
            width: 600px;
            height: 400px;
            background-color: #fff;
            position: relative;
            overflow: hidden;
        }
        .ogp-card img.ogp-image {
            width: 100%;
            height: 100%;
            object-fit: cover;
        }
        .ogp-content {
            position: absolute;
            bottom: 0;
            padding: 8px;
            background: rgba(0, 0, 0, 0.6);
            color: white;
            display: flex;
            flex-direction: column;
        }
        .ogp-title {
            font-size: 1.5em;
            font-weight: bold;
            margin: 0;
        }
        .ogp-url {
            font-size: 0.8em;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        .ogp-description {
            display: none;
        }
        .ogp-site-name {
            text-align: right;
            font-size: 0.8em;
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }
        </style>
        """,
        StyleType.Custom => $"<link rel=\"stylesheet\" href=\"{css}\">",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

enum StyleType
{
    Portrait,
    Landscape,
    Overlay,
    Custom,
}

static class Extensions
{
    private static readonly RecyclableMemoryStreamManager StreamManager = new();
    private static readonly Guid ThumbNamespace = Guid.Parse("77A05C22-DF4C-450A-927D-3DF3CCB80004");

    public static async Task<Guid> SetOgpThumb(this OgpDbContext db, HttpClient client, Uri originUrl)
    {
        var id = Uuid.NewNameBased(ThumbNamespace, originUrl.ToString());
        var image = await db.FindAsync<OgpImage>(id);
        if (image?.ExpiresAt > DateTime.UtcNow)
        {
            return id;
        }
        var req = new HttpRequestMessage(HttpMethod.Get, originUrl);
        if (!string.IsNullOrEmpty(image?.Etag))
        {
            req.Headers.IfNoneMatch.Add(new(image.Etag));
        }
        if (image?.LastModified is not null)
        {
            req.Headers.IfModifiedSince = image.LastModified;
        }
        var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var age = res.Headers.CacheControl?.MaxAge ?? default;
        age = age < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : age;
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            _ = image ?? throw new InvalidOperationException();
            image = image with { ExpiresAt = DateTime.UtcNow + age };
        }
        else
        {
            var isa = res.Headers.Date?.UtcDateTime ?? DateTime.UtcNow;
            var exp = isa + age;
            var last = res.Content.Headers.LastModified?.UtcDateTime;
            var etag = res.Headers.ETag?.Tag;
            using var input = StreamManager.GetStream(id, null, res.Content.Headers.ContentLength ?? 0);
            await res.Content.CopyToAsync(input);
            input.Position = 0;
            using var codec = SKCodec.Create(input, out var result);
            if (result != SKCodecResult.Success)
            {
                throw new InvalidOperationException(result.ToString());
            }
            using var bitmap = SKBitmap.Decode(codec);
            using var ski = SKImage.FromBitmap(bitmap);
            using var data = ski.Encode(SKEncodedImageFormat.Webp, 100);
            using var output = data.AsStream(true);
            var bytes = new byte[output.Length];
            await output.ReadAsync(bytes.AsMemory(0, bytes.Length));
            image = new(id, originUrl, bytes, isa, exp, etag, last);
        }
        await db.Upsert(image).RunAsync();
        return id;
    }

    public static string GetBase64Image(this OgpImage image)
        => $"data:{MediaTypeNames.Image.Webp};base64,{Convert.ToBase64String(image.Image)}";

    public static async Task<string> ReadContentAsHtmlString(this HttpContent content)
    {
        var stream = StreamManager.GetStream();
        await content.CopyToAsync(stream);
        stream.Position = 0;
        var doc = new HtmlDocument();
        var enc = doc.DetectEncoding(stream, true);
        stream.Position = 0;
        using var reader = new StreamReader(stream, enc);
        return reader.ReadToEnd();
    }
}
