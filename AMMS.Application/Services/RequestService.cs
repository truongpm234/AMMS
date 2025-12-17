using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class RequestService : IRequestService
    {
        private readonly IRequestRepository _repo;

        public RequestService(IRequestRepository repo)
        {
            _repo = repo;
        }

        private DateTime? ToUnspecified(DateTime? dateTime)
        {
            if (!dateTime.HasValue) return null;
            return DateTime.SpecifyKind(dateTime.Value, DateTimeKind.Unspecified);
        }

        public async Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req)
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
                detail_address = req.detail_address
            };

            await _repo.AddAsync(entity);
            await _repo.SaveChangesAsync();

            return new CreateCustomerOrderResponse();
        }

        private DateTime? Normalize(DateTime? dt)
        {
            if (!dt.HasValue) return null;
            return DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified);
        }

        public async Task<UpdateOrderRequestResponse> UpdateAsync(int id, UpdateOrderRequest req)
        {
            var entity = await _repo.GetByIdAsync(id);
            if (entity == null)
            {
                return new UpdateOrderRequestResponse
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
            await _repo.UpdateAsync(entity);
            await _repo.SaveChangesAsync();

            return new UpdateOrderRequestResponse
            {
                Success = true,
                Message = "Order request updated successfully",
                UpdatedId = id,
                UpdatedAt = DateTime.Now
            };
        }

        public async Task DeleteAsync(int id)
        {
            await _repo.DeleteAsync(id);
            await _repo.SaveChangesAsync();
        }
        public Task<order_request?> GetByIdAsync(int id)
            => _repo.GetByIdAsync(id);
    }
}