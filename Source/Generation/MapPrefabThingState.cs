using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class MapPrefabThingEntry
    {
        public IntVec3 pos;
        public ThingDef def;
        public ThingDef stuff;
        public Rot4 rotation;
        public int hitPoints;
        public int stackCount = 1;
        public string stateXml;
    }

    /// <summary>原版 Scribe 逐实例保存，整批读取以解析 prefab 内部引用。</summary>
    internal static class MapPrefabThingState
    {
        private sealed class Bundle : IExposable
        {
            public List<Thing> things;

            public void ExposeData()
            {
                Scribe_Collections.Look(ref things, "things", LookMode.Deep);
            }
        }

        internal static string Capture(Thing thing)
        {
            string xml = Scribe.saver.DebugOutputFor(thing);
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidDataException($"Scribe failed to export {thing}.");
            var document = new XmlDocument();
            document.LoadXml(xml);
            if (document.DocumentElement == null)
                throw new InvalidDataException($"Scribe exported no XML for {thing}.");
            if (thing is Building_Storage storage && storage.GetStoreSettings() != null)
            {
                // 共享储存组属于旧地图；保留建筑当时实际生效的筛选和优先级。
                string settingsXml = Scribe.saver.DebugOutputFor(storage.GetStoreSettings());
                var settingsDocument = new XmlDocument();
                settingsDocument.LoadXml(settingsXml);
                XmlElement settings = document.DocumentElement["settings"];
                if (settings == null)
                {
                    settings = document.CreateElement("settings");
                    document.DocumentElement.AppendChild(settings);
                }
                settings.InnerXml = settingsDocument.DocumentElement.InnerXml;
                XmlElement group = document.DocumentElement["storageGroup"];
                if (group != null) group.InnerText = "null";
            }
            return document.DocumentElement.OuterXml;
        }

        internal static List<Thing> Instantiate(IReadOnlyList<MapPrefabThingEntry> entries, string label, Faction faction)
        {
            var result = new List<Thing>();
            if (entries.Count == 0) return result;
            string path = Path.Combine(Path.GetTempPath(), "DeltaV-Prefab-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var document = new XmlDocument();
                XmlElement root = document.CreateElement("MapPrefabState");
                document.AppendChild(root);
                XmlElement list = document.CreateElement("things");
                root.AppendChild(list);
                foreach (MapPrefabThingEntry entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.stateXml))
                    {
                        // A failed third-party export gets a fresh instance at the same placement.
                        XmlElement blank = document.CreateElement("li");
                        blank.SetAttribute("IsNull", "True");
                        list.AppendChild(blank);
                        continue;
                    }
                    var source = new XmlDocument();
                    source.LoadXml(entry.stateXml);
                    XmlElement node = (XmlElement)document.ImportNode(source.DocumentElement, true);
                    node = Rename(document, node, "li");
                    list.AppendChild(node);
                }
                document.Save(path);
                if (Scribe.mode != LoadSaveMode.Inactive)
                    throw new InvalidOperationException("Cannot instantiate a prefab while RimWorld Scribe is active.");
                try
                {
                    Scribe.loader.InitLoading(path);
                    var bundle = new Bundle();
                    bundle.ExposeData();
                    // Scribe knows every deep-loaded object, including turret guns outside IThingHolder.
                    // FinalizeLoading clears this registry; keep it until old-ID references are resolved.
                    IExposable[] loaded = Scribe.loader.crossRefs.crossReferencingExposables.ToArray();
                    Scribe.loader.FinalizeLoading();
                    Reidentify(loaded, faction);
                    result = bundle.things ?? new List<Thing>();
                }
                finally
                {
                    if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DeltaV] MapPrefab '{label}' Scribe load failed: {ex}");
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            while (result.Count < entries.Count) result.Add(null);
            return result;
        }

        private static XmlElement Rename(XmlDocument document, XmlElement source, string name)
        {
            XmlElement target = document.CreateElement(name);
            foreach (XmlAttribute attribute in source.Attributes)
                target.SetAttribute(attribute.Name, attribute.Value);
            while (source.HasChildNodes)
                target.AppendChild(source.FirstChild);
            return target;
        }

        private static void Reidentify(IReadOnlyList<IExposable> loaded, Faction faction)
        {
            foreach (Thing item in loaded.OfType<Thing>())
            {
                item.questTags = null;
                if (item.def.CanHaveFaction) item.SetFactionDirect(faction);
                if (!item.def.HasThingIDNumber) continue;
                item.thingIDNumber = -1;
                ThingIDMaker.GiveIDTo(item);
            }
            // Verb load IDs contain their owner's Thing ID. Keep the resolved Verb instances/state,
            // but derive their identities again using the same API as vanilla VerbTracker.InitVerbs.
            foreach (VerbTracker tracker in loaded.OfType<VerbTracker>())
            {
                IVerbOwner owner = tracker.directOwner;
                foreach (Verb verb in tracker.AllVerbs)
                {
                    verb.loadID = verb.tool != null
                        ? Verb.CalculateUniqueLoadID(owner, verb.tool, verb.maneuver)
                        : Verb.CalculateUniqueLoadID(owner, owner.VerbProperties.IndexOf(verb.verbProps));
                }
            }
        }
    }
}
