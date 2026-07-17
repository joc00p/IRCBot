using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using IRCBot.Shared;

namespace IRCBot.ControlApp;

// Thin client that reaches out to the bots' TLS control endpoint and speaks
// the line-delimited JSON protocol over the encrypted channel.
public sealed class BotControlClient : IDisposable
{
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _tcp?.Connected ?? false;

    public async Task ConnectAsync(string host, int port, string? password)
    {
        Dispose();
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);

        // The endpoint uses a self-signed cert; accept it (loopback, local test).
        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });

        _reader = new StreamReader(_ssl, new UTF8Encoding(false));
        _writer = new StreamWriter(_ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        if (!string.IsNullOrEmpty(password))
        {
            var resp = await SendAsync(new BotRequest { Cmd = BotCommands.Auth, Args = { ["pass"] = password } });
            if (!resp.Ok) throw new InvalidOperationException(resp.Error ?? "Authentication failed");
        }
    }

    public async Task<BotResponse> SendAsync(BotRequest req)
    {
        if (_writer == null || _reader == null) throw new InvalidOperationException("Not connected");
        await _lock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(ControlJson.Serialize(req));
            var line = await _reader.ReadLineAsync();
            if (line == null) throw new IOException("Connection closed by host");
            return ControlJson.Deserialize<BotResponse>(line) ?? new BotResponse { Ok = false, Error = "Empty response" };
        }
        finally { _lock.Release(); }
    }

    public Task<BotResponse> SimpleAsync(string cmd) => SendAsync(new BotRequest { Cmd = cmd });

    public Task<BotResponse> ActionAsync(string cmd, params (string, string)[] args)
    {
        var req = new BotRequest { Cmd = cmd };
        foreach (var (k, v) in args) req.Args[k] = v;
        return SendAsync(req);
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _tcp = null; _ssl = null; _reader = null; _writer = null;
    }
}
