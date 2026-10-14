using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Voca.Services;

/// <summary>An HTTP request as the phone page sends it: small, one at a time, body read in full.</summary>
public sealed record PhoneRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());

    public string? Cookie(string name) =>
        (Header("cookie") ?? "").Split(';')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim() == name)
            .Select(pair => Uri.UnescapeDataString(pair[1].Trim()))
            .FirstOrDefault();
}

/// <summary>Part of a file sent as the body (a video), read and sent off the UI thread.</summary>
public sealed record FileBody(string Path, long Start, long Length);

public sealed record PhoneResponse(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null,
    FileBody? File = null)
{
    public static PhoneResponse Text(int status, string contentType, string text, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, contentType, Encoding.UTF8.GetBytes(text), headers);

    public static PhoneResponse Redirect(string location, IReadOnlyDictionary<string, string>? headers = null) =>
        new(302, "text/plain; charset=utf-8", [], new Dictionary<string, string>(headers ?? new Dictionary<string, string>()) { ["Location"] = location });
}

/// <summary>
/// A minimal HTTP/1.1 server on a plain socket, so it needs neither administrator rights (HttpListener does,
/// for addresses other than localhost) nor the ASP.NET Core runtime. One request per connection; requests
/// are handed to <c>handle</c>, which runs them on the UI thread.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    /// <summary>A phone that stops taking data this long (screen off, out of Wi-Fi) is dropped.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

    private readonly TcpListener _listener;
    private readonly Func<PhoneRequest, Task<PhoneResponse>> _handle;
    private readonly CancellationTokenSource _stop = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening right away; throws <see cref="SocketException"/> when the port is taken.</summary>
    public PhoneServer(IPAddress address, int port, Func<PhoneRequest, Task<PhoneResponse>> handle)
    {
        _handle = handle;
        _listener = new TcpListener(address, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                PhoneRequest? request;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(ReadTimeout);
                    request = await ReadRequestAsync(stream, timeout.Token);
                }
                PhoneResponse response;
                if (request is null)
                    response = PhoneResponse.Text(400, "text/plain; charset=utf-8", "Bad request");
                else
                {
                    try { response = await _handle(request); }
                    catch (Exception ex) { response = PhoneResponse.Text(500, "text/plain; charset=utf-8", ex.Message); }
                }
                await WriteResponseAsync(stream, response, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The phone went away (screen locked, Wi-Fi changed) or the server is stopping.
            }
        }
    }

    /// <summary>Reads the request line, headers and a Content-Length body. Null when it is not a request we accept.</summary>
    internal static async Task<PhoneRequest?> ReadRequestAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0) return null;
            length += read;
            headerEnd = IndexOfBlankLine(buffer, length);
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.")) return null;
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        var bodyLength = headers.TryGetValue("content-length", out var text) && int.TryParse(text, out var n) ? n : 0;
        if (bodyLength < 0 || bodyLength > MaxBodyBytes) return null;
        var body = new byte[bodyLength];
        var already = Math.Min(length - (headerEnd + 4), bodyLength);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        if (already < bodyLength) await stream.ReadExactlyAsync(body.AsMemory(already), cancel);

        var target = start[1];
        var question = target.IndexOf('?');
        var path = Uri.UnescapeDataString(question < 0 ? target : target[..question]);
        var query = ParseQuery(question < 0 ? "" : target[(question + 1)..]);
        return new PhoneRequest(start[0].ToUpperInvariant(), path, query, headers, Encoding.UTF8.GetString(body));
    }

    private static int IndexOfBlankLine(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
            result[Decode(pair[0])] = pair.Length == 2 ? Decode(pair[1]) : "";
        }
        return result;
    }

    private static async Task WriteResponseAsync(Stream stream, PhoneResponse response, CancellationToken cancel)
    {
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n")
            .Append($"Content-Type: {response.ContentType}\r\n")
            .Append($"Content-Length: {response.File?.Length ?? response.Body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await WriteAsync(stream, Encoding.UTF8.GetBytes(head.ToString()), cancel);
        if (response.File is { } file) await CopyFileAsync(stream, file, cancel);
        else await WriteAsync(stream, response.Body, cancel);
        await stream.FlushAsync(cancel);
    }

    private static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(WriteTimeout);
        await stream.WriteAsync(bytes, timeout.Token);
    }

    /// <summary>Sends the asked part of the file in chunks; the phone's player usually stops reading once it has enough.</summary>
    private static async Task CopyFileAsync(Stream stream, FileBody file, CancellationToken cancel)
    {
        await using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
        source.Seek(file.Start, SeekOrigin.Begin);
        var buffer = new byte[256 * 1024];
        var left = file.Length;
        while (left > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancel);
            if (read == 0) break;
            await WriteAsync(stream, buffer.AsMemory(0, read), cancel);
            left -= read;
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        302 => "Found",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        416 => "Range Not Satisfiable",
        _ => "Error"
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
