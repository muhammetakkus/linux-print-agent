using System.Net.Http.Headers;
using System.Net.Sockets;

namespace PrintAgent.Linux;

public sealed class IppException(ushort status, string? message) : IOException($"CUPS IPP 0x{status:X4}: {message ?? "request failed"}")
{
    public ushort Status { get; } = status;
}

public sealed class IppClient : IDisposable
{
    private readonly HttpClient _http;
    private int _requestId;
    public Uri Server { get; }
    public string? SocketPath { get; }

    public IppClient(Uri? server = null, string? socketPath = "/run/cups/cups.sock", HttpMessageHandler? handler = null)
    {
        Server = server ?? new Uri("http://localhost:631");
        SocketPath = socketPath;
        if (handler == null)
        {
            var sockets = new SocketsHttpHandler { AllowAutoRedirect = false };
            if (OperatingSystem.IsLinux() && socketPath != null)
            {
                sockets.ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                };
            }
            handler = sockets;
        }
        _http = new HttpClient(handler) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IppResponse> QueryAsync(ushort operation, string path, IEnumerable<IppAttribute> attributes, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _requestId);
        using var content = new ByteArrayContent(IppCodec.Encode(operation, id, attributes));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        using var response = await _http.PostAsync(path, content, ct);
        response.EnsureSuccessStatusCode();
        var result = IppCodec.Decode(await response.Content.ReadAsByteArrayAsync(ct));
        if (result.RequestId != id) throw new InvalidDataException("CUPS IPP request ID mismatch");
        if (result.Status >= 0x0100)
            throw new IppException(result.Status, result.Groups.Select(g => g.Text("status-message")).FirstOrDefault(v => v != null));
        return result;
    }

    public string PrinterUri(string name) => new UriBuilder(Server) { Scheme = Server.Scheme == "https" ? "ipps" : "ipp", Path = "/printers/" + Uri.EscapeDataString(name) }.Uri.AbsoluteUri;
    public void Dispose() => _http.Dispose();
}
