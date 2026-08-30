using System.Reflection;
using System.Runtime.InteropServices;

namespace Mixtri.Core.Diagnostics;

/// <summary>What the app should do after an unhandled exception reaches the top of the UI stack.</summary>
public enum UnhandledExceptionDisposition
{
    /// <summary>Leave the exception unhandled so the runtime tears the process down.</summary>
    Terminate,

    /// <summary>Mark the exception handled and keep running.</summary>
    Recover,
}

/// <summary>A classification result plus the reason, which is written to the crash log.</summary>
public readonly record struct UnhandledExceptionDecision(
    UnhandledExceptionDisposition Disposition,
    string Reason)
{
    public bool ShouldRecover => Disposition == UnhandledExceptionDisposition.Recover;
}

/// <summary>
/// Decides whether an exception that escaped to <c>Application.UnhandledException</c> is a
/// known-recoverable failure or an unknown one.
/// </summary>
/// <remarks>
/// The policy is deliberately an allow list, not a deny list. Swallowing an unknown UI
/// exception leaves navigation, project state or native/COM resources half-mutated, and the
/// process then dies later as a stowed failfast with no useful context (see the crash
/// playbook in <c>learnings.md</c>). Only failures that provably apply no partial state —
/// cancellation, and teardown races once shutdown has begun — are recovered; everything
/// else terminates so the fault is reported at its real origin.
/// </remarks>
public static class UnhandledExceptionPolicy
{
    /// <summary>RPC_E_DISCONNECTED — the proxied object's server is already gone.</summary>
    private const int RpcDisconnected = unchecked((int)0x80010108);

    /// <summary>RPC_S_SERVER_UNAVAILABLE — apartment torn down before the call landed.</summary>
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);

    /// <summary>RO_E_CLOSED — WinRT object used after <c>Close()</c>.</summary>
    private const int WinRtObjectClosed = unchecked((int)0x80000013);

    /// <summary>Bound on wrapper unwrapping so a self-referencing chain cannot spin.</summary>
    private const int MaxUnwrapDepth = 8;

    /// <summary>
    /// Classifies <paramref name="exception"/>.
    /// </summary>
    /// <param name="exception">The exception that reached the handler; may be null.</param>
    /// <param name="isShuttingDown">
    /// True once a quiesce/exit has started. Teardown ordering races are only excusable then —
    /// the same exception during normal operation is a real bug.
    /// </param>
    public static UnhandledExceptionDecision Classify(Exception? exception, bool isShuttingDown)
        => Classify(exception, isShuttingDown, depth: 0);

    private static UnhandledExceptionDecision Classify(Exception? exception, bool isShuttingDown, int depth)
    {
        if (exception is null)
            return Terminate("no exception instance was available to classify");

        if (depth >= MaxUnwrapDepth)
            return Terminate($"exception chain exceeded {MaxUnwrapDepth} wrappers");

        // Checked over the whole cause chain first, so a recoverable-looking wrapper such as
        // OperationCanceledException can never excuse a process-corrupting inner fault.
        if (IsProcessCorrupting(exception, depth))
            return Terminate($"{exception.GetType().Name} has a cause indicating unrecoverable process state");

        switch (exception)
        {
            case AggregateException aggregate:
                var inner = aggregate.Flatten().InnerExceptions;
                if (inner.Count == 0)
                    return Terminate("AggregateException carried no inner exception");

                foreach (var candidate in inner)
                {
                    var decision = Classify(candidate, isShuttingDown, depth + 1);
                    if (!decision.ShouldRecover)
                        return decision;
                }

                return Recover("every aggregated inner exception is individually recoverable");

            case TargetInvocationException { InnerException: { } invocationInner }:
                return Classify(invocationInner, isShuttingDown, depth + 1);

            case OperationCanceledException:
                return Recover("cooperative cancellation escaped a UI continuation; no state is partially applied");

            case ObjectDisposedException when isShuttingDown:
                return Recover("object disposed during shutdown teardown; the process is already exiting");

            case COMException com when isShuttingDown && IsShutdownRaceHResult(com.HResult):
                return Recover($"HR=0x{(uint)com.HResult:X8} is a shutdown teardown race; the process is already exiting");

            default:
                return Terminate($"{exception.GetType().FullName} is not on the recoverable allow list");
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> or anything in its cause chain is a fault after
    /// which the CLR, the XAML tree or the COM apartment cannot be trusted. Those are never
    /// recoverable — not even during shutdown, and not even when wrapped.
    /// </summary>
    private static bool IsProcessCorrupting(Exception? exception, int depth)
    {
        if (exception is null || depth >= MaxUnwrapDepth)
            return false;

        if (exception
            is OutOfMemoryException          // includes InsufficientMemoryException
            or StackOverflowException
            or InsufficientExecutionStackException
            or AccessViolationException
            or SEHException
            or BadImageFormatException
            or InvalidComObjectException     // RCW already released; further use is undefined
            or TypeInitializationException)  // the type stays permanently broken for this process
        {
            return true;
        }

        return exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.Any(inner => IsProcessCorrupting(inner, depth + 1))
            : IsProcessCorrupting(exception.InnerException, depth + 1);
    }

    private static bool IsShutdownRaceHResult(int hresult) => hresult
        is RpcDisconnected or RpcServerUnavailable or WinRtObjectClosed;

    private static UnhandledExceptionDecision Recover(string reason)
        => new(UnhandledExceptionDisposition.Recover, reason);

    private static UnhandledExceptionDecision Terminate(string reason)
        => new(UnhandledExceptionDisposition.Terminate, reason);
}
