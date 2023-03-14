﻿using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using static GiddyUp.IsMountableUtility;
using Settings = GiddyUp.ModSettings_GiddyUp;

namespace GiddyUp.Harmony
{
	[HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ChoicesAtFor))]
	static class FloatMenuMakerMap_ChoicesAtFor
	{
		static bool Prepare()
		{
			return Settings.rideAndRollEnabled;
		}
		static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> __result)
		{
			/*
			if (DebugSettings.godMode)
            {
                var godModeTargetting = new TargetingParameters() 
                {
                    canTargetAnimals = true, 
                    canTargetHumans = true, 
                    canTargetPawns = true,
                    validator = null,
                    onlyTargetColonists = false,
                    mustBeSelectable = false
                };
                foreach (LocalTargetInfo current in GenUI.TargetsAt(clickPos, godModeTargetting, true))
                {
                    if (current.Thing is Pawn target) FloatMenuUtility.AddMountingOptions(target, pawn, __result);
                }
				return;
            }*/
			foreach (LocalTargetInfo current in GenUI.TargetsAt(clickPos, TargetingParameters.ForAttackHostile(), true))
			{
				if (current.Thing is Pawn target && !pawn.Drafted && target.RaceProps.Animal) FloatMenuUtility.AddMountingOptions(target, pawn, __result);
			}
		}
	}

	[HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.AddDraftedOrders), new Type[] { typeof(Vector3), typeof(Pawn), typeof(List<FloatMenuOption>), typeof(bool) })]
	static class Patch_AddDraftedOrders
	{
		static bool Prepare()
		{
			return Settings.battleMountsEnabled;
		}
		static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> opts)
		{
			if (pawn.RaceProps.Animal) return;
			foreach (LocalTargetInfo current in GenUI.TargetsAt(clickPos, TargetingParameters.ForAttackHostile(), true))
			{
				if (current.Thing is Pawn target) FloatMenuUtility.AddMountingOptions(target, pawn, opts);
			}
		}
	}

	static class FloatMenuUtility
	{
		public static bool AddMountingOptions(Pawn animal, Pawn pawn, List<FloatMenuOption> opts)
		{
			var pawnData = pawn.GetGUData();
			//Right click to dismount...
			if (animal == pawnData.mount)
			{
				return opts.GenerateFloatMenuOption("GUC_Dismount".Translate(), true, () => pawn.Dismount(animal, pawnData, true));
			}
			//Right click to mount...
			else
			{
				pawn.IsCapableOfRiding(out Reason riderReason);
				if (animal.IsMountable(out Reason reason, pawn, true, true) && riderReason == Reason.False)
				{
					//New mount
					if (pawnData.mount == null)
					{
						return opts.GenerateFloatMenuOption("GUC_Mount".Translate(), true, () => pawn.GoMount(animal, MountUtility.GiveJobMethod.Try));
					}
					//Switch mount
					else
					{
						return opts.GenerateFloatMenuOption("GUC_SwitchMount".Translate(), true, delegate
						{ 
							pawn.Dismount(pawnData.mount, pawnData, true);
							pawn.GoMount(animal, MountUtility.GiveJobMethod.Try);
						});
					}
				}
				/*
				else if (DebugSettings.godMode)
				{
					return opts.GenerateFloatMenuOption("GUC_Mount_GodMode".Translate(), true, () => pawn.GoMount(animal, MountUtility.GiveJobMethod.Try));
				}*/
				else
				{
					if (Settings.logging) Log.Message("[Giddy-Up] " + pawn.Name.ToString() + " could not mount " + animal.thingIDNumber.ToString() + " because: " + reason.ToString());
					switch (reason)
					{
						case Reason.NotAnimal: return false;
						case Reason.WrongFaction: return false;
						case Reason.IsBusy: return opts.GenerateFloatMenuOption("GUC_AnimalBusy".Translate());
						case Reason.NotInModOptions: return opts.GenerateFloatMenuOption("GUC_NotInModOptions".Translate());
						case Reason.NotFullyGrown: return opts.GenerateFloatMenuOption("GUC_NotFullyGrown".Translate());
						case Reason.NeedsTraining: return opts.GenerateFloatMenuOption("GUC_NeedsObedience".Translate());
						case Reason.IsRoped: return opts.GenerateFloatMenuOption("GUC_IsRoped".Translate());
						case Reason.IsPoorCondition: return opts.GenerateFloatMenuOption("GUC_IsPoorCondition".Translate());
						case Reason.TooHeavy: return opts.GenerateFloatMenuOption("GUC_TooHeavy".Translate());
						default:
						{
							if (riderReason == Reason.TooYoung) return opts.GenerateFloatMenuOption("GU_Car_TooYoung".Translate());
							else if (riderReason == Reason.IncompatibleEquipment) return opts.GenerateFloatMenuOption("GU_IncompatibleEquipment".Translate());
							return false;
						}
					}
				}
			}
		}

		static bool GenerateFloatMenuOption(this List<FloatMenuOption> list, string text, bool prefixType = false, Action action = null)
		{
			if (!prefixType) text = "GUC_CannotMount".Translate() + text;
			list.Add(new FloatMenuOption(text, action, MenuOptionPriority.Low));
			return true;
		}
	}
}
