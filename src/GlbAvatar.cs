using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GrannyMP;

/// <summary>
/// Minimal runtime glTF 2.0 (.glb) loader: one skinned mesh + legacy AnimationClips.
/// glTF is right-handed, Unity left-handed: X is mirrored (pos.x, quat.y/z negated, winding flipped).
/// </summary>
public sealed class GlbAvatar
{
    public GameObject Template;
    public float ModelHeight = 1.8f;
    public readonly HashSet<string> Clips = new();

    // role -> candidate animation names in the glb (first match wins)
    static readonly (string role, string[] names, bool loop)[] Wanted =
    {
        ("idle", new[] { "Idle_Loop", "Idle" }, true),
        ("walk", new[] { "Walk_Loop", "Walk" }, true),
        ("run", new[] { "Jog_Fwd_Loop", "Sprint_Loop", "Run" }, true),
        ("crouchIdle", new[] { "Crouch_Idle_Loop" }, true),
        ("crouchWalk", new[] { "Crouch_Fwd_Loop" }, true),
        ("hit", new[] { "Hit_Chest", "Hit_Head" }, false),
        ("death", new[] { "Death01" }, false),
        ("interact", new[] { "Interact", "PickUp_Table" }, false),
        ("sprint", new[] { "Sprint_Loop", "Jog_Fwd_Loop" }, true),
        ("punch", new[] { "Punch_Cross", "Punch_Jab" }, false),
    };

    byte[] _bytes; int _bin; JsonElement _root;
    JsonElement[] _nodes, _accessors, _views;

    public static GlbAvatar Load(string path)
    {
        var g = new GlbAvatar();
        g.Build(path);
        return g;
    }

    void Build(string path)
    {
        _bytes = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(_bytes, 0) != 0x46546C67) throw new Exception("not a .glb file");
        var jsonLen = BitConverter.ToInt32(_bytes, 12);
        var json = Encoding.UTF8.GetString(_bytes, 20, jsonLen);
        _bin = 20 + jsonLen + 8;
        using var doc = JsonDocument.Parse(json);
        _root = doc.RootElement.Clone();
        _nodes = Arr("nodes"); _accessors = Arr("accessors"); _views = Arr("bufferViews");

        var wrapper = new GameObject("GrannyMP_AvatarTemplate");
        wrapper.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(wrapper);

        var goByNode = new GameObject[_nodes.Length];
        var pathByNode = new string[_nodes.Length];
        var parentOf = new int[_nodes.Length];
        for (var i = 0; i < parentOf.Length; i++) parentOf[i] = -1;
        for (var i = 0; i < _nodes.Length; i++)
            if (_nodes[i].TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray()) parentOf[c.GetInt32()] = i;

        var scene = _root.GetProperty("scenes")[_root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0];
        foreach (var r in scene.GetProperty("nodes").EnumerateArray())
            BuildNode(r.GetInt32(), wrapper.transform, "", goByNode, pathByNode);

        // ---- skinned mesh ----
        var meshNode = -1;
        for (var i = 0; i < _nodes.Length; i++)
            if (_nodes[i].TryGetProperty("mesh", out _) && _nodes[i].TryGetProperty("skin", out _)) { meshNode = i; break; }
        if (meshNode < 0) throw new Exception("no skinned mesh in glb");

        var skin = Arr("skins")[_nodes[meshNode].GetProperty("skin").GetInt32()];
        var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
        var ibm = ReadFloats(skin.GetProperty("inverseBindMatrices").GetInt32(), out _);
        for (var m = 0; m < joints.Length; m++)
            for (var c = 0; c < 4; c++)
                for (var r = 0; r < 4; r++)
                    if ((r == 0) ^ (c == 0)) ibm[m * 16 + c * 4 + r] = -ibm[m * 16 + c * 4 + r];

        var meshDef = Arr("meshes")[_nodes[meshNode].GetProperty("mesh").GetInt32()];
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
        var bpv = new List<byte>(); var bws = new List<UnityNative.BoneWeight1Raw>();
        var subTris = new List<int[]>(); var subColors = new List<Color>();
        foreach (var prim in meshDef.GetProperty("primitives").EnumerateArray())
        {
            var at = prim.GetProperty("attributes");
            var baseV = verts.Count;
            var p = ReadFloats(at.GetProperty("POSITION").GetInt32(), out _);
            var n = at.TryGetProperty("NORMAL", out var na) ? ReadFloats(na.GetInt32(), out _) : null;
            var t = at.TryGetProperty("TEXCOORD_0", out var ta) ? ReadFloats(ta.GetInt32(), out _) : null;
            var j = ReadInts(at.GetProperty("JOINTS_0").GetInt32(), out _);
            var w = ReadFloats(at.GetProperty("WEIGHTS_0").GetInt32(), out _);
            var count = p.Length / 3;
            for (var v = 0; v < count; v++)
            {
                verts.Add(new Vector3(-p[v * 3], p[v * 3 + 1], p[v * 3 + 2]));
                norms.Add(n != null ? new Vector3(-n[v * 3], n[v * 3 + 1], n[v * 3 + 2]) : Vector3.up);
                uvs.Add(t != null ? new Vector2(t[v * 2], 1f - t[v * 2 + 1]) : Vector2.zero);
                var infl = new List<UnityNative.BoneWeight1Raw>(4);
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var wt = w[v * 4 + k];
                    if (wt > 0.0001f) { infl.Add(new UnityNative.BoneWeight1Raw { Weight = wt, BoneIndex = j[v * 4 + k] }); sum += wt; }
                }
                if (infl.Count == 0) { infl.Add(new UnityNative.BoneWeight1Raw { Weight = 1, BoneIndex = 0 }); sum = 1; }
                infl.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                for (var k = 0; k < infl.Count; k++) { var x = infl[k]; x.Weight /= sum; bws.Add(x); }
                bpv.Add((byte)infl.Count);
            }
            var idx = prim.TryGetProperty("indices", out var ia) ? ReadInts(ia.GetInt32(), out _) : Enumerable.Range(0, count).ToArray();
            var tris = new int[idx.Length];
            for (var k = 0; k + 2 < idx.Length; k += 3)
            {
                tris[k] = baseV + idx[k]; tris[k + 1] = baseV + idx[k + 2]; tris[k + 2] = baseV + idx[k + 1];
            }
            subTris.Add(tris);
            subColors.Add(MaterialColor(prim));
        }

        // the mannequin has no usable UVs: map (distance from the spine, height) onto the outfit texture
        var minY = verts.Min(v => v.y); var maxY = verts.Max(v => v.y); var maxX = verts.Max(v => Mathf.Abs(v.x));
        for (var v = 0; v < verts.Count; v++)
            uvs[v] = new Vector2(Mathf.Abs(verts[v].x) / maxX, (verts[v].y - minY) / (maxY - minY));

        var mesh = new Mesh { name = "GrannyMP_Mannequin" };
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts.ToArray();
        mesh.normals = norms.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.subMeshCount = subTris.Count;
        for (var i = 0; i < subTris.Count; i++)
        {
            var list = new Il2CppSystem.Collections.Generic.List<int>(subTris[i].Length);
            foreach (var x in subTris[i]) list.Add(x);
            mesh.SetTriangles(list, i);
        }
        UnityNative.SetBoneWeights(mesh, bpv.ToArray(), bws.ToArray());
        UnityNative.SetBindposes(mesh, ibm);
        mesh.RecalculateBounds();

        var meshGo = goByNode[meshNode];
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        var bones = new Il2CppReferenceArray<Transform>(joints.Length);
        for (var i = 0; i < joints.Length; i++) bones[i] = goByNode[joints[i]].transform;
        smr.bones = bones;
        var rootJoint = joints.FirstOrDefault(jn => !joints.Contains(parentOf[jn]));
        smr.rootBone = goByNode[rootJoint].transform;
        smr.updateWhenOffscreen = true;

        // model height in bind pose (mesh space * mesh node world scale)
        var b = mesh.bounds;
        var scale = 1f;
        for (var t = meshGo.transform; t != null && t != wrapper.transform; t = t.parent) scale *= t.localScale.y;
        ModelHeight = Mathf.Max(0.1f, b.size.y * scale);
        // put the feet on the template origin
        var footOffset = -b.min.y * scale;
        if (Mathf.Abs(footOffset) > 0.01f)
            for (var i = 0; i < wrapper.transform.childCount; i++)
                wrapper.transform.GetChild(i).localPosition += Vector3.up * footOffset;
        Plugin.L.LogInfo($"[avatar] mesh {verts.Count} verts, {joints.Length} bones, {subTris.Count} submeshes, height {ModelHeight:F2}, foot offset {footOffset:F2}");

        // ---- animations (legacy clips) ----
        var anim = wrapper.AddComponent<Animation>();
        var anims = Arr("animations");
        foreach (var (role, names, loop) in Wanted)
        {
            JsonElement? found = null;
            foreach (var nm in names)
            {
                foreach (var a in anims)
                    if (a.TryGetProperty("name", out var an) && an.GetString() == nm) { found = a; break; }
                if (found != null) break;
            }
            if (found == null) { Plugin.L.LogWarning($"[avatar] no clip for '{role}'"); continue; }
            try
            {
                var clip = BuildClip(found.Value, pathByNode, loop);
                clip.name = role;
                UnityNative.AddClip(anim, clip, role);
                Clips.Add(role);
            }
            catch (Exception e) { Plugin.L.LogError($"[avatar] clip '{role}' failed: {e}"); }
        }
        anim.playAutomatically = false;
        anim.cullingType = AnimationCullingType.AlwaysAnimate;
        Plugin.L.LogInfo($"[avatar] clips: {string.Join(", ", Clips)}");

        // store base colours for tinting
        _subColors = subColors.ToArray();
        Template = wrapper;
    }

    Color[] _subColors;
    public Color[] SubmeshColors => _subColors;

    void BuildNode(int i, Transform parent, string parentPath, GameObject[] goByNode, string[] pathByNode)
    {
        var nd = _nodes[i];
        var name = nd.TryGetProperty("name", out var nn) ? nn.GetString() : "node" + i;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Vector3 t = Vector3.zero, sc = Vector3.one; var q = Quaternion.identity;
        if (nd.TryGetProperty("translation", out var tr)) t = new Vector3(-tr[0].GetSingle(), tr[1].GetSingle(), tr[2].GetSingle());
        if (nd.TryGetProperty("rotation", out var ro)) q = new Quaternion(ro[0].GetSingle(), -ro[1].GetSingle(), -ro[2].GetSingle(), ro[3].GetSingle());
        if (nd.TryGetProperty("scale", out var sca)) sc = new Vector3(sca[0].GetSingle(), sca[1].GetSingle(), sca[2].GetSingle());
        if (nd.TryGetProperty("matrix", out var mx))
        {
            var m = mx.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            t = new Vector3(-m[12], m[13], m[14]);
            var cx = new Vector3(m[0], m[1], m[2]); var cy = new Vector3(m[4], m[5], m[6]); var cz = new Vector3(m[8], m[9], m[10]);
            sc = new Vector3(cx.magnitude, cy.magnitude, cz.magnitude);
            var fwd = new Vector3(-cz.x, cz.y, cz.z) / sc.z; var up = new Vector3(-cy.x, cy.y, cy.z) / sc.y;
            q = Quaternion.LookRotation(fwd, up);
        }
        go.transform.localPosition = t; go.transform.localRotation = q; go.transform.localScale = sc;
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        goByNode[i] = go; pathByNode[i] = path;
        if (nd.TryGetProperty("children", out var ch))
            foreach (var c in ch.EnumerateArray()) BuildNode(c.GetInt32(), go.transform, path, goByNode, pathByNode);
    }

    AnimationClip BuildClip(JsonElement a, string[] pathByNode, bool loop)
    {
        var clip = NewClip();
        clip.legacy = true;
        clip.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
        var samplers = a.GetProperty("samplers").EnumerateArray().ToArray();
        var ttype = UnityNative.TransformType;
        foreach (var ch in a.GetProperty("channels").EnumerateArray())
        {
            var target = ch.GetProperty("target");
            if (!target.TryGetProperty("node", out var nodeEl)) continue;
            var node = nodeEl.GetInt32();
            var path = pathByNode[node];
            if (path == null) continue;
            var prop = target.GetProperty("path").GetString();
            var smp = samplers[ch.GetProperty("sampler").GetInt32()];
            var interp = smp.TryGetProperty("interpolation", out var ie) ? ie.GetString() : "LINEAR";
            var times = ReadFloats(smp.GetProperty("input").GetInt32(), out _);
            var vals = ReadFloats(smp.GetProperty("output").GetInt32(), out var comps);
            var cubic = interp == "CUBICSPLINE";
            var step = interp == "STEP";
            var n = times.Length;
            float V(int key, int comp) => cubic ? vals[(key * 3 + 1) * comps + comp] : vals[key * comps + comp];

            if (prop == "translation")
            {
                Emit(clip, path, ttype, "localPosition.x", times, k => -V(k, 0), step);
                Emit(clip, path, ttype, "localPosition.y", times, k => V(k, 1), step);
                Emit(clip, path, ttype, "localPosition.z", times, k => V(k, 2), step);
            }
            else if (prop == "rotation")
            {
                var qs = new Quaternion[n];
                for (var k = 0; k < n; k++)
                {
                    var q = new Quaternion(V(k, 0), -V(k, 1), -V(k, 2), V(k, 3));
                    if (k > 0 && Quaternion.Dot(qs[k - 1], q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    qs[k] = q;
                }
                Emit(clip, path, ttype, "localRotation.x", times, k => qs[k].x, step);
                Emit(clip, path, ttype, "localRotation.y", times, k => qs[k].y, step);
                Emit(clip, path, ttype, "localRotation.z", times, k => qs[k].z, step);
                Emit(clip, path, ttype, "localRotation.w", times, k => qs[k].w, step);
            }
            else if (prop == "scale")
            {
                var constOne = true;
                for (var k = 0; k < n && constOne; k++)
                    for (var c = 0; c < 3; c++) if (Mathf.Abs(V(k, c) - 1f) > 0.001f) { constOne = false; break; }
                if (constOne) continue;
                Emit(clip, path, ttype, "localScale.x", times, k => V(k, 0), step);
                Emit(clip, path, ttype, "localScale.y", times, k => V(k, 1), step);
                Emit(clip, path, ttype, "localScale.z", times, k => V(k, 2), step);
            }
        }
        return clip;
    }

    /// <summary>The parameterless AnimationClip ctor is stripped from this build: allocate the IL2CPP object and run the native create icall.</summary>
    static AnimationClip NewClip()
    {
        var ptr = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(Il2CppInterop.Runtime.Il2CppClassPointerStore<AnimationClip>.NativeClassPtr);
        var clip = new AnimationClip(ptr);
        AnimationClip.Internal_CreateAnimationClip(clip);
        return clip;
    }

    static void Emit(AnimationClip clip, string path, Il2CppSystem.Type type, string prop, float[] t, Func<int, float> v, bool step)
    {
        var n = t.Length;
        var keys = new Il2CppStructArray<Keyframe>(n);
        for (var k = 0; k < n; k++)
        {
            float inT, outT;
            if (step) { inT = float.PositiveInfinity; outT = float.PositiveInfinity; }
            else
            {
                inT = k > 0 ? (v(k) - v(k - 1)) / Mathf.Max(1e-5f, t[k] - t[k - 1]) : 0f;
                outT = k < n - 1 ? (v(k + 1) - v(k)) / Mathf.Max(1e-5f, t[k + 1] - t[k]) : inT;
                if (k == 0) inT = outT;
            }
            keys[k] = new Keyframe(t[k], v(k), inT, outT);
        }
        UnityNative.SetCurve(clip, path, type, prop, new AnimationCurve(keys));
    }

    Color MaterialColor(JsonElement prim)
    {
        try
        {
            if (!prim.TryGetProperty("material", out var mi)) return Color.white;
            var mat = Arr("materials")[mi.GetInt32()];
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var bc))
                return new Color(bc[0].GetSingle(), bc[1].GetSingle(), bc[2].GetSingle(), 1f);
        }
        catch { }
        return Color.white;
    }

    JsonElement[] Arr(string name) => _root.TryGetProperty(name, out var a) ? a.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    static int Comps(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, "MAT3" => 9, _ => 1 };
    static int Size(int ct) => ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };

    float[] ReadFloats(int accIdx, out int comps)
    {
        var acc = _accessors[accIdx];
        comps = Comps(acc.GetProperty("type").GetString());
        var ct = acc.GetProperty("componentType").GetInt32();
        var count = acc.GetProperty("count").GetInt32();
        var norm = acc.TryGetProperty("normalized", out var ne) && ne.GetBoolean();
        var res = new float[count * comps];
        if (!acc.TryGetProperty("bufferView", out var bvi)) return res;
        var bv = _views[bvi.GetInt32()];
        var off = _bin + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : comps * Size(ct);
        for (var i = 0; i < count; i++)
            for (var c = 0; c < comps; c++)
            {
                var p = off + i * stride + c * Size(ct);
                res[i * comps + c] = ct switch
                {
                    5126 => BitConverter.ToSingle(_bytes, p),
                    5121 => norm ? _bytes[p] / 255f : _bytes[p],
                    5123 => norm ? BitConverter.ToUInt16(_bytes, p) / 65535f : BitConverter.ToUInt16(_bytes, p),
                    5120 => norm ? Math.Max((sbyte)_bytes[p] / 127f, -1f) : (sbyte)_bytes[p],
                    5122 => norm ? Math.Max(BitConverter.ToInt16(_bytes, p) / 32767f, -1f) : BitConverter.ToInt16(_bytes, p),
                    5125 => BitConverter.ToUInt32(_bytes, p),
                    _ => 0f,
                };
            }
        return res;
    }

    int[] ReadInts(int accIdx, out int comps)
    {
        var acc = _accessors[accIdx];
        comps = Comps(acc.GetProperty("type").GetString());
        var ct = acc.GetProperty("componentType").GetInt32();
        var count = acc.GetProperty("count").GetInt32();
        var res = new int[count * comps];
        var bv = _views[acc.GetProperty("bufferView").GetInt32()];
        var off = _bin + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : comps * Size(ct);
        for (var i = 0; i < count; i++)
            for (var c = 0; c < comps; c++)
            {
                var p = off + i * stride + c * Size(ct);
                res[i * comps + c] = ct switch
                {
                    5121 => _bytes[p],
                    5123 => BitConverter.ToUInt16(_bytes, p),
                    5125 => (int)BitConverter.ToUInt32(_bytes, p),
                    5120 => (sbyte)_bytes[p],
                    5122 => BitConverter.ToInt16(_bytes, p),
                    _ => 0,
                };
            }
        return res;
    }
}
