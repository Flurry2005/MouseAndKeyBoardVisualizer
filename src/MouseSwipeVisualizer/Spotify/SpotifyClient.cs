using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MouseSwipeVisualizer.Spotify;

/// <summary>Access token from the Spotify accounts service.</summary>
public sealed record SpotifyToken(string AccessToken, DateTime ExpiresUtc, string? RefreshToken);

/// <summary>What is playing, as far as the cover art is concerned.</summary>
public sealed record NowPlaying(string Title, string Artist, string? ImageUrl, bool IsPlaying);

/// <summary>Result of one "currently playing" request.</summary>
public readonly record struct PlaybackResponse(HttpStatusCode Status, NowPlaying? Playing, TimeSpan? RetryAfter);

/// <summary>The accounts service refused a token request (bad credentials, revoked or expired grant).</summary>
public sealed class SpotifyAuthException(string message) : Exception(message);

/// <summary>Spotify accounts service / Web API calls (authorization code flow; scope: read what is playing).</summary>
public static class SpotifyClient
{
    public const string Scopes = "user-read-currently-playing user-read-playback-state";
    private const string AccountsUrl = "https://accounts.spotify.com";
    private const int MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        $"{AccountsUrl}/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
        $"&scope={Uri.EscapeDataString(Scopes)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

    public static Task<SpotifyToken> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken cancel) =>
        RequestTokenAsync(clientId, clientSecret, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, cancel);

    public static Task<SpotifyToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancel) =>
        RequestTokenAsync(clientId, clientSecret, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancel);

    private static async Task<SpotifyToken> RequestTokenAsync(string clientId, string clientSecret, Dictionary<string, string> form, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccountsUrl + "/api/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret)));
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        string body = await response.Content.ReadAsStringAsync(cancel);
        using JsonDocument json = ParseOrEmpty(body);
        JsonElement root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out JsonElement access))
        {
            // Spotify's error fields never contain the secret; safe to show.
            string error = Str(root, "error_description") ?? Str(root, "error") ?? ((int)response.StatusCode).ToString();
            throw new SpotifyAuthException(error);
        }

        int expires = root.TryGetProperty("expires_in", out JsonElement e) && e.TryGetInt32(out int s) ? s : 3600;
        return new SpotifyToken(access.GetString() ?? string.Empty, DateTime.UtcNow.AddSeconds(expires), Str(root, "refresh_token"));
    }

    public static async Task<PlaybackResponse> GetCurrentlyPlayingAsync(string accessToken, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing?additional_types=track,episode");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PlaybackResponse(response.StatusCode, null, retryAfter);
        }

        string body = await response.Content.ReadAsStringAsync(cancel);
        return new PlaybackResponse(response.StatusCode, ParseCurrentlyPlaying(body), retryAfter);
    }

    /// <summary>Parses /me/player/currently-playing; null = nothing (ad, local file without art, empty item).</summary>
    public static NowPlaying? ParseCurrentlyPlaying(string body)
    {
        using JsonDocument json = ParseOrEmpty(body);
        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool playing = root.TryGetProperty("is_playing", out JsonElement p) && p.ValueKind == JsonValueKind.True;
        string title = Str(item, "name") ?? string.Empty;
        string artist = string.Empty;
        string? image = null;
        if (Str(item, "type") == "episode")
        {
            if (item.TryGetProperty("show", out JsonElement show))
            {
                artist = Str(show, "name") ?? string.Empty;
                image = LargestImage(show);
            }

            image = LargestImage(item) ?? image;
        }
        else
        {
            if (item.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array)
            {
                artist = string.Join(", ", artists.EnumerateArray().Select(a => Str(a, "name")).Where(n => !string.IsNullOrEmpty(n)));
            }

            if (item.TryGetProperty("album", out JsonElement album))
            {
                image = LargestImage(album);
            }
        }

        return new NowPlaying(title, artist, image, playing);
    }

    private static string? LargestImage(JsonElement owner)
    {
        if (!owner.TryGetProperty("images", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        int bestWidth = -1;
        foreach (JsonElement image in images.EnumerateArray())
        {
            int width = image.TryGetProperty("width", out JsonElement w) && w.TryGetInt32(out int v) ? v : 0;
            string? url = Str(image, "url");
            if (url != null && width > bestWidth)
            {
                best = url;
                bestWidth = width;
            }
        }

        return best;
    }

    /// <summary>Cover art is only fetched over HTTPS from Spotify's image CDNs.</summary>
    public static bool IsAllowedImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        (uri.Host == "i.scdn.co" || uri.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase));

    public static async Task<byte[]> DownloadImageAsync(string url, CancellationToken cancel)
    {
        if (!IsAllowedImageUrl(url))
        {
            throw new InvalidOperationException("Cover URL is not a Spotify image URL.");
        }

        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes)
        {
            throw new InvalidOperationException("Cover image is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new System.IO.MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (memory.Length + read > MaxImageBytes)
            {
                throw new InvalidOperationException("Cover image is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static JsonDocument ParseOrEmpty(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
