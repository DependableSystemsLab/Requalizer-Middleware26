using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using OneOS.Runtime;
using OneOS.WebTerminal;

// The WebTerminal server: serves the desktop GUI (client/) over HTTPS (HTTP/2, or HTTP/1.1 for older clients) and
// bridges the pages' WebSockets (/ws) to the cluster (cluster-api.md). Browsers only speak HTTP/2 over TLS, so the
// main endpoint is HTTPS, with a self-signed certificate unless one is given.

var options = ServerOptions.Parse(args);
if (options == null) return 1;

var certificate = LoadCertificate(options.CertificatePath);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    void Listen(int port, Action<ListenOptions> configure)
    {
        if (options.Bind == "localhost") kestrel.ListenLocalhost(port, configure);
        else kestrel.Listen(IPAddress.Parse(options.Bind), port, configure);
    }
    Listen(options.Port, o => { o.Protocols = HttpProtocols.Http1AndHttp2; o.UseHttps(certificate); });
    if (options.HttpPort is int httpPort) Listen(httpPort, o => o.Protocols = HttpProtocols.Http1);
});
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton(new ClusterEndpoint(options.Cluster));
builder.Services.AddSingleton<ClusterViewManager>();
builder.Services.AddSingleton<ClusterSessionManager>();
builder.Services.AddSingleton<WebSocketBroker>();
builder.Services.AddSingleton<WsHandlers>();

var app = builder.Build();
var sessions = app.Services.GetRequiredService<SessionStore>();
var endpoint = app.Services.GetRequiredService<ClusterEndpoint>();
var clusterSessions = app.Services.GetRequiredService<ClusterSessionManager>();
var broker = app.Services.GetRequiredService<WebSocketBroker>();
var handlers = app.Services.GetRequiredService<WsHandlers>();
broker.Register("UserShell", handlers.UserShellAsync);
broker.Register("ResourceMonitor", handlers.ResourceMonitorAsync);
// When a web session ends (logout or expiry), tear down its cluster session (ends the Registry session + Browser).
sessions.SessionRemoved = token => clusterSessions.RemoveAsync(token);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Everything but the login page needs a session: pages are sent to /login, and API calls and sockets get 401.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/login")) { await next(ctx); return; }

    var session = sessions.Find(ctx);
    if (session == null)
    {
        if (path.StartsWithSegments("/ws")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect("/login?next=" + Uri.EscapeDataString(path + ctx.Request.QueryString));
        return;
    }
    ctx.Items[typeof(WebSession)] = session;
    await next(ctx);
});
WebSession SessionOf(HttpContext ctx) => (WebSession)ctx.Items[typeof(WebSession)]!;

app.MapGet("/login", (string? next) => Results.Content(LoginPage.Render(next: LoginPage.SafeNext(next)), "text/html"));
app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    string username = form["username"].ToString().Trim(), password = form["password"].ToString(), next = LoginPage.SafeNext(form["next"]);
    if (username == "") return Results.Content(LoginPage.Render("Username was not provided", next), "text/html");
    if (password == "") return Results.Content(LoginPage.Render("Password was not provided", next), "text/html");

    // Sign-in establishes the cluster session (a Terminal session recorded in the Registry, plus a Browser session);
    // the user is authenticated once that succeeds.
    var session = sessions.Create(username, password);
    var error = await clusterSessions.SignInAsync(session, ctx.RequestAborted);
    if (error != null)
    {
        sessions.Remove(session);
        return Results.Content(LoginPage.Render(error, next), "text/html");
    }
    SessionStore.SetCookie(ctx, session);
    return Results.Redirect(next);
}).DisableAntiforgery();
app.Map("/logout", (HttpContext ctx) =>
{
    sessions.Remove(SessionOf(ctx));
    SessionStore.ClearCookie(ctx);
    return Results.Redirect("/login");
});
app.MapGet("/me", (HttpContext ctx) => Results.Json(new { username = SessionOf(ctx).Username }));
app.Map("/ws", (HttpContext ctx) => broker.AcceptAsync(ctx, SessionOf(ctx)));

// The Browser session's view of the cluster, established at login (null only if the session just ended).
ClusterView? ViewOf(HttpContext ctx) => clusterSessions.Get(SessionOf(ctx).Token)?.View;
IResult WithView(HttpContext ctx, Func<ClusterView, IResult> read)
{
    var view = ViewOf(ctx);
    return view == null ? Results.StatusCode(StatusCodes.Status401Unauthorized) : read(view);
}

// The cluster views the Resource Monitor renders, read from the per-user Registry mirror (cluster-api.md §5, §6).
app.MapGet("/runtime/agents", (HttpContext ctx) => WithView(ctx, v => Results.Json(v.Agents())));
app.MapGet("/runtime/runtimes", (HttpContext ctx) => WithView(ctx, v => Results.Json(v.Runtimes())));
app.MapGet("/runtime/io", (HttpContext ctx) => WithView(ctx, v => Results.Json(v.IO())));
app.MapGet("/runtime/pipes", (HttpContext ctx) => WithView(ctx, v => Results.Json(v.Pipes())));
app.MapGet("/runtime/sockets", (HttpContext ctx) => WithView(ctx, v => Results.Json(v.Sockets())));

// The cluster file system (cluster-api.md §6, §7).
app.MapGet("/fs/{**path}", async (string? path, HttpContext ctx) =>
{
    var view = ViewOf(ctx);
    return view == null ? Results.StatusCode(StatusCodes.Status401Unauthorized)
        : await FileSystemApi.GetAsync(path ?? "", view, endpoint.Address, SessionOf(ctx), ctx.RequestAborted);
});
app.MapPost("/fs/{**path}", async (string? path, FileSystemApi.WriteBody body, HttpContext ctx) =>
{
    var view = ViewOf(ctx);
    return view == null ? Results.StatusCode(StatusCodes.Status401Unauthorized)
        : await FileSystemApi.PostAsync(path ?? "", body, view, endpoint.Address, SessionOf(ctx), ctx.RequestAborted);
}).DisableAntiforgery();

var clientFiles = new PhysicalFileProvider(options.ClientPath);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = clientFiles });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = clientFiles,
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",   // revalidate (ETag), so edits show up
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var host = options.Bind is "0.0.0.0" or "localhost" ? "localhost" : options.Bind;
    app.Logger.LogInformation("OneOS WebTerminal on https://{Host}:{Port} (HTTP/2){Http}, cluster {Cluster}, serving {Client}",
        host, options.Port, options.HttpPort is int p ? $" and http://{host}:{p} (HTTP/1.1)" : "", options.Cluster, options.ClientPath);
});
await app.RunAsync();
return 0;

// The certificate at `path`, created (self-signed, for "localhost") if it doesn't exist yet.
static X509Certificate2 LoadCertificate(string path)
{
    if (!File.Exists(path))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        CertificateHelper.GenerateSelfSignedCertificate(path, "localhost");
        Console.WriteLine($"Created a self-signed certificate: {path}");
    }
    return X509CertificateLoader.LoadPkcs12FromFile(path, "");
}

sealed record ServerOptions(string Cluster, string Bind, int Port, int? HttpPort, string CertificatePath, string ClientPath)
{
    public static ServerOptions? Parse(string[] args)
    {
        string cluster = "127.0.0.1:5000", bind = "localhost";
        int port = 8443;
        int? httpPort = null;
        string cert = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oneos", "webterminal", "cert.pfx");
        string client = Path.Combine(AppContext.BaseDirectory, "client");

        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--cluster": cluster = Value(); break;
                    case "--bind": bind = Value(); if (bind != "localhost") IPAddress.Parse(bind); break;
                    case "--port": port = int.Parse(Value()); break;
                    case "--http-port": httpPort = int.Parse(Value()); break;
                    case "--cert": cert = Value(); break;
                    case "--client": client = Path.GetFullPath(Value()); break;
                    case "-h" or "--help": PrintUsage(); return null;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                PrintUsage();
                return null;
            }
        }
        if (!Directory.Exists(client)) { Console.Error.WriteLine($"client directory not found: {client}"); return null; }
        return new ServerOptions(cluster, bind, port, httpPort, cert, client);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: OneOS-WebTerminal [options]");
        Console.WriteLine("  --cluster <host:port>   A runtime to connect to (default 127.0.0.1:5000)");
        Console.WriteLine("  --bind <address>        localhost (default), 0.0.0.0, or an IP address");
        Console.WriteLine("  --port <n>              HTTPS port, HTTP/2 and HTTP/1.1 (default 8443)");
        Console.WriteLine("  --http-port <n>         Also serve plain HTTP/1.1 on this port (off by default)");
        Console.WriteLine("  --cert <file.pfx>       TLS certificate (default ~/.oneos/webterminal/cert.pfx, created self-signed if missing)");
        Console.WriteLine("  --client <dir>          The front end to serve (default: the client/ folder next to the executable)");
    }
}
