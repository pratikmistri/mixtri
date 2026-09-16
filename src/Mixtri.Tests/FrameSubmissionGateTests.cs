using Mixtri.Core.Capture;

namespace Mixtri.Tests;

[TestClass]
public sealed class FrameSubmissionGateTests
{
    [TestMethod]
    public void CloseCompletesOnlyAfterEveryAdmittedProducerExits()
    {
        int completed = 0;
        var gate = new FrameSubmissionGate(() => completed++);
        Assert.IsTrue(gate.TryEnter());
        Assert.IsTrue(gate.TryEnter());
        gate.Close();
        gate.Close();
        Assert.IsFalse(gate.TryEnter());
        Assert.AreEqual(0, completed);
        gate.Exit();
        Assert.AreEqual(0, completed);
        gate.Exit();
        gate.Close();
        Assert.AreEqual(1, completed);
        Assert.ThrowsException<InvalidOperationException>(gate.Exit);
    }

    [TestMethod]
    public void CloseWithoutActiveProducersCompletesOnce()
    {
        int completed = 0;
        var gate = new FrameSubmissionGate(() => completed++);
        gate.Close();
        gate.Close();
        Assert.AreEqual(1, completed);
        Assert.IsFalse(gate.TryEnter());
    }

    [TestMethod]
    public async Task ClosingAndLastExitMayRaceWithoutLosingCompletion()
    {
        for (int i = 0; i < 100; i++)
        {
            int completed = 0;
            var gate = new FrameSubmissionGate(() => Interlocked.Increment(ref completed));
            Assert.IsTrue(gate.TryEnter());
            await Task.WhenAll(Task.Run(gate.Close), Task.Run(gate.Exit));
            Assert.AreEqual(1, completed);
            Assert.IsFalse(gate.TryEnter());
        }
    }
}
