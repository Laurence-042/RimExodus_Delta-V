using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>一次通用分帧地图生成请求。业务层只提交请求，不接触 MapGenerator 静态状态。</summary>
    public sealed class IncrementalMapGenerationRequest
    {
        public string key;
        public float priority;
        public Func<float, float> capacityScore;
        public MapParent parent;
        public IntVec3 size;
        public MapGeneratorDef generator;
        public IEnumerable<GenStepWithParams> extraGenSteps;
        public Action<Map> beforeContentGeneration;
        public Action<Map> onComplete;
        public Action<Exception> onFailure;
        public bool isPocketMap;
    }

    /// <summary>
    /// 单个 GenStep 的可续执行适配器。未知步骤仍可原子执行；新增步骤内分帧不需要修改调度器。
    /// </summary>
    public interface IIncrementalGenStepAdapter : IDisposable
    {
        void Start(Map map, GenStepWithParams step, int seed);

        /// <returns>true = 步骤完成；false = 下帧继续。</returns>
        bool Advance(float deadlineRealtime);
    }

    /// <summary>
    /// 可抽取的通用分帧 MapGenerator 复刻。全局单飞，逐渲染帧推进；准备与收尾保持原版顺序。
    /// </summary>
    public sealed class IncrementalMapGenerationService : GameComponent
    {
        private sealed class ActiveJob
        {
            public IncrementalMapGenerationRequest request;
            public Map map;
            public List<GenStepWithParams> steps;
            public object[] originalArgs;
            public int seed;
            public ulong postGenerationRandState;
            public int stepIndex;
            public IIncrementalGenStepAdapter adapter;
            public bool finalizing;
        }

        private readonly List<IncrementalMapGenerationRequest> Queue =
            new List<IncrementalMapGenerationRequest>();
        private readonly List<Func<GenStepWithParams, IIncrementalGenStepAdapter>> AdapterFactories =
            new List<Func<GenStepWithParams, IIncrementalGenStepAdapter>>();

        private ActiveJob active;
        private bool drainingSynchronously;
        // mapBeingGenerated 被外部同步生成短暂占用是正常的（玩家降落等）；只有持续
        // 占用才说明静态状态被遗留脏置，此时排队 POI 会整体静默停摆——必须可见。
        private float blockedSinceRealtime = -1f;

        private static readonly FieldInfo GravshipField = AccessTools.Field(typeof(MapGenerator), "gravship");
        private static readonly FieldInfo TmpGenStepsField = AccessTools.Field(typeof(MapGenerator), "tmpGenSteps");
        private static readonly MethodInfo ClearWorkingDataMethod =
            AccessTools.Method(typeof(MapGenerator), "ClearWorkingData");
        private static readonly PropertyInfo RandStateProperty =
            AccessTools.Property(typeof(Rand), "StateCompressed");

        public bool IsAnyGenerating => active != null;

        public string ProgressDescription => active == null
            ? null
            : active.finalizing
                ? "Finalizing"
                : $"{Mathf.Min(active.stepIndex + 1, active.steps.Count)}/{active.steps.Count} " +
                  (active.stepIndex < active.steps.Count ? active.steps[active.stepIndex].def.defName : "Finalizing");

        public float Progress01 => active == null ? 0f
            : active.finalizing ? 1f : active.steps.Count == 0 ? 0f
            : Mathf.Clamp01((float)active.stepIndex / active.steps.Count);

        public bool IsGenerating(Map map)
        {
            return map != null && active?.map == map;
        }

        public bool IsIncomplete(Map map)
        {
            return map != null && active?.map == map && !active.finalizing;
        }

        public bool IsQueued(string key)
        {
            if (key.NullOrEmpty()) return false;
            return active?.request.key == key || Queue.Any(r => r.key == key);
        }

        /// <summary>
        /// 同步完成一次生成请求：与分帧路径共用 Prepare/Advance/Finish（含 RNG 隔离、
        /// FinalizeInit 与 postfix 复放），只是不跨帧。适用于极小图（如射弹临时图），
        /// 也绕开 MapGenerator.GenerateMap 方法体及其 Harmony 补丁链。
        /// </summary>
        public Map GenerateSynchronously(IncrementalMapGenerationRequest request)
        {
            if (request == null || request.parent == null || request.generator == null
                || request.size.x <= 0 || request.size.z <= 0)
            {
                Log.WarningOnce("[DeltaV] Synchronous map generation request rejected: " +
                    $"key={request?.key ?? "null"}, parent={(request != null && request.parent != null).ToString()}, " +
                    $"generator={(request != null && request.generator != null).ToString()}, size={request?.size.ToString() ?? "null"}",
                    0x44564702);
                return null;
            }
            DrainSynchronouslyForExternalGeneration();
            if (active != null) return null;
            if (request.parent.Destroyed || request.parent.HasMap) return null;
            if (!Current.Game.GetComponent<UniverseManager>().MapPool.TryMakeRoom(
                score: request.capacityScore?.Invoke(request.priority) ?? float.PositiveInfinity)) return null;
            try
            {
                ActiveJob job = Prepare(request);
                active = job;
                Advance(float.PositiveInfinity, true);
                // 成功与失败都会置空 active；失败路径已销毁半成品图，以 map 是否仍在
                // 游戏地图列表区分。
                return job.map != null && Find.Maps.Contains(job.map) ? job.map : null;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Synchronous map generation failed ({request.key}): {ex}");
                ActiveJob failed = active;
                active = null;
                if (failed != null) CleanupFailed(failed, ex);
                return null;
            }
        }

        public bool Enqueue(IncrementalMapGenerationRequest request)
        {
            if (request == null || request.parent == null || request.generator == null
                || request.size.x <= 0 || request.size.z <= 0 || request.key.NullOrEmpty())
            {
                // 必要行为日志：入队被静默拒绝曾让 POI 接近激活链整体失效且无迹可查。
                Log.WarningOnce("[DeltaV] Map generation request rejected: " +
                    $"key={request?.key ?? "null"}, parent={(request != null && request.parent != null).ToString()}, " +
                    $"generator={(request != null && request.generator != null).ToString()}, size={request?.size.ToString() ?? "null"}",
                    0x44564701);
                return false;
            }
            if (IsQueued(request.key)) return false;
            Queue.Add(request);
            Queue.Sort((a, b) => a.priority.CompareTo(b.priority));
            return true;
        }

        private void RegisterAdapterFactory(
            Func<GenStepWithParams, IIncrementalGenStepAdapter> factory)
        {
            if (factory != null && !AdapterFactories.Contains(factory)) AdapterFactories.Add(factory);
        }

        public bool CancelQueued(string key)
        {
            return !key.NullOrEmpty() && Queue.RemoveAll(r => r.key == key) > 0;
        }

        public void UpdatePriority(string key, float priority)
        {
            IncrementalMapGenerationRequest request = Queue.FirstOrDefault(r => r.key == key);
            if (request == null) return;
            request.priority = priority;
            Queue.Sort((a, b) => a.priority.CompareTo(b.priority));
        }

        public void CancelQueuedForParent(MapParent parent)
        {
            if (parent != null) Queue.RemoveAll(r => r.parent == parent);
        }

        /// <summary>主动任务销毁 parent/map 时放弃生成静态状态，让原版删除继续。</summary>
        public void NotifyExternalMapRemoval(Map map)
        {
            if (map == null || active?.map != map) return;
            ActiveJob job = active;
            active = null;
            job.adapter?.Dispose();
            job.adapter = null;
            ResetGeneratorStatics();
            job.request.onFailure?.Invoke(new OperationCanceledException(
                "Map parent was explicitly removed during incremental generation."));
        }

        public void Reset()
        {
            Queue.Clear();
            if (active != null)
            {
                ActiveJob job = active;
                active = null;
                CleanupFailed(job, new OperationCanceledException("Game changed during incremental map generation."));
            }
        }

        public IncrementalMapGenerationService(Game game)
        {
            RegisterAdapterFactory(step =>
                step.def.genStep is GenStep_Space && ModsConfig.OdysseyActive
                    ? new TerrainFillGenStepAdapter(TerrainDefOf.Space, disableRegions: true)
                    : null);
            RegisterAdapterFactory(step =>
                step.def.genStep is GenStep_DeltaVShipFill
                    ? new TerrainFillGenStepAdapter(TerrainDefOf.Space, disableRegions: false)
                    : null);
        }

        public override void GameComponentUpdate()
        {
            if (Current.Game?.GetComponent<UniverseManager>()?.IsResettingScene == true) return;
            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;

            if (active == null)
            {
                TryStartNext();
                if (active == null) return;
            }

            float budgetMs = Mathf.Clamp(DeltaVMod.CurrentSettings?.incrementalMapGenBudgetMs ?? 8f, 1f, 16f);
            Advance(Time.realtimeSinceStartup + budgetMs / 1000f, false);
        }

        /// <summary>
        /// 原版同步 GenerateMap 将与本服务争用进程级静态状态时，先同步收完当前任务。
        /// 只在 Harmony Prefix 中调用，属于罕见兼容逃生路径。
        /// </summary>
        public void DrainSynchronouslyForExternalGeneration()
        {
            if (active == null) return;
            if (active.finalizing)
                throw new InvalidOperationException(
                    "A synchronous MapGenerator.GenerateMap call was attempted reentrantly during incremental finalization.");
            if (drainingSynchronously) return;
            drainingSynchronously = true;
            try
            {
                while (active != null)
                {
                    Advance(float.PositiveInfinity, true);
                }
            }
            finally
            {
                drainingSynchronously = false;
            }
        }

        private void TryStartNext()
        {
            if (Queue.Count == 0)
            {
                blockedSinceRealtime = -1f;
                return;
            }
            if (MapGenerator.mapBeingGenerated != null)
            {
                if (blockedSinceRealtime < 0f)
                {
                    blockedSinceRealtime = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - blockedSinceRealtime > 5f)
                {
                    Map held = MapGenerator.mapBeingGenerated;
                    Log.WarningOnce("[DeltaV] Incremental map generation stalled " +
                        $"{Time.realtimeSinceStartup - blockedSinceRealtime:F0}s: MapGenerator.mapBeingGenerated " +
                        $"is held by map #{held?.uniqueID.ToString() ?? "null"}; {Queue.Count} request(s) waiting. " +
                        "An external synchronous generation never returned or left the static dirty.",
                        0x44564700 + (int)blockedSinceRealtime);
                }
                return;
            }
            blockedSinceRealtime = -1f;
            Queue.RemoveAll(request => request.parent.Destroyed || request.parent.HasMap);
            if (Queue.Count == 0) return;
            SpaceMapPool pool = Current.Game.GetComponent<UniverseManager>().MapPool;
            IncrementalMapGenerationRequest request = Queue[0];
            float score = request.capacityScore?.Invoke(request.priority) ?? float.PositiveInfinity;
            for (int i = 1; i < Queue.Count; i++)
            {
                IncrementalMapGenerationRequest candidate = Queue[i];
                float candidateScore = candidate.capacityScore?.Invoke(candidate.priority) ?? float.PositiveInfinity;
                if (candidateScore <= score) continue;
                request = candidate;
                score = candidateScore;
            }
            if (!pool.TryMakeRoom(score: score)) return;
            Queue.Remove(request);

            if (request.parent.Destroyed || request.parent.HasMap) return;

            try
            {
                active = Prepare(request);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"map generation started: {request.key}, steps={active.steps.Count}");
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Incremental map generation prepare failed ({request.key}): {ex}");
                request.onFailure?.Invoke(ex);
                active = null;
            }
        }

        private static ActiveJob Prepare(IncrementalMapGenerationRequest request)
        {
            RimExodus.Maps.MapRuntime.Current.Generation.Acquire(SharedMapBackend.Owner,
                    () => Current.Game.GetComponent<UniverseManager>().Generation.DrainSynchronouslyForExternalGeneration());
            ProgramState oldState = Current.ProgramState;
            Current.ProgramState = ProgramState.MapInitializing;
            Rand.PushState();
            Map map = null;
            try
            {
                ClearWorkingData();
                MapGenerator.PlayerStartSpot = IntVec3.Invalid;
                MapGenerator.rootsToUnfog.Clear();
                GravshipField?.SetValue(null, null);

                int seed = Gen.HashCombineInt(Find.World.info.Seed,
                    request.isPocketMap ? request.parent.ID : request.parent.Tile.GetHashCode());
                Rand.Seed = seed;

                map = new Map
                {
                    uniqueID = Find.UniqueIDsManager.GetNextMapID(),
                    generationTick = GenTicks.TicksGame,
                    events = null
                };
                map.events = new MapEvents(map);
                MapGenerator.mapBeingGenerated = map;
                RimExodus.Maps.MapRuntime.Current.Generation.Bind(SharedMapBackend.Owner, map);
                map.info.Size = request.size;
                map.info.parent = request.parent;
                if (ModsConfig.OdysseyActive && request.extraGenSteps != null
                    && request.extraGenSteps.Any(step => step.def == GenStepDefOf.ReserveGravshipArea))
                    map.wasSpawnedViaGravShipLanding = true;
                map.generatorDef = request.generator;
                map.info.disableSunShadows = request.generator.disableShadows;
                if (request.isPocketMap)
                {
                    map.info.isPocketMap = true;
                    map.pocketTileInfo = new Tile { PrimaryBiome = request.generator.pocketMapProperties.biome };
                    foreach (TileMutatorDef mutator in request.generator.pocketMapProperties.tileMutators)
                    {
                        map.TileInfo.AddMutator(mutator);
                    }
                }
                map.ConstructComponents();
                foreach (TileMutatorDef mutator in map.TileInfo.Mutators)
                {
                    mutator.Worker?.Init(map);
                }
                Current.Game.AddMap(map);
                if (!Find.Maps.Contains(map))
                    throw new InvalidOperationException("The game rejected the new map before content generation.");
                if (request.generator.isUnderground)
                {
                    foreach (IntVec3 cell in map.AllCells)
                    {
                        map.roofGrid.SetRoof(cell, request.generator.roofDef ?? RoofDefOf.RoofRockThick);
                    }
                }
                request.beforeContentGeneration?.Invoke(map);

                List<GenStepWithParams> steps = BuildSteps(map, request);
                map.areaManager.AddStartingAreas();
                map.weatherDecider.StartInitialWeather();

                // 对齐 GenerateContentsIntoMap 入口：extra init 产生的临时 MapGenerator data
                // 在正式步骤前释放，再建立本轮步骤共享的 gravship/noise 状态。
                ClearWorkingData();
                if (ModsConfig.OdysseyActive)
                {
                    GenStepWithParams marker = steps.FirstOrDefault(step => step.def == GenStepDefOf.GravshipMarker);
                    if (marker.def != null) GravshipField?.SetValue(null, marker.parms.gravship);
                }
                List<GenStepWithParams> tmp = TmpGenStepsField?.GetValue(null) as List<GenStepWithParams>;
                tmp?.Clear();
                tmp?.AddRange(steps);
                Rand.PushState(seed);
                try { RockNoises.Init(map); }
                finally { Rand.PopState(); }

                return new ActiveJob
                {
                    request = request,
                    map = map,
                    steps = steps,
                    seed = seed,
                    postGenerationRandState = RandStateProperty != null
                        ? (ulong)RandStateProperty.GetValue(null) : (uint)seed,
                    originalArgs = new object[]
                    {
                        request.size, request.parent, request.generator, request.extraGenSteps,
                        request.beforeContentGeneration, request.isPocketMap, false
                    }
                };
            }
            catch
            {
                ResetGeneratorStatics();
                if (map != null && Find.Maps.Contains(map))
                {
                    try { Current.Game.GetComponent<UniverseManager>().Lifecycle.DeinitMap(map, false); }
                    catch { Find.Maps.Remove(map); }
                }
                else if (map != null) RimExodus.Maps.MapRuntime.Current.Forget(map);
                throw;
            }
            finally
            {
                Rand.PopState();
                Current.ProgramState = oldState;
            }
        }

        private static List<GenStepWithParams> BuildSteps(Map map, IncrementalMapGenerationRequest request)
        {
            IEnumerable<GenStepWithParams> result = request.generator.genSteps
                .Where(IsValidScenarioStep).Select(d => new GenStepWithParams(d, default(GenStepParams)));
            foreach (TileMutatorDef mutator in map.TileInfo.Mutators)
            {
                if (mutator.extraGenSteps.Any())
                    result = result.Concat(mutator.extraGenSteps.Select(d => new GenStepWithParams(d, default(GenStepParams))));
            }
            if (map.Biome.extraGenSteps.Any())
                result = result.Concat(map.Biome.extraGenSteps.Where(IsValidScenarioStep)
                    .Select(d => new GenStepWithParams(d, default(GenStepParams))));
            if (map.Biome.preventGenSteps.Any())
                result = result.Where(s => !map.Biome.preventGenSteps.Contains(s.def));
            foreach (TileMutatorDef mutator in map.TileInfo.Mutators)
            {
                if (mutator.preventGenSteps.Any())
                    result = result.Where(s => !mutator.preventGenSteps.Contains(s.def));
            }
            if (request.extraGenSteps != null) result = result.Concat(request.extraGenSteps);
            List<GenStepWithParams> ordered = result.Distinct()
                .OrderBy(s => s.def.order).ThenBy(s => s.def.index).ToList();
            ordered.RemoveAll(step => ordered.Any(other => other.def.preventsGenSteps != null
                && other.def.preventsGenSteps.Contains(step.def)));
            return ordered;
        }

        private static bool IsValidScenarioStep(GenStepDef def)
        {
            return !Find.Scenario.AllParts.Any(p =>
                typeof(ScenPart_DisableMapGen).IsAssignableFrom(p.def.scenPartClass) && p.def.genStep == def);
        }

        private void Advance(float deadline, bool synchronous)
        {
            ActiveJob job = active;
            if (job == null) return;
            try
            {
                while (job.stepIndex < job.steps.Count)
                {
                    GenStepWithParams step = job.steps[job.stepIndex];
                    if (job.adapter == null)
                    {
                        job.adapter = CreateAdapter(step);
                        if (job.adapter != null)
                            job.adapter.Start(job.map, step, Gen.HashCombineInt(job.seed, SeedPartFor(job.steps, job.stepIndex)));
                    }

                    if (job.adapter != null)
                    {
                        if (!job.adapter.Advance(deadline)) return;
                        job.adapter.Dispose();
                        job.adapter = null;
                    }
                    else
                    {
                        RunAtomicStep(job, step);
                    }

                    // 新生成 Thing 在全局 tick 表中注册；半成品阶段必须持续摘除。
                    Find.TickManager.RemoveAllFromMap(job.map);
                    job.stepIndex++;
                    if (!synchronous && Time.realtimeSinceStartup >= deadline) return;
                }
                RockNoises.Reset();
                Finish(job);
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] Incremental map generation failed ({job.request.key}, step {job.stepIndex}): {ex}");
                active = null;
                CleanupFailed(job, ex);
            }
        }

        private static void RunAtomicStep(ActiveJob job, GenStepWithParams step)
        {
            ProgramState oldState = Current.ProgramState;
            Current.ProgramState = ProgramState.MapInitializing;
            Rand.PushState();
            float started = Time.realtimeSinceStartup;
            try
            {
                GenStepParams parms = step.parms;
                if (GravshipField?.GetValue(null) is Gravship gravship) parms.gravship = gravship;
                Rand.Seed = Gen.HashCombineInt(job.seed, SeedPartFor(job.steps, job.stepIndex));
                step.def.genStep.Generate(job.map, parms);
                if (job.map.pathing.IncrementalDirtyingDisabled)
                {
                    Log.Error($"[DeltaV] GenStep {step.def.defName} left incremental path dirtying disabled; restoring it.");
                    job.map.pathing.ReEnableIncrementalDirtying();
                }
            }
            catch (Exception ex)
            {
                // 原版 GenerateContentsIntoMap 同样逐步骤记录错误后继续。
                Log.Error($"[DeltaV] Error in incremental GenStep {step.def.defName}: {ex}");
            }
            finally
            {
                Rand.PopState();
                Current.ProgramState = oldState;
            }
            float elapsed = (Time.realtimeSinceStartup - started) * 1000f;
            float budget = Mathf.Clamp(DeltaVMod.CurrentSettings?.incrementalMapGenBudgetMs ?? 8f, 1f, 16f);
            if (elapsed > budget)
            {
                Log.Warning($"[DeltaV] Incremental GenStep '{step.def.defName}' took {elapsed:F1}ms " +
                    $"(budget {budget:F1}ms); add an IIncrementalGenStepAdapter to split it.");
            }
        }

        private void Finish(ActiveJob job)
        {
            job.finalizing = true;
            ProgramState oldState = Current.ProgramState;
            Current.ProgramState = ProgramState.MapInitializing;
            Map completedMap = job.map;
            Rand.PushState();
            if (RandStateProperty != null) RandStateProperty.SetValue(null, job.postGenerationRandState);
            else Rand.Seed = job.seed;
            try
            {
                Find.Scenario.PostMapGenerate(job.map);
                job.map.FinalizeInit();
                MapComponentUtility.MapGenerated(job.map);
                job.request.parent?.PostMapGenerate();
                foreach (GenStepWithParams step in job.steps)
                {
                    GenStepParams parms = step.parms;
                    if (GravshipField?.GetValue(null) is Gravship gravship) parms.gravship = gravship;
                    step.def.genStep.PostMapInitialized(job.map, parms);
                }
                if (GravshipField?.GetValue(null) is Gravship landed && job.map.Parent.CanBeSettled)
                    GravshipUtility.SettleTile(job.map);
                if (job.map.TileInfo.Layer.Def.isSpace)
                    LessonAutoActivator.TeachOpportunity(ConceptDefOf.Orbit, OpportunityType.Critical);
                RestoreThingTicks(job.map);
                job.request.onComplete?.Invoke(job.map);
            }
            finally
            {
                Rand.PopState();
                Current.ProgramState = oldState;
                ResetGeneratorStatics();
                active = null;
            }

            GenerateMapPostfixReplay.Replay(completedMap, job.originalArgs);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"map generation completed: {job.request.key}, map={completedMap.uniqueID}");
        }

        private void CleanupFailed(ActiveJob job, Exception error)
        {
            job.adapter?.Dispose();
            job.adapter = null;
            Map map = job.map;
            ResetGeneratorStatics();
            if (map != null && Find.Maps.Contains(map))
            {
                try { Current.Game.GetComponent<UniverseManager>().Lifecycle.DeinitMap(map, false); }
                catch (Exception ex)
                {
                    Log.Error($"[DeltaV] Failed to remove partial map: {ex}");
                    Find.Maps.Remove(map);
                }
            }
            job.request.onFailure?.Invoke(error);
        }

        private IIncrementalGenStepAdapter CreateAdapter(GenStepWithParams step)
        {
            for (int i = 0; i < AdapterFactories.Count; i++)
            {
                IIncrementalGenStepAdapter adapter = AdapterFactories[i](step);
                if (adapter != null) return adapter;
            }
            return null;
        }

        private static int SeedPartFor(List<GenStepWithParams> steps, int index)
        {
            int part = steps[index].def.genStep.SeedPart;
            int repeats = 0;
            for (int i = 0; i < index; i++)
                if (steps[i].def.genStep.SeedPart == part) repeats++;
            return part + repeats;
        }

        private static void RestoreThingTicks(Map map)
        {
            Find.TickManager.RemoveAllFromMap(map);
            ThingOwner things = map.spawnedThings;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing != null && thing.Spawned && !thing.Destroyed)
                    Find.TickManager.RegisterAllTickabilityFor(thing);
            }
        }

        private static void ClearWorkingData()
        {
            if (ClearWorkingDataMethod == null)
                throw new MissingMethodException("MapGenerator.ClearWorkingData");
            ClearWorkingDataMethod.Invoke(null, null);
        }

        private static void ResetGeneratorStatics()
        {
            var coordinator = RimExodus.Maps.MapRuntime.Current.Generation;
            if (!coordinator.IsOwnedBy(SharedMapBackend.Owner)) return;
            coordinator.Release(SharedMapBackend.Owner);
            try { ClearWorkingDataMethod?.Invoke(null, null); } catch { }
            try { MapGenerator.mapBeingGenerated = null; } catch { }
            try { GravshipField?.SetValue(null, null); } catch { }
            try { RockNoises.Reset(); } catch { }
        }

        public sealed class TerrainFillGenStepAdapter : IIncrementalGenStepAdapter
        {
            private readonly TerrainDef terrain;
            private readonly bool disableRegions;
            private Map map;
            private int cellIndex;

            public TerrainFillGenStepAdapter(TerrainDef terrain, bool disableRegions)
            {
                this.terrain = terrain;
                this.disableRegions = disableRegions;
            }

            public void Start(Map map, GenStepWithParams step, int seed)
            {
                this.map = map;
                cellIndex = 0;
                if (disableRegions) map.regionAndRoomUpdater.Enabled = false;
            }

            public bool Advance(float deadlineRealtime)
            {
                int total = map.cellIndices.NumGridCells;
                do
                {
                    int end = Math.Min(cellIndex + 256, total);
                    for (; cellIndex < end; cellIndex++)
                        map.terrainGrid.SetTerrain(map.cellIndices.IndexToCell(cellIndex), terrain);
                }
                while (cellIndex < total && Time.realtimeSinceStartup < deadlineRealtime);
                return cellIndex >= total;
            }

            public void Dispose()
            {
                map = null;
            }
        }
    }
}
