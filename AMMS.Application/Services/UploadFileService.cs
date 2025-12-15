using AMMS.Application.Interfaces;
using AMMS.Infrastructure.FileStorage;
using AMMS.Infrastructure.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class UploadFileService : IUploadFileService
    {
        private readonly ICloudinaryFileStorageService _fileStorage;

        public UploadFileService(ICloudinaryFileStorageService fileStorage)
        {
            _fileStorage = fileStorage;
        }

        public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, string module)
        {
            return await _fileStorage.UploadAsync(fileStream, fileName, contentType, module);
        }
    }
}

