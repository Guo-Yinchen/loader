using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace Chispa.Launcher;

internal sealed record ProgramInfo(string Id, string Name, string Executable, string? Icon = null,
    bool IsBeta = false, string? ParentProgramId = null, bool? HasRelease = null)
{
    public override string ToString()
    {
        var label = IsBeta ? Name + " · Beta" : Name;
        return Icon is { Length: > 0 } icon && !icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            ? icon + " " + label : label;
    }
}
// `Signature` is the release signature envelope (base64) and `SignatureKeyId` is a display-only
// hint: the installer verifies the envelope itself and never trusts these two fields.
internal sealed record Release(string ProgramId, string Version, string Executable, string Sha256, long Size,
    Dictionary<string, string>? Binaries = null, string? Signature = null, string? SignatureKeyId = null);
internal sealed record LoginResult(string Token, DateTimeOffset ExpiresAt, string? Username = null,
    DateTimeOffset? AccountExpiresAt = null, DateTimeOffset? ServerTime = null);
internal sealed record AccountInfo(string Username, DateTimeOffset? AccountExpiresAt, DateTimeOffset ServerTime);
// Launcher metadata. DownloadUrl is validated before use and only ever opened by the user.
internal sealed record LauncherRelease(string Version, string? MinimumVersion = null,
    string? DownloadUrl = null, string? Notes = null);
// A launch ticket is the credential the program redeems on its own behalf: minutes of life,
// one redemption, bound to this account and this program.
internal sealed record LaunchTicket(string Ticket, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, int TicketSeconds);
internal sealed class SessionExpiredException : InvalidOperationException;

internal sealed class LauncherApi : IDisposable
{
    private readonly HttpClient http;
    private string? token;
    public string ServerUrl => http.BaseAddress?.ToString() ?? "";
    public AccountInfo? Account { get; private set; }
    private long accountReceivedAt;
    private int accountRevision;
    public DateTimeOffset ServerNow => Account is { } account
        ? account.ServerTime + Stopwatch.GetElapsedTime(accountReceivedAt) : DateTimeOffset.UtcNow;
    public bool AccountExpired => Account?.AccountExpiresAt is { } expiry && expiry <= ServerNow;

    public LauncherApi(string serverUrl)
    {
        var uri = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ArgumentException("Remote server URL must use HTTPS.");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<TimeSpan> CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var timer = Stopwatch.StartNew();
        using var response = await http.GetAsync("api/v1/connectivity/client", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("服务器版本较旧，请重新启动服务端。");
        response.EnsureSuccessStatusCode();
        timer.Stop();
        return timer.Elapsed;
    }

    /// <summary>What the server says the current loader build is. Display only: the download link
    /// is shown to the user and never executed, and it is validated again here (https, or loopback
    /// http for local development) so a hostile answer cannot smuggle in a local path.</summary>
    public async Task<LauncherRelease?> GetLauncherLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.GetAsync("api/v1/launcher/latest", timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<LauncherRelease>(timeout.Token);
            if (result is null || string.IsNullOrWhiteSpace(result.Version)) return null;
            if (!IsAcceptableDownload(result.DownloadUrl)) result = result with { DownloadUrl = "" };
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidDataException)
        {
            // An update hint is a courtesy; a server that does not answer must not break login.
            return null;
        }
    }

    private static bool IsAcceptableDownload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/v1/session", new { username, password }, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("账号或密码错误。");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("登录失败次数过多，请 15 分钟后再试。");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken);
        token = result?.Token ?? throw new InvalidDataException("Server did not return a session token.");
        // Fetch explicitly: older services must not silently make a missing expiry look permanent.
        await RefreshAccountAsync(cancellationToken);
    }

    public async Task RegisterAsync(string email, string password, string code, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.PostAsJsonAsync("api/v1/register", new { email, password, code }, timeout.Token);
        await EnsureActivationResponse(response, timeout.Token);
    }

    public async Task RedeemAsync(string code, CancellationToken cancellationToken)
    {
        var revision = ++accountRevision;
        var session = token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = AuthorizedRequest(HttpMethod.Post, "api/v1/activation/redeem");
        request.Content = JsonContent.Create(new { code });
        var requestedAt = Stopwatch.GetTimestamp();
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new SessionExpiredException();
        await EnsureActivationResponse(response, timeout.Token);
        var account = await response.Content.ReadFromJsonAsync<AccountInfo>(timeout.Token)
            ?? throw new InvalidDataException("服务器未返回续费后的账户信息。");
        if (session != token || revision != accountRevision) return;
        Account = account;
        accountReceivedAt = requestedAt;
    }

    private static async Task EnsureActivationResponse(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("操作过于频繁，请稍后再试。");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("服务端尚未启用注册和激活码功能，请联系管理员更新服务端。");
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            throw new InvalidOperationException(error.TryGetProperty("error", out var message) ? message.GetString() : "注册或兑换失败。");
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        var session = token;
        var revision = ++accountRevision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = AuthorizedRequest(HttpMethod.Get, "api/v1/session");
        var requestedAt = Stopwatch.GetTimestamp();
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("服务端需要更新，才能显示账户授权时间。请更新并重启服务端。");
        EnsureAuthorized(response);
        var account = await response.Content.ReadFromJsonAsync<AccountInfo>(timeout.Token)
            ?? throw new InvalidDataException("服务器未返回账户信息。");
        if (session != token || revision != accountRevision) return; // Ignore a stale background response.
        Account = account;
        accountReceivedAt = requestedAt;
    }

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, "api/v1/programs");
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureAuthorized(response);
        var programs = await response.Content.ReadFromJsonAsync<ProgramInfo[]>(cancellationToken) ?? [];
        foreach (var program in programs) ValidateProgram(program);
        return programs;
    }

    public async Task<Release> GetLatestAsync(string programId, CancellationToken cancellationToken)
    {
        ValidateProgramId(programId);
        using var request = AuthorizedRequest(HttpMethod.Get, $"api/v1/programs/{programId}/releases/latest");
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureAuthorized(response);
        return await response.Content.ReadFromJsonAsync<Release>(cancellationToken)
            ?? throw new InvalidDataException("Server returned an empty release manifest.");
    }

    /// <summary>Downloads a release with a bounded retry budget. A dropped connection resumes from
    /// the bytes already on disk (Range) instead of starting over; authentication, permission,
    /// ban/withdrawal and signature failures are never retried - retrying those just hides the
    /// real problem behind a spinner.</summary>
    public async Task DownloadAsync(Release release, string destination, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ValidateProgramId(release.ProgramId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(release.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new InvalidDataException("Invalid release version.");
        if (release.Size <= 0) throw new InvalidDataException("清单里的包大小无效，无法下载。");
        EnsureDiskSpace(destination, release.Size);

        var partial = destination + ".partial";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(release, partial, progress, cancellationToken);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && IsRetryable(ex, cancellationToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                }
            }
            if (new FileInfo(partial).Length != release.Size)
                throw new InvalidDataException("下载的文件大小与清单不符，请重试或联系管理员。");
            await using (var downloaded = File.OpenRead(partial))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
                if (!digest.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    downloaded.Close();
                    File.Delete(partial);
                    throw new InvalidDataException("下载的发布包哈希不符，已清除损坏的断点，请重新下载。");
                }
            }
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial) && !File.Exists(destination))
            {
                // Keep the partial file only when it can still be resumed; otherwise it is litter.
                try { if (new FileInfo(partial).Length == 0) File.Delete(partial); } catch { }
            }
        }
    }

    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        return ex switch
        {
            HttpRequestException request => request.StatusCode is null || (int)request.StatusCode >= 500 ||
                                            request.StatusCode == System.Net.HttpStatusCode.RequestTimeout,
            DownloadInterruptedException => true,
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };
    }

    private sealed class DownloadInterruptedException(string message, Exception? inner = null) : IOException(message, inner);

    internal static void EnsureDiskSpace(string destination, long needed)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
                            ?? Path.GetTempPath();
            Directory.CreateDirectory(directory);
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            // Extraction is checked separately using the actual ZIP expanded size.
            if (free < checked(needed + 64L * 1024 * 1024))
                throw new IOException($"磁盘空间不足：需要约 {(needed + 64L * 1024 * 1024) / (1024 * 1024)} MB，" +
                                      $"当前可用 {free / (1024 * 1024)} MB。请清理空间后重试。");
        }
        catch (IOException) { throw; }
        catch (Exception) { /* unknown volume: let the download itself report the failure */ }
    }

    private async Task DownloadOnceAsync(Release release, string partialPath, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        long already = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (already > release.Size) { File.Delete(partialPath); already = 0; }
        if (already == release.Size) { progress?.Report(100); return; }

        using var request = AuthorizedRequest(HttpMethod.Get,
            $"api/v1/programs/{release.ProgramId}/releases/{release.Version}/package");
        if (already > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server cannot continue from here (file changed or was replaced): start over.
            File.Delete(partialPath);
            throw new DownloadInterruptedException("续传位置不被服务器接受，已重新开始下载。");
        }
        EnsureAuthorized(response);
        if (already > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // Server ignored the range: restart cleanly rather than appending a whole second copy.
            already = 0;
            File.Delete(partialPath);
        }
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != already ||
                range.Length != release.Size || range.To != release.Size - 1)
                throw new InvalidDataException("服务器返回了错误的续传范围，已停止下载。");
        }
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size - already)
            throw new InvalidDataException("服务器返回的下载长度与清单不符。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partialPath, already > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = already;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            int count;
            try
            {
                // ResponseHeadersRead does not apply HttpClient.Timeout to body reads.
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                count = await input.ReadAsync(buffer, readTimeout.Token);
                readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is IOException or HttpRequestException or OperationCanceledException)
            {
                throw new DownloadInterruptedException("下载中断，已保留断点。", ex);
            }
            if (count == 0) break;
            received += count;
            if (received > release.Size) throw new InvalidDataException("下载内容超过清单声明的大小。");
            // A disk error is not a network interruption and must not trigger network retries.
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress?.Report((int)(received * 100 / release.Size));
        }
        if (received != release.Size)
            throw new DownloadInterruptedException($"下载中断（{received}/{release.Size} 字节），将尝试续传。");
    }

    internal static void ValidateProgramId(string id)
    {
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,31}$"))
            throw new InvalidDataException("Invalid program ID.");
    }

    public async Task<Release> AuthorizeLaunchAsync(string programId, string version, CancellationToken cancellationToken)
    {
        ValidateProgramId(programId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new InvalidDataException("本机版本信息无效，请重新安装。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = AuthorizedRequest(HttpMethod.Post, $"api/v1/programs/{programId}/releases/{version}/launch");
        using var response = await http.SendAsync(request, timeout.Token);
        EnsureAuthorized(response);
        return await response.Content.ReadFromJsonAsync<Release>(timeout.Token)
            ?? throw new InvalidDataException("服务器未返回启动验证信息。");
    }

    // The launch credential the program redeems for itself. Issued by the same session the
    // loader holds, and every property that matters is re-checked by the server when the
    // program spends it: the ticket proves the login happened, not that the account may run.
    public async Task<LaunchTicket> RequestLaunchTicketAsync(string programId, string version,
        CancellationToken cancellationToken)
    {
        ValidateProgramId(programId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new InvalidDataException("本机版本信息无效，请重新安装。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = AuthorizedRequest(HttpMethod.Post, $"api/v1/programs/{programId}/releases/{version}/launch-ticket");
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Two different 404s: the release is gone, or the server predates this endpoint.
            // Starting a program without a credential would be the bypass this exists to close,
            // so both refuse; the message has to say which.
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new InvalidOperationException(body.Contains("release_unknown", StringComparison.Ordinal)
                ? "该版本已不可用，请联系管理员。"
                : "服务器不支持启动凭证（服务端需要更新），已阻止启动。");
        }
        EnsureAuthorized(response);
        var result = await response.Content.ReadFromJsonAsync<LaunchTicket>(timeout.Token)
            ?? throw new InvalidDataException("服务器未返回启动凭证。");
        if (result.Ticket is null || result.Ticket.Length is < 16 or > 128 || result.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("服务器返回的启动凭证无效。");
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = AuthorizedRequest(HttpMethod.Delete, "api/v1/session");
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        finally { ClearSession(); }
    }

    public void ClearSession() { token = null; Account = null; }

    private static void EnsureAuthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new SessionExpiredException();
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("此程序的授权已撤销，请联系管理员或同步程序列表。");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("该版本已不可用，或服务器需要更新，请联系管理员。");
        response.EnsureSuccessStatusCode();
    }

    internal static void ValidateProgram(ProgramInfo program)
    {
        if (program is null) throw new InvalidDataException("Invalid program entry.");
        ValidateProgramId(program.Id);
        if (string.IsNullOrWhiteSpace(program.Name) || program.Name.Length > 80 ||
            string.IsNullOrWhiteSpace(program.Executable) || program.Executable.Length > 120 ||
            !program.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            program.Executable != Path.GetFileName(program.Executable) ||
            program.Executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            program.Icon?.Length > 180_000)
            throw new InvalidDataException("Invalid program metadata.");
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Log in first.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public void Dispose() => http.Dispose();
}
