using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Bindings;

namespace GrannyMP;

/// <summary>
/// Unity 6 stripped some managed wrappers from this build; Il2CppInterop "unstrips" them, but the
/// restored code marshals strings/arrays through Il2CppSystem.ReadOnlySpan which throws
/// MissingMethodException at runtime. These helpers call the native *_Injected icalls directly
/// with spans built from pinned managed memory.
/// </summary>
public static unsafe class UnityNative
{
    struct Msw { public void* Begin; public int Length; }

    public struct BoneWeight1Raw { public float Weight; public int BoneIndex; }

    static ManagedSpanWrapper Span(void* p, int len)
    {
        if (Unsafe.SizeOf<ManagedSpanWrapper>() == Unsafe.SizeOf<Msw>())
        {
            var m = new Msw { Begin = p, Length = len };
            return Unsafe.As<Msw, ManagedSpanWrapper>(ref m);
        }
        return new ManagedSpanWrapper(p, len);
    }

    public static void SetCurve(AnimationClip clip, string path, Il2CppSystem.Type type, string prop, AnimationCurve curve)
    {
        var self = clip.m_CachedPtr;
        var c = AnimationCurve.BindingsMarshaller.ConvertToNative(curve);
        fixed (char* p1 = path)
        fixed (char* p2 = prop)
        {
            var w1 = Span(p1, path.Length);
            var w2 = Span(p2, prop.Length);
            AnimationClip.SetCurve_Injected(self, ref w1, type, ref w2, c);
        }
    }

    public static void AddClip(Animation anim, AnimationClip clip, string name)
    {
        fixed (char* p = name)
        {
            var w = Span(p, name.Length);
            Animation.AddClip_Injected(anim.m_CachedPtr, clip.m_CachedPtr, ref w, int.MinValue, int.MaxValue, false);
        }
    }

    /// <summary>matrices: 16 floats per matrix, column-major (same memory layout as UnityEngine.Matrix4x4).</summary>
    public static void SetBindposes(Mesh mesh, float[] matrices)
    {
        fixed (float* p = matrices)
        {
            var w = Span(p, matrices.Length / 16);
            Mesh.set_bindposes_Injected(mesh.m_CachedPtr, ref w);
        }
    }

    public static void SetBoneWeights(Mesh mesh, byte[] bonesPerVertex, BoneWeight1Raw[] weights)
    {
        fixed (byte* b = bonesPerVertex)
        fixed (BoneWeight1Raw* w = weights)
        {
            mesh.InternalSetBoneWeights((IntPtr)b, bonesPerVertex.Length, (IntPtr)w, weights.Length);
        }
    }

    /// <summary>Font(string) ctor is stripped: allocate the object and call the native create with an OS font name.</summary>
    public static Font CreateOsFont(string name)
    {
        var ptr = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<Font>.NativeClassPtr);
        var f = new Font(ptr);
        fixed (char* p = name)
        {
            var w = Span(p, name.Length);
            Font.Internal_CreateFont_Injected(f, ref w);
        }
        return f;
    }

    public static Il2CppSystem.Type TransformType => Il2CppType.Of<Transform>();
}
