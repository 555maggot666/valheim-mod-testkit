using BepInEx;

namespace MyMod.Tests
{
    /// <summary>Stub plugin: BepInEx only loads assemblies that declare a plugin.</summary>
    [BepInPlugin("you.MyMod.Tests", "MyMod Test Suite", "1.0.0")]
    [BepInDependency("555maggot666.ValheimTestKit", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SuiteLoader : BaseUnityPlugin
    {
    }
}
