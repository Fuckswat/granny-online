using UnityEngine;

namespace GrannyMP;

/// <summary>
/// How long Granny stays down after a weapon, exactly like the original game (read from its code, 2026-11-06):
/// the ragdoll lies for RagdollFade/RagdollShotFade.fadeStartTime (15 s on Extreme), fades ~5 s (alpha 0.2/s),
/// then grannyRestart counts its timer: after the shotgun (RagdollShotFade -> startTimer2, setTime2)
/// Normal 60 / Easy 120 / Hard 30 / Extreme 15 s; after an arrow and the rest (RagdollFade -> startTimer, setTime)
/// Normal 30 / Easy 90 / Hard 0 / Extreme 0 s. Difficulty = PlayerPrefs "DiffData": 0 Normal, 1 Easy, 2 Hard, 3 Extreme.
/// The host computes it from its own game and sends it with the round start.
/// </summary>
public sealed partial class Game
{
    public float KnockGun = 75f, KnockArrow = 45f;
    public const byte StunSurvived = 9;

    static readonly float[] RestartAfterShot = { 60f, 120f, 30f, 15f };
    static readonly float[] RestartAfterFade = { 30f, 90f, 0f, 0f };
    const float FadeOutTime = 5f;
    const float KnockMax = 35f; // longest knockout after a weapon (shotgun, car, blast, arrow), seconds

    static int ReadDifficulty()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\DVloper\Granny");
            var name = k?.GetValueNames().FirstOrDefault(n => n.StartsWith("DiffData_h"));
            if (name != null && k.GetValue(name) is int v) return v == 5 ? 3 : Mathf.Clamp(v, 0, 3);
        }
        catch (Exception e) { Plugin.L.LogWarning("[knock] difficulty: " + e.Message); }
        return 0;
    }

    float RagdollLead(bool shot, int diff)
    {
        if (diff == 3) return 15f + FadeOutTime;
        try
        {
            foreach (var t in new[] { R?.GrannyAi?.grannyRagdoll, R?.GrannyAi?.grannyForceRagdoll })
            {
                if (t == null) continue;
                if (shot) { var c = t.GetComponentInChildren<RagdollShotFade>(true); if (c != null && c.fadeStartTime > 0) return c.fadeStartTime + FadeOutTime; }
                else { var c = t.GetComponentInChildren<RagdollFade>(true); if (c != null && c.fadeStartTime > 0) return c.fadeStartTime + FadeOutTime; }
            }
            var all = shot ? GameRefs.FindAll<RagdollShotFade>(true).Select(c => c.fadeStartTime) : GameRefs.FindAll<RagdollFade>(true).Select(c => c.fadeStartTime);
            var f = all.FirstOrDefault(x => x > 0);
            if (f > 0) return f + FadeOutTime;
        }
        catch (Exception e) { Plugin.L.LogWarning("[knock] ragdoll: " + e.Message); }
        return 10f + FadeOutTime;
    }

    /// <summary>Host, at the round start.</summary>
    void ComputeKnockouts()
    {
        var diff = ReadDifficulty();
        // the original's 120 s restart on Easy kept her down for 135 s: far too long online, she lies 35 s at most
        KnockGun = Mathf.Clamp(RagdollLead(true, diff) + RestartAfterShot[diff], 5f, KnockMax);
        KnockArrow = Mathf.Clamp(RagdollLead(false, diff) + RestartAfterFade[diff], 5f, KnockMax);
        Plugin.L.LogInfo($"[knock] difficulty {diff} ({new[] { "Normal", "Easy", "Hard", "Extreme" }[diff]}): shotgun/car/blast {KnockGun:F0} s, arrow {KnockArrow:F0} s");
    }

    /// <summary>Weapons that "kill" Granny in the original game.</summary>
    static bool IsWeaponKill(byte kind) => kind == 2 || kind == 3 || kind == 5 || kind == 6;

    readonly Dictionary<byte, float> _lastStunAt = new();
}
