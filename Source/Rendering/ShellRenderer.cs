using System.Collections.Generic;
using System.Reflection;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 非聚焦飞船外壳代理：每帧重放飞船 Map 的 SectionLayer 网格（RimExodus 邻居图重放思路），
    /// 以"绕飞船中心旋转 + 平移到聚焦帧"的矩阵画到主相机。
    ///
    /// 提交通道 = 原版 MapDrawLayer.DrawLayer 同款：
    /// Graphics.DrawMesh(mesh, matrix, material, subMesh.renderLayer)——camera=null + Layer 路由，
    /// 这是本渲染管线中唯一验证过的即时网格通道（"指定相机"重载与离屏捕获相机均已实测不可用，
    /// 2026-09-12，详见阶段1文档）。
    ///
    /// 架构定位（2026-09-12 定稿，同日修正表述）：
    /// - 宿主图是机制锚点（宇宙位置标记 + 口袋图宿主），不承载**完整内构**渲染
    ///   （固定模式下飞船经逆重降落以真实建筑落到宿主图，属未来阶段）；
    /// - 但宿主图上照常显示各船的**船体框架外壳**——玩家误入宿主图不能看到空白：
    ///   聚焦宿主图时以 HostFrame（宇宙原点，锚在宿主图中心）重放全部飞船；
    /// - 驾驶模式聚焦自己船图（identity），其他船以此外壳显示；
    /// - 显示只是投影结果，绝不反向驱动宇宙状态（不变量 #9）。
    /// </summary>
    public static class ShellRenderer
    {
        // RimWorld 1.6 Custom/Terrain hard 顶点程序的实际指令：
        // o1.xy = v0.xz * 0.0625。代理改用显式 UV 时必须保留同一倍率。
        private const float TerrainUvScale = 0.0625f;

        private sealed class TerrainReplayEntry
        {
            public Map map;
            public Section section;
            public Mesh mesh;
            public Material material;
        }

        // 原版 Terrain shader 直接按地图/世界 XZ 取样，不读取普通 UV。几何经代理矩阵旋转后，
        // 这种取样仍锁在屏幕轴上。这里只缓存一份带 ship-local UV 的派生网格并换用普通透明
        // shader；源 Map 的 SectionLayer 仍是事实源。缓存不存档，dirty/换焦/重置时释放重建。
        private static readonly Dictionary<LayerSubMesh, TerrainReplayEntry> TerrainReplayCache =
            new Dictionary<LayerSubMesh, TerrainReplayEntry>();

        private static Map lastObservedMap;

        // 纯反射，不引入 0Harmony 运行时依赖（阶段1 无补丁原则）。
        private static readonly FieldInfo SectionsField =
            typeof(MapDrawer).GetField("sections", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo LayersField =
            typeof(Section).GetField("layers", BindingFlags.NonPublic | BindingFlags.Instance);

        // 补建路径（2026-09-12 实测"结构变更后旧网格残留"根因）：
        // Section.TryUpdate 对视野外的 section 会清零 dirtyFlags 但保留 anyLayerDirty=true，
        // 原版由 Section.DrawSection 每帧检查 anyLayerDirty 并调 RegenerateDirtyLayers 补建；
        // 只查 dirtyFlags != 0 的重放会永久错过这些 section → 旧结构残留叠加。
        private static readonly FieldInfo AnyLayerDirtyField =
            typeof(Section).GetField("anyLayerDirty", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo RegenerateDirtyLayersMethod =
            typeof(Section).GetMethod("RegenerateDirtyLayers", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void DrawNonFocusedShips(UniverseManager universe)
        {
            Map current = Find.CurrentMap;
            if (current == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            if (current != lastObservedMap)
            {
                // 一艘船在聚焦期间由原版更新网格、不会经过代理 dirty 消费路径；离开该图时
                // 丢掉其派生地形缓存，保证下一次成为代理时从最新 SectionLayer 重建。
                ShipState previousFrame = universe.ShipOnMap(lastObservedMap);
                if (previousFrame?.map != null)
                {
                    InvalidateTerrainCache(previousFrame.map);
                }
                lastObservedMap = current;
            }
            // 显示参考系由"当前看的是哪张图"决定，而不是持久化的 focusedShipId——
            // 玩家可能经殖民者栏/地图按钮等任何原版途径切图，显示层必须无差别跟随。
            ShipState frame = universe.ShipOnMap(current);
            if (frame != null)
            {
                foreach (ShipState ship in universe.ships)
                {
                    if (ship != frame)
                    {
                        DrawShipShell(ship, frame, current);
                    }
                }
            }
            else if (universe.hostMap == current)
            {
                // 宿主图视角 = 空间总览：以 HostFrame（纯显示参考系）重放全部飞船外壳，
                // 玩家误入宿主图不会看到空白。
                ShipState hostFrame = universe.HostFrame;
                foreach (ShipState ship in universe.ships)
                {
                    DrawShipShell(ship, hostFrame, current);
                }
            }
        }

        private static void DrawShipShell(ShipState ship, ShipState frame, Map frameMap)
        {
            if (ship.map == null || SectionsField == null || LayersField == null)
            {
                return;
            }
            Map map = ship.map;
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, ship.pose.angle);
            Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, ship.pose.position, 0f);
            Vector2 center = SpaceTransform.ShipLocalOrigin(map);
            // 网格顶点是地图局部绝对坐标 v；目标 = R·(v − center) + drawPos = R·v + (drawPos − R·center)
            Vector3 translation = drawPos - rotation * new Vector3(center.x, 0f, center.y);
            Matrix4x4 matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);

            // 视野剔除：承载图视野矩形逆变换回飞船局部取包围盒（保守，只多画不漏画）。
            CellRect shipLocalView = TransformRectInverse(matrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(Section.Size);

            var sections = (Section[,])SectionsField.GetValue(map.mapDrawer);
            for (int i = 0; i < sections.GetLength(0); i++)
            {
                for (int j = 0; j < sections.GetLength(1); j++)
                {
                    Section section = sections[i, j];
                    // 门控用原始 CellRect（与 TryUpdate 内部 flag2 判定完全一致）；
                    // Bounds 是膨胀过的，用它做门会让"过门但 flag2=false"的 section
                    // 走进 deferral 分支产生滞后（2026-09-12 残留 bug 的一环）。
                    if (!section.CellRect.Overlaps(shipLocalView))
                    {
                        continue;
                    }
                    // 非当前地图的 Section 原版不自动重建，自驱动覆盖全部 deferral 状态：
                    // ① dirtyFlags != 0 → TryUpdate（视野内重建，视野外转 anyLayerDirty 记账）；
                    // ② anyLayerDirty / 任何 layer.Dirty 直接置位（不经 section 标记的路径）
                    //    → RegenerateDirtyLayers 补建（原版 Section.DrawSection 的兜底语义）。
                    // 船图为 Orbit 太空图（无雪/雨的持续标脏），RimExodus 警告的
                    // "全层重建性能灾难"场景不适用，正确性优先。
                    bool regenerated = false;
                    if (section.dirtyFlags != 0L)
                    {
                        regenerated = section.TryUpdate(shipLocalView);
                    }
                    if (AnyLayerDirtyField != null && RegenerateDirtyLayersMethod != null && NeedsLayerCatchUp(section))
                    {
                        RegenerateDirtyLayersMethod.Invoke(section, null);
                        regenerated = true;
                    }
                    if (regenerated)
                    {
                        InvalidateTerrainCache(section);
                    }
                    var layers = (List<SectionLayer>)LayersField.GetValue(section);
                    for (int k = 0; k < layers.Count; k++)
                    {
                        SectionLayer layer = layers[k];
                        System.Type layerType = layer.GetType();
                        if (layerType != typeof(SectionLayer_Terrain) && layerType != typeof(SectionLayer_ThingsGeneral))
                        {
                            continue;
                        }
                        if (!layer.Visible)
                        {
                            continue;
                        }
                        for (int m = 0; m < layer.subMeshes.Count; m++)
                        {
                            LayerSubMesh subMesh = layer.subMeshes[m];
                            // 严格镜像 MapDrawLayer.DrawLayer 的提交门槛。Regenerate() 会先
                            // ClearSubMeshes：若某材质已从结构中消失，对应 subMesh 会保持在列表里，
                            // finalized=false，但 Unity Mesh 在没有新顶点可 Finalize 时仍暂存旧几何。
                            // 仅检查 mesh != null 会把这份旧几何继续提交，表现为拆改后旧 sprite 叠加。
                            if (!subMesh.finalized || subMesh.disabled || subMesh.mesh == null)
                            {
                                continue;
                            }
                            if (layerType == typeof(SectionLayer_Terrain))
                            {
                                DrawTerrainSubMesh(map, section, subMesh, matrix);
                            }
                            else
                            {
                                Graphics.DrawMesh(subMesh.mesh, matrix, subMesh.material, subMesh.renderLayer);
                            }
                        }
                    }
                }
            }
        }

        private static void DrawTerrainSubMesh(Map map, Section section, LayerSubMesh source, Matrix4x4 matrix)
        {
            // ShadowMask 等无主纹理材质不是可见甲板；普通代理材质也无法有意义地重放它们。
            if (source.material == null || source.material.mainTexture == null)
            {
                return;
            }
            if (!TerrainReplayCache.TryGetValue(source, out TerrainReplayEntry entry))
            {
                entry = BuildTerrainReplay(map, section, source);
                if (entry == null)
                {
                    return;
                }
                TerrainReplayCache.Add(source, entry);
            }
            Graphics.DrawMesh(entry.mesh, matrix, entry.material, source.renderLayer);
        }

        private static TerrainReplayEntry BuildTerrainReplay(Map map, Section section, LayerSubMesh source)
        {
            Vector3[] vertices = source.mesh.vertices;
            int[] triangles = source.mesh.triangles;
            if (vertices.Length == 0 || triangles.Length == 0)
            {
                return null;
            }

            var uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                // Terrain shader 的隐式坐标就是源地图 XZ；把它显式烘进 UV 后，代理矩阵只改变
                // 顶点位置，纹理坐标仍留在 ship-local 空间，因此会和船体一起旋转。
                uvs[i] = new Vector2(vertices[i].x * TerrainUvScale, vertices[i].z * TerrainUvScale);
            }

            var mesh = new Mesh
            {
                name = "DeltaV_TerrainReplay_" + source.mesh.name,
                vertices = vertices,
                uv = uvs,
                triangles = triangles
            };
            Color32[] colors = source.mesh.colors32;
            if (colors != null && colors.Length == vertices.Length)
            {
                mesh.colors32 = colors;
            }
            mesh.RecalculateBounds();

            var request = new MaterialRequest(source.material.mainTexture, ShaderDatabase.Transparent, source.material.color)
            {
                renderQueue = source.material.renderQueue
            };
            return new TerrainReplayEntry
            {
                map = map,
                section = section,
                mesh = mesh,
                material = MaterialPool.MatFrom(request)
            };
        }

        private static void InvalidateTerrainCache(Section section)
        {
            var remove = new List<LayerSubMesh>();
            foreach (KeyValuePair<LayerSubMesh, TerrainReplayEntry> pair in TerrainReplayCache)
            {
                if (pair.Value.section == section)
                {
                    UnityEngine.Object.Destroy(pair.Value.mesh);
                    remove.Add(pair.Key);
                }
            }
            for (int i = 0; i < remove.Count; i++)
            {
                TerrainReplayCache.Remove(remove[i]);
            }
        }

        private static void InvalidateTerrainCache(Map map)
        {
            var remove = new List<LayerSubMesh>();
            foreach (KeyValuePair<LayerSubMesh, TerrainReplayEntry> pair in TerrainReplayCache)
            {
                if (pair.Value.map == map)
                {
                    UnityEngine.Object.Destroy(pair.Value.mesh);
                    remove.Add(pair.Key);
                }
            }
            for (int i = 0; i < remove.Count; i++)
            {
                TerrainReplayCache.Remove(remove[i]);
            }
        }

        public static void ClearRuntimeCaches()
        {
            foreach (TerrainReplayEntry entry in TerrainReplayCache.Values)
            {
                UnityEngine.Object.Destroy(entry.mesh);
            }
            TerrainReplayCache.Clear();
            lastObservedMap = null;
        }

        /// <summary>补建判定：anyLayerDirty 记账，或任何层被直接置 Dirty（不经 section 标记的路径）。</summary>
        private static bool NeedsLayerCatchUp(Section section)
        {
            if ((bool)AnyLayerDirtyField.GetValue(section))
            {
                return true;
            }
            var layers = (List<SectionLayer>)LayersField.GetValue(section);
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].Dirty)
                {
                    return true;
                }
            }
            return false;
        }

        private static CellRect TransformRectInverse(Matrix4x4 matrix, CellRect rect)
        {
            Matrix4x4 inverse = matrix.inverse;
            Vector3 a = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.minZ));
            Vector3 b = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.minZ));
            Vector3 c = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.maxZ));
            Vector3 d = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.maxZ));
            float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
            float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
            float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
            float maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
            return CellRect.FromLimits(Mathf.FloorToInt(minX), Mathf.FloorToInt(minZ), Mathf.CeilToInt(maxX), Mathf.CeilToInt(maxZ));
        }
    }
}
