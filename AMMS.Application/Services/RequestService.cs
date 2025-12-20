using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Requests;

namespace AMMS.Application.Services
{
    public class RequestService : IRequestService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly IOrderRepository _orderRepo;

        public RequestService(IRequestRepository requestRepo, IOrderRepository orderRepo)
        {
            _requestRepo = requestRepo;
            _orderRepo = orderRepo;
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

            // Update fields
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
            entity.order_request_date = ToUnspecified(req.delivery_date);
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

            // lấy dư 1 record để biết có trang sau
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

            // chỉ accept mới convert
            if (!string.Equals(req.process_status?.Trim(), "accepted", StringComparison.OrdinalIgnoreCase))
            {
                return new ConvertRequestToOrderResponse
                {
                    Success = false,
                    Message = "Only process_status = 'accepted' can be converted to order",
                    RequestId = requestId
                };
            }

            // chống tạo trùng
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

            // tạo order
            var code = await _orderRepo.GenerateNextOrderCodeAsync();
            var newOrder = new order
            {
                code = code,
                order_date = DateTime.Now,
                delivery_date = req.delivery_date,
                status = "New",
                payment_status = "Unpaid",
                // customer_id/consultant_id/quote_id nếu chưa có thì để null
            };

            await _orderRepo.AddOrderAsync(newOrder);
            await _orderRepo.SaveChangesAsync(); // để lấy order_id

            // tạo order_item (1 sp / 1 đơn)
            var newItem = new order_item
            {
                order_id = newOrder.order_id,
                product_name = req.product_name,
                quantity = (int)req.quantity,
                design_url = req.design_file_path,
                // các field khác bạn có thể map thêm nếu UI có
                // product_type_id = ...
            };

            await _orderRepo.AddOrderItemAsync(newItem);

            // đánh dấu request đã convert
            req.order_id = newOrder.order_id;
            await _requestRepo.UpdateAsync(req);

            // save chung
            await _orderRepo.SaveChangesAsync();
            await _requestRepo.SaveChangesAsync();

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
    }
}