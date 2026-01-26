using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Requests;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class RequestService : IRequestService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly IOrderRepository _orderRepo;
        private readonly AppDbContext _db;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IMaterialRepository _materialRepo;
        private readonly IBomRepository _bomRepo;

        public RequestService(
            IRequestRepository requestRepo,
            IOrderRepository orderRepo,
            ICostEstimateRepository estimateRepo,
            IMaterialRepository materialRepo,
            IBomRepository bomRepo,
            AppDbContext db)
        {
            _requestRepo = requestRepo;
            _orderRepo = orderRepo;
            _estimateRepo = estimateRepo;
            _materialRepo = materialRepo;
            _bomRepo = bomRepo;
            _db = db;
        }

        private DateTime? ToUnspecified(DateTime? dateTime)
        {
            if (!dateTime.HasValue) return null;
            return DateTime.SpecifyKind(dateTime.Value, DateTimeKind.Unspecified);
        }

        private static string Trunc20(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length <= 20) return s;
            return s.Substring(0, 20);
        }

        public async Task<CreateRequestResponse> CreateAsync(CreateResquest req)
        {
            var entity = new order_request
            {
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                delivery_date = ToUnspecified(req.delivery_date),
                product_name = req.product_name,
                quantity = req.quantity,
                description = req.description,
                design_file_path = req.design_file_path,
                order_request_date = ToUnspecified(req.order_request_date),
                detail_address = req.detail_address,
                process_status = "Pending",
                is_send_design = req.is_send_design
            };

            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return new CreateRequestResponse();
        }

        public async Task<CreateRequestResponse> CreateRequestByConsultantAsync(CreateResquestConsultant req)
        {
            var entity = new order_request
            {
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                detail_address = req.detail_address,
                process_status = "Pending"
            };

            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return new CreateRequestResponse
            {
                order_request_id = entity.order_request_id
            };
        }
        public async Task<UpdateRequestResponse> UpdateAsync(int id, UpdateOrderRequest req)
        {
            var entity = await _requestRepo.GetByIdAsync(id);
            if (entity == null)
            {
                return new UpdateRequestResponse
                {
                    Success = false,
                    Message = "Order request not found",
                    UpdatedId = id
                };
            }

            entity.customer_name = req.customer_name ?? entity.customer_name;
            entity.customer_phone = req.customer_phone ?? entity.customer_phone;
            entity.customer_email = req.customer_email ?? entity.customer_email;
            entity.product_name = req.product_name ?? entity.product_name;
            entity.quantity = req.quantity ?? entity.quantity;
            entity.description = req.description ?? entity.description;
            entity.design_file_path = req.design_file_path ?? entity.design_file_path;
            entity.detail_address = req.detail_address ?? entity.detail_address;
            entity.delivery_date = ToUnspecified(req.delivery_date);
            entity.order_request_date = ToUnspecified(req.order_request_date);
            entity.product_type = req.product_type ?? entity.product_type;
            entity.number_of_plates = req.number_of_plates ?? entity.number_of_plates;
            entity.paper_code = req.paper_code ?? entity.paper_code;
            entity.paper_name = req.paper_name ?? entity.paper_name;
            entity.coating_type = req.coating_type ?? entity.coating_type;
            entity.wave_type = req.wave_type ?? entity.wave_type;
            entity.product_length_mm = req.product_length_mm ?? entity.product_length_mm;
            entity.product_width_mm = req.product_width_mm ?? entity.product_width_mm;
            entity.product_height_mm = req.product_height_mm ?? entity.product_height_mm;
            entity.glue_tab_mm = req.glue_tab_mm ?? entity.glue_tab_mm;
            entity.bleed_mm = req.bleed_mm ?? entity.bleed_mm;
            entity.is_one_side_box = req.is_one_side_box ?? entity.is_one_side_box;
            entity.print_width_mm = req.print_width_mm ?? entity.print_width_mm;
            entity.print_height_mm = req.print_height_mm ?? entity.print_height_mm;
            entity.is_send_design = req.is_send_design ?? entity.is_send_design;
            entity.production_processes = req.production_processes ?? entity.production_processes;
            entity.process_status = "Pending";

            await _requestRepo.UpdateAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return new UpdateRequestResponse
            {
                Success = true,
                Message = "Order request updated successfully",
                UpdatedId = id,
                UpdatedAt = DateTime.Now
            };
        }


        public async Task CancelAsync(int id, string? reason, CancellationToken ct = default)
        {
            var entity = await _requestRepo.GetByIdAsync(id);
            if (entity == null) return;
           
            if (entity.order_id != null)
                throw new InvalidOperationException("This request is already linked to an order, cannot cancel.");
            entity.reason = reason;
            await _requestRepo.CancelAsync(id, ct);
            await _requestRepo.SaveChangesAsync();
        }

        public Task<order_request?> GetByIdAsync(int id) => _requestRepo.GetByIdAsync(id);

        public Task<RequestWithCostDto?> GetByIdWithCostAsync(int id)
        {
            return _requestRepo.GetByIdWithCostAsync(id);
        }

        public async Task<PagedResultLite<RequestPagedDto>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _requestRepo.GetPagedAsync(skip, pageSize + 1);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<RequestPagedDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        public async Task<ConvertRequestToOrderResponse> ConvertToOrderAsync(int requestId)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                try
                {
                    var req = await _requestRepo.GetByIdAsync(requestId);
                    if (req == null)
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = "Order request not found",
                            RequestId = requestId
                        };
                    }

                    if (!string.Equals(req.process_status?.Trim(), "Accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = "Only process_status = 'Accepted' can be converted to order",
                            RequestId = requestId
                        };
                    }

                    if (req.quote_id == null)
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = "No quote found for this request",
                            RequestId = requestId
                        };
                    }

                    if (req.order_id != null || await _requestRepo.AnyOrderLinkedAsync(requestId))
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = true,
                            Message = "This request was already converted",
                            RequestId = requestId,
                            OrderId = req.order_id
                        };
                    }

                    var est = await _estimateRepo.GetByOrderRequestIdAsync(requestId);
                    if (est == null)
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = "Cost estimate not found for this request",
                            RequestId = requestId
                        };
                    }

                    // ===== map product_type_id from req.product_type (code) =====
                    var ptCode = (req.product_type ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(ptCode))
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = "order_request.product_type is missing, cannot map product_type_id",
                            RequestId = requestId
                        };
                    }

                    var productTypeId = await _db.product_types
                        .AsNoTracking()
                        .Where(x => x.code == ptCode)
                        .Select(x => (int?)x.product_type_id)
                        .FirstOrDefaultAsync();

                    if (productTypeId == null)
                    {
                        return new ConvertRequestToOrderResponse
                        {
                            Success = false,
                            Message = $"Product type code '{ptCode}' not found in product_types",
                            RequestId = requestId
                        };
                    }

                    // ✅ order.status luôn Scheduled, is_enough mới phản ánh đủ/thiếu NVL
                    var newOrder = new order
                    {
                        code = "TMP-ORD",
                        order_date = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
                        delivery_date = req.delivery_date,
                        status = "Scheduled",
                        payment_status = "Deposited",
                        quote_id = req.quote_id,
                        total_amount = est.final_total_cost,
                        is_enough = null,
                        is_buy = false
                    };

                    await _orderRepo.AddOrderAsync(newOrder);
                    await _orderRepo.SaveChangesAsync();

                    newOrder.code = $"ORD-{newOrder.order_id:00000}"; 
                    _orderRepo.Update(newOrder);

                    await _orderRepo.SaveChangesAsync();

                    // ===== order_item =====
                    var newItem = new order_item
                    {
                        order_id = newOrder.order_id,
                        product_name = req.product_name,
                        quantity = req.quantity ?? 0,
                        design_url = req.design_file_path,
                        product_type_id = productTypeId,
                        paper_code = req.paper_code,
                        production_process = req.production_processes,
                        paper_name = req.paper_name,
                        glue_type = req.coating_type,
                        wave_type = req.wave_type,
                        est_paper_sheets_total = est.sheets_total,
                        est_ink_weight_kg = est.ink_weight_kg,
                        est_coating_glue_weight_kg = est.coating_glue_weight_kg,
                        est_mounting_glue_weight_kg = est.mounting_glue_weight_kg,
                        est_lamination_weight_kg = est.lamination_weight_kg,
                        height_mm = req.product_height_mm,
                        length_mm = req.product_length_mm,
                        width_mm = req.product_width_mm
                    };

                    await _orderRepo.AddOrderItemAsync(newItem);
                    await _orderRepo.SaveChangesAsync(); 

                    // ===== BOM =====
                    material? paperMaterial = null;
                    if (!string.IsNullOrWhiteSpace(req.paper_code))
                        paperMaterial = await _materialRepo.GetByCodeAsync(req.paper_code!);

                    var qty = (decimal)(newItem.quantity <= 0 ? 1 : newItem.quantity);
                    var bomTasks = new List<Task>();

                    if (est.sheets_total > 0)
                        bomTasks.Add(_bomRepo.AddBomAsync(new bom
                        {
                            order_item_id = newItem.item_id,
                            material_id = paperMaterial?.material_id,
                            material_code = Trunc20(req.paper_code ?? "PAPER"),
                            material_name = req.paper_name ?? "Giấy",
                            unit = "tờ",
                            qty_total = est.sheets_total,
                            qty_per_product = est.sheets_total / qty,
                            source_estimate_id = est.estimate_id
                        }));

                    if (est.ink_weight_kg > 0)
                        bomTasks.Add(_bomRepo.AddBomAsync(new bom
                        {
                            order_item_id = newItem.item_id,
                            material_code = Trunc20("INK"),
                            material_name = "Mực in",
                            unit = "kg",
                            qty_total = est.ink_weight_kg,
                            qty_per_product = est.ink_weight_kg / qty,
                            source_estimate_id = est.estimate_id
                        }));

                    if (est.coating_glue_weight_kg > 0)
                        bomTasks.Add(_bomRepo.AddBomAsync(new bom
                        {
                            order_item_id = newItem.item_id,
                            material_code = Trunc20(req.coating_type ?? "COATING_GLUE"),
                            material_name = "Keo phủ",
                            unit = "kg",
                            qty_total = est.coating_glue_weight_kg,
                            qty_per_product = est.coating_glue_weight_kg / qty,
                            source_estimate_id = est.estimate_id
                        }));

                    if (est.mounting_glue_weight_kg > 0)
                    {
                        var codeBoi = string.IsNullOrWhiteSpace(req.wave_type) ? "MOUNTING_GLUE" : $"BOI_{req.wave_type}";
                        bomTasks.Add(_bomRepo.AddBomAsync(new bom
                        {
                            order_item_id = newItem.item_id,
                            material_code = Trunc20(codeBoi),
                            material_name = "Keo bồi",
                            unit = "kg",
                            qty_total = est.mounting_glue_weight_kg,
                            qty_per_product = est.mounting_glue_weight_kg / qty,
                            source_estimate_id = est.estimate_id
                        }));
                    }

                    if (est.lamination_weight_kg > 0)
                        bomTasks.Add(_bomRepo.AddBomAsync(new bom
                        {
                            order_item_id = newItem.item_id,
                            material_code = Trunc20("LAMINATION"),
                            material_name = "Màng cán",
                            unit = "kg",
                            qty_total = est.lamination_weight_kg,
                            qty_per_product = est.lamination_weight_kg / qty,
                            source_estimate_id = est.estimate_id
                        }));

                    await Task.WhenAll(bomTasks);
                    await _bomRepo.SaveChangesAsync();

                    var bomHasNullMaterial = await (
                        from oi in _db.order_items.AsNoTracking()
                        join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                        where oi.order_id == newOrder.order_id
                        select b.material_id
                    ).AnyAsync(x => x == null);

                    bool isEnough;
                    if (bomHasNullMaterial)
                    {
                        isEnough = false;
                    }
                    else
                    {
                        isEnough = await (
                            from oi in _db.order_items.AsNoTracking()
                            join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                            join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                            where oi.order_id == newOrder.order_id
                            group new { oi, b, m } by b.material_id into g
                            select new
                            {
                                Required = g.Sum(x =>
                                    ((decimal)x.oi.quantity)
                                    * (x.b.qty_per_product ?? 0m)
                                    * (1m + ((x.b.wastage_percent ?? 0m) / 100m))
                                ),
                                StockQty = g.Max(x => x.m.stock_qty ?? 0m)
                            }
                        ).AllAsync(x => x.StockQty >= x.Required);
                    }

                    newOrder.is_enough = isEnough;
                    _orderRepo.Update(newOrder);
                    await _orderRepo.SaveChangesAsync();

                    // ===== ✅ auto create production Scheduled =====
                    var managerId = await _db.users
    .AsNoTracking()
    .Where(u => u.username == "manager")
    .Select(u => (int?)u.user_id)
    .FirstOrDefaultAsync();

                    managerId ??= newOrder.consultant_id;

                    var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

                    var prod = new production
                    {
                        code = "TMP-PROD",
                        order_id = newOrder.order_id,
                        status = "Scheduled",
                        product_type_id = productTypeId,

                        manager_id = managerId,
                        start_date = now
                    };

                    await _db.productions.AddAsync(prod);
                    await _db.SaveChangesAsync(); // lấy prod_id

                    prod.code = $"PROD-{prod.prod_id:00000}"; // <= 20
                    _db.productions.Update(prod);
                    await _db.SaveChangesAsync();

                    // Link order.production_id (entity order đã có field sẵn)
                    newOrder.production_id = prod.prod_id;
                    _orderRepo.Update(newOrder);
                    await _orderRepo.SaveChangesAsync();

                    // ===== link request -> order =====
                    req.order_id = newOrder.order_id;
                    await _requestRepo.UpdateAsync(req);
                    await _requestRepo.SaveChangesAsync();

                    await tx.CommitAsync();

                    return new ConvertRequestToOrderResponse
                    {
                        Success = true,
                        Message = "Converted order request to order successfully",
                        RequestId = requestId,
                        OrderId = newOrder.order_id,
                        OrderItemId = newItem.item_id,
                        OrderCode = newOrder.code
                    };
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            });
        }
        public async Task<RequestDetailDto?> GetInformationRequestById(int requestId, CancellationToken ct = default)
        {
            return await _requestRepo.GetInformationRequestById(requestId, ct);
        }

        public Task<PagedResultLite<RequestSortedDto>> GetSortedByQuantityPagedAsync(
            bool ascending, int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetSortedByQuantityPagedAsync(ascending, page, pageSize, ct);

        public Task<PagedResultLite<RequestSortedDto>> GetSortedByDatePagedAsync(
            bool ascending, int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetSortedByDatePagedAsync(ascending, page, pageSize, ct);

        public Task<PagedResultLite<RequestSortedDto>> GetSortedByDeliveryDatePagedAsync(
            bool nearestFirst, int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetSortedByDeliveryDatePagedAsync(nearestFirst, page, pageSize, ct);

        public Task<PagedResultLite<RequestEmailStatsDto>> GetEmailsByAcceptedCountPagedAsync(
            int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetEmailsByAcceptedCountPagedAsync(page, pageSize, ct);

        public Task<PagedResultLite<RequestStockCoverageDto>> GetSortedByStockCoveragePagedAsync(
            int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetSortedByStockCoveragePagedAsync(page, pageSize, ct);

        public Task<PagedResultLite<RequestSortedDto>> GetByOrderRequestDatePagedAsync(
            DateOnly date, int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.GetByOrderRequestDatePagedAsync(date, page, pageSize, ct);

        public Task<PagedResultLite<RequestSortedDto>> SearchPagedAsync(
            string keyword, int page, int pageSize, CancellationToken ct = default)
            => _requestRepo.SearchPagedAsync(keyword, page, pageSize, ct);

        public async Task<int> CreateOrderRequestAsync(CreateOrderRequestDto dto, CancellationToken ct = default)
        {
            if (dto.quantity is null or <= 0)
                throw new ArgumentException("quantity must be > 0");
            if (string.IsNullOrWhiteSpace(dto.product_name))
                throw new ArgumentException("product_name is required");

            var now = DateTime.UtcNow;

            var entity = new order_request
            {
                customer_name = dto.customer_name?.Trim(),
                customer_phone = dto.customer_phone?.Trim(),
                customer_email = dto.customer_email?.Trim(),

                delivery_date = ToUnspecified(dto.delivery_date),
                detail_address = dto.detail_address?.Trim(),

                product_name = dto.product_name?.Trim(),
                quantity = dto.quantity,
                description = dto.description,

                design_file_path = dto.design_file_path,
                is_send_design = dto.is_send_design,

                product_type = dto.product_type?.Trim(),
                number_of_plates = dto.number_of_plates,
                production_processes = dto.production_processes?.Trim(),
                coating_type = dto.coating_type?.Trim(),
                paper_code = dto.paper_code?.Trim(),
                paper_name = dto.paper_name?.Trim(),
                wave_type = dto.wave_type?.Trim(),

                product_length_mm = dto.product_length_mm,
                product_width_mm = dto.product_width_mm,
                product_height_mm = dto.product_height_mm,
                glue_tab_mm = dto.glue_tab_mm,
                bleed_mm = dto.bleed_mm,
                is_one_side_box = dto.is_one_side_box,

                print_width_mm = dto.print_width_mm,
                print_height_mm = dto.print_height_mm,

                order_request_date = ToUnspecified(now),
                process_status = "Pending"
            };

            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return entity.order_request_id;
        }

        public async Task<OrderRequestDesignFileResponse?> GetDesignFileAsync(int orderRequestId, CancellationToken ct = default)
        {
            var path = await _requestRepo.GetDesignFilePathAsync(orderRequestId, ct);

            if (path == null)
                return null;

            return new OrderRequestDesignFileResponse
            {
                order_request_id = orderRequestId,
                design_file_path = path
            };
        }

        public async Task UpdateDesignFilePathAsync(int orderRequestId, string designFilePath, CancellationToken ct = default)
        {
            var entity = await _requestRepo.GetByIdAsync(orderRequestId);
            if (entity == null)
                throw new Exception("Order request not found");

            entity.design_file_path = designFilePath;
            entity.is_send_design = true;

            await _requestRepo.UpdateAsync(entity);
            await _requestRepo.SaveChangesAsync();
        }
    }
}
