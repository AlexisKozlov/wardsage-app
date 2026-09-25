// RealmForge.Bridge - local IPC service between the local web dashboard and the system worker.
//
//   GET  /api/state            current data (JSON) + queue info
//   POST /api/actions/apply    [{ "id", "type", "params" }, ...] -> validated, queued -> 202 + /api/jobs/{id}
//   GET  /api/jobs/{id}        status of an accepted batch and of each of its commands
//
// Listens on loopback only (localhost:5055). Browser access: CORS for http(s)://localhost:* and Origin "null"
// (a dashboard opened from a file), plus the X-RealmForge-Token header on every /api request (Security/*).
// Ctrl+C / SIGTERM / service stop: new actions are refused at once, the running command gets a canceled token,
// everything still queued is marked Canceled (Queue/ActionWorker.cs).
using System.Text.Json;
using System.Text.Json.Serialization;
using RealmForge.Bridge;
using RealmForge.Bridge.Actions;
using RealmForge.Bridge.Queue;
using RealmForge.Bridge.Security;
using RealmForge.Bridge.State;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(BridgeOptions.Section).Get<BridgeOptions>() ?? new BridgeOptions();
options.Validate();
builder.Services.AddSingleton(options);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(options.Port);   // 127.0.0.1 and [::1] only, never the network
    kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
    kestrel.AddServerHeader = false;
});
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));   // "queued", "succeeded"
builder.Services.AddProblemDetails();

builder.Services.AddSingleton<AccessToken>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<ActionQueue>();
builder.Services.AddSingleton<ActionBatchParser>();
builder.Services.AddSingleton<ActionHandlerRegistry>();
builder.Services.AddSingleton<IActionHandler, SetStateHandler>();
builder.Services.AddSingleton<IActionHandler, RemoveStateHandler>();
builder.Services.AddHostedService<ActionWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<LocalAccessMiddleware>();
app.MapBridgeApi();

var queue = app.Services.GetRequiredService<ActionQueue>();
var token = app.Services.GetRequiredService<AccessToken>();
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "RealmForge bridge on http://localhost:{Port}; token {Mode}: {File}",
    options.Port, options.RequireToken ? "required" : "NOT required", token.FilePath));
// Refuse new work as soon as shutdown begins (before the worker is stopped).
app.Lifetime.ApplicationStopping.Register(queue.Complete);

await app.RunAsync();
