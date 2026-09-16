namespace Mixtri.Core.Shell;

/// <summary>Retries a durable recording handoff without mistaking transport loss for rejection.</summary>
public static class RecordingDelivery
{
    public static async Task<ShellProcessRequest> DeliverAsync(
        ShellProcessRequest request,
        Func<ShellProcessRequest, Task<ShellProcessResponse?>> trySend,
        Func<Task<Guid>> createEditor,
        Func<ShellProcessRequest, Task> persistRedirect,
        int retries,
        TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trySend);
        ArgumentNullException.ThrowIfNull(createEditor);
        ArgumentNullException.ThrowIfNull(persistRedirect);
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Project is null || request.Id == Guid.Empty)
            throw new ArgumentException("A completed recording is required.", nameof(request));

        if (request.EditorId != Guid.Empty)
        {
            var response = await SendWithRetryAsync(request, trySend, retries, retryDelay);
            if (response.Success) return request;
            if (request.RedirectedFromEditorId.HasValue)
                throw new InvalidOperationException(response.Error ?? "The separate editor rejected the recording.");
        }

        var editor = await createEditor();
        if (editor == Guid.Empty || editor == request.EditorId)
            throw new InvalidOperationException("The separate editor must have a new identity.");
        var separate = request with
        {
            EditorId = editor,
            RedirectedFromEditorId = request.EditorId,
            AppendToProjectId = null,
            Message = request.Message ?? (request.AppendToProjectId.HasValue
                ? "The original editor could not accept Record More. Your new recording was opened separately; the original project was not changed."
                : "The previous editor could not accept this recording, so it was opened separately without replacing existing edits."),
        };

        // Recovery must know this destination before the editor can apply any side effect.
        await persistRedirect(separate);
        var accepted = await SendWithRetryAsync(separate, trySend, retries, retryDelay);
        if (!accepted.Success)
            throw new InvalidOperationException(accepted.Error ?? "The separate editor rejected the recording.");
        return separate;
    }

    private static async Task<ShellProcessResponse> SendWithRetryAsync(
        ShellProcessRequest request,
        Func<ShellProcessRequest, Task<ShellProcessResponse?>> trySend,
        int retries,
        TimeSpan retryDelay)
    {
        for (int attempt = 0; ; attempt++)
        {
            var response = await trySend(request);
            if (response is { OutcomeUnknown: false }) return response;
            if (attempt == retries)
                throw new InvalidOperationException(
                    response?.Error ?? "The editor did not confirm the recording. It has been kept for retry in the same editor.");
            await Task.Delay(retryDelay);
        }
    }
}
