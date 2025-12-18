using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Machines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IMachineService
    {
        Task<List<FreeMachineDto>> GetFreeMachinesAsync();
        Task<MachineCapacityResponse> GetCapacityAsync();
    }
}
