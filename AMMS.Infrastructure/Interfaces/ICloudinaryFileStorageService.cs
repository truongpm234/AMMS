using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces

{
        public interface ICloudinaryFileStorageService
    {
            Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, string folder);
        }
}
