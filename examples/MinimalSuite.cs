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
                // A bool payload proves the typed path: Deliver goes through RoutedMethodBase.Invoke,
                // which deserializes the argument exactly like a real peer's packet.
                bool seen = false;
                ZRoutedRpc.instance.Register<bool>("MYMOD_SelfTest",
                    (long sender, bool value) => { seen = value; });

                var payload = new ZPackage();
                payload.Write(true);
                Assert(RpcSynth.Deliver("MYMOD_SelfTest", payload, sender: 987654321L), "handler missing");
                Assert(seen, "the typed argument did not arrive");
            });
        }
    }
}
