using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;

namespace CatalogWeaver.MonitorClient;

public static class MonitorClientProgram
{
    public static async Task Main(string[] args)
    {
        if(args.Contains("--help"))
        {
            Console.WriteLine("CatalogWeaver.MonitorClient [--port 8870] [--no-browser] [--Client:DataDirectory PATH]");
            return;
        }
        var noBrowser=args.Contains("--no-browser");
        var builder=WebApplication.CreateBuilder(args.Where(x=>x!="--no-browser").ToArray());
        var port=builder.Configuration.GetValue("port",8870);
        if(port is <1024 or >65535)throw new ArgumentException("Choose a local port between 1024 and 65535.");
        builder.WebHost.ConfigureKestrel(server=>server.Listen(IPAddress.Loopback,port));
        builder.WebHost.ConfigureKestrel(server=>server.Limits.MaxRequestBodySize=64*1024);
        builder.Services.AddMonitorClient(builder.Configuration);
        await using var app=builder.Build();app.MapMonitorClient();
        if(!noBrowser)app.Lifetime.ApplicationStarted.Register(()=>
        {
            try{Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/monitor/"){UseShellExecute=true});}
            catch(Exception){app.Logger.LogWarning("Open the client URL in your browser.");}
        });
        await app.RunAsync();
    }
}

public static class MonitorClientHost
{
    public static void AddMonitorClient(this IServiceCollection services,IConfiguration configuration)
    {
        var root=ConnectionRegistry.DataDirectory(configuration);Directory.CreateDirectory(root);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var keyRoot=Path.Combine(root,"keys");Directory.CreateDirectory(keyRoot);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(keyRoot,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var protection=services.AddDataProtection().SetApplicationName("CatalogWeaver.MonitorClient")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRoot));
        if(OperatingSystem.IsWindows())protection.ProtectKeysWithDpapi();
        services.AddSingleton<ConnectionRegistry>();services.AddSingleton<ServerConnections>();
        services.AddHostedService(s=>s.GetRequiredService<ServerConnections>());
        services.AddHttpClient("monitoring-sources",client=>client.Timeout=Timeout.InfiniteTimeSpan).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,
                PooledConnectionLifetime=TimeSpan.FromMinutes(5),MaxConnectionsPerServer=8});
    }

    public static void MapMonitorClient(this WebApplication app)
    {
        var registry=app.Services.GetRequiredService<ConnectionRegistry>();
        var connections=app.Services.GetRequiredService<ServerConnections>();
        app.Use(async(context,next)=>
        {
            context.Response.Headers["X-CatalogWeaver-Role"]="monitor-client";
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["X-Content-Type-Options"]="nosniff";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            context.Response.Headers.ContentSecurityPolicy="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            var host=context.Request.Host.Host;
            if(host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1")) {context.Response.StatusCode=403;return;}
            if(context.Request.Path.StartsWithSegments("/monitor/api") &&
                (context.Request.Headers["X-CatalogWeaver-Request"]!="monitor"
                    || context.Request.Headers.Origin.Count>0 && context.Request.Headers.Origin!=$"{context.Request.Scheme}://{context.Request.Host}"))
            {context.Response.StatusCode=403;return;}
            try{await next(context);}
            catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){ }
            catch(Exception error)when(!context.Response.HasStarted)
            {
                var code=error is ClientConnectionException known?known.Code:error is KeyNotFoundException?"environment_not_found"
                    :error is ArgumentException or System.Text.Json.JsonException?"invalid_fields":"local_client_error";
                context.Response.StatusCode=code is "server_already_connected" or "server_identity_changed"?409
                    :error is ClientConnectionException?502:error is KeyNotFoundException?404:error is ArgumentException or System.Text.Json.JsonException?400:500;
                app.Logger.LogWarning("Monitor client operation: {Code}",code);
                await context.Response.WriteAsJsonAsync(new{error=code});
            }
        });
        app.MapGet("/",()=>Results.Redirect("/monitor/"));
        foreach(var name in new[]{"index.html","app.js","styles.css"})
        {
            var file=name;
            app.MapGet(file=="index.html"?"/monitor/":"/monitor/"+file,()=>Results.Stream(
                Assembly.GetExecutingAssembly().GetManifestResourceStream("CatalogWeaver.Web."+file)!,
                file.EndsWith(".js")?"text/javascript; charset=utf-8":file.EndsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8"));
        }
        app.MapGet("/monitor/api/client-info",()=>Results.Ok(new{role="monitor-client",version=typeof(MonitorClientHost).Assembly.GetName().Version?.ToString(),
            credentialsProtection=OperatingSystem.IsWindows()?"Windows DPAPI":"private local keyring",settingsDirectory=registry.DirectoryPath}));
        app.MapGet("/monitor/api/environments",()=>Results.Json(registry.List().Select(environment=>
        {
            var status=connections.State(environment.Id);
            return new{environment.Id,environment.Name,environment.Address,environment.ServerId,environment.Color,environment.Description,
                environment.IntervalSeconds,environment.Enabled,environment.TimeoutSeconds,environment.MinimumFreeBytes,environment.NoProgressMinutes,
                data=status?.Data,error=status?.Error,checkedUtc=status?.CheckedUtc,successUtc=status?.SuccessUtc};
        })));
        async Task<IResult> Save(Guid? id,EnvironmentInput input,CancellationToken ct)
        {
            ConnectionRegistry.Validate(input);
            var existing=id is null?null:registry.Get(id.Value);
            var key=string.IsNullOrEmpty(input.Key)&&existing is not null?registry.Key(existing):input.Key;
            if(string.IsNullOrWhiteSpace(key)||key.Length is <32 or >4096||key.Contains('\r')||key.Contains('\n'))
                return Results.BadRequest(new{error="invalid_monitor_key"});
            Guid serverId;
            if(!input.Enabled&&existing is not null&&ConnectionRegistry.Address(input.Address).AbsoluteUri==existing.Address&&string.IsNullOrEmpty(input.Key))serverId=existing.ServerId;
            else serverId=ServerConnections.ValidateSnapshot(await connections.FetchAsync(input.Address,key,"snapshot",input.TimeoutSeconds,ct));
            var guid=id??Guid.NewGuid();
            await registry.SaveAsync(guid,input,serverId,key,id is null,ct);connections.Forget(guid);
            return Results.Ok(new{id=guid});
        }
        app.MapPost("/monitor/api/environments",(EnvironmentInput input,CancellationToken ct)=>Save(null,input,ct));
        app.MapPut("/monitor/api/environments/{id:guid}",(Guid id,EnvironmentInput input,CancellationToken ct)=>Save(id,input,ct));
        app.MapDelete("/monitor/api/environments/{id:guid}",async(Guid id,CancellationToken ct)=>
        {await registry.DeleteAsync(id,ct);connections.Forget(id);return Results.NoContent();});
        app.MapGet("/monitor/api/environments/{id:guid}/history",async(Guid id,int? hours,CancellationToken ct)=>
            Results.Json(await connections.ReadAsync(id,"history?hours="+Math.Clamp(hours??1,1,720),ct)));
        app.MapGet("/monitor/api/environments/{id:guid}/jobs",async(Guid id,string? provider,string? state,string? cursor,CancellationToken ct)=>
            Results.Json(await connections.ReadAsync(id,"jobs?provider="+Uri.EscapeDataString(provider??"")+"&state="+Uri.EscapeDataString(state??"")+"&cursor="+Uri.EscapeDataString(cursor??""),ct)));
        app.MapGet("/monitor/api/environments/{id:guid}/jobs/{job:guid}",async(Guid id,Guid job,CancellationToken ct)=>
            Results.Json(await connections.ReadAsync(id,"jobs/"+job,ct)));
    }
}
