using Square.Models;

namespace Invoicer.Models
{
    public class BookingInvoice
    {
        public Booking Booking { get; set; }
        public Order Order { get; set; }
    }
}