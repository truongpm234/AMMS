using AMMS.Domain;
using AMMS.Infrastructure.DBContext;
using DomainEntity = AMMS.Domain.Entities.order_request;
using InfraEntity = AMMS.Infrastructure.Entities.order_request;

namespace AMMS.Infrastructure.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly AppDbContext _db;

        public OrderRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(DomainEntity entity)
        {
            await _db.order_requests.AddAsync(ToInfra(entity));
        }

        public void Update(DomainEntity entity)
        {
            _db.order_requests.Update(ToInfra(entity));
        }

        public async Task DeleteAsync(int id)
        {
            var infra = await _db.order_requests.FindAsync(id);
            if (infra != null) _db.order_requests.Remove(infra);
        }

        public Task<int> SaveChangesAsync() => _db.SaveChangesAsync();

        private static InfraEntity ToInfra(DomainEntity d) => new()
        {
            order_request_id = d.order_request_id,
            customer_name = d.customer_name,
            customer_phone = d.customer_phone,
            customer_email = d.customer_email,
            delivery_date = d.delivery_date,
            product_name = d.product_name,
            quantity = d.quantity,
            description = d.description,
            design_file_path = d.design_file_path,
            order_request_date = d.order_request_date
        };
    }
}
