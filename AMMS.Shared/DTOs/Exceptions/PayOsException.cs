using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Exceptions
{
    namespace AMMS.Application.Exceptions
    {
        public sealed class PayOsException : Exception
        {
            public PayOsException(string message) : base(message) { }

            public PayOsException(string message, Exception? innerException)
                : base(message, innerException) { }
        }
    }
}
