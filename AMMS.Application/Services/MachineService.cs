using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Machines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class MachineService: IMachineService
    {
        private readonly IMachineRepository _repo;

        public MachineService(IMachineRepository repo)
        {
            _repo = repo;
        }

        public Task<List<FreeMachineDto>> GetFreeMachinesAsync()
            => _repo.GetFreeMachinesAsync();

        public async Task<MachineCapacityResponse> GetCapacityAsync()
        {
            var total = await _repo.CountAllAsync();
            var active = await _repo.CountActiveAsync();
            var running = await _repo.CountRunningAsync();
            return new MachineCapacityResponse
            {
                TotalMachines = total,
                ActiveMachines = active,
                RunningMachines = running
            };
        }

        public async Task<List<machine>> GetAllAsync()
        {
            return await _repo.GetAllAsync();
        }
    }
}
