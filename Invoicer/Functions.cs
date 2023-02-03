using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Square;
using Square.Exceptions;
using Square.Models;
using Invoicer.Models;
using Microsoft.Azure.Functions.Worker.Http;
using System.Text;
using System.Net;

namespace Invoicer
{
    public class Functions
    {
        private readonly ISquareClient _square;
        private readonly ILogger _logger;

        public Functions(ISquareClient squareClient, ILoggerFactory loggerFactory)
        {
            _square = squareClient;
            _logger = loggerFactory.CreateLogger<Functions>();
        }

        [Function("getAppointmentsTimer")]
        [ServiceBusOutput("sqOrders", Connection = "SBCONNECTION")]
        public async Task<IEnumerable<Booking>> Timer([TimerTrigger("0 0 4 * * *")]TimerInfo myTimer)
        {
            return await GetAppointments(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        }

        [Function("getAppointments")]
        [ServiceBusOutput("sqOrders", Connection = "SBCONNECTION")]
        public async Task<IEnumerable<Booking>> Run([HttpTrigger(AuthorizationLevel.Function, "get")]HttpRequestData req,
            FunctionContext context,
            DateTime start,
            DateTime end)
        {
            return await GetAppointments(start, end);
        }

        private async Task<IEnumerable<Booking>> GetAppointments(DateTime start, DateTime end)
        {
            var orders = new List<Booking>();
            var startStr = start.ToString("u");
            var endStr = end.ToString("u");

            try
            {
                ListBookingsResponse result = await _square.BookingsApi.ListBookingsAsync(null, null, null, null, startStr, endStr);
                _logger.LogInformation("Found {0} appointments", result.Bookings.Count);
                foreach(var booking in result.Bookings)
                {
                    if (booking.Status == "ACCEPTED" || booking.Status == "NO_SHOW")
                    {
                        orders.Add(booking);
                    }
                }
                return orders;
            }
            catch (ApiException e)
            {
                _logger.LogError("{0} : {1}", e.Errors[0].Code, e.Errors[0].Detail);
                throw;
            };
        }
        
        [Function("generateOrder")]
        [ServiceBusOutput("sqInvoices", Connection = "SBCONNECTION")]
        public async Task<BookingInvoice> CreateOrderAsync(
            [ServiceBusTrigger("sqOrders", "invoicer", Connection = "SBCONNECTION")]Booking booking) 
        {
            var services = await _square.CatalogApi.ListCatalogAsync(types: "ITEM");
            var serviceLines = new List<OrderServiceCharge>();
            foreach(var s in booking.AppointmentSegments)
            {
                //This makes me sad
                var service = services.Objects.FirstOrDefault(x => x.ItemData.Variations.Any(v => v.Id == s.ServiceVariationId));
                var variation = service.ItemData.Variations.FirstOrDefault(v => v.Id == s.ServiceVariationId);
                serviceLines.Add(new OrderServiceCharge.Builder()
                    .Name(service.ItemData.Name)
                    .AmountMoney(variation.ItemVariationData.PriceMoney)
                    .CalculationPhase("TOTAL_PHASE")
                    .Metadata(new Dictionary<string, string>() { { "appointment", booking.Id } })
                    .Build());
            }

            var order = new Order.Builder(booking.LocationId)
                .CustomerId(booking.CustomerId)
                .ServiceCharges(serviceLines)
                .Build();
            var body = new CreateOrderRequest.Builder()
                .Order(order)
                .IdempotencyKey(booking.Id)
                .Build();

            try
            {
                var result = await _square.OrdersApi.CreateOrderAsync(body);
                return new BookingInvoice() { Order = result.Order, Booking = booking };
            }
            catch (ApiException e)
            {
                _logger.LogError("Failed to create order: {0} - {1}", booking.Id, e.Errors[0].Detail);
                throw;
            }
        }
        
        [Function("generateInvoice")]
        [ServiceBusOutput("sqPublish", Connection = "SBCONNECTION")]
        public async Task<Invoice> CreateInvoiceAsync(
            [ServiceBusTrigger("sqInvoices", "invoicer", Connection = "SBCONNECTION")]BookingInvoice item)
        {
            var recipient = new InvoiceRecipient.Builder()
                .CustomerId(item.Booking.CustomerId)
                .Build();
            var paymentRequests = new List<InvoicePaymentRequest>();

            var paymentReminders = new List<InvoicePaymentReminder>();
            paymentReminders.Add(new InvoicePaymentReminder.Builder()
                .RelativeScheduledDays(-1)
                .Message("Your invoice is due tomorrow")
                .Build());

            var serviceDate = DateTime.Parse(item.Booking.StartAt);
            var due = serviceDate.AddDays(7);
            paymentRequests.Add(new InvoicePaymentRequest.Builder()
                .RequestType("BALANCE")
                .DueDate(due.ToString("u"))
                .AutomaticPaymentSource("NONE")
                .Reminders(paymentReminders)
                .Build());

            var paymentMethods = new InvoiceAcceptedPaymentMethods.Builder()
                .Card(true)
                .SquareGiftCard(true)
                .BankAccount(true)
                .Build();

            var deliveryMethod = await GetDeliveryMethod(item.Booking.CustomerId);

            var invoice = new Invoice.Builder()
                .LocationId(item.Booking.LocationId)
                .OrderId(item.Order.Id)
                .PrimaryRecipient(recipient)
                .PaymentRequests(paymentRequests)
                .DeliveryMethod(deliveryMethod)
                .AcceptedPaymentMethods(paymentMethods)
                .SaleOrServiceDate(serviceDate.ToString("u"))
                .Build();

            var body = new CreateInvoiceRequest.Builder(invoice)
                .IdempotencyKey(item.Booking.Id)
                .Build();

            try
            {
                var result = await _square.InvoicesApi.CreateInvoiceAsync(body);
                _logger.LogInformation("Created invoice {0} for booking {1}", result.Invoice.Id, item.Booking.Id);
                if (deliveryMethod == "EMAIL")
                    return result.Invoice;
                else
                    return null;
            }
            catch (ApiException e)
            {
                _logger.LogError("Failed to create invoice: {0} - {1}", item.Booking.Id, e.Errors[0].Detail);
                throw;
            }
        }

        [Function("publishInvoice")]
        public async Task PublishInvoiceAsync(
            [ServiceBusTrigger("sqPublish", "invoicer", Connection = "SBCONNECTION")]Invoice draft)
        {
            var body = new PublishInvoiceRequest.Builder(draft.Version.Value)
                .Build();
            try
            {
                var result = await _square.InvoicesApi.PublishInvoiceAsync(draft.Id, body);
                _logger.LogInformation("Published invoice {0}", result.Invoice.Id); 
            }
            catch (ApiException e)
            {
                _logger.LogError("Failed to publish invoice: {0} - {1}", draft.Id, e.Errors[0].Detail);
                throw;
            }
        }

        private async Task<string> GetDeliveryMethod(string CustomerId)
        {
            var customer = await _square.CustomersApi.RetrieveCustomerAsync(CustomerId);
            if (!string.IsNullOrEmpty(customer.Customer.EmailAddress))
                return "EMAIL";
            else 
                return "SHARE_MANUALLY";
        }

        [Function("webhookReceiver")]
        public async Task<HttpResponseData> webhook([HttpTrigger(AuthorizationLevel.Function, "get")]HttpRequestData req,
            FunctionContext context)
        {
            var body = new StreamReader(req.Body).ReadToEnd();
            _logger.LogInformation($"webhook received: \n {body}");
            return req.CreateResponse(HttpStatusCode.Accepted);
        }

    }
}
