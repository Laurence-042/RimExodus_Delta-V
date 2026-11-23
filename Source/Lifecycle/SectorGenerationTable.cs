using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Verse;

namespace DeltaV
{
    /// <summary>User-editable generation recipe; never a runtime SpaceObject type.</summary>
    [DataContract]
    public sealed class SectorGenerationNode : IExposable
    {
        [DataMember(Order = 0)] public string id;
        [DataMember(Order = 1)] public string label;
        [DataMember(Order = 2)] public bool group;
        [DataMember(Order = 3)] public float standardDensityPer100MCells2;
        [DataMember(Order = 4)] public List<string> prefabPaths = new List<string>();
        [DataMember(Order = 5)] public List<SectorGenerationNode> children = new List<SectorGenerationNode>();
        public bool IsLeaf => !group;

        public SectorGenerationNode() { }
        public SectorGenerationNode(string id, string label, params SectorGenerationNode[] children)
        { this.id = id; this.label = label; group = true; if (children != null) this.children.AddRange(children); }
        public SectorGenerationNode(string id, string label, float density, string prefabPath)
        { this.id = id; this.label = label; standardDensityPer100MCells2 = density; if (!prefabPath.NullOrEmpty()) prefabPaths.Add(prefabPath); }

        public SectorGenerationNode Copy() => new SectorGenerationNode
        {
            id = id, label = label, group = group,
            standardDensityPer100MCells2 = standardDensityPer100MCells2,
            prefabPaths = new List<string>(prefabPaths ?? new List<string>()),
            children = (children ?? new List<SectorGenerationNode>()).Where(c => c != null).Select(c => c.Copy()).ToList()
        };

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id"); Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref group, "group");
            Scribe_Values.Look(ref standardDensityPer100MCells2, "standardDensityPer100MCells2");
            Scribe_Collections.Look(ref prefabPaths, "prefabPaths", LookMode.Value);
            Scribe_Collections.Look(ref children, "children", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                prefabPaths ??= new List<string>(); children ??= new List<SectorGenerationNode>();
                children.RemoveAll(c => c == null); if (!group) children.Clear();
            }
        }
    }

    public static class SectorGenerationTable
    {
        public const int MaxNodes = 256, MaxDepth = 16;
        public const float MaxStandardDensity = 10f;

        public static List<SectorGenerationNode> Defaults() => new List<SectorGenerationNode>
        {
            new SectorGenerationNode("craft", Label("DeltaV_SectorGenerationDefaultCraft", "Craft"),
                Leaf("fighter", "DeltaV_SectorGenerationDefaultFighter", "Fighter", .520f, "Sector/Craft/SectorFighter.xml"),
                Leaf("gunboat", "DeltaV_SectorGenerationDefaultGunboat", "Gunboat", .404f, "Sector/Craft/SectorGunboat.xml"),
                Leaf("frigate", "DeltaV_SectorGenerationDefaultFrigate", "Frigate", .289f, "Sector/Craft/SectorFrigate.xml"),
                Leaf("destroyer", "DeltaV_SectorGenerationDefaultDestroyer", "Destroyer", .173f, "Sector/Craft/SectorDestroyer.xml"),
                Leaf("cruiser", "DeltaV_SectorGenerationDefaultCruiser", "Cruiser", .058f, "Sector/Craft/SectorCruiser.xml")),
            new SectorGenerationNode("structures", Label("DeltaV_SectorGenerationDefaultStructure", "Structures"),
                new SectorGenerationNode("satellites", Label("DeltaV_SectorGenerationDefaultSatellite", "Satellites"),
                    Leaf("weapon-relay", "DeltaV_SectorGenerationDefaultWeaponRelay", "Weapon relay", .115f, "Sector/Stations/SectorWeaponRelay.xml"),
                    Leaf("abandoned-satellite", "DeltaV_SectorGenerationDefaultAbandonedSatellite", "Abandoned satellite", .346f, "Sector/Stations/SectorAbandonedSatellite.xml")),
                Leaf("weapon-platform", "DeltaV_SectorGenerationDefaultWeaponPlatform", "Weapon platform", .231f, "Sector/Stations/SectorWeaponPlatform.xml"),
                Leaf("trade-station", "DeltaV_SectorGenerationDefaultTradeStation", "Trade station", .231f, "Sector/Stations/SectorTradeStation.xml"),
                Leaf("shipyard", "DeltaV_SectorGenerationDefaultShipyard", "Shipyard", .115f, "Sector/Stations/SectorShipyard.xml"),
                Leaf("factory-station", "DeltaV_SectorGenerationDefaultFactoryStation", "Factory station", .058f, "Sector/Stations/SectorFactoryStation.xml"),
                Leaf("asteroid", "DeltaV_SectorGenerationDefaultAsteroid", "Asteroid belt", .346f, "Sector/Asteroids/SectorAsteroid.xml"))
        };

        private static string Label(string key, string fallback)
            => LanguageDatabase.activeLanguage == null ? fallback : key.Translate().ToString();

        internal static string Message(string key, string fallback)
            => LanguageDatabase.activeLanguage == null ? fallback : key.Translate().ToString();

        internal static string Message(string key, string fallback, NamedArgument arg)
            => LanguageDatabase.activeLanguage == null ? string.Format(fallback, arg.arg) : key.Translate(arg).ToString();

        private static SectorGenerationNode Leaf(string id, string key, string fallback, float density, string path)
            => new SectorGenerationNode(id, Label(key, fallback), density, path);

        public static IEnumerable<SectorGenerationNode> AllNodes(IEnumerable<SectorGenerationNode> roots)
        {
            if (roots == null) yield break;
            foreach (SectorGenerationNode root in roots)
                if (root != null) foreach (SectorGenerationNode node in Subtree(root)) yield return node;
        }
        public static IEnumerable<SectorGenerationNode> Leaves(IEnumerable<SectorGenerationNode> roots)
            => AllNodes(roots).Where(node => node.IsLeaf);
        private static IEnumerable<SectorGenerationNode> Subtree(SectorGenerationNode node)
        {
            yield return node;
            if (!node.group || node.children == null) yield break;
            foreach (SectorGenerationNode child in node.children)
                if (child != null) foreach (SectorGenerationNode nested in Subtree(child)) yield return nested;
        }

        public static bool Validate(List<SectorGenerationNode> roots, out string error)
        {
            string failure = null; var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int count = 0;
            bool Visit(SectorGenerationNode node, int depth)
            {
                if (node == null) { failure = Message("DeltaV_SectorValidationNullNode", "Generation table contains a null node."); return false; }
                if (++count > MaxNodes) { failure = Message("DeltaV_SectorValidationTooManyNodes", "Generation table exceeds {0} nodes.", MaxNodes); return false; }
                if (depth > MaxDepth) { failure = Message("DeltaV_SectorValidationTooDeep", "Generation table exceeds depth {0}.", MaxDepth); return false; }
                node.id = node.id?.Trim(); node.label = node.label?.Trim();
                if (node.id.NullOrEmpty() || node.id.Length > 64) { failure = Message("DeltaV_SectorValidationInvalidId", "Every node needs an ID of at most 64 characters."); return false; }
                if (!ids.Add(node.id)) { failure = Message("DeltaV_SectorValidationDuplicateId", "Duplicate node ID '{0}'.", node.id); return false; }
                if (node.label.NullOrEmpty() || node.label.Length > 128) { failure = Message("DeltaV_SectorValidationInvalidName", "Node '{0}' needs a name of at most 128 characters.", node.id); return false; }
                node.children ??= new List<SectorGenerationNode>(); node.prefabPaths ??= new List<string>();
                if (!node.group && node.children.Count != 0) { failure = Message("DeltaV_SectorValidationLeafChildren", "Entry '{0}' cannot have children.", node.id); return false; }
                if (node.group && node.prefabPaths.Count != 0) { failure = Message("DeltaV_SectorValidationGroupPrefabs", "Group '{0}' cannot have prefabs.", node.id); return false; }
                float d = node.standardDensityPer100MCells2;
                if (float.IsNaN(d) || float.IsInfinity(d) || d < 0f || d > MaxStandardDensity)
                { failure = Message("DeltaV_SectorValidationInvalidDensity", "Node '{0}' has an invalid density.", node.id); return false; }
                foreach (string path in node.prefabPaths)
                    if (path.NullOrEmpty() || path.Length > 260 || System.IO.Path.IsPathRooted(path)
                        || path.Replace('\\', '/').Split('/').Any(part => part == ".."))
                    { failure = Message("DeltaV_SectorValidationUnsafePath", "Node '{0}' has an unsafe prefab path.", node.id); return false; }
                foreach (SectorGenerationNode child in node.children) if (!Visit(child, depth + 1)) return false;
                return true;
            }
            if (roots == null || roots.Count == 0) failure = Message("DeltaV_SectorValidationEmpty", "Generation table is empty.");
            else foreach (SectorGenerationNode root in roots) if (!Visit(root, 1)) break;
            if (failure == null && !Leaves(roots).Any()) failure = Message("DeltaV_SectorValidationNoEntries", "Generation table has no entries.");
            error = failure; return failure == null;
        }
    }
}
