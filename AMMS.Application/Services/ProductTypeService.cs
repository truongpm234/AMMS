using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.ProductTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class ProductTypeService : IProductTypeService
    {
        private readonly IProductTypeRepository _productTypeRepository;
        public ProductTypeService(IProductTypeRepository productTypeRepository)
        {
            _productTypeRepository = productTypeRepository;
        }
        public async Task<List<product_type>> GetAllAsync()
        {
            return await _productTypeRepository.GetAllAsync();
        }
        
        public Task<List<string>> GetAllTypeFormGachAsync()
        {
            var result = Enum.GetNames(typeof(ProductTypeCodeOfGach)).ToList();
            return Task.FromResult(result);
        }
        public Task<List<string>> GetAllTypeHop_MauAsync()
        {
            var result = Enum.GetNames(typeof(ProductTypeCodeOfHop_mau)).ToList();
            return Task.FromResult(result);
        }
        public Task<List<string>> GetAllTypeGeneralAsync()
        {
            var result = Enum.GetNames(typeof(ProductTypeCodeGeneral)).ToList();
            return Task.FromResult(result);
        }
        public Task<ProductTypeDetailDto?> GetDetailAsync(int productTypeId, CancellationToken ct = default)
            => _productTypeRepository.GetProductTypeDetailAsync(productTypeId, ct);
    }
}
