using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;

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
internal sealed record Release(string ProgramId, string Version, string Executable, string Sha256, long Size,
    Dictionary<string, string>? Binaries = null);
internal sealed record LoginResult(string Token, DateTimeOffset ExpiresAt);
internal sealed class SessionExpiredException : InvalidOperationException;

internal sealed class LauncherApi : IDisposable
{
    private readonly HttpClient http;
    private string? token;
    public string ServerUrl => http.BaseAddress?.ToString() ?? "";

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

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/v1/session", new { username, password }, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("账号或密码错误，或账号已到期。");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("登录失败次数过多，请 15 分钟后再试。");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken);
        token = result?.Token ?? throw new InvalidDataException("Server did not return a session token.");
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

    public async Task DownloadAsync(Release release, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ValidateProgramId(release.ProgramId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(release.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new InvalidDataException("Invalid release version.");
        using var request = AuthorizedRequest(HttpMethod.Get, $"api/v1/programs/{release.ProgramId}/releases/{release.Version}/package");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureAuthorized(response);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            received += count;
            if (received > release.Size) throw new InvalidDataException("Download exceeds declared package size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            if (release.Size > 0) progress?.Report((int)(received * 100 / release.Size));
        }
        if (received != release.Size) throw new InvalidDataException("Download size does not match manifest.");
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

    public void ClearSession() => token = null;

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
