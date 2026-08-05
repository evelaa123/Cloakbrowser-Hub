using System.Net;
using System.Net.Sockets;
using System.Text;
using CloakHub.Core.Model;

namespace CloakHub.Core.Network;

/// <summary>
/// A loopback proxy that adds credentials on the browser's behalf.
/// <para>
/// Chromium can be told to use an authenticated HTTP proxy inline, but only on
/// recent builds; older ones read <c>user:pass@host</c> as a hostname and drop the
/// credentials, which surfaces as every page failing with 407 and no indication
/// why. The Electron build worked around this by falling back to Playwright's
/// authentication interceptor — not available here, and not desirable either,
/// since it needs the automation channel this app deliberately keeps closed.
/// </para>
/// <para>
/// So the browser is pointed at an unauthenticated proxy on 127.0.0.1 that
/// forwards to the real one with the <c>Proxy-Authorization</c> header attached.
/// The browser never handles the credentials, the behaviour is identical on every
/// binary, and nothing is exposed off the machine.
/// </para>
/// </summary>
public sealed class ProxyRelay(ProxyConfig upstream) : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _disposed;

    /// <summary>Where the browser should point, once started.</summary>
    public string Endpoint { get; private set; } = "";

    /// <summary>Port chosen by the OS. Zero before <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Connections accepted. Used by tests and diagnostics.</summary>
    public int Connections;

    public void Start()
    {
        if (_listener is not null) return;

        // Bound to loopback only. A relay that carries the user's proxy credentials
        // and listens on every interface would be an open authenticated proxy for
        // anyone on the network -- so the address is not configurable.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = $"127.0.0.1:{Port}";

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Listener closed underneath us during disposal.
                return;
            }

            Interlocked.Increment(ref Connections);

            // Not awaited: a browser opens many connections at once, and serialising
            // them would make the relay the bottleneck for every page load.
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var server = new TcpClient();
                await server.ConnectAsync(upstream.Host!, upstream.Port!.Value, ct).ConfigureAwait(false);

                var clientStream = client.GetStream();
                var serverStream = server.GetStream();

                // The first request carries the CONNECT (for https) or an absolute-URI
                // GET (for plain http). Either way it is the one that needs the header,
                // and after it the connection is an opaque tunnel.
                var header = await ReadHeaderAsync(clientStream, ct).ConfigureAwait(false);
                if (header is null) return;

                var rewritten = WithCredentials(header, upstream);
                await serverStream.WriteAsync(Encoding.ASCII.GetBytes(rewritten), ct).ConfigureAwait(false);
                await serverStream.FlushAsync(ct).ConfigureAwait(false);

                // Pumped in both directions until either side hangs up.
                var up = clientStream.CopyToAsync(serverStream, ct);
                var down = serverStream.CopyToAsync(clientStream, ct);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch
            {
                // One failed connection is a failed page load, which the browser
                // already reports in a form the user understands. Tearing down the
                // relay -- and with it every other tab -- would be far worse.
            }
        }
    }

    /// <summary>
    /// Read up to the blank line that ends the request head.
    /// <para>
    /// Byte at a time rather than buffered: anything read past the header belongs to
    /// the tunnel body, and a buffered reader would swallow it.
    /// </para>
    /// </summary>
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];

        while (buffer.Count < 32 * 1024)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) return null;

            buffer.Add(one[0]);

            if (buffer.Count >= 4
                && buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n'
                && buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
        }

        // A header this large is not something a browser sends; treating it as a
        // failure is safer than forwarding it.
        return null;
    }

    /// <summary>
    /// Insert the authorization header, replacing any the client already sent.
    /// <para>
    /// Internal so the exact rewrite can be asserted without opening a socket.
    /// </para>
    /// </summary>
    internal static string WithCredentials(string header, ProxyConfig upstream)
    {
        var token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{upstream.Username}:{upstream.Password ?? ""}"));

        var lines = header.Split("\r\n").ToList();

        // Drop any existing one. The browser has no credentials to send, but a
        // duplicate header is rejected by some proxies outright.
        lines.RemoveAll(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));

        // After the request line, which must stay first.
        var insertAt = lines.Count > 0 ? 1 : 0;
        lines.Insert(insertAt, $"Proxy-Authorization: Basic {token}");

        return string.Join("\r\n", lines);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { _cts?.Cancel(); } catch { /* already torn down */ }
        try { _listener?.Stop(); } catch { /* already torn down */ }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }
}
