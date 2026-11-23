using System.Text.Json;
using Mixtri.Core.Projects;

namespace Mixtri.Core.Export;

// Append only: kinds are serialized by value. OutputPlanned tells the client which file the
// worker is about to write so it can clean up after a worker it had to kill.
public enum ExportWorkerMessageKind { Start, Preview, Cancel, Progress, Completed, Cancelled, Failed, OutputPlanned }

public sealed record ExportWorkerMessage(ExportWorkerMessageKind Kind)
{
    public ProjectStateSnapshot? Snapshot { get; init; }
    public ExportSettings? Settings { get; init; }
    public string? OutputFolder { get; init; }
    /// <summary>Start: the exact file the user chose; <c>null</c> to name it after the project in <see cref="OutputFolder"/>.</summary>
    public string? OutputFilePath { get; init; }
    public bool Interactive { get; init; }
    public double PreviewMilliseconds { get; init; }
    public ExportProgress? Progress { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
    /// <summary>Completed: non-fatal problems, each completing "Exported, but …".</summary>
    public string[]? Warnings { get; init; }
}

public static class ExportWorkerProtocol
{
    public const string Argument = "--mixtri-export-worker";
    private static readonly JsonSerializerOptions Options = new(MixtriPackage.JsonOptions)
    {
        WriteIndented = false,
    };

    public static string Serialize(ExportWorkerMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static ExportWorkerMessage Deserialize(string json) =>
        JsonSerializer.Deserialize<ExportWorkerMessage>(json, Options)
        ?? throw new InvalidDataException("The export worker returned an empty message.");
}
