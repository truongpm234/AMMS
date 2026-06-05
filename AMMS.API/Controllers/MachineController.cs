using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Machines;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MachineController : ControllerBase
    {
        private readonly IMachineService _service;

        public MachineController(IMachineService service)
        {
            _service = service;
        }

        [HttpGet("free-machines")]
        public async Task<IActionResult> GetFreeMachines()
        {
            var result = await _service.GetFreeMachinesAsync();
            return Ok(result);
        }

        [HttpGet("capacity")]
        public async Task<IActionResult> GetCapacity()
        {
            var result = await _service.GetCapacityAsync();
            return Ok(result);
        }

        [HttpGet("get-all-machines")]
        public async Task<IActionResult> GetAllMachines()
        {
            var result = await _service.GetAllAsync();
            return Ok(result);
        }

        [HttpGet("availability-snapshot")]
        public async Task<IActionResult> GetAvailabilitySnapshot(
    [FromQuery] DateTime? at,
    CancellationToken ct = default)
        {
            var anchor = at ?? AppTime.NowVnUnspecified();
            var result = await _service.GetAvailabilitySnapshotAsync(anchor, ct);
            return Ok(result);
        }

    }
}
