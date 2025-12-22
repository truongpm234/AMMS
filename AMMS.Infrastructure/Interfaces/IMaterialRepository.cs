using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IMaterialRepository
    {
        Task<material?> GetByCodeAsync(string code);
        Task<List<material>> GetAll();
        Task<material> GetByIdAsync(int id);
        Task UpdateAsync(material entity);
        Task SaveChangeAsync();
        Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
            int page, int pageSize, CancellationToken ct = default);
    }
}
