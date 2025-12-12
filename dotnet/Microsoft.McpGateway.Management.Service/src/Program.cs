// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Identity.Web;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Service.Authentication;
using Microsoft.McpGateway.Management.Store;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var credential = new DefaultAzureCredential();

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddLogging();

builder.Services.AddSingleton<IKubernetesClientFactory, LocalKubernetesClientFactory>();

if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);

    var redisConnection = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "mcpgateway:";
    });

    builder.Services.AddSingleton<IAdapterResourceStore, RedisAdapterResourceStore>();
    builder.Services.AddSingleton<IToolResourceStore, RedisToolResourceStore>();

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
else
{
    var azureAdConfig = builder.Configuration.GetSection("AzureAd");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(azureAdConfig);

    // Create CosmosClient with credential-based authentication
    var cosmosConfig = builder.Configuration.GetSection("CosmosSettings");
    var cosmosClient = new CosmosClient(
        cosmosConfig["AccountEndpoint"], 
        credential, 
        new CosmosClientOptions
        {
            Serializer = new CosmosSystemTextJsonSerializer(new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            })
        });

    builder.Services.AddSingleton<IAdapterResourceStore>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CosmosAdapterResourceStore>>();
        return new CosmosAdapterResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "AdapterContainer", logger);
    });

    builder.Services.AddSingleton<IToolResourceStore>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CosmosToolResourceStore>>();
        return new CosmosToolResourceStore(cosmosClient, cosmosConfig["DatabaseName"]!, "ToolContainer", logger);
    });
    
    builder.Services.AddCosmosCache(options =>
    {
        options.ContainerName = "CacheContainer";
        options.DatabaseName = cosmosConfig["DatabaseName"]!;
        options.CreateIfNotExists = true;
        options.ClientBuilder = new CosmosClientBuilder(cosmosConfig["AccountEndpoint"], credential);
    });
}

builder.Services.AddSingleton<IKubeClientWrapper>(c =>
{
    var kubeClientFactory = c.GetRequiredService<IKubernetesClientFactory>();
    return new KubeClient(kubeClientFactory, "adapter");
});
builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
builder.Services.AddSingleton<IAdapterDeploymentManager>(c =>
{
    var config = builder.Configuration.GetSection("ContainerRegistrySettings");
    return new KubernetesAdapterDeploymentManager(config["Endpoint"]!, c.GetRequiredService<IKubeClientWrapper>(), c.GetRequiredService<ILogger<KubernetesAdapterDeploymentManager>>());
});
builder.Services.AddSingleton<IAdapterManagementService, AdapterManagementService>();
builder.Services.AddSingleton<IToolManagementService, ToolManagementService>();
builder.Services.AddSingleton<IAdapterRichResultProvider, AdapterRichResultProvider>();

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8001);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.RunAsync();

