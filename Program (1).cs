using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// Bağlı tüm clientlar
ConcurrentDictionary<string, WebSocket> clients = new();

app.Map("/chat", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
    string id = Guid.NewGuid().ToString();
    clients[id] = ws;

    Console.WriteLine($"[+] Bağlandı: {id} | Toplam: {clients.Count}");

    byte[] buffer = new byte[4096];

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"[MSG] {message}");

            // Tüm diğer clientlara ilet
            byte[] data = Encoding.UTF8.GetBytes(message);
            foreach (var (clientId, clientWs) in clients)
            {
                if (clientId != id && clientWs.State == WebSocketState.Open)
                {
                    await clientWs.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
    }
    catch { }
    finally
    {
        clients.TryRemove(id, out _);
        Console.WriteLine($"[-] Ayrıldı: {id} | Toplam: {clients.Count}");
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bağlantı kesildi", CancellationToken.None);
    }
});

// Railway port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");
