using UnityEngine;

namespace GrannyMP;

/// <summary>Mod language: every player-facing string is written as L("рус", "eng").</summary>
public static class Tr
{
    public static bool En;

    public static void Init()
    {
        var v = Cfg.Language.Value;
        En = v == "en" || (v != "ru" && Application.systemLanguage != SystemLanguage.Russian);
    }

    public static void Set(bool en) { En = en; Cfg.Language.Value = en ? "en" : "ru"; }

    public static string L(string ru, string en) => En ? en : ru;

    /// <summary>A text sent over the network: each player sees it in their own language.</summary>
    public static string Both(string ru, string en) => ru + "\u0001" + en;

    public static string Pick(string s)
    {
        var i = s.IndexOf('\u0001');
        return i < 0 ? s : En ? s[(i + 1)..] : s[..i];
    }
}
