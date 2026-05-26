using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AMMS.Application.Helpers;

public sealed class SubProductStageSignature
{
    public int? cost_estimate_id { get; set; }

    public string? paper_material_code { get; set; }

    public string? wave_material_code { get; set; }

    public string? coating_material_code { get; set; }

    public string? lamination_material_code { get; set; }

    public string material_signature { get; set; } = "";

    public decimal unit_cost_to_stage { get; set; }

    public decimal total_cost_to_stage { get; set; }
}

public sealed class SubProductMethodCostOptionDto
{
    public string method { get; set; } = "";

    public bool is_available { get; set; }

    public int? sub_product_id { get; set; }

    public int sub_available_qty { get; set; }

    public int sub_used_qty { get; set; }

    public int nvl_qty { get; set; }

    public decimal unit_cost { get; set; }

    public decimal total_cost { get; set; }

    public decimal? saving_vs_nvl_unit { get; set; }

    public decimal? saving_vs_nvl_total { get; set; }

    public string? reason { get; set; }
}

public static class SubProductCompatibilityHelper
{
    private static readonly string[] FullRouteOrder =
    {
        "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN"
    };


    public static string Norm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var s = RemoveDiacritics(value)
            .Trim()
            .ToUpperInvariant();

        s = s.Replace("Đ", "D");
        s = s.Replace("-", "_").Replace(" ", "_");
        s = Regex.Replace(s, @"[^A-Z0-9_]+", "_");
        s = Regex.Replace(s, @"_+", "_").Trim('_');

        return s switch
        {
            "KEO_NUOC" => "KEO_PHU_NUOC",
            "KEO_PHU_NUOC" => "KEO_PHU_NUOC",

            "KEO_DAU" => "KEO_PHU_DAU",
            "KEO_PHU_DAU" => "KEO_PHU_DAU",

            "UV" => "KEO_PHU_UV",
            "KEO_UV" => "KEO_PHU_UV",
            "PHU_UV" => "KEO_PHU_UV",
            "KEO_PHU_UV" => "KEO_PHU_UV",

            "MANG_12_MIC" => "MANG_12MIC",

            "MOUNTING_GLUE" => "KEO_BOI",

            _ => s
        };
    }

    public static bool IsMaterialMatchedForProductionSub(
    sub_product sub,
    SubProductStageSignature expected)
    {
        if (!Same(Norm(sub.paper_material_code), Norm(expected.paper_material_code)))
            return false;

        if (!Same(Norm(sub.wave_material_code), Norm(expected.wave_material_code)))
            return false;

        if (!Same(Norm(sub.coating_material_code), Norm(expected.coating_material_code)))
            return false;

        if (!Same(Norm(sub.lamination_material_code), Norm(expected.lamination_material_code)))
            return false;

        /*
         * Không check unit_cost_to_stage.
         * Logic này chỉ dùng để xét sub_product có hợp lệ cho order mới hay không.
         */
        return true;
    }

    public static List<string> ParseRoute(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new List<string>();

        return csv
            .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(RouteIndex)
            .ToList();
    }

    public static bool IsSubPathUsableForOrderRoute(string? subProductProcess, string? orderRouteCsv)
    {
        var subCodes = ParseRoute(subProductProcess);
        var orderCodes = ParseRoute(orderRouteCsv);

        if (subCodes.Count == 0 || orderCodes.Count == 0)
            return false;

        foreach (var code in subCodes)
        {
            if (!orderCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        var subLastIndex = subCodes
            .Select(x => orderCodes.FindIndex(y => string.Equals(y, x, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x >= 0)
            .DefaultIfEmpty(-1)
            .Max();

        var expectedPrefix = orderCodes.Take(subLastIndex + 1).ToList();

        return expectedPrefix.SequenceEqual(subCodes, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<cost_estimate?> LoadAcceptedEstimateByOrderIdAsync(
        AppDbContext db,
        int orderId,
        CancellationToken ct)
    {
        var req = await db.order_requests
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .OrderByDescending(x => x.order_request_id)
            .FirstOrDefaultAsync(ct);

        if (req == null)
            return null;

        cost_estimate? est = null;

        if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
        {
            est = await db.cost_estimates
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.estimate_id == req.accepted_estimate_id.Value &&
                    x.order_request_id == req.order_request_id,
                    ct);
        }

        est ??= await db.cost_estimates
            .AsNoTracking()
            .Where(x => x.order_request_id == req.order_request_id)
            .OrderByDescending(x => x.is_active)
            .ThenByDescending(x => x.estimate_id)
            .FirstOrDefaultAsync(ct);

        return est;
    }

    public static async Task<int> ResolveOrderQtyAsync(
        AppDbContext db,
        int orderId,
        CancellationToken ct)
    {
        var reqQty = await db.order_requests
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .OrderByDescending(x => x.order_request_id)
            .Select(x => x.quantity)
            .FirstOrDefaultAsync(ct);

        if (reqQty.HasValue && reqQty.Value > 0)
            return reqQty.Value;

        var itemQty = await db.order_items
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .OrderBy(x => x.item_id)
            .Select(x => x.quantity)
            .FirstOrDefaultAsync(ct);

        return itemQty > 0 ? itemQty : 1;
    }

    public static async Task<SubProductStageSignature?> BuildSignatureForOrderStageAsync(
    AppDbContext db,
    int orderId,
    string? processPath,
    int stageQuantity,
    CancellationToken ct)
    {
        var est = await LoadAcceptedEstimateByOrderIdAsync(db, orderId, ct);
        if (est == null)
            return null;

        var orderQty = await ResolveOrderQtyAsync(db, orderId, ct);
        var codes = ParseRoute(processPath);

        var unitCost = await CalculateUnitCostToStageAsync(
            db,
            est.estimate_id,
            codes,
            orderQty,
            ct);

        var paperCode = NormalizeMaterialCode(
            !string.IsNullOrWhiteSpace(est.paper_code)
                ? est.paper_code
                : est.paper_alternative);

        var waveCode = codes.Any(x => x == "BOI")
            ? NormalizeMaterialCode(EstimateMaterialAlternativeHelper.ResolveWaveType(
                est.wave_alternative,
                est.wave_type))
            : null;

        var coatingCode = codes.Any(x => x == "PHU")
            ? ResolveCoatingMaterialCode(est)
            : null;

        var laminationCode = codes.Any(x => x == "CAN" || x == "CAN_MANG")
            ? NormalizeMaterialCode(
                !string.IsNullOrWhiteSpace(est.lamination_material_code)
                    ? est.lamination_material_code
                    : !string.IsNullOrWhiteSpace(est.lamination_material_name)
                        ? est.lamination_material_name
                        : est.lamination_material_id?.ToString())
            : null;

        var sig = BuildMaterialSignature(
            paperCode,
            waveCode,
            coatingCode,
            laminationCode);

        return new SubProductStageSignature
        {
            cost_estimate_id = est.estimate_id,
            paper_material_code = NullIfEmpty(paperCode),
            wave_material_code = NullIfEmpty(waveCode),
            coating_material_code = NullIfEmpty(coatingCode),
            lamination_material_code = NullIfEmpty(laminationCode),
            material_signature = sig,
            unit_cost_to_stage = Math.Round(unitCost, 4),
            total_cost_to_stage = Math.Round(unitCost * stageQuantity, 2)
        };
    }

    private static string? ResolveCoatingMaterialCode(cost_estimate est)
    {
        if (!string.IsNullOrWhiteSpace(est.coating_material_code))
            return NormalizeMaterialCode(est.coating_material_code);

        return NormalizeMaterialCode(est.coating_type);
    }

    public static string BuildMaterialSignature(
        string? paperCode,
        string? waveCode,
        string? coatingCode,
        string? laminationCode)
    {
        return
            $"PAPER={NormalizeMaterialCode(paperCode) ?? "NONE"}|" +
            $"WAVE={NormalizeMaterialCode(waveCode) ?? "NONE"}|" +
            $"COATING={NormalizeMaterialCode(coatingCode) ?? "NONE"}|" +
            $"LAMINATION={NormalizeMaterialCode(laminationCode) ?? "NONE"}";
    }

    public static bool IsMaterialMatched(sub_product sub, SubProductStageSignature expected)
    {
        if (!Same(Norm(sub.paper_material_code), Norm(expected.paper_material_code)))
            return false;

        if (!Same(Norm(sub.wave_material_code), Norm(expected.wave_material_code)))
            return false;

        if (!Same(Norm(sub.coating_material_code), Norm(expected.coating_material_code)))
            return false;

        if (!Same(Norm(sub.lamination_material_code), Norm(expected.lamination_material_code)))
            return false;

        return true;
    }

    public static bool IsMaterialAndCostMatched(
        sub_product sub,
        SubProductStageSignature expected)
    {
        return IsMaterialMatched(sub, expected);
    }

    private static bool SameMaterial(string? a, string? b)
    {
        var aa = NormalizeMaterialCode(a) ?? "NONE";
        var bb = NormalizeMaterialCode(b) ?? "NONE";

        return string.Equals(aa, bb, StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeMaterialCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = RemoveDiacritics(value)
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");

        s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

        return s switch
        {
            "" => null,
            "NONE" => null,
            "NULL" => null,

            "KEO_NUOC" => "KEO_PHU_NUOC",
            "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
            "KEO_DAU" => "KEO_PHU_DAU",
            "KEO_PHU_DAU" => "KEO_PHU_DAU",
            "UV" => "KEO_PHU_UV",
            "KEO_UV" => "KEO_PHU_UV",
            "KEO_PHU_UV" => "KEO_PHU_UV",

            "MANG_12_MIC" => "MANG_12MIC",
            "MANG_12MIC" => "MANG_12MIC",

            _ => s
        };
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);

        var chars = normalized
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                         != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray();

        return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
    }

    public static async Task<decimal> CalculateUnitCostToStageAsync(
        AppDbContext db,
        int estimateId,
        IReadOnlyList<string> processPathCodes,
        int orderQty,
        CancellationToken ct)
    {
        if (processPathCodes == null || processPathCodes.Count == 0)
            return 0m;

        orderQty = orderQty > 0 ? orderQty : 1;

        var costCodes = processPathCodes
            .Select(NormCostProcessCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = await db.cost_estimate_processes
            .AsNoTracking()
            .Where(x => x.estimate_id == estimateId)
            .Where(x => costCodes.Contains(x.process_code.Trim().ToUpper()))
            .SumAsync(x => x.total_cost, ct);

        return Math.Round(total / orderQty, 4);
    }

    public static async Task<decimal> CalculateUnitCostForFullRouteAsync(
        AppDbContext db,
        int estimateId,
        string? orderRouteCsv,
        int orderQty,
        CancellationToken ct)
    {
        var route = ParseRoute(orderRouteCsv);
        return await CalculateUnitCostToStageAsync(
            db,
            estimateId,
            route,
            orderQty,
            ct);
    }

    public static string BuildImportMergeKey(sub_product x)
    {
        return string.Join("|", new[]
        {
        $"PT={x.product_type_id}",
        $"W={x.width?.ToString() ?? "NULL"}",
        $"L={x.length?.ToString() ?? "NULL"}",
        $"PATH={string.Join(",", ParseRoute(x.product_process))}",
        $"MAT={x.material_signature ?? BuildMaterialSignature(x.paper_material_code, x.wave_material_code, x.coating_material_code, x.lamination_material_code)}"
    });
    }

    private static string NormCostProcessCode(string? code)
    {
        var c = Norm(code);

        return c switch
        {
            "CAN" => "CAN",
            _ => c
        };
    }

    private static int RouteIndex(string code)
    {
        var idx = Array.FindIndex(
            FullRouteOrder,
            x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

        return idx < 0 ? 999 : idx;
    }

    private static bool Same(string? a, string? b)
    {
        return string.Equals(
            a ?? "",
            b ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

}