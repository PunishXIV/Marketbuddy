using System;
using System.Numerics;
using Dalamud.Configuration;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        [NonSerialized] public const int MIN_PRICE = 1;
        [NonSerialized] public const int MAX_PRICE = 999999999;

        public bool HoldShiftToStop = true;
        public bool AutoOpenComparePrices = true;
        public bool AutoOpenHistory = true;
        public bool SaveToClipboard = true;
        public bool AutoInputNewPrice = true;
        public bool AutoConfirmNewPrice = true;
        public bool HoldCtrlToPaste = true;
        public bool HoldAltHistoryHandling = false;

        public bool AdjustMaxStackSizeInSellList = true;
        public Vector2 AdjustMaxStackSizeInSellListOffset = new Vector2(77, 10);
        public bool UseMaxStackSize = false;
        public int MaximumStackSize = 99;
        public int UndercutPrice = 1;
        public bool UndercutUsePercent = false;
        public int UndercutPercent = 1;

        /// <summary>
        /// If enabled, the final undercut price is rounded down to the nearest multiple of <see cref="PriceRoundingMultiple"/>
        /// (while still remaining strictly below the selected price).
        /// </summary>
        public bool EnablePriceRounding = false;

        /// <summary>
        /// The multiple used when <see cref="EnablePriceRounding"/> is enabled.
        /// Example: multiple=5 will yield prices ending in 0 or 5.
        /// </summary>
        public int PriceRoundingMultiple = 5;

        /// <summary>
        /// When true, the bulk-undercut button skips an item if the cheapest competitor's price
        /// is at least <see cref="BulkUndercutSkipPercent"/>% below the player's current price.
        /// </summary>
        public bool BulkUndercutSkipIfTooLow = true;

        /// <summary>
        /// Skip threshold (percent) for the bulk-undercut button. See <see cref="BulkUndercutSkipIfTooLow"/>.
        /// </summary>
        public int BulkUndercutSkipPercent = 25;

        /// <summary>
        /// Base delay (milliseconds) inserted between processing each item during a bulk-undercut run.
        /// Required because issuing market-board queries too fast triggers a server-side "retry later" error.
        /// </summary>
        public int BulkInterItemDelayMs = 3000;

        /// <summary>
        /// Random jitter (milliseconds) added on top of <see cref="BulkInterItemDelayMs"/>. The actual delay
        /// per item is base + Random(0..jitter). Mostly to make the cadence less mechanical.
        /// </summary>
        public int BulkInterItemDelayJitterMs = 1500;

        public int Version { get; set; } = 0;

        // the below exist just to make saving/loading less cumbersome
        [NonSerialized] private static Configuration? _cachedConfig;

        public void Save()
        {
            PluginInterface.SavePluginConfig(this);
        }

        public static Configuration GetOrLoad()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            if (PluginInterface.GetPluginConfig() is not Configuration conf)
            {
                conf = new Configuration();
                conf.Save();
            }
            else
            {
                if (conf.MaximumStackSize > 9999)
                    conf.MaximumStackSize = 9999;
                if (!conf.AutoInputNewPrice)
                    conf.AutoConfirmNewPrice = false;
                //if (!conf.AutoOpenComparePrices)
                //    conf.HoldShiftToStop = false;
                if (conf.UndercutPrice < 0)
                    conf.UndercutPrice = 0;

                if (conf.PriceRoundingMultiple < 1)
                    conf.PriceRoundingMultiple = 1;

                if (conf.BulkUndercutSkipPercent < 0)
                    conf.BulkUndercutSkipPercent = 0;
                if (conf.BulkUndercutSkipPercent > 99)
                    conf.BulkUndercutSkipPercent = 99;

                if (conf.BulkInterItemDelayMs < 0)
                    conf.BulkInterItemDelayMs = 0;
                if (conf.BulkInterItemDelayJitterMs < 0)
                    conf.BulkInterItemDelayJitterMs = 0;
            }

            _cachedConfig = conf;
            return _cachedConfig;
        }
    }
}