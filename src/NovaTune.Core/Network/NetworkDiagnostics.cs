using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NovaTune.Core.Localization;

namespace NovaTune.Core.Network;

public sealed record PingStats(
    string Target,
    string? ResolvedAddress,
    int Sent,
    int Received,
    double? MinMs,
    double? AvgMs,
    double? MaxMs,
    double? JitterMs,
    string? Error)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    /// <summary>Считает статистику по списку RTT (null — ответ не получен).</summary>
    public static PingStats From(string target, string? address, IReadOnlyList<double?> rtts, string? error = null)
    {
        var ok = rtts.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        double? jitter = null;
        if (ok.Count >= 2)
        {
            // Джиттер — среднее абсолютное отклонение между соседними ответами (как в RFC 3550, без сглаживания).
            jitter = ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        }

        return new PingStats(
            target,
            address,
            rtts.Count,
            ok.Count,
            ok.Count > 0 ? ok.Min() : null,
            ok.Count > 0 ? ok.Average() : null,
            ok.Count > 0 ? ok.Max() : null,
            jitter,
            error);
    }
}

public sealed record DnsResult(string Host, IReadOnlyList<string> Addresses, double? ElapsedMs, string? Error);

public sealed record ConnectivityResult(bool NetworkAvailable, bool? InternetReachable, string Detail);

/// <summary>Диагностика сети. Только измерения — никакие сетевые настройки не меняются.</summary>
public sealed class NetworkDiagnostics
{
    public async Task<PingStats> PingAsync(string target, int count, int timeoutMs, int intervalMs, IProgress<int>? progress, CancellationToken ct)
    {
        string? address = null;
        IPAddress? ip;
        try
        {
            if (!IPAddress.TryParse(target, out ip))
            {
                var addresses = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            }
        }
        catch (SocketException ex)
        {
            return PingStats.From(target, null, [], Loc.T("Не удалось разрешить имя: ", "Name resolution failed: ") + ex.Message);
        }

        if (ip is null)
        {
            return PingStats.From(target, null, [], Loc.T("Адрес не найден.", "No address found."));
        }

        address = ip.ToString();
        var rtts = new List<double?>();
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct).ConfigureAwait(false);
                rtts.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException)
            {
                rtts.Add(null);
            }

            progress?.Report(i + 1);
            if (i < count - 1)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }

        var error = rtts.All(r => r is null)
            ? Loc.T("Нет ответов. Некоторые серверы не отвечают на ICMP — это не обязательно означает проблему с сетью.", "No replies. Some servers ignore ICMP — this does not necessarily mean a network problem.")
            : null;
        return PingStats.From(target, address, rtts, error);
    }

    public async Task<DnsResult> ResolveAsync(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            sw.Stop();
            return new DnsResult(host, addresses.Select(a => a.ToString()).ToList(), sw.Elapsed.TotalMilliseconds, null);
        }
        catch (SocketException ex)
        {
            return new DnsResult(host, [], null, ex.Message);
        }
    }

    /// <summary>
    /// Проверка доступа в интернет тем же способом, что и Windows (NCSI): запрос к msftconnecttest.com.
    /// </summary>
    public async Task<ConnectivityResult> CheckConnectivityAsync(HttpClient http, CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return new ConnectivityResult(false, false, Loc.T("Нет активного сетевого подключения.", "No active network connection."));
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var text = await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt", cts.Token).ConfigureAwait(false);
            var ok = text.StartsWith("Microsoft Connect Test", StringComparison.Ordinal);
            return new ConnectivityResult(true, ok, ok
                ? Loc.T("Интернет доступен.", "Internet is reachable.")
                : Loc.T("Ответ отличается от ожидаемого — возможно, требуется вход в сеть (captive portal).", "Unexpected response — a sign-in page (captive portal) may be required."));
        }
        catch (HttpRequestException ex)
        {
            return new ConnectivityResult(true, false, Loc.T("Тестовый сервер недоступен: ", "Test server unreachable: ") + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConnectivityResult(true, false, Loc.T("Превышено время ожидания ответа.", "The request timed out."));
        }
    }
}
