var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
var data = builder.AddPostgres("data");
var ogp = data.AddDatabase("ogp");

builder.AddProject<Projects.CacheOGP_ApiService>("apiservice")
    .WithReference(cache)
    .WithReference(ogp);

builder.Build().Run();
