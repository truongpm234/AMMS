using AMMS.Shared.DTOs.Orders;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.DBContext;          
using Microsoft.EntityFrameworkCore;
using AMMS.Domain;

namespace AMMS.Infrastructure.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly AppDbContext _db;

        public OrderRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<CreateCustomerOrderResponse> CreateCustomerOrderAsync(CreateCustomerOrderRequest req)
        {
            // Validate
            if (string.IsNullOrWhiteSpace(req.CustomerName))
                throw new ArgumentException("Customer name is required");
            if (string.IsNullOrWhiteSpace(req.CustomerPhone))
                throw new ArgumentException("Customer phone is required");
            if (string.IsNullOrWhiteSpace(req.ProductName))
                throw new ArgumentException("Product name is required");
            if (req.Quantity <= 0)
                throw new ArgumentException("Quantity must be > 0");

            // 1) Find/Create customer (phone ưu tiên, fallback email)
            var customer = await _db.customers.FirstOrDefaultAsync(c =>
                c.phone == req.CustomerPhone ||
                (!string.IsNullOrEmpty(req.CustomerEmail) && c.email == req.CustomerEmail));

            if (customer == null)
            {
                customer = new customer
                {
                    contact_name = req.CustomerName,
                    phone = req.CustomerPhone,
                    email = req.CustomerEmail
                };

                _db.customers.Add(customer);
                await _db.SaveChangesAsync();
            }

            // 2) Create order
            var code = $"ORD-{DateTime.UtcNow:yyMMddHHmmss}";

            var orderEntity = new order
            {
                code = code,
                customer_id = customer.customer_id,
                order_date = DateTime.UtcNow,
                delivery_date = req.DeliveryDate,
                status = "New",
                payment_status = "Unpaid"
            };

            _db.orders.Add(orderEntity);
            await _db.SaveChangesAsync();

            // 3) Create order_item
            var item = new order_item
            {
                order_id = orderEntity.order_id,
                product_name = req.ProductName,
                quantity = req.Quantity,
                post_processing = req.Description,
                design_url = req.DesignFileUrl
            };

            _db.order_items.Add(item);
            await _db.SaveChangesAsync();

            return new CreateCustomerOrderResponse
            {
                Message = "Create order successfully"
            };
        }
    }
}
