using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class SupplierRepository : ISupplierRepository
    {
        private readonly AppDbContext _db;

        public SupplierRepository(AppDbContext db)
        {
            _db = db;
        }

        public Task<int> CountAsync(CancellationToken ct = default)
            => _db.suppliers.AsNoTracking().CountAsync(ct);

        // ✅ Lấy danh sách supplier + materials có main_material_type trùng nhau
        public async Task<List<SupplierWithMaterialsDto>> GetPagedWithMaterialsAsync(
            int skip, int take, CancellationToken ct = default)
        {
            // 1) Lấy suppliers theo trang
            var suppliers = await _db.suppliers
                .AsNoTracking()
                .OrderBy(s => s.name)
                .Skip(skip)
                .Take(take)
                .Select(s => new SupplierWithMaterialsDto
                {
                    SupplierId = s.supplier_id,
                    Name = s.name,
                    ContactPerson = s.contact_person,
                    Phone = s.phone,
                    Email = s.email,
                    MainMaterialType = s.main_material_type
                })
                .ToListAsync(ct);

            if (!suppliers.Any())
                return suppliers;

            // 2) Lấy danh sách main_material_type của page hiện tại
            var types = suppliers
                .Select(s => s.MainMaterialType)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            if (!types.Any())
                return suppliers;

            // 3) Lấy tất cả materials có main_material_type thuộc danh sách trên
            var materials = await _db.materials
                .AsNoTracking()
                .Where(m => m.main_material_type != null && types.Contains(m.main_material_type))
                .Select(m => new
                {
                    m.material_id,
                    m.code,
                    m.name,
                    m.unit,
                    m.main_material_type
                })
                .ToListAsync(ct);

            // 4) Group theo main_material_type để gán vào từng supplier
            var matLookup = materials
                .GroupBy(m => m.main_material_type!)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new SupplierMaterialBasicDto
                    {
                        MaterialId = x.material_id,
                        Code = x.code,
                        Name = x.name,
                        Unit = x.unit,
                        MainMaterialType = x.main_material_type
                    }).ToList()
                );

            foreach (var s in suppliers)
            {
                if (!string.IsNullOrWhiteSpace(s.MainMaterialType)
                    && matLookup.TryGetValue(s.MainMaterialType!, out var list))
                {
                    s.Materials = list;
                }
            }

            return suppliers;
        }

        // 🔎 Chi tiết 1 supplier + lịch sử mua materials (theo purchase_items)
        public async Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            // 1) Supplier info
            var supplierInfo = await _db.suppliers
                .AsNoTracking()
                .Where(s => s.supplier_id == supplierId)
                .Select(s => new
                {
                    s.supplier_id,
                    s.name,
                    s.contact_person,
                    s.phone,
                    s.email,
                    s.main_material_type
                })
                .FirstOrDefaultAsync(ct);

            if (supplierInfo == null) return null;

            var skip = (page - 1) * pageSize;

            // 2) materials theo lịch sử mua: supplier -> purchases -> purchase_items -> materials
            var baseQuery =
                from p in _db.purchases.AsNoTracking()
                join pi in _db.purchase_items.AsNoTracking()
                    on p.purchase_id equals pi.purchase_id
                join m in _db.materials.AsNoTracking()
                    on pi.material_id equals m.material_id
                where p.supplier_id == supplierId
                group pi by new { m.material_id, m.code, m.name, m.unit } into g
                select new
                {
                    g.Key.material_id,
                    g.Key.code,
                    g.Key.name,
                    g.Key.unit,
                    TotalQty = g.Sum(x => x.qty_ordered) // decimal?
                };

            var orderedQuery = baseQuery
                .OrderByDescending(x => x.TotalQty)
                .ThenBy(x => x.name);

            var list = await orderedQuery
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(x => new SupplierMaterialDto(
                    x.material_id,
                    x.code,
                    x.name,
                    x.unit,
                    x.TotalQty ?? 0m
                ))
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            var pagedMaterials = new PagedResultLite<SupplierMaterialDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = list
            };

            return new SupplierDetailDto
            {
                SupplierId = supplierInfo.supplier_id,
                Name = supplierInfo.name,
                ContactPerson = supplierInfo.contact_person,
                Phone = supplierInfo.phone,
                Email = supplierInfo.email,
                MainMaterialType = supplierInfo.main_material_type,
                Materials = pagedMaterials
            };
        }
    }
}
