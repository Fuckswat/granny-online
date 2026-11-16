using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GrannyMP;

public sealed class RemoteAvatar
{
    public GameObject Go;
    Animation _anim;
    SkinnedMeshRenderer _main;
    SkinnedMeshRenderer _xray;
    AudioSource _audio;
    string _cur;
    float _nextStep;
    bool _visible = true, _xrayOn;
    readonly List<Material> _mats = new();

    static Material _xrayMat;
    static bool _xrayTried;

    static readonly Color[] Palette =
    {
        new(0.25f, 0.55f, 1f), new(0.3f, 0.85f, 0.35f), new(1f, 0.8f, 0.2f), new(0.75f, 0.35f, 1f),
        new(0.2f, 0.9f, 0.9f), new(1f, 0.5f, 0.15f), new(0.9f, 0.9f, 0.9f), new(0.55f, 0.35f, 0.2f),
    };

    public static RemoteAvatar Create(GlbAvatar tpl, byte id, float height, Material baseMat)
    {
        var a = new RemoteAvatar();
        a.Go = UnityEngine.Object.Instantiate(tpl.Template);
        a.Go.name = "GrannyMP_Player_" + id;
        a._height = height;
        a.Go.transform.localScale = Vector3.one * (height / tpl.ModelHeight);
        a._anim = a.Go.GetComponent<Animation>();
        a._main = a.Go.GetComponentInChildren<SkinnedMeshRenderer>(true);

        var outfit = Outfit(Palette[id % Palette.Length]);
        var subs = tpl.SubmeshColors ?? new[] { Color.white };
        var mats = new Il2CppReferenceArray<Material>(subs.Length);
        for (var i = 0; i < subs.Length; i++)
        {
            Material m;
            if (baseMat != null) m = new Material(baseMat);
            else m = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse"));
            try { m.mainTexture = outfit; } catch { }
            try { if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.6f, 0.6f, 0.6f)); } catch { }
            try { if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null); } catch { }
            try { if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black); } catch { }
            // Granny's Standard material reflects the light probes: cloth must stay matte or it glows in the dark
            foreach (var prop in new[] { "_Glossiness", "_GlossMapScale", "_Metallic", "_GlossyReflections", "_SpecularHighlights", "_OcclusionStrength" })
                try { if (m.HasProperty(prop)) m.SetFloat(prop, 0f); } catch { }
            foreach (var tex in new[] { "_MetallicGlossMap", "_EmissionMap", "_OcclusionMap", "_ParallaxMap", "_DetailAlbedoMap" })
                try { if (m.HasProperty(tex)) m.SetTexture(tex, null); } catch { }
            try { m.DisableKeyword("_EMISSION"); m.DisableKeyword("_METALLICGLOSSMAP"); m.EnableKeyword("_GLOSSYREFLECTIONS_OFF"); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); } catch { }
            mats[i] = m; a._mats.Add(m);
        }
        a._main.sharedMaterials = mats;

        a.Go.SetActive(true);
        a.CreateXray();
        try
        {
            a._audio = a.Go.AddComponent<AudioSource>();
            a._audio.spatialBlend = 1f;
            a._audio.rolloffMode = AudioRolloffMode.Linear;
            a._audio.minDistance = height * 0.6f;
            a._audio.maxDistance = height * 9f;
            a._audio.volume = 0.8f;
            a._audio.playOnAwake = false;
        }
        catch (Exception e) { Plugin.L.LogWarning("[avatar] audio: " + e.Message); }
        a.Play("idle");
        return a;
    }

    /// <summary>Clothes painted on the (distance from spine, height) map set up by GlbAvatar: jeans, T-shirt, skin, hair.</summary>
    static Texture2D Outfit(Color shirt)
    {
        const int w = 64, h = 256;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        var skin = new Color(0.46f, 0.33f, 0.26f);
        var jeans = new Color(0.10f, 0.13f, 0.21f);
        var hair = new Color(0.07f, 0.05f, 0.04f);
        var shoe = new Color(0.08f, 0.07f, 0.06f);
        var cloth = Color.Lerp(shirt, new Color(0.2f, 0.2f, 0.2f), 0.45f) * 0.55f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var u = x / (w - 1f); var v = y / (h - 1f);
                Color c;
                if (v < 0.05f) c = shoe;
                else if (v < 0.5f) c = jeans;
                else if (v < 0.525f) c = shoe;
                else if (v > 0.93f) c = hair;
                else if (v > 0.82f) c = skin;
                else c = u > 0.42f ? skin : cloth;
                var n = 0.92f + 0.16f * Mathf.PerlinNoise(x * 0.9f, y * 0.35f);
                c *= n; c.a = 1;
                tex.SetPixel(x, y, c);
            }
        tex.Apply();
        return tex;
    }

    public static Material XrayMaterial()
    {
        if (_xrayTried) return _xrayMat;
        _xrayTried = true;
        var pink = new Color(1f, 0.25f, 0.75f, 0.8f);
        try
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                var m = new Material(sh);
                m.SetInt("_ZTest", 8);     // Always: draw through walls
                m.SetInt("_ZWrite", 0);
                m.SetInt("_Cull", 0);
                m.SetInt("_SrcBlend", 5);  // SrcAlpha
                m.SetInt("_DstBlend", 10); // OneMinusSrcAlpha
                m.SetColor("_Color", pink);
                m.renderQueue = 4000;
                _xrayMat = m;
            }
            else
            {
                sh = Shader.Find("UI/Default");
                if (sh != null)
                {
                    var m = new Material(sh);
                    m.SetFloat("unity_GUIZTestMode", 8);
                    m.color = pink;
                    m.renderQueue = 4000;
                    _xrayMat = m;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[avatar] xray shader: " + e.Message); }
        Plugin.L.LogInfo("[avatar] x-ray material: " + (_xrayMat != null ? _xrayMat.shader.name : "НЕТ (будут розовые метки на экране)"));
        return _xrayMat;
    }

    public bool HasXrayMesh => _xray != null;

    void CreateXray()
    {
        var mat = XrayMaterial();
        if (mat == null || _main == null) return;
        try
        {
            var go = new GameObject("xray");
            go.transform.SetParent(_main.transform, false);
            _xray = go.AddComponent<SkinnedMeshRenderer>();
            _xray.sharedMesh = _main.sharedMesh;
            _xray.bones = _main.bones;
            _xray.rootBone = _main.rootBone;
            _xray.updateWhenOffscreen = true;
            var n = _main.sharedMesh.subMeshCount;
            var arr = new Il2CppReferenceArray<Material>(n);
            for (var i = 0; i < n; i++) arr[i] = mat;
            _xray.sharedMaterials = arr;
            _xray.enabled = false;
        }
        catch (Exception e) { Plugin.L.LogWarning("[avatar] xray mesh: " + e.Message); _xray = null; }
    }

    public bool XrayOn => _xrayOn;

    GameObject _heldGo;
    float _height = 4f;
    int _heldIdx = -1;
    Transform _handBone;

    /// <summary>A copy of the item this player holds, in the right hand of the model.</summary>
    public void SetHeld(int idx, GameObject src)
    {
        if (idx == _heldIdx) return;
        _heldIdx = idx;
        if (_heldGo != null) UnityEngine.Object.Destroy(_heldGo);
        _heldGo = null;
        if (src == null || Go == null) return;
        if (_handBone == null)
            foreach (var t in Go.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name.ToLowerInvariant();
                if (n == "def-hand.r" || n == "hand_r" || n == "hand.r") { _handBone = t; break; }
            }
        if (_handBone == null) { Plugin.L.LogWarning("[avatar] no hand bone"); return; }
        _heldGo = UnityEngine.Object.Instantiate(src);
        _heldGo.name = "GrannyMP_Held";
        foreach (var mb in _heldGo.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
        foreach (var a in _heldGo.GetComponentsInChildren<Animation>(true)) a.enabled = false;
        _heldGo.SetActive(true);
        var want = src.transform.lossyScale * 1.6f;
        _heldGo.transform.SetParent(_handBone, false);
        _heldGo.transform.localPosition = Vector3.zero;
        _heldGo.transform.localRotation = Quaternion.identity;
        var b = _handBone.lossyScale;
        _heldGo.transform.localScale = new Vector3(want.x / Mathf.Abs(b.x), want.y / Mathf.Abs(b.y), want.z / Mathf.Abs(b.z));
        // the hand models are built for a camera right next to them: fit the size to the figure instead
        var bounds = new Bounds(); var any = false;
        foreach (var r in _heldGo.GetComponentsInChildren<Renderer>(false))
        {
            r.enabled = true;
            if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
        }
        var longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        var big = src.name.Contains("Shotgun") || src.name.Contains("Armborst");
        if (longest > 0.0001f) _heldGo.transform.localScale *= _height * (big ? 0.55f : 0.2f) / longest;
    }

    public void SetXray(bool on)
    {
        _xrayOn = on;
        if (_xray != null && _xray.enabled != on) _xray.enabled = on;
        if (Go != null && on && !Go.activeSelf) Go.SetActive(true);
        if (Go != null && !on && !_visible && Go.activeSelf) Go.SetActive(false);
    }

    public void SetVisible(bool v)
    {
        _visible = v;
        if (_main != null && _main.enabled != v) _main.enabled = v;
        var wantActive = v || _xrayOn;
        if (Go != null && Go.activeSelf != wantActive) Go.SetActive(wantActive);
    }

    public void Play(string clip)
    {
        if (_anim == null || clip == null || clip == _cur) return;
        try { _anim.CrossFade(clip, 0.2f); _cur = clip; } catch { }
    }

    /// <summary>A one-shot clip from its first frame, even if it was the last one played.</summary>
    public void PlayFromStart(string clip)
    {
        if (_anim == null || clip == null) return;
        try { _anim.Stop(); } catch { } // Rewind(string) is span-broken in this Unity build; Stop() rewinds everything
        try { _anim.CrossFade(clip, 0.08f); _cur = clip; } catch { }
    }

    public void SetShadows(bool on)
    {
        try { if (_main != null) _main.shadowCastingMode = on ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off; } catch { }
    }

    public void Place(Vector3 feet, float yaw)
    {
        Go.transform.position = feet;
        Go.transform.rotation = Quaternion.Euler(0, yaw + Cfg.AvatarYawOffset.Value, 0);
    }

    public void Step(AudioClip[] clips, float interval)
    {
        if (_audio == null || clips == null || clips.Length == 0 || interval <= 0 || !Cfg.AvatarFootsteps.Value) return;
        if (Time.time < _nextStep) return;
        _nextStep = Time.time + interval;
        try { _audio.PlayOneShot(clips[UnityEngine.Random.Range(0, clips.Length)], 0.9f); } catch { }
    }

    public void Destroy()
    {
        if (Go != null) UnityEngine.Object.Destroy(Go);
        foreach (var m in _mats) if (m != null) UnityEngine.Object.Destroy(m);
        Go = null;
    }
}

public sealed partial class Game
{
    GlbAvatar _avatarTpl;
    bool _avatarTried;

    void EnsureAvatarTemplate()
    {
        if (_avatarTried) return;
        _avatarTried = true;
        var path = Path.Combine(Plugin.PluginDir, "mannequin.glb");
        try
        {
            if (!File.Exists(path)) { Plugin.L.LogError("[avatar] нет файла " + path); return; }
            _avatarTpl = GlbAvatar.Load(path);
        }
        catch (Exception e) { Plugin.L.LogError("[avatar] загрузка модели не удалась: " + e); _avatarTpl = null; }
    }

    List<GameObject> _heldItems;

    /// <summary>The models the game shows in the player's hand; the same sorted list on every machine.</summary>
    List<GameObject> HeldItems()
    {
        if (_heldItems != null) return _heldItems;
        var list = new List<GameObject>();
        var pu = R.Player.GetComponentInChildren<PickUp>(true);
        foreach (var prop in pu.GetType().GetProperties())
        {
            if (prop.PropertyType != typeof(GameObject) || !prop.CanRead) continue;
            GameObject g = null;
            try { g = prop.GetValue(pu) as GameObject; } catch { }
            if (g == null || !g.name.Contains("Hand") || list.Contains(g)) continue;
            if (g.name.StartsWith("Lantern") || g.name.StartsWith("Flashlight") || g.name.StartsWith("Ficklampa") || g.name.StartsWith("Bloody")) continue;
            if (g.GetComponentInChildren<Renderer>(true) != null) list.Add(g);
        }
        return _heldItems = list.OrderBy(g => g.name).ToList();
    }

    int _devHeld = -1;

    sbyte MyHeldItem()
    {
        if (_devHeld >= 0) return (sbyte)_devHeld;
        var l = HeldItems();
        for (var i = 0; i < l.Count && i < 127; i++) if (l[i] != null && l[i].activeInHierarchy) return (sbyte)i;
        return -1;
    }

    float AvatarHeight => Cfg.AvatarHeight.Value > 0.01f ? Cfg.AvatarHeight.Value : (R?.EyeHeight ?? 2.8f) * 1.08f;

    void UpdateAvatars()
    {
        foreach (var p in Players.Values)
        {
            if (p.Id == Net.LocalId) continue;
            var stale = !p.HasState || Now - p.LastSeen > 3f;
            var show = !stale && p.InHouse && !p.Spectating && !(InRound && (p.Role == Role.Granny || !p.Alive || p.Escaped));
            var hidden = p.OutOfSight; // under a bed / in a coffin / in a closed wardrobe: the model must not stick out
            if (!show) { p.Avatar?.SetVisible(false); p.Avatar?.SetXray(false); continue; }

            if (p.Avatar == null || p.Avatar.Go == null)
            {
                if (_avatarTpl?.Template == null) continue;
                try { p.Avatar = RemoteAvatar.Create(_avatarTpl, p.Id, AvatarHeight, R?.BaseMaterial); }
                catch (Exception e) { Plugin.L.LogError("[avatar] create: " + e); _avatarTpl = null; continue; }
            }

            var k = Mathf.Clamp01(Time.deltaTime * 12f);
            p.SmoothPos = Vector3.Lerp(p.SmoothPos, p.Pos, k);
            p.SmoothYaw = Mathf.LerpAngle(p.SmoothYaw, p.Yaw, k);
            p.Avatar.Place(p.SmoothPos, p.SmoothYaw);
            p.Avatar.SetVisible(!hidden && !DownWatching(p)); // knocked-out Granny looks through his eyes: his own head would fill the screen
            p.Avatar.SetHeld(p.Held, p.Held >= 0 && p.Held < HeldItems().Count ? HeldItems()[p.Held] : null);
            if (Now >= p.NextLosCheck)
            {
                p.NextLosCheck = Now + 0.2f;
                var head = p.SmoothPos + Vector3.up * (AvatarHeight * 0.9f);
                var dir = head - R.CamPos;
                p.NameVisible = !Physics.Raycast(R.CamPos, dir.normalized, dir.magnitude - 0.3f * U, ~0, QueryTriggerInteraction.Ignore);
            }

            // a punch plays over everything else, from its first frame
            var punching = p.Punching && !hidden && _avatarTpl.Clips.Contains("punch");
            if (punching && !p.WasPunching) p.Avatar.PlayFromStart("punch");
            p.WasPunching = punching;
            if (punching) continue;

            var walk = R?.BaseWalk ?? 4f; var run = R?.BaseRun ?? 7f;
            string clip; float step;
            if (p.Crouch) { clip = p.Speed > walk * 0.15f ? "crouchWalk" : "crouchIdle"; step = 0; }
            else if (p.Speed < walk * 0.2f) { clip = "idle"; step = 0; }
            else if (p.Speed < (walk + run) * 0.5f) { clip = "walk"; step = 0.5f; }
            else if (p.Boost) { clip = "sprint"; step = 0.24f; }
            else { clip = "run"; step = 0.32f; }
            if (!_avatarTpl.Clips.Contains(clip)) clip = clip == "sprint" ? "run" : clip.StartsWith("crouch") ? (clip == "crouchWalk" ? "walk" : "idle") : "idle";
            if (!_avatarTpl.Clips.Contains(clip)) clip = "idle";
            p.Avatar.Play(clip);
            if (!hidden && step > 0) p.Avatar.Step(R?.StepClips, step);
        }
    }
}
