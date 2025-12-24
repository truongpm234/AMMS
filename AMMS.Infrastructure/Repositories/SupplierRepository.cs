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
                    supplier_id = s.supplier_id,
                    name = s.name,
                    contact_person = s.contact_person,
                    phone = s.phone,
                    email = s.email,
                    main_material_type = s.main_material_type
                })
                .ToListAsync(ct);

            if (!suppliers.Any())
                return suppliers;

            // 2) Lấy danh sách main_material_type của page hiện tại
            var types = suppliers
                .Select(s => s.main_material_type)
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
                if (!string.IsNullOrWhiteSpace(s.main_material_type)
                    && matLookup.TryGetValue(s.main_material_type!, out var list))
                {
                }
            }

            return suppliers;
        }

        public async Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
    int supplierId, int page, int pageSize, CancellationToken ct = default)
        {
            page = page <= 0 ? 1 : page;
            pageSize = pageSize <= 0 ? 10 : pageSize;

            var supplier = await _db.suppliers.AsNoTracking()
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

            if (supplier == null) return null;

            // ✅ Query base: select ra anonymous trước (EF dịch SQL được)
            var baseQuery =
                from sm in _db.supplier_materials.AsNoTracking()
                where sm.supplier_id == supplierId
                join m in _db.materials.AsNoTracking()
                    on sm.material_id equals m.material_id
                select new
                {
                    m.material_id,
                    m.code,
                    m.name,
                    m.unit,
                    sm.is_active,
                    sm.note
                };

            var totalCount = await baseQuery.CountAsync(ct);

            // ✅ OrderBy trước, rồi mới Select DTO
            var items = await baseQuery
                .OrderBy(x => x.name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new SupplierMaterialDto(
                    x.material_id,
                    x.code,
                    x.name,
                    x.unit,
                    x.is_active,
                    x.note
                ))
                .ToListAsync(ct);

            return new SupplierDetailDto
            {
                supplier_id = supplier.supplier_id,
                name = supplier.name,
                contact_person = supplier.contact_person,
                phone = supplier.phone,
                email = supplier.email,
                main_material_type = supplier.main_material_type,
                Materials = new PagedResultLite<SupplierMaterialDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = (page * pageSize) < totalCount,
                    Data = items
                }
            };
        }
    }
}