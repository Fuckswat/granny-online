using UnityEngine;

namespace GrannyMP;

/// <summary>Waiting room: until the round starts everybody walks around here in survivor form. The room itself is only LobbyBuild().</summary>
public sealed partial class Game
{
    GameObject _lobby;
    Vector3 _lobbyCentre;
    bool _lobbyOn, _lobbyFog;
    UnityEngine.Rendering.AmbientMode _lobbyAmbientMode;
    Color _lobbyAmbient;
    float _lobbyAmbientIntensity;

    Material LobbyMaterial(Color color, Texture2D tex, Vector2 tile)
    {
        var m = R.BaseMaterial != null ? new Material(R.BaseMaterial) : new Material(Shader.Find("Standard"));
        m.mainTexture = tex; m.mainTextureScale = tile;
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        foreach (var t in new[] { "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_OcclusionMap", "_ParallaxMap", "_DetailAlbedoMap" })
            if (m.HasProperty(t)) m.SetTexture(t, null);
        foreach (var f in new[] { "_Glossiness", "_GlossMapScale", "_Metallic", "_OcclusionStrength" })
            if (m.HasProperty(f)) m.SetFloat(f, 0f);
        m.DisableKeyword("_EMISSION"); m.DisableKeyword("_METALLICGLOSSMAP");
        return m;
    }

    static Texture2D LobbyTexture(Color a, Color b)
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                tex.SetPixel(x, y, x < 2 || y < 2 ? b : a);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Repeat;
        return tex;
    }

    void LobbyBox(string name, Vector3 centre, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(_lobby.transform, false);
        go.transform.localPosition = centre; go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    /// <summary>A plain cube room with one lamp. Add the furniture here.</summary>
    void LobbyBuild()
    {
        var u = U;
        float w = 14 * u, h = 6 * u, t = 1 * u;
        _lobbyCentre = R.PlayerSpawn + new Vector3(1500f, 60f, 0f);
        _lobby = new GameObject("GrannyMP_Lobby");
        _lobby.transform.position = _lobbyCentre;

        var floor = LobbyMaterial(Color.white, LobbyTexture(new Color(0.36f, 0.34f, 0.32f), new Color(0.17f, 0.16f, 0.15f)), new Vector2(7, 7));
        var wall = LobbyMaterial(Color.white, LobbyTexture(new Color(0.44f, 0.46f, 0.5f), new Color(0.34f, 0.36f, 0.4f)), new Vector2(7, 3));
        var top = LobbyMaterial(new Color(0.3f, 0.3f, 0.32f), LobbyTexture(Color.white, Color.white), Vector2.one);
        LobbyBox("floor", new Vector3(0, -t / 2, 0), new Vector3(w + 2 * t, t, w + 2 * t), floor);
        LobbyBox("ceiling", new Vector3(0, h + t / 2, 0), new Vector3(w + 2 * t, t, w + 2 * t), top);
        LobbyBox("wall N", new Vector3(0, h / 2, w / 2 + t / 2), new Vector3(w + 2 * t, h, t), wall);
        LobbyBox("wall S", new Vector3(0, h / 2, -w / 2 - t / 2), new Vector3(w + 2 * t, h, t), wall);
        LobbyBox("wall E", new Vector3(w / 2 + t / 2, h / 2, 0), new Vector3(t, h, w), wall);
        LobbyBox("wall W", new Vector3(-w / 2 - t / 2, h / 2, 0), new Vector3(t, h, w), wall);
        LobbyBox("lamp", new Vector3(0, h - 0.1f * u, 0), new Vector3(3 * u, 0.2f * u, 3 * u), LobbyMaterial(new Color(4f, 3.8f, 3.4f), LobbyTexture(Color.white, Color.white), Vector2.one));

        var lamp = new GameObject("light");
        lamp.transform.SetParent(_lobby.transform, false);
        lamp.transform.localPosition = new Vector3(0, h - 1.2f * u, 0);
        var l = lamp.AddComponent<Light>();
        l.type = LightType.Point; l.range = 34 * u; l.intensity = 4.5f; l.color = new Color(1f, 0.93f, 0.82f); l.shadows = LightShadows.None;
        l.renderMode = LightRenderMode.ForcePixel;
    }

    Vector3 LobbySpot(byte id)
    {
        var angle = id * 180f + id / 2 * 45f;
        return _lobbyCentre + Quaternion.Euler(0, angle, 0) * Vector3.back * (3.5f * U) + Vector3.up * (0.2f * U);
    }

    void EnterLobby()
    {
        if (_lobby == null) LobbyBuild();
        _lobby.SetActive(true);
        _lobbyFog = RenderSettings.fog;
        _lobbyAmbientMode = RenderSettings.ambientMode; _lobbyAmbient = RenderSettings.ambientLight; _lobbyAmbientIntensity = RenderSettings.ambientIntensity;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
        RenderSettings.ambientIntensity = 1f;
        _lobbyOn = true;
        PlaceInLobby();
        MoveBots(LobbySpot);
        Plugin.L.LogInfo($"[lobby] room at {_lobbyCentre}");
    }

    public void LeaveLobby()
    {
        if (!_lobbyOn) return;
        _lobbyOn = false;
        if (_lobby != null) _lobby.SetActive(false);
        RenderSettings.fog = _lobbyFog;
        RenderSettings.ambientMode = _lobbyAmbientMode; RenderSettings.ambientLight = _lobbyAmbient; RenderSettings.ambientIntensity = _lobbyAmbientIntensity;
    }

    void PlaceInLobby()
    {
        var spot = LobbySpot(Net.LocalId);
        R.Teleport(spot, Quaternion.LookRotation(Flat(_lobbyCentre - spot)).eulerAngles.y);
    }

    void UpdateLobby()
    {
        if (InRound || Executing || R == null || !R.Valid) { if (InRound) LeaveLobby(); return; }
        if (!_lobbyOn) { EnterLobby(); return; }
        // the game's own wake-up can move the player back to the bed
        if (Vector3.SqrMagnitude(R.Feet() - _lobbyCentre) > 144f * U * U) PlaceInLobby();
    }
}
