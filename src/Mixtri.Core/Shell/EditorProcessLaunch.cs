using System.Text;

namespace Mixtri.Core.Shell;

public sealed record EditorProcessLaunch(Guid? EditorId, string? ProjectPath, bool Background, bool StartInEditor = false)
{
    public bool IsEditor => EditorId.HasValue || ProjectPath is not null;

    public static EditorProcessLaunch Parse(IEnumerable<string> arguments)
    {
        Guid? editorId = null;
        string? projectPath = null;
        bool background = false;
        bool startInEditor = false;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--editor=", StringComparison.Ordinal))
            {
                if (!Guid.TryParseExact(argument[9..], "N", out var id) || id == Guid.Empty)
                    throw new ArgumentException("Invalid editor session identifier.");
                editorId = id;
            }
            else if (argument.StartsWith("--project=", StringComparison.Ordinal))
            {
                projectPath = Path.GetFullPath(Encoding.UTF8.GetString(Convert.FromBase64String(argument[10..])));
                if (!Projects.MixtriPackage.IsPackagePath(projectPath))
                    throw new ArgumentException("The editor launch must name a Mixtri project.");
            }
            else if (argument == "--background-recorder")
                background = true;
            else if (argument == "--start-editor")
                startInEditor = true;
        }
        if (background && (editorId.HasValue || projectPath is not null))
            throw new ArgumentException("A recorder launch cannot also be an editor launch.");
        if (startInEditor && !editorId.HasValue && projectPath is null)
            throw new ArgumentException("An editor process is required for editor navigation.");
        return new(editorId, projectPath, background, startInEditor);
    }

    public static IEnumerable<string> Arguments(Guid editorId, string? projectPath = null, bool startInEditor = false)
    {
        if (editorId == Guid.Empty) throw new ArgumentException("An editor session identifier is required.");
        yield return $"--editor={editorId:N}";
        if (startInEditor) yield return "--start-editor";
        if (projectPath is not null)
            yield return "--project=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath)));
    }
}
