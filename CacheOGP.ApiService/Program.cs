using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using CacheOGP.ApiService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using OpenGraphNet;
using PuppeteerSharp;
using SkiaSharp;
using UUIDNext;

var builder = WebApplication.CreateBuilder(args);

var browserFetcher = new BrowserFetcher(SupportedBrowser.Chromium);
await browserFetcher.DownloadAsync();

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
builder.Services.AddSingleton(sp => Puppeteer.LaunchAsync(new() { Browser = SupportedBrowser.Chromium }, sp.GetService<ILoggerFactory>()).Result);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseResponseCaching();
app.UseOutputCache();
app.UseResponseCompression();

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
        var last = res.Headers.GetLastModified();
        var etag = res.Headers.ETag?.Tag;
        var ogp = OpenGraph.ParseHtml(await res.Content.ReadAsStringAsync());
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
static async Task<IResult> GetOgpEmbed([FromQuery] Uri url, OgpDbContext db, HttpClient client)
{
    var ogp = await GetOgp(url, db, client);
    return Results.Text(GenHtmlContent(ogp.Title, ogp.Url, "../" + ogp.Image.ToString(), ogp.Description, ogp.SiteName), MediaTypeNames.Text.Html, Encoding.UTF8);
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
static async Task<IResult> GetImage([FromQuery] Uri url, OgpDbContext db, HttpClient client, IBrowser browser)
{
    var ogp = await GetOgpInfo(url, db, client);

    var ns = new Guid("95DDE42A-79E7-46C6-A9DC-25C8ED7CFF73");
    var id = Uuid.NewNameBased(ns, ogp.Url.ToString());
    var image = await db.Images.FindAsync(id);
    if (image is null || image.ExpiresAt < DateTime.UtcNow || image.Etag != ogp.Etag || image.LastModified != ogp.LastModified)
    {
        using var page = await browser.NewPageAsync();
        //await page.SetViewportAsync(ViewPortOptions.Default with { DeviceScaleFactor = 2});
        var thumbId = new Guid(ogp.Image.OriginalString["thumb/".Length..]);
        var thumb = await db.Images.FindAsync(thumbId) ?? throw new InvalidOperationException();
        await page.SetContentAsync(GenHtmlContent(ogp.Title, ogp.Url, thumb.GetBase64Image(), ogp.Description, ogp.SiteName));
        var element = await page.QuerySelectorAsync(".ogp-card") ?? throw new InvalidOperationException();
        var sc = await element.ScreenshotDataAsync(new() { Type = ScreenshotType.Png, OmitBackground = true, BurstMode = true });
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

static string GenHtmlContent(string title, Uri url, string image, string? desc, string? site)
    => $$"""
    <!DOCTYPE HTML>
    <meta chartset="utf-8">
    <title>{{title}}</title>
    <style>
    body {
        font-family: Arial, sans-serif;
        display: flex;
        justify-content: center;
        align-items: center;
        height: 100vh;
        margin: 0;
    }
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
        height: auto;
    }
    .ogp-content {
        padding: 16px;
    }
    .ogp-title {
        font-size: 1.5em;
        margin: 0 0 8px;
    }
    .ogp-description {
        color: #555;
        margin: 0 0 16px;
    }
    .ogp-site-name {
        font-size: 0.9em;
        color: #888;
    }
    </style>
    <div class="ogp-card">
        <a href="{{url}}" target="_blank">
            <img src="{{image}}" alt="OGP Image" class="ogp-image">
            <div class="ogp-content">
                <h1 class="ogp-title">{{title}}</h1>
                <p class="ogp-description">{{desc}}</p>
                <p class="ogp-site-name">{{site}}</p>
            </div>
        </a>
    </div>
    """;

static class Extensions
{
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
            var last = res.Headers.GetLastModified();
            var etag = res.Headers.ETag?.Tag;
            using var input = await res.Content.ReadAsStreamAsync();
            using var bitmap = SKBitmap.Decode(input);
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

    public static DateTime? GetLastModified(this HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Last-Modified", out var lm))
        {
            return null;
        }
        if (!DateTime.TryParseExact(lm.First(), "ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var last))
        {
            return null;
        }
        return last;
    }

    public static string GetBase64Image(this OgpImage image)
        => $"data:{MediaTypeNames.Image.Webp};base64,{Convert.ToBase64String(image.Image)}";
}
