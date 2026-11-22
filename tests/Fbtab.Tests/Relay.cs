using System.Net;
using System.Net.Sockets;

namespace Fbtab.Tests;

/// <summary>
/// A TCP relay between the driver and the test server that spoils the server's answers: delivers them in
/// small pieces, or passes only the first bytes and then sends noise and hangs up.
/// </summary>
internal sealed class Relay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _port;
    private readonly int _pieceSize;
    private readonly long _breakAfter;

    /// <param name="pieceSize">Deliver the server's bytes in pieces of this size; 0 leaves them as they come.</param>
    /// <param name="breakAfter">Pass this many bytes from the server, then send noise and close; -1 never breaks.</param>
    public Relay(string host, int port, int pieceSize = 0, long breakAfter = -1)
    {
        _host = host;
        _port = port;
        _pieceSize = pieceSize;
        _breakAfter = breakAfter;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                client.NoDelay = true;
                await server.ConnectAsync(_host, _port, _stop.Token);
                var up = client.GetStream().CopyToAsync(server.GetStream(), _stop.Token);
                var down = DownAsync(server.GetStream(), client.GetStream());
                await Task.WhenAny(up, down);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Either side hung up: closing both below is all that is left to do.
            }
        }
    }

    private async Task DownAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[65536];
        long passed = 0;
        while (true)
        {
            var read = await from.ReadAsync(buffer, _stop.Token);
            if (read == 0)
            {
                return;
            }

            var good = _breakAfter < 0 ? read : (int)Math.Min(read, _breakAfter - passed);
            var piece = _pieceSize > 0 ? _pieceSize : good;
            for (var at = 0; at < good; at += piece)
            {
                await to.WriteAsync(buffer.AsMemory(at, Math.Min(piece, good - at)), _stop.Token);
                if (_pieceSize > 0)
                {
                    await Task.Delay(1, _stop.Token);
                }
            }

            passed += good;
            if (_breakAfter >= 0 && passed >= _breakAfter)
            {
                var noise = new byte[64];
                new Random(42).NextBytes(noise);
                await to.WriteAsync(noise, _stop.Token);
                return;
            }
        }
    }
}
