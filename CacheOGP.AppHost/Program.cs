var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
var data = builder.AddPostgres("data");
var ogp = data.AddDatabase("ogp");

builder.AddProject<Projects.CacheOGP_ApiService>("cache-ogp")
    .WithReference(cache)
    .WithReference(ogp);

builder.Build().Run();
