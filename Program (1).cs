//
using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// ══════════════════════════════════════════
//   DDOS KORUMA AYARLARI
// ══════════════════════════════════════════
const int MAX_CONNECTIONS_PER_IP = 3;       // Bir IP'den max eş zamanlı bağlantı
const int MAX_MESSAGES_PER_SECOND = 5;      // Saniyede max mesaj
const int MAX_MESSAGE_SIZE = 512;           // Max mesaj boyutu (byte)
const int BAN_DURATION_SECONDS = 120;       // Ban süresi
const int CONNECTION_TIMEOUT_SECONDS = 300; // 5 dk sessiz kalırsa at
const int MAX_TOTAL_CONNECTIONS = 100;      // Sunucuya toplam max bağlantı
const int MAX_USERNAME_LENGTH = 20;         // Max kullanıcı adı uzunluğu

// ══════════════════════════════════════════
//   VERİ YAPILARI
// ══════════════════════════════════════════
ConcurrentDictionary<string, WebSocket> clients = new();
ConcurrentDictionary<string, int> connectionCount = new();     // IP → bağlantı sayısı
ConcurrentDictionary<string, int> messageCount = new();        // IP → mesaj sayısı (son saniye)
ConcurrentDictionary<string, DateTime> bannedIPs = new();      // IP → ban bitiş zamanı
ConcurrentDictionary<string, DateTime> lastActivity = new();   // clientId → son aktivite

// Her saniye mesaj sayaçlarını sıfırla
var resetTimer = new System.Timers.Timer(1000);
resetTimer.Elapsed += (s, e) => messageCount.Clear();
resetTimer.Start();

// Her 30 saniyede zombi bağlantıları ve süresi dolmuş banları temizle
var cleanupTimer = new System.Timers.Timer(30000);
cleanupTimer.Elapsed += async (s, e) =>
{
    // Süresi dolmuş banları kaldır
    foreach (var ip in bannedIPs.Keys.ToList())
        if (bannedIPs.TryGetValue(ip, out var expiry) && DateTime.UtcNow >= expiry)
        {
            bannedIPs.TryRemove(ip, out _);
            Console.WriteLine($"[BAN KALDIRILDI] {ip}");
        }

    // Timeout olan bağlantıları kapat
    foreach (var (id, lastAct) in lastActivity.ToList())
        if ((DateTime.UtcNow - lastAct).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
            if (clients.TryGetValue(id, out var ws) && ws.State == WebSocketState.Open)
            {
                Console.WriteLine($"[TIMEOUT] {id} atıldı");
                try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Timeout", CancellationToken.None); } catch { }
            }
};
cleanupTimer.Start();

// ══════════════════════════════════════════
//   YARDIMCI FONKSİYONLAR
// ══════════════════════════════════════════
bool IsBanned(string ip)
{
    if (bannedIPs.TryGetValue(ip, out var expiry))
    {
        if (DateTime.UtcNow < expiry) return true;
        bannedIPs.TryRemove(ip, out _);
    }
    return false;
}

void BanIP(string ip, string reason)
{
    bannedIPs[ip] = DateTime.UtcNow.AddSeconds(BAN_DURATION_SECONDS);
    Console.WriteLine($"[BAN] {ip} → {reason} | Süre: {BAN_DURATION_SECONDS}sn");
}

void OnDisconnect(string ip, string clientId)
{
    connectionCount.AddOrUpdate(ip, 0, (k, v) => Math.Max(0, v - 1));
    clients.TryRemove(clientId, out _);
    lastActivity.TryRemove(clientId, out _);
    Console.WriteLine($"[-] Ayrıldı: {clientId} | {ip} | Toplam: {clients.Count}");
}

string SanitizeMessage(string msg)
{
    // HTML injection önle, uzunluğu sınırla
    msg = msg.Replace("<", "&lt;").Replace(">", "&gt;");
    if (msg.Length > 500) msg = msg.Substring(0, 500);
    return msg.Trim();
}

// ══════════════════════════════════════════
//   WEB ARAYÜZÜ
// ══════════════════════════════════════════
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Agalarla Chat</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', sans-serif; background: #1a1a2e; color: #eee; height: 100vh; display: flex; flex-direction: column; }
        #header { background: #16213e; padding: 15px 20px; display: flex; align-items: center; gap: 15px; border-bottom: 2px solid #0f3460; flex-wrap: wrap; }
        #header h1 { font-size: 20px; color: #e94560; }
        #usernameInput { padding: 8px 12px; border-radius: 20px; border: none; background: #0f3460; color: white; font-size: 14px; width: 150px; }
        #connectBtn { padding: 8px 20px; border-radius: 20px; border: none; background: #e94560; color: white; cursor: pointer; font-size: 14px; transition: 0.2s; }
        #connectBtn:hover { background: #c73652; }
        #connectBtn.connected { background: #2ecc71; }
        #status { font-size: 13px; color: #888; margin-left: auto; }
        #chatBox { flex: 1; overflow-y: auto; padding: 20px; display: flex; flex-direction: column; gap: 8px; }
        .message { max-width: 70%; padding: 10px 15px; border-radius: 18px; font-size: 14px; line-height: 1.4; word-break: break-word; }
        .message.mine { align-self: flex-end; background: #0f3460; color: #fff; border-bottom-right-radius: 4px; }
        .message.other { align-self: flex-start; background: #16213e; color: #eee; border-bottom-left-radius: 4px; }
        .message.system { align-self: center; background: transparent; color: #666; font-size: 12px; font-style: italic; }
        .message .meta { font-size: 11px; color: #888; margin-bottom: 4px; }
        .message.mine .meta { text-align: right; }
        #bottomPanel { background: #16213e; padding: 12px 20px; display: flex; gap: 10px; border-top: 2px solid #0f3460; }
        #messageInput { flex: 1; padding: 12px 18px; border-radius: 25px; border: none; background: #0f3460; color: white; font-size: 14px; outline: none; }
        #messageInput::placeholder { color: #666; }
        #sendBtn { padding: 12px 25px; border-radius: 25px; border: none; background: #e94560; color: white; cursor: pointer; font-size: 14px; }
        #sendBtn:disabled { background: #444; cursor: not-allowed; }
        #chatBox::-webkit-scrollbar { width: 5px; }
        #chatBox::-webkit-scrollbar-thumb { background: #333; border-radius: 5px; }
    </style>
</head>
<body>
    <div id="header">
        <h1>💬 Mat Canavarları</h1>
        <input id="usernameInput" type="text" placeholder="Kullanıcı adın..." value="Kullanıcı" maxlength="20" />
        <button id="connectBtn" onclick="toggleConnect()">Bağlan</button>
        <span id="status">Bağlı değil</span>
    </div>
    <div id="chatBox"></div>
    <div id="bottomPanel">
        <input id="messageInput" type="text" placeholder="Mesaj yaz..." disabled maxlength="500" onkeydown="if(event.key==='Enter') sendMessage()" />
        <button id="sendBtn" onclick="sendMessage()" disabled>Gönder</button>
    </div>
    <script>
        let ws = null, connected = false;
        let lastSendTime = 0, sendCount = 0;

        function toggleConnect() { connected ? ws.close() : connect(); }

        function connect() {
            const user = document.getElementById('usernameInput').value.trim();
            if (!user || user.length < 2) { alert('Kullanıcı adı en az 2 karakter olmalı!'); return; }
            const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${proto}//${location.host}/chat`);
            ws.onopen = () => {
                connected = true;
                document.getElementById('connectBtn').textContent = 'Bağlantıyı Kes';
                document.getElementById('connectBtn').classList.add('connected');
                document.getElementById('status').textContent = '🟢 Bağlı';
                document.getElementById('status').style.color = '#2ecc71';
                document.getElementById('messageInput').disabled = false;
                document.getElementById('sendBtn').disabled = false;
                document.getElementById('messageInput').focus();
                addMessage('system', '', '*** Sunucuya bağlandınız! ***');
            };
            ws.onmessage = (e) => {
                const i = e.data.indexOf(':');
                if (i > 0) addMessage('other', e.data.substring(0, i).trim(), e.data.substring(i+1).trim());
            };
            ws.onclose = (e) => {
                connected = false;
                document.getElementById('connectBtn').textContent = 'Bağlan';
                document.getElementById('connectBtn').classList.remove('connected');
                document.getElementById('status').textContent = '🔴 Bağlı değil';
                document.getElementById('status').style.color = '#888';
                document.getElementById('messageInput').disabled = true;
                document.getElementById('sendBtn').disabled = true;
                const reason = e.reason || 'Bağlantı kesildi';
                addMessage('system', '', `*** ${reason} ***`);
            };
        }

        function sendMessage() {
            const input = document.getElementById('messageInput');
            const user = document.getElementById('usernameInput').value.trim() || 'Kullanıcı';
            const text = input.value.trim();
            if (!text || !ws || ws.readyState !== WebSocket.OPEN) return;

            // Client tarafı rate limit (saniyede max 5)
            const now = Date.now();
            if (now - lastSendTime < 1000) {
                sendCount++;
                if (sendCount > 5) { addMessage('system', '', '*** Çok hızlı mesaj gönderiyorsunuz! ***'); return; }
            } else {
                lastSendTime = now;
                sendCount = 1;
            }

            ws.send(`${user}: ${text}`);
            addMessage('mine', user, text);
            input.value = '';
        }

        function addMessage(type, user, text) {
            const box = document.getElementById('chatBox');
            const div = document.createElement('div');
            div.className = `message ${type}`;
            const time = new Date().toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
            div.innerHTML = type === 'system' ? text : `<div class="meta">${user} · ${time}</div>${text}`;
            box.appendChild(div);
            box.scrollTop = box.scrollHeight;
        }
    </script>
</body>
</html>
""", "text/html"));

// ══════════════════════════════════════════
//   WEBSOCKET ENDPOINT
// ══════════════════════════════════════════
app.Map("/chat", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // 1. BAN KONTROLÜ
    if (IsBanned(ip))
    {
        Console.WriteLine($"[ENGEL] Banlı IP: {ip}");
        context.Response.StatusCode = 403;
        return;
    }

    // 2. TOPLAM BAĞLANTI LİMİTİ
    if (clients.Count >= MAX_TOTAL_CONNECTIONS)
    {
        Console.WriteLine($"[DOLU] Sunucu dolu, reddedildi: {ip}");
        context.Response.StatusCode = 503;
        return;
    }

    // 3. IP BAŞINA BAĞLANTI LİMİTİ
    int currentCount = connectionCount.GetOrAdd(ip, 0);
    if (currentCount >= MAX_CONNECTIONS_PER_IP)
    {
        BanIP(ip, $"Çok fazla bağlantı ({currentCount})");
        context.Response.StatusCode = 429;
        return;
    }
    connectionCount.AddOrUpdate(ip, 1, (k, v) => v + 1);

    WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
    string clientId = Guid.NewGuid().ToString();
    clients[clientId] = ws;
    lastActivity[clientId] = DateTime.UtcNow;

    Console.WriteLine($"[+] Bağlandı: {ip} | ID: {clientId} | Toplam: {clients.Count}");

    byte[] buffer = new byte[MAX_MESSAGE_SIZE * 2]; // Biraz fazla al ama kontrol et

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CONNECTION_TIMEOUT_SECONDS));
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[TIMEOUT] {ip} zaman aşımı");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) break;

            // 4. MESAJ BOYUTU LİMİTİ
            if (result.Count > MAX_MESSAGE_SIZE)
            {
                BanIP(ip, $"Çok büyük mesaj ({result.Count} byte)");
                await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Mesaj çok büyük", CancellationToken.None);
                break;
            }

            // 5. RATE LİMİT (saniyede max mesaj)
            int msgCount = messageCount.AddOrUpdate(ip, 1, (k, v) => v + 1);
            if (msgCount > MAX_MESSAGES_PER_SECOND)
            {
                BanIP(ip, $"Rate limit aşıldı ({msgCount} msg/sn)");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Çok hızlı mesaj gönderiyorsunuz", CancellationToken.None);
                break;
            }

            // 6. AKTİVİTE GÜNCELLE
            lastActivity[clientId] = DateTime.UtcNow;

            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            // 7. MESAJ DOĞRULAMA (boş veya sadece boşluk mu?)
            if (string.IsNullOrWhiteSpace(message)) continue;

            // 8. SANITIZE (XSS önleme)
            message = SanitizeMessage(message);

            Console.WriteLine($"[MSG] {ip}: {message}");

            // 9. DİĞER CLİENTLARA İLET
            byte[] data = Encoding.UTF8.GetBytes(message);
            foreach (var (cId, cWs) in clients.ToList())
            {
                if (cId != clientId && cWs.State == WebSocketState.Open)
                {
                    try
                    {
                        await cWs.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        // Bozuk bağlantıyı temizle
                        clients.TryRemove(cId, out _);
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[HATA] {ip}: {ex.Message}");
    }
    finally
    {
        OnDisconnect(ip, clientId);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
    }
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Console.WriteLine($"[SERVER] Başlatılıyor → Port: {port}");
app.Run($"http://0.0.0.0:{port}");
