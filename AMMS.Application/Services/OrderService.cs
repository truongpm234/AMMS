using AMMS.Application.Interfaces;
using AMMS.Domain;
using AMMS.Domain.Entities;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _repo;

        public OrderService(IOrderRepository repo)
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
                delivery_date = DateTime.SpecifyKind(req.delivery_date, DateTimeKind.Utc),
                product_name = req.product_name,
                quantity = req.quantity,
                description = req.description,
                design_file_path = req.design_file_path,
                order_request_date = DateTime.UtcNow
            };

            await _repo.AddAsync(entity);
            await _repo.SaveChangesAsync();

            // ✅ CHỈ TRẢ MESSAGE
            return new CreateCustomerOrderResponse();
        }


        public async Task UpdateAsync(int id, CreateCustomerOrderResquest req)
        {
            var entity = new order_request
            {
                order_request_id = id,
                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                delivery_date = DateTime.SpecifyKind(req.delivery_date, DateTimeKind.Utc),
                product_name = req.product_name,
                quantity = req.quantity,
                description = req.description,
                design_file_path = req.design_file_path
            };

            _repo.Update(entity);
            await _repo.SaveChangesAsync();
        }


        public async Task DeleteAsync(int id)
        {
            await _repo.DeleteAsync(id);
            await _repo.SaveChangesAsync();
        }
    }
}
