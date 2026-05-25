using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AMMS.Shared.Helpers;

public sealed class SubStageQtyResult
{
    public string current_process_code { get; init; } = "";
    public decimal product_qty { get; init; }
    public int n_up { get; init; }
    public int sheets_base { get; init; }

    public decimal current_stage_waste_qty { get; init; }
    public decimal downstream_waste_qty { get; init; }

    // Số BTP cần đưa vào công đoạn hiện tại.
    public decimal input_qty { get; init; }

    // Số BTP tốt sau khi hoàn thành công đoạn hiện tại.
    public decimal output_qty { get; init; }

    public string unit { get; init; } = "sp";
}

public static class SubProductionQuantityHelper
{
    public static SubStageQtyResult ResolveStageQty(
        string? currentProcessCode,
        IReadOnlyList<string?> routeProcessCodes,
        decimal productQty,
        int? nUp,
        int? explicitSheetsBase,
        string? coatingType)
    {
        var currentCode = Norm(currentProcessCode);

        if (string.IsNullOrWhiteSpace(currentCode))
        {
            return BuildFallback(currentCode, productQty, nUp, explicitSheetsBase);
        }

        var route = (routeProcessCodes ?? Array.Empty<string?>())
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (route.Count == 0)
            route.Add(currentCode);

        var currentIndex = route.FindIndex(x =>
            string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));

        if (currentIndex < 0)
            currentIndex = 0;

        var safeNUp = nUp.HasValue && nUp.Value > 0 ? nUp.Value : 1;
        var safeProductQty = productQty > 0 ? productQty : 1;

        var sheetsBase = explicitSheetsBase.HasValue && explicitSheetsBase.Value > 0
            ? explicitSheetsBase.Value
            : (int)Math.Ceiling(safeProductQty / safeNUp);

        if (sheetsBase <= 0)
            sheetsBase = 1;

        decimal currentStageWaste = 0m;
        decimal downstreamWaste = 0m;

        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = currentIndex; i < route.Count; i++)
        {
            var code = route[i];

            /*
             * Tránh cộng trùng nếu route vô tình có duplicate.
             */
            if (!counted.Add(code))
                continue;

            var waste = ResolveWasteAsProductQty(
                processCode: code,
                productQty: safeProductQty,
                nUp: safeNUp,
                sheetsBase: sheetsBase,
                coatingType: coatingType);

            if (i == currentIndex)
                currentStageWaste += waste;
            else
                downstreamWaste += waste;
        }

        var inputQty = Math.Ceiling(safeProductQty + currentStageWaste + downstreamWaste);
        var outputQty = Math.Ceiling(safeProductQty + downstreamWaste);

        return new SubStageQtyResult
        {
            current_process_code = currentCode,
            product_qty = safeProductQty,
            n_up = safeNUp,
            sheets_base = sheetsBase,
            current_stage_waste_qty = currentStageWaste,
            downstream_waste_qty = downstreamWaste,
            input_qty = inputQty,
            output_qty = outputQty,
            unit = "sp"
        };
    }

    private static SubStageQtyResult BuildFallback(
        string currentCode,
        decimal productQty,
        int? nUp,
        int? explicitSheetsBase)
    {
        var safeNUp = nUp.HasValue && nUp.Value > 0 ? nUp.Value : 1;
        var safeProductQty = productQty > 0 ? productQty : 1;

        var sheetsBase = explicitSheetsBase.HasValue && explicitSheetsBase.Value > 0
            ? explicitSheetsBase.Value
            : (int)Math.Ceiling(safeProductQty / safeNUp);

        return new SubStageQtyResult
        {
            current_process_code = currentCode,
            product_qty = safeProductQty,
            n_up = safeNUp,
            sheets_base = sheetsBase,
            current_stage_waste_qty = 0,
            downstream_waste_qty = 0,
            input_qty = Math.Ceiling(safeProductQty),
            output_qty = Math.Ceiling(safeProductQty),
            unit = "sp"
        };
    }

    private static decimal ResolveWasteAsProductQty(
        string? processCode,
        decimal productQty,
        int nUp,
        int sheetsBase,
        string? coatingType)
    {
        var code = Norm(processCode);

        return code switch
        {
            "PHU" => ResolveCoatingWasteProductQty(sheetsBase, nUp, coatingType),

            "CAN" or "CAN_MANG" => ResolveLaminationWasteProductQty(sheetsBase, nUp),

            "BOI" => ResolveMountingWasteProductQty(sheetsBase, nUp),

            "BE" => ResolveDieCuttingWasteProductQty(sheetsBase, nUp),

            "DAN" => ResolveGluingWasteProductQty(productQty),

            /*
             * DUT hiện tại không có rule hao hụt trong file tính toán bạn gửi.
             */
            "DUT" => 0m,

            /*
             * SUB không cộng lại hao hụt RALO/CAT/IN vì các công đoạn này đã nằm trong bán thành phẩm.
             */
            "RALO" or "CAT" or "IN" => 0m,

            _ => 0m
        };
    }

    private static decimal ResolveDieCuttingWasteProductQty(int sheetsBase, int nUp)
    {
        var wasteSheets = ResolveSheetWasteByThreeRanges(sheetsBase);
        return wasteSheets * nUp;
    }

    private static decimal ResolveMountingWasteProductQty(int sheetsBase, int nUp)
    {
        var wasteSheets = ResolveSheetWasteByThreeRanges(sheetsBase);
        return wasteSheets * nUp;
    }

    private static decimal ResolveCoatingWasteProductQty(
        int sheetsBase,
        int nUp,
        string? coatingType)
    {
        if (!IsOilCoating(coatingType))
            return 0m;

        var wasteSheets = sheetsBase < 10000 ? 20 : 30;
        return wasteSheets * nUp;
    }

    private static decimal ResolveLaminationWasteProductQty(int sheetsBase, int nUp)
    {
        var wasteSheets = sheetsBase < 10000 ? 20 : 30;
        return wasteSheets * nUp;
    }

    private static decimal ResolveGluingWasteProductQty(decimal productQty)
    {
        if (productQty < 100)
            return 10;

        if (productQty < 500)
            return 15;

        if (productQty < 2000)
            return 20;

        return 25;
    }

    private static int ResolveSheetWasteByThreeRanges(int sheetsBase)
    {
        if (sheetsBase < 5000)
            return 20;

        if (sheetsBase < 20000)
            return 30;

        return 40;
    }

    private static bool IsOilCoating(string? coatingType)
    {
        var text = RemoveDiacritics(coatingType)
            .Trim()
            .ToUpperInvariant();

        text = Regex.Replace(text, @"[^A-Z0-9]+", "_");

        return text.Contains("KEO_DAU") ||
               text.Contains("DAU") ||
               text.Contains("OIL");
    }

    public static string Norm(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    private static string RemoveDiacritics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();

        foreach (var ch in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}