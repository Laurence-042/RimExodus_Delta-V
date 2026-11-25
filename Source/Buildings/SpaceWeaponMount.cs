using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Both physical and embedded weapons expose the same mount to the weapon pipeline.
    public interface ISpaceWeaponMount
    {
        ThingWithComps Host { get; }
        long Identity { get; }
        Vector2 Offset { get; }
        IReadOnlyList<ThingComp> Components { get; }
    }

    internal sealed class BuildingWeaponMount : ISpaceWeaponMount
    {
        public ThingWithComps Host { get; }
        public long Identity => (long)Host.thingIDNumber << 32;
        public Vector2 Offset => Vector2.zero;
        public IReadOnlyList<ThingComp> Components => Host.AllComps;
        internal BuildingWeaponMount(ThingWithComps host) { Host = host; }
    }

    public class VirtualWeaponDef : Def
    {
        public List<CompProperties> comps = new List<CompProperties>();
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (comps.Count(p => p is CompProperties_SpaceWeapon) != 1)
                yield return defName + ": a mount must contain exactly one space weapon";
        }
    }

    public class VirtualWeaponSlot
    {
        public VirtualWeaponDef weapon;
        public Vector2 offset;
    }

    public class CompProperties_VirtualWeapons : CompProperties
    {
        public List<VirtualWeaponSlot> mounts = new List<VirtualWeaponSlot>();
        public CompProperties_VirtualWeapons() { compClass = typeof(CompVirtualWeapons); }
    }

    internal sealed class VirtualWeaponMount : ISpaceWeaponMount
    {
        public ThingWithComps Host { get; }
        public long Identity => ((long)Host.thingIDNumber << 32) | (uint)(index + 1);
        public Vector2 Offset => slot.offset;
        internal string SettingsKey => slot.weapon.defName + ":" + index;
        internal string Label => slot.weapon.LabelCap;
        public IReadOnlyList<ThingComp> Components => components;
        internal readonly CompSpaceWeapon Weapon;
        private readonly int index;
        private readonly VirtualWeaponSlot slot;
        private readonly List<ThingComp> components = new List<ThingComp>();
        internal VirtualWeaponMount(ThingWithComps host, VirtualWeaponSlot slot, int index)
        {
            Host = host; this.slot = slot; this.index = index;
            foreach (CompProperties properties in slot.weapon.comps)
            {
                var comp = (ThingComp)Activator.CreateInstance(properties.compClass);
                comp.parent = host;
                comp.Initialize(properties);
                components.Add(comp);
            }
            Weapon = components.OfType<CompSpaceWeapon>().Single();
            Weapon.SetMount(this);
        }
    }

    internal sealed class PawnWeaponMount : ISpaceWeaponMount
    {
        public ThingWithComps Host { get; }
        public long Identity { get; }
        public Vector2 Offset => Vector2.zero;
        public IReadOnlyList<ThingComp> Components => components;
        internal readonly CompSpaceWeapon Weapon;
        private readonly List<ThingComp> components = new List<ThingComp>();

        internal PawnWeaponMount(Pawn pawn, uint identitySuffix, VirtualWeaponDef definition,
            int cooldownUntilTick)
        {
            Host = pawn;
            Identity = ((long)pawn.thingIDNumber << 32) | identitySuffix;
            Weapon = AddComponents(pawn, definition.comps, cooldownUntilTick);
        }

        private CompSpaceWeapon AddComponents(Pawn pawn, IEnumerable<CompProperties> properties,
            int cooldownUntilTick)
        {
            foreach (CompProperties property in properties)
            {
                var comp = (ThingComp)Activator.CreateInstance(property.compClass);
                comp.parent = pawn;
                comp.Initialize(property);
                components.Add(comp);
            }
            CompSpaceWeapon weapon = components.OfType<CompSpaceWeapon>().Single();
            weapon.cooldownUntilTick = cooldownUntilTick;
            weapon.SetMount(this);
            weapon.PostSpawnSetup(respawningAfterLoad: cooldownUntilTick > 0);
            return weapon;
        }
    }

    public class CompVirtualWeapons : ThingComp
    {
        private List<VirtualWeaponMount> mounts;
        private IEnumerable<VirtualWeaponMount> Mounts
        {
            get
            {
                if (mounts == null)
                {
                    var slots = ((CompProperties_VirtualWeapons)props).mounts;
                    mounts = slots.Select((slot, index) => new VirtualWeaponMount(parent, slot, index)).ToList();
                }
                return mounts;
            }
        }
        public IEnumerable<CompSpaceWeapon> Weapons => Mounts.Select(m => m.Weapon);
        internal CompSpaceWeapon WeaponFor(ThingComp component)
            => Mounts.FirstOrDefault(m => m.Components.Contains(component))?.Weapon;
        public override void PostPostMake()
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.PostPostMake();
        }
#if RIMWORLD_1_7
        public override void CompTickInterval(int delta)
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.CompTickInterval(delta);
        }
#endif
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.PostSpawnSetup(respawningAfterLoad);
        }
        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.PostDeSpawn(map, mode);
        }
        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.PostDestroy(mode, previousMap);
        }
        public override void ReceiveCompSignal(string signal)
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.ReceiveCompSignal(signal);
        }
        public override void CompTick()
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components) comp.CompTick();
        }
        public override void PostExposeData()
        {
            int index = 0;
            foreach (var mount in Mounts)
            {
                if (Scribe.EnterNode("weaponMount" + index++))
                {
                    try
                    {
                        int component = 0;
                        foreach (var comp in mount.Components)
                            if (Scribe.EnterNode("component" + component++))
                                try { comp.PostExposeData(); }
                                finally { Scribe.ExitNode(); }
                    }
                    finally { Scribe.ExitNode(); }
                }
            }
        }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var mount in Mounts)
                foreach (var comp in mount.Components)
                    foreach (Gizmo gizmo in comp.CompGetGizmosExtra())
                    {
                        if (gizmo is Command command) command.defaultLabel = mount.Label + ": " + command.defaultLabel;
                        yield return gizmo;
                    }
        }
    }

    public partial class CompSpaceWeapon
    {
        private ISpaceWeaponMount mount;
        public ISpaceWeaponMount Mount => mount ??= new BuildingWeaponMount(parent);
        public long Identity => Mount.Identity;
        internal string SettingsKey => Mount is VirtualWeaponMount embedded ? embedded.SettingsKey : "";
        internal bool AllowsPointDefense => !WeaponProps.manualOnly
            || WeaponProps.tractor && Component<CompSpaceTractor>()?.ManualCargo != true;
        internal void SetMount(ISpaceWeaponMount value) { mount = value; }
        public T Component<T>() where T : ThingComp => Mount.Components.OfType<T>().FirstOrDefault();
        public static IEnumerable<CompSpaceWeapon> On(ThingWithComps thing)
        {
            if (thing == null) yield break;
            foreach (var comp in thing.AllComps)
                if (comp is CompSpaceWeapon weapon) yield return weapon;
                else if (comp is CompVirtualWeapons embedded)
                    foreach (var child in embedded.Weapons) yield return child;
        }
        internal static CompSpaceWeapon ForComponent(ThingComp component)
            => component.parent.TryGetComp<CompVirtualWeapons>()?.WeaponFor(component)
                ?? component.parent.TryGetComp<CompSpaceWeapon>();
    }
}
