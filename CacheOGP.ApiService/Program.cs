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
using SkiaSharp;
using UUIDNext;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();

builder.AddRedisOutputCache("cache");
builder.AddNpgsqlDbContext<OgpDbContext>("ogp");
builder.Services.AddResponseCaching();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseOutputCache();
app.UseResponseCaching();
app.MapGet("/info", GetOgpInfo);
app.MapGet("/ogp", GetOgp);
app.MapGet("/embed", GetOgpEmbed);
app.MapGet("/thumb/{id:guid}", GetThumb);

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
        var id = await db.SetOgpImage(client, ogp.Image ?? throw new InvalidOperationException());
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
    return Results.Text($$"""
        <!DOCTYPE HTML>
        <meta chartset="utf-8">
        <title>{{ogp.Title}}</title>
        <style>
            body {
                font-family: Arial, sans-serif;
                background-color: #f9f9f9;
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
            <a href="{{ogp.Url}}" target="_blank">
                <img src="../{{ogp.Image}}" alt="OGP Image" class="ogp-image">
                <div class="ogp-content">
                    <h1 class="ogp-title">{{ogp.Title}}</h1>
                    <p class="ogp-description">{{ogp.Description}}</p>
                    <p class="ogp-site-name">{{ogp.SiteName}}</p>
                </div>
            </a>
        </div>
        """,
        MediaTypeNames.Text.Html,
        Encoding.UTF8);
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

static class Extensions
{
    private static readonly Guid ImageNamespace = Guid.Parse("77A05C22-DF4C-450A-927D-3DF3CCB80004");

    public static async Task<Guid> SetOgpImage(this OgpDbContext db, HttpClient client, Uri originUrl)
    {
        var id = Uuid.NewNameBased(ImageNamespace, originUrl.ToString());
        var iamge = await db.FindAsync<OgpImage>(id);
        if (iamge?.ExpiresAt > DateTime.UtcNow)
        {
            return id;
        }
        var req = new HttpRequestMessage(HttpMethod.Get, originUrl);
        if (!string.IsNullOrEmpty(iamge?.Etag))
        {
            req.Headers.IfNoneMatch.Add(new(iamge.Etag));
        }
        if (iamge?.LastModified is not null)
        {
            req.Headers.IfModifiedSince = iamge.LastModified;
        }
        var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var age = res.Headers.CacheControl?.MaxAge ?? default;
        age = age < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : age;
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            _ = iamge ?? throw new InvalidOperationException();
            iamge = iamge with { ExpiresAt = DateTime.UtcNow + age };
        }
        else
        {
            var isa = res.Headers.Date?.UtcDateTime ?? DateTime.UtcNow;
            var exp = isa + age;
            var last = res.Headers.GetLastModified();
            var etag = res.Headers.ETag?.Tag;
            using var input = await res.Content.ReadAsStreamAsync();
            using var bitmap = SKBitmap.Decode(input);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 100);
            using var output = data.AsStream(true);
            var bytes = new byte[output.Length];
            await output.ReadAsync(bytes.AsMemory(0, bytes.Length));
            iamge = new(id, originUrl, bytes, isa, exp, etag, last);
        }
        await db.Upsert(iamge).RunAsync();
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
}
