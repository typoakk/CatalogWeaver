using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatalogWeaver.MonitorClient;

public sealed record ServerState(JsonObject? Data,DateTime CheckedUtc,DateTime? SuccessUtc,string? Error);

/// <summary>Runs on the user's PC. Each server is polled independently using its own read key.</summary>
public sealed class ServerConnections(ConnectionRegistry registry,IHttpClientFactory clients,SshTransport sshTransport) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid,ServerState> states = new();
    private readonly ConcurrentDictionary<Guid,Poll> polls = new();
    private readonly ConcurrentDictionary<Guid,long> revisions = new();
    public ServerState? State(Guid id) => states.GetValueOrDefault(id);
    public void Forget(Guid id)
    {
        revisions.AddOrUpdate(id,1,(_,version)=>version+1); states.TryRemove(id,out _);
        if(polls.TryRemove(id,out var poll)) poll.Cancellation.Cancel();
    }
    public static Guid ValidateSnapshot(JsonNode data)
    {
        try
        {
            if(data["schemaVersion"]?.GetValue<int>()==1 && data["adapters"] is JsonArray
                && Guid.TryParse(data["serverId"]?.GetValue<string>(),out var id)
                && DateTimeOffset.TryParse(data["updatedUtc"]?.GetValue<string>(),out _)) return id;
        }
        catch(InvalidOperationException) { }
        throw new ClientConnectionException("monitor_incompatible_schema");
    }
    public async Task<JsonNode> FetchAsync(string? address,SshTarget? ssh,string key,string route,int timeoutSeconds,CancellationToken ct)
    {
        var path = "api/v1/monitor/" + route;
        using var request = ssh is null
            ? new HttpRequestMessage(HttpMethod.Get,new Uri(ConnectionRegistry.Address(address),path))
            : SshTransport.Request(ssh, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds,1,60)));
        try
        {
            using var response = await (ssh is null ? clients.CreateClient("monitoring-sources") : sshTransport.Client)
                .SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode) throw new ClientConnectionException(response.StatusCode==System.Net.HttpStatusCode.Unauthorized
                ? "monitor_key_rejected" : "monitor_http_"+(int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();var buffer=new byte[16384];
            while(true)
            {
                var count=await stream.ReadAsync(buffer,timeout.Token);if(count==0)break;
                if(output.Length+count>8*1024*1024)throw new ClientConnectionException("monitor_response_too_large");
                output.Write(buffer,0,count);
            }
            return JsonNode.Parse(output.ToArray(),documentOptions:new JsonDocumentOptions{MaxDepth=48})
                ?? throw new ClientConnectionException("monitor_invalid_response");
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new ClientConnectionException("monitor_timeout");}
        catch(HttpRequestException error) when(error.InnerException is ClientConnectionException sshError){throw sshError;}
        catch(HttpRequestException){throw new ClientConnectionException(ssh is null ? "monitor_unreachable" : "monitor_ssh_failed");}
        catch(IOException){throw new ClientConnectionException(ssh is null ? "monitor_unreachable" : "monitor_ssh_failed");}
        catch(JsonException){throw new ClientConnectionException("monitor_invalid_response");}
    }
    public Task PollAsync(CancellationToken ct)
    {
        var rows=registry.List();
        foreach(var removed in states.Keys.Except(rows.Where(x=>x.Enabled).Select(x=>x.Id)))Forget(removed);
        foreach(var connection in rows.Where(x=>x.Enabled))
        {
            if(State(connection.Id) is {} previous && DateTime.UtcNow-previous.CheckedUtc<TimeSpan.FromSeconds(connection.IntervalSeconds))continue;
            var poll=new Poll(CancellationTokenSource.CreateLinkedTokenSource(ct));
            if(!polls.TryAdd(connection.Id,poll)){poll.Cancellation.Dispose();continue;}
            poll.Task=PollOneAsync(connection,revisions.GetValueOrDefault(connection.Id),poll);
        }
        return Task.CompletedTask;
    }
    private async Task PollOneAsync(EnvironmentConnection connection,long revision,Poll poll)
    {
        var previous=State(connection.Id);var ct=poll.Cancellation.Token;
        try
        {
            var data=await FetchAsync(connection.Address,connection.Ssh,registry.Key(connection),"snapshot",connection.TimeoutSeconds,ct);
            if(ValidateSnapshot(data)!=connection.ServerId)throw new ClientConnectionException("monitor_server_identity_changed");
            if(!ct.IsCancellationRequested && revisions.GetValueOrDefault(connection.Id)==revision)
                states[connection.Id]=new(data.AsObject(),DateTime.UtcNow,DateTime.UtcNow,null);
        }
        catch(Exception error)
        {
            if(!ct.IsCancellationRequested && revisions.GetValueOrDefault(connection.Id)==revision)
                states[connection.Id]=new(previous?.Data,DateTime.UtcNow,previous?.SuccessUtc,
                    error is ClientConnectionException known?known.Code:"monitor_local_credentials_unavailable");
        }
        finally
        {
            ((ICollection<KeyValuePair<Guid,Poll>>)polls).Remove(new(connection.Id,poll));
            poll.Cancellation.Dispose();
        }
    }
    public async Task<JsonNode> ReadAsync(Guid id,string route,CancellationToken ct)
    {
        var connection=registry.Get(id);
        if(!connection.Enabled)throw new ClientConnectionException("environment_disabled");
        var key=registry.Key(connection);
        var snapshot=await FetchAsync(connection.Address,connection.Ssh,key,"snapshot",connection.TimeoutSeconds,ct);
        if(ValidateSnapshot(snapshot)!=connection.ServerId)throw new ClientConnectionException("monitor_server_identity_changed");
        return await FetchAsync(connection.Address,connection.Ssh,key,route,connection.TimeoutSeconds,ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            try{await Task.Delay(1000,stoppingToken);}catch(OperationCanceledException){break;}
        }
        var pending=polls.Values.Select(x=>x.Task).Where(x=>x is not null).ToArray();
        await Task.WhenAll(pending!);
    }
    private sealed class Poll(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation {get;}=cancellation;
        public Task? Task {get;set;}
    }
}
