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

        public static string EnergyAmount(float amount) => amount.ToString("F2");
        public static string WattDays(float amount) => EnergyAmount(amount) + " W·d";
        public static string Watts(float amount) => amount.ToString("0") + " W";
        public static string Seconds(int ticks) =>
            "DeltaV_StatSeconds".Translate((ticks / (float)GenTicks.TicksPerRealSecond).ToString("0.##"));
        public static string GameHours(int ticks) =>
            "DeltaV_StatGameHours".Translate((ticks / (float)GenDate.TicksPerHour).ToString("0.##"));
    }
}
