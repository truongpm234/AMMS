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

        public async Task<OrderDetailDto?> GetDetailByIdAsync(int orderId, CancellationToken ct = default)
        {
            // Lấy order + items + productions (manager)
            var order = await _db.orders
                .AsNoTracking()
                .Include(o => o.order_items)
                .Include(o => o.productions)
                    .ThenInclude(p => p.manager)
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null) return null;

            // Lấy order_request (nếu có) – để lấy email, phone, note, product_name, quantity
            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.order_id == orderId, ct);

            // Lấy cost_estimate theo order_request (nếu có)
            cost_estimate? estimate = null;
            if (req != null)
            {
                estimate = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.order_request_id == req.order_request_id, ct);
            }

            // Chọn 1 item chính của đơn (nếu có)
            var item = order.order_items
                .OrderBy(i => i.item_id)
                .FirstOrDefault();

            // ================= MAP FIELD THEO MÀN HÌNH =================

            // Khách hàng
            // Ưu tiên: company_name từ customers, nếu không thì dùng customer_name từ order_request
            string customerName = string.Empty;
            string? customerEmail = null;
            string? customerPhone = null;

            if (order.customer_id.HasValue)
            {
                var customer = await _db.customers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.customer_id == order.customer_id.Value, ct);

                if (customer != null)
                {
                    customerName = customer.company_name
                                   ?? customer.contact_name
                                   ?? customerName;
                    customerEmail = customer.email;
                    customerPhone = customer.phone;
                }
            }

            // Nếu vẫn trống thì fallback từ order_request
            if (req != null)
            {
                if (string.IsNullOrWhiteSpace(customerName))
                    customerName = req.customer_name;
                customerEmail ??= req.customer_email;
                customerPhone ??= req.customer_phone;
            }

            // Sản phẩm + số lượng
            var productName = item?.product_name
                              ?? req?.product_name
                              ?? string.Empty;

            var quantity = item?.quantity
                           ?? req?.quantity
                           ?? 0;

            // Lịch sản xuất (lấy min start_date & max end_date của tất cả productions của order)
            DateTime? prodStart = order.productions
                .Select(p => p.start_date)
                .Where(d => d != null)
                .OrderBy(d => d)
                .FirstOrDefault();

            DateTime? prodEnd = order.productions
                .Select(p => p.end_date)
                .Where(d => d != null)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            // Người duyệt: lấy manager_full_name của production mới nhất
            string approverName = order.productions
                .OrderByDescending(p => p.start_date ?? p.end_date ?? order.order_date)
                .Select(p => p.manager != null ? p.manager.full_name : null)
                .FirstOrDefault()
                ?? "Chưa cập nhật";

            // Quy cách: ghép từ các field của order_item (nếu có)
            string? specification = null;
            if (item != null)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(item.finished_size))
                    parts.Add($"Thành phẩm: {item.finished_size}");
                if (!string.IsNullOrWhiteSpace(item.print_size))
                    parts.Add($"Khổ in: {item.print_size}");
                if (!string.IsNullOrWhiteSpace(item.paper_type))
                    parts.Add($"Giấy: {item.paper_type}");
                if (!string.IsNullOrWhiteSpace(item.colors))
                    parts.Add($"Màu: {item.colors}");

                if (parts.Count > 0)
                    specification = string.Join(" | ", parts);
            }

            // Ghi chú – hiện tại mình lấy từ order_request.description
            string? note = req?.description;

            // Tài chính
            decimal rushAmount = estimate?.rush_amount ?? 0m;
            decimal estimateTotal = estimate?.final_total_cost ?? order.total_amount ?? 0m;

            // File mẫu & hợp đồng – hiện bạn chưa có cột riêng => tạm thời null
            string? sampleFileUrl = item?.design_url;     // nếu FE muốn, có thể dùng làm file mẫu
            string? contractFileUrl = null;               // chưa có, sau này thêm cột thì map

            // ================= RETURN DTO =================
            return new OrderDetailDto
            {
                OrderId = order.order_id,
                Code = order.code,
                Status = order.status ?? "New",
                PaymentStatus = order.payment_status ?? "Unpaid",
                OrderDate = (DateTime)order.order_date,
                DeliveryDate = order.delivery_date,

                CustomerName = customerName,
                CustomerEmail = customerEmail,
                CustomerPhone = customerPhone,

                ProductName = productName,
                Quantity = quantity,

                ProductionStartDate = prodStart,
                ProductionEndDate = prodEnd,
                ApproverName = approverName,

                Specification = specification,
                Note = note,

                RushAmount = rushAmount,
                EstimateTotal = estimateTotal,

                SampleFileUrl = sampleFileUrl,
                ContractFileUrl = contractFileUrl
            };
        }
    }
}

