using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using R2API;
using R2API.Utils;
using RoR2;
using UnityEngine.Networking;

namespace ScalableInfusion
{
    [BepInPlugin(ModGuid, ModName, ModVer)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.EveryoneNeedSameModVersion)]

    [BepInDependency(R2API.R2API.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [R2APISubmoduleDependency(nameof(LanguageAPI))]
    public class ScalableInfusionPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.Windows10CE.ScalableInfusion";
        public const string ModName = "ScalableInfusion";
        public const string ModVer = "1.0.1";

        internal static ConfigEntry<bool> modEnabled;
        internal static ConfigEntry<float> percentHealth;
        internal static ConfigEntry<float> maxHealth;
        internal static ConfigEntry<int> healthPerKill;

        internal static Dictionary<NetworkInstanceId, List<int>> InfusionCounters;
        private bool anyErrors = false;

        public void Awake()
        {
            modEnabled = Config.Bind<bool>("ScalableInfusion", nameof(modEnabled), true, "Whether ScalableInfusion is enabled or not.");
            percentHealth = Config.Bind<float>("ScalableInfusion", nameof(percentHealth), 0.20f, "The max amount of health one stack of infusion can give you (% of base health). 25% = 0.25. Set to 0 for each infusion to never stop giving health.");
            maxHealth = Config.Bind<float>("ScalableInfusion", nameof(maxHealth), 0f, "The max amount of health all of your infusions can give you, regardless of how many stacks you have (% of base health). 80% = 0.80. Set to 0 for there to be no limit on how much health you can get.");
            healthPerKill = Config.Bind<int>("ScalableInfusion", nameof(healthPerKill), 1, "How much health each stack of infusion should give you per kill.");

            if (!modEnabled.Value)
                return;
            On.RoR2.Run.Start += (orig, self) =>
            {
                InfusionCounters = new Dictionary<NetworkInstanceId, List<int>>();
                orig(self);
            };

            try
            {
                IL.RoR2.CharacterBody.RecalculateStats += RecalculateStatsInfusionHook;
                IL.RoR2.GlobalEventManager.OnCharacterDeath += OnCharacterDeathInfusionHook;
            }
            catch
            {
                anyErrors = true;
            }
            finally
            {
                if (anyErrors)
                {
                    Logger.LogError("An error occurred while hooking, unpatching methods...");
                    IL.RoR2.CharacterBody.RecalculateStats -= RecalculateStatsInfusionHook;
                    IL.RoR2.GlobalEventManager.OnCharacterDeath -= OnCharacterDeathInfusionHook;
                }
                else
                {
                    string langString = $"Increases health by <style=cIsHealing>{healthPerKill.Value} <style=cStack>(+{healthPerKill.Value} per stack)</style></style> per <style=cDeath>enemy death</style>";
                    if (percentHealth.Value > 0)
                        langString += $" up to <style=cUserSetting>{percentHealth.Value * 100}% <style=cStack>(+{percentHealth.Value * 100}% per stack)</style></style> of your base health.";
                    else
                        langString += ".";
                    if (maxHealth.Value > 0)
                        langString += $" Caps at <style=cUserSetting>{maxHealth.Value * 100}%</style> of your base health.";
                    LanguageAPI.Add("ITEM_INFUSION_PICKUP", langString);
                }
            }
        }

        private void RecalculateStatsInfusionHook(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            int infusionCountLoc = 0;
            int infusionBonusLoc = 34;
            int baseHealthLoc = 41;

            bool found = false;

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdcI4((int)ItemIndex.Infusion),
                x => x.MatchCallOrCallvirt(typeof(Inventory).GetMethod(nameof(Inventory.GetItemCount))),
                x => x.MatchStloc(out infusionCountLoc)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where CharacterBody::RecalculateStats() checks infusion stack count, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt(typeof(Inventory).GetProperty(nameof(Inventory.infusionBonus)).GetGetMethod()),
                x => x.MatchStloc(out infusionBonusLoc)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where CharacterBody::RecalculateStats() checks infusion bonus, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.Before,
                x => x.MatchLdloc(out baseHealthLoc),
                x => x.MatchLdloc(infusionBonusLoc),
                x => x.MatchConvRUn(),
                x => x.MatchConvR4(),
                x => x.MatchAdd(),
                x => x.MatchStloc(baseHealthLoc)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where the infusion bonus is applied in CharacterBody::RecalculateStats(), aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            c.RemoveRange(6);
            c.Emit(OpCodes.Ldloc, baseHealthLoc);
            c.Emit(OpCodes.Dup);
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<float, CharacterBody, float>>((baseHealth, cb) => 
            {
                List<int> infusionCounts;
                if (!ScalableInfusionPlugin.InfusionCounters.TryGetValue(cb.netId, out infusionCounts))
                    return 0f;
                float total = 0;
                int percentHealth = (int)(baseHealth * ScalableInfusionPlugin.percentHealth.Value);
                int maxHealth = (int)(baseHealth * ScalableInfusionPlugin.maxHealth.Value);
                int healthPerKill = ScalableInfusionPlugin.healthPerKill.Value;
                foreach (int kills in infusionCounts)
                    total += Math.Min(kills * healthPerKill, percentHealth > 0 ? percentHealth : float.PositiveInfinity);

                return Math.Min(total, maxHealth > 0 ? maxHealth : float.PositiveInfinity);
            });
            c.Emit(OpCodes.Add);
            c.Emit(OpCodes.Stloc, baseHealthLoc);
        }

        private void OnCharacterDeathInfusionHook(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            int attackerBodyLoc = 13;
            int infusionCountLoc = 33;

            bool found = false;

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdarg(1),
                x => x.MatchLdfld(typeof(DamageReport).GetField("attackerBody")),
                x => x.MatchStloc(out attackerBodyLoc)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where OnCharacterDeath gets attackerBody from the damageReport, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdcI4((int)ItemIndex.Infusion),
                x => x.MatchCallOrCallvirt(typeof(Inventory).GetMethod(nameof(Inventory.GetItemCount))),
                x => x.MatchStloc(out infusionCountLoc)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where OnCharacterDeath gets infusion count, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdloc(infusionCountLoc),
                x => x.MatchLdcI4(0),
                x => x.MatchBle(out _)
            );
            if (!found)
            {
                Logger.LogError("Couldn't find where OnCharacterDeath checks infusion count, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            c.RemoveRange(10);

            c.Emit(OpCodes.Ldloc, attackerBodyLoc);
            c.Emit(OpCodes.Ldloc, infusionCountLoc);
            c.EmitDelegate<Action<CharacterBody, int>>((cb, infusionCount) =>
            {
                List<int> infusionCounter = null;
                if (!ScalableInfusionPlugin.InfusionCounters.TryGetValue(cb.netId, out infusionCounter))
                    infusionCounter = new List<int>();

                for (int i = 0; i < infusionCount; i++)
                {
                    if (i >= infusionCounter.Count)
                        infusionCounter.Add(0);
                    infusionCounter[i] += 1;
                }

                ScalableInfusionPlugin.InfusionCounters[cb.netId] = infusionCounter;
            });
        }
    }
}
