using UnityEngine;

namespace GrannyMP;

/// <summary>The only injected MonoBehaviour: forwards Unity callbacks to the plain C# <see cref="Game"/>.</summary>
public sealed class MpRuntime : MonoBehaviour
{
    public MpRuntime(IntPtr ptr) : base(ptr) { }

    static readonly Dictionary<string, float> _errAt = new();

    static void Guard(string where, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            var key = where + e.GetType().Name + e.Message;
            if (!_errAt.TryGetValue(key, out var t) || Time.realtimeSinceStartup - t > 10f)
            {
                _errAt[key] = Time.realtimeSinceStartup;
                Plugin.L.LogError($"[{where}] {e}");
            }
        }
    }

    void Update() => Guard("update", Game.I.Update);
    void LateUpdate() => Guard("late", Game.I.LateUpdate);
    void OnGUI() => Guard("gui", Game.I.OnGUI);
    void OnApplicationQuit() => Guard("quit", Game.I.Shutdown);
}
