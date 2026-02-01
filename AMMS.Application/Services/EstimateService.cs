using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Requests;

namespace AMMS.Application.Services
{
    public class EstimateService : IEstimateService
    {
        private readonly ICostEstimateRepository _estimateRepo;

        public EstimateService(ICostEstimateRepository costEstimateRepository)
        {
            _estimateRepo = costEstimateRepository;
        }

        public async Task UpdateFinalCostAsync(int orderRequestId, decimal? finalCostInput)
        {
            var estimate = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found for this order_request_id");

            if (finalCostInput is null)
                throw new ArgumentException("final_total_cost is required");

            var finalCost = finalCostInput.Value;

            if (finalCost < 0m)
                throw new ArgumentException("final_total_cost must be >= 0");

            estimate.final_total_cost = RoundToThousand(finalCost);

            await _estimateRepo.SaveChangesAsync();
        }
        private static decimal RoundToThousand(decimal value)
        {
            return Math.Round(value / 1000m, MidpointRounding.AwayFromZero) * 1000m;
        }

        public async Task<cost_estimate?> GetEstimateByIdAsync(int estimateId)
        {
            return await _estimateRepo.GetByIdAsync(estimateId);
        }

        public async Task<cost_estimate?> GetEstimateByOrderRequestIdAsync(int orderRequestId)
        {
            return await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId);
        }

        public Task<DepositByRequestResponse?> GetDepositByRequestIdAsync(int requestId, CancellationToken ct = default) 
            => _estimateRepo.GetDepositByRequestIdAsync(requestId, ct);

        public async Task<bool> OrderRequestExistsAsync(int orderRequestId)
        {
            return await _estimateRepo.OrderRequestExistsAsync(orderRequestId);
        }

        public async Task SaveFeCostEstimateAsync(CostEstimateInsertRequest req, CancellationToken ct = default)
        {
            if (req.order_request_id <= 0)
                throw new ArgumentException("order_request_id must be > 0");

            var entity = await _estimateRepo.GetByOrderRequestIdAsync(req.order_request_id);

            bool isNew = false;
            var now = AppTime.NowVnUnspecified();

            if (entity == null)
            {
                isNew = true;
                entity = new cost_estimate
                {
                    order_request_id = req.order_request_id,
                    created_at = ToUnspecified(req.created_at ?? now),
                };

                await _estimateRepo.AddAsync(entity);
            }

            static void SetIfHasValue<T>(T? value, Action<T> setter) where T : struct
            {
                if (value.HasValue) setter(value.Value);
            }

            static void SetIfNotNull(string? value, Action<string> setter)
            {
                if (value != null) setter(value);
            }

            // ----- PAPER -----
            SetIfHasValue(req.paper_cost, v => entity.paper_cost = v);
            SetIfHasValue(req.paper_sheets_used, v => entity.paper_sheets_used = v);
            SetIfHasValue(req.paper_unit_price, v => entity.paper_unit_price = v);

            // ----- INK -----
            SetIfHasValue(req.ink_cost, v => entity.ink_cost = v);
            SetIfHasValue(req.ink_weight_kg, v => entity.ink_weight_kg = v);
            SetIfHasValue(req.ink_rate_per_m2, v => entity.ink_rate_per_m2 = v);

            // ----- COATING GLUE -----
            SetIfHasValue(req.coating_glue_cost, v => entity.coating_glue_cost = v);
            SetIfHasValue(req.coating_glue_weight_kg, v => entity.coating_glue_weight_kg = v);
            SetIfHasValue(req.coating_glue_rate_per_m2, v => entity.coating_glue_rate_per_m2 = v);
            SetIfNotNull(req.coating_type, v => entity.coating_type = v);

            // ----- MOUNTING GLUE -----
            SetIfHasValue(req.mounting_glue_cost, v => entity.mounting_glue_cost = v);
            SetIfHasValue(req.mounting_glue_weight_kg, v => entity.mounting_glue_weight_kg = v);
            SetIfHasValue(req.mounting_glue_rate_per_m2, v => entity.mounting_glue_rate_per_m2 = v);

            // ----- LAMINATION -----
            SetIfHasValue(req.lamination_cost, v => entity.lamination_cost = v);
            SetIfHasValue(req.lamination_weight_kg, v => entity.lamination_weight_kg = v);
            SetIfHasValue(req.lamination_rate_per_m2, v => entity.lamination_rate_per_m2 = v);

            // ----- MATERIAL / OVERHEAD -----
            SetIfHasValue(req.material_cost, v => entity.material_cost = v);
            SetIfHasValue(req.overhead_percent, v => entity.overhead_percent = v);
            SetIfHasValue(req.overhead_cost, v => entity.overhead_cost = v);
            SetIfHasValue(req.base_cost, v => entity.base_cost = v);

            // ----- RUSH -----
            if (req.is_rush.HasValue) entity.is_rush = req.is_rush.Value;
            SetIfHasValue(req.rush_percent, v => entity.rush_percent = v);
            SetIfHasValue(req.rush_amount, v => entity.rush_amount = v);
            SetIfHasValue(req.days_early, v => entity.days_early = v);

            // ----- SUBTOTAL / DISCOUNT / FINAL -----
            SetIfHasValue(req.subtotal, v => entity.subtotal = v);
            SetIfHasValue(req.discount_percent, v => entity.discount_percent = v);
            SetIfHasValue(req.discount_amount, v => entity.discount_amount = v);
            SetIfHasValue(req.final_total_cost, v => entity.final_total_cost = v);

            // ----- DATES -----
            if (req.estimated_finish_date.HasValue)
                entity.estimated_finish_date = ToUnspecified(req.estimated_finish_date.Value);

            if (req.desired_delivery_date.HasValue)
                entity.desired_delivery_date = ToUnspecified(req.desired_delivery_date.Value);

            if (req.created_at.HasValue)
                entity.created_at = ToUnspecified(req.created_at.Value);
            else if (isNew)
                entity.created_at = ToUnspecified(now);

            // ----- SHEETS / AREA -----
            SetIfHasValue(req.sheets_required, v => entity.sheets_required = v);
            SetIfHasValue(req.sheets_waste, v => entity.sheets_waste = v);
            SetIfHasValue(req.sheets_total, v => entity.sheets_total = v);
            SetIfHasValue(req.n_up, v => entity.n_up = v);
            SetIfHasValue(req.total_area_m2, v => entity.total_area_m2 = v);

            // ----- DESIGN -----
            SetIfHasValue(req.design_cost, v => entity.design_cost = v);
            SetIfNotNull(req.cost_note, v => entity.cost_note = v);

            // ----- PROCESS COSTS -----
            // ----- PROCESS COSTS -----
            if (req.process_costs != null)
            {
                entity.process_costs.Clear();

                // dùng “tờ” làm base cho IN/PHU/CAN
                int sheetsBase = entity.sheets_required > 0
                    ? entity.sheets_required
                    : (entity.sheets_total > 0 ? entity.sheets_total : 1);

                decimal totalArea = entity.total_area_m2 > 0 ? entity.total_area_m2 : 0m;
                decimal areaPerSheet = (sheetsBase > 0 && totalArea > 0) ? (totalArea / sheetsBase) : 0m;

                foreach (var p in req.process_costs)
                {
                    var pcode = (p.process_code ?? "").Trim().ToUpperInvariant();

                    decimal qty = p.quantity ?? 0m;
                    decimal unitPrice = p.unit_price ?? 0m;

                    // total_cost ưu tiên lấy từ FE nếu có, không thì tự tính
                    decimal totalCost = (p.total_cost.HasValue && p.total_cost.Value > 0)
                        ? p.total_cost.Value
                        : (qty * unitPrice);

                    var unit = (p.unit ?? "").Trim();

                    // ✅ Normalize IN/PHU/CAN từ m2 -> tờ
                    if (pcode is "IN" or "PHU" or "CAN" or "CAN_MANG")
                    {
                        var qtySheets = (decimal)sheetsBase;
                        var unitPriceSheet = sheetsBase > 0 ? (totalCost / qtySheets) : 0m;

                        if (totalCost <= 0 && unitPrice > 0 && totalArea > 0)
                        {
                            totalCost = unitPrice * totalArea;
                            unitPriceSheet = sheetsBase > 0 ? (totalCost / qtySheets) : 0m;
                        }

                        entity.process_costs.Add(new cost_estimate_process
                        {
                            process_code = p.process_code,
                            process_name = p.process_name ?? p.process_code,
                            quantity = qtySheets,       
                            unit = "tờ",                
                            unit_price = unitPriceSheet,
                            total_cost = totalCost,
                            note = p.note,
                            created_at = AppTime.NowVnUnspecified()
                        });

                        continue;
                    }

                    entity.process_costs.Add(new cost_estimate_process
                    {
                        process_code = p.process_code,
                        process_name = p.process_name ?? p.process_code,
                        quantity = qty,
                        unit = string.IsNullOrWhiteSpace(unit) ? "" : unit,
                        unit_price = unitPrice,
                        total_cost = totalCost,
                        note = p.note,
                        created_at = AppTime.NowVnUnspecified()
                    });
                }
            }

            await _estimateRepo.SaveChangesAsync();
        }
        public static DateTime ToUnspecified(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                return dt;

            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }
    }
}