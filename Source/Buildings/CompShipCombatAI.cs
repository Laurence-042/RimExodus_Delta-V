using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using System.Xml;
using System.Xml.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class CompProperties_ShipCombatAI : CompProperties
    {
        public CompProperties_ShipCombatAI() { compClass = typeof(CompShipCombatAI); }
    }

    /// <summary>Per-console or per-shuttle combat instructions. Runtime decisions remain on the controller.</summary>
    public sealed class CompShipCombatAI : ThingComp
    {
        public CombatStrategy strategy = new CombatStrategy();
        public float initialThrust;
        public float initialFirepower;
        public float initialHullHp;
        public int initialMainConsoleId;
        public int baselineShipKey;
        public bool retreating;
        [Unsaved] public string lastManeuverReason;
        [Unsaved] public string lastFireReason;
        [Unsaved] public string lastTargetReason;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref strategy, "combatStrategy");
            Scribe_Values.Look(ref initialThrust, "combatInitialThrust");
            Scribe_Values.Look(ref initialFirepower, "combatInitialFirepower");
            Scribe_Values.Look(ref initialHullHp, "combatInitialHullHp");
            Scribe_Values.Look(ref initialMainConsoleId, "combatInitialMainConsoleId");
            Scribe_Values.Look(ref baselineShipKey, "combatBaselineShipKey");
            Scribe_Values.Look(ref retreating, "combatRetreating");
            strategy ??= new CombatStrategy();
        }

        internal void Replace(CombatStrategy replacement)
        {
            replacement.Validate();
            strategy = replacement.Copy();
            lastManeuverReason = null;
            lastFireReason = null;
            lastTargetReason = null;
        }

        internal void EnsureBaselineFor(ShipBase ship)
        {
            int key = ship.Identity.key;
            if (baselineShipKey == key) return;
            initialThrust = 0f;
            initialFirepower = 0f;
            initialHullHp = 0f;
            initialMainConsoleId = 0;
            retreating = false;
            baselineShipKey = key;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent?.Spawned != true || parent.Faction != Faction.OfPlayer) yield break;
            if (parent.TryGetComp<CompDeltaVPilotConsole>() == null
                && parent.TryGetComp<CompSpaceShuttle>() == null) yield break;
            yield return new Command_BatchSetting<CompShipCombatAI>("CombatStrategy", this,
                selected => Find.WindowStack.Add(new Dialog_CombatStrategy(selected)))
            {
                defaultLabel = "DeltaV_CombatAIEdit".Translate(),
                defaultDesc = "DeltaV_CombatAIEditTip".Translate(),
                icon = TexCommand.Install
            };
            CompDeltaVPilotConsole console = parent.TryGetComp<CompDeltaVPilotConsole>();
            if (console?.isMain == false && console.shipId > 0)
            {
                CompDeltaVPilotConsole main = ShipRegistry.MainConsoleForOwner(parent.Map, console.shipId);
                if (main != null && main != console && main.parent.Faction == Faction.OfPlayer)
                    yield return new Command_Action
                    {
                        defaultLabel = "DeltaV_CombatAICopyMain".Translate(),
                        defaultDesc = "DeltaV_CombatAICopyMainTip".Translate(),
                        icon = TexCommand.Install,
                        action = () =>
                        {
                            CompShipCombatAI source = main.parent.TryGetComp<CompShipCombatAI>();
                            if (source != null) Replace(source.strategy);
                        }
                    };
            }
        }
    }

    internal static class CombatStrategyFile
    {
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(CombatStrategy));
        internal static string DirectoryPath => Path.Combine(Path.GetDirectoryName(MapPrefab.PrefabDirectory),
            "CombatStrategies");

        internal static List<string> Files()
        {
            if (!Directory.Exists(DirectoryPath)) return new List<string>();
            return Directory.GetFiles(DirectoryPath, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        }

        internal static string Export(CombatStrategy strategy)
        {
            strategy.Validate();
            Directory.CreateDirectory(DirectoryPath);
            string stem = new string((strategy.name ?? "Strategy")
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (stem.Length == 0) stem = "Strategy";
            if (stem.Length > 60) stem = stem.Substring(0, 60);
            string path = Path.Combine(DirectoryPath,
                stem + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                Serializer.Serialize(stream, strategy.Copy());
            return path;
        }

        internal static CombatStrategy Import(string path) => ImportFrom(DirectoryPath, path);

        internal static CombatStrategy ImportFrom(string directory, string path)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Strategy path escapes CombatStrategies.");
            if (new FileInfo(full).Length > 1024 * 1024)
                throw new InvalidDataException("Strategy file is too large.");
            using (FileStream stream = File.OpenRead(full))
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                var document = XDocument.Load(reader);
                if (document.Root?.Element("format")?.Value != CombatStrategy.CurrentFormat.ToString())
                    throw new InvalidDataException("Unsupported or missing strategy format; expected " + CombatStrategy.CurrentFormat + ".");
                using var parsed = document.CreateReader();
                var strategy = (CombatStrategy)Serializer.Deserialize(parsed);
                strategy.Validate();
                return strategy.Copy();
            }
        }
    }
}
