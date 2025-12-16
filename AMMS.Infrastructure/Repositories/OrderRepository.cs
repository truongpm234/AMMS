using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly AppDbContext _db;
        public OrderRepository(AppDbContext db)
        {
            _db = db;
        }
        public async Task AddAsync(order entity)
        {
            await _db.orders.AddAsync(entity);
        }
        public void Update(order entity)
        {
            _db.orders.Update(entity);
        }
        public async Task<order?> GetByIdAsync(int id)
        {
            return await _db.orders.FindAsync(id);
        }
        public async Task DeleteAsync(int id)
        {
            var order = await GetByIdAsync(id);
            if (order != null)
            {
                _db.orders.Remove(order);
            }
        }
        public async Task<int> SaveChangesAsync()
        {
            return await _db.SaveChangesAsync();
        }
    }
}
