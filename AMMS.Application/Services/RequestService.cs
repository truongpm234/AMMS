using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
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

        public async Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req)
        {
            var entity = new order_request
            {
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                delivery_date = req.delivery_date,
                product_name = req.product_name,
                quantity = req.quantity,
                description = req.description,
                design_file_path = req.design_file_path,
                order_request_date = req.order_request_date,
                province = req.province,
                district = req.district,
                detail_address = req.detail_address
            };

            await _repo.AddAsync(entity);
            await _repo.SaveChangesAsync();

            return new CreateCustomerOrderResponse();
        }

        //public async Task UpdateAsync(int id, CreateCustomerOrderResquest req)
        //{
        //    var entity = await _repo.GetByIdAsync(id);
        //    if (entity == null)
        //        throw new KeyNotFoundException("Order request not found");

        //    entity.customer_name = req.customer_name;
        //    entity.customer_phone = req.customer_phone;
        //    entity.customer_email = req.customer_email;
        //    entity.product_name = req.product_name;
        //    entity.quantity = req.quantity;
        //    entity.description = req.description;
        //    entity.design_file_path = req.design_file_path;
        //    entity.delivery_date = DateTime.SpecifyKind(req.delivery_date.Value, DateTimeKind.Unspecified);
        //    entity.order_request_date = DateTime.SpecifyKind(req.order_request_date.Value, DateTimeKind.Unspecified);

        //    _repo.Update(entity);
        //    await _repo.SaveChangesAsync();
        //}

        //public async Task DeleteAsync(int id)
        //{
        //    await _repo.DeleteAsync(id);
        //    await _repo.SaveChangesAsync();
        //}
    }
}
