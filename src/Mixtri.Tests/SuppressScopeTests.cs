using Mixtri_App.Helpers;

namespace Mixtri.Tests;

[TestClass]
public sealed class SuppressScopeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Dispose_RestoresPreviousValue(bool previous)
    {
        bool suppress = previous;
        using (SuppressScope.Enter(ref suppress))
            Assert.IsTrue(suppress);
        Assert.AreEqual(previous, suppress);
    }

    [TestMethod]
    public void NestedScope_KeepsOuterSuppressionEnabled()
    {
        bool suppress = false;
        using (SuppressScope.Enter(ref suppress))
        {
            using (SuppressScope.Enter(ref suppress))
                Assert.IsTrue(suppress);
            Assert.IsTrue(suppress);
        }
        Assert.IsFalse(suppress);
    }

    [TestMethod]
    public void Exception_RestoresOuterSuppression()
    {
        bool suppress = false;
        using (SuppressScope.Enter(ref suppress))
        {
            try
            {
                using var inner = SuppressScope.Enter(ref suppress);
                throw new InvalidOperationException("test");
            }
            catch (InvalidOperationException)
            {
                Assert.IsTrue(suppress);
            }
        }
        Assert.IsFalse(suppress);
    }
}
