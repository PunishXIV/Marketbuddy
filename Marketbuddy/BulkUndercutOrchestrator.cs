using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Marketbuddy.Common;
using Marketbuddy.Structs;
using static Marketbuddy.Common.Dalamud;

namespace Marketbuddy
{
    internal enum BulkState
    {
        Idle,
        StartRow,
        WaitForContextMenu,
        ClickAdjustPrice,
        WaitForRetainerSell,
        ReadCurrentPrice,
        WaitForItemSearchResult,
        WaitForMarketData,
        DecideAndAct,
        WaitForDialogsClosed,
        NextRow,
    }

    internal unsafe class BulkUndercutOrchestrator : IDisposable
    {
        private Configuration conf => Configuration.GetOrLoad();
        private readonly Marketbuddy plugin;

        private BulkState state = BulkState.Idle;
        private int currentVisualIndex;
        private int playerCurrentPrice;
        private int framesInState;
        private bool subscribedToFramework;
        private long nextRowReadyTickMs;
        private readonly Random rng = new();

        // Snapshot of which visual rows had an item at Start, so server-side row reshuffling
        // doesn't make us skip newly-empty ones or visit duplicates.
        private bool[] slotHasItem = new bool[20];

        public bool IsRunning => state != BulkState.Idle;

        public BulkUndercutOrchestrator(Marketbuddy plugin)
        {
            this.plugin = plugin;
        }

        public void Start()
        {
            if (IsRunning) return;

            if (IPCManager.IsLocked)
            {
                ChatGui.PrintError("[Marketbuddy] Bulk undercut blocked: another plugin holds a Marketbuddy lock.");
                return;
            }

            if (!conf.AutoOpenComparePrices || !conf.AutoInputNewPrice || !conf.AutoConfirmNewPrice)
            {
                ChatGui.PrintError(
                    "[Marketbuddy] Bulk undercut needs these settings ON: 'Open current prices list', " +
                    "'Click a price sets your price...', and 'Closes the price list and confirms...'. " +
                    "Enable them in /mbuddy and try again.");
                return;
            }

            var sellList = Commons.GetUnitBase("RetainerSellList");
            if (sellList == null || !sellList->IsVisible)
            {
                ChatGui.PrintError("[Marketbuddy] Open a retainer's sell list first.");
                return;
            }

            // Snapshot which visual rows have items. RetainerSellList stores per-row item info
            // in AtkValues starting at index 15, with 13 values per row, for up to 20 rows.
            // atkValues[15 + i*13].Type == 0 means "empty slot".
            for (var i = 0; i < 20; i++) slotHasItem[i] = false;
            if (sellList->AtkValues != null)
            {
                for (var i = 0; i < 20; i++)
                {
                    var idx = 15 + i * 13;
                    slotHasItem[i] = sellList->AtkValues[idx].Type != 0;
                }
            }

            currentVisualIndex = 0;
            playerCurrentPrice = 0;
            framesInState = 0;
            state = BulkState.StartRow;

            if (!subscribedToFramework)
            {
                Framework.Update += OnFrameworkUpdate;
                subscribedToFramework = true;
            }

            Log.Information("[Marketbuddy] Bulk undercut started.");
        }

        public void Stop()
        {
            if (state == BulkState.Idle) return;

            state = BulkState.Idle;
            framesInState = 0;
            playerCurrentPrice = 0;

            if (subscribedToFramework)
            {
                Framework.Update -= OnFrameworkUpdate;
                subscribedToFramework = false;
            }

            Log.Information("[Marketbuddy] Bulk undercut stopped.");
        }

        public void Dispose()
        {
            if (subscribedToFramework)
            {
                Framework.Update -= OnFrameworkUpdate;
                subscribedToFramework = false;
            }
            state = BulkState.Idle;
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            if (state == BulkState.Idle) return;

            // Abort if another plugin locks us mid-run.
            if (IPCManager.IsLocked)
            {
                ChatGui.PrintError("[Marketbuddy] Bulk undercut aborted: a plugin lock was acquired.");
                Stop();
                return;
            }

            framesInState++;

            try
            {
                Step();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Marketbuddy] Bulk undercut state-machine error; stopping.");
                ChatGui.PrintError("[Marketbuddy] Bulk undercut hit an unexpected error and stopped. See /xllog.");
                Stop();
            }
        }

        private void Transition(BulkState next)
        {
            state = next;
            framesInState = 0;
        }

        private void Step()
        {
            switch (state)
            {
                case BulkState.StartRow:
                {
                    // Skip empty visual rows up front; advance to next or finish.
                    while (currentVisualIndex < 20 && !slotHasItem[currentVisualIndex])
                        currentVisualIndex++;

                    if (currentVisualIndex >= 20)
                    {
                        Log.Information("[Marketbuddy] Bulk undercut finished.");
                        Stop();
                        return;
                    }

                    var sellList = Commons.GetUnitBase("RetainerSellList");
                    if (sellList == null || !sellList->IsVisible)
                    {
                        ChatGui.PrintError("[Marketbuddy] Retainer sell list closed unexpectedly; stopping.");
                        Stop();
                        return;
                    }

                    // Opens the per-row context menu ("Adjust Price", "Move", etc.).
                    FireCallback(sellList, 0, currentVisualIndex, 1);
                    Transition(BulkState.WaitForContextMenu);
                    break;
                }

                case BulkState.WaitForContextMenu:
                {
                    var ctx = Commons.GetUnitBase("ContextMenu");
                    if (ctx != null && ctx->IsVisible)
                    {
                        Transition(BulkState.ClickAdjustPrice);
                    }
                    else if (framesInState > 180) // 3s
                    {
                        Log.Warning($"[Marketbuddy] Row {currentVisualIndex}: context menu didn't open; skipping.");
                        currentVisualIndex++;
                        Transition(BulkState.StartRow);
                    }
                    break;
                }

                case BulkState.ClickAdjustPrice:
                {
                    var ctx = Commons.GetUnitBase("ContextMenu");
                    if (ctx == null || !ctx->IsVisible)
                    {
                        Transition(BulkState.WaitForRetainerSell);
                        break;
                    }
                    // "Adjust Price" is option 0 on the retainer-sell context menu.
                    FireCallback(ctx, 0, 0, 0, -1, 0);
                    Transition(BulkState.WaitForRetainerSell);
                    break;
                }

                case BulkState.WaitForRetainerSell:
                {
                    var rs = Commons.GetUnitBase("RetainerSell");
                    if (rs != null && rs->IsVisible)
                    {
                        Transition(BulkState.ReadCurrentPrice);
                    }
                    else if (framesInState > 180)
                    {
                        Log.Warning($"[Marketbuddy] Row {currentVisualIndex}: RetainerSell didn't open; skipping.");
                        currentVisualIndex++;
                        Transition(BulkState.StartRow);
                    }
                    break;
                }

                case BulkState.ReadCurrentPrice:
                {
                    // Give the dialog 1 frame to settle so the price field is populated.
                    if (framesInState < 2) break;

                    var rs = Commons.GetUnitBase("RetainerSell");
                    if (rs == null || !rs->IsVisible)
                    {
                        currentVisualIndex++;
                        Transition(BulkState.StartRow);
                        break;
                    }

                    playerCurrentPrice = ReadCurrentPriceFromRetainerSell(rs);
                    Transition(BulkState.WaitForItemSearchResult);
                    break;
                }

                case BulkState.WaitForItemSearchResult:
                {
                    var isr = Commons.GetUnitBase("ItemSearchResult");
                    if (isr != null && isr->IsVisible)
                    {
                        Transition(BulkState.WaitForMarketData);
                    }
                    else if (framesInState > 180)
                    {
                        // Compare-prices window never opened — close the dialog and skip.
                        Log.Warning($"[Marketbuddy] Row {currentVisualIndex}: ItemSearchResult didn't open; skipping.");
                        CloseRetainerSell();
                        Transition(BulkState.WaitForDialogsClosed);
                    }
                    break;
                }

                case BulkState.WaitForMarketData:
                {
                    var isr = (AddonItemSearchResult*)Commons.GetUnitBase("ItemSearchResult");
                    if (isr == null || !isr->AtkUnitBase.IsVisible)
                    {
                        currentVisualIndex++;
                        Transition(BulkState.StartRow);
                        break;
                    }

                    var results = isr->Results;
                    if (results == null)
                    {
                        if (framesInState > 300)
                        {
                            Log.Warning($"[Marketbuddy] Row {currentVisualIndex}: market data never arrived; skipping.");
                            CloseItemSearchResult();
                            CloseRetainerSell();
                            Transition(BulkState.WaitForDialogsClosed);
                        }
                        break;
                    }

                    var listLength = results->ListLength;
                    if (listLength > 0)
                    {
                        // Confirm the first renderer holds a parseable price before acting.
                        var price = ReadPriceForListIndex(results, 0);
                        if (price > 0)
                        {
                            Transition(BulkState.DecideAndAct);
                            break;
                        }
                    }

                    if (framesInState > 300) // 5s
                    {
                        // No competitors after waiting → treat as "you are the only seller" → skip.
                        Log.Information($"[Marketbuddy] Row {currentVisualIndex}: no competitor listings; skipping.");
                        CloseItemSearchResult();
                        CloseRetainerSell();
                        Transition(BulkState.WaitForDialogsClosed);
                    }
                    break;
                }

                case BulkState.DecideAndAct:
                {
                    var isr = (AddonItemSearchResult*)Commons.GetUnitBase("ItemSearchResult");
                    if (isr == null || isr->Results == null)
                    {
                        currentVisualIndex++;
                        Transition(BulkState.StartRow);
                        break;
                    }

                    var cheapest = ReadPriceForListIndex(isr->Results, 0);
                    if (cheapest <= 0)
                    {
                        Log.Information($"[Marketbuddy] Row {currentVisualIndex}: could not read cheapest price; skipping.");
                        CloseItemSearchResult();
                        CloseRetainerSell();
                        Transition(BulkState.WaitForDialogsClosed);
                        break;
                    }

                    // Skip if competitor is too far below player's current price (integer compare avoids float drift).
                    if (conf.BulkUndercutSkipIfTooLow
                        && playerCurrentPrice > 0
                        && cheapest * 100 < (long)playerCurrentPrice * (100 - conf.BulkUndercutSkipPercent))
                    {
                        Log.Information($"[Marketbuddy] Row {currentVisualIndex}: cheapest {cheapest} < {100 - conf.BulkUndercutSkipPercent}% of current {playerCurrentPrice}; skipping.");
                        CloseItemSearchResult();
                        CloseRetainerSell();
                        Transition(BulkState.WaitForDialogsClosed);
                        break;
                    }

                    var newPrice = MarketGuiEventHandler.ComputeUndercutPrice(cheapest, conf);

                    if (newPrice == playerCurrentPrice && playerCurrentPrice > 0)
                    {
                        Log.Information($"[Marketbuddy] Row {currentVisualIndex}: undercut price {newPrice} matches current; skipping.");
                        CloseItemSearchResult();
                        CloseRetainerSell();
                        Transition(BulkState.WaitForDialogsClosed);
                        break;
                    }

                    Log.Information($"[Marketbuddy] Row {currentVisualIndex}: cheapest={cheapest}, current={playerCurrentPrice}, setting={newPrice}.");
                    plugin.MarketGuiEventHandler.SetPrice(newPrice);
                    Transition(BulkState.WaitForDialogsClosed);
                    break;
                }

                case BulkState.WaitForDialogsClosed:
                {
                    var rs = Commons.GetUnitBase("RetainerSell");
                    var isr = Commons.GetUnitBase("ItemSearchResult");
                    var rsOpen = rs != null && rs->IsVisible;
                    var isrOpen = isr != null && isr->IsVisible;

                    if (!rsOpen && !isrOpen)
                    {
                        Transition(BulkState.NextRow);
                    }
                    else if (framesInState > 240) // 4s safety
                    {
                        // Force-close anything still open and move on.
                        if (isrOpen) CloseItemSearchResult();
                        if (rsOpen) CloseRetainerSell();
                        Transition(BulkState.NextRow);
                    }
                    break;
                }

                case BulkState.NextRow:
                {
                    // On entry, compute when the next row may begin. Market-board queries are server
                    // rate-limited — issuing them too fast triggers a "retry later" error.
                    if (framesInState == 1)
                    {
                        var jitter = conf.BulkInterItemDelayJitterMs > 0
                            ? rng.Next(0, conf.BulkInterItemDelayJitterMs + 1)
                            : 0;
                        nextRowReadyTickMs = Environment.TickCount64
                            + Math.Max(0, conf.BulkInterItemDelayMs)
                            + jitter;
                    }
                    if (Environment.TickCount64 < nextRowReadyTickMs) break;
                    currentVisualIndex++;
                    Transition(BulkState.StartRow);
                    break;
                }
            }
        }

        private int ReadCurrentPriceFromRetainerSell(AtkUnitBase* retainerSell)
        {
            if (retainerSell->UldManager.NodeListCount < 16) return 0;
            var priceNode = retainerSell->UldManager.NodeList[15];
            if (priceNode == null) return 0;
            var priceComponent = (AtkComponentNumericInput*)priceNode->GetComponent();
            if (priceComponent == null) return 0;
            var text = Commons.Utf8StringToString(
                ((AtkComponentNumericInputCustom*)priceComponent)->AtkTextNode->NodeText);
            var cleaned = text.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
            return int.TryParse(cleaned, out var p) ? p : 0;
        }

        private static int ReadPriceForListIndex(AtkComponentList* list, int index)
        {
            if (list == null || index < 0 || index >= list->ListLength) return 0;
            var renderer = list->ItemRendererList[index].AtkComponentListItemRenderer;
            if (renderer == null) return 0;
            var componentBase = (AtkComponentBase*)renderer;
            if (componentBase->UldManager.NodeListCount < 11) return 0;
            var priceTextNode = (AtkTextNode*)componentBase->UldManager.NodeList[10];
            if (priceTextNode == null) return 0;
            var priceString = Commons.Utf8StringToString(priceTextNode->NodeText)
                .Replace($"{(char)SeIconChar.Gil}", "")
                .Replace(",", "")
                .Replace(" ", "")
                .Replace(".", "");
            return int.TryParse(priceString, out var p) ? p : 0;
        }

        private static void CloseItemSearchResult()
        {
            var addon = Commons.GetUnitBase("ItemSearchResult");
            if (addon != null && addon->IsVisible) addon->Close(true);
        }

        private static void CloseRetainerSell()
        {
            // Standard FireCallback(0, -1) cancels a confirm-dialog-style addon.
            // RetainerSell follows that convention (Confirm=0, Cancel=-1).
            var addon = Commons.GetUnitBase("RetainerSell");
            if (addon != null && addon->IsVisible)
                FireCallback(addon, -1);
        }

        // Marshals a varargs list of int/uint/bool/string into AtkValues and invokes AtkUnitBase.FireCallback.
        // Buffer is ZERO-allocated so AtkValue.Type starts as Undefined — otherwise SetInt/SetBool/SetUInt
        // call ReleaseManagedMemory on garbage and crash the game.
        // We also assign fields directly rather than using the Set* helpers, which trigger that release path.
        private static void FireCallback(AtkUnitBase* unitBase, params object[] values)
        {
            if (unitBase == null) return;
            var byteCount = (nuint)(values.Length * sizeof(AtkValue));
            var atkValues = (AtkValue*)NativeMemory.AllocZeroed(byteCount);
            try
            {
                for (var i = 0; i < values.Length; i++)
                {
                    switch (values[i])
                    {
                        case bool b:
                            atkValues[i].Type = AtkValueType.Bool;
                            atkValues[i].Byte = (byte)(b ? 1 : 0);
                            break;
                        case int n:
                            atkValues[i].Type = AtkValueType.Int;
                            atkValues[i].Int = n;
                            break;
                        case uint u:
                            atkValues[i].Type = AtkValueType.UInt;
                            atkValues[i].UInt = u;
                            break;
                        case string s:
                            atkValues[i].Type = AtkValueType.String;
                            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
                            Marshal.Copy(bytes, 0, ptr, bytes.Length);
                            ((byte*)ptr)[bytes.Length] = 0;
                            atkValues[i].String.Value = (byte*)ptr;
                            break;
                        default:
                            atkValues[i].Type = AtkValueType.Undefined;
                            break;
                    }
                }
                unitBase->FireCallback((uint)values.Length, atkValues, true);
            }
            finally
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] is string)
                        Marshal.FreeHGlobal((IntPtr)atkValues[i].String.Value);
                }
                NativeMemory.Free(atkValues);
            }
        }
    }
}
