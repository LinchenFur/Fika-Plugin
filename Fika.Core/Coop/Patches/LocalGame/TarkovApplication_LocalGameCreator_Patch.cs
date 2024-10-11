﻿using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core.Coop.GameMode;
using Fika.Core.Coop.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking.Http;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Fika.Core.Coop.Patches
{
	/// <summary>
	/// Created by: Paulov
	/// Paulov: Overwrite and use our own CoopGame instance instead
	/// </summary>
	internal class TarkovApplication_LocalGameCreator_Patch : ModulePatch
	{
		protected override MethodBase GetTargetMethod()
		{
			return typeof(TarkovApplication).GetMethod(nameof(TarkovApplication.method_46));
		}

		[PatchPrefix]
		public static bool Prefix(ref Task __result, TarkovApplication __instance, TimeAndWeatherSettings timeAndWeather, MatchmakerTimeHasCome.TimeHasComeScreenClass timeHasComeScreenController,
			RaidSettings ____raidSettings, InputTree ____inputTree, GameDateTime ____localGameDateTime, float ____fixedDeltaTime, string ____backendUrl, MetricsEventsClass metricsEvents,
			MetricsConfigClass metricsConfig, GameWorld gameWorld)
		{
#if DEBUG
			Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Prefix");

#endif
			__result = CreateFikaGame(__instance, timeAndWeather, timeHasComeScreenController, ____raidSettings,
				____inputTree, ____localGameDateTime, ____fixedDeltaTime, ____backendUrl,
				metricsEvents, metricsConfig, gameWorld);
			return false;
		}

		public static async Task CreateFikaGame(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather, MatchmakerTimeHasCome.TimeHasComeScreenClass timeHasComeScreenController,
			RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime, string backendUrl, MetricsEventsClass metricsEvents, MetricsConfigClass metricsConfig,
			GameWorld gameWorld)
		{
			bool isServer = FikaBackendUtils.IsServer;
			bool isTransit = FikaBackendUtils.IsTransit;

			if (isServer && !isTransit)
			{
				FikaBackendUtils.CachedRaidSettings = raidSettings;
			}
			else if (isServer && isTransit && FikaBackendUtils.CachedRaidSettings != null)
			{
				Logger.LogInfo("Applying cached raid settings from previous raid");
				RaidSettings cachedSettings = FikaBackendUtils.CachedRaidSettings;
				raidSettings.WavesSettings = cachedSettings.WavesSettings;
				raidSettings.BotSettings = cachedSettings.BotSettings;
				raidSettings.MetabolismDisabled = cachedSettings.MetabolismDisabled;
				raidSettings.PlayersSpawnPlace = cachedSettings.PlayersSpawnPlace;
			}

			metricsEvents.SetGamePrepared();

			LocationSettingsClass.Location location = raidSettings.SelectedLocation;

			if (Singleton<NotificationManagerClass>.Instantiated)
			{
				Singleton<NotificationManagerClass>.Instance.Deactivate();
			}

			ISession session = instance.Session;

			if (session == null)
			{
				throw new NullReferenceException("Backend session was null when initializing game!");
			}

			Profile profile = session.GetProfileBySide(raidSettings.Side);

			bool isDedicatedHost = session.Profile.Nickname.StartsWith("dedicated_");
			if (isDedicatedHost)
			{
				FikaBackendUtils.IsDedicated = true;
			}

			profile.Inventory.Stash = null;
			profile.Inventory.QuestStashItems = null;
			profile.Inventory.DiscardLimits = Singleton<ItemFactoryClass>.Instance.GetDiscardLimits();

#if DEBUG
			Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Postfix: Attempt to set Raid Settings");
#endif

			await session.SendRaidSettings(raidSettings);
			LocalRaidSettings localRaidSettings = new()
			{
				location = raidSettings.LocationId,
				timeVariant = raidSettings.SelectedDateTime,
				mode = ELocalMode.PVE_OFFLINE,
				playerSide = raidSettings.Side,
				isLocationTransition = raidSettings.isLocationTransition
			};
			Traverse applicationTraverse = Traverse.Create(instance);
			applicationTraverse.Field<LocalRaidSettings>("localRaidSettings_0").Value = localRaidSettings;

			LocalSettings localSettings = await instance.Session.LocalRaidStarted(localRaidSettings);
			applicationTraverse.Field<LocalRaidSettings>("localRaidSettings_0").Value.serverId = localSettings.serverId;
			applicationTraverse.Field<LocalRaidSettings>("localRaidSettings_0").Value.selectedLocation = localSettings.locationLoot;
			applicationTraverse.Field<LocalRaidSettings>("localRaidSettings_0").Value.transition = FikaBackendUtils.TransitData;

			GClass1284 profileInsurance = localSettings.profileInsurance;
			if ((profileInsurance?.insuredItems) != null)
			{
				profile.InsuredItems = localSettings.profileInsurance.insuredItems;
			}

			if (!isServer)
			{
				timeHasComeScreenController.ChangeStatus("Joining coop game...");

				RaidSettingsRequest data = new();
				RaidSettingsResponse raidSettingsResponse = await FikaRequestHandler.GetRaidSettings(data);

				raidSettings.MetabolismDisabled = raidSettingsResponse.MetabolismDisabled;
				raidSettings.PlayersSpawnPlace = raidSettingsResponse.PlayersSpawnPlace;
				timeAndWeather.HourOfDay = raidSettingsResponse.HourOfDay;
				timeAndWeather.TimeFlowType = raidSettingsResponse.TimeFlowType;
			}
			else
			{
				timeHasComeScreenController.ChangeStatus("Creating coop game...");
			}

			StartHandler startHandler = new(instance, session.Profile, session.ProfileOfPet, raidSettings.SelectedLocation, timeHasComeScreenController);

			TimeSpan raidLimits = instance.method_47(raidSettings.SelectedLocation.EscapeTimeLimit);

			CoopGame coopGame = CoopGame.Create(inputTree, profile, gameWorld, localGameDateTime, instance.Session.InsuranceCompany,
				MonoBehaviourSingleton<MenuUI>.Instance, MonoBehaviourSingleton<GameUI>.Instance, location,
				timeAndWeather, raidSettings.WavesSettings, raidSettings.SelectedDateTime, startHandler.HandleStop,
				fixedDeltaTime, instance.PlayerUpdateQueue, instance.Session, raidLimits, metricsEvents,
				new GClass2314(metricsConfig, instance), localRaidSettings, raidSettings);

			Singleton<AbstractGame>.Create(coopGame);
			metricsEvents.SetGameCreated();
			FikaEventDispatcher.DispatchEvent(new AbstractGameCreatedEvent(coopGame));

			if (!isServer)
			{
				coopGame.SetMatchmakerStatus("Coop game joined");
			}
			else
			{
				coopGame.SetMatchmakerStatus("Coop game created");
			}

			await coopGame.InitPlayer(raidSettings.BotSettings, backendUrl);
			using (CounterCreatorAbstractClass.StartWithToken("LoadingScreen.LoadComplete"))
			{
				GameObject.DestroyImmediate(MonoBehaviourSingleton<MenuUI>.Instance.gameObject);
				MainMenuController mmc = Traverse.Create(instance).Field<MainMenuController>("mainMenuController").Value;
				mmc?.Unsubscribe();
				gameWorld.OnGameStarted();

				if(FikaBackendUtils.IsSpectator)
				{
					await HandleJoinAsSpectator();
				}
			}
		}

		private class StartHandler(TarkovApplication tarkovApplication, Profile pmcProfile, Profile scavProfile,
			LocationSettingsClass.Location location, MatchmakerTimeHasCome.TimeHasComeScreenClass timeHasComeScreenController)
		{
			private readonly TarkovApplication tarkovApplication = tarkovApplication;
			private readonly Profile pmcProfile = pmcProfile;
			private readonly Profile scavProfile = scavProfile;
			private readonly LocationSettingsClass.Location location = location;
			private readonly MatchmakerTimeHasCome.TimeHasComeScreenClass timeHasComeScreenController = timeHasComeScreenController;

			public void HandleStop(Result<ExitStatus, TimeSpan, MetricsClass> result)
			{
				tarkovApplication.method_49(pmcProfile.Id, scavProfile, location, result, timeHasComeScreenController);
			}
		}

		private static async Task HandleJoinAsSpectator()
		{
			Player MainPlayer = Singleton<GameWorld>.Instance.MainPlayer;

			// Teleport the player underground to avoid it from being looted
			Vector3 currentPosition = MainPlayer.Position;
			MainPlayer.Teleport(new(currentPosition.x, currentPosition.y - 75, currentPosition.z));

			// Small delay to ensure the teleport command is processed first
			await Task.Delay(250);

			DamageInfo damageInfo = new()
			{
				Damage = 1000,
				DamageType = EDamageType.Impact
			};

			// Kill the player to put it in spectator mode
			MainPlayer.ApplyDamageInfo(damageInfo, EBodyPart.Head, EBodyPartColliderType.Eyes, 0);
		}
	}
}
