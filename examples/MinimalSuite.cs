using System.Collections;
using ValheimTestKit;

namespace MyMod.Tests
{
    [TestKitSuite("mymod")]
    public sealed class MinimalSuite : TestSuite
    {
        public override string Name => "mymod";

        // true for suites that only make sense in a running client (popups, hud)
        public override bool RequiresClient => false;

        public override IEnumerator Run()
        {
            yield return Scenario("pure-logic", () =>
            {
                // Anything that needs no Unity object goes here; this runs inside the game process,
                // so your mod assembly is loaded and its statics are the real ones.
                AssertEqual(2, 1 + 1, "arithmetic");
            });

            yield return Scenario("handler-registered", () =>
            {
                Assert(RpcSynth.IsRegistered("MYMOD_Hello"), "MYMOD_Hello is not registered");
            });

            yield return Scenario("synth-peer-delivery", () =>
            {
                // Deliver builds the parameter package with ZRpc.Serialize (exactly what InvokeRoutedRPC sends) and
                // hands it to RoutedMethodBase.Invoke, so the typed handler deserializes the argument itself.
                int calls = 0;
                bool seen = true;
                ZRoutedRpc.instance.Register<bool>("MYMOD_SelfTest",
                    (long sender, bool value) => { calls++; seen = value; });

                Assert(RpcSynth.Deliver("MYMOD_SelfTest", 987654321L, false), "handler missing");
                AssertEqual(1, calls, "handler call count");
                Assert(!seen, "the typed argument did not arrive");
            });
        }
    }
}
