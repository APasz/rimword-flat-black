using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace FlatBlack;

internal static class ThemePatches
{
    internal const string HarmonyId = "APasz.FlatBlack";
    private static Action? applyFonts;
    private static readonly MethodInfo ResolveColor = AccessTools.Method(typeof(ThemeResources), nameof(ThemeResources.ResolveColor));
    private static readonly MethodInfo ResolveTexture = AccessTools.Method(typeof(ThemeResources), nameof(ThemeResources.ResolveTexture));

    internal static int Install(IReadOnlySet<FieldInfo> fields, Action updateFonts)
    {
        var harmony = new Harmony(HarmonyId);
        var transpiler = new HarmonyMethod(typeof(ThemePatches), nameof(Transpiler)) { priority = Priority.Last };
        MethodInfo[] targets = FindTargets(typeof(Widgets).Assembly, fields).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("No matching vanilla UI methods were found for Flat Black.");

        try
        {
            foreach (MethodInfo target in targets)
                harmony.Patch(target, transpiler: transpiler);
            applyFonts = updateFonts;
            harmony.Patch(AccessTools.Method(typeof(Text), nameof(Text.StartOfOnGUI)),
                postfix: new HarmonyMethod(typeof(ThemePatches), nameof(UpdateFonts)) { priority = Priority.Last });
        }
        catch
        {
            foreach (MethodBase target in harmony.GetPatchedMethods().ToArray())
                harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId);
            applyFonts = null;
            throw;
        }

        return targets.Length;
    }

    private static void UpdateFonts() => applyFonts?.Invoke();

    internal static IEnumerable<MethodInfo> FindTargets(Assembly assembly, IReadOnlySet<FieldInfo> fields)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        HashSet<int> tokens = fields.Where(field => field.Module == assembly.ManifestModule).Select(field => field.MetadataToken).ToHashSet();
        foreach (Type type in assembly.GetTypes())
        {
            if (type.ContainsGenericParameters)
                continue;
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.ContainsGenericParameters || method.GetMethodBody()?.GetILAsByteArray() is not byte[] body
                    || !MayLoadResource(body, tokens))
                    continue;
                if (PatchProcessor.ReadMethodBody(method).Any(instruction => instruction.Key == OpCodes.Ldsfld
                    && instruction.Value is FieldInfo field && fields.Contains(field)))
                    yield return method;
            }
        }
    }

    private static bool MayLoadResource(ReadOnlySpan<byte> body, IReadOnlySet<int> tokens)
    {
        // Avoid decoding the entire game; Harmony verifies the few matching byte sequences.
        for (int offset = 0; offset + sizeof(int) < body.Length; offset++)
        {
            if (body[offset] == (byte)OpCodes.Ldsfld.Value && tokens.Contains(BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 1)..])))
                return true;
        }
        return false;
    }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode != OpCodes.Ldsfld || instruction.operand is not FieldInfo field)
            {
                yield return instruction;
                continue;
            }

            MethodInfo? resolver = ThemeResources.IsColor(field) ? ResolveColor
                : ThemeResources.IsTexture(field) ? ResolveTexture : null;
            if (resolver is null)
            {
                yield return instruction;
                continue;
            }

            var load = new CodeInstruction(instruction);
            var resolve = new CodeInstruction(OpCodes.Call, resolver);
            foreach (ExceptionBlock block in load.blocks.Where(block => block.blockType == ExceptionBlockType.EndExceptionBlock).ToArray())
            {
                load.blocks.Remove(block);
                resolve.blocks.Add(block);
            }

            yield return load;
            yield return new CodeInstruction(OpCodes.Ldtoken, field);
            yield return resolve;
        }
    }
}
