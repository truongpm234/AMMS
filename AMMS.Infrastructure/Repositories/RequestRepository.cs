using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AMMS.Infrastructure.Repositories
{
    public class RequestRepository : IRequestRepository
    {
        private readonly AppDbContext _db;

        public RequestRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<order_request?> GetByIdAsync(int id)
        {
            return await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == id);
        }
        public async Task<RequestWithCostDto?> GetByIdWithCostAsync(int id)
        {
            var query = from r in _db.order_requests.AsNoTracking()
                        where r.order_request_id == id
                        join ce in _db.cost_estimates.AsNoTracking()
                            on r.order_request_id equals ce.order_request_id into ceJoin
                        from ce in ceJoin.OrderBy(x => x.estimate_id).Take(1).DefaultIfEmpty()

                        select new RequestWithCostDto
                        {
                            order_request_id = r.order_request_id,
                            customer_name = r.customer_name,
                            customer_phone = r.customer_phone,
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
                            production_processes = r.production_processes,
                            coating_type = r.coating_type,
                            paper_code = r.paper_code,
                            paper_name = r.paper_name,
                            wave_type = r.wave_type,
                            order_id = r.order_id,
                            quote_id = r.quote_id,
                            product_length_mm = r.product_length_mm,
                            product_width_mm = r.product_width_mm,
                            product_height_mm = r.product_height_mm,
                            glue_tab_mm = r.glue_tab_mm,
                            bleed_mm = r.bleed_mm,
                            is_one_side_box = r.is_one_side_box,
                            print_width_mm = r.print_width_mm,
                            print_height_mm = r.print_height_mm,
                            is_send_design = r.is_send_design,
                            reason = r.reason,
                            final_total_cost = ce != null ? ce.final_total_cost : null,
                            deposit_amount = ce != null ? ce.deposit_amount : null
                        };

            return await query.FirstOrDefaultAsync();
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
        public Task<int> CountAsync()
        {
            return _db.order_requests.AsNoTracking().CountAsync();
        }

        public Task<List<RequestPagedDto>> GetPagedAsync(int skip, int takePlusOne)
        {
            return (
                from r in _db.order_requests.AsNoTracking()

                join ce in _db.cost_estimates.AsNoTracking()
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
                    product_name = r.product_name,
                    quantity = r.quantity,
                    description = r.description,
                    design_file_path = r.design_file_path,
                    detail_address = r.detail_address,
                    number_of_plates = r.number_of_plates,
                    coating_type = r.coating_type,
                    process_status = r.process_status,
                    order_request_date = r.order_request_date,
                    final_cost = ce != null ? ce.final_total_cost : null,
                    deposit_amount = ce != null ? ce.deposit_amount : null
                }
            )
            .Skip(skip)
            .Take(takePlusOne)
            .ToListAsync();
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


        public async Task<PagedResultLite<RequestSortedDto>> GetSortedByQuantityPagedAsync(
    bool ascending, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var query = _db.order_requests.AsNoTracking();

            query = ascending
                ? query.OrderBy(x => x.quantity ?? 0)
                : query.OrderByDescending(x => x.quantity ?? 0);

            // lấy dư 1 record để biết có trang sau
            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(o => new RequestSortedDto(
                    o.order_request_id,
                    o.customer_name ?? "",
                    o.customer_phone ?? "",
                    o.customer_email,
                    o.delivery_date,
                    o.product_name ?? "",
                    o.quantity ?? 0,
                    o.process_status,
                    o.order_request_date
                ))
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<RequestSortedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetSortedByDatePagedAsync(
    bool ascending, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var query = _db.order_requests.AsNoTracking();
            query = ascending
                ? query.OrderBy(x => x.order_request_date == null)
                       .ThenBy(x => x.order_request_date)
                : query.OrderBy(x => x.order_request_date == null)
                       .ThenByDescending(x => x.order_request_date);

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(o => new RequestSortedDto(
                    o.order_request_id,
                    o.customer_name ?? "",
                    o.customer_phone ?? "",
                    o.customer_email,
                    o.delivery_date,
                    o.product_name ?? "",
                    o.quantity ?? 0,
                    o.process_status,
                    o.order_request_date
                ))
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<RequestSortedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetSortedByDeliveryDatePagedAsync(
    bool nearestFirst, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var query = _db.order_requests.AsNoTracking();
            query = nearestFirst
                ? query.OrderBy(x => x.delivery_date == null).ThenBy(x => x.delivery_date)
                : query.OrderBy(x => x.delivery_date == null).ThenByDescending(x => x.delivery_date);

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(o => new RequestSortedDto(
                    o.order_request_id,
                    o.customer_name ?? "",
                    o.customer_phone ?? "",
                    o.customer_email,
                    o.delivery_date,
                    o.product_name ?? "",
                    o.quantity ?? 0,
                    o.process_status,
                    o.order_request_date
                ))
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<RequestSortedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
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
    int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            // 1) Tạo base query có stock + ratio dạng primitive
            var baseQuery =
                from r in _db.order_requests.AsNoTracking()
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
        public async Task<PagedResultLite<RequestSortedDto>> GetByOrderRequestDatePagedAsync(
    DateOnly date, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var start = date.ToDateTime(TimeOnly.MinValue);     
            var end = start.AddDays(1);                         

            var query = _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_request_date != null)
                .Where(x => x.order_request_date >= start && x.order_request_date < end)
                .OrderByDescending(x => x.order_request_date);

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(o => new RequestSortedDto(
                    o.order_request_id,
                    o.customer_name ?? "",
                    o.customer_phone ?? "",
                    o.customer_email,
                    o.delivery_date,
                    o.product_name ?? "",
                    o.quantity ?? 0,
                    o.process_status,
                    o.order_request_date
                ))
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
        public async Task<PagedResultLite<RequestSortedDto>> SearchPagedAsync(
     string keyword, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            keyword = (keyword ?? "").Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return new PagedResultLite<RequestSortedDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = false,
                    Data = new()
                };
            }

            var skip = (page - 1) * pageSize;
            var pattern = $"%{keyword}%";

            var query = _db.order_requests
                .AsNoTracking()
                .Where(o =>
                    (o.product_name != null && EF.Functions.ILike(o.product_name, pattern)) ||
                    (o.product_type != null && EF.Functions.ILike(o.product_type, pattern)) ||
                    (o.customer_email != null && EF.Functions.ILike(o.customer_email, pattern)) ||
                    (o.customer_name != null && EF.Functions.ILike(o.customer_name, pattern)) ||
                    (o.customer_phone != null && EF.Functions.ILike(o.customer_phone, pattern)) ||
                    (o.process_status != null && EF.Functions.ILike(o.process_status, pattern))
                )
                .Select(o => new
                {
                    Entity = o,
                    Rank =
                        o.product_name != null && EF.Functions.ILike(o.product_name, pattern) ? 1 :
                        o.product_type != null && EF.Functions.ILike(o.product_type, pattern) ? 2 :
                        o.customer_email != null && EF.Functions.ILike(o.customer_email, pattern) ? 3 :
                        o.customer_name != null && EF.Functions.ILike(o.customer_name, pattern) ? 4 :
                        o.customer_phone != null && EF.Functions.ILike(o.customer_phone, pattern) ? 5 :
                        6
                })
                .OrderBy(x => x.Rank)
                .ThenByDescending(x => x.Entity.order_request_date);

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(x => new RequestSortedDto(
                    x.Entity.order_request_id,
                    x.Entity.customer_name ?? "",
                    x.Entity.customer_phone ?? "",
                    x.Entity.customer_email,
                    x.Entity.delivery_date,
                    x.Entity.product_name ?? "",
                    x.Entity.quantity ?? 0,
                    x.Entity.process_status,
                    x.Entity.order_request_date
                ))
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

        public async Task<PagedResultLite<OrderListDto>> GetOrdersByPhonePagedAsync(
            string phone, int page, int pageSize, CancellationToken ct = default)
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
                    Order_id = o.order_id,
                    Code = o.code,
                    Order_date = o.order_date,
                    Delivery_date = o.delivery_date,
                    Status = o.status,
                    Payment_status = o.payment_status,
                    Quote_id = o.quote_id,
                    Total_amount = o.total_amount
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

        public Task<string?> GetDesignFilePathAsync(int orderRequestId, CancellationToken ct = default)
        {
            return _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId)
                .Select(x => x.design_file_path)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetRequestsByPhonePagedAsync(
    string phone, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;
            phone = phone.Trim();

            var query = _db.order_requests
                .AsNoTracking()
                .Where(r => r.customer_phone == phone)
                .OrderByDescending(r => r.order_request_date)   
                .ThenByDescending(r => r.order_request_id);

            var list = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(o => new RequestSortedDto(
                    o.order_request_id,
                    o.customer_name ?? "",
                    o.customer_phone ?? "",
                    o.customer_email,
                    o.delivery_date,
                    o.product_name ?? "",
                    o.quantity ?? 0,
                    o.process_status,
                    o.order_request_date
                ))
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

        public async Task<RequestDetailDto?> GetInformationRequestById(int requestId, CancellationToken ct = default)
        {
            var query = await (
                from r in _db.order_requests.AsNoTracking()
                where r.order_request_id == requestId
                join ce in _db.cost_estimates.AsNoTracking()
                    on r.order_request_id equals ce.order_request_id into ceJoin
                from ce in ceJoin.DefaultIfEmpty()
                join cep in _db.cost_estimate_processes.AsNoTracking()
                    on ce.estimate_id equals cep.estimate_id into cepJoin
                from cep in cepJoin.DefaultIfEmpty()
                select new
                {
                    Request = r,
                    CostEstimate = ce,
                    ProcessCost = cep
                }
            )
            .OrderByDescending(x => x.CostEstimate != null ? x.CostEstimate.estimate_id : 0)
            .ToListAsync(ct);

            if (!query.Any())
                return null;

            var firstItem = query.First();
            return new RequestDetailDto
            {
                request_id = firstItem.Request.order_request_id,
                customer_name = firstItem.Request.customer_name ?? "",
                customer_phone = firstItem.Request.customer_phone ?? "",
                email = firstItem.Request.customer_email,
                delevery_date = firstItem.Request.delivery_date,
                product_name = firstItem.Request.product_name ?? "",
                quantity = firstItem.Request.quantity ?? 0,
                process_status = firstItem.Request.process_status,
                request_date = firstItem.Request.order_request_date,
                description = firstItem.Request.description,
                design_file_path = firstItem.Request.design_file_path,
                detail_address = firstItem.Request.detail_address,
                product_type = firstItem.Request.product_type,
                number_of_plates = firstItem.Request.number_of_plates,
                production_processes = firstItem.Request.production_processes,
                coating_type = firstItem.Request.coating_type,
                paper_code = firstItem.Request.paper_code,
                paper_name = firstItem.Request.paper_name,
                wave_type = firstItem.Request.wave_type,
                product_length_mm = firstItem.Request.product_length_mm,
                product_width_mm = firstItem.Request.product_width_mm,
                product_height_mm = firstItem.Request.product_height_mm,
                glue_tab_mm = firstItem.Request.glue_tab_mm,
                bleed_mm = firstItem.Request.bleed_mm,
                is_one_side_box = firstItem.Request.is_one_side_box,
                print_width_mm = firstItem.Request.print_width_mm,
                print_height_mm = firstItem.Request.print_height_mm,
                reason = firstItem.Request.reason,
                cost_estimate = query
                    .GroupBy(x => x.CostEstimate?.estimate_id)
                    .Select(g => new CostEstimateDetailDto
                    {
                        estimate_id = g.Key ?? 0,
                        final_total_cost = g.First().CostEstimate?.final_total_cost,
                        deposit_amount = g.First().CostEstimate?.deposit_amount,
                        process_cost = g
                            .Where(x => x.ProcessCost != null)
                            .Select(x => new ProcessCostDetailDto
                            {
                                process_cost_id = x.ProcessCost.process_cost_id,
                                process_code = x.ProcessCost.process_code,
                                cost = x.ProcessCost.total_cost
                            })
                            .ToList()
                    })
                    .ToList()
            };
        }
        public async Task<int> DeleteDesignFilePathByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return await _db.order_requests
                .Where(x => x.order_request_id == orderRequestId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.design_file_path, (string?)null),
                    ct);
        }
    }
}