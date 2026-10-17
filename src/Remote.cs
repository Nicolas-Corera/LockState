// Canal remoto: conexión WebSocket SALIENTE de la PC al servidor intermedio (sin abrir puertos).
// Principio: NUNCA es requisito para bloquear. Si falla, reintenta con backoff y el scheduler local sigue igual.
using System.Net.WebSockets;
namespace BloqueadorHorario;

class Remote : IRemoteChannel
{
    readonly Engine engine;
    readonly string wsUrl, pcId, pcToken;
    readonly Func<string> phoneKey;
    CancellationTokenSource? cts;
    ClientWebSocket? ws;
    readonly SemaphoreSlim sendLock = new(1, 1);
    TaskCompletionSource<string?>? codeWaiter;

    public Remote(Engine engine, string baseUrl, string pcId, string pcToken, Func<string> phoneKey)
    {
        this.engine = engine; this.pcId = pcId; this.pcToken = pcToken; this.phoneKey = phoneKey;
        var b = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/');
        wsUrl = $"{b}/pc?id={Uri.EscapeDataString(pcId)}";
    }

    public void Start() { cts = new CancellationTokenSource(); _ = Task.Run(() => Loop(cts.Token)); }
    public void Stop() { try { cts?.Cancel(); } catch { } try { ws?.Abort(); } catch { } }

    // Punto de extensión IRemoteChannel (del pedido original).
    Task IRemoteChannel.StartAsync(ICommands c, CancellationToken ct) { Start(); return Task.CompletedTask; }
    Task IRemoteChannel.StopAsync() { Stop(); return Task.CompletedTask; }

    async Task Loop(CancellationToken ct)
    {
        int backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), ct);
                await SendObj(new { t = "hello", token = pcToken, phoneKey = phoneKey() }, ct);
                await SendObj(new { t = "status", data = engine.RemoteStatus() }, ct);
                backoff = 2;
                Log.W("Remoto: conectado");
                _ = Task.Run(() => StatusPump(ct));
                await Receive(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.W("Remoto: conexión caída: " + e.Message); }
            try { ws?.Dispose(); } catch { }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff * 1000, ct); } catch { break; }
            backoff = Math.Min(backoff * 2, 60);
        }
    }

    async Task StatusPump(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws?.State == WebSocketState.Open)
            {
                await Task.Delay(10000, ct);
                if (ws?.State == WebSocketState.Open) await SendObj(new { t = "status", data = engine.RemoteStatus() }, ct);
            }
        }
        catch { }
    }

    async Task Receive(CancellationToken ct)
    {
        var buf = new byte[16384];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && ws!.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close) { try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { } return; }
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            Handle(sb.ToString(), ct);
        }
    }

    void Handle(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            if (t == "code") { codeWaiter?.TrySetResult(root.TryGetProperty("code", out var c) ? c.GetString() : null); return; }
            if (t == "queued") { _ = Task.Run(() => ApplyQueued(root.GetProperty("items"), ct)); return; }
            if (t == "cmd")
            {
                var id = root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                var cmd = root.GetProperty("cmd").GetString();
                _ = Task.Run(() => Exec(id, cmd, json, ct));
            }
        }
        catch (Exception e) { Log.W("Remoto: mensaje inválido: " + e.Message); }
    }

    async Task Exec(string? id, string? cmd, string json, CancellationToken ct)
    {
        bool ok = true; string msg = "OK";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (cmd)
            {
                case "status": break;
                case "extra":
                    int min = root.TryGetProperty("min", out var m) ? m.GetInt32() : 0;
                    min = Math.Clamp(min, 0, engine.RemoteExtraCap);
                    engine.AgregarTiempoExtra(min);
                    msg = $"Tiempo extra: {min} min"; Log.W("Remoto: " + msg);
                    break;
                case "apagar":
                    Log.W("Remoto: apagar"); engine.ApagarAhora(); msg = "Apagando";
                    break;
                case "gracia":
                    engine.RemoteSetGrace(root.TryGetProperty("seg", out var sg) ? sg.GetInt32() : 30);
                    msg = "Tiempo de gracia actualizado";
                    break;
                case "horario":
                    var dias = JsonSerializer.Deserialize<DayCfg[]>(root.GetProperty("dias").GetRawText())!;
                    engine.RemoteSetDays(dias); msg = "Horario actualizado";
                    break;
                default: ok = false; msg = "Comando desconocido"; break;
            }
        }
        catch (Exception e) { ok = false; msg = e.Message; }
        try { await SendObj(new { t = "res", id, ok, msg, data = engine.RemoteStatus() }, ct); } catch { }
    }

    /// <summary>Aplica los comandos que quedaron en la cola mientras la PC estuvo apagada (los que llegaron por "queued").</summary>
    async Task ApplyQueued(JsonElement items, CancellationToken ct)
    {
        var aplicados = new List<string>();
        foreach (var it in items.EnumerateArray())
        {
            var cmd = it.GetProperty("cmd").GetString();
            long ts = it.TryGetProperty("ts", out var tsp) && tsp.ValueKind == JsonValueKind.Number ? tsp.GetInt64() : 0;
            try
            {
                switch (cmd)
                {
                    case "extra":
                        // El extra vale solo el día en que se pidió: si ya es otro día, se descarta (pero igual se confirma).
                        var pedido = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().Date;
                        if (ts != 0 && pedido == DateTime.Now.Date)
                        {
                            int min = it.TryGetProperty("min", out var m) ? m.GetInt32() : 0;
                            engine.AgregarTiempoExtra(Math.Clamp(min, 0, engine.RemoteExtraCap));
                            Log.W("Remoto (cola): tiempo extra aplicado");
                        }
                        else Log.W("Remoto (cola): extra descartado (de otro día)");
                        break;
                    case "gracia":
                        if (engine.RemoteLocalNewerThan(ts)) { Log.W("Remoto (cola): gracia descartada (cambio local más nuevo)"); break; }
                        engine.RemoteSetGrace(it.TryGetProperty("seg", out var sg) ? sg.GetInt32() : 30);
                        Log.W("Remoto (cola): gracia aplicada");
                        break;
                    case "horario":
                        if (engine.RemoteLocalNewerThan(ts)) { Log.W("Remoto (cola): horario descartado (cambio local más nuevo)"); break; }
                        var dias = JsonSerializer.Deserialize<DayCfg[]>(it.GetProperty("dias").GetRawText())!;
                        engine.RemoteSetDays(dias);
                        Log.W("Remoto (cola): horario aplicado");
                        break;
                }
                if (cmd != null) aplicados.Add(cmd);   // confirmado (aplicado o descartado a propósito) -> el servidor lo borra
            }
            catch (Exception e) { Log.W("Remoto (cola): error en " + cmd + ": " + e.Message); }
        }
        if (aplicados.Count > 0)
            try { await SendObj(new { t = "qack", cmds = aplicados }, ct); } catch { }
    }

    /// <summary>Pide al servidor un código de vinculación de un solo uso (bloqueante, con timeout).</summary>
    public string? PedirCodigo()
    {
        try
        {
            // Esperar a que el WebSocket termine de conectar (recién activado puede tardar un par de segundos).
            for (int i = 0; i < 50 && ws?.State != WebSocketState.Open; i++) Thread.Sleep(100);
            if (ws?.State != WebSocketState.Open) return null;
            codeWaiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = SendObj(new { t = "makecode" }, CancellationToken.None);
            return codeWaiter.Task.Wait(8000) ? codeWaiter.Task.Result : null;
        }
        catch { return null; }
    }

    /// <summary>Empuja el estado ahora mismo (al cambiar horario, extra, bloqueo, etc.).</summary>
    public void PushNow() { try { _ = SendObj(new { t = "status", data = engine.RemoteStatus() }, CancellationToken.None); } catch { } }

    async Task SendObj(object o, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(o);
        await sendLock.WaitAsync(ct);
        try { if (ws?.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { sendLock.Release(); }
    }
}
