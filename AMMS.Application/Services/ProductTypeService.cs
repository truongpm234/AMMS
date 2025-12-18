using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
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
    }
}
