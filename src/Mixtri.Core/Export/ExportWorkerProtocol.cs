using System.Text.Json;
using Mixtri.Core.Projects;

namespace Mixtri.Core.Export;

public enum ExportWorkerMessageKind { Start, Preview, Cancel, Progress, Completed, Cancelled, Failed }

public sealed record ExportWorkerMessage(ExportWorkerMessageKind Kind)
{
    public ProjectStateSnapshot? Snapshot { get; init; }
    public ExportSettings? Settings { get; init; }
    public string? OutputFolder { get; init; }
    public bool Interactive { get; init; }
    public double PreviewMilliseconds { get; init; }
    public ExportProgress? Progress { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
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
