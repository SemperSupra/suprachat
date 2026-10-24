using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace SupraChat.WebViewProbe;

internal sealed class FixtureServer : IAsyncDisposable
{
    public const string Sentinel = "synthetic-private-sentinel-51";
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _requests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _firstChunkAcks = new();
    public Uri Origin { get; }
    public string Nonce { get; } = Guid.NewGuid().ToString("N");
    public Uri? ForeignRedirectTarget { get; set; }

    public FixtureServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(ServeAsync);
        _app.StartAsync().GetAwaiter().GetResult();
        Origin = new Uri(_app.Urls.Single().TrimEnd('/') + "/");
    }

    public Uri Page(string session, string phase, bool retain = false) => new(Origin, $"{Nonce}/{session}/{phase}/page?private={Sentinel}&retain={(retain ? 1 : 0)}");
    public int Count(string session, string phase, string route) => _requests.GetValueOrDefault($"{session}/{phase}/{route}");

    private async Task ServeAsync(HttpContext context)
    {
        try
        {
            var parts = context.Request.Path.Value!.Trim('/').Split('/');
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) || parts.Length != 4 ||
                parts[0] != Nonce || parts[1] is not ("a" or "b") || parts[2] is not ("observed" or "detached"))
            {
                context.Response.StatusCode = 404;
                return;
            }
            var route = parts[3];
            _requests.AddOrUpdate(string.Join('/', parts.Skip(1)), 1, (_, count) => count + 1);
            context.Response.Headers["Cache-Control"] = "no-store";
            switch (route)
            {
                case "redirect-page":
                    context.Response.ContentType = "text/html";
                    context.Response.Headers["Content-Security-Policy"] =
                        $"default-src 'none'; script-src 'unsafe-inline'; connect-src 'self' {ForeignRedirectTarget?.GetLeftPart(UriPartial.Authority)}";
                    await WriteAsync(context, "<!doctype html><title>redirect-pending</title><script>fetch('redirect').then(async r=>{await r.text();document.title=r.status===201?'redirect-complete':'fixture-failed'}).catch(()=>document.title='fixture-failed')</script>");
                    break;
                case "redirect":
                    if (ForeignRedirectTarget is null) { context.Response.StatusCode = 404; break; }
                    context.Response.Redirect(ForeignRedirectTarget.AbsoluteUri);
                    break;
                case "foreign":
                    context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    context.Response.StatusCode = 201;
                    await WriteAsync(context, "synthetic-foreign-origin");
                    break;
                case "page":
                    context.Response.ContentType = "text/html";
                    context.Response.Headers["Content-Security-Policy"] =
                        $"default-src 'none'; script-src 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:{Origin.Port}; base-uri 'none'; form-action 'none'";
                    await WriteAsync(context, PageHtml);
                    break;
                case "stream":
                case "events":
                    var ack = route == "stream" ? _firstChunkAcks.GetOrAdd(parts[1] + "/" + parts[2], _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)) : null;
                    context.Response.ContentType = "text/event-stream";
                    if (route == "stream")
                        context.Response.ContentLength = Encoding.UTF8.GetByteCount($"data: {Sentinel}-one\n\ndata: {Sentinel}-two\n\n");
                    await WriteAsync(context, $"data: {Sentinel}-one\n\n");
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (ack is not null) await ack.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
                    else await Task.Delay(80, context.RequestAborted);
                    await WriteAsync(context, $"data: {Sentinel}-two\n\n");
                    break;
                case "ack":
                    if (context.Request.Method != "POST" || !_firstChunkAcks.TryGetValue(parts[1] + "/" + parts[2], out var firstChunk))
                        context.Response.StatusCode = 404;
                    else { firstChunk.TrySetResult(); context.Response.StatusCode = 204; }
                    break;
                case "cancel":
                    await WriteAsync(context, "ready");
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(1000, context.RequestAborted);
                    await WriteAsync(context, Sentinel);
                    break;
                case "socket":
                    using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                    {
                        var data = new byte[256];
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(data), context.RequestAborted);
                        await socket.SendAsync(new ArraySegment<byte>(data, 0, result.Count), WebSocketMessageType.Text, true, context.RequestAborted);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture-complete", context.RequestAborted);
                    }
                    break;
                default:
                    context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception) { /* Fixture disconnect/cancellation has no raw error output. */ }
    }

    private Task WriteAsync(HttpContext context, string text) =>
        context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestAborted).AsTask();

    private const string PageHtml = """
        <!doctype html><html><head><title>SupraChat synthetic WebView probe</title></head>
        <body><h1>Synthetic fixture only</h1><p>No ChatGPT sign-in or real traffic.</p><script>
        (async () => {
          try {
            const stream = fetch('stream?private=synthetic-private-sentinel-51', {
              headers: {'X-Synthetic-Private': 'synthetic-private-sentinel-51'}
            }).then(async r => { const reader = r.body.getReader(); let body = '';
              if (new URLSearchParams(location.search).get('retain') === '1') globalThis.__fixtureRetained = {r, reader};
              const decoder = new TextDecoder();
              let acknowledged = false;
              for (;;) { const v = await reader.read(); if (v.done) break; body += decoder.decode(v.value, {stream:true});
                if (!acknowledged) { const ack = await fetch('ack', {method:'POST'}); if (!ack.ok) throw Error('fixture'); acknowledged = true; } }
              body += decoder.decode();
              if (body !== 'data: synthetic-private-sentinel-51-one\n\ndata: synthetic-private-sentinel-51-two\n\n') throw Error('fixture'); });
            const events = new Promise((resolve, reject) => { const s = new EventSource('events');
              let count = 0; s.onmessage = () => { if (++count === 2) { s.close(); resolve(); } };
              s.onerror = () => { s.close(); reject(Error('fixture')); }; });
            const ws = new Promise((resolve, reject) => { const u = new URL('socket', location.href); u.protocol = 'ws:';
              const s = new WebSocket(u); s.onopen = () => s.send('synthetic-private-sentinel-51');
              s.onmessage = e => { if (e.data !== 'synthetic-private-sentinel-51') reject(Error('fixture')); else { s.close(); resolve(); } };
              s.onerror = reject; });
            const cancel = (async () => { const c = new AbortController();
              const r = await fetch('cancel', {signal:c.signal}); const reader = r.body.getReader();
              await reader.read(); c.abort(); try { await reader.read(); } catch {} })();
            await Promise.all([stream, events, ws, cancel]);
            document.title = 'fixture-complete-' + location.pathname.split('/')[3];
          } catch { document.title = 'fixture-failed'; }
        })();</script></body></html>
        """;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
