using System.Text.Json;

namespace Mixtri.Core.Shell;

/// <summary>Keeps a completed take recoverable until an editor acknowledges ownership.</summary>
public sealed class RecordingHandoffStore(string root)
{
    public async Task SaveAsync(ShellProcessRequest request)
    {
        if (request.Command != ShellProcessCommand.RecordingCompleted || request.Project is null || request.Id == Guid.Empty)
            throw new ArgumentException("A completed recording is required.", nameof(request));
        Directory.CreateDirectory(root);
        string path = GetPath(request.Id);
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(request));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IEnumerable<ShellProcessRequest> ReadPending()
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal))
        {
            var request = JsonSerializer.Deserialize<ShellProcessRequest>(File.ReadAllBytes(path));
            if (request?.Command != ShellProcessCommand.RecordingCompleted || request.Project is null
                || request.Id == Guid.Empty || !string.Equals(path, GetPath(request.Id), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid recording handoff: {path}");
            yield return request;
        }
    }

    public void Acknowledge(Guid id) => File.Delete(GetPath(id));
    private string GetPath(Guid id) => Path.Combine(root, $"{id:N}.json");
}
