using RimWorld;
using Verse;

namespace DeltaV
{
    public static class SpaceCombatStats
    {
        private static StatCategoryDef Category =>
            DefDatabase<StatCategoryDef>.GetNamed("DeltaV_SpaceCombat");

        public static StatDrawEntry Entry(string key, string value, int priority) =>
            new StatDrawEntry(Category, key.Translate(), value, key.Translate(), priority);

        public static string WattDays(float amount) => amount.ToString("0.##") + " W·d";
        public static string Watts(float amount) => amount.ToString("0") + " W";
        public static string Ticks(int ticks) => ticks.ToString() + " " + "DeltaV_StatTicks".Translate();
    }
}
