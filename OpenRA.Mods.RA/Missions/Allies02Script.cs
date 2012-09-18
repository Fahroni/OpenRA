#region Copyright & License Information
/*
 * Copyright 2007-2012 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Mods.RA.Activities;
using OpenRA.Mods.RA.Air;
using OpenRA.Mods.RA.Buildings;
using OpenRA.Mods.RA.Effects;
using OpenRA.Mods.RA.Move;
using OpenRA.Network;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.RA.Missions
{
	class Allies02ScriptInfo : TraitInfo<Allies02Script>, Requires<SpawnMapActorsInfo> { }

	class Allies02Script : IWorldLoaded, ITick
	{
		static readonly string[] Objectives =
		{
			"Hold off the Soviet forces and destroy the SAM sites. Tanya and Einstein must survive.",
			"Wait for the helicopter and extract Einstein. Tanya and Einstein must survive."
		};

		int currentObjective;

		Actor sam1;
		Actor sam2;
		Actor sam3;
		Actor sam4;
		Actor tanya;
		Actor einstein;
		Actor engineer;

		Actor chinookHusk;
		Actor allies2BasePoint;
		Actor reinforcementsEntryPoint;
		Actor extractionLZEntryPoint;
		Actor extractionLZ;
		Actor badgerEntryPoint;
		Actor badgerDropPoint;
		Actor sovietRallyPoint;
		Actor flamersEntryPoint;

		Actor einsteinChinook;

		World world;
		Player allies1;
		Player allies2;
		Player soviets;

		Actor sovietBarracks;
		Actor sovietWarFactory;

		CountdownTimer reinforcementsTimer;
		CountdownTimerWidget reinforcementsTimerWidget;

		const string InfantryQueueName = "Infantry";
		const string VehicleQueueName = "Vehicle";
		readonly List<string> sovietInfantry = new List<string> { "e1", "e2", "e3" };
		readonly List<string> sovietVehicles = new List<string> { "3tnk" };
		static readonly string[] SovietVehicleAdditions = { "v2rl" };
		const int SovietGroupSize = 8;
		const int SovietVehicleAdditionsTicks = 1500 * 4;
		const int SovietHelperCash = 2000;

		const int ReinforcementsTicks = 1500 * 12;
		static readonly string[] Reinforcements = { "2tnk", "2tnk", "2tnk", "2tnk", "2tnk", "2tnk", "1tnk", "1tnk", "jeep", "e1", "e1", "e1", "e1", "e3", "e3", "mcv" };
		const int ReinforcementsCash = 2000;

		const int ParatroopersTicks = 1500 * 10;
		static readonly string[] Paratroopers = { "e1", "e1", "e1", "e2", "3tnk" };
		const string BadgerName = "badr";

		const int FlamersTicks = 1500 * 7;
		static readonly string[] Flamers = { "e4", "e4", "e4", "e4", "e4" };
		const string ApcName = "apc";

		const string ChinookName = "tran";
		const string SignalFlareName = "flare";
		const int EngineerSafeRange = 5;

		void DisplayObjective()
		{
			Game.AddChatLine(Color.LimeGreen, "Objective", Objectives[currentObjective]);
			Sound.Play("bleep6.aud");
		}

		void MissionFailed(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Lost;
			if (reinforcementsTimer != null)
			{
				reinforcementsTimerWidget.Visible = false;
			}
			foreach (var actor in world.Actors.Where(a => a.IsInWorld && (a.Owner == allies1 || a.Owner == allies2) && !a.IsDead()))
			{
				actor.Kill(actor);
			}
			Game.AddChatLine(Color.Red, "Mission failed", text);
			Sound.Play("misnlst1.aud");
		}

		void MissionAccomplished(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Won;
			if (reinforcementsTimer != null)
			{
				reinforcementsTimerWidget.Visible = false;
			}
			Game.AddChatLine(Color.Blue, "Mission accomplished", text);
			Sound.Play("misnwon1.aud");
		}

		public void Tick(Actor self)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			if (world.FrameNumber % 3500 == 1)
			{
				DisplayObjective();
			}
			if (world.FrameNumber % 50 == 1)
			{
				world.Add(new Smoke(world, chinookHusk.CenterLocation, "smoke_m"));
			}
			if (world.FrameNumber == 1)
			{
				InitializeSovietFactories();
				StartReinforcementsTimer();
			}
			reinforcementsTimer.Tick();
			if (world.FrameNumber == ParatroopersTicks)
			{
				ParadropSovietUnits();
			}
			if (world.FrameNumber == FlamersTicks)
			{
				RushSovietFlamers();
			}
			if (world.FrameNumber == SovietVehicleAdditionsTicks)
			{
				sovietVehicles.AddRange(SovietVehicleAdditions);
			}
			if (world.FrameNumber % 25 == 0)
			{
				AddSovietCashIfRequired();
				BuildSovietUnits();
				ManageSovietUnits();
			}
			if (EngineerSafe())
			{
				RescueEngineer();
			}
			if (currentObjective == 0)
			{
				if (sam1.Destroyed && sam2.Destroyed && sam3.Destroyed && sam4.Destroyed)
				{
					currentObjective++;
					DisplayObjective();
					SpawnSignalFlare();
					Sound.Play("flaren1.aud");
					SendChinook();
				}
			}
			else if (currentObjective == 1 && einsteinChinook != null)
			{
				if (einsteinChinook.Destroyed)
				{
					MissionFailed("The extraction helicopter was destroyed.");
				}
				else if (!world.Map.IsInMap(einsteinChinook.Location) && einsteinChinook.Trait<Cargo>().Passengers.Contains(einstein))
				{
					MissionAccomplished("Einstein was rescued.");
				}
			}
			if (tanya.Destroyed)
			{
				MissionFailed("Tanya was killed.");
			}
			else if (einstein.Destroyed)
			{
				MissionFailed("Einstein was killed.");
			}
			else if (!world.Actors.Any(a => a.IsInWorld && a.HasTrait<Building>() && !a.HasTrait<Wall>() && a.Owner == allies2))
			{
				MissionFailed("The Allied reinforcements have been defeated.");
			}
		}

		void AddSovietCashIfRequired()
		{
			var resources = soviets.PlayerActor.Trait<PlayerResources>();
			if (resources.Cash < SovietHelperCash)
			{
				resources.GiveCash(SovietHelperCash);
			}
		}

		void BuildSovietUnits()
		{
			var powerManager = soviets.PlayerActor.Trait<PowerManager>();
			if (powerManager.ExcessPower < 0)
			{
				return;
			}
			if (!sovietBarracks.Destroyed)
			{
				BuildSovietUnit(InfantryQueueName, sovietInfantry.Random(world.SharedRandom));
			}
			if (!sovietWarFactory.Destroyed)
			{
				BuildSovietUnit(VehicleQueueName, sovietVehicles.Random(world.SharedRandom));
			}
		}

		void ManageSovietUnits()
		{
			var idleSovietUnitsAtRP = world.ForcesNearLocation(sovietRallyPoint.CenterLocation, 3).Where(a => a.Owner == soviets && a.IsIdle && a.HasTrait<Mobile>());
			if (idleSovietUnitsAtRP.Count() >= SovietGroupSize)
			{
				var firstUnit = idleSovietUnitsAtRP.FirstOrDefault();
				if (firstUnit != null)
				{
					var closestAlliedBuilding = ClosestAlliedBuilding(firstUnit, 40);
					if (closestAlliedBuilding != null)
					{
						foreach (var unit in idleSovietUnitsAtRP)
						{
							unit.Trait<Mobile>().Nudge(unit, unit, true);
							unit.QueueActivity(new AttackMove.AttackMoveActivity(unit, new Move.Move(closestAlliedBuilding.Location, 3)));
						}
					}
				}
			}
			var idleSovietUnits = world.ForcesNearLocation(allies2BasePoint.CenterLocation, 20).Where(a => a.Owner == soviets && a.IsIdle);
			foreach (var unit in idleSovietUnits)
			{
				var closestAlliedBuilding = ClosestAlliedBuilding(unit, 40);
				if (closestAlliedBuilding != null)
				{
					unit.QueueActivity(new AttackMove.AttackMoveActivity(unit, new Move.Move(closestAlliedBuilding.Location, 3)));
				}
			}
		}

		Actor ClosestAlliedBuilding(Actor actor, int range)
		{
			return MissionUtils.ClosestPlayerBuilding(world, allies2, actor.CenterLocation, range);
		}

		void InitializeSovietFactories()
		{
			var sbrp = sovietBarracks.Trait<RallyPoint>();
			var swrp = sovietWarFactory.Trait<RallyPoint>();
			sbrp.rallyPoint = swrp.rallyPoint = sovietRallyPoint.Location;
			sbrp.nearEnough = swrp.nearEnough = 3;
			sovietBarracks.Trait<PrimaryBuilding>().SetPrimaryProducer(sovietBarracks, true);
			sovietWarFactory.Trait<PrimaryBuilding>().SetPrimaryProducer(sovietWarFactory, true);
		}

		IEnumerable<ProductionQueue> FindQueues(Player player, string category)
		{
			return world.ActorsWithTrait<ProductionQueue>()
				.Where(a => a.Actor.Owner == player && a.Trait.Info.Type == category)
				.Select(a => a.Trait);
		}

		void BuildSovietUnit(string category, string unit)
		{
			var queue = FindQueues(soviets, category).FirstOrDefault(q => q.CurrentItem() == null);
			if (queue == null)
			{
				return;
			}
			if (Game.IsHost)
			{
				world.IssueOrder(Order.StartProduction(queue.self, unit, 1));
			}
		}

		void SpawnSignalFlare()
		{
			world.CreateActor(SignalFlareName, new TypeDictionary { new OwnerInit(allies1), new LocationInit(extractionLZ.Location) });
		}

		void StartReinforcementsTimer()
		{
			Sound.Play("timergo1.aud");
			reinforcementsTimer = new CountdownTimer(ReinforcementsTicks, ReinforcementsTimerExpired);
			reinforcementsTimerWidget = new CountdownTimerWidget(reinforcementsTimer, "Reinforcements arrive in", new float2(128, 96));
			Ui.Root.AddChild(reinforcementsTimerWidget);
		}

		void ParadropSovietUnits()
		{
			var badger = world.CreateActor(BadgerName, new TypeDictionary
			{
				new LocationInit(badgerEntryPoint.Location),
				new OwnerInit(soviets),
				new FacingInit(Util.GetFacing(badgerDropPoint.Location - badgerEntryPoint.Location, 0)),
				new AltitudeInit(Rules.Info[BadgerName].Traits.Get<PlaneInfo>().CruiseAltitude),
			});
			badger.QueueActivity(new FlyAttack(Target.FromCell(badgerDropPoint.Location)));
			badger.Trait<ParaDrop>().SetLZ(badgerDropPoint.Location);
			var cargo = badger.Trait<Cargo>();
			foreach (var unit in Paratroopers)
			{
				cargo.Load(badger, world.CreateActor(false, unit, new TypeDictionary { new OwnerInit(soviets) }));
			}
		}

		void RushSovietFlamers()
		{
			var closestAlliedBuilding = ClosestAlliedBuilding(badgerDropPoint, 10);
			if (closestAlliedBuilding == null)
			{
				return;
			}
			var apc = world.CreateActor(ApcName, new TypeDictionary { new OwnerInit(soviets), new LocationInit(flamersEntryPoint.Location) });
			foreach (var flamer in Flamers)
			{
				var unit = world.CreateActor(false, flamer, new TypeDictionary { new OwnerInit(soviets) });
				apc.Trait<Cargo>().Load(apc, unit);
			}
			apc.QueueActivity(new MoveAdjacentTo(Target.FromActor(closestAlliedBuilding)));
			apc.QueueActivity(new UnloadCargo(true));
		}

		void ReinforcementsTimerExpired(CountdownTimer countdownTimer)
		{
			reinforcementsTimerWidget.Visible = false;
			SendReinforcements();
		}

		void SendReinforcements()
		{
			Sound.Play("reinfor1.aud");
			var resources = allies2.PlayerActor.Trait<PlayerResources>();
			resources.GiveCash(ReinforcementsCash);
			foreach (var unit in Reinforcements)
			{
				var actor = world.CreateActor(unit, new TypeDictionary
				{
					new LocationInit(reinforcementsEntryPoint.Location),
					new FacingInit(0),
					new OwnerInit(allies2)
				});
				actor.QueueActivity(new Move.Move(allies2BasePoint.Location));
			}
		}

		void SendChinook()
		{
			einsteinChinook = world.CreateActor(ChinookName, new TypeDictionary { new OwnerInit(allies1), new LocationInit(extractionLZEntryPoint.Location) });
			einsteinChinook.QueueActivity(new HeliFly(extractionLZ.CenterLocation));
			einsteinChinook.QueueActivity(new Turn(0));
			einsteinChinook.QueueActivity(new HeliLand(true, 0));
			einsteinChinook.QueueActivity(new WaitFor(() => einsteinChinook.Trait<Cargo>().Passengers.Contains(einstein)));
			einsteinChinook.QueueActivity(new Wait(150));
			einsteinChinook.QueueActivity(new HeliFly(extractionLZEntryPoint.CenterLocation));
			einsteinChinook.QueueActivity(new RemoveSelf());
		}

		bool EngineerSafe()
		{
			if (engineer.Destroyed)
			{
				return false;
			}
			var units = world.ForcesNearLocation(engineer.CenterLocation, EngineerSafeRange);
			return units.Any() && units.All(a => a.Owner == allies1);
		}

		void RescueEngineer()
		{
			if (!engineer.Destroyed)
			{
				engineer.ChangeOwner(allies1);
			}
		}

		public void WorldLoaded(World w)
		{
			world = w;
			allies1 = w.Players.Single(p => p.InternalName == "Allies1");
			allies2 = w.Players.Single(p => p.InternalName == "Allies2");
			soviets = w.Players.Single(p => p.InternalName == "Soviets");
			var actors = w.WorldActor.Trait<SpawnMapActors>().Actors;
			sam1 = actors["SAM1"];
			sam2 = actors["SAM2"];
			sam3 = actors["SAM3"];
			sam4 = actors["SAM4"];
			tanya = actors["Tanya"];
			einstein = actors["Einstein"];
			chinookHusk = actors["ChinookHusk"];
			allies2BasePoint = actors["Allies2BasePoint"];
			reinforcementsEntryPoint = actors["ReinforcementsEntryPoint"];
			extractionLZ = actors["ExtractionLZ"];
			extractionLZEntryPoint = actors["ExtractionLZEntryPoint"];
			badgerEntryPoint = actors["BadgerEntryPoint"];
			badgerDropPoint = actors["BadgerDropPoint"];
			engineer = actors["Engineer"];
			sovietBarracks = actors["SovietBarracks"];
			sovietWarFactory = actors["SovietWarFactory"];
			sovietRallyPoint = actors["SovietRallyPoint"];
			flamersEntryPoint = actors["FlamersEntryPoint"];
			var shroud = w.WorldActor.Trait<Shroud>();
			shroud.Explore(w, sam1.Location, 2);
			shroud.Explore(w, sam2.Location, 2);
			shroud.Explore(w, sam3.Location, 2);
			shroud.Explore(w, sam4.Location, 2);
			if (w.LocalPlayer == null || w.LocalPlayer == allies1)
			{
				Game.MoveViewport(chinookHusk.Location.ToFloat2());
			}
			else
			{
				Game.MoveViewport(allies2BasePoint.Location.ToFloat2());
			}
			PlayMusic();
			Game.ConnectionStateChanged += StopMusic;
		}

		void PlayMusic()
		{
			if (!Rules.InstalledMusic.Any())
			{
				return;
			}
			var track = Rules.InstalledMusic.Random(Game.CosmeticRandom);
			Sound.PlayMusicThen(track.Value, PlayMusic);
		}

		void StopMusic(OrderManager orderManager)
		{
			if (!orderManager.GameStarted)
			{
				Sound.StopMusic();
				Game.ConnectionStateChanged -= StopMusic;
			}
		}
	}
}
