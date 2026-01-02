using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UploadsController : ControllerBase
    {
        private readonly IUploadFileService _uploadService;
        private readonly IRequestService _requestService;
        public UploadsController(IUploadFileService uploadService, IRequestService requestService)
        {
            _uploadService = uploadService;
            _requestService = requestService;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File is empty");

            using var stream = file.OpenReadStream();

            var url = await _uploadService.UploadAsync(
                stream,
                file.FileName,
                file.ContentType,
                "uploads");

            return Ok(new { url });
        }

        [HttpPost("update-design-file/{orderRequestId:int}")]
        public async Task<IActionResult> UploadDesignFile(int orderRequestId, IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File is required");

            await using var stream = file.OpenReadStream();

            // folder trên Cloudinary, bạn có thể đổi theo convention của bạn
            var folder = "order-requests/designs";

            var url = await _uploadService.UploadAsync(
                stream,
                file.FileName,
                file.ContentType,
                folder
            );

            await _requestService.UpdateDesignFilePathAsync(orderRequestId, url, ct);

            return Ok(new
            {
                order_request_id = orderRequestId,
                design_file_path = url
            });
        }
    }
}
