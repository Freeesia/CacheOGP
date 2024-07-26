var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.CacheOGP_ApiService>("apiservice");

builder.AddProject<Projects.CacheOGP_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService);

builder.Build().Run();
