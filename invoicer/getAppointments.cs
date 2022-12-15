using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.ServiceBus;
using Microsoft.Extensions.Logging;
using Square;
using Square.Exceptions;
using Square.Models;
using invoicer.Models;

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
        public async Task Run([TimerTrigger("0 0 23 * * *")]TimerInfo myTimer, 
            [ServiceBus("sqOrders", Connection = "SBCONNECTION")]IAsyncCollector<Booking> orders,
            ILogger log)
        {
            try
            {
                var start = DateTime.UtcNow.AddDays(-7).ToString("u");
                var end = DateTime.UtcNow.ToString("u");
                ListBookingsResponse result = await _square.BookingsApi.ListBookingsAsync(null, null, null, null, start, end);
                log.LogInformation("Found {0} appointments", result.Bookings.Count);
                foreach(var booking in result.Bookings)
                {
                    if (booking.Status == "ACCEPTED" || booking.Status == "NO_SHOW")
                    {
                        await orders.AddAsync(booking);
                    }
                }
            }
            catch (ApiException e)
            {
                log.LogError("Error getting appointments: {0}", e.Errors.ToString());
            };
        }
        
        [FunctionName("generateOrder")]
        [return: ServiceBus("sqInvoices", Connection = "SBCONNECTION")]
        public async Task<BookingInvoice> CreateOrderAsync(
            [ServiceBusTrigger("sqOrders", Connection = "SBCONNECTION")]Booking booking, 
            string MessageId,
            ILogger log) 
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
                .IdempotencyKey(MessageId)
                .Build();

            try
            {
                var result = await _square.OrdersApi.CreateOrderAsync(body);
                return new BookingInvoice() { Order = result.Order, Booking = booking };
            }
            catch (ApiException e)
            {
                log.LogError("Failed to create order: {0} - {1}", booking.Id, e.Errors[0].Detail);
                throw e;
            }
        }
        
        [FunctionName("generateInvoice")]
        [return: ServiceBus("sqPublish", Connection = "SBCONNECTION")]
        public async Task<Invoice> CreateInvoiceAsync(
            [ServiceBusTrigger("sqInvoices", Connection = "SBCONNECTION")]BookingInvoice item, 
            string MessageId,
            ILogger log)
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

            var invoice = new Invoice.Builder()
                .LocationId(item.Booking.LocationId)
                .OrderId(item.Order.Id)
                .PrimaryRecipient(recipient)
                .PaymentRequests(paymentRequests)
                .DeliveryMethod("EMAIL")
                .AcceptedPaymentMethods(paymentMethods)
                .SaleOrServiceDate(serviceDate.ToString("u"))
                .Build();

            var body = new CreateInvoiceRequest.Builder(invoice)
                .IdempotencyKey(MessageId)
                .Build();

            try
            {
                var result = await _square.InvoicesApi.CreateInvoiceAsync(body);
                log.LogInformation("Created invoice {0} for booking {1}", result.Invoice.Id, item.Booking.Id);
                return result.Invoice; 
            }
            catch (ApiException e)
            {
                log.LogError("Failed to create invoice: {0} - {1}", item.Booking.Id, e.Errors[0].Detail);
                throw e;
            }
        }

        [FunctionName("publishInvoice")]
        public async Task PublishInvoiceAsync(
            [ServiceBusTrigger("sqPublish", Connection = "SBCONNECTION")]Invoice draft, 
            string MessageId,
            ILogger log)
        {
            var body = new PublishInvoiceRequest.Builder(1)
                .IdempotencyKey(MessageId)
                .Build();
            try
            {
                var result = await _square.InvoicesApi.PublishInvoiceAsync(draft.Id, body);
                log.LogInformation("Published invoice {0}", result.Invoice.Id); 
            }
            catch (ApiException e)
            {
                log.LogError("Failed to publish invoice: {0} - {1}", draft.Id, e.Errors[0].Detail);
                throw e;
            }
        }
    }
}
