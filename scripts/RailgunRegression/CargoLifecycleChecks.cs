using System;
using System.Collections.Generic;
using AdaptiveStorage;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    sealed class LifecycleCargo : Building_SpaceCargoContainer
    {
        public readonly List<Thing> ejected = new();
        public override void SetFaction(Faction faction, Pawn recruiter = null) => SetFactionDirect(faction);
        public override Thing Eject(Thing item, int count = int.MaxValue, bool forbid = true,
            in IntVec3 dropOffset = default)
        {
            Check(!forbid, "hack ejection leaves cargo available for hauling");
            ejected.Add(item);
            Set(StoredThings, "_count", StoredThings.Count - 1);
            return item;
        }
    }

    // Keep the real HackNow -> ProcessHacked state transition and IHackable callback;
    // omit vanilla quest/letter/drop graphics after the callback in this headless fixture.
    static bool CargoHackedCallback(CompHackable __instance)
    {
        ((IHackable)__instance.parent).OnHacked();
        return false;
    }

    static void CheckCargoLifecycle()
    {
        var patch = new Harmony("DeltaV.Regression.CargoLifecycle");
        var oldFactions = Find.World.factionManager;
        bool oldGod = DebugSettings.godMode;
        var player = (Faction)Raw(typeof(Faction));
        var enemy = (Faction)Raw(typeof(Faction));
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        var cargoMap = (Map)Raw(typeof(Map));
        Find.Maps.Add(cargoMap);
        try
        {
            patch.Patch(typeof(ThingCollection).TypeInitializer,
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            foreach (var method in new[] {
                AccessTools.DeclaredMethod(typeof(ThingClass), "PostMake"),
                AccessTools.DeclaredMethod(typeof(ThingClass), "OnSpawn"),
                AccessTools.Method(typeof(Building_SpaceCargoContainer), "ApplyStorageMode"),
                AccessTools.Method(typeof(Building_SpaceCargoContainer), "RefreshCargoGraphic") })
                patch.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            patch.Patch(AccessTools.Method(typeof(CompHackable), "OnHacked"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CargoHackedCallback)));

            LifecycleCargo make(Faction faction, bool fresh)
            {
                var c = new LifecycleCargo { def = MakeDef("LifecycleCargo") };
                c.SetFactionDirect(faction);
                Set(c, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(cargoMap));
                var hack = new CompHackable { parent = c, props = new CompProperties_Hackable { defence = 1800 }, defence = 1800 };
                Set(c, "comps", new List<ThingComp> { hack });
                var stored = (ThingCollection)Raw(typeof(ThingCollection));
                Set(stored, "_things", Array.Empty<Thing>());
                Set(c, "_storedThings", stored);
                if (fresh) c.PostMake();
                return c;
            }
            var cargo = make(player, true);
            cargo.SpawnSetup(cargoMap, false);
            Check(cargo.IsUnlocked, "fresh player-built cargo is unlocked on first spawn");
            var npc = make(enemy, true);
            npc.SpawnSetup(cargoMap, false);
            Check(!npc.IsUnlocked && npc.Faction == enemy, "fresh NPC cargo retains its lock and faction");
            var loaded = make(player, false);
            loaded.SpawnSetup(cargoMap, true);
            Check(!loaded.IsUnlocked, "loading player cargo preserves a saved dev lock");

            var lockMethod = AccessTools.Method(typeof(Building_SpaceCargoContainer), "LockForDebug");
            DebugSettings.godMode = false;
            lockMethod.Invoke(cargo, null);
            Check(cargo.IsUnlocked, "normal mode cannot lock player cargo even through the dev action");
            DebugSettings.godMode = true;
            lockMethod.Invoke(cargo, null);
            Check(!cargo.IsUnlocked && cargo.GetComp<CompHackable>().ProgressPercent == 0,
                "God Mode locks the original hackable comp and resets hack progress");
            cargo.SpawnSetup(cargoMap, false);
            Check(!cargo.IsUnlocked, "respawning or transferring a dev-locked cargo does not unlock it");

            var goods = new Thing[] { new Thing { stackCount = 75 }, new Thing { stackCount = 2 } };
            Set(cargo.StoredThings, "_things", goods); Set(cargo.StoredThings, "_count", goods.Length);
            cargo.GetComp<CompHackable>().HackNow();
            Check(cargo.IsUnlocked && cargo.StoredThings.Count == 0 && cargo.ejected.Count == 2
                && ReferenceEquals(cargo.ejected[0], goods[0]) && ReferenceEquals(cargo.ejected[1], goods[1]),
                "native completed hacking ejects every original cargo stack exactly once");
            cargo.GetComp<CompHackable>().HackNow();
            Check(cargo.ejected.Count == 2, "repeated completed hacking does not eject cargo twice");
        }
        finally
        {
            patch.UnpatchAll(patch.Id); Find.Maps.Remove(cargoMap);
            Find.World.factionManager = oldFactions; DebugSettings.godMode = oldGod;
        }
    }
}
