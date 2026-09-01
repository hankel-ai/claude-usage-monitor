using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// Shared polling scaffolding for the dollar-spend meters (LiteLLM, OpenRouter).
/// Owns the self-rescheduling timer, the HttpClient lifecycle, the shared log file and the
/// error/offline events; subclasses supply only a base URL and the work of one poll.
///
/// Deliberately lighter than <see cref="ClaudeApiService"/>: no 429 backoff machinery —
/// transient failures are logged, surfaced, and retried on the next tick.
/// </summary>
public abstract class SpendServiceBase : IDisposable
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageMonitor", "log.txt");

    private HttpClient? _httpClient;
    private string? _httpClientBaseUrl;
    private Timer? _timer;
    private volatile bool _stopped = true;
    private CancellationTokenSource? _cts;
    private int _pollIntervalSeconds = 120;
    private int _pollInProgress;

    /// <summary>Base address for this provider's requests. Re-read on every poll so a
    /// settings change rebuilds the client.</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Fallback used when <see cref="BaseUrl"/> is blank.</summary>
    protected abstract string DefaultBaseUrl { get; }

    /// <summary>Performs one poll and raises whatever "updated" event the subclass owns.
    /// The payload shape differs per provider, so the update event lives on the subclass.</summary>
    protected abstract Task PollOnceAsync(CancellationToken ct);

    /// <summary>Human-readable message for the bar tooltip when a fetch fails.</summary>
    public event Action<string>? SpendError;

    /// <summary>Fired when the request fails at the transport level (DNS, refused, timeout).</summary>
    public event Action? NetworkError;

    protected void RaiseError(string message) => SpendError?.Invoke(message);
    protected void RaiseNetworkError() => NetworkError?.Invoke();

    protected static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected HttpClient EnsureHttpClient()
    {
        var baseUrl = BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultBaseUrl;

        // Rebuild the client if the configured base URL changed.
        if (_httpClient != null && _httpClientBaseUrl != baseUrl)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        if (_httpClient == null)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClientBaseUrl = baseUrl;
        }

        return _httpClient;
    }

    public void StartPolling(int intervalSeconds)
    {
        StopPolling();
        _pollIntervalSeconds = Math.Max(60, intervalSeconds);
        _stopped = false;
        _cts = new CancellationTokenSource();
        _timer = new Timer(async _ => await PollAndRescheduleAsync(), null,
            TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void StopPolling()
    {
        _stopped = true;
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Fetch immediately (e.g. after a settings change).</summary>
    public void RefreshNow()
    {
        if (_stopped) return;
        _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private async Task PollAndRescheduleAsync()
    {
        if (_stopped) return;
        if (Interlocked.Exchange(ref _pollInProgress, 1) == 1)
            return;
        try
        {
            await PollOnceAsync(_cts?.Token ?? CancellationToken.None);
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
            if (!_stopped)
                _timer?.Change(TimeSpan.FromSeconds(_pollIntervalSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    public virtual void Dispose()
    {
        StopPolling();
        _httpClient?.Dispose();
        _httpClient = null;
        _cts?.Dispose();
        _cts = null;
    }
}
