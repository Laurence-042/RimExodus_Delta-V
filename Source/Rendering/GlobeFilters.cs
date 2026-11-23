using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    [Flags] public enum GlobeRelation { Player = 1, Ally = 2, Neutral = 4, Hostile = 8, None = 16, Unknown = 32, All = 63 }
    [Flags] public enum GlobeArmament { Armed = 1, Unarmed = 2, Unknown = 4, All = 7 }
    [Flags] public enum GlobeMission { Tracked = 1, Active = 2, None = 4, All = 7 }

    /// <summary>User preferences only. Display facts remain in the shared entry/archival summary.</summary>
    public sealed class GlobeFilters : IExposable
    {
        public List<string> hiddenTypes = new List<string> { "IntegratedShip", "Projectile", "Scattered" };
        public GlobeRelation relations = GlobeRelation.All;
        public GlobeArmament armament = GlobeArmament.All;
        public GlobeMission missions = GlobeMission.All;
        public bool bookmarksOnly, keepTracked = true, showCoordinateGrid = true;

        public bool Restricted => hiddenTypes.Count > 0
            || relations != GlobeRelation.All || armament != GlobeArmament.All
            || missions != GlobeMission.All || bookmarksOnly || !showCoordinateGrid;
        public GlobeFilters Copy() => new GlobeFilters {
            hiddenTypes = new List<string>(hiddenTypes),
            relations = relations, armament = armament, missions = missions,
            bookmarksOnly = bookmarksOnly, keepTracked = keepTracked,
            showCoordinateGrid = showCoordinateGrid };
        public static GlobeFilters Everything() => new GlobeFilters { hiddenTypes = new List<string>() };

        public void ExposeData()
        {
            Scribe_Collections.Look(ref hiddenTypes, "hiddenTypes", LookMode.Value);
            Scribe_Values.Look(ref relations, "relations", GlobeRelation.All);
            Scribe_Values.Look(ref armament, "armament", GlobeArmament.All);
            Scribe_Values.Look(ref missions, "missions", GlobeMission.All);
            Scribe_Values.Look(ref bookmarksOnly, "bookmarksOnly");
            Scribe_Values.Look(ref keepTracked, "keepTracked", true);
            Scribe_Values.Look(ref showCoordinateGrid, "showCoordinateGrid", true);
            hiddenTypes ??= new List<string> { "IntegratedShip", "Projectile", "Scattered" };
        }

        internal bool Matches(GlobeDisplayEntry entry, bool tracked, bool active, bool bookmarked)
        {
            if (entry == null) return false;
            if (keepTracked && tracked) return true;
            GlobeMission mission = tracked ? GlobeMission.Tracked : active ? GlobeMission.Active : GlobeMission.None;
            return !hiddenTypes.Contains(TypeOf(entry))
                && (relations & RelationOf(entry.Faction)) != 0
                && (armament & entry.Armament) != 0 && (missions & mission) != 0
                && (!bookmarksOnly || bookmarked);
        }

        internal static string TypeOf(GlobeDisplayEntry entry)
        {
            if (entry.projectile != null) return "Projectile";
            if (entry.owner.IsScattered) return "Scattered";
            if (entry.ship != null || entry.craft) return entry.ship?.shuttle == true ? "IntegratedShip" : "FoundationShip";
            if (entry.natural) return "Natural";
            return entry.wreck ? "Wreck" : "Other";
        }
        internal static GlobeRelation RelationOf(Faction faction)
        {
            if (faction == null) return GlobeRelation.None;
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) return GlobeRelation.Unknown;
            if (faction == player) return GlobeRelation.Player;
            switch (faction.RelationKindWith(player))
            {
                case FactionRelationKind.Ally: return GlobeRelation.Ally;
                case FactionRelationKind.Hostile: return GlobeRelation.Hostile;
                default: return GlobeRelation.Neutral;
            }
        }
    }
}
