using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.ProductTemplates;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IProductTemplateService
    {
        Task<List<ProductTemplateDto>> GetByProductTypeIdAsync(int productTypeId, CancellationToken ct = default);
        Task<List<product_template>> GetAllAsync(CancellationToken ct = default);
        Task<ProductTemplatesWithPaperStockResponse> GetByProductTypeIdWithPaperStockAsync(CancellationToken ct = default);

    }
}
