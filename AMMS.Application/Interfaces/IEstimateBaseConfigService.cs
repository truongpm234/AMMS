using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Shared.DTOs.Estimates;

namespace AMMS.Application.Interfaces
{
    public interface IEstimateBaseConfigService
    {
        Task<EstimateBaseConfigDto> GetAsync(CancellationToken ct = default);
    }
}
