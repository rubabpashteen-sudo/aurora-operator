using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AuroraOperator.Core.Agent;

internal static class BrowserWorkflow
{
    internal const string OpenTab = "open-chrome-tab";
    internal const string ReadPage = "read-public-page";
    private const int MaxPageBytes = 128 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly HttpClient PublicHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = ConnectPublicAsync,
    }) { Timeout = TimeSpan.FromSeconds(20) };

    internal static string? MatchDirectTab(string request)
    {
        var query = Regex.Match(request.Trim(),
            @"^(?:open (?:google )?chrome and search(?: for)?|search (?:google )?chrome for)\s+(?<query>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (query.Success && query.Groups["query"].Value.Length <= 300 &&
            !Regex.IsMatch(query.Groups["query"].Value, @"\b(?:and then|then|and open|and post|and save)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Groups["query"].Value.Trim());

        var link = Regex.Match(request.Trim(),
            @"^(?:(?:open|visit) (?<url>\S+) in (?:google )?chrome|open (?:google )?chrome (?:and (?:open|visit)|to)\s+(?<url>\S+)|(?:google )?chrome kholo (?:aur |us mein |os mein )?\s*(?<url>\S+?)(?:\s+khol(?:o| do))?)\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (!link.Success) return null;
        var value = link.Groups["url"].Value.TrimEnd('.');
        if (!value.Contains('.') && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        return TryPublicUrl(value, out var uri) ? uri.AbsoluteUri : null;
    }

    internal static bool TryPublicUrl(string value, out Uri uri)
    {
        uri = null!;
        if (value.Length > 2000 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort ||
            parsed.UserInfo.Length != 0 || parsed.HostNameType != UriHostNameType.Dns)
            return false;

        var host = parsed.IdnHost.TrimEnd('.');
        if (!host.Contains('.') || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    internal static string? FindChrome()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return roots.Where(root => !string.IsNullOrEmpty(root))
            .Select(root => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static Task<ToolResult> OpenAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || FindChrome() is not { } chrome)
            return Task.FromResult(new ToolResult(false, "Google Chrome was not found in the standard installation folders."));
        if (!TryPublicUrl(args["url"], out var uri))
            return Task.FromResult(new ToolResult(false, "The Chrome tab requires a public HTTPS address."));
        ct.ThrowIfCancellationRequested();
        var launch = new ProcessStartInfo(chrome) { UseShellExecute = false };
        launch.ArgumentList.Add(uri.AbsoluteUri);
        using var process = Process.Start(launch);
        return Task.FromResult(process is null
            ? new ToolResult(false, "Chrome did not accept the tab launch request.")
            : new ToolResult(true, $"Sent the tab request to Chrome for {uri.AbsoluteUri}."));
    }

    internal static async Task<ToolResult> ReadAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (!TryPublicUrl(args["url"], out var uri))
            return new ToolResult(false, "Page inspection requires a public HTTPS address.");

        for (int redirect = 0; redirect <= 3; redirect++)
        {
            using var response = await PublicHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } next)
            {
                if (!TryPublicUrl(new Uri(uri, next).AbsoluteUri, out uri))
                    return new ToolResult(false, "The page redirected to an unsupported address.");
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return new ToolResult(false, $"The public page returned HTTP {(int)response.StatusCode}.");
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("text/plain" or "text/html"))
                return new ToolResult(false, "The page did not return text or HTML.");
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] bytes = new byte[MaxPageBytes];
            int count = await body.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
            string content = Encoding.UTF8.GetString(bytes, 0, count);
            return PageResult(uri, content, mediaType == "text/html", count == bytes.Length);
        }
        return new ToolResult(false, "The page exceeded the redirect limit.");
    }

    internal static ToolResult PageResult(Uri uri, string content, bool html, bool truncated)
    {
        if (html)
        {
            content = Regex.Replace(content, @"<!--[\s\S]*?-->|<(script|style|noscript)\b[^>]*>[\s\S]*?</\1\s*>", " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            content = Regex.Replace(content, @"<[^>]*>", " ", RegexOptions.CultureInvariant, RegexTimeout);
            content = WebUtility.HtmlDecode(content);
        }
        content = Regex.Replace(content, @"\s+", " ", RegexOptions.CultureInvariant, RegexTimeout).Trim();
        if (content.Length == 0)
            return new ToolResult(false, "The page returned no readable text. It may need JavaScript or sign-in.");
        string sample = content.Length > 1400 ? content[..1400] : content;
        return new ToolResult(true, $"Read a text preview of {uri.AbsoluteUri}.", sample + (truncated || sample.Length < content.Length ? "…" : ""));
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new HttpRequestException("The hostname did not resolve exclusively to public IP addresses.");
        Exception? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("The public website could not be reached.", failure);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xe0) == 0x20 && !address.IsIPv6Teredo &&
                !(b[0] == 0x20 && b[1] == 0x02) &&
                !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] is 0 or 168) &&
            !(b[0] == 198 && b[1] is 18 or 19 or 51) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
