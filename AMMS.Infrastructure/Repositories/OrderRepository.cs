using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.EntityFrameworkCore;
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
        public async Task AddOrderAsync(order entity)
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
        public Task<int> CountAsync()
        {
            return _db.orders.AsNoTracking().CountAsync();
        }

        public Task<List<OrderListDto>> GetPagedAsync(int skip, int take)
        {
            return _db.orders
                .AsNoTracking()
                .OrderByDescending(o => o.order_date)
                .Skip(skip)
                .Take(take)
                .Select(o => new OrderListDto
                {
                    OrderId = o.order_id,
                    Code = o.code,
                    OrderDate = o.order_date,
                    DeliveryDate = o.delivery_date,
                    Status = o.status,
                    PaymentStatus = o.payment_status,
                    QuoteId = o.quote_id,
                    TotalAmount = o.total_amount
                })
                .ToListAsync();
        }
        public async Task<order?> GetByCodeAsync(string code)
        {
            return await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.code == code);
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
        public Task AddOrderItemAsync(order_item entity) => _db.order_items.AddAsync(entity).AsTask();
        public async Task<string> GenerateNextOrderCodeAsync()
        {
            var last = await _db.orders.AsNoTracking()
                .OrderByDescending(x => x.order_id)
                .Select(x => x.code)
                .FirstOrDefaultAsync();

            int nextNum = 1;
            if (!string.IsNullOrWhiteSpace(last))
            {
                var digits = new string(last.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n)) nextNum = n + 1;
            }

            return $"ORD-{nextNum:00}";
        }
    }
}

