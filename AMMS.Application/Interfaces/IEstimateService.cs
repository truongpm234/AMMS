using AMMS.Shared.DTOs.Estimates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IEstimateService
    {
        Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req);
    }
}
