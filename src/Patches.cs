using HarmonyLib;
using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace GrannyMP;

/// <summary>Hooks into original game scripts. Each patch is applied separately so one failure can't break the rest.</summary>
public static class Patches
{
    public static void Apply(Harmony h)
    {
        Try(h, typeof(PickUp), "Update", nameof(PickUpUpdate));
        Try(h, typeof(GrannyHear), "OnTriggerEnter", nameof(GrannyHearEnter));
        Try(h, typeof(EnemyAIGranny), "grannyHitByPepper", nameof(Pepper));
        Try(h, typeof(EnemyAIGranny), "grannyHitByGun", nameof(Gun));
        Try(h, typeof(EnemyAIGranny), "grannyHitByCar", nameof(Car));
        Try(h, typeof(EnemyAIGranny), "grannyHitByArrow", nameof(Arrow));
        Try(h, typeof(EnemyAIGranny), "grannyFreeze", nameof(Freeze));
        Try(h, typeof(playerCrawl), "crouching", nameof(NoCrouch));
        Try(h, typeof(explode), "explodeNow", nameof(Exploded));
        // the game's own endings count its own days: rounds end only through our death scenes
        Try(h, typeof(playerDead), "startEndScene", nameof(NoEnding));
        Try(h, typeof(playerDead), "startEndScene2", nameof(NoEnding));
        Try(h, typeof(playerDead), "startEndScene3", nameof(NoEnding));
        Try(h, typeof(playerDead), "gameOverNoGranny", nameof(NoEnding));
        Try(h, typeof(playerDead), "startEndScene4", nameof(NoEndingVoid));
        Try(h, typeof(FirstPersonController_Egen), "playerFalling", nameof(NoFallCoroutine));
        Try(h, typeof(FirstPersonController_Egen), "afterPlayerFallHurt", nameof(NoFallCoroutine));
        Try(h, typeof(FirstPersonController_Egen), "PlayerLandBad", nameof(NoFallDeath));
        Try(h, typeof(playerCaught), "fallingDead", nameof(NoFallCoroutine));
        Try(h, typeof(FirstPersonController_Egen), "PlayerGetsCaught", nameof(NativeCaught));
        Try(h, typeof(FirstPersonController_Egen), "PlayerGetsCaughtSpider", nameof(NativeCaught));
    }

    static void Try(Harmony h, Type type, string method, string prefix)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { Plugin.L.LogWarning($"[patch] {type.Name}.{method} not found"); return; }
            h.Patch(target, prefix: new HarmonyMethod(typeof(Patches), prefix));
        }
        catch (Exception e) { Plugin.L.LogWarning($"[patch] {type.Name}.{method} failed: {e.Message}"); }
    }

    static Game G => Game.I;
    static bool RoundOn => G.Session && G.InRound;

    // Granny can't pick up items
    static bool PickUpUpdate() => !(RoundOn && G.AmGranny);

    static bool NoEnding(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!G.Session) return true;
        Plugin.L.LogWarning("[day] the game's own ending blocked");
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }
    static bool NoEndingVoid() { if (!G.Session) return true; Plugin.L.LogWarning("[day] the game's own ending blocked"); return false; }

    // nothing around the house can kill Granny: no fall deaths for her
    static bool NoFallDeath() => !(RoundOn && G.AmGranny);
    static bool NoFallCoroutine(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!(RoundOn && G.AmGranny)) return true;
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }

    // Granny's trigger colliders are off in a session, so the game's own blast check can't reach her
    static void Exploded(explode __instance)
    {
        try { if (RoundOn && G.AmSurvivor) G.ReportExplosion(__instance.transform.position); } catch { }
    }

    // Granny never crouches
    static bool NoCrouch() => !(RoundOn && G.AmGranny);

    // creaky floor points: tell the Granny player instead of the (disabled) AI
    static bool GrannyHearEnter(GrannyHear __instance)
    {
        if (!G.Session) return true;
        try { if (G.InRound && G.AmSurvivor) G.ReportNoise(__instance.transform.position); } catch { }
        return false;
    }

    static bool Pepper() { if (!RoundOn) return true; G.ReportGrannyStun(8f, 1); return false; }
    static bool Gun() { if (!RoundOn) return true; G.ReportGrannyStun(20f, 2); return false; }
    static bool Car() { if (!RoundOn) return true; G.ReportGrannyStun(20f, 6); return false; }
    // coroutines: an empty enumerator instead of the game's "fall down and die" script
    static bool Arrow(ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!RoundOn) return true;
        G.ReportGrannyStun(10f, 3);
        __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
        return false;
    }
    static bool Freeze() { if (!RoundOn) return true; G.ReportGrannyStun(10f, 4); return false; }

    // environment catches (attic spider etc.) go through our day system
    static bool NativeCaught()
    {
        if (!RoundOn) return true;
        if (G.AmSurvivor) G.EnvironmentCaught();
        return false;
    }
}
