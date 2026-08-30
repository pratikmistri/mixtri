using System.Reflection;
using System.Runtime.InteropServices;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Tests;

/// <summary>
/// STAB-001: the XAML unhandled-exception handler must only swallow explicitly
/// classified recoverable faults; everything else has to terminate the process.
/// </summary>
[TestClass]
public sealed class UnhandledExceptionPolicyTests
{
    #region Unknown exceptions terminate

    [TestMethod]
    public void Classify_NullException_Terminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(null, isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
        Assert.IsFalse(decision.ShouldRecover);
        Assert.IsFalse(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [TestMethod]
    public void Classify_UnknownException_Terminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new InvalidOperationException("layout is inconsistent"), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
        StringAssert.Contains(decision.Reason, "InvalidOperationException");
    }

    [TestMethod]
    public void Classify_UnknownExceptionDuringShutdown_StillTerminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new NullReferenceException(), isShuttingDown: true);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    [TestMethod]
    public void Classify_ComException_Terminates()
    {
        // RPC_E_WRONG_THREAD — a thread-affinity violation is never recoverable.
        var decision = UnhandledExceptionPolicy.Classify(
            new COMException("wrong thread", unchecked((int)0x8001010E)), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    [TestMethod]
    public void Classify_ComExceptionWithShutdownHResult_TerminatesWhileRunning()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new COMException("disconnected", unchecked((int)0x80010108)), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    [TestMethod]
    public void Classify_IoFailure_Terminates()
    {
        // Not on the allow list: file work reaching the UI top level may have written
        // a partial project, so the process must not continue.
        var decision = UnhandledExceptionPolicy.Classify(
            new IOException("disk full"), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    #endregion

    #region Process-corrupting exceptions always terminate

    [TestMethod]
    public void Classify_ProcessCorruptingExceptions_AlwaysTerminate()
    {
        Exception[] fatal =
        [
            new OutOfMemoryException(),
            new InsufficientMemoryException(),
            new AccessViolationException(),
            new SEHException(),
            new BadImageFormatException(),
            new InvalidComObjectException(),
            new TypeInitializationException("Some.Type", null),
        ];

        foreach (var exception in fatal)
        {
            foreach (var shuttingDown in new[] { false, true })
            {
                var decision = UnhandledExceptionPolicy.Classify(exception, shuttingDown);

                Assert.AreEqual(
                    UnhandledExceptionDisposition.Terminate,
                    decision.Disposition,
                    $"{exception.GetType().Name} (isShuttingDown={shuttingDown}) must terminate");
            }
        }
    }

    [TestMethod]
    public void Classify_CancellationWrappingFatalCause_Terminates()
    {
        // The wrapper is recoverable in isolation, but a fatal cause anywhere in the chain wins.
        var decision = UnhandledExceptionPolicy.Classify(
            new OperationCanceledException("cancelled", new OutOfMemoryException()),
            isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    [TestMethod]
    public void Classify_TargetInvocationWrappingFatalCause_Terminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new TargetInvocationException(new OutOfMemoryException()), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    #endregion

    #region Known recoverable exceptions

    [TestMethod]
    public void Classify_OperationCanceled_Recovers()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new OperationCanceledException(), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Recover, decision.Disposition);
        Assert.IsTrue(decision.ShouldRecover);
    }

    [TestMethod]
    public void Classify_TaskCanceled_Recovers()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new TaskCanceledException(), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Recover, decision.Disposition);
    }

    [TestMethod]
    public void Classify_ObjectDisposed_RecoversOnlyWhileShuttingDown()
    {
        var exception = new ObjectDisposedException("CanvasControl");

        Assert.AreEqual(
            UnhandledExceptionDisposition.Terminate,
            UnhandledExceptionPolicy.Classify(exception, isShuttingDown: false).Disposition);

        Assert.AreEqual(
            UnhandledExceptionDisposition.Recover,
            UnhandledExceptionPolicy.Classify(exception, isShuttingDown: true).Disposition);
    }

    [TestMethod]
    public void Classify_ShutdownRaceComExceptions_RecoverWhileShuttingDown()
    {
        int[] hresults =
        [
            unchecked((int)0x80010108), // RPC_E_DISCONNECTED
            unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
            unchecked((int)0x80000013), // RO_E_CLOSED
        ];

        foreach (var hresult in hresults)
        {
            var decision = UnhandledExceptionPolicy.Classify(
                new COMException("teardown", hresult), isShuttingDown: true);

            Assert.AreEqual(
                UnhandledExceptionDisposition.Recover,
                decision.Disposition,
                $"HR=0x{(uint)hresult:X8} should be a recoverable teardown race");
        }
    }

    [TestMethod]
    public void Classify_TargetInvocationWrappingCancellation_Recovers()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new TargetInvocationException(new OperationCanceledException()), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Recover, decision.Disposition);
    }

    #endregion

    #region Aggregates

    [TestMethod]
    public void Classify_AggregateOfCancellations_Recovers()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new AggregateException(new TaskCanceledException(), new OperationCanceledException()),
            isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Recover, decision.Disposition);
    }

    [TestMethod]
    public void Classify_AggregateWithOneUnknownInner_Terminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new AggregateException(new TaskCanceledException(), new InvalidOperationException("bad")),
            isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
        StringAssert.Contains(decision.Reason, "InvalidOperationException");
    }

    [TestMethod]
    public void Classify_EmptyAggregate_Terminates()
    {
        var decision = UnhandledExceptionPolicy.Classify(
            new AggregateException(), isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    [TestMethod]
    public void Classify_DeeplyNestedWrappers_Terminate()
    {
        Exception exception = new OperationCanceledException();
        for (var i = 0; i < 12; i++)
            exception = new TargetInvocationException(exception);

        var decision = UnhandledExceptionPolicy.Classify(exception, isShuttingDown: false);

        Assert.AreEqual(UnhandledExceptionDisposition.Terminate, decision.Disposition);
    }

    #endregion
}
