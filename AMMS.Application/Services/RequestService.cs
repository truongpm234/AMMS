using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Requests;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        public RequestService(IRequestRepository requestRepo, IOrderRepository orderRepo, ICostEstimateRepository estimateRepo, IMaterialRepository materialRepo, IBomRepository bomRepo, AppDbContext db)
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
                province = req.province,
                district = req.district,
                detail_address = req.detail_address,
                process_status = "Pending"
            };

            await _requestRepo.AddAsync(entity);
            await _requestRepo.SaveChangesAsync();

            return new CreateRequestResponse();
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
            entity.province = req.province ?? entity.province;
            entity.district = req.district ?? entity.district;
            entity.detail_address = req.detail_address ?? entity.detail_address;
            entity.delivery_date = ToUnspecified(req.delivery_date);
            entity.order_request_date = ToUnspecified(req.order_request_date);
            entity.product_type = req.product_type ?? entity.product_type;
            entity.process_status = "Verified";

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

        public async Task DeleteAsync(int id)
        {
            await _requestRepo.DeleteAsync(id);
            await _requestRepo.SaveChangesAsync();
        }

        public Task<order_request?> GetByIdAsync(int id) => _requestRepo.GetByIdAsync(id);

        public async Task<PagedResultLite<order_request>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _requestRepo.GetPagedAsync(skip, pageSize + 1);

            var hasNext = list.Count > pageSize;
            var data = list.Take(pageSize).ToList();

            return new PagedResultLite<order_request>
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

                    var hasEnoughStock = await _requestRepo.HasEnoughStockForRequestAsync(requestId);
                    var orderStatus = hasEnoughStock ? "Scheduled" : "Not enough";

                    // 1) Create order with TEMP unique code (GUID)
                    var newOrder = new order
                    {
                        code = "TMP-" + Guid.NewGuid().ToString("N"),
                        order_date = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
                        delivery_date = req.delivery_date,
                        status = orderStatus,
                        payment_status = "Unpaid",
                        quote_id = req.quote_id,
                        total_amount = est.final_total_cost
                    };

                    await _orderRepo.AddOrderAsync(newOrder);
                    await _orderRepo.SaveChangesAsync(); // get order_id

                    // 2) Update code based on order_id (unique)
                    newOrder.code = $"ORD-{newOrder.order_id:00000}";
                    await _orderRepo.SaveChangesAsync();

                    // 3) Create order_item
                    var newItem = new order_item
                    {
                        order_id = newOrder.order_id,
                        product_name = req.product_name,
                        quantity = req.quantity ?? 0,
                        design_url = req.design_file_path,
                        paper_code = req.paper_code,
                        paper_name = req.paper_name,
                        glue_type = req.coating_type,
                        wave_type = req.wave_type,
                        est_paper_sheets_total = est.sheets_total,
                        est_ink_weight_kg = est.ink_weight_kg,
                        est_coating_glue_weight_kg = est.coating_glue_weight_kg,
                        est_mounting_glue_weight_kg = est.mounting_glue_weight_kg,
                        est_lamination_weight_kg = est.lamination_weight_kg
                    };

                    await _orderRepo.AddOrderItemAsync(newItem);
                    await _orderRepo.SaveChangesAsync(); // get item_id

                    // 4) BOM
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
                            material_code = req.paper_code ?? "PAPER",
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
                            material_code = "INK",
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
                            material_code = req.coating_type ?? "COATING_GLUE",
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
                            material_code = codeBoi,
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
                            material_code = "LAMINATION",
                            material_name = "Màng cán",
                            unit = "kg",
                            qty_total = est.lamination_weight_kg,
                            qty_per_product = est.lamination_weight_kg / qty,
                            source_estimate_id = est.estimate_id
                        }));

                    await Task.WhenAll(bomTasks);
                    await _bomRepo.SaveChangesAsync();

                    // 5) Link request -> order
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
        public async Task<OrderRequestDesignFileResponse?> GetDesignFileAsync(int orderRequestId, CancellationToken ct = default)
        {
            var path = await _requestRepo.GetDesignFilePathAsync(orderRequestId, ct);

            if (path == null)
            {
                return null;
            }

            return new OrderRequestDesignFileResponse
            {
                order_request_id = orderRequestId,
                design_file_path = path
            };
        }
    }
}
