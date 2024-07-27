using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace CacheOGP.ApiService;

public class OgpDbContext(DbContextOptions<OgpDbContext> options) : DbContext(options)
{
    public DbSet<OgpInfo> Ogps => Set<OgpInfo>();
}

public record OgpInfo(
    [property: Key]
    Uri Origin,
    Uri Url,
    string Title,
    string Type,
    Uri Image,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    string? Etag = null,
    DateTime? LastModified = null,
    string? SiteName = null,
    string? Description = null,
    string? Locale = null)
{
    public static implicit operator Ogp(OgpInfo info)
        => new(info.Url, info.Title, info.Type, info.Image, info.SiteName, info.Description, info.Locale);
}

public record Ogp(
    Uri Url,
    string Title,
    string Type,
    Uri Image,
    string? SiteName = null,
    string? Description = null,
    string? Locale = null);
