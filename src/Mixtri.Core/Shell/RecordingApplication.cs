using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Shell;

/// <summary>Called on the editor dispatcher; receipts live with the captured recording.</summary>
public sealed class RecordingApplication
{
    private readonly HashSet<Guid> _appliedAwaitingReceipt = [];

    public ShellProcessResponse Apply(
        ShellProcessRequest request, string receiptDirectory,
        Func<string?> getRejection, Action apply)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(getRejection);
        ArgumentNullException.ThrowIfNull(apply);
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Id == Guid.Empty)
            throw new ArgumentException("A recording completion request is required.", nameof(request));

        string pending = Path.Combine(receiptDirectory, $"{request.Id:N}.applying");
        string completed = Path.Combine(receiptDirectory, $"{request.Id:N}.applied");
        try
        {
            if (MarkerExists(completed))
            {
                _appliedAwaitingReceipt.Remove(request.Id);
                return new(true);
            }
            if (_appliedAwaitingReceipt.Contains(request.Id))
            {
                File.Move(pending, completed);
                _appliedAwaitingReceipt.Remove(request.Id);
                return new(true);
            }
            if (MarkerExists(pending)) return Uncertain();
            if (getRejection() is { } rejection) return new(false, rejection);

            Directory.CreateDirectory(receiptDirectory);
            try
            {
                using var marker = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                marker.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                if (MarkerExists(completed)) return new(true);
                if (MarkerExists(pending)) return Uncertain();
                throw;
            }

            // A different editor may have completed between the first check and our claim.
            if (MarkerExists(completed))
            {
                File.Delete(pending);
                return new(true);
            }

            apply();
            _appliedAwaitingReceipt.Add(request.Id);
            File.Move(pending, completed);
            _appliedAwaitingReceipt.Remove(request.Id);
            return new(true);
        }
        catch (Exception ex)
        {
            DiagLog.Write("ShellProcess", $"Recording {request.Id:N} application/receipt failed: {ex}");
            return new(false, $"Could not confirm recording application: {ex.Message}") { OutcomeUnknown = true };
        }
    }

    private static ShellProcessResponse Uncertain() => new(
        false, "A previous recording delivery was interrupted. Its outcome requires recovery; the take will not be applied again automatically.")
    { OutcomeUnknown = true };

    private static bool MarkerExists(string path)
    {
        try
        {
            using var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
