using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

record ChatMessage(string type, string? from, string? to, string? text, long ts);

class ClientConn
{
    public TcpClient Tcp { get; }
    public NetworkStream Stream { get; }
    public string? Username { get; set; }
    public ClientConn(TcpClient tcp) { Tcp = tcp; Stream = tcp.GetStream(); }
}

class Program
{
    static readonly ConcurrentDictionary<string, ClientConn> ClientsByName = new();
    static readonly ConcurrentDictionary<ClientConn, byte> Clients = new();
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static async Task Main(string[] args)
    {
        int port = args.Length > 0 ? int.Parse(args[0]) : 9000;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[SERVER] listening on *:{port}");

        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var conn = new ClientConn(tcp);
            Clients.TryAdd(conn, 0);
            _ = HandleClient(conn);
        }
    }

    static async Task HandleClient(ClientConn conn)
    {
        Console.WriteLine("[SERVER] new connection");
        var reader = new StreamReader(conn.Stream, Encoding.UTF8);

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                ChatMessage? msg;
                try { msg = JsonSerializer.Deserialize<ChatMessage>(line, JsonOpts); }
                catch { continue; }
                if (msg is null) continue;

                switch (msg.type)
                {
                    // >>> IJUN
                    case "typing":
                        if (!EnsureNamed(conn)) break;
                        await Broadcast(new("typing", conn.Username, null, null, NowTs()));
                        break;

                    case "stoptyping":
                        if (!EnsureNamed(conn)) break;
                        await Broadcast(new("stoptyping", conn.Username, null, null, NowTs()));
                        break;
                    // <<< IJUN

                    // >>> REZA
                    // (tidak ada handling typing/stoptyping di versi Reza)
                    // <<< REZA

                    case "join":
                        // >>> IJUN
                        if (string.IsNullOrWhiteSpace(msg.from))
                        { await SendSys(conn, "missing username"); break; }
                        // <<< IJUN

                        // >>> REZA
                        if (string.IsNullOrWhiteSpace(msg.from))
                        { await SendSys(conn, "JOIN missing username"); break; }
                        // <<< REZA

                        if (!ClientsByName.TryAdd(msg.from, conn))
                        {
                            await SendSys(conn, "Username already used");
                            await Close(conn, silent: true);
                            return;
                        }
                        conn.Username = msg.from;
                        Console.WriteLine($"[SERVER] {conn.Username} joined");
                        await Broadcast(Sys($"{conn.Username} joined"));
                        await PushUserList();
                        break;

                    case "msg":
                        if (!EnsureNamed(conn)) break;
                        await Broadcast(new("msg", conn.Username, null, msg.text, NowTs()));
                        break;

                    case "pm":
                        if (!EnsureNamed(conn)) break;
                        if (string.IsNullOrWhiteSpace(msg.to))
                        { await SendSys(conn, "PM requires 'to'"); break; }

                        if (ClientsByName.TryGetValue(msg.to, out var target))
                        {
                            await Send(target, new("pm", conn.Username, msg.to, msg.text, NowTs()));
                            if (!ReferenceEquals(target, conn))
                                await Send(conn, new("pm", conn.Username, msg.to, msg.text, NowTs()));
                        }
                        else { await SendSys(conn, $"User '{msg.to}' not found"); }
                        break;

                    case "leave":
                        await Close(conn);
                        return;
                }
            }
        }
        catch { /* drop */ }
        finally { await Close(conn); }
    }

    static bool EnsureNamed(ClientConn c)
    {
        if (c.Username is null) { _ = SendSys(c, "You must JOIN first"); return false; }
        return true;
    }

    static async Task Broadcast(ChatMessage msg)
    {
        var data = Serialize(msg);
        foreach (var c in Clients.Keys)
        {
            try { await c.Stream.WriteAsync(data); } catch { }
        }
    }

    static async Task Send(ClientConn conn, ChatMessage msg)
        => await conn.Stream.WriteAsync(Serialize(msg));

    static async Task SendSys(ClientConn conn, string text)
        => await Send(conn, Sys(text, conn.Username));

    static ChatMessage Sys(string text, string? to = null)
        => new("sys", "server", to, text, NowTs());

    static async Task PushUserList()
    {
        var csv = string.Join(",", ClientsByName.Keys.OrderBy(n => n));
        await Broadcast(new("userlist", "server", null, csv, NowTs()));
    }

    static async Task Close(ClientConn conn, bool silent = false)
    {
        if (Clients.TryRemove(conn, out _))
        {
            string? name = conn.Username;
            if (name is not null) ClientsByName.TryRemove(name, out _);
            try { conn.Stream.Close(); } catch { }
            try { conn.Tcp.Close(); } catch { }
            if (!silent && name is not null)
            {
                Console.WriteLine($"[SERVER] {name} left");
                await Broadcast(Sys($"{name} left"));
                await PushUserList();
            }
        }
    }

    static long NowTs() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static byte[] Serialize(ChatMessage msg)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts) + "\n");
}
