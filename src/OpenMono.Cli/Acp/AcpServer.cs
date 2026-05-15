using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Tools;

namespace OpenMono.Acp;

public static class AcpServer
{
    public static async Task StartAsync(
        AcpServerSettings settings,
        AppConfig config,
        ILlmClient llm,
        ToolRegistry toolRegistry,
        CancellationToken ct)
    {
        var store = new AcpSessionStore(config, settings);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(settings.Port));
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins(settings.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(llm);
        builder.Services.AddSingleton(toolRegistry);
        builder.Services.AddSingleton(store);

        var app = builder.Build();
        app.UseCors();
        AcpEndpoints.Map(app);

        try
        {
            await app.RunAsync(ct);
        }
        finally
        {
            store.Dispose();
        }
    }
}
