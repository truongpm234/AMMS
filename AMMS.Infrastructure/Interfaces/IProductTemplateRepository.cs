using AMMS.Infrastructure.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IProductTemplateRepository
    {
        Task<List<product_template>> GetByProductTypeIdAsync(int productTypeId, CancellationToken ct = default);
        Task<List<product_template>> GetAllAsync(CancellationToken ct = default);
        Task<List<material>> GetPaperMaterialsStockDescAsync(CancellationToken ct = default);

    }
}
