// SimdGenerator â€?Roslyn incremental source generator.
//
// Emits the transposed (x86-simd-sort / JDK libsimdsort) AVX-512 network for the
// supported element types. The emission logic is written ONCE against a Target
// descriptor; everything element-type/ISA specific (vector type, min/max, mask
// construction, swizzle expressions, padding, lane count) lives in the Target
// instances below. Adding a type = adding one Target (plus one dispatch line in
// SimdNetwork<T>.TrySort's caller) â€?no emitter changes.
//
// Algorithm (per target, nv = 2/4/8 vectors of `Lanes` elements):
//   1. column sort: a sorting network whose comparators are WHOLE-VECTOR min/max
//      (no lane masks â€?cheaper than the row-wise design, whose every step needs a
//      keep-max mask);
//   2. merge_n_vec: for scale = 2, 4, ..., MaxScale:
//        - cross-vector substep: reverse the upper half's vectors with
//          reverse_n<scale>, COEX pairs (i, n-1-i), recurse on both halves;
//        - within-vector merge per vector: reverse_n<scale> + COEX + merge_n<scale>,
//          then swap_n<s> + COEX + merge_n<s> for s = scale/2 .. 2.
//      merge_n is a masked blend with k = ((i % scale) < scale/2) â€?verified against
//      avx512-32bit-qsort.hpp; all swizzles use immediate-control instructions.
// One straight-line method per nv (the .NET ABI passes Vector512 through memory, so
// no helper call may carry a vector argument). The caller caps the generated path at
// nv <= 4: at nv = 8 the eight simultaneously-live vectors make RyuJIT spill.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Sorts.Experiments.Generators;

/// <summary>Everything element-type/ISA specific about one generated network.</summary>
internal sealed class Target
{
    public string ClassName = "";
    public string MethodSuffix = "";     // SortAvx512_<suffix>
    public string ElemType = "";         // "int"
    public string VecType = "";          // "Vector512<int>"
    public string VecStatic = "";        // "Vector512" (non-generic statics: LoadUnsafe/Create)
    public string PtrType = "";          // "int"
    public string Isa = "";              // "Avx512F"
    public int Lanes;                    // 16 (Vector512<T>.Count)
    public int MaxScale;                 // 16 (== Lanes)
    public string PadExpr = "";          // "Vector512.Create(int.MaxValue)"
    public string MakeFuncType = "";     // "Func<int, int>"
    public string MaskOn = "";           // "~0" / "-1"
    public string MaskOff = "";          // "0" / "0u"

    /// <summary>(a, b) =&gt; "Vector512.Min(a, b)" (or the raw hardware form).</summary>
    public Func<string, string, string> Min = null!;
    public Func<string, string, string> Max = null!;

    /// <summary>(v, scale) =&gt; reverse_n&lt;scale&gt; expression for scale in {2,4,8}.</summary>
    public Func<string, int, string> RevExpr = null!;

    /// <summary>(v) =&gt; reverse within each 8-lane block (the first half of reverse_n&lt;16&gt;).</summary>
    public Func<string, string> Rev8Expr = null!;

    /// <summary>(v) =&gt; swap the two Lanes/2 halves (the second half of reverse_n&lt;16&gt;).</summary>
    public Func<string, string> SwapHalvesExpr = null!;

    /// <summary>(v, scale) =&gt; swap_n&lt;scale&gt; expression for scale in {2,4,8}.</summary>
    public Func<string, int, string> SwapExpr = null!;

    /// <summary>(reg, other, scale) =&gt; merge_n&lt;scale&gt; expression.</summary>
    public Func<string, string, int, string> MergeNExpr = null!;
}

[Generator(LanguageNames.CSharp)]
public sealed class SimdGenerator : IIncrementalGenerator
{
    // Comparator networks over vectors (verified by the 0-1 principle below).
    private static readonly int[][] Net2 = { new[] { 0, 1 } };

    private static readonly int[][] Net4 =
    {
        new[] { 0, 1 }, new[] { 2, 3 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 1, 2 },
    };

    private static readonly int[][] Net8 =
    {
        new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 },
        new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 },
        new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 },
        new[] { 2, 4 }, new[] { 3, 5 },
        new[] { 1, 4 }, new[] { 3, 6 },
        new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5, 6 },
    };

    // ---- targets ------------------------------------------------------------
    private static Target MakeIntLike(string className, string methodSuffix, string elem, string on, string off, string toElem) => new()
    {
        ClassName = className,
        MethodSuffix = methodSuffix,
        ElemType = elem,
        VecType = $"Vector512<{elem}>",
        VecStatic = "Vector512",
        PtrType = elem,
        Isa = "Avx512F",
        Lanes = 16,
        MaxScale = 16,
        PadExpr = $"Vector512.Create({elem}.MaxValue)",
        MakeFuncType = $"Func<int, {elem}>",
        MaskOn = on,
        MaskOff = off,
        Min = (a, b) => $"Vector512.Min({a}, {b})",
        Max = (a, b) => $"Vector512.Max({a}, {b})",
        RevExpr = (v, scale) => scale switch
        {
            2 => $"Avx512F.Shuffle({v}, 0xB1)",
            4 => $"Avx512F.Shuffle({v}, 0x1B)",
            8 => $"Avx512F.Permute4x64(Avx512F.Shuffle({v}, 0xB1).AsInt64(), 0x1B).{toElem}()",
            _ => throw new InvalidOperationException($"rev {scale}"),
        },
        Rev8Expr = v => $"Avx512F.Permute4x64(Avx512F.Shuffle({v}, 0xB1).AsInt64(), 0x1B).{toElem}()",
        SwapHalvesExpr = v => $"Avx512F.AlignRight32({v}, {v}, 8)",
        SwapExpr = (v, scale) => scale switch
        {
            2 => $"Avx512F.Shuffle({v}, 0xB1)",
            4 => $"Avx512F.Shuffle({v}, 0x4E)",
            8 => $"Avx512F.Permute4x64({v}.AsInt64(), 0x4E).{toElem}()",
            _ => throw new InvalidOperationException($"swap {scale}"),
        },
        MergeNExpr = (reg, other, scale) => $"Avx512F.BlendVariable({reg}, {other}, Mask{scale})",
    };

    private static readonly Target[] Targets =
    {
        MakeIntLike("SimdNetworkGenerated", "Int", "int", "-1", "0", "AsInt32"),
        MakeIntLike("SimdNetworkGeneratedUInt", "UInt", "uint", "~0u", "0u", "AsUInt32"),
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            var text = Emit();
            VerifySyntax(text);
            ctx.AddSource("SimdNetworkGenerated.g.cs", SourceText.From(text, Encoding.UTF8));
        });
    }

    /// <summary>Parse the emitted text before handing it to the compiler: a typo in the
    /// emitter then surfaces as a generator failure with the offending line, instead of
    /// as a confusing error inside the consumer's generated file.</summary>
    private static void VerifySyntax(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "SimdGenerator emitted invalid C#: " + string.Join("; ", errors.Select(e => e.ToString())));
        }
    }

    internal static string Emit()
    {
        VerifyNetworks();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by Sorts.Experiments.Generators.SimdGenerator. Do not edit by hand.");
        sb.AppendLine("// Transposed network (column sort + scale merge), AVX-512, nv = 2/4/8.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using System.Runtime.Intrinsics;");
        sb.AppendLine("using System.Runtime.Intrinsics.X86;");
        sb.AppendLine();
        sb.AppendLine("namespace Sorts.Experiments.Bcl;");
        sb.AppendLine();

        foreach (var t in Targets)
        {
            EmitTarget(sb, t);
        }

        return sb.ToString();
    }

    private static void EmitTarget(StringBuilder sb, Target t)
    {
        sb.AppendLine($"internal static class {t.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>merge_n&lt;scale&gt; blend masks: lane i takes the second operand when (i % scale) &lt; scale/2.</summary>");
        for (int scale = 2; scale <= t.MaxScale; scale *= 2)
        {
            sb.AppendLine($"    private static readonly {t.VecType} Mask{scale} = Make(i => (i % {scale}) < {scale / 2} ? {t.MaskOn} : {t.MaskOff});");
        }
        sb.AppendLine($"    private static readonly {t.VecType} LaneIdx = Make(i => ({t.ElemType})i);");
        sb.AppendLine($"    private static readonly {t.VecType} Pad = {t.PadExpr};");
        sb.AppendLine();

        sb.AppendLine("    [MethodImpl(MethodImplOptions.NoInlining)]");
        sb.AppendLine("    [SkipLocalsInit]");
        sb.AppendLine($"    internal static unsafe void SortAvx512_{t.MethodSuffix}(ref {t.ElemType} p, int len, int nv)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (nv)");
        sb.AppendLine("        {");
        sb.AppendLine("            case 2: Sort2(ref p, len); return;");
        sb.AppendLine("            case 4: Sort4(ref p, len); return;");
        sb.AppendLine("            default: Sort8(ref p, len); return;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitSort(sb, t, 2, Net2);
        EmitSort(sb, t, 4, Net4);
        EmitSort(sb, t, 8, Net8);

        sb.AppendLine($"    private static {t.VecType} Make({t.MakeFuncType} f)");
        sb.AppendLine("    {");
        sb.AppendLine($"        {t.ElemType}[] a = new {t.ElemType}[{t.Lanes}];");
        sb.AppendLine($"        for (int i = 0; i < {t.Lanes}; i++) {{ a[i] = f(i); }}");
        sb.AppendLine($"        ref {t.ElemType} r = ref MemoryMarshal.GetArrayDataReference(a);");
        sb.AppendLine($"        return {t.VecStatic}.LoadUnsafe(ref r);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // Self-check the networks on all binary inputs (0-1 principle).
    private static void VerifyNetworks()
    {
        foreach (var (net, n) in new[] { (Net2, 2), (Net4, 4), (Net8, 8) })
        {
            for (int mask = 0; mask < (1 << n); mask++)
            {
                var a = new int[n];
                for (int i = 0; i < n; i++)
                {
                    a[i] = (mask >> i) & 1;
                }
                foreach (var c in net)
                {
                    if (a[c[0]] > a[c[1]])
                    {
                        var tmp = a[c[0]];
                        a[c[0]] = a[c[1]];
                        a[c[1]] = tmp;
                    }
                }
                for (int i = 1; i < n; i++)
                {
                    if (a[i - 1] > a[i])
                    {
                        throw new InvalidOperationException($"sorting network n={n} failed on mask {mask}");
                    }
                }
            }
        }
    }

    private static void EmitSort(StringBuilder sb, Target t, int nv, int[][] net)
    {
        sb.AppendLine("    [MethodImpl(MethodImplOptions.NoInlining)]");
        sb.AppendLine("    [SkipLocalsInit]");
        sb.AppendLine($"    private static unsafe void Sort{nv}(ref {t.ElemType} p, int len)");
        sb.AppendLine("    {");
        sb.AppendLine($"        {t.VecType} lo, hi, rv;");
        for (int i = nv / 2; i < nv; i++)
        {
            sb.AppendLine($"        {t.VecType} m{i} = default;");
            sb.AppendLine($"        int c{i} = len - {i * t.Lanes};");
        }
        // ---- loads ----
        for (int i = 0; i < nv; i++)
        {
            if (i < nv / 2)
            {
                sb.AppendLine($"        {t.VecType} v{i} = {t.VecStatic}.LoadUnsafe(ref {(i == 0 ? "p" : $"Unsafe.Add(ref p, {i * t.Lanes})")});");
            }
            else
            {
                sb.AppendLine($"        {t.VecType} v{i};");
                sb.AppendLine($"        if (c{i} >= {t.Lanes}) {{ v{i} = {t.VecStatic}.LoadUnsafe(ref Unsafe.Add(ref p, {i * t.Lanes})); }}");
                sb.AppendLine($"        else if (c{i} > 0)");
                sb.AppendLine($"        {{");
                sb.AppendLine($"            m{i} = {t.Isa}.CompareLessThan(LaneIdx, {t.VecStatic}.Create(({t.ElemType})c{i}));");
                sb.AppendLine($"            v{i} = {t.Isa}.MaskLoad(({t.PtrType}*)Unsafe.AsPointer(ref Unsafe.Add(ref p, {i * t.Lanes})), m{i}, Pad);");
                sb.AppendLine($"        }}");
                sb.AppendLine($"        else {{ v{i} = Pad; }}");
            }
        }
        // ---- column sort (whole-vector comparators) ----
        foreach (var c in net)
        {
            EmitCoex(sb, "        ", t, $"v{c[0]}", $"v{c[1]}");
        }
        // ---- merge_n_vec: scales 2 .. MaxScale ----
        for (int scale = 2; scale <= t.MaxScale; scale *= 2)
        {
            EmitMergeSubstep(sb, "        ", t, Enumerable.Range(0, nv).Select(i => $"v{i}").ToArray(), scale);
            for (int i = 0; i < nv; i++)
            {
                EmitInternalMerge(sb, "        ", t, $"v{i}", scale);
            }
        }
        // ---- stores ----
        for (int i = 0; i < nv; i++)
        {
            if (i < nv / 2)
            {
                sb.AppendLine($"        v{i}.StoreUnsafe(ref {(i == 0 ? "p" : $"Unsafe.Add(ref p, {i * t.Lanes})")});");
            }
            else
            {
                sb.AppendLine($"        if (c{i} >= {t.Lanes}) {{ v{i}.StoreUnsafe(ref Unsafe.Add(ref p, {i * t.Lanes})); }}");
                sb.AppendLine($"        else if (c{i} > 0) {{ {t.Isa}.MaskStore(({t.PtrType}*)Unsafe.AsPointer(ref Unsafe.Add(ref p, {i * t.Lanes})), m{i}, v{i}); }}");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // merge_substep_n_vec: reverse the upper half with reverse_n<scale>, COEX (i, n-1-i),
    // then recurse on both halves.
    private static void EmitMergeSubstep(StringBuilder sb, string ind, Target t, string[] v, int scale)
    {
        int n = v.Length;
        if (n <= 1)
        {
            return;
        }
        int half = n / 2;
        for (int i = half; i < n; i++)
        {
            EmitRevN(sb, ind, t, v[i], scale);
        }
        for (int i = 0; i < half; i++)
        {
            EmitCoex(sb, ind, t, v[i], v[n - 1 - i]);
        }
        EmitMergeSubstep(sb, ind, t, v.Take(half).ToArray(), scale);
        EmitMergeSubstep(sb, ind, t, v.Skip(half).ToArray(), scale);
    }

    // internal_merge_n_vec for one vector: reverse_n<scale> + COEX + merge_n<scale>,
    // then swap_n<s> + COEX + merge_n<s> for s = scale/2 .. 2. Emitted per vector: for
    // nv = 2/4 this keeps each vector's live range short and the method spill-free (the
    // per-scale order measured 126 -> 180 instructions and 0 -> 10 spills for nv = 2).
    private static void EmitInternalMerge(StringBuilder sb, string ind, Target t, string v, int scale)
    {
        if (scale == t.MaxScale && t.MaxScale == t.Lanes && t.Lanes == 16)
        {
            // reverse v INTO rv (two steps); reversing rv in place would use the stale
            // value left over from the previous stage.
            sb.AppendLine($"{ind}rv = {t.Rev8Expr(v)};");
            sb.AppendLine($"{ind}rv = {t.SwapHalvesExpr("rv")};");
        }
        else
        {
            sb.AppendLine($"{ind}rv = {t.RevExpr(v, scale)};");
        }
        EmitCoex(sb, ind, t, "rv", v);
        EmitMergeN(sb, ind, t, v, v, "rv", scale);
        for (int s = scale / 2; s >= 2; s /= 2)
        {
            sb.AppendLine($"{ind}rv = {t.SwapExpr(v, s)};");
            EmitCoex(sb, ind, t, "rv", v);
            EmitMergeN(sb, ind, t, v, v, "rv", s);
        }
    }

    private static void EmitCoex(StringBuilder sb, string ind, Target t, string a, string b)
    {
        sb.AppendLine($"{ind}lo = {t.Min(a, b)};");
        sb.AppendLine($"{ind}hi = {t.Max(a, b)};");
        sb.AppendLine($"{ind}{a} = lo;");
        sb.AppendLine($"{ind}{b} = hi;");
    }

    // Ground truth (avx512-32bit-qsort.hpp): _mm512_mask_blend_epi32(k, reg, other)
    // with k = ((i % scale) < scale/2) â€?a per-lane blend, not an interleave.
    private static void EmitMergeN(StringBuilder sb, string ind, Target t, string dst, string a, string b, int scale)
    {
        sb.AppendLine($"{ind}{dst} = {t.MergeNExpr(a, b, scale)};");
    }

    // reverse_n<scale> with immediate-control instructions only.
    private static void EmitRevN(StringBuilder sb, string ind, Target t, string v, int scale)
    {
        if (scale == t.MaxScale && t.MaxScale == t.Lanes && t.Lanes == 16)
        {
            // reverse_n<16>: reverse within each 256-bit half, then swap the halves.
            sb.AppendLine($"{ind}{v} = {t.Rev8Expr(v)};");
            sb.AppendLine($"{ind}{v} = {t.SwapHalvesExpr(v)};");
            return;
        }
        sb.AppendLine($"{ind}{v} = {t.RevExpr(v, scale)};");
    }
}


