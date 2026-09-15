using System.Buffers.Binary;
using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Shell;

/// <summary>Bounded, current-user-only control messages. Media stays in files, never in this pipe.</summary>
public sealed class ShellProcessPipe : IDisposable
{
    public const int MaximumMessageBytes = 1024 * 1024;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ShellProcessRequest, Task<ShellProcessResponse>> _handler;
    private readonly string _name;
    private readonly Task _listener;

    public ShellProcessPipe(string name, Func<ShellProcessRequest, Task<ShellProcessResponse>> handler)
    {
        _name = name;
        _handler = handler;
        _listener = ListenAsync(CreateServer());
    }

    private NamedPipeServerStream CreateServer() => new(
        _name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream server)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                var connected = server;
                server = CreateServer();
                _ = ServeAsync(connected);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagLog.Write("ShellIPC", $"Listener '{_name}' stopped unexpectedly: {ex}");
        }
        finally { server.Dispose(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        using (var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, timeout.Token))
        {
            try
            {
                var request = await ReadAsync<ShellProcessRequest>(pipe, readCancellation.Token).ConfigureAwait(false);
                ShellProcessResponse response;
                try
                {
                    response = request.Version != 1 || request.Id == Guid.Empty || !Enum.IsDefined(request.Command)
                        ? new ShellProcessResponse(false, "Unsupported shell message.")
                        : await _handler(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DiagLog.Write("ShellIPC", $"Handler failed: {ex}");
                    response = new(false, ex.Message);
                }
                await WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                DiagLog.Write("ShellIPC", $"Request failed: {ex}");
            }
        }
    }

    public static async Task<ShellProcessResponse> SendAsync(
        string name, ShellProcessRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var pipe = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        if (request.ExpectedProcessId is { } expected)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the connected shell process.");
            if (actual != expected)
                throw new IOException("The connected recorder is a different process; the request was not sent.");
        }
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<ShellProcessResponse>(pipe, timeout.Token).ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Shell message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid shell message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty shell message.");
    }

    public void Dispose() => _shutdown.Cancel();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
