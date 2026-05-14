using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class QuoteRepository : IQuoteRepository
    {
        private readonly AppDbContext _context;

        public QuoteRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(quote entity)
        {
            await _context.quotes.AddAsync(entity);
        }
        public async Task<quote?> GetByIdAsync(int id)
        {
            return await _context.quotes.FirstOrDefaultAsync(x => x.quote_id == id);
        }

        public async Task SaveChangesAsync()
        {
            await _context.SaveChangesAsync();
        }

        public async Task<QuoteEmailComparePreviewResponse> BuildPreviewAsync(int requestId, CancellationToken ct = default)
        {
            if (requestId <= 0) throw new ArgumentException("requestId must be > 0");

            var req = await _context.order_requests.AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == requestId, ct)
                ?? throw new Exception($"OrderRequest not found id={requestId}");

            var ests = await _context.cost_estimates.AsNoTracking()
                .Include(x => x.process_costs)
                .Where(x => x.order_request_id == requestId && x.is_active)
                .OrderByDescending(x => x.estimate_id)
                .Take(2)
                .ToListAsync(ct);

            if (ests.Count == 0)
                throw new Exception("No active estimates found for this request");

            var estIds = ests.Select(e => e.estimate_id).ToList();

            var quoteMap = await _context.quotes.AsNoTracking()
                .Where(x => x.order_request_id == requestId && estIds.Contains(x.estimate_id))
                .GroupBy(x => x.estimate_id)
                .Select(g => g.OrderByDescending(x => x.quote_id).First())
                .ToDictionaryAsync(x => x.estimate_id, x => x, ct);

            var compare = new QuoteEmailComparePreviewResponse
            {
                order_request_id = req.order_request_id,
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                detail_address = req.detail_address,
                delivery_date = req.delivery_date,
                order_request_date = req.order_request_date,
                product_name = req.product_name,
                quantity = req.quantity ?? 0,
                is_send_design = req.is_send_design ?? false,
                delivery_date_change_reason = req.delivery_date_change_reason,
            };

            foreach (var est in ests)
            {
                var paperName = string.IsNullOrWhiteSpace(est.paper_name) ? "N/A" : est.paper_name;
                var coatingType = string.IsNullOrWhiteSpace(est.coating_type) ? "N/A" : est.coating_type;
                var waveType = string.IsNullOrWhiteSpace(est.wave_type) ? "N/A" : est.wave_type;

                var designType = (req.is_send_design == true)
                    ? "Tự gửi file thiết kế"
                    : "Doanh nghiệp";

                var materialCost = est.paper_cost + est.ink_cost + est.coating_glue_cost + est.mounting_glue_cost + est.lamination_cost;

                var laborCost = est.process_costs != null
                    ? est.process_costs.Where(p => p.estimate_id == est.estimate_id).Sum(p => p.total_cost)
                    : 0m;

                var expiredAt = est.created_at.AddHours(24);
                var productionProcessText = BuildProductionProcessText(est);
                quoteMap.TryGetValue(est.estimate_id, out var q);

                compare.quotes.Add(new QuoteEmailPreviewResponse
                {
                    order_request_id = req.order_request_id,
                    estimate_id = est.estimate_id,
                    quote_id = q?.quote_id ?? 0,
                    customer_name = req.customer_name,
                    customer_phone = req.customer_phone,
                    customer_email = req.customer_email,
                    detail_address = req.detail_address,
                    delivery_date = req.delivery_date,
                    order_request_date = req.order_request_date,
                    product_name = req.product_name,
                    quantity = req.quantity ?? 0,

                    paper_name = paperName,
                    coating_type = coatingType,
                    wave_type = waveType,
                    is_send_design = req.is_send_design ?? false,

                    quote_created_at = est.created_at,
                    quote_expired_at = expiredAt,

                    material_cost = materialCost,
                    labor_cost = laborCost,
                    other_fees = est.design_cost,
                    rush_amount = est.rush_amount,
                    subtotal = est.subtotal,
                    final_total = est.final_total_cost,
                    discount_percent = est.discount_percent,
                    discount_amount = est.discount_amount,
                    deposit = est.deposit_amount,

                    design_type_text = designType,
                    production_process_text = productionProcessText,

                    delivery_text = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A",
                    request_date_text = req.order_request_date?.ToString("dd/MM/yyyy HH:mm") ?? "N/A",
                    quote_expired_at_text = expiredAt.ToString("dd/MM/yyyy HH:mm"),
                    consultant_contract_path = est.consultant_contract_path,
                    customer_signed_contract_path = est.customer_signed_contract_path,
                    order_detail_url = null,
                    is_customer_copy = true,
                    email_html = null
                });
            }

            return compare;
        }

        private static string BuildProductionProcessText(cost_estimate est)
        {
            var codes = new List<string>();

            if (!string.IsNullOrWhiteSpace(est.production_processes))
            {
                codes = est.production_processes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            else if (est.process_costs is { Count: > 0 })
            {
                codes = est.process_costs
                    .Select(p => p.process_code)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .ToList();
            }

            if (codes.Count == 0)
                return "Không có / Không áp dụng";

            return string.Join(", ", codes.Select(MapProcessCode));
        }

        private static string MapProcessCode(string code) => code.Trim().ToUpperInvariant() switch
        {
            "IN" => "In",
            "RALO" => "Ra lô",
            "CAT" => "Cắt",
            "CAN_MANG" => "Cán",
            "CAN" => "Cán",
            "BOI" => "Bồi",
            "PHU" => "Phủ",
            "DUT" => "Dứt",
            "DAN" => "Dán",
            "BE" => "Bế",
            _ => code
        };
    }
}
