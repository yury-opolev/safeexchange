/// <summary>
/// EntraCallGateTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using NUnit.Framework;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Threading.Tasks;

    [TestFixture]
    public class EntraCallGateTests
    {
        [Test]
        public async Task AtMostTheConfiguredCallsRun_AndAFreedSlotIsReused()
        {
            var gate = new EntraCallGate(2, TimeSpan.FromMilliseconds(20));

            Assert.That(await gate.TryEnterAsync(), Is.True);
            Assert.That(await gate.TryEnterAsync(), Is.True);
            Assert.That(await gate.TryEnterAsync(), Is.False);
            gate.Exit();
            Assert.That(await gate.TryEnterAsync(), Is.True);
        }

        [Test]
        public async Task AWaitingCall_GetsASlotFreedWithinTheWait()
        {
            var gate = new EntraCallGate(1, TimeSpan.FromSeconds(10));
            Assert.That(await gate.TryEnterAsync(), Is.True);

            var waiting = gate.TryEnterAsync();
            gate.Exit();

            Assert.That(await waiting, Is.True);
        }
    }
}
