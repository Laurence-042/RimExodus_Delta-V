using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public readonly struct SpaceShieldFragmentContext
    {
        public readonly ThingDef sourceDef;
        public readonly SpaceObject sourceContainer;
        public readonly Vector3 hit;
        public readonly Vector2 direction;
        public readonly float speed;
        public readonly float damageScale;
        public readonly float armorScale;
        public readonly Thing launcher;

        public SpaceShieldFragmentContext(ThingDef sourceDef, SpaceObject sourceContainer,
            Vector3 hit, Vector2 direction, float speed, float damageScale, float armorScale,
            Thing launcher)
        {
            this.sourceDef = sourceDef;
            this.sourceContainer = sourceContainer;
            this.hit = hit;
            this.direction = direction;
            this.speed = speed;
            this.damageScale = damageScale;
            this.armorScale = armorScale;
            this.launcher = launcher;
        }
    }

    /// <summary>弹体类型拥有自己的盾反应与碎裂初始化逻辑；碰撞核心只调用此契约。</summary>
    public abstract class SpaceShieldProjectileBehavior
    {
        private static readonly Dictionary<Type, SpaceShieldProjectileBehavior> Instances =
            new Dictionary<Type, SpaceShieldProjectileBehavior>();

        public static SpaceShieldProjectileBehavior For(ThingDef def)
        {
            CompProperties_SpaceProjectile props = def?.GetCompProperties<CompProperties_SpaceProjectile>();
            Type type = props?.shieldBehaviorClass ?? typeof(UnshieldedProjectileBehavior);
            if (!typeof(SpaceShieldProjectileBehavior).IsAssignableFrom(type))
                type = typeof(UnshieldedProjectileBehavior);
            if (!Instances.TryGetValue(type, out SpaceShieldProjectileBehavior response))
            {
                response = (SpaceShieldProjectileBehavior)Activator.CreateInstance(type);
                Instances[type] = response;
            }
            return response;
        }

        public virtual bool IgnoresShield => false;
        public virtual bool Shatters(CompProperties_SpaceProjectile props, float incidenceDeg) => false;

        public virtual float PulseCost(CompProperties_SpaceProjectile props, int strength,
            float directionChangeDeg, bool shatter)
        {
            return props.shieldPulseCost * strength
                * (shatter ? 1f : Mathf.Max(0.1f, directionChangeDeg / 45f));
        }

        public virtual void SpawnFragments(SpaceProjectileService service,
            in SpaceShieldFragmentContext context) { }
    }

    public sealed class UnshieldedProjectileBehavior : SpaceShieldProjectileBehavior
    {
        public override bool IgnoresShield => true;
    }

    public sealed class RefractingShieldProjectileBehavior : SpaceShieldProjectileBehavior { }

    public class FragmentingShieldProjectileBehavior : SpaceShieldProjectileBehavior
    {
        public override bool Shatters(CompProperties_SpaceProjectile props, float incidenceDeg)
        {
            return props.shieldFragmentDef != null && props.shieldFragmentCount > 0
                && incidenceDeg > props.shieldBreakAngleDeg;
        }

        public override void SpawnFragments(SpaceProjectileService service,
            in SpaceShieldFragmentContext context)
        {
            CompProperties_SpaceProjectile props = context.sourceDef
                .GetCompProperties<CompProperties_SpaceProjectile>();
            if (props?.shieldFragmentDef == null) return;
            // 每个原弹体类型可覆盖本方法；此通用实心弹实现只读取自身 Def 的配置。
            int count = Mathf.Clamp(props.shieldFragmentCount, 1, 12);
            for (int i = 0; i < count; i++)
            {
                float span = count == 1 ? 0f : (2f * i / (count - 1) - 1f)
                    * props.shieldFragmentScatterDeg;
                Vector2 direction = SpaceTransform.Rotate(context.direction,
                    span * Mathf.Deg2Rad);
                service.LaunchShieldFragment(props.shieldFragmentDef, context, direction,
                    context.speed * props.shieldFragmentSpeedFactor,
                    context.damageScale * props.shieldFragmentDamageFraction,
                    context.armorScale * props.shieldFragmentArmorFactor);
            }
        }
    }

    public sealed class CannonShieldProjectileBehavior : FragmentingShieldProjectileBehavior { }
    public sealed class RailgunShieldProjectileBehavior : FragmentingShieldProjectileBehavior { }
}
