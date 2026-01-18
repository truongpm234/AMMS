using AMMS.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class ConsoleSmsSender : ISmsSender
    {
        public Task SendAsync(string phone, string message, CancellationToken ct = default)
        {
            Console.WriteLine($"[SMS to {phone}] {message}");
            return Task.CompletedTask;
        }
    }
}
