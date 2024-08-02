var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
var data = builder.AddPostgres("data");
var ogp = data.AddDatabase("ogp");

builder.AddProject<Projects.CacheOGP_ApiService>("cache-ogp")
// builder.AddDockerfile("cache-ogp", "..", "Dockerfile")
//     .WithHttpEndpoint(targetPort: 8080)
    .WithReference(cache)
    .WithReference(ogp);

builder.Build().Run();
