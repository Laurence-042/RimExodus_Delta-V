using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>原版 gizmo 合组只点击代表命令；把同组目标交给同一个设置动作。</summary>
    internal sealed class Command_BatchSetting<T> : Command where T : ThingComp
    {
        private readonly string setting;
        private readonly Action<T[]> apply;
        private readonly List<T> targets = new List<T>();

        internal Command_BatchSetting(string setting, T target, Action<T[]> apply)
        {
            this.setting = setting + (CompSpaceWeapon.ForComponent(target)?.Mount is VirtualWeaponMount mount
                ? ":" + mount.SettingsKey : "");
            this.apply = apply;
            targets.Add(target);
        }

        public override bool GroupsWith(Gizmo other) =>
            groupable && other is Command_BatchSetting<T> { groupable: true } command
            && command.setting == setting;

        public override bool InheritInteractionsFrom(Gizmo other)
        {
            foreach (T target in ((Command_BatchSetting<T>)other).targets)
                if (!targets.Contains(target)) targets.Add(target);
            return false;
        }

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            T[] active = targets.Where(t => t?.parent?.Spawned == true && !t.parent.Destroyed)
                .Distinct().ToArray();
            if (active.Length > 0) apply(active);
        }
    }

    internal sealed class Command_BatchToggle<T> : Command_Toggle where T : ThingComp
    {
        private readonly string setting;
        private readonly List<T> targets = new List<T>();

        internal Command_BatchToggle(string setting, T target, Func<T, bool> active,
            Action<T, bool> set)
        {
            this.setting = setting + (CompSpaceWeapon.ForComponent(target)?.Mount is VirtualWeaponMount mount
                ? ":" + mount.SettingsKey : "");
            targets.Add(target);
            isActive = () => active(target);
            toggleAction = () =>
            {
                bool next = !active(target);
                foreach (T item in targets.Distinct())
                    if (item?.parent?.Spawned == true && !item.parent.Destroyed)
                        set(item, next);
            };
        }

        public override bool GroupsWith(Gizmo other) =>
            groupable && other is Command_BatchToggle<T> { groupable: true } command
            && command.setting == setting;

        public override bool InheritInteractionsFrom(Gizmo other)
        {
            foreach (T target in ((Command_BatchToggle<T>)other).targets)
                if (!targets.Contains(target)) targets.Add(target);
            return false;
        }
    }
}
