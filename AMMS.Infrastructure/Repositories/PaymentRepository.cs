using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.PayOS;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class PaymentRepository : IPaymentRepository
    {
        private readonly AppDbContext _db;
        public PaymentRepository(AppDbContext db) => _db = db;

        public Task AddAsync(payment entity, CancellationToken ct = default)
            => _db.payments.AddAsync(entity, ct).AsTask();
        public Task<payment?> GetPaidByProviderOrderCodeAsync(string provider, long orderCode, CancellationToken ct = default)
            => _db.payments.AsNoTracking()
                .FirstOrDefaultAsync(x => x.provider == provider && x.order_code == orderCode && x.status == "PAID", ct);

        public Task<bool> IsPaidAsync(int orderRequestId, CancellationToken ct = default)
            => _db.payments.AsNoTracking()
                .AnyAsync(x => x.order_request_id == orderRequestId && x.status == "PAID", ct);

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);
        public Task<payment?> GetLatestByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return _db.payments
                .AsNoTracking()
                .Where(p => p.order_request_id == orderRequestId && p.provider == "PAYOS")
                .OrderByDescending(p => p.paid_at ?? DateTime.MinValue)
                .ThenByDescending(p => p.payment_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<payment?> GetLatestPendingByRequestIdAsync(int requestId, CancellationToken ct)
        {
            return await _db.payments
                .Where(x => x.provider == "PAYOS" && x.order_request_id == requestId && x.status == "PENDING")
                .OrderByDescending(x => x.created_at)
                .FirstOrDefaultAsync(ct);
        }

        public async Task UpsertPendingAsync(payment p, CancellationToken ct)
        {
            var existing = await _db.payments
                .FirstOrDefaultAsync(x => x.provider == p.provider && x.order_code == p.order_code, ct);

            if (existing == null)
            {
                await _db.payments.AddAsync(p, ct);
            }
            else
            {
                // update snapshot create-link nếu muốn refresh
                existing.status = "PENDING";
                existing.amount = p.amount;
                existing.payos_payment_link_id = p.payos_payment_link_id;
                existing.payos_raw = p.payos_raw;
                existing.updated_at = p.updated_at;
            }
        }


    }
}
