using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class PurchaseService : IPurchaseService
    {
        private readonly IPurchaseRepository _repo;

        public PurchaseService(IPurchaseRepository repo)
        {
            _repo = repo;
        }

        private DateTime? ToUnspecified(DateTime? dt)
            => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified) : null;

        public async Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(
            CreatePurchaseRequestDto dto,
            int? createdBy,
            CancellationToken ct = default)
        {
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            foreach (var i in dto.Items)
            {
                if (i.MaterialId <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.Quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                var exists = await _repo.MaterialExistsAsync(i.MaterialId, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.MaterialId} not found");
            }

            var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

            var p = new purchase
            {
                code = code,
                supplier_id = dto.SupplierId,          
                created_by = createdBy,                
                status = "Pending",                   
                eta_date = ToUnspecified(dto.EtaDate),
                created_at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
            };

            await _repo.AddPurchaseAsync(p, ct);
            await _repo.SaveChangesAsync(ct); 

            var items = dto.Items.Select(x => new purchase_item
            {
                purchase_id = p.purchase_id,
                material_id = x.MaterialId,
                qty_ordered = x.Quantity,
                price = x.Price
            }).ToList();

            await _repo.AddPurchaseItemsAsync(items, ct);
            await _repo.SaveChangesAsync(ct);

            return new CreatePurchaseRequestResponse(
                p.purchase_id,
                p.code,
                p.status ?? "Pending",
                p.created_at
            );
        }

        public async Task<PagedResultLite<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(int page, int pageSize, CancellationToken ct = default)
        {
            return await _repo.GetPurchaseOrdersAsync(page, pageSize, ct);
        }

        public async Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(
            CreatePurchaseRequestDto dto,
            CancellationToken ct = default)
        {
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            if (dto.SupplierId.HasValue)
            {
                var supplierOk = await _repo.SupplierExistsAsync(dto.SupplierId.Value, ct);
                if (!supplierOk) throw new ArgumentException($"SupplierId {dto.SupplierId.Value} not found");
            }

            foreach (var i in dto.Items)
            {
                if (i.MaterialId <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.Quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                var exists = await _repo.MaterialExistsAsync(i.MaterialId, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.MaterialId} not found");
            }

            var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

            // ✅ set cứng người đặt
            const string createdByName = "manager";
            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found. Please create it first.");

            var p = new purchase
            {
                code = code,
                supplier_id = dto.SupplierId,
                created_by = managerId, 
                status = "Pending",
                eta_date = ToUnspecified(dto.EtaDate),
                created_at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
            };

            await _repo.AddPurchaseAsync(p, ct);
            await _repo.SaveChangesAsync(ct); // có purchase_id

            var items = dto.Items.Select(x => new purchase_item
            {
                purchase_id = p.purchase_id,
                material_id = x.MaterialId,
                qty_ordered = x.Quantity,
                price = x.Price
            }).ToList();

            await _repo.AddPurchaseItemsAsync(items, ct);
            await _repo.SaveChangesAsync(ct);

            // ✅ tổng SL
            var totalQty = dto.Items.Sum(x => (decimal)x.Quantity);

            // ✅ tên NCC
            var supplierName = await _repo.GetSupplierNameAsync(dto.SupplierId, ct) ?? "N/A";

            return new PurchaseOrderListItemDto(
                p.purchase_id,
                p.code,
                supplierName,
                p.created_at,
                createdByName,
                totalQty
            );
        }

        public async Task<object> ReceiveAllPendingPurchasesAsync(CancellationToken ct = default)
        {
            // set cứng manager
            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found. Please create it first.");

            return await _repo.ReceiveAllPendingPurchasesAsync(managerId.Value, ct);
        }

    }
}
