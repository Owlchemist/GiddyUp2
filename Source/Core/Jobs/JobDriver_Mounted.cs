using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;
using Settings = GiddyUp.ModSettings_GiddyUp;

namespace GiddyUp.Jobs
{
	public class JobDriver_Mounted : JobDriver
	{
		public static Dictionary<JobDef, bool> allowedJobs;
		public Pawn rider;
		ExtendedPawnData riderData;
		Map map;
		public bool isTrained, interrupted, isParking, isDespawning;
		IntVec3 startingPoint, dismountingAt, riderOriginalDestinaton;
		PathEndMode originalPeMode = PathEndMode.Touch;
		MountUtility.DismountLocationType dismountLocationType = MountUtility.DismountLocationType.Auto;
		int parkingFailures = 0;
		enum DismountReason { False, Interrupted, BadState, LeftMap, NotSpawned, WrongMount, BadJob, ForbiddenAreaAndCannotPark, Parking, ParkingFailSafe };

		public override IEnumerable<Toil> MakeNewToils()
		{
			this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
			rider = job.targetA.Thing as Pawn;
			riderData = rider.GetGUData();
			isTrained = pawn.training != null && pawn.training.HasLearned(TrainableDefOf.Obedience);
			map = Map;
			startingPoint = pawn.Position;
			yield return WaitForRider();
			yield return DelegateMovement();
		}
		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref this.isTrained, "isTrained");
			Scribe_Values.Look(ref this.interrupted, "interrupted");
			Scribe_Values.Look(ref this.isParking, "isParking");
			Scribe_Values.Look(ref this.dismountingAt, "dismountingAt");
			Scribe_Values.Look(ref this.dismountLocationType, "dismountLocationType");
			Scribe_Values.Look(ref this.originalPeMode, "originalPeMode");
			Scribe_Values.Look(ref this.riderOriginalDestinaton, "riderOriginalDestinaton");
		}
		public override bool TryMakePreToilReservations(bool errorOnFailed)
		{
			return true;
		}
		Toil WaitForRider()
		{
			return new Toil()
			{
				defaultCompleteMode = ToilCompleteMode.Never,
				tickAction = delegate
				{
					//Rider just mounted up, finish toil
					if (riderData.mount == pawn) ReadyForNextToil();

					//Something interrupted the rider, abort
					if (Current.gameInt.tickManager.ticksGameInt % 15 == 0) //Check 4 times per second
					{
						var curJobDef = rider.CurJobDef;
						if ((rider == null || rider.Dead || !rider.Spawned || rider.Downed || rider.InMentalState) ||
							//Rider changed their mind
							(curJobDef != ResourceBank.JobDefOf.Mount && 
							curJobDef != JobDefOf.Vomit && 
							curJobDef != JobDefOf.Wait_MaintainPosture && 
							curJobDef != JobDefOf.Wait && 
							riderData.mount == null) ||
							//Rider is cheating on this mount and went with another
							(rider.CurJobDef == ResourceBank.JobDefOf.Mount && rider.jobs.curDriver is JobDriver_Mount mountDriver && mountDriver.Mount != pawn))
						{
							if (Settings.logging) Log.Message("[Giddy-Up] " + pawn.Label + " is no longer waiting for " + rider.Label);
							interrupted = true;
							ReadyForNextToil();
						}
					}
				}
			};
		}
		Toil DelegateMovement()
		{
			return new Toil()
			{
				defaultCompleteMode = ToilCompleteMode.Never,
				tickAction = delegate
				{
					if (map == null) map = Map;
					if (CheckReason(RiderShouldDismount(riderData), out DismountReason dismountReason))
					{
						if (Settings.logging) Log.Message("[Giddy-Up] " + pawn.Label + " dismounting for reason: " + 
							dismountReason.ToString() + " (rider's job was: " + (rider.CurJobDef?.ToString() ?? "NULL" + ")"));

						//Check if something went wrong
						if (dismountReason == DismountReason.ParkingFailSafe) interrupted = true;

						ReadyForNextToil();
						return;
					}
					pawn.Drawer.tweener = rider.Drawer.tweener; //Could probably just be set once, but reloading could cause issues?
					pawn.Position = rider.Position;
					pawn.Rotation = rider.Rotation;
					if (isTrained) TryAttackEnemy(rider);
				},
				finishActions = new List<Action>() { (delegate
				{
					if (isParking) pawn.pather.StopDead();

					//Check mount first. If it's null then they must have dismounted outside the driver's control
					if (riderData.mount != null) rider.Dismount(
						animal: pawn,
						pawnData: riderData,
						clearReservation: false,
						parkLoc: isParking && pawn.Position.DistanceTo(dismountingAt) < 5f ? dismountingAt : default(IntVec3),
						waitForRider: !interrupted);
					isParking = false;

					//Check if the mount was meant to despawn along with the rider. This is already handled in the RiderShouldDismount but some spaghetti code elsewhere could bypass it
					//TODO: See if the two could be unified
					if (!isDespawning && rider != null && !pawn.Faction.IsPlayer && !rider.Spawned && pawn.Position.CloseToEdge(map, ResourceBank.mapEdgeIgnore))
					{
						isDespawning = true; //Avoid recurssive loop
						pawn.ExitMap(false, CellRect.WholeMap(map).GetClosestEdge(pawn.Position));
					}
				})}
			};
		}
		DismountReason RiderShouldDismount(ExtendedPawnData riderData)
		{
			if (interrupted || riderData == null || riderData.mount == null || riderData.ID != rider.thingIDNumber) return DismountReason.Interrupted;

			if (isParking)
			{
				if ((dismountLocationType == MountUtility.DismountLocationType.Auto && rider.pather.nextCell == dismountingAt) ||
					(dismountLocationType != MountUtility.DismountLocationType.Auto && dismountingAt.AdjacentTo8Way(rider.pather.nextCell)))
				{
					rider.pather.StartPath(riderOriginalDestinaton, originalPeMode); //Resume original work
					if (startingPoint.DistanceTo(dismountingAt) < 10f) return DismountReason.ParkingFailSafe;
					else return DismountReason.Parking;
				}
				else if (rider.pather.destination.Cell != dismountingAt)
				{
					isParking = false;
					if (parkingFailures++ == 3) return DismountReason.ParkingFailSafe; //Some sorta job is interferring with the parking, so just dismount.
				}
			}
			
			//Check remaining statements twice per second
			if (Current.gameInt.tickManager.ticksGameInt % 15 != 0) return DismountReason.False;
			
			//Check physical and mental health
			if (rider.Downed || rider.Dead || pawn.Downed || pawn.Dead || 
				pawn.HasAttachment(ThingDefOf.Fire) || rider.HasAttachment(ThingDefOf.Fire) || rider.GetPosture() != PawnPosture.Standing ||
				pawn.InMentalState || (rider.InMentalState && rider.MentalState.def != MentalStateDefOf.PanicFlee) ||
				pawn.Faction != rider.Faction //Quests can cause faction flips mid-mount
			)
			{
				return DismountReason.BadState;
			}

			//This will move the mount off the map, assuming their rider left the map as well
			if (!rider.Spawned)
			{
				var riderIsColonist = rider.IsColonist;
				if (!riderIsColonist || rider.GetCaravan() != null)
				{
					pawn.ExitMap(riderIsColonist, CellRect.WholeMap(map).GetClosestEdge(pawn.Position));
					return DismountReason.LeftMap;
				}
				else return DismountReason.NotSpawned;
			}

			var allowedJob = allowedJobs.TryGetValue(rider.CurJobDef, out bool checkTargets);
			var riderDestinaton = rider.pather.Destination.Cell;
			map.GetGUAreas(out Area areaNoMount, out Area areaDropAnimal);

			if (!rider.Drafted)
			{
				if (!isParking && Settings.rideAndRollEnabled)
				{
					//Special checks for allowedJobs
					if (allowedJob && checkTargets && rider.CurJob.targetA.Thing?.InteractionCell == riderDestinaton)
					{
						allowedJob = false;
					}
					//If the mount's non-drafted rider is heading towards a forbidden area, they'll need to dismount
					if ( (!allowedJob && rider.Position.DistanceTo(riderDestinaton) < 25f) || !riderDestinaton.CanRideAt(areaNoMount) )
					{
						isParking = true;
						if (rider.FindPlaceToDismount(areaDropAnimal, areaNoMount, riderDestinaton, out dismountingAt, pawn, out dismountLocationType))
						{
							riderOriginalDestinaton = riderDestinaton;
							originalPeMode = rider.pather.peMode;
							if (Settings.logging) Log.Message("[Giddy-Up] " + rider.Label + " wants to dismount at " + dismountingAt.ToString() + " which is a " + dismountLocationType.ToString());
							rider.pather.StartPath(dismountingAt, dismountLocationType == MountUtility.DismountLocationType.Auto ? PathEndMode.OnCell : PathEndMode.Touch);
						}
						else 
						{
							isParking = false;
							ExtendedDataStorage.GUComp.badSpots.Add(riderDestinaton);
							return DismountReason.ForbiddenAreaAndCannotPark;
						}
					}
				}
			}
			else
			{
				if (!allowedJob && rider.Position.DistanceTo(rider.pather.Destination.Cell) < ResourceBank.autoHitchDistance) return DismountReason.BadJob;
				if (!pawn.Faction.def.isPlayer) return DismountReason.False;
			}
			
			if (Settings.caravansEnabled)
			{
				var riderMindstateDef = rider.mindState?.duty?.def;
				if (riderMindstateDef == DutyDefOf.TravelOrWait || 
					riderMindstateDef == DutyDefOf.TravelOrLeave || 
					riderMindstateDef == DutyDefOf.PrepareCaravan_GatherAnimals || 
					riderMindstateDef == DutyDefOf.PrepareCaravan_GatherDownedPawns)
				{
					return riderData.reservedMount == pawn ? DismountReason.False : DismountReason.WrongMount;
				}
				
				if (rider.Position.CloseToEdge(map, ResourceBank.mapEdgeIgnore)) return DismountReason.False; //Caravan just entered map and has not picked a job yet on this tick.
			}
			return DismountReason.False;
		}
		bool CheckReason(DismountReason dismountReason, out DismountReason reason)
		{
			reason = dismountReason;
			return dismountReason != DismountReason.False;
		}
		void TryAttackEnemy(Pawn rider)
		{
			Thing targetThing = null;
			bool confirmedHostile = false;
			
			//The mount has something targetted but not the rider, so pass the target
			if (rider.TargetCurrentlyAimingAt != null)
			{
				targetThing = rider.TargetCurrentlyAimingAt.Thing;
			}
			//The rider is already trying to attack something
			else if (rider.CurJobDef == JobDefOf.AttackMelee && rider.CurJob.targetA.Thing.HostileTo(rider))
			{
				targetThing = rider.CurJob.targetA.Thing;
				confirmedHostile = true;
			}

			if (targetThing != null && (confirmedHostile || targetThing.HostileTo(rider)))
			{
				var modExt = pawn.def.GetModExtension<ResearchRestrictions>();
				if (modExt != null && modExt.researchProjectDefToAttack != null && !modExt.researchProjectDefToAttack.IsFinished)
				{
					return;
				}

				var verb = pawn.meleeVerbs?.TryGetMeleeVerb(targetThing);
				if (verb == null || !verb.CanHitTarget(targetThing))
				{
					pawn.TryStartAttack(targetThing); //Try start ranged attack if possible
				}
				else pawn.meleeVerbs.TryMeleeAttack(targetThing);
			}
		}
	}
}