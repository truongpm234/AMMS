using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Requests;

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

            await _repo.AddAsync(entity);
            await _repo.SaveChangesAsync();

            return new CreateRequestResponse();
        }

        public async Task<UpdateRequestResponse> UpdateAsync(int id, UpdateOrderRequest req)
        {
            var entity = await _repo.GetByIdAsync(id);
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
            await _repo.UpdateAsync(entity);
            await _repo.SaveChangesAsync();

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

            await _repo.DeleteAsync(id);
            await _repo.SaveChangesAsync();
        }
        public Task<order_request?> GetByIdAsync(int id) => _repo.GetByIdAsync(id);
        public async Task<PagedResultLite<order_request>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            // lấy dư 1 record để biết có trang sau
            var list = await _repo.GetPagedAsync(skip, pageSize + 1);

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
    }
}