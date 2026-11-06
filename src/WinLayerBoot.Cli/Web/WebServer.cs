using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinLayerBoot.Core;
using WinLayerBoot.Core.Api;
using FieldInfo = WinLayerBoot.Core.Api.FieldInfo;

namespace WinLayerBoot.Web;

/// <summary>
/// wlb webui: HttpListener serves the web interface. The pages are static files built by WinLayerBoot.WebUi (embedded resources wwwroot/...),
/// and /api/ holds the endpoints: operation fields, checks, execution (background job + event stream), inheritance, boot entries, settings, preset import and export.
/// </summary>
public static class WebServer
{
    private const string Prefix = "wwwroot/";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8", [".svg"] = "image/svg+xml", [".png"] = "image/png", [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2", [".txt"] = "text/plain; charset=utf-8",
    };

    public static int Run(string[] args)
    {
        var cfg = Config.Load();
        Jobs.Frontend = "Web";
        Jobs.Watch();
        cfg.Watch();
        var port = cfg.WebPort;
        for (var i = 0; i < args.Length; i++)
            if (args[i].EqualsIgnoreCase("--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = p;
        var asm = Assembly.GetExecutingAssembly();
        var files = asm.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n.Replace('\\', '/')[Prefix.Length..], n => n, StringComparer.OrdinalIgnoreCase);
        if (!files.ContainsKey("index.html"))
        {
            ConsoleWrite.Write("Error: the Web UI (wwwroot/index.html) is not embedded in the exe; build WinLayerBoot.WebUi first, then compile\n");
            return 1;
        }

        var listener = Start($"http://+:{port}/", out var error);
        var all = listener is not null;
        listener ??= Start($"http://localhost:{port}/", out error);
        if (listener is null) { ConsoleWrite.Write($"Error: failed to start on port {port}: {error}\n"); return 1; }

        var api = new Api(cfg);
        ConsoleWrite.Write($"WinLayerBoot Web UI started ({files.Count} files), Ctrl+C to stop\n  http://localhost:{port}/\n");
        if (all) foreach (var ip in LocalAddresses()) ConsoleWrite.Write($"  http://{ip}:{port}/\n");
        else ConsoleWrite.Write("  (not an administrator: listening on localhost only; running operations also requires administrator)\n");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (Exception) when (!listener.IsListening) { break; }
            // An exception not caught on a thread pool thread ends the process and takes the running job with it: no error in a request may let that happen
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Serve(ctx, asm, files, api); }
                catch (Exception e) { ConsoleWrite.Write($"  Request error: {e.GetType().Name}: {e.Message}\n"); }
            });
        }
        listener.Close();
        api.Dispose();
        ConsoleWrite.Write("Stopped\n");
        return 0;
    }

    private static HttpListener? Start(string prefix, out string error)
    {
        var l = new HttpListener();
        l.Prefixes.Add(prefix);
        try { l.Start(); error = ""; return l; }
        catch (HttpListenerException e) { error = e.Message; l.Close(); return null; }
    }

    private static void Serve(HttpListenerContext ctx, Assembly asm, Dictionary<string, string> files, Api api)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = Uri.UnescapeDataString(req.Url!.AbsolutePath).TrimStart('/');
        // Not using req.IsWebSocketRequest: it initializes the WebSocket protocol components, which also affects ordinary requests; only look at the upgrade header
        var socket = string.Equals(req.Headers["Upgrade"], "websocket", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            {
                if (socket) { api.HandleWebSocket(ctx, path[4..]).GetAwaiter().GetResult(); return; }
                api.Handle(ctx, path[4..]);
                return;
            }
            if (req.HttpMethod is not ("GET" or "HEAD")) { res.StatusCode = 405; return; }
            if (path == "") path = "index.html";
            if (!files.ContainsKey(path) && Path.GetExtension(path) == "") path = "index.html";
            if (!files.TryGetValue(path, out var name)) { res.StatusCode = 404; return; }
            res.ContentType = Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
            res.Headers["Cache-Control"] = path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? "public, max-age=31536000, immutable" : "no-cache";
            using var s = asm.GetManifestResourceStream(name)!;
            res.ContentLength64 = s.Length;
            if (req.HttpMethod == "GET") s.CopyTo(res.OutputStream);
        }
        catch (Exception e)
        {
            try { res.StatusCode = 500; } catch { }
            var inner = e; while (inner.InnerException is not null) inner = inner.InnerException;
            ConsoleWrite.Write($"  Error {path}: {e.GetType().Name}: {e.Message} ← {inner.GetType().Name}: {inner.Message}\n");
            if (Environment.GetEnvironmentVariable("WLB_DEBUG") is { Length: > 0 }) ConsoleWrite.Write(e.ToString() + "\n");
        }
        finally
        {
            // When the WebSocket ends, req and res are already disposed and must not be touched; job event polling is too frequent, so it is not logged either
            if (!socket && !(path.StartsWith("api/jobs/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/events")))
                ConsoleWrite.Write($"[{DateTime.Now:HH:mm:ss}] {req.RemoteEndPoint?.Address} {req.HttpMethod} {req.Url!.AbsolutePath} {res.StatusCode}\n");
            try { res.Close(); } catch { }
        }
    }

    private static IEnumerable<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());
}

/// <summary>
/// Endpoints. Only one execution runs at a time (background thread, output collected in the job, the page receives it over WebSocket);
/// queries (fields, checks, overview, boot entries) each build their own context per request and do not wait on execution:
/// mount directories and hive names carry the context number, a mounted ISO is shared within the process and unmounted together at exit, and probe results such as base disk info are a process-level cache.
/// </summary>
public sealed class Api : IDisposable
{
    private readonly Config _cfg;

    public Api(Config cfg)
    {
        _cfg = cfg;
        using var ctx = NewCtx();
        try { ctx.Mnt.CleanStale(); } catch { }
    }

    /// <summary>Context for one request: output is discarded, the ISO is not unmounted (left until exit).</summary>
    private Context NewCtx() => new(_cfg, new BufferedOutput(), detachIsos: false) { Quick = true };

    public void Dispose() => IsoPool.DetachAll();


    /// <summary>
    /// GET /api/jobs/{id}/ws (WebSocket): first sends all existing output, then pushes each new item as it is produced; when the job ends it sends one state message and closes.
    /// Each message is the JSON of an Event; the final message has kind = "state", text = done / failed, level = error message.
    /// </summary>
    public async Task HandleWebSocket(HttpListenerContext http, string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["events"]) { await StatusStream(http); return; }
        if (parts is not ["jobs", var id, "ws"]) { http.Response.StatusCode = 404; http.Response.Close(); return; }
        var job = Jobs.Find(id);
        if (job is null) { http.Response.StatusCode = 404; http.Response.Close(); return; }
        WebSocket ws;
        try { ws = (await http.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception e)
        {
            var inner = e; while (inner.InnerException is not null) inner = inner.InnerException;
            ConsoleWrite.Write($"  WebSocket handshake failed: {e.Message} ← {inner.GetType().Name}: {inner.Message}\n");
            try { http.Response.StatusCode = 500; http.Response.Close(); } catch { }
            return;
        }
        if (!job.Local)
        {
            try { await TailStream(ws, job); }
            catch { /* client disconnected */ }
            finally { try { ws.Dispose(); } catch { } }
            return;
        }
        var queue = new System.Collections.Concurrent.BlockingCollection<BufferedOutput.Item>();
        void OnAdded(BufferedOutput.Item it) { try { queue.Add(it); } catch { } }
        try
        {
            // Subscribe first, replay afterwards, so nothing produced in between is missed; deduplicate by sequence number
            job.Out.Added += OnAdded;
            long last = 0;
            foreach (var it in job.Out.Since(0)) { await Send(ws, it); last = it.Seq; }
            while (ws.State == WebSocketState.Open)
            {
                if (queue.TryTake(out var it, 500))
                {
                    if (it.Seq <= last && it.Kind != "progress") continue;
                    await Send(ws, it);
                    last = Math.Max(last, it.Seq);
                    continue;
                }
                if (job.State != "running" && queue.Count == 0)
                {
                    // Wrap-up: replay once more, in case an event arrives after the TryTake timeout but before the state is updated
                    foreach (var it2 in job.Out.Since(last)) { await Send(ws, it2); last = it2.Seq; }
                    var end = new Event { Seq = last + 1, Kind = "state", Text = job.State, Level = job.Error };
                    await ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(end, WlbJson.Default.Event)), WebSocketMessageType.Text, true, CancellationToken.None);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception) { /* client disconnected */ }
        finally
        {
            job.Out.Added -= OnAdded;
            queue.Dispose();
            try { ws.Dispose(); } catch { }
        }

        static Task Send(WebSocket ws, BufferedOutput.Item it) =>
            ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(Event.From(it), WlbJson.Default.Event)), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>A job running in another process: it has no in-memory output, so new lines in its log file are read and pushed, and state is pushed at the end.</summary>
    private static async Task TailStream(WebSocket ws, Job job)
    {
        var tail = new LogTail(job.LogPath);
        long seq = 0;
        Task Send(string kind, string text, string level) =>
            ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new Event { Seq = ++seq, Kind = kind, Text = text, Level = level }, WlbJson.Default.Event)), WebSocketMessageType.Text, true, CancellationToken.None);
        await Send("line", $"(running in {job.Who}, process {job.Pid}; its log follows: {job.LogPath})", "dim");
        while (ws.State == WebSocketState.Open)
        {
            var cur = Jobs.Find(job.Id);
            foreach (var l in tail.ReadNew()) await Send("line", l, "info");
            if (cur is not { Running: true })
            {
                await Send("state", cur?.State ?? "failed", cur?.Error ?? "");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                return;
            }
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// GET /api/events (WebSocket): on connect pushes the current state once, then once each when a job starts and ends (JobInfo, without output; Changes is the cumulative count);
    /// every 20 seconds it pushes the same state as a heartbeat, and a failed push means the page was closed. The running marker in the navigation bar and list page refresh rely on it, no polling.
    /// </summary>
    private static async Task StatusStream(HttpListenerContext http)
    {
        WebSocket ws;
        try { ws = (await http.AcceptWebSocketAsync(null)).WebSocket; }
        catch { try { http.Response.StatusCode = 500; http.Response.Close(); } catch { } return; }
        using var changed = new SemaphoreSlim(0);
        void OnChanged(Job _) { try { changed.Release(); } catch { } }
        Jobs.Changed += OnChanged;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(Status(), WlbJson.Default.StatusInfo)), WebSocketMessageType.Text, true, CancellationToken.None);
                await changed.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception) { /* page closed */ }
        finally
        {
            Jobs.Changed -= OnChanged;
            try { ws.Dispose(); } catch { }
        }
    }

    private static StatusInfo Status() => new() { Hash = BuildInfo.Hash, Changes = Jobs.Changes, Running = Jobs.Running().Select(j => Info(j, long.MaxValue)).ToList() };

    public void Handle(HttpListenerContext http, string route)
    {
        var req = http.Request;
        var res = http.Response;
        res.Headers["Cache-Control"] = "no-store";
        try
        {
            var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var m = req.HttpMethod;
            switch (parts)
            {
                case ["overview"] when m == "GET": Json(res, Overview(), WlbJson.Default.Overview); break;
                case ["status"] when m == "GET": Json(res, Status(), WlbJson.Default.StatusInfo); break;
                case ["ops"] when m == "GET": Json(res, Operations.All.Select(OpInfo).ToList(), WlbJson.Default.ListOpInfo); break;
                case ["ops", var name, "check"] when m == "POST":
                {
                    var op = Operations.Find(name) ?? throw new WlbError($"operation {name} not found");
                    var v = ReadValues(req);
                    using (var ctx = NewCtx())
                    {
                        var (vals, fields, checks) = op.Prepare(ctx, v);
                        Json(res, new CheckResult
                        {
                            Values = vals, Fields = fields.Select(FieldInfo.From).ToList(),
                            Checks = checks.Select(CheckInfo.From).ToList(),
                            ExtraHeaders = [.. checks.Extra?.Headers ?? []], ExtraRows = checks.Extra?.Rows.ConvertAll(r => r.ToList()) ?? [],
                            Passed = checks.Passed, Warns = checks.Warns,
                        }, WlbJson.Default.CheckResult);
                    }
                    break;
                }
                case ["ops", var name, "run"] when m == "POST":
                {
                    var op = Operations.Find(name) ?? throw new WlbError($"operation {name} not found");
                    var v = ReadValues(req);
                    Json(res, Info(Jobs.Start(_cfg, op, v, $"Web ({req.RemoteEndPoint?.Address})"), 0), WlbJson.Default.JobInfo);
                    break;
                }
                case ["jobs", var id] when m == "GET": Json(res, Info(FindJob(id), 0), WlbJson.Default.JobInfo); break;
                case ["jobs", var id, "events"] when m == "GET":
                {
                    long.TryParse(req.QueryString["after"], out var after);
                    Json(res, Info(FindJob(id), after), WlbJson.Default.JobInfo);
                    break;
                }
                case ["entries"] when m == "GET":
                    using (var ctx = NewCtx()) Json(res, ctx.Bcd.Entries().Select(EntryInfo.From).ToList(), WlbJson.Default.ListEntryInfo);
                    break;
                case ["config"] when m == "GET": Json(res, ConfigInfo(), WlbJson.Default.ConfigInfo); break;
                case ["config"] when m == "PUT":
                {
                    _cfg.Update(ReadValues(req));
                    Json(res, ConfigInfo(), WlbJson.Default.ConfigInfo);
                    break;
                }
                case ["preset", "export"] when m == "POST":
                {
                    var p = Preset.FromJson(Body(req));
                    p.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    res.ContentType = "application/json; charset=utf-8";
                    res.Headers["Content-Disposition"] = $"attachment; filename=\"{Preset.FileName(p.Op)}\"";
                    var bytes = Files.Utf8Bom.GetBytes(p.ToJson());
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes);
                    break;
                }
                case ["preset", "disks"] when m == "GET":
                    using (var ctx = NewCtx()) Json(res, ctx.ChildDisks().Select(d => d.Name).ToList(), WlbJson.Default.ListString);
                    break;
                case ["preset", "from-vhd"] when m == "GET":
                    using (var ctx = NewCtx())
                    {
                        var values = Operations.All.OfType<Ops.LayerOp>().First().FromVhd(ctx, ctx.Cfg.VhdPath(req.QueryString["disk"] ?? ""));
                        Json(res, new Dictionary<string, string>(values), WlbJson.Default.DictionaryStringString);
                    }
                    break;
                case ["preset", "import"] when m == "POST":
                {
                    var p = Preset.FromJson(Body(req));
                    Json(res, p, WlbJson.Default.Preset);
                    break;
                }
                default: res.StatusCode = 404; Json(res, new Error { Message = $"No handler for {m} /api/{route}" }, WlbJson.Default.Error); break;
            }
        }
        catch (WlbError e) { res.StatusCode = 400; Json(res, new Error { Message = e.Message }, WlbJson.Default.Error); }
        catch (Exception e) { res.StatusCode = 500; Json(res, new Error { Message = e.Message }, WlbJson.Default.Error); }
    }

    private static string Body(HttpListenerRequest req)
    {
        using var r = new StreamReader(req.InputStream, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static Values ReadValues(HttpListenerRequest req)
    {
        var body = Body(req);
        if (string.IsNullOrWhiteSpace(body)) return new Values();
        var r = JsonSerializer.Deserialize(body, WlbJson.Default.Request);
        return r?.Values is null ? new Values() : new Values(r.Values);
    }

    private static void Json<T>(HttpListenerResponse res, T obj, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
    {
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, ti);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
    }

    private static OpInfo OpInfo(Operation o) => new()
    {
        Name = o.Name, PresetFile = Preset.FileName(o.Name), Title = o.Title, Summary = o.Summary, Steps = [.. o.Steps], Stages = [.. o.Stages], Mutates = o.Mutates, Category = o.Category, Lock = o.Lock,
    };

    private Overview Overview()
    {
        using var ctx = NewCtx();
        var d = OverviewData.Load(ctx);
        return new Overview
        {
            Roots = d.Tree.Roots.ConvertAll(TreeNode.From),
            Orphans = d.Tree.Orphans.ConvertAll(EntryInfo.From),
            OtherEntries = ctx.Bcd.Entries().Where(e => !e.IsWlb).Select(EntryInfo.From).ToList(),
            VhdDir = d.VhdDir, FreeGB = d.FreeGB, Host = Environment.MachineName, OnWorkHost = d.OnWorkHost, IsAdmin = d.IsAdmin, Env = d.Env, Totals = d.Totals,
        };
    }

    private ConfigInfo ConfigInfo() => new()
    {
        Values = Config.Keys.ToDictionary(k => k.Key, k => _cfg.Get(k.Key)),
        Fields = _cfg.Fields().Select(FieldInfo.From).ToList(),
        Isos = _cfg.AllIsos(), Path = _cfg.LoadedFrom,
        Resolved = new Dictionary<string, string>
        {
            ["LofIso"] = _cfg.ResolveLofIso(), ["VirtioIso"] = _cfg.ResolveVirtioIso(),
        },
    };

    private static Job FindJob(string id) => Jobs.Find(id) ?? throw new WlbError($"job {id} not found");

    private static JobInfo Info(Job job, long after) => new()
    {
        Id = job.Id, Op = job.Op.Name, Title = job.Op.Title, State = job.State, Error = job.Error, Started = job.Started, LogPath = job.LogPath, Changes = Jobs.Changes,
        ElapsedMs = (long)((job.EndedAt ?? DateTime.Now) - job.StartedAt).TotalMilliseconds,
        Lock = job.Lock, Who = job.Who, Pid = job.Pid, Local = job.Local,
        Events = job.Out.Since(after).Select(Event.From).ToList(),
    };
}
