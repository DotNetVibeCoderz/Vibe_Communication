// Made by Gravicode Studios, led by Kang Fadhil.
using System.IO;
using UnrealBuildTool;

public class RumbleNet : ModuleRules
{
    public RumbleNet(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "Json" });

        var thirdParty = Path.Combine(ModuleDirectory, "..", "ThirdParty", "rumble");
        PublicIncludePaths.Add(Path.Combine(thirdParty, "include"));

        if (Target.Platform == UnrealTargetPlatform.Win64)
        {
            var bin = Path.Combine(thirdParty, "lib", "Win64");
            PublicAdditionalLibraries.Add(Path.Combine(bin, "rumble_native.dll.lib"));
            PublicDelayLoadDLLs.Add("rumble_native.dll");
            RuntimeDependencies.Add("$(BinaryOutputDir)/rumble_native.dll", Path.Combine(bin, "rumble_native.dll"));
        }
        else if (Target.Platform == UnrealTargetPlatform.Linux)
        {
            var so = Path.Combine(thirdParty, "lib", "Linux", "librumble_native.so");
            PublicAdditionalLibraries.Add(so);
            RuntimeDependencies.Add("$(BinaryOutputDir)/librumble_native.so", so);
        }
        else if (Target.Platform == UnrealTargetPlatform.Mac)
        {
            var dylib = Path.Combine(thirdParty, "lib", "Mac", "librumble_native.dylib");
            PublicDelayLoadDLLs.Add(dylib);
            RuntimeDependencies.Add("$(BinaryOutputDir)/librumble_native.dylib", dylib);
        }
    }
}
