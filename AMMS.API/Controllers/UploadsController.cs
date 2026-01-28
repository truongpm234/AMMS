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
        public async Task<IActionResult> Upload([FromForm] List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No file uploaded");

            var urls = new List<string>();

            foreach (var file in files)
            {
                if (file == null || file.Length == 0)
                    continue;

                using var stream = file.OpenReadStream();

                var url = await _uploadService.UploadAsync(
                    stream,
                    file.FileName,
                    file.ContentType,
                    "uploads");

                urls.Add(url);
            }

            if (urls.Count == 0)
                return BadRequest("All files are empty");

            return Ok(new
            {
                url = string.Join(",", urls)
            });
        }

        [HttpPost("update-design-file/{orderRequestId:int}")]
        public async Task<IActionResult> UploadDesignFile(int orderRequestId, [FromForm] List<IFormFile> files, CancellationToken ct)
        {
            if (files == null || files.Count == 0)
                return BadRequest("At least one file is required");

            var folder = "order-requests/designs";
            var urls = new List<string>();

            foreach (var file in files)
            {
                if (file == null || file.Length == 0)
                    continue;

                await using var stream = file.OpenReadStream();

                var url = await _uploadService.UploadAsync(
                    stream,
                    file.FileName,
                    file.ContentType,
                    folder
                );

                urls.Add(url);
            }

            if (!urls.Any())
                return BadRequest("All files are empty");

            var joinedUrls = string.Join(",", urls);

            await _requestService.UpdateDesignFilePathAsync(orderRequestId, joinedUrls, ct);

            return Ok(new
            {
                order_request_id = orderRequestId,
                design_file_path = joinedUrls,   
            });
        }

        [HttpDelete("delete-design-file/{request_id:int}")]
        public async Task<IActionResult> DeleteDesignFile(int request_id,  CancellationToken ct)
        {
            var deletedCount = await _requestService.DeleteDesignFilePathByRequestIdAsync(request_id, ct);
            return Ok(new
            {
                order_request_id = request_id,
                deleted_count = deletedCount
            });
        }
    }
}
