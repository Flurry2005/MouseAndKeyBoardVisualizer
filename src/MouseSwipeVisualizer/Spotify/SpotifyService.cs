using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer.Spotify;

/// <summary>
/// Uses the cover art of what is playing on Spotify as the frame background.
/// <para>
/// Connect: authorization code flow with the user's own Spotify app (client ID + secret). The browser
/// shows Spotify's consent page, which redirects to a one-shot listener on 127.0.0.1. Scopes: read what
/// is playing, nothing else. The client secret and refresh token are stored DPAPI-encrypted
/// (<see cref="SpotifySecrets"/>), never in settings.json, never logged.
/// </para>
/// <para>
/// Poll: every <see cref="AppSettings.SpotifyPollSeconds"/> the currently playing item is read; when its
/// cover changes, the image is downloaded (Spotify CDNs only) into a small cache and
/// <see cref="CoverChanged"/> is raised with its path (null = nothing playing: normal background).
/// </para>
/// </summary>
public sealed class SpotifyService : IDisposable
{
    public const string CallbackPath = "/callback";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(3);
    private const int KeepCoverFiles = 3;
    private static readonly TimeSpan MinPollGap = TimeSpan.FromSeconds(1);
    private long _lastPoll;

    private readonly string _secretsFile;
    private readonly string _coverDirectory;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private SpotifySecrets _secrets;
    private string _clientId = string.Empty;
    private int _port = AppSettings.DefaultSpotifyPort;
    private double _pollSeconds = AppSettings.DefaultSpotifyPollSeconds;
    private bool _enabled;
    private string? _accessToken;
    private DateTime _accessExpiresUtc;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private string? _coverUrl;
    private string? _coverPath;
    private volatile string _status = "Not connected.";
    private string? _lastLoggedError;
    private bool _disposed;

    public SpotifyService(string dataDirectory)
    {
        _secretsFile = Path.Combine(dataDirectory, "spotify.dat");
        _coverDirectory = Path.Combine(dataDirectory, "spotify-covers");
        _secrets = SpotifySecrets.Load(_secretsFile);
    }

    /// <summary>Raised on a background thread with the new cover file, or null to use the normal background.</summary>
    public event Action<string?>? CoverChanged;

    public static string RedirectUri(int port) => $"http://127.0.0.1:{port}{CallbackPath}";

    public string Status => _enabled ? _status : "Off." + (IsConnected ? " (Spotify account connected.)" : string.Empty);

    public bool HasSavedSecret
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_secrets.ClientSecret);
            }
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_secrets.RefreshToken) && _secrets.ClientId == _clientId;
            }
        }
    }

    /// <summary>Applies settings (UI thread): start/stop polling, new interval takes effect immediately.</summary>
    public void Configure(AppSettings settings)
    {
        bool changed;
        lock (_lock)
        {
            string clientId = settings.SpotifyClientId.Trim();
            changed = clientId != _clientId || settings.SpotifyPollSeconds != _pollSeconds;
            _clientId = clientId;
            _port = settings.SpotifyRedirectPort;
            _pollSeconds = settings.SpotifyPollSeconds;
        }

        bool enable = settings.SpotifyCoverEnabled;
        if (enable && !_enabled)
        {
            _enabled = true;
            _status = IsConnected ? "Connecting…" : "Not connected: enter your Client ID and secret, then Connect.";
            _pollCts = new CancellationTokenSource();
            CancellationToken token = _pollCts.Token;
            _pollTask = Task.Run(() => PollLoopAsync(token));
        }
        else if (!enable && _enabled)
        {
            _enabled = false;
            _pollCts?.Cancel();
            _pollCts = null;
            SetCover(null, null);
        }

        if (changed)
        {
            _wake.Release(); // re-evaluate now with the new interval / client
        }
    }

    /// <summary>
    /// Runs the authorization flow: opens Spotify's consent page in the browser and waits for the redirect.
    /// <paramref name="clientSecret"/> null/empty = use the saved one.
    /// </summary>
    public async Task<string> ConnectAsync(string clientId, string? clientSecret, CancellationToken cancel = default)
    {
        clientId = clientId.Trim();
        if (clientId.Length == 0)
        {
            return "Enter the Client ID of your Spotify app first.";
        }

        string? secret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim();
        lock (_lock)
        {
            secret ??= _secrets.ClientId == clientId || _secrets.ClientId == null ? _secrets.ClientSecret : null;
            _clientId = clientId;
        }

        if (string.IsNullOrEmpty(secret))
        {
            return "Enter the Client secret of your Spotify app.";
        }

        int port;
        lock (_lock)
        {
            port = _port;
        }

        string redirect = RedirectUri(port);
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        LoopbackCallback listener;
        try
        {
            listener = LoopbackCallback.Start(port);
        }
        catch (SocketException)
        {
            return $"Port {port} is already in use. Pick another callback port (and update the redirect URI in your Spotify app).";
        }

        using (listener)
        {
            try
            {
                Process.Start(new ProcessStartInfo(SpotifyClient.AuthorizeUrl(clientId, redirect, state)) { UseShellExecute = true });
                Dictionary<string, string> query = await listener.WaitAsync(CallbackPath, ConnectTimeout, cancel);
                if (query.TryGetValue("error", out string? error))
                {
                    return error == "access_denied" ? "Access was denied in the browser." : "Spotify: " + error;
                }

                if (!query.TryGetValue("state", out string? returned) ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returned), Encoding.ASCII.GetBytes(state)))
                {
                    return "Rejected the response: it did not come from this sign-in (state mismatch).";
                }

                if (!query.TryGetValue("code", out string? code) || code.Length == 0)
                {
                    return "Spotify did not return an authorization code.";
                }

                SpotifyToken token = await SpotifyClient.ExchangeCodeAsync(clientId, secret, code, redirect, cancel);
                lock (_lock)
                {
                    _secrets = new SpotifySecrets { ClientId = clientId, ClientSecret = secret, RefreshToken = token.RefreshToken };
                    _secrets.Save(_secretsFile);
                    _accessToken = token.AccessToken;
                    _accessExpiresUtc = token.ExpiresUtc;
                }

                Logger.Info("Connected to Spotify.");
                _status = "Connected.";
                _wake.Release();
                return _enabled ? "Connected to Spotify." : "Connected to Spotify. Turn on \"Use Spotify cover\" to show it.";
            }
            catch (OperationCanceledException)
            {
                return "Timed out waiting for the browser. Check that the redirect URI in your Spotify app is exactly " + redirect;
            }
            catch (SpotifyAuthException ex)
            {
                return "Spotify refused the sign-in: " + ex.Message + ". Check the Client ID/secret and the redirect URI " + redirect;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception or IOException)
            {
                return "Connecting failed: " + ex.Message;
            }
        }
    }

    /// <summary>Forgets the client secret and tokens (deletes the encrypted file) and the cover cache.</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            _secrets = new SpotifySecrets();
            _accessToken = null;
            SpotifySecrets.Delete(_secretsFile);
        }

        SetCover(null, null);
        _status = "Not connected.";
        Logger.Info("Spotify credentials removed.");
    }

    private async Task PollLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TimeSpan delay;
            lock (_lock)
            {
                delay = TimeSpan.FromSeconds(_pollSeconds);
            }

            try
            {
                // Never faster than once a second, however often settings change.
                TimeSpan sinceLast = Stopwatch.GetElapsedTime(_lastPoll);
                if (_lastPoll != 0 && sinceLast < MinPollGap)
                {
                    await Task.Delay(MinPollGap - sinceLast, cancel);
                }

                _lastPoll = Stopwatch.GetTimestamp();
                delay = await PollOnceAsync(cancel) ?? delay;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                string message = ex is SpotifyAuthException ? "Spotify sign-in expired or was revoked: click Connect again." : "Spotify: " + ex.Message;
                _status = message;
                if (message != _lastLoggedError)
                {
                    Logger.Warn(message); // messages never contain tokens or the secret
                    _lastLoggedError = message;
                }

                delay = TimeSpan.FromSeconds(Math.Max(15, delay.TotalSeconds));
            }

            try
            {
                // Sleep until the next poll; Configure/Connect wake it early.
                while (_wake.CurrentCount > 0)
                {
                    await _wake.WaitAsync(0, cancel);
                }

                await _wake.WaitAsync(delay, cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One poll; returns a delay override (rate limit, retry) or null for the normal interval.</summary>
    private async Task<TimeSpan?> PollOnceAsync(CancellationToken cancel)
    {
        string? access = await GetAccessTokenAsync(cancel);
        if (access == null)
        {
            _status = "Not connected: enter your Client ID and secret, then Connect.";
            SetCover(null, null);
            return null;
        }

        PlaybackResponse response = await SpotifyClient.GetCurrentlyPlayingAsync(access, cancel);
        switch (response.Status)
        {
            case HttpStatusCode.OK when response.Playing != null:
                NowPlaying now = response.Playing;
                _status = $"Connected: {(now.IsPlaying ? "playing" : "paused")} {now.Title}{(now.Artist.Length > 0 ? " – " + now.Artist : string.Empty)}";
                await UpdateCoverAsync(now.ImageUrl, cancel);
                _lastLoggedError = null;
                return null;
            case HttpStatusCode.OK:
            case HttpStatusCode.NoContent:
                _status = "Connected: nothing playing.";
                SetCover(null, null);
                return null;
            case HttpStatusCode.Unauthorized:
                lock (_lock)
                {
                    _accessToken = null; // expired early: refresh on the next poll
                }

                return TimeSpan.FromSeconds(1);
            case HttpStatusCode.TooManyRequests:
                _status = "Spotify rate limit: waiting.";
                return response.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(30);
            default:
                _status = $"Spotify returned {(int)response.Status}; retrying.";
                return TimeSpan.FromSeconds(15);
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancel)
    {
        string clientId;
        string? secret, refresh;
        lock (_lock)
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromSeconds(60))
            {
                return _accessToken;
            }

            clientId = _clientId;
            secret = _secrets.ClientSecret;
            refresh = _secrets.ClientId == clientId ? _secrets.RefreshToken : null;
        }

        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(refresh))
        {
            return null;
        }

        SpotifyToken token = await SpotifyClient.RefreshAsync(clientId, secret, refresh, cancel);
        lock (_lock)
        {
            _accessToken = token.AccessToken;
            _accessExpiresUtc = token.ExpiresUtc;
            if (!string.IsNullOrEmpty(token.RefreshToken) && token.RefreshToken != _secrets.RefreshToken)
            {
                _secrets.RefreshToken = token.RefreshToken; // Spotify may rotate it
                _secrets.Save(_secretsFile);
            }

            return _accessToken;
        }
    }

    private async Task UpdateCoverAsync(string? url, CancellationToken cancel)
    {
        if (url == _coverUrl && (_coverPath == null || File.Exists(_coverPath)))
        {
            return;
        }

        if (!SpotifyClient.IsAllowedImageUrl(url))
        {
            SetCover(null, url);
            return;
        }

        string name = "cover-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url!)))[..16].ToLowerInvariant() + ".img";
        string path = Path.Combine(_coverDirectory, name);
        if (!File.Exists(path))
        {
            byte[] bytes = await SpotifyClient.DownloadImageAsync(url!, cancel);
            Directory.CreateDirectory(_coverDirectory);
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancel);
            File.Move(temp, path, overwrite: true);
        }

        SetCover(path, url);
        PruneCovers(path);
    }

    private void SetCover(string? path, string? url)
    {
        _coverUrl = url;
        if (path == _coverPath)
        {
            return;
        }

        _coverPath = path;
        CoverChanged?.Invoke(path);
    }

    /// <summary>Keeps the newest few covers (the renderer may still be reading the previous one).</summary>
    private void PruneCovers(string current)
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(_coverDirectory).GetFiles("cover-*")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepCoverFiles))
            {
                if (!string.Equals(old.FullName, current, StringComparison.OrdinalIgnoreCase))
                {
                    old.Delete();
                }
            }
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollCts?.Cancel();
        try
        {
            _pollTask?.Wait(2000);
        }
        catch (AggregateException)
        {
        }

        _wake.Dispose();
    }
}
