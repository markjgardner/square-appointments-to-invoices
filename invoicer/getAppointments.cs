using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Square;
using Square.Exceptions;
using Square.Models;

namespace invoicer
{
    public class getAppointments
    {
        private readonly ISquareClient _square;

        public getAppointments(ISquareClient squareClient)
        {
            _square = squareClient;
        }

        [FunctionName("getAppointments")]
        public async Task Run([TimerTrigger("0 0 23 * * *")]TimerInfo myTimer, ILogger log)
        {
            try
            {
                var start = DateTime.UtcNow.AddDays(-7).ToString("u");
                var end = DateTime.UtcNow.ToString("u");
                ListBookingsResponse result = await _square.BookingsApi.ListBookingsAsync(null, null, null, null, start, end);
                log.LogInformation("Found {0} appointments", result.Bookings.Count);
            }
            catch (ApiException e)
            {
                log.LogError("Error getting appointments: {0}", e.Errors.ToString());
            };
        }
    }
}
