using CacheOGP.ApiService;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<OgpDbContext>("ogp");

var host = builder.Build();

using var scope = host.Services.CreateScope();
await using var dbContext = scope.ServiceProvider.GetRequiredService<OgpDbContext>();
await dbContext.Database.MigrateAsync();
