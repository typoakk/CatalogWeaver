using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CatalogWeaver.MonitorClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MediaImporter.Tests;

public sealed class MonitorClientTests
{
    private const string ReadKey="synthetic-read-only-monitor-key-for-tests";

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:password@server/")]
    [InlineData("http://server/?key=secret")]
    [InlineData("http://server/path")]
    [InlineData("http://server/#fragment")]
    public void Connections_require_an_explicit_origin(string address)
        =>Assert.Throws<ArgumentException>(()=>ConnectionRegistry.Address(address));

    [Fact]
    public async Task Local_client_starts_empty_without_a_server_login_and_protects_its_local_api()
    {
        await using var fixture=await Fixture.CreateAsync();
        var html=await fixture.Client.GetStringAsync("/monitor/");
        Assert.DoesNotContain("login-key",html);Assert.Contains("Клиент на этом ПК",html);
        fixture.Client.DefaultRequestHeaders.Remove("X-CatalogWeaver-Request");
        Assert.Equal(HttpStatusCode.Forbidden,(await fixture.Client.GetAsync("/monitor/api/environments")).StatusCode);
        fixture.Client.DefaultRequestHeaders.Add("X-CatalogWeaver-Request","monitor");
        Assert.Empty((await fixture.Client.GetFromJsonAsync<JsonElement>("/monitor/api/environments")).EnumerateArray());
        fixture.Client.DefaultRequestHeaders.Add("Origin","http://other-origin.test");
        Assert.Equal(HttpStatusCode.Forbidden,(await fixture.Client.PostAsJsonAsync("/monitor/api/environments",new EnvironmentInput("Dev","http://dev.test",ReadKey))).StatusCode);
        fixture.Client.DefaultRequestHeaders.Remove("Origin");fixture.Client.DefaultRequestHeaders.Host="rebinding.test";
        Assert.Equal(HttpStatusCode.Forbidden,(await fixture.Client.GetAsync("/monitor/")).StatusCode);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task Two_servers_are_requested_by_the_client_with_their_own_keys_and_are_never_connected_to_each_other()
    {
        await using var fixture=await Fixture.CreateAsync();
        var devKey=ReadKey+"-dev";var secondKey=ReadKey+"-second";
        fixture.ExpectedKeys["dev.test"]=devKey;fixture.ExpectedKeys["second.test"]=secondKey;
        var dev=await fixture.AddAsync("Dev","http://dev.test",devKey);
        var second=await fixture.AddAsync("Second","http://second.test",secondKey);
        await fixture.Connections.PollAsync(default);
        await WaitForAsync(()=>fixture.Connections.State(dev)?.Data is not null&&fixture.Connections.State(second)?.Data is not null);
        var rows=await fixture.Client.GetStringAsync("/monitor/api/environments");
        Assert.DoesNotContain(devKey,rows);Assert.DoesNotContain(secondKey,rows);Assert.DoesNotContain("protectedKey",rows,StringComparison.OrdinalIgnoreCase);
        var devHistory=await fixture.Client.GetAsync($"/monitor/api/environments/{dev}/history?hours=1");devHistory.EnsureSuccessStatusCode();
        var secondJobs=await fixture.Client.GetAsync($"/monitor/api/environments/{second}/jobs?provider=steam");secondJobs.EnsureSuccessStatusCode();
        Assert.All(fixture.Requests,request=>
        {
            Assert.Equal(HttpMethod.Get,request.Method);Assert.StartsWith("/api/v1/monitor/",request.Uri.AbsolutePath);
            Assert.Equal("Bearer "+fixture.ExpectedKeys[request.Uri.Host],request.Authorization);
            Assert.DoesNotContain("key=",request.Uri.Query);
        });
        Assert.Contains(fixture.Requests,x=>x.Uri.Host=="dev.test"&&x.Uri.AbsolutePath.EndsWith("/history"));
        Assert.Contains(fixture.Requests,x=>x.Uri.Host=="second.test"&&x.Uri.AbsolutePath.EndsWith("/jobs"));
    }

    [Fact]
    public async Task Local_profiles_survive_restart_without_storing_plain_keys_or_leaking_them_to_the_browser()
    {
        var root=Path.Combine(Path.GetTempPath(),"cw-client-restart-"+Guid.NewGuid().ToString("N"));
        Guid id; Guid serverId;
        await using(var first=await Fixture.CreateAsync(root))
        {
            id=await first.AddAsync("Dev","http://dev.test",ReadKey);
            serverId=first.Registry.Get(id).ServerId;
            var json=await File.ReadAllTextAsync(Path.Combine(root,"environments.json"));
            Assert.DoesNotContain(ReadKey,json);Assert.DoesNotContain("management",json,StringComparison.OrdinalIgnoreCase);
        }
        await using(var second=await Fixture.CreateAsync(root))
        {
            second.Identities["dev.test"]=serverId;
            Assert.Equal(ReadKey,second.Registry.Key(second.Registry.Get(id)));
            var change=await second.Client.PutAsJsonAsync($"/monitor/api/environments/{id}",new EnvironmentInput("Renamed","http://dev.test",null));change.EnsureSuccessStatusCode();
            Assert.Equal(ReadKey,second.Registry.Key(second.Registry.Get(id)));
            Assert.DoesNotContain(ReadKey,await second.Client.GetStringAsync("/monitor/api/environments"));
            var delete=await second.Client.DeleteAsync($"/monitor/api/environments/{id}");delete.EnsureSuccessStatusCode();
            Assert.Empty(second.Registry.List());
        }
        Directory.Delete(root,true);
    }

    [Fact]
    public async Task Duplicate_servers_wrong_keys_and_changed_server_identity_are_rejected()
    {
        await using var fixture=await Fixture.CreateAsync();
        var id=await fixture.AddAsync("Dev","http://dev.test",ReadKey);
        var duplicate=await fixture.Client.PostAsJsonAsync("/monitor/api/environments",new EnvironmentInput("Duplicate","http://dev.test",ReadKey));
        Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
        var wrong=await fixture.Client.PostAsJsonAsync("/monitor/api/environments",new EnvironmentInput("Wrong","http://second.test",new string('x',40)));
        Assert.Equal(HttpStatusCode.BadGateway,wrong.StatusCode);Assert.Contains("monitor_key_rejected",await wrong.Content.ReadAsStringAsync());
        fixture.Identities["dev.test"]=Guid.NewGuid();
        var replaced=await fixture.Client.PutAsJsonAsync($"/monitor/api/environments/{id}",new EnvironmentInput("New server","http://dev.test",null));
        Assert.Equal(HttpStatusCode.Conflict,replaced.StatusCode);Assert.Equal("Dev",fixture.Registry.Get(id).Name);
    }

    [Fact]
    public async Task A_slow_server_does_not_delay_other_servers_or_create_duplicate_polls()
    {
        await using var fixture=await Fixture.CreateAsync();
        var fast=await fixture.AddAsync("Fast","http://fast.test",ReadKey);
        var slow=await fixture.AddAsync("Slow","http://slow.test",ReadKey);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BlockSlow=release;
        try
        {
            await fixture.Connections.PollAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForAsync(()=>fixture.Connections.State(fast)?.Data is not null);
            Assert.Null(fixture.Connections.State(slow));
            await fixture.Connections.PollAsync(default);Assert.Equal(1,fixture.SlowPolls);
        }
        finally{release.TrySetResult(true);}
        await WaitForAsync(()=>fixture.Connections.State(slow)?.Data is not null);
    }

    [Fact]
    public async Task Disconnected_servers_keep_their_last_snapshot_and_recover_independently()
    {
        await using var fixture=await Fixture.CreateAsync();var id=await fixture.AddAsync("Dev","http://dev.test",ReadKey);
        await fixture.Connections.PollAsync(default);await WaitForAsync(()=>fixture.Connections.State(id)?.Data is not null);
        var last=fixture.Connections.State(id)!.Data!["updatedUtc"]!.ToJsonString();
        fixture.Unreachable=true;await Task.Delay(2050);await fixture.Connections.PollAsync(default);
        await WaitForAsync(()=>fixture.Connections.State(id)?.Error is not null);
        Assert.Equal(last,fixture.Connections.State(id)!.Data!["updatedUtc"]!.ToJsonString());
        fixture.Unreachable=false;await Task.Delay(2050);await fixture.Connections.PollAsync(default);
        await WaitForAsync(()=>fixture.Connections.State(id)?.Error is null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for(var i=0;i<200&&!condition();i++)await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed record Sent(HttpMethod Method,Uri Uri,string Authorization);
    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App {get;init;}
        public required HttpClient Client {get;init;}
        public required string Root {get;init;}
        public required bool OwnRoot {get;init;}
        public required SourceHandler Handler {get;init;}
        public ConcurrentQueue<Sent> Requests=>Handler.Requests;
        public Dictionary<string,string> ExpectedKeys=>Handler.Keys;
        public Dictionary<string,Guid> Identities=>Handler.Identities;
        public TaskCompletionSource<bool>? BlockSlow {get=>Handler.BlockSlow;set=>Handler.BlockSlow=value;}
        public int SlowPolls=>Handler.SlowPolls;
        public bool Unreachable {get=>Handler.Unreachable;set=>Handler.Unreachable=value;}
        public ConnectionRegistry Registry=>App.Services.GetRequiredService<ConnectionRegistry>();
        public ServerConnections Connections=>App.Services.GetRequiredService<ServerConnections>();
        public static async Task<Fixture> CreateAsync(string? root=null)
        {
            var own=root is null;root??=Path.Combine(Path.GetTempPath(),"cw-client-"+Guid.NewGuid().ToString("N"));
            var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Configuration["Client:DataDirectory"]=root;
            builder.Services.AddMonitorClient(builder.Configuration);builder.Services.RemoveAll<IHostedService>();
            var handler=new SourceHandler();builder.Services.AddSingleton<IHttpClientFactory>(new TestFactory(handler));
            var app=builder.Build();app.MapMonitorClient();await app.StartAsync();var client=app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-CatalogWeaver-Request","monitor");
            return new(){App=app,Client=client,Root=root,OwnRoot=own,Handler=handler};
        }
        public async Task<Guid> AddAsync(string name,string address,string key)
        {
            var response=await Client.PostAsJsonAsync("/monitor/api/environments",new EnvironmentInput(name,address,key,IntervalSeconds:2));
            response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        public async ValueTask DisposeAsync()
        {Client.Dispose();await App.StopAsync();await App.DisposeAsync();Handler.Dispose();if(OwnRoot)Directory.Delete(Root,true);}
    }
    private sealed class TestFactory(HttpMessageHandler handler):IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,false);}
    private sealed class SourceHandler:HttpMessageHandler
    {
        public ConcurrentQueue<Sent> Requests {get;}=new();
        public Dictionary<string,string> Keys {get;}=new();
        public Dictionary<string,Guid> Identities {get;}=new();
        public TaskCompletionSource<bool>? BlockSlow;
        public int SlowPolls;
        public bool Unreachable;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Enqueue(new(request.Method,request.RequestUri!,request.Headers.Authorization?.ToString()??""));
            if(Unreachable)throw new HttpRequestException("synthetic offline source");
            var host=request.RequestUri!.Host;
            if(host=="slow.test"&&BlockSlow is {} block){Interlocked.Increment(ref SlowPolls);await block.Task.WaitAsync(ct);}
            if(request.Headers.Authorization?.Parameter!=(Keys.GetValueOrDefault(host)??ReadKey))return new(HttpStatusCode.Unauthorized);
            lock(Identities){if(!Identities.ContainsKey(host))Identities[host]=Guid.NewGuid();}
            return new(HttpStatusCode.OK){Content=JsonContent.Create<object>(request.RequestUri.AbsolutePath.EndsWith("snapshot")
                ?new{schemaVersion=1,serverId=Identities[host],updatedUtc=DateTime.UtcNow,adapters=Array.Empty<object>()}
                :request.RequestUri.AbsolutePath.EndsWith("history")?Array.Empty<object>():new{items=Array.Empty<object>(),nextCursor=(string?)null})};
        }
    }
}
