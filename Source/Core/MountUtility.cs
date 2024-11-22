using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System;
using System.Linq;
//using Multiplayer.API;
using Verse;
using Verse.AI;
using Settings = GiddyUp.ModSettings_GiddyUp;

namespace GiddyUp
{
	static class MountUtility
	{
		public static HashSet<PawnKindDef> allWildAnimals = new HashSet<PawnKindDef>(), allDomesticAnimals = new HashSet<PawnKindDef>();
		public enum GiveJobMethod { Inject, Try, Instant };
		enum ListToUse { Local, Foreign, Domestic };
		public enum DismountLocationType { Invalid, Pen, Spot, Auto };
		
		public static ThinkResult? GoMount(this Pawn rider, Pawn animal, GiveJobMethod giveJobMethod = GiveJobMethod.Inject, ThinkResult? thinkResult = null, Job currentJob = null)
		{
			//Cancel any reservations. The IsMountable method already checked if the reservation was fine to abort
			animal.CancelRopers();
			
			//Immediately mount the pawn on the animal. This is done when the mount job finishes, or emulating that it has already finished such as when pawns come in pre-mounted
			if (giveJobMethod == GiveJobMethod.Instant) rider.Mount(animal);
			
			//This prompts them to mount up before they carry out another job they were planning to do
			else if (giveJobMethod == GiveJobMethod.Inject)
			{
				if (currentJob != null) rider.jobs?.jobQueue?.EnqueueFirst(currentJob);
				if (thinkResult != null) return new ThinkResult(new Job(ResourceBank.JobDefOf.Mount, animal) {count = 1}, thinkResult.Value.SourceNode, thinkResult.Value.Tag, false);
				else rider.jobs?.StartJob(new Job(
					ResourceBank.JobDefOf.Mount, animal) {count = 1}, 
					lastJobEndCondition: JobCondition.InterruptOptional, 
					resumeCurJobAfterwards: currentJob == null, 
					cancelBusyStances: false, 
					keepCarryingThingOverride: true);
			}
			
			//This has them mount after they're done doing their current task
			else if (giveJobMethod == GiveJobMethod.Try)
			{
				animal.jobs.StopAll();
				animal.jobs.EndCurrentJob(JobCondition.InterruptForced, false, false); //The StopAll above will trigger the WaitForRider job. This will stop it.
				animal.pather.StopDead();
				rider.jobs.TryTakeOrderedJob(new Job(ResourceBank.JobDefOf.Mount, animal) {count = 1});
			}
			
			return null;
		}
		static void Mount(this Pawn rider, Pawn animal)
		{
			//First check if the pawn had a mount to begin with...
			ExtendedPawnData pawnData = rider.GetGUData();
			if (animal == null) animal = pawnData.reservedMount;
			
			//If they did...
			if (animal != null)
			{
				//Instantly mount, as if the mount jobdriver had just finished
				pawnData.mount = animal;
				ExtendedDataStorage.isMounted.Add(rider.thingIDNumber);
				pawnData.ReservedMount = animal;
				animal.GetGUData().ReservedBy = rider;
				
				//Break ropes if there are any
				if (animal.roping?.IsRoped ?? false) animal.roping.BreakAllRopes();

				//Set the offset
				pawnData.drawOffset = TextureUtility.FetchCache(animal);
				
				//Set the animal job and state
				if (animal.CurJobDef != ResourceBank.JobDefOf.Mounted)
				{
					if (animal.HostileTo(Current.gameInt.worldInt.factionManager.ofPlayer)) animal.mindState.duty = new PawnDuty(DutyDefOf.Defend);
					animal.jobs.TryTakeOrderedJob(new Job(ResourceBank.JobDefOf.Mounted, rider) { count = 1});
				}
			}
		}
		public static void TryAutoMount(this Pawn pawn, Pawn_JobTracker jobTracker, ref ThinkResult thinkResult)
		{
			if (!pawn.IsColonistPlayerControlled ||
				pawn.def.race.intelligence != Intelligence.Humanlike ||
				thinkResult.Job == null || 
				thinkResult.Job.def == ResourceBank.JobDefOf.Mount || 
				pawn.CurJobDef == ResourceBank.JobDefOf.Mount ||
				pawn.Drafted || 
				pawn.InMentalState || 
				pawn.IsMounted() ||
				(pawn.mindState.duty != null && (pawn.mindState.duty.def == DutyDefOf.TravelOrWait || pawn.mindState.duty.def == DutyDefOf.TravelOrLeave)))
			{
				return;
			}

			//Is this a job that cannot be done mounted?
			var jobDef = thinkResult.Job.def;
			
			//Sort out where the targets are. For some jobs the first target is B, and the second A.
			if (!thinkResult.Job.DetermineTargets(out IntVec3 firstTarget, out IntVec3 secondTarget)) return;
			
			//Determine distances
			float pawnTargetDistance = pawn.Position.DistanceTo(firstTarget);
			
			float firstToSecondTargetDistance;
			if (secondTarget.IsValid && (jobDef == JobDefOf.HaulToCell || jobDef == JobDefOf.HaulToContainer)) firstToSecondTargetDistance = firstTarget.DistanceTo(secondTarget);
			else firstToSecondTargetDistance = 0;
			
			ExtendedPawnData pawnData = pawn.GetGUData();
			if (!pawnData.canRide) return;

			/*
			//Bias nearby waiting animals first
			//Disabling for now, this is probably the cause of the mount/dismount loop problem.
			if (pawnData.reservedMount != null && pawnData.reservedMount.CurJobDef == ResourceBank.JobDefOf.WaitForRider && pawnData.reservedMount.Position.DistanceTo(pawn.Position) < 30f)
			{
				thinkResult = pawn.GoMount(pawnData.reservedMount, MountUtility.GiveJobMethod.Inject, thinkResult, thinkResult.Job).Value;
			}
			*/
			if (pawnTargetDistance + firstToSecondTargetDistance > Settings.minAutoMountDistance)
			{
				//Do some less performant final check. It's less costly to run these near the end on successful mount attempts than to check constantly
				if (!pawn.IsCapableOfRiding(out IsMountableUtility.Reason reason) || pawn.IsBorrowedByAnyFaction() || pawn.IsFormingCaravan()) return;

				if (MountUtility.GetBestAnimal(pawn, out Pawn bestAnimal, firstTarget, secondTarget, pawnTargetDistance, firstToSecondTargetDistance, pawnData))
				{
					//Check if the mount is too close to the destination to be worht it
					if (bestAnimal.Position.DistanceTo(firstTarget) < Settings.minAutoMountDistance / 4) return;

					//Finally, go mount up
					thinkResult = pawn.GoMount(bestAnimal, MountUtility.GiveJobMethod.Inject, thinkResult, thinkResult.Job).Value;
				}
			}
		}
		public static bool GoDismount(this Pawn rider, Pawn animal, IntVec3 target = default(IntVec3))
		{
			if (animal == null) return false;
			bool isGuest = animal.Faction == null || !animal.Faction.def.isPlayer;
			Area areaFound = rider.Map.areaManager.GetLabeled(isGuest ? ResourceBank.VisitorAreaDropMount : ResourceBank.AreaDropMount);

			IntVec3 targetLoc;
			//Any dismount spots available?
			if (areaFound != null && areaFound.innerGrid.TrueCount > 0)
			{
				targetLoc = areaFound.GetClosestAreaLoc(target == default(IntVec3) ? rider.Position : target);
				if (isGuest && targetLoc.DistanceTo(rider.Position) > ResourceBank.guestSpotCheckRange)
				{
					var guestData = rider.GetGUData();
					rider.Dismount(guestData.mount, guestData);
					return false;
				}
			}
			
			//If not, use a pen?
			else
			{
				var pen = AnimalPenUtility.GetPenAnimalShouldBeTakenTo(rider, animal, out string failReason, true, true, false, true);
				if (pen != null) targetLoc = AnimalPenUtility.FindPlaceInPenToStand(pen, rider);
				else return false; //Neither a pen or spot available
			}
			//Can reach
			if (rider.Map.reachability.CanReach(rider.Position, targetLoc, PathEndMode.Touch, TraverseParms.For(rider)))
			{
				rider.jobs.jobQueue.EnqueueFirst(new Job(ResourceBank.JobDefOf.Dismount, targetLoc) { count = 1});
				return true;
			}
			else Messages.Message("GU_Car_NotReachable_DropAnimal_NPC_Message".Translate(), new RimWorld.Planet.GlobalTargetInfo(targetLoc, rider.Map), MessageTypeDefOf.NegativeEvent);

			return false;
		}
		public static void Dismount(this Pawn rider, Pawn animal, ExtendedPawnData pawnData, bool clearReservation = false, IntVec3 parkLoc = default(IntVec3), bool ropeIfNeeded = true, bool waitForRider = true)
		{
			ExtendedDataStorage.isMounted.Remove(rider.thingIDNumber);
			if (pawnData == null) pawnData = rider.GetGUData();
			pawnData.mount = null;
			if (Settings.logging) Log.Message("[Giddy-Up] " + rider.Label + " no longer riding  " + (animal?.Label ?? "NULL"));

			//Normally should not happen, may come in null from sanity checks. Odd bugs or save/reload conflicts between version changes
			ExtendedPawnData animalData;
			if (animal == null) ExtendedDataStorage.GUComp.ReverseLookup(rider.thingIDNumber, out animalData);
			else animalData = animal.GetGUData();

			//Reservation handling
			if (clearReservation) 
			{
				pawnData.ReservedMount = null;
				animalData.ReservedBy = null;
			}
			
			//Reset free locomotion
			if (animal == null) return; //We're done here
			animal.Drawer.tweener = new PawnTweener(animal);
			animal.pather.ResetToCurrentPosition();
			
			//========Post-dismount behavior======
			//If this is a visitor's animal, keep it from wandering off
			if (!rider.Faction.def.isPlayer && animal.mindState.duty != null) animal.mindState.duty.focus = new LocalTargetInfo(animal.Position);
			
			//If the animal is being dismounted outside of a pen and it's a roamer, hitch it
			if (ropeIfNeeded && AnimalPenUtility.NeedsToBeManagedByRope(animal) && AnimalPenUtility.GetCurrentPenOf(animal, true) == null && //Needs to be roped and not already penned?
				 (!animal.IsRoped()) && //Skip already roped
				 !animal.Position.CloseToEdge(animal.Map, ResourceBank.mapEdgeIgnore) && //Skip pawns near the map edge. They may be entering/exiting the map which triggers dismount calls
				 (animal.Faction.def.isPlayer && animal.inventory != null && animal.inventory.innerContainer.Count == 0)) //Skip guest caravan pack animals
			{
				if (animal.roping == null) animal.roping = new Pawn_RopeTracker(animal); //Not needed, but changes to modded animals could maybe cause issues
				if (Settings.logging) Log.Message("[Giddy-Up] " + rider.Label + " just roped " + animal.Label);
				animal.roping.RopeToSpot(parkLoc == default(IntVec3) ? animal.Position : parkLoc);
			}
			//Follow the rider for a while to give it an opportunity to take a ride back
			if (Settings.rideAndRollEnabled && !rider.Drafted && rider.Faction.def.isPlayer && animalData.reservedBy != null)
			{
				if (waitForRider)
				{
					animal.jobs.jobQueue.EnqueueFirst(new Job(ResourceBank.JobDefOf.WaitForRider, animalData.reservedBy)
					{
						expiryInterval = Settings.waitForRiderTimer,
						checkOverrideOnExpire = true,
						followRadius = 8,
						locomotionUrgency = LocomotionUrgency.Walk
					});
				}
			}
		}
		public static void InvoluntaryDismount(this Pawn rider, Pawn animal, ExtendedPawnData pawnData)
		{
			if (!rider.Faction.def.isPlayer && animal != null && !animal.Dead && animal.Spawned && //Is a non-colonist, and animal is valid?
				(ExtendedDataStorage.nofleeingAnimals == null || !ExtendedDataStorage.nofleeingAnimals.Contains(animal)) && //Does this animal ever flee?
				(animal.Faction == null || !animal.Faction.def.isPlayer) ) //Is the animal ours?
			{
				animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.PanicFlee);
			}
			Dismount(rider, animal, pawnData, clearReservation: true, ropeIfNeeded: false, waitForRider: false);
		}
		public static bool FindPlaceToDismount(this Pawn rider, Area areaDropAnimal, Area areaNoMount, IntVec3 riderDestinaton, out IntVec3 parkLoc, Pawn animal, out DismountLocationType dismountLocationType)
		{
			Map map = rider.Map;
			if (areaDropAnimal == null || areaDropAnimal.TrueCount == 0)
			{
				dismountLocationType = DismountLocationType.Pen;
				TryParkAnimalPen(out parkLoc);
			}
			else
			{
				dismountLocationType = DismountLocationType.Spot;
				parkLoc = areaDropAnimal.GetClosestAreaLoc(riderDestinaton);
			}

			//Invalidate the results if not reachable
			if (!map.reachability.CanReach(rider.Position, parkLoc, PathEndMode.Touch, TraverseParms.For(rider)))
			{
				parkLoc = IntVec3.Invalid;
			}

			//Dropoff is too far away, setup a hitching point instead
			if (parkLoc.DistanceTo(riderDestinaton) > Settings.autoHitchDistance)
			{
				dismountLocationType = DismountLocationType.Auto;
				Predicate<IntVec3> freeCell = delegate(IntVec3 cell)
				{
					return (cell.Standable(map) && 
						cell.GetDangerFor(animal, map) == Danger.None && 
						!cell.Fogged(map) &&
						cell.InBounds(map) &&
						(areaNoMount == null || !areaNoMount.innerGrid[map.cellIndices.CellToIndex(cell)]) &&
						rider.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.None));
				};
				
				bool foundRandomCellNear = false;
				for (int attempt = 1; attempt < 8; attempt++)
				{
					if (CellFinder.TryFindRandomCellNear(riderDestinaton, map, attempt * 5, freeCell, out parkLoc))
					{
						foundRandomCellNear = true;
						break;
					}
				}
				if (!foundRandomCellNear)
				{
					if (Settings.logging) Log.Message("[Giddy-Up] " + rider.Label + " could not find a valid autohitch spot near " + parkLoc.ToString());
					parkLoc = IntVec3.Invalid;
				}
			}
			//Validate results
			if (parkLoc == IntVec3.Invalid)
			{
				dismountLocationType = DismountLocationType.Invalid;
				if (Prefs.DevMode) Log.Message("[Giddy-Up] " + rider.Label + " could not ride their mount to their job because they could not find any places to dismount. Immediately dismounting.");
			}
			//Looks good, begin pathing
			else return true;
			return false;

			#region Embedded methods
			void TryParkAnimalPen(out IntVec3 parkLoc)
			{
				parkLoc = IntVec3.Invalid;
				var pen = AnimalPenUtility.GetPenAnimalShouldBeTakenTo(rider, animal, out string failReason, true);
				if (pen != null)
				{
					parkLoc = AnimalPenUtility.FindPlaceInPenToStand(pen, rider);
				}
			}
			#endregion
		}
		public static bool GenerateMounts(ref List<Pawn> list, IncidentParms parms)
		{
			//if (MP.enabled) return false; // Best we can do for now
			Map map = parms.target as Map;
			if (map == null)
			{
				Caravan caravan = (Caravan)parms.target;
				int tile = caravan.Tile;
				map = Current.Game.FindMap(tile);
				if (map == null) return false;
			}

			int mountChance = GetMountChance(parms.faction);
			if (mountChance == -1) return false; //wrong faction
			float domesticWeight = Settings.nonWildWeight;
			float localWeight = Settings.inBiomeWeight;
			float foreignWeight = Settings.outBiomeWeight;
			
			//Setup working lists
			GetAnimalArrays(out PawnKindDef[] wildAnimals, out PawnKindDef[] domesticAnimals, out PawnKindDef[] localAnimals);
			
			//Setup weight ranges
			float totalWeight = localWeight + foreignWeight + domesticWeight; //EG 100
			localWeight = (localWeight / totalWeight) * 100f; //EG 20
			foreignWeight = (foreignWeight / totalWeight) * 100f; //EG 20+10 = 30
			foreignWeight += localWeight;
			float averageCommonality = AverageAnimalCommonality(map.Biome);

			if (Settings.logging) Log.Message("[Giddy-Up] List weights: localWeight: " + localWeight.ToString() + " foreignWeight: " + foreignWeight.ToString());
			
			List<Pawn> packAnimals = new List<Pawn>();
			bool hasUnmountedPackAnimals = Settings.ridePackAnimals && GetPackAnimals(list, packAnimals);
			//hasPackAnimals = false;
			var length = list.Count;
			for (int i = 0; i < length; i++)
			{
				Pawn pawn = list[i];
				if (!pawn.RaceProps.Humanlike || pawn.kindDef == PawnKindDefOf.Slave) continue;

				int random = Rand.Range(1, 100);

				PawnKindDef pawnKindDef;
				Pawn animal;
				CustomMounts modExtension = pawn.kindDef.GetModExtension<CustomMounts>();
				if (hasUnmountedPackAnimals)
				{
					animal = packAnimals.Pop();
					animal.Position = pawn.Position; //Avoids the pop-in glitch
					hasUnmountedPackAnimals = packAnimals.Count != 0;
					goto Spawned;
				}
				
				if (modExtension != null && modExtension.mountChance != 0) mountChance = modExtension.mountChance;
				if (mountChance <= random) continue;

				if (modExtension != null && modExtension.possibleMounts.Count > 0)
				{
					Rand.PushState();
					if (modExtension.possibleMounts.TryRandomElementByWeight(mount => mount.Value, out KeyValuePair<PawnKindDef, int> selectedMount))
					{
						if (Settings.logging)
						{
							var report = System.String.Join(", ", modExtension.possibleMounts.Select(x => x.Key.defName));
							Log.Message("[Giddy-Up] " + (pawn.Label ?? "NULL") + " had a custom mount extension. The allowed mounts were: " + 
							report + " and they picked " + selectedMount.Key.defName);
						}
						pawnKindDef = selectedMount.Key;
					}
					else pawnKindDef = null;
					Rand.PopState();
				}
				else
				{
					int pawnHandlingLevel = pawn.skills?.GetSkill(SkillDefOf.Animals).Level ?? 8;
					if (pawnHandlingLevel <= Settings.minHandlingLevel) continue;

					PawnKindDef[] workingList;
					bool domestic = false;
					switch (DetermineList(localWeight, foreignWeight, random))
					{
						case ListToUse.Local: workingList = localAnimals; break;
						case ListToUse.Foreign: workingList = wildAnimals; break;
						default: workingList = domesticAnimals; domestic = true; break;
					}

					Predicate<PawnKindDef> commonPredicate = pawnKindDef => Settings.mountableCache.Contains(pawnKindDef.race.shortHash) && //Is mountable?
						parms.points > pawnKindDef.combatPower * ResourceBank.combatPowerFactor && //Is not too powerful for this particular raid?
						(pawnKindDef.combatPower * ResourceBank.combatPowerFactor > pawn.kindDef.combatPower || //Rider considers this a worthy creature, or...
						pawnKindDef.race.GetStatValueAbstract(StatDefOf.MoveSpeed) >= pawn.GetStatValue(StatDefOf.MoveSpeed)); //Rider sees this as a faster creature?

					if (domestic) workingList.Where(x => commonPredicate(x)).
						TryRandomElementByWeight(def => def.race.BaseMarketValue / def.race.GetStatValueAbstract(StatDefOf.CaravanRidingSpeedFactor), out pawnKindDef);

					else workingList.Where(x => map.mapTemperature.SeasonAcceptableFor(x.race) && commonPredicate(x)).
						TryRandomElementByWeight(def => CalculateCommonality(def, map.Biome, pawnHandlingLevel, averageCommonality), out pawnKindDef);
				}

				//Validate and spawn
				if (pawnKindDef == null) 
				{
					if (Settings.logging) Log.Warning("[Giddy-Up] Could not find any suitable animal for " + pawn.thingIDNumber);
					return false;
				}
				animal = PawnGenerator.GeneratePawn(pawnKindDef, parms.faction);
				GenSpawn.Spawn(animal, pawn.Position, map, parms.spawnRotation);
				list.Add(animal);

				//Set their training
				Spawned:
				if (animal.playerSettings == null) animal.playerSettings = new Pawn_PlayerSettings(animal);
				animal.training.Train(TrainableDefOf.Obedience, pawn);

				//Mount up
				pawn.GoMount(animal, GiveJobMethod.Instant);
			}
			return true;

			#region embedded methods
			int GetMountChance(Faction faction)
			{
				if (faction == null) return -1;
				if (faction.HostileTo(Current.gameInt.worldInt.factionManager.ofPlayer))
				{
					if (faction.def.techLevel < TechLevel.Industrial) return Settings.enemyMountChancePreInd;
					else if (faction.def != FactionDefOf.Mechanoid) return Settings.enemyMountChance;
				}
				else if (Settings.caravansEnabled)
				{
					if (faction.def.techLevel < TechLevel.Industrial) return Settings.visitorMountChancePreInd;
					else if (faction.def != FactionDefOf.Mechanoid) return Settings.visitorMountChance;
				}
				
				return -1;
			}
			ListToUse DetermineList(float localWeight, float foreignWeight, int random)
			{
				if (random < foreignWeight) return ListToUse.Local;
				else if (random >= localWeight && random < foreignWeight) return ListToUse.Foreign;
				return ListToUse.Domestic;
			}
			void GetAnimalArrays(out PawnKindDef[] wildAnimals, out PawnKindDef[] domesticAnimals, out PawnKindDef[] localAnimals)
			{
				FactionRestrictions factionRules = parms.faction?.def?.GetModExtension<FactionRestrictions>();
				if (factionRules != null)
				{
					//Override working list
					wildAnimals = factionRules.allowedWildAnimals.ToArray();
					var wildAnimalsReadonly = wildAnimals;
					domesticAnimals = factionRules.allowedNonWildAnimals.ToArray();
					localAnimals = map.Biome.AllWildAnimals.
						Where(x => wildAnimalsReadonly.Contains(x) && map.mapTemperature.SeasonAcceptableFor(x.race) && 
						Settings.mountableCache.Contains(x.shortHash) && parms.points > x.combatPower * 2f).ToArray();

					//Override mount chance
					if (factionRules.mountChance > -1) mountChance = factionRules.mountChance;

					//Apply weights if needed
					if (wildAnimals.Length == 0) localWeight = foreignWeight = 0;
					else if (factionRules.wildAnimalWeight >= 0) foreignWeight = factionRules.wildAnimalWeight;

					if (domesticAnimals.Length == 0) domesticWeight = 0;
					else if (factionRules.nonWildAnimalWeight >= 0) foreignWeight = factionRules.nonWildAnimalWeight;
				}
				else
				{
					wildAnimals = allWildAnimals.ToArray();
					domesticAnimals = allDomesticAnimals.ToArray();
					localAnimals = map.Biome.AllWildAnimals.
						Where(x => map.mapTemperature.SeasonAcceptableFor(x.race) && Settings.mountableCache.Contains(x.shortHash) && parms.points > x.combatPower * 2f).ToArray();
				}

			}
			float AverageAnimalCommonality(BiomeDef biome)
			{
				float sum = 0;
				float count = 0f;
				foreach (PawnKindDef animalKind in biome.AllWildAnimals)
				{
					sum += biome.CommonalityOfAnimal(animalKind);
					++count;
				}
				return sum / count;
			}
			float CalculateCommonality(PawnKindDef def, BiomeDef biome, int pawnHandlingLevel, float averageCommonality = 0)
			{
				float commonality;
				if (averageCommonality == 0) commonality = biome.CommonalityOfAnimal(def);
				else commonality = averageCommonality;

				//minimal level to get bonus. 
				pawnHandlingLevel = pawnHandlingLevel > 5 ? pawnHandlingLevel - 5 : 0;

				//Common animals more likely when pawns have low handling, and rare animals more likely when pawns have high handling.  
				float commonalityAdjusted = commonality * ((15f - (float)commonality)) / 15f + (1 - commonality) * ((float)pawnHandlingLevel) / 15f;
				//Wildness decreases the likelyhood of the mount being picked. Handling level mitigates this. 
				float wildnessPenalty = 1 - (def.RaceProps.wildness * ((15f - (float)pawnHandlingLevel) / 15f));

				//Log.Message("name: " + def.defName + ", commonality: " + commonality + ", pawnHandlingLevel: " + pawnHandlingLevel + ", wildness: " + def.RaceProps.wildness + ", commonalityBonus: " + commonalityAdjusted + ", wildnessPenalty: " + wildnessPenalty + ", result: " + commonalityAdjusted * wildnessPenalty);
				return commonalityAdjusted * wildnessPenalty;
			}
			bool GetPackAnimals(List<Pawn> list, List<Pawn> packAnimals)
			{
				if (list.NullOrEmpty()) return false;

				var length = list.Count;
				for (int i = 0; i < length; i++)
				{
					Pawn pawn = list[i];
					if (pawn.IsEverMountable(out IsMountableUtility.Reason reason) && pawn.RaceProps.packAnimal && pawn.inventory != null && pawn.inventory.innerContainer.Count > 0)
					{
						packAnimals.Add(pawn);
					}
					else if (Settings.logging) Log.Message("[Giddy-Up] Skipping " + pawn.def.defName +"-"+ pawn.thingIDNumber.ToString() + ". Reason: " + reason.ToString());
				}
				return packAnimals.Count == 0 ? false : true;
			}
			#endregion
		}
		//Gets animal that'll get the pawn to the target the quickest. Returns null if no animal is found or if walking is faster. 
		public static bool GetBestAnimal(Pawn pawn, out Pawn bestAnimal, IntVec3 firstTarget, IntVec3 secondTarget, float pawnTargetDistance, float firstToSecondTargetDistance, ExtendedPawnData pawnData)
		{
			//Prepare locals
			float pawnWalkSpeed = pawn.GetStatValue(StatDefOf.MoveSpeed);
			float timeNormalWalking = (pawnTargetDistance + firstToSecondTargetDistance) / pawnWalkSpeed;
			bool firstTargetInForbiddenArea = false;
			bool secondTargetInForbiddenArea = false;
			Map map = pawn.Map;
			map.GetGUAreas(out Area areaNoMount, out Area areaDropAnimal);

			//This notes that the first destination is in a no-ride zone
			IntVec3[] areaDropCache = new IntVec3[0];
			if (areaNoMount != null && areaDropAnimal != null)
			{
				firstTargetInForbiddenArea = areaNoMount.innerGrid[map.cellIndices.CellToIndex(firstTarget)];
				secondTargetInForbiddenArea = secondTarget.y >= 0 && areaNoMount.innerGrid[map.cellIndices.CellToIndex(secondTarget)];
				areaDropCache = areaDropAnimal.ActiveCells.ToArray();
			}

			//Start looking for an animal
			Pawn closestAnimal = null;
			float timeBestRiding = float.MaxValue;
			float distanceBestRiding = float.MaxValue;
			var reservedAnimals = map.FetchReservedAnimals();
			var list = map.mapPawns.pawnsSpawned;
			var length = list.Count;
			for (int i = 0; i < length; i++)
			{
				Pawn animal = list[i];

				if (!animal.IsMountable(out IsMountableUtility.Reason reason, pawn, checkState: true, checkFaction: true, reservationsToCheck: reservedAnimals)) 
				{
					if (Settings.logging && reason != IsMountableUtility.Reason.WrongFaction && reason != IsMountableUtility.Reason.NotInModOptions && 
						reason != IsMountableUtility.Reason.NotAnimal) Log.Message("[Giddy-Up] " + pawn.Label + " will not ride " + animal.Label + " because: " + reason.ToString());
					continue;
				}
			
				if (!pawn.IsAllowed(animal)) continue; //Disallowed

				float distancePawnToAnimal = (float)Math.Pow(pawn.Position.DistanceTo(animal.Position), 1.05); //Penalize farther mounts to account for presumed non-straight paths
				float distanceRiding = (animal.Position.DistanceTo(firstTarget) + firstToSecondTargetDistance) * 1.05f;
				float timeNeededForThisMount = (distancePawnToAnimal / pawnWalkSpeed) + (distanceRiding / StatPart_Riding.GetRidingSpeed(animal.GetStatValue(StatDefOf.MoveSpeed), animal, pawn.skills));
				
				if (timeNeededForThisMount < timeBestRiding)
				{
					closestAnimal = animal;
					timeBestRiding = timeNeededForThisMount;
					distanceBestRiding = distanceRiding; //Only used for logging
				}
			}
			
			if (Settings.logging)
			{
				if (closestAnimal == null) Log.Message("[Giddy-Up] " + pawn.Label + " tried to find an animal but couldn't find any.");
				else Log.Message("[Giddy-Up] report for " + pawn.Label + ":\n" +
				"Animal: " + closestAnimal.Label + "\n" +
				"First target: " + firstTarget + "\n" + 
				"Second target: " + secondTarget + "\n" + 
				"Normal walking speed: " + (int)pawnWalkSpeed + "\n" + 
				"Normal walking distance: " + (int)(pawnTargetDistance + firstToSecondTargetDistance) + "\n" + 
				"Normal walking time: " + (int)timeNormalWalking + "\n" + 
				"Distance to animal: " + (int)pawn.Position.DistanceTo(closestAnimal.Position) + "\n" + 
				"Ride distance: " + (int)distanceBestRiding  + "\n" + 
				"Ride speed: " + (int)StatPart_Riding.GetRidingSpeed(closestAnimal.GetStatValue(StatDefOf.MoveSpeed), closestAnimal, pawn.skills) + "\n" +
				"Ride time: " + (int)((pawn.Position.DistanceTo(closestAnimal.Position) / pawnWalkSpeed) + 
					(distanceBestRiding / StatPart_Riding.GetRidingSpeed(closestAnimal.GetStatValue(StatDefOf.MoveSpeed), closestAnimal, pawn.skills)))
				);
			}

			if (timeBestRiding < timeNormalWalking)
			{
				bestAnimal = closestAnimal;

				//Known dead zones
				if (ExtendedDataStorage.GUComp.badSpots.Contains(firstTarget))
				{
					//Check if this blacklisting is still valid
					if (pawn.FindPlaceToDismount(areaDropAnimal, areaNoMount, firstTarget, out IntVec3 dismountingAt, bestAnimal, out MountUtility.DismountLocationType dismountLocationType))
					{
						ExtendedDataStorage.GUComp.badSpots.Remove(firstTarget);
					}
					else return false;
				}

				return true;
			}
			bestAnimal = null;
			return false;
		}
		public static bool IsRoped(this Pawn animal)
		{
			return animal.roping != null && animal.roping.IsRoped;
		}
		public static void CancelRopers(this Pawn animal)
		{
			var workingList = new HashSet<Pawn>();
			animal.Map.reservationManager.ReserversOf(animal, workingList);
			foreach (var pawn in workingList)
			{
				if (pawn.CurJobDef == JobDefOf.RopeToPen)
				{
					if (Settings.logging) Log.Message("[Giddy-Up] " + pawn.Label + " was roping " + animal.Label + ", but stopped to give rider priority.");
					pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false, canReturnToPool: false);
				}
			}
		}
	}
}