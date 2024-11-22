using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Linq;
using Verse;
using Settings = GiddyUp.ModSettings_GiddyUp;

namespace GiddyUp.Harmony
{
    [HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos), MethodType.Getter)]
    static class Pawn_DrawTracker_DrawPos
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var firstLdArg0Label = generator.DefineLabel();
            var firstLdArg0 = instructions.First(i => i.opcode == OpCodes.Ldarg_0);
            firstLdArg0.labels.Add(firstLdArg0Label);

            var returnInstructionLabel = generator.DefineLabel();
            var returnInstruction = instructions.ToList()[instructions.Count() - 1];
            returnInstruction.labels.Add(returnInstructionLabel);

            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(ExtendedDataStorage), nameof(ExtendedDataStorage.isMounted)));
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.pawn)));
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber)));
            yield return new CodeInstruction(OpCodes.Callvirt, typeof(HashSet<int>).GetMethod(nameof(HashSet<int>.Contains)));
            yield return new CodeInstruction(OpCodes.Brfalse_S, firstLdArg0Label);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, typeof(Pawn_DrawTracker_DrawPos).GetMethod(nameof(Pawn_DrawTracker_DrawPos.DrawOffset)));
            yield return new CodeInstruction(OpCodes.Br_S, returnInstructionLabel);

            foreach (var code in instructions) yield return code;
        }
        public static Vector3 DrawOffset(Pawn_DrawTracker __instance)
        {
            Pawn pawn = __instance.pawn;
            ExtendedPawnData pawnData = pawn.GetGUData();

            //Failsafe. Should never happen but too dangerous to chance
            if (pawnData.mount == null)
            {
                pawn.Dismount(null, pawnData, true);
                return Vector3.zero;
            }
            var offset = pawnData.mount.Drawer.DrawPos;
            if (pawnData.drawOffset != -1)
            {
                offset.z = offset.z + pawnData.drawOffset;
            }
            //Apply custom offsets
            var rotation = pawn.rotationInt;
            var modX = pawnData.mount.def.GetModExtension<DrawingOffset>();
            if (modX != null) offset += AddCustomOffsets(rotation, modX);
            
            if (rotation == Rot4.South && Settings.drawRulesCache.Contains(pawnData.mount.def.shortHash))
            {
                offset.y -= 0.01f;
            }
            else offset.y += 0.01f;

            return offset;
        }

        static Vector3 AddCustomOffsets(Rot4 rotation, DrawingOffset customOffsets)
        {
            if (rotation == Rot4.North) return customOffsets.northOffset;
            if (rotation == Rot4.South) return customOffsets.southOffset;
            if (rotation == Rot4.East) return customOffsets.eastOffset;
            return customOffsets.westOffset;
        }
    }
}