using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>侧车条目：掩码内一格的基架（原版 PrefabDef 不保存 foundation 层）。</summary>
    public class PrefabFoundationEntry
    {
        public IntVec3 pos;
        public TerrainDef def;
    }

    /// <summary>侧车条目：掩码内一格的屋顶（原版 PrefabDef 不保存 roof）。</summary>
    public class PrefabRoofEntry
    {
        public IntVec3 pos;
        public RoofDef def;
    }

    /// <summary>
    /// 通用地图 prefab 文档：原版 prefab 词汇（PrefabThingData/PrefabTerrainData + size）加
    /// 原版不保存的 foundation/roof 侧车。文档始终持有自有列表形态（PrefabThingData 的字段
    /// 全部 public，可自由构造与裁剪）；PrefabDef 的 things/terrain 是 internal，只在
    /// BuildPrefabDef() 生成 SpawnPrefab 入参时装填（反射字段名 = XML 标签名，属稳定契约）。
    /// &lt;prefab&gt; 节点保持原版 PrefabDef 的 XML 形状，Load 完全走 ObjectFromXml 原版管线
    /// （含 PrefabThingData.LoadDataFromXmlCustom），Def 交叉引用经 DirectXmlCrossRefLoader
    /// 运行时解析——不重启、不自写 def 解析。不支持 sub-prefab（本导出器从不产生）。
    /// </summary>
    public class MapPrefabDocument
    {
        public const int CurrentFormatVersion = 1;

        public string name;
        public int formatVersion = CurrentFormatVersion;
        public IntVec2 size;
        public List<PrefabThingData> things = new List<PrefabThingData>();
        public List<PrefabTerrainData> terrain = new List<PrefabTerrainData>();
        public List<PrefabFoundationEntry> foundations = new List<PrefabFoundationEntry>();
        public List<PrefabRoofEntry> roofs = new List<PrefabRoofEntry>();

        private static readonly FieldInfo PrefabThingsField =
            typeof(PrefabDef).GetField("things", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PrefabTerrainField =
            typeof(PrefabDef).GetField("terrain", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>spawn 对账用：things 里 positions 的总格数（chance 恒 1）。
        /// SpawnPrefab 对放不下的 thing 静默跳过，靠此计数暴露丢失。</summary>
        public int ThingCellCount
        {
            get
            {
                int count = 0;
                foreach (PrefabThingData entry in things)
                {
                    count += entry.positions?.Count ?? 1;
                }
                return count;
            }
        }

        /// <summary>生成 SpawnPrefab 入参。游戏更新若改名 internal 字段（等于改 XML 契约，
        /// 可能性极低），这里抛异常交由调用方回退。</summary>
        internal PrefabDef BuildPrefabDef()
        {
            if (PrefabThingsField == null || PrefabTerrainField == null)
            {
                throw new NotSupportedException("PrefabDef internal fields 'things'/'terrain' not found; game update changed the prefab XML contract.");
            }
            var def = new PrefabDef
            {
                size = size,
            };
            PrefabThingsField.SetValue(def, things);
            PrefabTerrainField.SetValue(def, terrain);
            return def;
        }

        public static MapPrefabDocument Load(string path)
        {
            var xml = new XmlDocument();
            xml.Load(path);
            XmlElement root = xml.DocumentElement;
            if (root == null || root.Name != "MapPrefab")
            {
                throw new InvalidDataException($"Not a MapPrefab document: {path}");
            }
            string versionText = root.GetAttribute("formatVersion");
            int version = versionText.NullOrEmpty() ? 0 : ParseHelper.FromString<int>(versionText);
            if (version != CurrentFormatVersion)
            {
                throw new InvalidDataException($"MapPrefab formatVersion {version} is not supported (expected {CurrentFormatVersion}): {path}");
            }
            XmlElement prefabNode = root["prefab"];
            if (prefabNode == null)
            {
                throw new InvalidDataException($"MapPrefab has no <prefab> core node: {path}");
            }

            PrefabDef parsed = DirectXmlToObject.ObjectFromXml<PrefabDef>(prefabNode, doPostLoad: false);
            if (parsed == null)
            {
                throw new InvalidDataException($"MapPrefab <prefab> core node failed to parse: {path}");
            }
            XmlElement foundationsNode = root["foundations"];
            XmlElement roofsNode = root["roofs"];
            // foundations/roofs 必须与核心节点一起在 resolve 之前解析：Def 引用（TerrainDef/
            // RoofDef）在 ObjectFromXml 里注册为延迟交叉引用，任何一组在 resolve 之后才解析，
            // 其引用就永远不会被解析（def 留 null，下游 SetFoundation 逐格静默失败）。
            List<PrefabFoundationEntry> foundations = foundationsNode != null
                ? DirectXmlToObject.ObjectFromXml<List<PrefabFoundationEntry>>(foundationsNode, doPostLoad: false)
                : new List<PrefabFoundationEntry>();
            List<PrefabRoofEntry> roofs = roofsNode != null
                ? DirectXmlToObject.ObjectFromXml<List<PrefabRoofEntry>>(roofsNode, doPostLoad: false)
                : new List<PrefabRoofEntry>();
            DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);

            // resolve 失败（defName 不存在等）只写游戏日志、字段留 null——这里显式拒绝，
            // 让坏文件以异常走调用方回退，而不是静默 null 在下游产生难查的连锁故障。
            if (parsed.GetThings().Any(entry => entry.data.def == null)
                || parsed.GetTerrain().Any(entry => entry.data.def == null)
                || foundations.Any(entry => entry.def == null)
                || roofs.Any(entry => entry.def == null))
            {
                throw new InvalidDataException($"MapPrefab contains unresolved def references (see game log for 'Could not resolve cross-reference' entries): {path}");
            }

            var doc = new MapPrefabDocument
            {
                name = root["name"]?.InnerText,
                size = parsed.size,
                foundations = foundations,
                roofs = roofs,
            };
            // 重组为文档自有形态：GetThings()/GetTerrain() 按格展开且共享 data 引用。
            var thingsByData = new Dictionary<PrefabThingData, List<IntVec3>>();
            foreach (var (data, cell) in parsed.GetThings())
            {
                if (!thingsByData.TryGetValue(data, out List<IntVec3> cells))
                {
                    cells = new List<IntVec3>();
                    thingsByData[data] = cells;
                }
                cells.Add(cell);
            }
            foreach (KeyValuePair<PrefabThingData, List<IntVec3>> pair in thingsByData)
            {
                pair.Key.positions = pair.Value;
                doc.things.Add(pair.Key);
            }
            var terrainByData = new Dictionary<(TerrainDef def, ColorDef color), PrefabTerrainData>();
            foreach (var (data, cell) in parsed.GetTerrain())
            {
                if (!terrainByData.TryGetValue((data.def, data.color), out PrefabTerrainData entry))
                {
                    entry = new PrefabTerrainData
                    {
                        def = data.def,
                        color = data.color,
                        rects = new List<CellRect>(),
                    };
                    terrainByData[(data.def, data.color)] = entry;
                }
                entry.rects.Add(new CellRect(cell.x, cell.z, 1, 1));
            }
            doc.terrain.AddRange(terrainByData.Values);
            return doc;
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!directory.NullOrEmpty())
            {
                Directory.CreateDirectory(directory);
            }
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
            };
            using (XmlWriter writer = XmlWriter.Create(path, settings))
            {
                writer.WriteStartElement("MapPrefab");
                writer.WriteAttributeString("formatVersion", CurrentFormatVersion.ToString());
                writer.WriteElementString("name", name);

                writer.WriteStartElement("prefab");
                writer.WriteElementString("defName", "MapPrefab_" + name);
                writer.WriteElementString("size", size.ToString());
                if (things.Count > 0)
                {
                    writer.WriteStartElement("things");
                    foreach (PrefabThingData entry in things)
                    {
                        writer.WriteStartElement(entry.def.defName);
                        writer.WriteStartElement("positions");
                        foreach (IntVec3 pos in entry.positions)
                        {
                            writer.WriteElementString("li", pos.ToString());
                        }
                        writer.WriteEndElement();
                        if (entry.relativeRotation != RotationDirection.None)
                        {
                            writer.WriteElementString("relativeRotation", entry.relativeRotation.ToString());
                        }
                        if (entry.stuff != null)
                        {
                            writer.WriteElementString("stuff", entry.stuff.defName);
                        }
                        if (entry.quality.HasValue)
                        {
                            writer.WriteElementString("quality", entry.quality.Value.ToString());
                        }
                        if (entry.hp != 0)
                        {
                            writer.WriteElementString("hp", entry.hp.ToString());
                        }
                        if (entry.stackCountRange != IntRange.One)
                        {
                            writer.WriteElementString("stackCountRange",
                                $"{entry.stackCountRange.min}~{entry.stackCountRange.max}");
                        }
                        if (entry.colorDef != null)
                        {
                            writer.WriteElementString("colorDef", entry.colorDef.defName);
                        }
                        if (entry.color != default(Color))
                        {
                            writer.WriteElementString("color", ColorToString(entry.color));
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (terrain.Count > 0)
                {
                    writer.WriteStartElement("terrain");
                    foreach (PrefabTerrainData entry in terrain)
                    {
                        writer.WriteStartElement(entry.def.defName);
                        if (entry.color != null)
                        {
                            writer.WriteElementString("color", entry.color.defName);
                        }
                        writer.WriteStartElement("rects");
                        foreach (CellRect rect in entry.rects)
                        {
                            writer.WriteElementString("li", rect.ToString());
                        }
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();

                if (foundations.Count > 0)
                {
                    writer.WriteStartElement("foundations");
                    foreach (PrefabFoundationEntry entry in foundations)
                    {
                        writer.WriteStartElement("li");
                        writer.WriteElementString("pos", entry.pos.ToString());
                        writer.WriteElementString("def", entry.def.defName);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (roofs.Count > 0)
                {
                    writer.WriteStartElement("roofs");
                    foreach (PrefabRoofEntry entry in roofs)
                    {
                        writer.WriteStartElement("li");
                        writer.WriteElementString("pos", entry.pos.ToString());
                        writer.WriteElementString("def", entry.def.defName);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }

        private static string ColorToString(Color color)
        {
            static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
            return $"({F(color.r)}, {F(color.g)}, {F(color.b)}, {F(color.a)})";
        }
    }
}
