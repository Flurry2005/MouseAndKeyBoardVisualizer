using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MouseSwipeVisualizer.Spotify;

/// <summary>
/// Minimal one-shot HTTP listener on 127.0.0.1 for the OAuth redirect. A raw loopback socket instead of
/// HttpListener: http.sys needs admin rights (URL ACL) for 127.0.0.1 prefixes, and Spotify does not accept
/// "localhost" redirect URIs. Only the loopback interface is bound, so nothing on the network can reach it.
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;

    private LoopbackCallback(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
    }

    /// <summary>Starts listening now (before the browser is opened). Throws <see cref="SocketException"/> if the port is taken.</summary>
    public static LoopbackCallback Start(int port) => new(port);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Waits for GET <paramref name="path"/>?… and returns its query parameters. Other requests get 404.</summary>
    public async Task<Dictionary<string, string>> WaitAsync(string path, TimeSpan timeout, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        while (true)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = client.GetStream();
            string? target = await ReadRequestTargetAsync(stream, cts.Token);
            if (target == null)
            {
                continue;
            }

            int q = target.IndexOf('?');
            string requestPath = q >= 0 ? target[..q] : target;
            if (!string.Equals(requestPath, path, StringComparison.Ordinal))
            {
                await RespondAsync(stream, "404 Not Found", "Not found.", cts.Token);
                continue;
            }

            Dictionary<string, string> query = ParseQuery(q >= 0 ? target[(q + 1)..] : string.Empty);
            bool ok = query.ContainsKey("code");
            await RespondAsync(stream, "200 OK",
                ok ? "Mouse Swipe Visualizer is connected to Spotify. You can close this tab."
                   : "Spotify did not grant access. You can close this tab and try again from Settings.",
                cts.Token);
            return query;
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxRequestBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancel);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string text = Encoding.ASCII.GetString(buffer, 0, total);
        int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        string[] parts = (lineEnd >= 0 ? text[..lineEnd] : text).Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken cancel)
    {
        string body = "<!doctype html><meta charset=\"utf-8\"><title>Mouse Swipe Visualizer</title>" +
                      "<body style=\"font-family:Segoe UI,sans-serif;margin:40px\"><p>" + WebUtility.HtmlEncode(message) + "</p></body>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\n" +
                      "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancel);
        await stream.WriteAsync(bodyBytes, cancel);
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
            string value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    public void Dispose() => _listener.Stop();
}
