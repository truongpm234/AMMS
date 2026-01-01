using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.ProductTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IProductTypeRepository
    {
        Task<List<product_type>> GetAllAsync();
        Task<product_type?> GetByCodeAsync(string code);
        Task<ProductTypeDetailDto?> GetProductTypeDetailAsync(int productTypeId, CancellationToken ct = default);
    }
}
