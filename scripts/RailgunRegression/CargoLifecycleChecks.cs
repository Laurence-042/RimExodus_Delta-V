using System;
using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    sealed class LifecycleCargo : Building_SealedSpaceCargoContainer
    {
        public override void SetFaction(Faction faction, Pawn recruiter = null) => SetFactionDirect(faction);
    }

    static readonly List<Thing> crateDropped = new();
    // Replace only final placement/Unity map mesh work. Native Crate.Open,
    // EjectContents, CompHackable and ThingOwner all execute in this fixture.
    static bool DropCrateCargo(ThingOwner __instance, ref bool __result)
    {
        if (__instance.Owner is not LifecycleCargo) return true;
        foreach (Thing item in __instance.ToArray())
        { crateDropped.Add(item); __instance.Remove(item); }
        __result = true; return false;
    }
    static bool CargoHackedCallback(CompHackable __instance)
    { ((IHackable)__instance.parent).OnHacked(); return false; }

    static void CheckCargoLifecycle()
    {
        var patch = new Harmony("DeltaV.Regression.CargoLifecycle");
        var oldFactions = Find.World.factionManager;
        var oldIds = Current.Game.uniqueIDsManager;
        Current.Game.uniqueIDsManager = new UniqueIDsManager();
        bool oldGod = DebugSettings.godMode;
        var player = new Faction { def = new FactionDef { isPlayer = true } };
        var enemy = new Faction { def = new FactionDef() };
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        var cargoMap = (Map)Raw(typeof(Map));
        Find.Maps.Add(cargoMap);
        foreach (Map m in Find.Maps) m.designationManager ??= (DesignationManager)Raw(typeof(DesignationManager));
        crateDropped.Clear();
        try
        {
            patch.Patch(AccessTools.Method(typeof(DesignationManager), "RemoveAllDesignationsOn"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.Method(typeof(CompHackable), "OnHacked"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CargoHackedCallback)));
            patch.Patch(AccessTools.Method(typeof(ThingOwner), "TryDropAll"),
                prefix: new HarmonyMethod(typeof(Program), nameof(DropCrateCargo)));
            patch.Patch(AccessTools.Method(typeof(Thing), "DirtyMapMesh"),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));

            LifecycleCargo make(Faction faction, bool fresh)
            {
                var c = new LifecycleCargo { def = MakeDef("LifecycleCargo") };
                c.SetFactionDirect(faction);
                Set(c, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(cargoMap));
                var hack = new CompHackable { parent = c, props = new CompProperties_Hackable { defence = 1800 }, defence = 1800 };
                var comp = new CompSpaceCargo { parent = c, props = new CompProperties_SpaceCargo() };
                Set(c, "comps", new List<ThingComp> { hack, comp });
                if (fresh) comp.PostPostMake();
                comp.PostSpawnSetup(!fresh);
                return c;
            }
            var cargo = make(player, true);
            Check(cargo.IsUnlocked, "fresh player-built crate unlocks on its first spawn");
            var npc = make(enemy, true);
            Check(!npc.IsUnlocked && npc.Faction == enemy, "fresh NPC crate keeps its native lock and faction");
            var loaded = make(player, false);
            Check(!loaded.IsUnlocked, "loading player cargo keeps a saved dev lock");
            var cargoComp = cargo.GetComp<CompSpaceCargo>();
            var tint = AccessTools.PropertyGetter(typeof(CompSpaceCargo), "TerminalColor");
            var sealedColor = (UnityEngine.Color)tint.Invoke(cargoComp, null);
            var lockMethod = AccessTools.Method(typeof(CompSpaceCargo), "LockForDebug");
            DebugSettings.godMode = false; lockMethod.Invoke(cargoComp, null);
            Check(cargo.IsUnlocked, "normal mode cannot lock cargo through the dev action");
            DebugSettings.godMode = true; lockMethod.Invoke(cargoComp, null);
            Check(!cargo.IsUnlocked && cargo.GetComp<CompHackable>().ProgressPercent == 0,
                "God Mode resets the native hackable comp");
            var lockedColor = (UnityEngine.Color)tint.Invoke(cargoComp, null);
            Check(lockedColor.r > lockedColor.b && sealedColor.b > sealedColor.r,
                "terminal state color follows the native lock state rather than paint or a saved visual flag");
            cargoComp.PostSpawnSetup(false);
            Check(!cargo.IsUnlocked, "transferring a dev-locked crate does not unlock it");

            var itemDef = MakeDef("NativeCargoItem"); itemDef.category = ThingCategory.Item;
            itemDef.stackLimit = 75; itemDef.thingClass = typeof(ThingWithComps); itemDef.useHitPoints = true;
            var goods = new ThingWithComps { def = itemDef, stackCount = 75, HitPoints = 37 };
            Set(goods, "mapIndexOrState", (sbyte)-1);
            var forbid = new CompForbiddable { parent = goods };
            Set(goods, "comps", new List<ThingComp> { forbid });
            Check(cargo.TryStore(goods) && !goods.Spawned && goods.ParentHolder == cargo,
                "sealed cargo is held by native ThingOwner and is not a map item");
            forbid.Forbidden = true; forbid.Forbidden = false;
            Check(!cargo.CanOpen && !goods.Spawned && goods.ParentHolder == cargo,
                "unforbidding cannot release locked loot or make it haulable");
            Check((object)cargo is not IHaulDestination && (object)cargo is not IHaulSource,
                "sealed crates expose no haul source or storage destination");
            Check(typeof(ThingClass).IsAssignableFrom(typeof(Building_SpaceCargoContainer)),
                "storage mode still uses ASF rather than a custom hauling implementation");
            cargo.GetComp<CompHackable>().HackNow();
            Check(cargo.IsUnlocked && cargo.Contents.Count == 0 && crateDropped.Count == 1
                && ReferenceEquals(crateDropped[0], goods) && goods.stackCount == 75 && goods.HitPoints == 37,
                "native Hacked signal opens and ejects the original stack once, preserving its state");
            cargo.GetComp<CompHackable>().HackNow();
            Check(crateDropped.Count == 1, "completed hacking cannot eject a stack twice");
            Check(cargo.TryStore(goods) && cargo.CanOpen && cargo.OpenTicks == 100,
                "unlocked sealed cargo uses native crate opening duration and availability");
            cargo.Open();
            Check(crateDropped.Count == 2 && cargo.Contents.Count == 0,
                "ordinary native Open uses the same crate ejection path");
            Check(AccessTools.TypeByName("DeltaV.JobDriver_OpenSpaceCargo") == null
                && AccessTools.TypeByName("DeltaV.Patch_SealedCargoTradeBeacon") == null,
                "custom instant-open job and beacon exclusion patch are removed");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(cargoMap); crateDropped.Clear();
            Find.World.factionManager = oldFactions; DebugSettings.godMode = oldGod;
            Current.Game.uniqueIDsManager = oldIds;
        }
    }
}
