using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GrannyMP;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "local.adam.granny.multiplayer";
    public const string Name = "Granny Multiplayer";
    public const string Version = "1.1.8";
    /// <summary>Changes with every build, so two different archives are told apart.</summary>
    public static readonly string ModBuild = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8);

    public static ManualLogSource L;
    public static string PluginDir;

    /// <summary>Fingerprint of the game build: sizes of GameAssembly.dll and global-metadata.dat.</summary>
    public static string GameBuild = "?";
    public const string ReferenceBuild = "39105024/9163140"; // Steam build 21006637 (06.06.2026), Unity 6000.0.58f2
    public static string GameVersion = "?";
    public static string GameBuildLabel => $"v{GameVersion}, " + (GameBuild == ReferenceBuild ? Tr.L("сборка 06.06.2026 (как у хоста)", "build 06.06.2026 (same as the host)") : Tr.L($"ДРУГАЯ сборка ({GameBuild})", $"DIFFERENT build ({GameBuild})"));

    /// <summary>Second copy on one PC for testing: Granny.exe -mpid 2 -screen-fullscreen 0 ...</summary>
    public static int TestId;
    public static bool Windowed;

    public override void Load()
    {
        var args = Environment.GetCommandLineArgs();
        var idArg = Array.IndexOf(args, "-mpid");
        if (idArg >= 0 && idArg + 1 < args.Length) int.TryParse(args[idArg + 1], out TestId);
        Windowed = args.Contains("-screen-fullscreen");
        L = Log;
        PluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        try
        {
            var root = Paths.GameRootPath;
            var ga = new FileInfo(Path.Combine(root, "GameAssembly.dll")).Length;
            var dataDir = Directory.GetDirectories(root, "*_Data").FirstOrDefault(d => Directory.Exists(Path.Combine(d, "il2cpp_data"))) ?? Path.Combine(root, "Granny_Data");
            var md = new FileInfo(Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat")).Length;
            GameBuild = $"{ga}/{md}";
        }
        catch { }
        try { GameVersion = Application.version; } catch { }
        Log.LogInfo("Game build: " + GameBuildLabel);
        Cfg.Bind(Config);

        ClassInjector.RegisterTypeInIl2Cpp<MpRuntime>();
        var host = new GameObject("GrannyMP.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<MpRuntime>();

        try { Application.runInBackground = true; } catch { }
        Patches.Apply(new Harmony(Guid));
        Log.LogInfo($"{Name} {Version} loaded. F1 — меню мультиплеера.");
    }
}
