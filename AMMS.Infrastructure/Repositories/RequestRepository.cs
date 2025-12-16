using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class RequestRepository : IRequestRepository
    {
        private readonly AppDbContext _db;

        public RequestRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(order_request entity)
        {
            await _db.order_requests.AddAsync(entity);
        }
        public void Update(order_request entity)
        {
            _db.order_requests.Update(entity);
        }

        public async Task<order_request?> GetByIdAsync(int id)
        {
            return await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == id);
        }
        public async Task DeleteAsync(int id)
        {
            var entity = new order_request { order_request_id = id };
            _db.Attach(entity);
            _db.order_requests.Remove(entity);
        }


        public async Task<int> SaveChangesAsync()
        {
            return await _db.SaveChangesAsync();
        }
    }
}
