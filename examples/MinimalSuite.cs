using System.Collections;
using GroupRecipesWithPopups.Core; // replace with your mod's namespace
using ValheimTestKit;

namespace MyMod.Tests
{
    [TestKitSuite("mymod")]
    public sealed class MinimalSuite : TestSuite
    {
        public override string Name => "mymod";

        // Set to true for suites that only make sense in a running client (popups, hud).
        public override bool RequiresClient => false;

        public override IEnumerator Run()
        {
            yield return Scenario("planner-cap", () =>
            {
                PopupPlan plan = PopupPlanner.BuildPlan(new KnowledgeEntry[0], maxPopups: 25);
                AssertEqual(0, plan.PopupCount, "popups on an empty plan");
            });

            yield return Scenario("handler-exists", () =>
            {
                // Deliver a payload to a handler your mod registered, as if a peer had sent it.
                var pkg = new ZPackage();
                Assert(RpcSynth.Deliver("MYMOD_Hello", RpcSynth.Rewind(pkg), sender: 987654321L), "handler not registered");
            });
        }
    }
}
