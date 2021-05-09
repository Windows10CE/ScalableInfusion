using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using RoR2;
using HarmonyLib;
using BepInEx.Logging;

namespace ScalableInfusion
{
    [BepInPlugin(ModGuid, ModName, ModVer)]

    [BepInDependency(ModCommon.ModCommonPlugin.ModGUID, BepInDependency.DependencyFlags.HardDependency)]
    [ModCommon.NetworkModlistInclude]
    public class ScalableInfusionPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.Windows10CE.ScalableInfusion";
        public const string ModName = "ScalableInfusion";
        public const string ModVer = "1.1.0";

        internal static ConfigEntry<bool> modEnabled;
        internal static ConfigEntry<float> percentHealth;
        internal static ConfigEntry<float> maxHealth;
        internal static ConfigEntry<int> healthPerKill;

        internal static Harmony HarmonyInstance = new Harmony(ModGuid);

        new internal static ManualLogSource Logger;

        public void Awake()
        {
            ScalableInfusionPlugin.Logger = base.Logger;

            modEnabled = Config.Bind<bool>("ScalableInfusion", nameof(modEnabled), true, "Whether ScalableInfusion is enabled or not.");
            percentHealth = Config.Bind<float>("ScalableInfusion", nameof(percentHealth), 0.20f, "The max amount of health one stack of infusion can give you (% of base health). 25% = 0.25. Set to 0 for each infusion to never stop giving health.");
            maxHealth = Config.Bind<float>("ScalableInfusion", nameof(maxHealth), 0f, "The max amount of health all of your infusions can give you, regardless of how many stacks you have (% of base health). 80% = 0.80. Set to 0 for there to be no limit on how much health you can get.");
            healthPerKill = Config.Bind<int>("ScalableInfusion", nameof(healthPerKill), 1, "How much health each stack of infusion should give you per kill.");

            if (!modEnabled.Value)
                return;

            try
            {
                HarmonyInstance.PatchAll(typeof(Patches));
            }
            catch (Exception e)
            {
                Patches.anyErrors = true;
                Logger.LogError(e.ToString());
            }

            if (Patches.anyErrors)
            {
                HarmonyInstance.UnpatchSelf();
                return;
            }

            string langString = $"Increases health by <style=cIsHealing>{healthPerKill.Value} <style=cStack>(+{healthPerKill.Value} per stack)</style></style> per <style=cDeath>enemy death</style>";
            if (percentHealth.Value > 0)
                langString += $" up to <style=cUserSetting>{percentHealth.Value * 100}% <style=cStack>(+{percentHealth.Value * 100}% per stack)</style></style> of your base health.";
            else
                langString += ".";
            if (maxHealth.Value > 0)
                langString += $" Caps at <style=cUserSetting>{maxHealth.Value * 100}%</style> of your base health.";
            ModCommon.LanguageTokens.Add("ITEM_INFUSION_PICKUP", langString);
        }
    }


    internal static class Patches
    {
        internal static bool anyErrors = false;

        [HarmonyILManipulator]
        [HarmonyPatch(typeof(CharacterBody), nameof(CharacterBody.RecalculateStats))]
        internal static void RecalculateStatsInfusionHook(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            int infusionCountLoc = 3;
            int infusionBonusLoc = 40;
            int baseHealthLoc = 50;

            bool found = false;

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdsfld(AccessTools.Field(typeof(RoR2Content.Items), nameof(RoR2Content.Items.Infusion))),
                x => x.MatchCallOrCallvirt(AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemCount), parameters: new Type[] { typeof(ItemDef) })),
                x => x.MatchStloc(out infusionCountLoc)
            );
            if (!found)
            {
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where CharacterBody::RecalculateStats() checks infusion stack count, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt(AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.infusionBonus))),
                x => x.MatchStloc(out infusionBonusLoc)
            );
            if (!found)
            {
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where CharacterBody::RecalculateStats() checks infusion bonus, aborting ScalableInfusion hooks...");
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
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where the infusion bonus is applied in CharacterBody::RecalculateStats(), aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            c.RemoveRange(6);
            c.Emit(OpCodes.Ldloc, baseHealthLoc);
            c.Emit(OpCodes.Dup);
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<float, CharacterBody, float>>((baseHealth, cb) =>
            {
                var tracker = cb.master.gameObject.GetComponent<InfusionTracker>();
                if (!tracker)
                    return 0f;
                float total = 0;
                int percentHealth = (int)(baseHealth * ScalableInfusionPlugin.percentHealth.Value);
                int maxHealth = (int)(baseHealth * ScalableInfusionPlugin.maxHealth.Value);
                int healthPerKill = ScalableInfusionPlugin.healthPerKill.Value;
                foreach (int kills in tracker.Tracker)
                    total += Math.Min(kills * healthPerKill, percentHealth > 0 ? percentHealth : float.PositiveInfinity);

                return Math.Min(total, maxHealth > 0 ? maxHealth : float.PositiveInfinity);
            });
            c.Emit(OpCodes.Add);
            c.Emit(OpCodes.Stloc, baseHealthLoc);
        }

        [HarmonyILManipulator]
        [HarmonyPatch(typeof(GlobalEventManager), nameof(GlobalEventManager.OnCharacterDeath))]
        internal static void OnCharacterDeathInfusionHook(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            int attackerBodyLoc = 13;
            int infusionCountLoc = 36;

            bool found = false;

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdarg(1),
                x => x.MatchLdfld(AccessTools.Field(typeof(DamageReport), nameof(DamageReport.attackerBody))),
                x => x.MatchStloc(out attackerBodyLoc)
            );
            if (!found)
            {
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where OnCharacterDeath gets attackerBody from the damageReport, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            found = c.TryGotoNext(MoveType.After,
                x => x.MatchLdsfld(AccessTools.Field(typeof(RoR2Content.Items), nameof(RoR2Content.Items.Infusion))),
                x => x.MatchCallOrCallvirt(AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemCount), parameters: new Type[] { typeof(ItemDef) })),
                x => x.MatchStloc(out infusionCountLoc)
            );
            if (!found)
            {
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where OnCharacterDeath gets infusion count, aborting ScalableInfusion hooks...");
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
                ScalableInfusionPlugin.Logger.LogError("Couldn't find where OnCharacterDeath checks infusion count, aborting ScalableInfusion hooks...");
                anyErrors = true;
                return;
            }

            c.RemoveRange(10);

            c.Emit(OpCodes.Ldloc, attackerBodyLoc);
            c.Emit(OpCodes.Ldloc, infusionCountLoc);
            c.EmitDelegate<Action<CharacterBody, int>>((cb, infusionCount) =>
            {
                var tracker = cb.master.gameObject.GetComponent<InfusionTracker>();
                if (!tracker)
                    tracker = cb.master.gameObject.AddComponent<InfusionTracker>();

                for (int i = 0; i < infusionCount; i++)
                {
                    if (i >= tracker.Tracker.Count)
                        tracker.Tracker.Add(0);
                    tracker.Tracker[i] += 1;
                }
            });
        }
    }

    public class InfusionTracker : UnityEngine.MonoBehaviour
    {
        public List<int> Tracker = new List<int>();
    }
}
