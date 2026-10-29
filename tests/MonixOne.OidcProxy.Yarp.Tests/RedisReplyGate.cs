using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Holds a selected RESP reply after the real Redis has executed its command.
/// Commands continue upstream, so tests can observe recovery reads without sleeps
/// or replacing Redis/CAS with a test implementation.
/// </summary>
internal sealed class RedisReplyGate : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private readonly IPEndPoint _upstream;
    private readonly Task _accept;
    private Reply? _reply;

    internal RedisReplyGate(InfrastructureFixture infrastructure)
    {
        _upstream = (IPEndPoint)infrastructure.Connection.GetEndPoints().Single();
        _listener.Start();
        _accept = AcceptAsync();
    }

    internal Task<ConnectionMultiplexer> ConnectAsync(int asyncTimeout = 10000)
        => ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { (IPEndPoint)_listener.LocalEndpoint },
            AsyncTimeout = asyncTimeout,
            Protocol = RedisProtocol.Resp2
        });

    internal Reply HoldNextSessionReply(string command)
    {
        var reply = new Reply(command);
        Volatile.Write(ref _reply, reply);
        return reply;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient downstream)
    {
        using var client = downstream;
        using var server = new TcpClient();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var ct = connection.Token;
        try
        {
            await server.ConnectAsync(_upstream, ct);
            var requests = Channel.CreateUnbounded<(string Command, bool Session)>();
            var outgoing = ForwardCommandsAsync();
            var incoming = ForwardRepliesAsync();
            await Task.WhenAny(outgoing, incoming);
            connection.Cancel();
            await Task.WhenAll(outgoing, incoming);

            async Task ForwardCommandsAsync()
            {
                while (true)
                {
                    var frame = await ReadFrameAsync(client.GetStream(), ct);
                    var text = Encoding.UTF8.GetString(frame);
                    var command = text.Split("\r\n", 4)[2];
                    var session = text.Contains(":sessions:", StringComparison.Ordinal);
                    var reply = Volatile.Read(ref _reply);
                    if (session && command == "GET" && reply?.Reached.Task.IsCompleted == true)
                        reply.RecoveryRead.TrySetResult();
                    await requests.Writer.WriteAsync((command, session), ct);
                    await server.GetStream().WriteAsync(frame, ct);
                }
            }

            async Task ForwardRepliesAsync()
            {
                await foreach (var request in requests.Reader.ReadAllAsync(ct))
                {
                    var frame = await ReadFrameAsync(server.GetStream(), ct);
                    var reply = Volatile.Read(ref _reply);
                    if (request.Session && request.Command == reply?.Command &&
                        Interlocked.CompareExchange(ref reply.Claimed, 1, 0) == 0)
                    {
                        reply.Reached.TrySetResult();
                        await reply.Release.Task.WaitAsync(ct);
                    }
                    await client.GetStream().WriteAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { /* Client disconnects are expected in fault tests. */ }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await ReadPartAsync();
        return buffer.ToArray();

        async Task ReadPartAsync()
        {
            var single = new byte[1];
            await stream.ReadExactlyAsync(single, ct);
            var prefix = single[0];
            buffer.WriteByte(prefix);
            var line = new StringBuilder();
            while (true)
            {
                await stream.ReadExactlyAsync(single, ct);
                buffer.WriteByte(single[0]);
                if (single[0] == '\n') break;
                if (single[0] != '\r') line.Append((char)single[0]);
            }
            if (prefix == '$' && int.Parse(line.ToString()) is var length && length >= 0)
            {
                var data = new byte[length + 2];
                await stream.ReadExactlyAsync(data, ct);
                buffer.Write(data);
            }
            else if (prefix == '*')
            {
                var count = int.Parse(line.ToString());
                for (var i = 0; i < count; i++) await ReadPartAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _shutdown.Dispose();
    }

    internal sealed class Reply(string command)
    {
        internal string Command { get; } = command;
        internal int Claimed;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RecoveryRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
