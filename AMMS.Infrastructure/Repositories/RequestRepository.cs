using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class RequestRepository : IRequestRepository
    {
        private readonly AppDbContext _db;
        private readonly IMachineRepository _machineRepository;
        public RequestRepository(AppDbContext db, IMachineRepository machineRepository)
        {
            _db = db;
            _machineRepository = machineRepository;
        }

        public async Task<order_request?> GetByIdAsync(int id)
        {
            return await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == id);
        }

        public async Task<order_request?> GetByOderIdAsync(int id)
        {
            return await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_id == id);
        }
        public async Task<RequestWithCostDto?> GetByIdWithCostAsync(int id, int? consultantUserId = null)
        {
            var request = await _db.order_requests.AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == id);

            var payment = await _db.payments.AsNoTracking()
                .FirstOrDefaultAsync(p => p.order_request_id == id);

            if (request == null)
                return null;
            var assignedFullName = request.assigned_consultant.HasValue
    ? await _db.users
        .AsNoTracking()
        .Where(u => u.user_id == request.assigned_consultant.Value)
        .Select(u => u.full_name)
        .FirstOrDefaultAsync()
    : null;
            var estimate = await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync();

            var displayPaperCode = estimate == null
                ? null
                : EstimateMaterialAlternativeHelper.ResolvePaperCode(
                    estimate.paper_alternative,
                    estimate.paper_code);

            var displayWaveType = estimate == null
                ? null
                : EstimateMaterialAlternativeHelper.ResolveWaveType(
                    estimate.wave_alternative,
                    estimate.wave_type);

            string? displayPaperName = estimate?.paper_name;

            if (!string.IsNullOrWhiteSpace(displayPaperCode))
            {
                displayPaperName = await _db.materials
                    .AsNoTracking()
                    .Where(x => x.code == displayPaperCode)
                    .Select(x => x.name)
                    .FirstOrDefaultAsync() ?? estimate?.paper_name ?? displayPaperCode;
            }

            return new RequestWithCostDto
            {
                order_request_id = request.order_request_id,
                customer_name = request.customer_name,
                customer_phone = request.customer_phone,
                customer_email = request.customer_email,
                delivery_date = request.delivery_date,
                delivery_date_change_reason = request.delivery_date_change_reason,
                product_name = request.product_name,
                quantity = request.quantity,
                description = request.description,
                design_file_path = request.design_file_path,
                order_request_date = request.order_request_date,
                detail_address = request.detail_address,
                process_status = request.process_status,
                product_type = request.product_type,
                number_of_plates = request.number_of_plates,
                assign_name = string.IsNullOrWhiteSpace(assignedFullName) ? null : assignedFullName.Trim(),
                paper_code = displayPaperCode,
                paper_name = displayPaperName,
                wave_type = displayWaveType,
                order_code = payment.order_code,
                ink_type_names = estimate?.ink_type_names,

                paper_alternative = estimate?.paper_alternative,
                wave_alternative = estimate?.wave_alternative,
                alternative_material_reason = estimate?.alternative_material_reason,
                coating_type = estimate?.coating_type,

                order_id = request.order_id,
                quote_id = request.quote_id,
                product_length_mm = request.product_length_mm,
                product_width_mm = request.product_width_mm,
                product_height_mm = request.product_height_mm,
                glue_tab_mm = request.glue_tab_mm,
                bleed_mm = request.bleed_mm,
                is_one_side_box = request.is_one_side_box,
                print_width_mm = request.print_width_mm,
                print_length_mm = request.print_length_mm,
                is_send_design = request.is_send_design,
                reason = request.reason,
                final_total_cost = estimate?.final_total_cost,
                deposit_amount = estimate?.deposit_amount,
                verified_at = request.verified_at,
                quote_expires_at = request.quote_expires_at,
                message_to_customer = request.message_to_customer,
                production_processes = estimate?.production_processes,
                preliminary_estimated_price = request.preliminary_estimated_price,
                consultant_contract_path = estimate?.consultant_contract_path,
                customer_signed_contract_path = estimate?.customer_signed_contract_path,
                actual_consultant_user_id = request.actual_consultant_user_id,
                deposit_receipt_path = request.deposit_receipt_path,
                remaining_receipt_path = request.remaining_receipt_path,

                lamination_material_id = estimate?.lamination_material_id,
                lamination_material_code = estimate?.lamination_material_code,
                lamination_material_name = estimate?.lamination_material_name,
            };
        }

        public Task UpdateAsync(order_request entity)
        {
            _db.order_requests.Update(entity);
            return Task.CompletedTask;
        }

        public async Task<int> SaveChangesAsync()
        {
            return await _db.SaveChangesAsync();
        }

        public async Task AddAsync(order_request entity)
        {
            await _db.order_requests.AddAsync(entity);
        }

        public async Task CancelAsync(int id, CancellationToken ct = default)
        {
            var entity = await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == id, ct);

            if (entity == null) return;

            if (entity.order_id != null)
                throw new InvalidOperationException("This request is already linked to an order, cannot cancel.");

            entity.process_status = "Cancel";
            _db.order_requests.Update(entity);
        }

        public async Task MarkProcessStatusFinishedByOrderAsync(int orderId, int? quoteId, CancellationToken ct = default)
        {
            var requests = await _db.order_requests
                .AsTracking()
                .Where(x =>
                    (x.order_id == orderId || (quoteId.HasValue && x.quote_id == quoteId.Value)) &&
                    !string.Equals(x.process_status, "Finished"))
                .ToListAsync(ct);

            foreach (var req in requests)
            {
                req.process_status = "Finished";
            }
        }

        public async Task MarkProcessStatusImportingByOrderAsync(int orderId, int? quoteId, CancellationToken ct = default)
        {
            var req = await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);
            var order = await _db.orders.FirstOrDefaultAsync(o => o.order_id == orderId, ct);
            if (req != null && order != null)
            {
                order.status = "Importing";
                req.process_status = "Importing";
                await _db.SaveChangesAsync(ct);
            }
        }

        public Task<int> CountAsync()
        {
            return _db.order_requests.AsNoTracking().CountAsync();
        }

        public Task<List<RequestPagedDto>> GetPagedAsync(int skip, int takePlusOne, int? consultantUserId = null)
        {
            var requestQuery = _db.order_requests.AsNoTracking();

            return (
                from r in requestQuery
                join ce in _db.cost_estimates.AsNoTracking().Where(x=>x.is_active == true)
                    on r.order_request_id equals ce.order_request_id into ceJoin
                from ce in ceJoin
                    .OrderByDescending(x => x.estimate_id)
                    .Take(1)
                    .DefaultIfEmpty()
                orderby r.order_request_date descending
                select new RequestPagedDto
                {
                    order_request_id = r.order_request_id,
                    customer_name = r.customer_name ?? "",
                    customer_phone = r.customer_phone ?? "",
                    customer_email = r.customer_email,
                    delivery_date = r.delivery_date,
                    delivery_date_change_reason = r.delivery_date_change_reason,
                    product_name = r.product_name,
                    quantity = r.quantity,
                    description = r.description,
                    design_file_path = r.design_file_path,
                    order_request_date = r.order_request_date,
                    detail_address = r.detail_address,
                    process_status = r.process_status,
                    product_type = r.product_type,
                    number_of_plates = r.number_of_plates,
                    order_id = r.order_id,
                    quote_id = r.quote_id,
                    product_length_mm = r.product_length_mm,
                    product_width_mm = r.product_width_mm,
                    product_height_mm = r.product_height_mm,
                    glue_tab_mm = r.glue_tab_mm,
                    bleed_mm = r.bleed_mm,
                    is_one_side_box = r.is_one_side_box,
                    print_width_mm = r.print_width_mm,
                    print_length_mm = r.print_length_mm,
                    is_send_design = r.is_send_design,
                    reason = r.reason,
                    note = r.note,
                    accepted_estimate_id = r.accepted_estimate_id,
                    consultant_note = r.consultant_note,
                    verified_at = r.verified_at,
                    quote_expire_at = r.quote_expires_at,
                    message_to_customer = r.message_to_customer,
                    preliminary_estimated_price = r.preliminary_estimated_price,
                    assigned_consultant = r.assigned_consultant,
                    assigned_at = r.assigned_at,
                    delivery_note = r.delivery_note,
                    print_ready_file = r.print_ready_file,
                    estimate_finish_date = r.estimate_finish_date,
                    estimate_id = ce != null ? ce.estimate_id : null,
                    base_cost = ce != null ? ce.base_cost : null,
                    is_rush = ce != null ? ce.is_rush : null,
                    rush_percent = ce != null ? ce.rush_percent : null,
                    rush_amount = ce != null ? ce.rush_amount : null,
                    estimated_finish_date = ce != null ? ce.estimated_finish_date : null,
                    desired_delivery_date = ce != null ? ce.desired_delivery_date : null,
                    estimate_created_at = ce != null ? ce.created_at : null,
                    paper_cost = ce != null ? ce.paper_cost : null,
                    ink_cost = ce != null ? ce.ink_cost : null,
                    coating_glue_cost = ce != null ? ce.coating_glue_cost : null,
                    mounting_glue_cost = ce != null ? ce.mounting_glue_cost : null,
                    lamination_cost = ce != null ? ce.lamination_cost : null,
                    material_cost = ce != null ? ce.material_cost : null,
                    sheets_required = ce != null ? ce.sheets_required : null,
                    sheets_waste = ce != null ? ce.sheets_waste : null,
                    sheets_total = ce != null ? ce.sheets_total : null,
                    total_area_m2 = ce != null ? ce.total_area_m2 : null,
                    final_total_cost = ce != null ? ce.final_total_cost : null,
                    cost_note = ce != null ? ce.cost_note : null,
                    paper_sheets_used = ce != null ? ce.paper_sheets_used : null,
                    paper_unit_price = ce != null ? ce.paper_unit_price : null,
                    ink_weight_kg = ce != null ? ce.ink_weight_kg : null,
                    ink_rate_per_m2 = ce != null ? ce.ink_rate_per_m2 : null,
                    coating_glue_weight_kg = ce != null ? ce.coating_glue_weight_kg : null,
                    coating_glue_rate_per_m2 = ce != null ? ce.coating_glue_rate_per_m2 : null,
                    coating_type = ce != null ? ce.coating_type : null,
                    mounting_glue_weight_kg = ce != null ? ce.mounting_glue_weight_kg : null,
                    mounting_glue_rate_per_m2 = ce != null ? ce.mounting_glue_rate_per_m2 : null,
                    lamination_weight_kg = ce != null ? ce.lamination_weight_kg : null,
                    lamination_rate_per_m2 = ce != null ? ce.lamination_rate_per_m2 : null,
                    days_early = ce != null ? ce.days_early : null,
                    subtotal = ce != null ? ce.subtotal : null,
                    discount_percent = ce != null ? ce.discount_percent : null,
                    discount_amount = ce != null ? ce.discount_amount : null,
                    deposit_amount = ce != null ? ce.deposit_amount : null,
                    design_cost = ce != null ? ce.design_cost : null,
                    n_up = ce != null ? ce.n_up : null,
                    is_active = ce != null ? ce.is_active : null,
                    paper_code = ce != null ? ce.paper_code : null,
                    paper_name = ce != null ? ce.paper_name : null,
                    wave_type = ce != null ? ce.wave_type : null,
                    production_processes = ce != null ? ce.production_processes : null,
                    previous_estimate_id = ce != null ? ce.previous_estimate_id : null,
                    consultant_contract_path = ce != null ? ce.consultant_contract_path : null,
                    customer_signed_contract_path = ce != null ? ce.customer_signed_contract_path : null,
                    wave_sheets_used = ce != null ? ce.wave_sheets_used : null,
                    paper_alternative = ce != null ? ce.paper_alternative : null,
                    wave_alternative = ce != null ? ce.wave_alternative : null,
                    assign_name = _db.users.AsNoTracking().Where(u => u.user_id == r.assigned_consultant).Select(u => u.full_name).FirstOrDefault(),
                    lamination_material_id = ce != null ? ce.lamination_material_id : null,
                    lamination_material_code = ce != null ? ce.lamination_material_code : null,
                    lamination_material_name = ce != null ? ce.lamination_material_name : null,
                    require_remaining_before_delivery =_db.estimate_config.AsNoTracking().Where(c =>c.config_group == "deliveryPayment" &&
            c.config_key == "require_remaining_before_delivery")
        .OrderByDescending(c => c.updated_at)
        .Select(c => c.value_bool)
        .FirstOrDefault() ?? true,
                }
            )
            .Skip(skip)
            .Take(takePlusOne)
            .ToListAsync();
        }

        public async Task<RequestPagedDto?> GetByOrderIdAsync(int orderId)
        {
            var requestQuery = _db.order_requests.AsNoTracking();
            var requireRemainingBeforeDelivery = await _db.estimate_config
    .AsNoTracking()
    .Where(x =>
        x.config_group == "deliveryPayment" &&
        x.config_key == "require_remaining_before_delivery")
    .OrderByDescending(x => x.updated_at)
    .Select(x => x.value_bool)
    .FirstOrDefaultAsync() ?? true;
            return await (
                from r in requestQuery
                where r.order_id == orderId

                join ce in _db.cost_estimates.AsNoTracking()
                    on r.order_request_id equals ce.order_request_id into ceJoin

                from ce in ceJoin
                    .OrderByDescending(x => x.estimate_id)
                    .Take(1)
                    .DefaultIfEmpty()

                select new RequestPagedDto
                {
                    order_request_id = r.order_request_id,
                    actual_consultant_user_id = r.actual_consultant_user_id,
                    customer_name = r.customer_name ?? "",
                    customer_phone = r.customer_phone ?? "",
                    customer_email = r.customer_email,
                    delivery_date = r.delivery_date,
                    product_name = r.product_name,
                    quantity = r.quantity,
                    description = r.description,
                    design_file_path = r.design_file_path,
                    order_request_date = r.order_request_date,
                    detail_address = r.detail_address,
                    process_status = r.process_status,
                    product_type = r.product_type,
                    number_of_plates = r.number_of_plates,
                    order_id = r.order_id,
                    quote_id = r.quote_id,
                    product_length_mm = r.product_length_mm,
                    product_width_mm = r.product_width_mm,
                    product_height_mm = r.product_height_mm,
                    glue_tab_mm = r.glue_tab_mm,
                    bleed_mm = r.bleed_mm,
                    is_one_side_box = r.is_one_side_box,
                    print_width_mm = r.print_width_mm,
                    print_length_mm = r.print_length_mm,
                    is_send_design = r.is_send_design,
                    reason = r.reason,
                    note = r.note,
                    accepted_estimate_id = r.accepted_estimate_id,
                    consultant_note = r.consultant_note,
                    verified_at = r.verified_at,
                    quote_expire_at = r.quote_expires_at,
                    message_to_customer = r.message_to_customer,
                    preliminary_estimated_price = r.preliminary_estimated_price,
                    assigned_consultant = r.assigned_consultant,
                    assigned_at = r.assigned_at,
                    delivery_note = r.delivery_note,
                    print_ready_file = r.print_ready_file,
                    estimate_finish_date = r.estimate_finish_date,
                    estimate_id = ce != null ? ce.estimate_id : null,
                    base_cost = ce != null ? ce.base_cost : null,
                    is_rush = ce != null ? ce.is_rush : null,
                    rush_percent = ce != null ? ce.rush_percent : null,
                    rush_amount = ce != null ? ce.rush_amount : null,
                    estimated_finish_date = ce != null ? ce.estimated_finish_date : null,
                    desired_delivery_date = ce != null ? ce.desired_delivery_date : null,
                    estimate_created_at = ce != null ? ce.created_at : null,
                    paper_cost = ce != null ? ce.paper_cost : null,
                    ink_cost = ce != null ? ce.ink_cost : null,
                    coating_glue_cost = ce != null ? ce.coating_glue_cost : null,
                    mounting_glue_cost = ce != null ? ce.mounting_glue_cost : null,
                    lamination_cost = ce != null ? ce.lamination_cost : null,
                    material_cost = ce != null ? ce.material_cost : null,
                    sheets_required = ce != null ? ce.sheets_required : null,
                    sheets_waste = ce != null ? ce.sheets_waste : null,
                    sheets_total = ce != null ? ce.sheets_total : null,
                    total_area_m2 = ce != null ? ce.total_area_m2 : null,
                    final_total_cost = ce != null ? ce.final_total_cost : null,
                    cost_note = ce != null ? ce.cost_note : null,
                    paper_sheets_used = ce != null ? ce.paper_sheets_used : null,
                    paper_unit_price = ce != null ? ce.paper_unit_price : null,
                    ink_weight_kg = ce != null ? ce.ink_weight_kg : null,
                    ink_rate_per_m2 = ce != null ? ce.ink_rate_per_m2 : null,
                    coating_glue_weight_kg = ce != null ? ce.coating_glue_weight_kg : null,
                    coating_glue_rate_per_m2 = ce != null ? ce.coating_glue_rate_per_m2 : null,
                    coating_type = ce != null ? ce.coating_type : null,
                    mounting_glue_weight_kg = ce != null ? ce.mounting_glue_weight_kg : null,
                    mounting_glue_rate_per_m2 = ce != null ? ce.mounting_glue_rate_per_m2 : null,
                    lamination_weight_kg = ce != null ? ce.lamination_weight_kg : null,
                    lamination_rate_per_m2 = ce != null ? ce.lamination_rate_per_m2 : null,
                    days_early = ce != null ? ce.days_early : null,
                    subtotal = ce != null ? ce.subtotal : null,
                    discount_percent = ce != null ? ce.discount_percent : null,
                    discount_amount = ce != null ? ce.discount_amount : null,
                    deposit_amount = ce != null ? ce.deposit_amount : null,
                    design_cost = ce != null ? ce.design_cost : null,
                    n_up = ce != null ? ce.n_up : null,
                    is_active = ce != null ? ce.is_active : null,
                    paper_code = ce != null ? ce.paper_code : null,
                    paper_name = ce != null ? ce.paper_name : null,
                    wave_type = ce != null ? ce.wave_type : null,
                    production_processes = ce != null ? ce.production_processes : null,
                    previous_estimate_id = ce != null ? ce.previous_estimate_id : null,
                    consultant_contract_path = ce != null ? ce.consultant_contract_path : null,
                    customer_signed_contract_path = ce != null ? ce.customer_signed_contract_path : null,
                    wave_sheets_used = ce != null ? ce.wave_sheets_used : null,
                    paper_alternative = ce != null ? ce.paper_alternative : null,
                    wave_alternative = ce != null ? ce.wave_alternative : null,
                    assign_name = _db.users.AsNoTracking().Where(u => u.user_id == r.assigned_consultant).Select(u => u.full_name).FirstOrDefault(),
                    lamination_material_id = ce != null ? ce.lamination_material_id : null,
                    lamination_material_code = ce != null ? ce.lamination_material_code : null,
                    lamination_material_name = ce != null ? ce.lamination_material_name : null,
                    require_remaining_before_delivery = requireRemainingBeforeDelivery
                }
            ).FirstOrDefaultAsync();
        }

        public Task<bool> AnyOrderLinkedAsync(int requestId)
            => _db.order_requests
                .AnyAsync(r => r.order_request_id == requestId && r.order_id != null);

        public async Task<bool> HasEnoughStockForRequestAsync(int requestId, CancellationToken ct = default)
        {
            var q =
                from r in _db.order_requests
                where r.order_request_id == requestId
                join m in _db.materials
                    on r.product_name equals m.name into mj
                from m in mj.DefaultIfEmpty()
                select new
                {
                    RequiredQty = (decimal)(r.quantity ?? 0),
                    StockQty = m != null ? (m.stock_qty ?? 0m) : 0m
                };

            var data = await q.FirstOrDefaultAsync(ct);

            if (data == null)
                return false;

            if (data.RequiredQty <= 0m)
                return true;

            return data.StockQty >= data.RequiredQty;
        }
        
        public async Task<PagedResultLite<RequestEmailStatsDto>> GetEmailsByAcceptedCountPagedAsync(int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var q = _db.order_requests
                .AsNoTracking()
                .Where(x => x.customer_email != null)
                .Where(x => x.process_status != null && EF.Functions.ILike(x.process_status, "accepted%"))
                .GroupBy(x => x.customer_email!)
                .Select(g => new
                {
                    CustomerEmail = g.Key,
                    AcceptedCount = g.Count()
                })
                .OrderByDescending(x => x.AcceptedCount)
                .ThenBy(x => x.CustomerEmail);

            var list = await q.Skip(skip).Take(pageSize + 1).ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            return new PagedResultLite<RequestEmailStatsDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = list.Select(x => new RequestEmailStatsDto(x.CustomerEmail, x.AcceptedCount)).ToList()
            };
        }

        public async Task<PagedResultLite<RequestStockCoverageDto>> GetSortedByStockCoveragePagedAsync(
    int page, int pageSize, int? consultantUserId = null, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var scopedRequests = _db.order_requests.AsNoTracking();

            var baseQuery =
                from r in scopedRequests
                join m in _db.materials.AsNoTracking()
                    on r.product_name equals m.name into mj
                from m in mj.DefaultIfEmpty()
                let qtyDec = (decimal)(r.quantity ?? 0)
                let stockQty = (m != null ? (m.stock_qty ?? 0m) : 0m)
                let ratio = qtyDec == 0m ? 0m : (stockQty / qtyDec)
                select new
                {
                    r,
                    stockQty,
                    ratio
                };

            var ordered = baseQuery
                .OrderByDescending(x => x.ratio)
                .ThenByDescending(x => x.stockQty)
                .ThenBy(x => x.r.order_request_id);

            var list = await ordered
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(x => new RequestStockCoverageDto(
                    x.r.order_request_id,
                    x.r.customer_name ?? "",
                    x.r.customer_phone ?? "",
                    x.r.customer_email,
                    x.r.delivery_date,
                    x.r.product_name ?? "",
                    x.r.quantity ?? 0,
                    x.stockQty,
                    x.ratio,
                    x.r.process_status,
                    x.r.order_request_date
                ))
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            return new PagedResultLite<RequestStockCoverageDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = list
            };
        }
        
        public async Task<string?> GetEmailByPhoneAsync(string phone, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return null;

            phone = phone.Trim();

            return await _db.order_requests
                .AsNoTracking()
                .Where(r => r.customer_phone == phone && r.customer_email != null)
                .OrderByDescending(r => r.order_request_date)
                .Select(r => r.customer_email!)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<PagedResultLite<OrderListDto>> GetOrdersByPhonePagedAsync(string phone, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;
            phone = phone.Trim();

            var baseQuery =
                from r in _db.order_requests.AsNoTracking()
                join o in _db.orders.AsNoTracking()
                    on r.order_id equals o.order_id
                where r.customer_phone == phone
                orderby o.order_date descending, o.order_id descending
                select new OrderListDto
                {
                    order_id = o.order_id,
                    code = o.code,
                    quote_id = o.quote_id,
                    order_date = o.order_date,
                    delivery_date = o.delivery_date,
                    total_amount = o.total_amount,
                    status = o.status,
                    is_enough = o.is_enough,
                    is_buy = o.is_buy,
                    payment_status = o.payment_status,
                    production_id = o.production_id,
                    layout_confirmed = o.layout_confirmed,
                    is_production_ready = o.is_production_ready,
                    confirmed_delivery_at = o.confirmed_delivery_at
                };

            var list = await baseQuery
                .Skip(skip)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            return new PagedResultLite<OrderListDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = list
            };
        }

        public Task<string?> GetDesignFilePathAsync(int orderRequestId, int? consultantUserId = null, CancellationToken ct = default)
        {
            return _db.order_requests.AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId)
                .Select(x => x.design_file_path)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetRequestsByPhonePagedAsync(
    string phone, int page, int pageSize, int? consultantUserId = null, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;
            phone = phone.Trim();

            var query =
                from r in _db.order_requests.AsNoTracking()
                where r.customer_phone == phone

                join ce in _db.cost_estimates.AsNoTracking()
                    on r.order_request_id equals ce.order_request_id into ceJoin

                from ce in ceJoin
                    .OrderByDescending(x => x.is_active)
                    .ThenByDescending(x => x.estimate_id)
                    .Take(1)
                    .DefaultIfEmpty()

                orderby r.order_request_date descending, r.order_request_id descending
                select new RequestSortedDto
                {
                    // ===== order_request =====
                    request_id = r.order_request_id,
                    customer_name = r.customer_name,
                    phone = r.customer_phone,
                    email = r.customer_email,
                    delivery_date = r.delivery_date,
                    product_name = r.product_name,
                    quantity = r.quantity,
                    description = r.description,
                    design_file_path = r.design_file_path,
                    print_ready_file = r.print_ready_file,
                    request_date = r.order_request_date,
                    detail_address = r.detail_address,
                    status = r.process_status,
                    product_type = r.product_type,
                    number_of_plates = r.number_of_plates,
                    order_id = r.order_id,
                    quote_id = r.quote_id,
                    product_length_mm = r.product_length_mm,
                    product_width_mm = r.product_width_mm,
                    product_height_mm = r.product_height_mm,
                    glue_tab_mm = r.glue_tab_mm,
                    bleed_mm = r.bleed_mm,
                    is_one_side_box = r.is_one_side_box,
                    print_width_mm = r.print_width_mm,
                    print_length_mm = r.print_length_mm,
                    is_send_design = r.is_send_design,
                    note = r.note,
                    reason = r.reason,
                    accepted_estimate_id = r.accepted_estimate_id,
                    consultant_note = r.consultant_note,
                    verified_at = r.verified_at,
                    quote_expires_at = r.quote_expires_at,
                    message_to_customer = r.message_to_customer,
                    preliminary_estimated_price = r.preliminary_estimated_price,
                    assigned_consultant = r.assigned_consultant,
                    assigned_at = r.assigned_at,
                    delivery_note = r.delivery_note,
                    actual_consultant_user_id = r.actual_consultant_user_id,
                    delivery_date_change_reason = r.delivery_date_change_reason,
                    estimate_finish_date = r.estimate_finish_date,
                    assign_name = _db.users.AsNoTracking().Where(u => u.user_id == r.assigned_consultant).Select(u => u.full_name).FirstOrDefault(),
                    // ===== cost_estimate =====
                    estimate_id = ce != null ? ce.estimate_id : null,
                    estimate_order_request_id = ce != null ? ce.order_request_id : null,
                    paper_cost = ce != null ? ce.paper_cost : null,
                    paper_sheets_used = ce != null ? ce.paper_sheets_used : null,
                    paper_unit_price = ce != null ? ce.paper_unit_price : null,
                    ink_cost = ce != null ? ce.ink_cost : null,
                    ink_weight_kg = ce != null ? ce.ink_weight_kg : null,
                    ink_rate_per_m2 = ce != null ? ce.ink_rate_per_m2 : null,
                    coating_glue_cost = ce != null ? ce.coating_glue_cost : null,
                    coating_glue_weight_kg = ce != null ? ce.coating_glue_weight_kg : null,
                    coating_glue_rate_per_m2 = ce != null ? ce.coating_glue_rate_per_m2 : null,
                    coating_type = ce != null ? ce.coating_type : null,
                    mounting_glue_cost = ce != null ? ce.mounting_glue_cost : null,
                    mounting_glue_weight_kg = ce != null ? ce.mounting_glue_weight_kg : null,
                    mounting_glue_rate_per_m2 = ce != null ? ce.mounting_glue_rate_per_m2 : null,
                    lamination_cost = ce != null ? ce.lamination_cost : null,
                    lamination_weight_kg = ce != null ? ce.lamination_weight_kg : null,
                    lamination_rate_per_m2 = ce != null ? ce.lamination_rate_per_m2 : null,
                    material_cost = ce != null ? ce.material_cost : null,
                    base_cost = ce != null ? ce.base_cost : null,
                    is_rush = ce != null ? ce.is_rush : null,
                    rush_percent = ce != null ? ce.rush_percent : null,
                    rush_amount = ce != null ? ce.rush_amount : null,
                    days_early = ce != null ? ce.days_early : null,
                    subtotal = ce != null ? ce.subtotal : null,
                    discount_percent = ce != null ? ce.discount_percent : null,
                    discount_amount = ce != null ? ce.discount_amount : null,
                    final_total_cost = ce != null ? ce.final_total_cost : null,
                    estimated_finish_date = ce != null ? ce.estimated_finish_date : null,
                    desired_delivery_date = ce != null ? ce.desired_delivery_date : null,
                    estimate_created_at = ce != null ? ce.created_at : null,
                    sheets_required = ce != null ? ce.sheets_required : null,
                    sheets_waste = ce != null ? ce.sheets_waste : null,
                    sheets_total = ce != null ? ce.sheets_total : null,
                    n_up = ce != null ? ce.n_up : null,
                    total_area_m2 = ce != null ? ce.total_area_m2 : null,
                    design_cost = ce != null ? ce.design_cost : null,
                    cost_note = ce != null ? ce.cost_note : null,
                    is_active = ce != null ? ce.is_active : null,
                    paper_code = ce != null ? ce.paper_code : null,
                    paper_name = ce != null ? ce.paper_name : null,
                    wave_type = ce != null ? ce.wave_type : null,
                    paper_alternative = ce != null ? ce.paper_alternative : null,
                    wave_alternative = ce != null ? ce.wave_alternative : null,
                    wave_sheets_used = ce != null ? ce.wave_sheets_used : null,
                    production_processes = ce != null ? ce.production_processes : null,
                    deposit_amount = ce != null ? ce.deposit_amount : null,
                    previous_estimate_id = ce != null ? ce.previous_estimate_id : null,
                    consultant_contract_path = ce != null ? ce.consultant_contract_path : null,
                    customer_signed_contract_path = ce != null ? ce.customer_signed_contract_path : null,
                    waste_gluing_boxes = ce != null ? ce.waste_gluing_boxes : null,
                    sheet_area_m2 = ce != null ? ce.sheet_area_m2 : null,
                    print_sheets_used = ce != null ? ce.print_sheets_used : null,
                    total_coating_area_m2 = ce != null ? ce.total_coating_area_m2 : null,
                    total_lamination_area_m2 = ce != null ? ce.total_lamination_area_m2 : null,
                    coating_sheets_used = ce != null ? ce.coating_sheets_used : null,
                    lamination_sheets_used = ce != null ? ce.lamination_sheets_used : null,
                    wave_sheet_area_m2 = ce != null ? ce.wave_sheet_area_m2 : null,
                    wave_n_up = ce != null ? ce.wave_n_up : null,
                    wave_sheets_required = ce != null ? ce.wave_sheets_required : null,
                    total_mounting_area_m2 = ce != null ? ce.total_mounting_area_m2 : null,
                    wave_unit_price = ce != null ? ce.wave_unit_price : null,
                    wave_cost = ce != null ? ce.wave_cost : null,
                    total_process_cost = ce != null ? ce.total_process_cost : null,
                    ink_type_names = ce != null ? ce.ink_type_names : null,
                    alternative_material_reason = ce != null ? ce.alternative_material_reason : null,
                    lamination_material_id = ce != null ? ce.lamination_material_id : null,
                    lamination_material_code = ce != null ? ce.lamination_material_code : null,
                    lamination_material_name = ce != null ? ce.lamination_material_name : null,
                };

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            return new PagedResultLite<RequestSortedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = list
            };
        }

        public async Task<RequestDetailDto?> GetInformationRequestById(
    int requestId, int? consultantUserId = null, CancellationToken ct = default)
        {
            var request = await _db.order_requests.AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == requestId, ct);

            if (request == null)
                return null;
            var assignedFullName = request.assigned_consultant.HasValue
    ? await _db.users
        .AsNoTracking()
        .Where(u => u.user_id == request.assigned_consultant.Value)
        .Select(u => u.full_name)
        .FirstOrDefaultAsync(ct)
    : null;
            var estimates = await _db.cost_estimates
                .AsNoTracking()
                .Include(x => x.process_costs)
                .Where(x => x.order_request_id == requestId)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .ToListAsync(ct);
            var altPaperCodes = estimates
    .Where(x => !string.IsNullOrWhiteSpace(x.paper_alternative))
    .Select(x => x.paper_alternative!.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

            var materialNameByCode = altPaperCodes.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await _db.materials
                    .AsNoTracking()
                    .Where(x => altPaperCodes.Contains(x.code))
                    .Select(x => new { x.code, x.name })
                    .ToDictionaryAsync(x => x.code, x => x.name, StringComparer.OrdinalIgnoreCase, ct);
            var vatPercent = await GetVatPercentAsync(ct);

            var selectedEstimate = estimates.FirstOrDefault();

            var selectedPaperCode = selectedEstimate == null
    ? ""
    : EstimateMaterialAlternativeHelper.ResolvePaperCode(
        selectedEstimate.paper_alternative,
        selectedEstimate.paper_code) ?? "";

            var selectedPaperName = selectedEstimate == null
                ? ""
                : EstimateMaterialAlternativeHelper.ResolvePaperName(
                    selectedPaperCode,
                    selectedEstimate.paper_name,
                    materialNameByCode);

            var selectedWaveType = selectedEstimate == null
                ? ""
                : EstimateMaterialAlternativeHelper.ResolveWaveType(
                    selectedEstimate.wave_alternative,
                    selectedEstimate.wave_type) ?? "";

            return new RequestDetailDto
            {
                request_id = request.order_request_id,
                order_id = request.order_id,
                customer_name = SafeText(request.customer_name),
                customer_phone = SafeText(request.customer_phone),
                email = SafeText(request.customer_email),
                delevery_date = request.delivery_date,
                delivery_date_change_reason = SafeText(request.delivery_date_change_reason),
                consultant_note = SafeText(request.consultant_note),
                product_name = SafeText(request.product_name),
                quantity = request.quantity ?? 0,
                process_status = SafeText(request.process_status),
                request_date = request.order_request_date,
                description = SafeText(request.description),
                design_file_path = SafeText(request.design_file_path),
                detail_address = SafeText(request.detail_address),
                product_type = SafeText(request.product_type),
                number_of_plates = request.number_of_plates,
                production_processes = SafeText(selectedEstimate?.production_processes),
                coating_type = SafeText(selectedEstimate?.coating_type),
                paper_code = selectedPaperCode,
                paper_name = selectedPaperName,
                wave_type = selectedWaveType,
                is_send_design = request.is_send_design,
                product_length_mm = request.product_length_mm,
                product_width_mm = request.product_width_mm,
                product_height_mm = request.product_height_mm,
                glue_tab_mm = request.glue_tab_mm,
                bleed_mm = request.bleed_mm,
                is_one_side_box = request.is_one_side_box,
                print_width_mm = request.print_width_mm,
                print_length_mm = request.print_length_mm,
                reason = SafeText(request.reason),
                note = SafeText(request.note),
                verified_at = request.verified_at,
                quote_expires_at = request.quote_expires_at,
                message_to_customer = SafeText(request.message_to_customer),
                printer_ready_file_path = SafeText(request.print_ready_file),
                actual_consultant_user_id = request.actual_consultant_user_id,
                assign_name = SafeText(assignedFullName),
                deposit_receipt_path = request.deposit_receipt_path,
                remaining_receipt_path = request.remaining_receipt_path,
                cost_estimate = estimates.Select(ce =>
                {
                    var discountAmount = ce.discount_amount < 0m ? 0m : ce.discount_amount;
                    var vatBase = Math.Max(ce.subtotal - discountAmount, 0m);
                    var vatAmount = vatPercent <= 0m ? 0m : vatBase * vatPercent / 100m;
                    var displayPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(ce.paper_alternative, ce.paper_code);
                    var displayPaperName = EstimateMaterialAlternativeHelper.ResolvePaperName(
                        displayPaperCode,
                        ce.paper_name,
                        materialNameByCode);

                    var displayWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                        ce.wave_alternative,
                        ce.wave_type);

                    return new CostEstimateDetailDto
                    {
                        estimate_id = ce.estimate_id,
                        previous_estimate_id = ce.previous_estimate_id,
                        final_total_cost = ce.final_total_cost,
                        deposit_amount = ce.deposit_amount,
                        is_active = ce.is_active,

                        paper_code = displayPaperCode,
                        paper_name = displayPaperName,
                        coating_type = SafeText(ce.coating_type),
                        wave_type = displayWaveType,
                        paper_alternative = ce.paper_alternative,
                        wave_alternative = ce.wave_alternative,
                        wave_sheet_used = ce.wave_sheets_used,
                        production_processes = SafeText(ce.production_processes),
                        cost_note = SafeText(ce.cost_note),

                        paper_sheets_used = ce.paper_sheets_used,
                        paper_unit_price = ce.paper_unit_price,
                        alternative_material_reason = SafeText(ce.alternative_material_reason),
                        ink_weight_kg = ce.ink_weight_kg,
                        ink_rate_per_m2 = ce.ink_rate_per_m2,
                        ink_type_names = ce.ink_type_names,

                        coating_glue_weight_kg = ce.coating_glue_weight_kg,
                        coating_glue_rate_per_m2 = ce.coating_glue_rate_per_m2,

                        mounting_glue_weight_kg = ce.mounting_glue_weight_kg,
                        mounting_glue_rate_per_m2 = ce.mounting_glue_rate_per_m2,

                        lamination_weight_kg = ce.lamination_weight_kg,
                        lamination_rate_per_m2 = ce.lamination_rate_per_m2,

                        paper_cost = ce.paper_cost,
                        ink_cost = ce.ink_cost,
                        coating_glue_cost = ce.coating_glue_cost,
                        mounting_glue_cost = ce.mounting_glue_cost,
                        lamination_cost = ce.lamination_cost,
                        material_cost = ce.material_cost,
                        base_cost = ce.base_cost,
                        design_cost = ce.design_cost,
                        subtotal = ce.subtotal,
                        discount_percent = ce.discount_percent,
                        discount_amount = discountAmount,
                        vat_percent = vatPercent,
                        vat_amount = vatAmount,
                        consultant_contract_path = SafeText(ce.consultant_contract_path),
                        customer_signed_contract_path = SafeText(ce.customer_signed_contract_path),
                        lamination_material_id = ce.lamination_material_id,
                        lamination_material_code = ce.lamination_material_code,
                        lamination_material_name = ce.lamination_material_name,
                        process_cost = ce.process_costs
                            .OrderBy(pc => pc.process_cost_id)
                            .Select(pc => new ProcessCostDetailDto
                            {
                                process_cost_id = pc.process_cost_id,
                                process_code = SafeText(pc.process_code),
                                cost = pc.total_cost
                            })
                            .ToList()
                    };
                }).ToList()
            };
        }

        public async Task<RequestWithTwoEstimatesDto?> GetActiveEstimatesInProcessAsync(int requestId, int? consultantUserId = null, CancellationToken ct = default)
        {
            var req = await _db.order_requests.AsNoTracking()
                    .Where(r => r.order_request_id == requestId)
                .Select(r => new RequestWithTwoEstimatesDto
                {
                    order_request_id = r.order_request_id,
                    actual_consultant_user_id = r.actual_consultant_user_id,
                    print_ready_file = r.print_ready_file,
                    customer_name = r.customer_name ?? "",
                    customer_phone = r.customer_phone ?? "",
                    customer_email = r.customer_email,
                    delivery_date = r.delivery_date,
                    delivery_date_change_reason = r.delivery_date_change_reason,
                    consultant_note = r.consultant_note,
                    product_name = r.product_name ?? "",
                    quantity = r.quantity ?? 0,
                    description = r.description,
                    detail_address = r.detail_address,
                    product_type = r.product_type,
                    number_of_plates = r.number_of_plates,
                    product_length_mm = r.product_length_mm,
                    product_width_mm = r.product_width_mm,
                    product_height_mm = r.product_height_mm,
                    glue_tab_mm = r.glue_tab_mm,
                    bleed_mm = r.bleed_mm,
                    is_one_side_box = r.is_one_side_box,
                    print_width_mm = r.print_width_mm,
                    print_length_mm = r.print_length_mm,
                    is_send_design = r.is_send_design,
                    message_to_customer = r.message_to_customer,
                    assign_name = _db.users.AsNoTracking().Where(u => u.user_id == r.assigned_consultant).Select(u => u.full_name).FirstOrDefault(),
                })
                .FirstOrDefaultAsync(ct);

            if (req == null) return null;

            var ests = await _db.cost_estimates
                .AsNoTracking()
                .Where(e => e.order_request_id == requestId && e.is_active)
                .OrderByDescending(e => e.estimate_id)
                .Take(2)
                .Select(e => new CostEstimateCompareDto
                {
                    estimate_id = e.estimate_id,
                    previous_estimate_id = e.previous_estimate_id,
                    is_active = e.is_active,
                    paper_cost = e.paper_cost,
                    ink_cost = e.ink_cost,
                    coating_glue_cost = e.coating_glue_cost,
                    mounting_glue_cost = e.mounting_glue_cost,
                    lamination_cost = e.lamination_cost,
                    material_cost = e.material_cost,
                    base_cost = e.base_cost,
                    is_rush = e.is_rush,
                    rush_percent = e.rush_percent,
                    rush_amount = e.rush_amount,
                    subtotal = e.subtotal,
                    discount_percent = e.discount_percent,
                    discount_amount = e.discount_amount,
                    final_total_cost = e.final_total_cost,
                    deposit_amount = e.deposit_amount,
                    created_at = e.created_at,
                    estimated_finish_date = e.estimated_finish_date,
                    desired_delivery_date = e.desired_delivery_date,
                    sheets_required = e.sheets_required,
                    sheets_waste = e.sheets_waste,
                    sheets_total = e.sheets_total,
                    n_up = e.n_up,
                    total_area_m2 = e.total_area_m2,
                    production_processes = e.production_processes,
                    design_cost = e.design_cost,
                    paper_code = e.paper_code,
                    paper_name = e.paper_name,
                    coating_type = e.coating_type,
                    wave_type = e.wave_type,
                    paper_alternative = e.paper_alternative,
                    wave_alternative = e.wave_alternative,
                    wave_sheet_used = e.wave_sheets_used,
                    cost_note = e.cost_note,
                    waste_gluing_boxes = e.waste_gluing_boxes,
                    sheet_area_m2 = e.sheet_area_m2,
                    print_sheets_used = e.print_sheets_used,
                    total_coating_area_m2 = e.total_coating_area_m2,
                    total_lamination_area_m2 = e.total_lamination_area_m2,
                    coating_sheets_used = e.coating_sheets_used,
                    lamination_sheets_used = e.lamination_sheets_used,
                    wave_sheet_area_m2 = e.wave_sheet_area_m2,
                    wave_n_up = e.wave_n_up,
                    wave_sheets_required = e.wave_sheets_required,
                    total_mounting_area_m2 = e.total_mounting_area_m2,
                    wave_unit_price = e.wave_unit_price,
                    wave_cost = e.wave_cost,
                    total_process_cost = e.total_process_cost,
                    lamination_material_id = e.lamination_material_id,
                    lamination_material_code = e.lamination_material_code,
                    lamination_material_name = e.lamination_material_name,
                    process_costs = e.process_costs
    .OrderBy(pc => pc.process_code)
    .Select(pc => new EstimateProcessCostDto
    {
        process_code = pc.process_code,
        process_name = pc.process_name,
        quantity = pc.quantity,
        unit = pc.unit,
        unit_price = pc.unit_price,
        total_cost = pc.total_cost,
        note = pc.note
    }).ToList(),
                })
                .ToListAsync(ct);
            var altPaperCodes = ests
    .Where(x => !string.IsNullOrWhiteSpace(x.paper_alternative))
    .Select(x => x.paper_alternative!.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

            var materialNameByCode = altPaperCodes.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await _db.materials
                    .AsNoTracking()
                    .Where(x => altPaperCodes.Contains(x.code))
                    .Select(x => new { x.code, x.name })
                    .ToDictionaryAsync(x => x.code, x => x.name, StringComparer.OrdinalIgnoreCase, ct);

            foreach (var e in ests)
            {
                var displayPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                    e.paper_alternative,
                    e.paper_code);

                var displayPaperName = EstimateMaterialAlternativeHelper.ResolvePaperName(
                    displayPaperCode,
                    e.paper_name,
                    materialNameByCode);

                var displayWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                    e.wave_alternative,
                    e.wave_type);

                e.paper_code = displayPaperCode;
                e.paper_name = displayPaperName;
                e.wave_type = displayWaveType;
            }
            req.estimates = ests;
            return req;
        }

        public async Task<int> DeleteDesignFilePathByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return await _db.order_requests
                .Where(x => x.order_request_id == orderRequestId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.design_file_path, (string?)null),
                    ct);
        }

        public async Task<List<cost_estimate>> GetActiveEstimatesWithProcessesByRequestIdAsync(int requestId, int? consultantUserId = null, CancellationToken ct = default)
        {
            var allowedRequestIds = _db.order_requests.AsNoTracking()
                .Where(x => x.order_request_id == requestId)
                .Select(x => x.order_request_id);

            return await _db.cost_estimates
                .Include(x => x.process_costs)
                .Where(x => allowedRequestIds.Contains(x.order_request_id) && x.is_active)
                .OrderByDescending(x => x.estimate_id)
                .ToListAsync(ct);
        }

        private static string SafeText(string? value)
    => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        private static string FirstText(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        public async Task<order_request?> GetRequestForUpdateAsync(int orderRequestId, CancellationToken ct)
        {
            return await _db.order_requests
                .FromSqlInterpolated($@"
            SELECT *
            FROM ""AMMS_DB"".""order_request""
            WHERE order_request_id = {orderRequestId}
            FOR UPDATE")
                .FirstOrDefaultAsync(ct);
        }

        public async Task<decimal> GetVatPercentAsync(CancellationToken ct = default)
        {
            var vat = await _db.estimate_config
                .AsNoTracking()
                .Where(x =>
                    x.config_key == "vat_percent" &&
                    (x.config_group == "systemParameters" || x.config_group == "system"))
                .OrderByDescending(x => x.config_group == "systemParameters" ? 1 : 0)
                .ThenByDescending(x => x.updated_at)
                .Select(x => x.value_num)
                .FirstOrDefaultAsync(ct);
            return vat ?? 0m;
        }
        public async Task<bool> TryMarkDealWaitingFromVerifiedAsync(int requestId, CancellationToken ct = default)
        {
            var affected = await _db.order_requests
                .Where(x => x.order_request_id == requestId && x.process_status == "Verified")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.process_status, "Waiting"), ct);

            return affected == 1;
        }

        private static readonly string[] ClosedStatuses = new[]
{
    "Cancel", "Declined", "Rejected"
};

        //private IQueryable<order_request> ApplyConsultantScope(
        //    IQueryable<order_request> query,
        //    int? consultantUserId)
        //{
        //    if (!consultantUserId.HasValue)
        //        return query;

        //    return query.Where(x => x.assigned_consultant == consultantUserId.Value);
        //}

        public async Task<int?> GetLeastLoadedConsultantUserIdAsync(CancellationToken ct = default)
        {
            const int ConsultantRoleId = 2;

            var query =
                from u in _db.users.AsNoTracking()
                where u.role_id == ConsultantRoleId && (u.is_active ?? true)
                join req in _db.order_requests.AsNoTracking()
                        .Where(x =>
                            x.assigned_consultant != null &&
                            x.order_id == null &&
                            !ClosedStatuses.Contains((x.process_status ?? "").Trim()))
                    on u.user_id equals req.assigned_consultant into reqGroup
                select new
                {
                    u.user_id,
                    workload = reqGroup.Count(),
                    last_assigned_at = reqGroup.Max(x => x.assigned_at)
                };

            return await query
                .OrderBy(x => x.workload)
                .ThenBy(x => x.last_assigned_at ?? DateTime.MinValue)
                .ThenBy(x => x.user_id)
                .Select(x => (int?)x.user_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<bool> CanConsultantAccessRequestAsync(int requestId, int consultantUserId, CancellationToken ct = default)
        {
            return await _db.order_requests
                .AsNoTracking()
                .AnyAsync(x =>
                    x.order_request_id == requestId &&
                    x.assigned_consultant == consultantUserId,
                    ct);
        }

        public async Task<bool> UpdateDeliveryNoteAsync(int orderId, string note, CancellationToken ct = default)
        {
            var entity = await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (entity == null)
                return false;

            entity.delivery_note = note;

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<order_request?> GetByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _db.order_requests
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<DateTime?> CalculateAsync(int orderRequestId, CancellationToken ct = default)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            if (req == null)
                return null;

            var baseDate = ResolveBaseDate(req);

            var minFinish = baseDate.AddDays(7);

            var productTypeId = await _db.product_types
                .AsNoTracking()
                .Where(x => x.code == req.product_type)
                .Select(x => (int?)x.product_type_id)
                .FirstOrDefaultAsync(ct);

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                return minFinish;

            var latestEstimate = await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            var selectedCodes = ParseCsv(latestEstimate?.production_processes);

            var allSteps = await _db.product_type_processes
                .AsNoTracking()
                .Where(x => x.product_type_id == productTypeId.Value && (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .Select(x => new ProcessStepVm
                {
                    ProcessCode = x.process_code,
                    ProcessName = x.process_name,
                    SeqNum = x.seq_num
                })
                .ToListAsync(ct);

            if (allSteps.Count == 0)
                return minFinish;

            var routeSteps = selectedCodes.Count == 0
                ? allSteps
                : allSteps
                    .Where(x => selectedCodes.Contains(Normalize(x.ProcessCode)))
                    .OrderBy(x => x.SeqNum)
                    .ToList();

            if (routeSteps.Count == 0)
                routeSteps = allSteps;

            var freeMachines = await _machineRepository.GetFreeMachinesAsync();

            var machineMap = freeMachines
                .GroupBy(x => NormalizeText(x.ProcessName))
                .ToDictionary(
                    g => g.Key,
                    g => new MachineStatVm
                    {
                        Total = g.Sum(x => x.TotalMachines),
                        Busy = g.Sum(x => x.BusyMachines),
                        Free = g.Sum(x => x.FreeMachines)
                    });

            decimal totalProcessDays = 0m;

            foreach (var step in routeSteps)
            {
                var key = NormalizeText(step.ProcessName);

                if (!machineMap.TryGetValue(key, out var stat))
                {
                    totalProcessDays += 1m;
                    continue;
                }

                if (stat.Free >= 2)
                {
                    totalProcessDays += 0.5m;
                }
                else if (stat.Free == 1)
                {
                    totalProcessDays += 1m;
                }
                else if (stat.Total > 0)
                {
                    totalProcessDays += 2m;
                }
                else
                {
                    totalProcessDays += 1m;
                }
            }

            const decimal deliveryBufferDays = 1m;

            var estimatedByCapacity = baseDate.AddDays((double)Math.Ceiling(totalProcessDays + deliveryBufferDays));

            return estimatedByCapacity > minFinish ? estimatedByCapacity : minFinish;
        }

        public async Task<DateTime?> RecalculateAndPersistAsync(int orderRequestId, CancellationToken ct = default)
        {
            var req = await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            if (req == null)
                return null;

            var calculated = await CalculateAsync(orderRequestId, ct);
            if (!calculated.HasValue)
                return null;

            req.estimate_finish_date = ToUnspecified(calculated.Value);
            await _db.SaveChangesAsync(ct);

            return req.estimate_finish_date;
        }

        private static DateTime ResolveBaseDate(order_request req)
        {
            if (req.order_request_date != default)
                return ToUnspecified((DateTime)req.order_request_date);

            return AppTime.NowVnUnspecified();
        }

        private static HashSet<string> ParseCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Normalize(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string Normalize(string? value)
            => (value ?? "").Trim().ToUpperInvariant();

        private static string NormalizeText(string? value)
            => (value ?? "").Trim().ToUpperInvariant();

        private static DateTime ToUnspecified(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                return dt;

            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }

        private sealed class ProcessStepVm
        {
            public string? ProcessCode { get; set; }
            public string? ProcessName { get; set; }
            public int SeqNum { get; set; }
        }

        private sealed class MachineStatVm
        {
            public int Total { get; set; }
            public int Busy { get; set; }
            public int Free { get; set; }
        }
    }
}